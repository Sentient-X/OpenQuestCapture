#nullable enable

using System;
using System.Diagnostics;
using UnityEngine;
using RealityLog.Common;
using Debug = UnityEngine.Debug;

namespace RealityLog.Rig
{
    /// <summary>
    /// Sounds a loud, looping siren from a worn headset while a collector is signed into the pod and
    /// the last take (good or failed) ended more than <see cref="BreakAlarmPolicy.GraceSeconds"/> ago (the rule is
    /// <see cref="BreakAlarmPolicy"/>'s). While it sounds, the headset's media volume is held at its
    /// maximum and unmuted every quarter second, so the volume buttons cannot quiet it; the volume
    /// the collector had is put back the moment it stops. It stops when a take starts recording or
    /// the collector signs out of the pod. Sound only: no controller vibration, so the gripper
    /// switch's hold pulse (RigCoordinator) still reads as itself. Every change logs one line,
    /// <c>[RealityLog] break alarm: ...</c>. Auto-bootstraps; no scene wiring.
    /// </summary>
    public sealed class BreakAlarm : MonoBehaviour
    {
        private const float LookIntervalSeconds = 0.25f;
        private const int StreamMusic = 3;      // android.media.AudioManager.STREAM_MUSIC (Unity's output)
        private const int AdjustUnmute = 100;   // android.media.AudioManager.ADJUST_UNMUTE
        private const int Rate = 44100;

        private readonly BreakAlarmPolicy policy = new();
        private BreakAlarmVerdict last = BreakAlarmVerdict.Off;
        private float lastLook = float.NegativeInfinity;
        private AudioSource? siren;
        private RecordingManager? recorder;
        private string? collector;
        private int? volumeBefore;
        private bool volumeWarned;
#if UNITY_ANDROID && !UNITY_EDITOR
        private AndroidJavaObject? audioManager;
#endif

        /// <summary>The alarm is sounding now (main thread).</summary>
        public static bool Sounding { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            var go = new GameObject(nameof(BreakAlarm));
            go.AddComponent<BreakAlarm>();
            DontDestroyOnLoad(go);
        }

        private void Update()
        {
            if (Time.unscaledTime - lastLook < LookIntervalSeconds) return;
            lastLook = Time.unscaledTime;
            var now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

            var rig = RigCoordinator.Instance;
            var signedIn = rig != null && rig.Enrolled
                ? RigStatusJson.CollectorName(RigCoordinator.ParsedStatusOrDefault)
                : null;
            if (recorder == null) recorder = FindFirstObjectByType<RecordingManager>();
            var collecting = recorder != null && recorder.State != RecordingState.Idle;

            var worn = rig?.HeadsetWorn;
            var deviceRefusal = PodScreenBoard.EncoderRefusal(RigCoordinator.ParsedStatusOrDefault) != null;
            var verdict = policy.Observe(now, signedIn != null, worn, deviceRefusal, collecting);
            if (verdict == BreakAlarmVerdict.Sounding)
            {
                if (last != BreakAlarmVerdict.Sounding) StartSiren(signedIn!);
                HoldVolume();
            }
            else if (last == BreakAlarmVerdict.Sounding)
            {
                StopSiren(collecting ? "a take started recording"
                    : signedIn == null ? "the collector signed out of the pod"
                    : worn != true ? "the headset came off"
                    : "the rig refuses to start for a device fault");
            }
            if (signedIn != null) collector = signedIn;
            last = verdict;
        }

        private void StartSiren(string who)
        {
            Sounding = true;
            Debug.LogWarning($"[{Constants.LOG_TAG}] break alarm: sounding; no take for " +
                $"{BreakAlarmPolicy.GraceSeconds:F0} s since the last one ended while {who} is signed into the pod");
            volumeBefore = ReadVolume();
            if (siren == null)
            {
                siren = gameObject.AddComponent<AudioSource>();
                siren.clip = BuildSiren();
                siren.loop = true;
                siren.spatialBlend = 0f;
                siren.playOnAwake = false;
                siren.priority = 0;
                siren.volume = 1f;
                siren.ignoreListenerPause = true;
                siren.ignoreListenerVolume = true;
                siren.bypassEffects = true;
                siren.bypassListenerEffects = true;
            }
            siren.Play();
        }

