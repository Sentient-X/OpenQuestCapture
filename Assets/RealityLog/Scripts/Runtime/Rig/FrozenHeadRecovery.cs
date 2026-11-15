#nullable enable

using System;
using System.Diagnostics;
using UnityEngine;
using RealityLog.Common;
using Debug = UnityEngine.Debug;

namespace RealityLog.Rig
{
    /// <summary>
    /// Relaunches the app when the OVR runtime stopped delivering head poses and nothing is in
    /// progress (<see cref="SelfRelaunchPolicy"/>, sx#4210): on Quest 3S that stream has frozen
    /// until the app restarts, and every start is refused "head tracking stopped" meanwhile.
    /// Looks once a second. Each relaunch is logged, counted in <c>self_relaunch.json</c> and
    /// shown in <c>/api/status</c> <c>selfRelaunch</c>. Auto-bootstraps; no scene wiring.
    /// </summary>
    public sealed class FrozenHeadRecovery : MonoBehaviour
    {
        private const float LookIntervalSeconds = 1f;

        private static SelfRelaunchLedger ledger = new();
        private readonly SelfRelaunchPolicy policy = new();
        private float lastLook = float.NegativeInfinity;
        private RelaunchVerdict lastVerdict = RelaunchVerdict.Healthy;
        private string? lastBusy;
        private string dataPath = "";
        private bool relaunching;

        /// <summary>Self relaunches since the app was installed (any thread).</summary>
        public static int Relaunches => ledger.Count;

        /// <summary>When the app last relaunched itself, UTC; null if it never has (any thread).</summary>
        public static DateTime? LastRelaunchUtc => ledger.LastUtc;

        /// <summary>
        /// <c>/api/status</c> <c>selfRelaunch</c>: <c>{"count", "lastUtc", "lastReason"}</c>, the
        /// relaunches since install. Any thread.
        /// </summary>
        public static string StatusJson()
        {
            var read = ledger;
            return new Newtonsoft.Json.Linq.JObject
            {
                ["count"] = read.Count,
                ["lastUtc"] = read.LastUtc?.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                ["lastReason"] = read.LastReason,
            }.ToString(Newtonsoft.Json.Formatting.None);
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            var go = new GameObject(nameof(FrozenHeadRecovery));
            go.AddComponent<FrozenHeadRecovery>();
            DontDestroyOnLoad(go);
        }

        private void Awake()
        {
            dataPath = Application.persistentDataPath;
            ledger = SelfRelaunchLedger.Read(dataPath);
            if (ledger.LastUtc is DateTime last && DateTime.UtcNow - last < TimeSpan.FromMinutes(5))
            {
                Debug.LogWarning($"[{Constants.LOG_TAG}] FrozenHeadRecovery: launched by a self relaunch at {last:O} " +
                    $"({ledger.LastReason}); self relaunches so far: {ledger.Count}");
            }
        }

        private void Update()
        {
            if (relaunching || Time.unscaledTime - lastLook < LookIntervalSeconds) return;
            lastLook = Time.unscaledTime;
            var now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
            var busy = Busy();
            // Update runs only while the OS lets the app run frames; a paused app is never frozen.
            var verdict = policy.Observe(now, DateTime.UtcNow, HeadTrackingMonitor.HeadPoseAgeMs,
                !HeadTrackingMonitor.AppPaused, busy, ledger.LastUtc);
            if (verdict != lastVerdict || (verdict == RelaunchVerdict.Held && busy != lastBusy))
            {
                Report(verdict, busy, now);
            }
            lastVerdict = verdict;
            lastBusy = busy;
            if (verdict == RelaunchVerdict.Relaunch)
            {
                Relaunch($"head poses frozen for {policy.FrozenForSeconds(now):F0} s with nothing recording");
            }
        }

        private static string? Busy()
        {
            if (HeadTrackingMonitor.Recording) return "a recording is running";
            if (FindFirstObjectByType<RecordingManager>() is { State: not RecordingState.Idle } manager)
                return "the recording manager is " + manager.State;
            return RigCoordinator.Instance?.BusyForRelaunch();
        }

        private void Report(RelaunchVerdict verdict, string? busy, double now)
        {
            var frozen = policy.FrozenForSeconds(now) is double s ? $"{s:F0} s" : "no";
            switch (verdict)
            {
                case RelaunchVerdict.Healthy when lastVerdict != RelaunchVerdict.Healthy:
                    Debug.LogWarning($"[{Constants.LOG_TAG}] FrozenHeadRecovery: head poses advance again; no relaunch");
                    break;
                case RelaunchVerdict.Waiting:
                    Debug.LogWarning($"[{Constants.LOG_TAG}] FrozenHeadRecovery: head poses frozen ({frozen}); relaunching after " +
                        $"{SelfRelaunchPolicy.HoldSeconds:F0} s if nothing records");
                    break;
                case RelaunchVerdict.Held:
                    Debug.LogWarning($"[{Constants.LOG_TAG}] FrozenHeadRecovery: head poses frozen ({frozen}); not relaunching while {busy}");
                    break;
                case RelaunchVerdict.RateLimited:
                    Debug.LogWarning($"[{Constants.LOG_TAG}] FrozenHeadRecovery: head poses frozen ({frozen}); the app relaunched itself at " +
                        $"{ledger.LastUtc:O}, at most once per {SelfRelaunchPolicy.MinInterval.TotalMinutes:F0} min: restart it or reboot the headset");
                    break;
            }
        }

        private void Relaunch(string reason)
        {
            relaunching = true;
            try
            {
                ledger.Record(dataPath, DateTime.UtcNow, reason);
            }
            catch (Exception ex)
            {
                // Without the ledger the rate limit cannot hold across launches: do not relaunch.
                Debug.LogError($"[{Constants.LOG_TAG}] FrozenHeadRecovery: cannot record the relaunch ({ex.Message}); not relaunching");
                return;
            }
            Debug.LogWarning($"[{Constants.LOG_TAG}] FrozenHeadRecovery: relaunching the app: {reason} (self relaunch {ledger.Count})");
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                // Close the rig the way a quit does (USB released, receipts written) before the
                // process goes; then start the launcher activity in a fresh task and end this
                // process, so the OVR runtime is reconnected from a new one.
                RigCoordinator.Instance?.ShutDown();
                using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                using var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
                using var packages = activity.Call<AndroidJavaObject>("getPackageManager");
                using var launch = packages.Call<AndroidJavaObject>("getLaunchIntentForPackage", activity.Call<string>("getPackageName"));
                using var component = launch.Call<AndroidJavaObject>("getComponent");
                using var intents = new AndroidJavaClass("android.content.Intent");
                using var restart = intents.CallStatic<AndroidJavaObject>("makeRestartActivityTask", component);
                activity.Call("startActivity", restart);
                using var process = new AndroidJavaClass("android.os.Process");
                process.CallStatic("killProcess", process.CallStatic<int>("myPid"));
            }
            catch (Exception ex)
            {
                Debug.LogError($"[{Constants.LOG_TAG}] FrozenHeadRecovery: relaunch failed: {ex}");
            }
#endif
        }
    }
}
