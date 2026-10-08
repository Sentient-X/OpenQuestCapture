# nullable enable

using System;
using System.Diagnostics;
using System.Threading;
using UnityEngine;
using RealityLog.Common;
using Debug = UnityEngine.Debug;

namespace RealityLog
{
    /// <summary>
    /// Watches whether the OVR runtime is still delivering head poses, whether or not a
    /// recording is running.
    ///
    /// The pose, IMU and body loggers drop any sample whose OVR time has not advanced. A
    /// frozen tracking stream therefore produces header-only CSVs while Camera2 keeps
    /// recording, and nothing in the session says so. On Quest 3S that stream has been seen
    /// to freeze until the app restarts. This monitor gives <see cref="RecordingManager"/>
    /// a reason to refuse a start, and gives <c>/api/status</c> a <c>tracking</c> block,
    /// so a broken stream is refused up front instead of found after transfer.
    ///
    /// Auto-bootstraps at runtime; no scene wiring. Runs before other scripts each frame so
    /// a start queued by the HTTP server or cloud relay never sees a pose age left over from
    /// a long main-thread stall.
    /// </summary>
    [DefaultExecutionOrder(-10000)]
    public class HeadTrackingMonitor : MonoBehaviour
    {
        /// <summary>A head pose older than this refuses a recording start.</summary>
        public const double StartMaxPoseAgeMs = 1000.0;

        /// <summary>A gap in head poses longer than this during a recording is a stall.</summary>
        public const double StallThresholdMs = 1000.0;

        private const long NoPose = long.MinValue;

        // Written on the main thread, read from the HTTP server's thread.
        private static long lastAdvanceTicks = NoPose; // Stopwatch ticks of the last new head pose
        private static double lastOvrTime;
        private static volatile bool positionTracked;
        private static volatile bool orientationTracked;
        private static volatile bool hmdPresent;
        private static volatile bool vrFocus;
        private static volatile bool inputFocus;

        private static readonly object eventLock = new();
        private static string? lastEvent;
        private static long lastEventTicks = NoPose;

        // Stalls observed during the current (or last) recording.
        private static volatile bool recording;
        private static int recordingStalls;
        private static long longestStallTicks;
        private static bool inStall;

        // Stopwatch ticks of the OS pause (OnApplicationPause(true)); NoPose while running.
        // While paused the main thread runs nothing, so HTTP handlers must not queue work on it.
        private static long pausedSinceTicks = NoPose;
        // Frame of the last OnApplicationPause(true). Unity issues one more frame after the
        // callback, so Update only treats the pause as stale after that frame. Main thread only.
        private static int pausedFrame = -1;

        // Controller state sampled in Update. Written on the main thread, read from the
        // HTTP server's thread under controllerLock.
        private static readonly object controllerLock = new();
        private static ControllerSample leftController;
        private static ControllerSample rightController;
        private static long controllerSampleTicks = NoPose;

        private struct ControllerSample
        {
            public bool Connected;
            public bool PositionTracked;
            public float? DistanceToHeadM;
        }

        // Statics survive Enter Play Mode without a domain reload in the Editor.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            Interlocked.Exchange(ref lastAdvanceTicks, NoPose);
            lastOvrTime = 0;
            Interlocked.Exchange(ref pausedSinceTicks, NoPose);
            pausedFrame = -1;
            recording = false;
            inStall = false;
            Interlocked.Exchange(ref recordingStalls, 0);
            Interlocked.Exchange(ref longestStallTicks, 0);
            lock (eventLock)
            {
                lastEvent = null;
                lastEventTicks = NoPose;
            }
            lock (controllerLock)
            {
                leftController = default;
                rightController = default;
                controllerSampleTicks = NoPose;
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            var go = new GameObject(nameof(HeadTrackingMonitor));
            go.AddComponent<HeadTrackingMonitor>();
            DontDestroyOnLoad(go);
        }

        private void OnEnable()
        {
            OVRManager.HMDLost += OnHmdLost;
            OVRManager.HMDAcquired += OnHmdAcquired;
            OVRManager.TrackingLost += OnTrackingLost;
            OVRManager.TrackingAcquired += OnTrackingAcquired;
            OVRManager.VrFocusLost += OnVrFocusLost;
            OVRManager.VrFocusAcquired += OnVrFocusAcquired;
            OVRManager.InputFocusLost += OnInputFocusLost;
            OVRManager.InputFocusAcquired += OnInputFocusAcquired;
        }

