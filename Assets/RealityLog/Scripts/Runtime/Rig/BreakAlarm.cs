#nullable enable

using System;
using System.Diagnostics;
using UnityEngine;
using RealityLog.Common;
using Debug = UnityEngine.Debug;

namespace RealityLog.Rig
{
    /// <summary>
    /// Sounds from any running headset once the last take (good or failed) ended more than
    /// <see cref="BreakAlarmPolicy.GraceSeconds"/> ago, signed in or not, worn or not (the rule is
    /// <see cref="BreakAlarmPolicy"/>'s). It speaks (owner, 2026-11-25: a beep is annoying and says
    /// nothing): "Pod N, start now", the headset's own pod number (<see cref="BreakAlarmVoice"/>),
    /// over and over, climbing as the idle time grows: quietly every <see cref="SoftBeepEverySeconds"/> s,
    /// then at full level with a <see cref="LoudPauseSeconds"/> s pause, then back to back. The times
    /// are the same as when it beeped. From the second level on, the headset's media volume is held at
    /// <see cref="HeldVolumeFraction"/> of its maximum (owner, 2026-11-24: 70 % of build 97's full
    /// volume) and unmuted every quarter second, so the volume buttons cannot move it; the volume
    /// the collector had is put back the moment it stops. Only a take starting stops it. If the
    /// recorded phrases cannot be read, it falls back to what it did before (soft beep, loud beep,
    /// siren) rather than stay silent. Sound only: no controller vibration, so the gripper
    /// switch's hold pulse (RigCoordinator) still reads as itself. Every change logs one line,
    /// <c>[RealityLog] break alarm: ...</c>. Auto-bootstraps; no scene wiring.
    /// </summary>
    public sealed class BreakAlarm : MonoBehaviour
    {
        private const float LookIntervalSeconds = 0.25f;
        private const int StreamMusic = 3;      // android.media.AudioManager.STREAM_MUSIC (Unity's output)
        private const int AdjustUnmute = 100;   // android.media.AudioManager.ADJUST_UNMUTE
        private const int Rate = 44100;
        private const float SoftBeepEverySeconds = 5f;
        private const float LoudBeepEverySeconds = 2f;
        private const float SoftBeepVolume = 0.35f;
        /// <summary>The silence between two phrases at the second level.</summary>
        private const float LoudPauseSeconds = 1f;
        /// <summary>The silence between two phrases at the top level: back to back.</summary>
        private const float SirenPauseSeconds = 0.25f;
        /// <summary>The share of the media stream's maximum the alarm holds from the loud beep on.</summary>
        private const float HeldVolumeFraction = 0.7f;

        private readonly BreakAlarmPolicy policy = new();
        private BreakAlarmVerdict last = BreakAlarmVerdict.Off;
        private float lastLook = float.NegativeInfinity;
        private AudioSource? siren;
        private AudioClip? beep;
        private float nextBeep = float.NegativeInfinity;
        private AudioClip? phrase;
        private string? phraseFor;
        private string phraseWords = "";
        private float phraseEnds = float.NegativeInfinity;
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

            var verdict = policy.Observe(now, collecting);
            if (verdict >= BreakAlarmVerdict.Soft)
            {
                if (verdict != last) Escalate(verdict, signedIn ?? collector ?? "nobody", policy.IdleSeconds(now) ?? 0);
                Play(verdict);
            }
            else if (last >= BreakAlarmVerdict.Soft)
            {
                StopSiren("a take started recording");
            }
            if (signedIn != null) collector = signedIn;
            last = verdict;
        }

        /// <summary>The alarm starts or climbs a level: one log line, and the collector's volume kept to put back.</summary>
        private void Escalate(BreakAlarmVerdict level, string who, double idle)
        {
            // The volume is taken over only from Loud on, so only then is the collector's kept.
            if (level >= BreakAlarmVerdict.Loud && volumeBefore == null) volumeBefore = ReadVolume();
            Sounding = true;
            var speaks = Phrase() != null;
            // A phrase already in the air finishes; the new level's pace starts after it.
            nextBeep = phraseEnds;
            if (!speaks && level != BreakAlarmVerdict.Siren && siren != null) siren.Stop();
            Debug.LogWarning($"[{Constants.LOG_TAG}] break alarm: {level.ToString().ToLowerInvariant()}; no take for " +
                $"{idle:F0} s since the last one ended (collector {who}); " +
                (speaks ? $"saying \"{phraseWords}\"" : "beeping, no recorded phrase"));
        }