        private void StopSiren(string why)
        {
            Sounding = false;
            if (siren != null) siren.Stop();
            RestoreVolume();
            Debug.LogWarning($"[{Constants.LOG_TAG}] break alarm: off; {why} (collector {collector ?? "unknown"})");
        }

        private void OnDestroy()
        {
            if (Sounding) RestoreVolume();
            Sounding = false;
#if UNITY_ANDROID && !UNITY_EDITOR
            audioManager?.Dispose();
            audioManager = null;
#endif
        }

        /// <summary>Media volume to its maximum and unmuted, every look while the alarm sounds.</summary>
        private void HoldVolume()
        {
            AudioListener.pause = false;
            AudioListener.volume = 1f;
            if (siren != null && !siren.isPlaying) siren.Play();
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                var audio = Audio();
                if (audio.Call<bool>("isStreamMute", StreamMusic)) audio.Call("adjustStreamVolume", StreamMusic, AdjustUnmute, 0);
                var max = audio.Call<int>("getStreamMaxVolume", StreamMusic);
                if (audio.Call<int>("getStreamVolume", StreamMusic) < max) audio.Call("setStreamVolume", StreamMusic, max, 0);
            }
            catch (Exception ex)
            {
                if (!volumeWarned) Debug.LogWarning($"[{Constants.LOG_TAG}] break alarm: cannot hold the volume at maximum: {ex.Message}");
                volumeWarned = true;
            }
#endif
        }

        private int? ReadVolume()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try { return Audio().Call<int>("getStreamVolume", StreamMusic); }
            catch (Exception ex) { Debug.LogWarning($"[{Constants.LOG_TAG}] break alarm: cannot read the volume: {ex.Message}"); }
#endif
            return null;
        }

        private void RestoreVolume()
        {
            if (volumeBefore is not int before) return;
            volumeBefore = null;
#if UNITY_ANDROID && !UNITY_EDITOR
            try { Audio().Call("setStreamVolume", StreamMusic, before, 0); }
            catch (Exception ex) { Debug.LogWarning($"[{Constants.LOG_TAG}] break alarm: cannot put the volume back: {ex.Message}"); }
#endif
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        private AndroidJavaObject Audio()
        {
            if (audioManager != null) return audioManager;
            using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
            using var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
            audioManager = activity.Call<AndroidJavaObject>("getSystemService", "audio");
            return audioManager;
        }
#endif

        /// <summary>
        /// One second of siren, looped: a harsh square-wave two-tone (960 Hz / 640 Hz, a quarter
        /// second each), near full scale, so it cannot be mistaken for any rig cue.
        /// </summary>
        private static AudioClip BuildSiren()
        {
            (float hz, float s)[] notes = { (960f, 0.25f), (640f, 0.25f), (960f, 0.25f), (640f, 0.25f) };
            var total = 0;
            foreach (var (_, s) in notes) total += (int)(s * Rate);
            var samples = new float[total];
            var at = 0;
            foreach (var (hz, s) in notes)
            {
                var n = (int)(s * Rate);
                for (var i = 0; i < n; i++)
                {
                    var envelope = Mathf.Min(1f, Mathf.Min(i, n - i) / (Rate * 0.003f));
                    var wave = Mathf.Sin(2f * Mathf.PI * hz * i / Rate) >= 0f ? 1f : -1f;
                    samples[at + i] = 0.95f * envelope * wave;
                }
                at += n;
            }
            var clip = AudioClip.Create("BreakAlarmSiren", samples.Length, 1, Rate, false);
            clip.SetData(samples, 0);
            return clip;
        }
    }
}