        private void OnDisable()
        {
            OVRManager.HMDLost -= OnHmdLost;
            OVRManager.HMDAcquired -= OnHmdAcquired;
            OVRManager.TrackingLost -= OnTrackingLost;
            OVRManager.TrackingAcquired -= OnTrackingAcquired;
            OVRManager.VrFocusLost -= OnVrFocusLost;
            OVRManager.VrFocusAcquired -= OnVrFocusAcquired;
            OVRManager.InputFocusLost -= OnInputFocusLost;
            OVRManager.InputFocusAcquired -= OnInputFocusAcquired;
        }

        private void Update()
        {
            // Update never runs while the OS has the app paused, so a frame running here (past
            // the one extra frame Unity issues after the pause callback) means it is not paused,
            // even if Unity never delivered OnApplicationPause(false).
            if (Interlocked.Read(ref pausedSinceTicks) != NoPose && Time.frameCount > pausedFrame + 1)
            {
                OnApplicationPause(false);
            }

            var poseState = OVRPlugin.GetNodePoseStateImmediate(OVRPlugin.Node.Head);
            var now = Stopwatch.GetTimestamp();
            ObserveHeadPose(poseState, now);

            positionTracked = OVRPlugin.GetNodePositionTracked(OVRPlugin.Node.Head);
            orientationTracked = OVRPlugin.GetNodeOrientationTracked(OVRPlugin.Node.Head);
            hmdPresent = OVRManager.isHmdPresent;
            vrFocus = OVRManager.hasVrFocus;
            inputFocus = OVRManager.hasInputFocus;

            SampleControllers(poseState, positionTracked, now);

            if (recording)
            {
                ObserveRecordingStall(now);
            }
        }

        // Any change counts, not only an increase: a runtime that restarts its clock
        // must not read as frozen forever.
        private static void ObserveHeadPose(OVRPlugin.PoseStatef poseState, long now)
        {
            if (poseState.Time > 0 && poseState.Time != lastOvrTime)
            {
                lastOvrTime = poseState.Time;
                Interlocked.Exchange(ref lastAdvanceTicks, now);
            }
        }

        /// <summary>
        /// Samples the head pose now. Main thread only. <see cref="RecordingManager"/> calls it
        /// right before checking <see cref="StartRefusal"/>, so the check reflects the runtime
        /// at this moment, not the last frame before a stall.
        /// </summary>
        public static void RefreshNow()
        {
            ObserveHeadPose(OVRPlugin.GetNodePoseStateImmediate(OVRPlugin.Node.Head), Stopwatch.GetTimestamp());
        }

        private void OnApplicationPause(bool paused)
        {
            if (paused)
            {
                // Keep the first pause time if Unity reports the pause twice.
                Interlocked.CompareExchange(ref pausedSinceTicks, Stopwatch.GetTimestamp(), NoPose);
                pausedFrame = Time.frameCount;
                Debug.LogWarning($"[{Constants.LOG_TAG}] HeadTrackingMonitor: app paused by the OS (recording={recording})");
            }
            else
            {
                var since = Interlocked.Exchange(ref pausedSinceTicks, NoPose);
                if (since != NoPose)
                {
                    Debug.LogWarning($"[{Constants.LOG_TAG}] HeadTrackingMonitor: app resumed after {TicksToMs(Stopwatch.GetTimestamp() - since):F0} ms paused");
                }
            }
        }

        // Samples the controllers on the same OVR nodes the controller PoseLoggers use
        // (ControllerLeft = 12, ControllerRight = 13) and in the same tracking space.
        private static void SampleControllers(OVRPlugin.PoseStatef headPose, bool headTracked, long now)
        {
            var left = SampleController(OVRInput.Controller.LTouch, OVRPlugin.Node.ControllerLeft, headPose, headTracked);
            var right = SampleController(OVRInput.Controller.RTouch, OVRPlugin.Node.ControllerRight, headPose, headTracked);
            lock (controllerLock)
            {
                leftController = left;
                rightController = right;
                controllerSampleTicks = now;
            }
        }