        /// <summary>
        /// Every look while alarming: the phrase at the level's pace (the level's old sound if there is
        /// no phrase), and from Loud on the volume held at 70 % of maximum.
        /// </summary>
        private void Play(BreakAlarmVerdict level)
        {
            if (level >= BreakAlarmVerdict.Loud) HoldVolume();
            var said = Phrase();
            if (said != null)
            {
                if (Time.unscaledTime < nextBeep) return;
                phraseEnds = Time.unscaledTime + said.length;
                nextBeep = level == BreakAlarmVerdict.Siren ? phraseEnds + SirenPauseSeconds
                    : level == BreakAlarmVerdict.Loud ? phraseEnds + LoudPauseSeconds
                    : Mathf.Max(Time.unscaledTime + SoftBeepEverySeconds, phraseEnds + LoudPauseSeconds);
                Source().PlayOneShot(said, level == BreakAlarmVerdict.Soft ? SoftBeepVolume : 1f);
                return;
            }
            if (level == BreakAlarmVerdict.Siren)
            {
                StartSiren();
                return;
            }
            if (Time.unscaledTime < nextBeep) return;
            nextBeep = Time.unscaledTime + (level == BreakAlarmVerdict.Loud ? LoudBeepEverySeconds : SoftBeepEverySeconds);
            var source = Source();
            beep ??= BuildBeep();
            source.PlayOneShot(beep, level == BreakAlarmVerdict.Loud ? 1f : SoftBeepVolume);
        }

        private AudioSource Source()
        {
            if (siren != null) return siren;
            siren = gameObject.AddComponent<AudioSource>();
            siren.loop = true;
            siren.spatialBlend = 0f;
            siren.playOnAwake = false;
            siren.priority = 0;
            siren.volume = 1f;
            siren.ignoreListenerPause = true;
            siren.ignoreListenerVolume = true;
            siren.bypassEffects = true;
            siren.bypassListenerEffects = true;
            return siren;
        }

        private void StartSiren()
        {
            var source = Source();
            if (source.clip == null) source.clip = BuildSiren();
            if (!source.isPlaying) source.Play();
        }

        /// <summary>
        /// The recorded phrase for this headset's pod name, read again when the name changes (a headset
        /// has none until it is enrolled). Null when there is no recorded phrase to say.
        /// </summary>
        private AudioClip? Phrase()
        {
            var podName = RigCoordinator.Instance?.PodName ?? "";
            if (phraseFor == podName) return phrase;
            phraseFor = podName;
            if (phrase != null) Destroy(phrase);
            phrase = null;
            try
            {
                var file = Resources.Load<TextAsset>(BreakAlarmVoice.ResourceName);
                var read = BreakAlarmVoice.Read(file != null ? file.bytes : null, podName);
                if (file != null) Resources.UnloadAsset(file);
                if (read is BreakAlarmVoice.Phrase found)
                {
                    phrase = AudioClip.Create("BreakAlarmPhrase", found.Samples.Length, 1, found.Rate, false);
                    phrase.SetData(found.Samples, 0);
                    phraseWords = found.Words;
                    Debug.Log($"[{Constants.LOG_TAG}] break alarm: will say \"{phraseWords}\" (pod name \"{podName}\")");
                }
                else
                {
                    Debug.LogWarning($"[{Constants.LOG_TAG}] break alarm: no recorded phrase in {BreakAlarmVoice.ResourceName}; it will beep");
                }
            }
            catch (Exception ex)
            {
                phrase = null;
                Debug.LogWarning($"[{Constants.LOG_TAG}] break alarm: cannot read the recorded phrase, it will beep: {ex.Message}");
            }
            return phrase;
        }

        private void StopSiren(string why)
        {
            Sounding = false;
            if (siren != null) siren.Stop();
            phraseEnds = float.NegativeInfinity;
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

        /// <summary>Media volume to <see cref="HeldVolumeFraction"/> of its maximum and unmuted, every look while the alarm sounds.</summary>
        private void HoldVolume()
        {
            AudioListener.pause = false;
            AudioListener.volume = 1f;
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                var audio = Audio();
                if (audio.Call<bool>("isStreamMute", StreamMusic)) audio.Call("adjustStreamVolume", StreamMusic, AdjustUnmute, 0);
                var max = audio.Call<int>("getStreamMaxVolume", StreamMusic);
                var held = Mathf.Max(1, Mathf.RoundToInt(max * HeldVolumeFraction));
                if (audio.Call<int>("getStreamVolume", StreamMusic) != held) audio.Call("setStreamVolume", StreamMusic, held, 0);
            }
            catch (Exception ex)
            {
                if (!volumeWarned) Debug.LogWarning($"[{Constants.LOG_TAG}] break alarm: cannot hold the volume at 70 %: {ex.Message}");
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
        /// One beep: two short 1200 Hz pips (0.12 s each, 0.08 s apart), softer-edged than the siren and
        /// unlike any rig cue.
        /// </summary>
        private static AudioClip BuildBeep()
        {
            var pip = (int)(0.12f * Rate);
            var gap = (int)(0.08f * Rate);
            var samples = new float[pip * 2 + gap];
            for (var k = 0; k < 2; k++)
            {
                var at = k * (pip + gap);
                for (var i = 0; i < pip; i++)
                {
                    var envelope = Mathf.Min(1f, Mathf.Min(i, pip - i) / (Rate * 0.005f));
                    samples[at + i] = 0.9f * envelope * Mathf.Sin(2f * Mathf.PI * 1200f * i / Rate);
                }
            }
            var clip = AudioClip.Create("BreakAlarmBeep", samples.Length, 1, Rate, false);
            clip.SetData(samples, 0);
            return clip;
        }

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