        private static ControllerSample SampleController(
            OVRInput.Controller controller, OVRPlugin.Node node, OVRPlugin.PoseStatef headPose, bool headTracked)
        {
            var sample = new ControllerSample
            {
                Connected = OVRInput.IsControllerConnected(controller),
                PositionTracked = OVRPlugin.GetNodePositionTracked(node),
            };
            // An untracked position is stale or predicted, not where the controller is, so
            // no distance is reported for it.
            if (sample.Connected && sample.PositionTracked && headTracked)
            {
                var c = OVRPlugin.GetNodePoseStateImmediate(node).Pose.Position;
                var h = headPose.Pose.Position;
                float dx = c.x - h.x, dy = c.y - h.y, dz = c.z - h.z;
                sample.DistanceToHeadM = Mathf.Sqrt(dx * dx + dy * dy + dz * dz);
            }
            return sample;
        }

        private static void ObserveRecordingStall(long now)
        {
            var last = Interlocked.Read(ref lastAdvanceTicks);
            if (last == NoPose)
            {
                return;
            }

            var gapTicks = now - last;
            var stalled = TicksToMs(gapTicks) > StallThresholdMs;
            if (stalled && !inStall)
            {
                inStall = true;
                Interlocked.Increment(ref recordingStalls);
                Debug.LogWarning($"[{Constants.LOG_TAG}] HeadTrackingMonitor: head poses stopped during recording ({Describe()})");
            }
            else if (!stalled && inStall)
            {
                inStall = false;
                Debug.LogWarning($"[{Constants.LOG_TAG}] HeadTrackingMonitor: head poses resumed after {TicksToMs(gapTicks):F0} ms");
            }

            if (gapTicks > Interlocked.Read(ref longestStallTicks) && stalled)
            {
                Interlocked.Exchange(ref longestStallTicks, gapTicks);
            }
        }

        /// <summary>Called by <see cref="RecordingManager"/> when a recording starts.</summary>
        public static void BeginRecording()
        {
            Interlocked.Exchange(ref recordingStalls, 0);
            Interlocked.Exchange(ref longestStallTicks, 0);
            inStall = false;
            recording = true;
        }

        /// <summary>Called by <see cref="RecordingManager"/> when a recording stops.</summary>
        public static void EndRecording()
        {
            recording = false;
            var stalls = Interlocked.CompareExchange(ref recordingStalls, 0, 0);
            if (stalls > 0)
            {
                Debug.LogWarning(
                    $"[{Constants.LOG_TAG}] HeadTrackingMonitor: recording had {stalls} head-pose stall(s), " +
                    $"longest {TicksToMs(Interlocked.Read(ref longestStallTicks)):F0} ms");
            }
        }

        /// <summary>Milliseconds since the OVR runtime last delivered a new head pose; null if it never has.</summary>
        public static double? HeadPoseAgeMs
        {
            get
            {
                var last = Interlocked.Read(ref lastAdvanceTicks);
                return last == NoPose ? null : TicksToMs(Stopwatch.GetTimestamp() - last);
            }
        }

        /// <summary>
        /// Why a recording must not start now, or null when head tracking is live. A session
        /// started without head poses has no usable HMD, controller, IMU or body data.
        /// </summary>
        public static string? StartRefusal
        {
            get
            {
                var age = HeadPoseAgeMs;
                if (age is null)
                {
                    return "head tracking has not delivered a pose since the app started; " +
                        "put on the headset, or restart the capture app";
                }
                if (age > StartMaxPoseAgeMs)
                {
                    return $"head tracking stopped {age.Value / 1000.0:F1} s ago ({Describe()}); " +
                        "restart the capture app or reboot the headset";
                }
                return null;
            }
        }

        /// <summary>
        /// The <c>tracking</c> object for <c>/api/status</c>, without a trailing comma.
        /// Safe to call from any thread.
        /// </summary>
        public static string StatusJson(string indent)
        {
            var age = HeadPoseAgeMs;
            string? evt;
            long evtTicks;
            lock (eventLock)
            {
                evt = lastEvent;
                evtTicks = lastEventTicks;
            }
            var evtAge = evtTicks == NoPose ? (double?)null : TicksToMs(Stopwatch.GetTimestamp() - evtTicks);
            var refusal = StartRefusal;
            return "{\n" +
                $"{indent}  \"headPoseAgeMs\": {Number(age)},\n" +
                $"{indent}  \"positionTracked\": {Bool(positionTracked)},\n" +
                $"{indent}  \"orientationTracked\": {Bool(orientationTracked)},\n" +
                $"{indent}  \"hmdPresent\": {Bool(hmdPresent)},\n" +
                $"{indent}  \"vrFocus\": {Bool(vrFocus)},\n" +
                $"{indent}  \"inputFocus\": {Bool(inputFocus)},\n" +
                $"{indent}  \"startRefusal\": {Str(refusal)},\n" +
                $"{indent}  \"recordingStalls\": {Interlocked.CompareExchange(ref recordingStalls, 0, 0)},\n" +
                $"{indent}  \"longestStallMs\": {TicksToMs(Interlocked.Read(ref longestStallTicks)):F0},\n" +
                $"{indent}  \"lastEvent\": {Str(evt)},\n" +
                $"{indent}  \"lastEventAgeMs\": {Number(evtAge)}\n" +
                $"{indent}}}";
        }

        /// <summary>True while the OS has the app paused. Safe to call from any thread.</summary>
        public static bool AppPaused => Interlocked.Read(ref pausedSinceTicks) != NoPose;

        /// <summary>
        /// The <c>app</c> object for <c>/api/status</c> and <c>/api/diagnostics</c>, without a
        /// trailing comma. Safe to call from any thread.
        /// </summary>
        public static string AppJson(string indent)
        {
            var since = Interlocked.Read(ref pausedSinceTicks);
            var pausedForMs = since == NoPose ? (double?)null : TicksToMs(Stopwatch.GetTimestamp() - since);
            return "{\n" +
                $"{indent}  \"paused\": {Bool(since != NoPose)},\n" +
                $"{indent}  \"pausedForMs\": {Number(pausedForMs)}\n" +
                $"{indent}}}";
        }

        /// <summary>
        /// The <c>controllers</c> object for <c>/api/status</c>, without a trailing comma.
        /// Values come from the last main-thread sample; <c>sampleAgeMs</c> grows while the
        /// main thread is blocked or the app is paused. Safe to call from any thread.
        /// </summary>
        public static string ControllersJson(string indent)
        {
            ControllerSample left;
            ControllerSample right;
            long sampleTicks;
            lock (controllerLock)
            {
                left = leftController;
                right = rightController;
                sampleTicks = controllerSampleTicks;
            }
            var sampleAge = sampleTicks == NoPose ? (double?)null : TicksToMs(Stopwatch.GetTimestamp() - sampleTicks);
            return "{\n" +
                $"{indent}  \"left\": {ControllerJson(left)},\n" +
                $"{indent}  \"right\": {ControllerJson(right)},\n" +
                $"{indent}  \"sampleAgeMs\": {Number(sampleAge)}\n" +
                $"{indent}}}";
        }

        private static string ControllerJson(ControllerSample sample)
        {
            var distance = sample.DistanceToHeadM is float d
                ? d.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)
                : "null";
            return $"{{\"connected\": {Bool(sample.Connected)}, " +
                $"\"positionTracked\": {Bool(sample.PositionTracked)}, " +
                $"\"distanceToHeadM\": {distance}}}";
        }

        private static string Describe()
        {
            return $"positionTracked={positionTracked}, orientationTracked={orientationTracked}, " +
                $"hmdPresent={hmdPresent}, vrFocus={vrFocus}, inputFocus={inputFocus}";
        }

        private static void Record(string name)
        {
            lock (eventLock)
            {
                lastEvent = name;
                lastEventTicks = Stopwatch.GetTimestamp();
            }
            Debug.LogWarning($"[{Constants.LOG_TAG}] HeadTrackingMonitor: OVR {name} (recording={recording})");
        }

        private static void OnHmdLost() => Record("HMDLost");
        private static void OnHmdAcquired() => Record("HMDAcquired");
        private static void OnTrackingLost() => Record("TrackingLost");
        private static void OnTrackingAcquired() => Record("TrackingAcquired");
        private static void OnVrFocusLost() => Record("VrFocusLost");
        private static void OnVrFocusAcquired() => Record("VrFocusAcquired");
        private static void OnInputFocusLost() => Record("InputFocusLost");
        private static void OnInputFocusAcquired() => Record("InputFocusAcquired");

        private static double TicksToMs(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

        private static string Bool(bool value) => value ? "true" : "false";

        private static string Number(double? value) =>
            value is double v ? v.ToString("F0", System.Globalization.CultureInfo.InvariantCulture) : "null";

        private static string Str(string? value)
        {
            if (value is null)
            {
                return "null";
            }
            return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n") + "\"";
        }
    }
}
