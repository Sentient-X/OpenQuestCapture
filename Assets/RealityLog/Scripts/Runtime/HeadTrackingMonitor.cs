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

        // Admission source freshness uses the 100 ms bound from the native body/pose
        // gap policy, but measures age now, not gaps within a completed recording.
        // Recorded samples and the separate final validator remain unchanged.
        private const double StartMaxSourceAgeMs = 100.0;

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
        // BodyTrackingLogger starts the body runtime at app launch. This monitor reads it;
        // it does not own or restart the body-tracking lifecycle.
        private static OVRPlugin.BodyState bodyState = new()
        {
            JointLocations = new OVRPlugin.BodyJointLocation[84]
        };
        private static BodySample bodySample;
        private struct BodySample
        {
            public ControllerSample LeftShoulder;
            public ControllerSample RightShoulder;
            public float Confidence;
            public double SourceTime;
            public double? AgeMs;
        }
        private static ControllerSample headSample;
        private static ControllerSample leftController;
        private static ControllerSample rightController;
        private static long controllerSampleTicks = NoPose;

        private struct ControllerSample
        {
            public bool Connected;
            public bool PositionTracked;
            public bool OrientationTracked;
            public OVRPlugin.Posef Pose;
            public double SourceTime;
            public double? SourceAgeMs;
            public float? DistanceToHeadM;
        }

        // Statics survive Enter Play Mode without a domain reload in the Editor.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            Interlocked.Exchange(ref lastAdvanceTicks, NoPose);
            lastOvrTime = 0;
            positionTracked = orientationTracked = hmdPresent = vrFocus = inputFocus = false;
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
                bodySample = default;
                headSample = default;
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

            RefreshNow();

            if (recording)
            {
                ObserveRecordingStall(Stopwatch.GetTimestamp());
            }
        }

        // Any change counts, not only an increase: a runtime that restarts its clock
        // must not read as frozen forever.
        private static void ObserveHeadPose(OVRPlugin.PoseStatef poseState, long now)
        {
            if (poseState.Time > 0 && !double.IsInfinity(poseState.Time) && poseState.Time != lastOvrTime)
            {
                lastOvrTime = poseState.Time;
                Interlocked.Exchange(ref lastAdvanceTicks, now);
            }
        }

        /// <summary>
        /// Samples all tracking readiness now. Main thread only. <see cref="RecordingManager"/> calls it
        /// right before checking <see cref="StartRefusal"/>, so the check reflects the runtime
        /// at this moment, not the last frame before a stall.
        /// </summary>
        public static void RefreshNow()
        {
            var poseState = OVRPlugin.GetNodePoseStateImmediate(OVRPlugin.Node.Head);
            ObserveHeadPose(poseState, Stopwatch.GetTimestamp());
            positionTracked = OVRPlugin.GetNodePositionTracked(OVRPlugin.Node.Head);
            orientationTracked = OVRPlugin.GetNodeOrientationTracked(OVRPlugin.Node.Head);
            hmdPresent = OVRManager.isHmdPresent;
            vrFocus = OVRManager.hasVrFocus;
            inputFocus = OVRManager.hasInputFocus;
            SampleControllers(poseState, positionTracked);
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
        private static void SampleControllers(OVRPlugin.PoseStatef headPose, bool headTracked)
        {
            var left = SampleController(OVRInput.Controller.LTouch, OVRPlugin.Node.ControllerLeft, headPose, headTracked);
            var right = SampleController(OVRInput.Controller.RTouch, OVRPlugin.Node.ControllerRight, headPose, headTracked);
            var body = SampleBody();
            var sourceNow = OVRPlugin.GetTimeInSeconds();
            var now = Stopwatch.GetTimestamp();
            left.SourceAgeMs = SourceAgeMs(left.SourceTime, sourceNow);
            right.SourceAgeMs = SourceAgeMs(right.SourceTime, sourceNow);
            body.AgeMs = SourceAgeMs(body.SourceTime, sourceNow);
            if (body.AgeMs == null) body = default;
            lock (controllerLock)
            {
                bodySample = body;
                headSample = new ControllerSample
                {
                    PositionTracked = headTracked,
                    OrientationTracked = orientationTracked,
                    Pose = headPose.Pose,
                    SourceAgeMs = SourceAgeMs(headPose.Time, sourceNow),
                };
                leftController = left;
                rightController = right;
                controllerSampleTicks = now;
            }
        }

        private static BodySample SampleBody()
        {
            if (!OVRPlugin.GetBodyState4(OVRPlugin.Step.Render, OVRPlugin.BodyJointSet.FullBody, ref bodyState))
                return default;
            if (bodyState.JointLocations == null || bodyState.JointLocations.Length <= 13)
                return default;
            return new BodySample
            {
                LeftShoulder = Shoulder(bodyState.JointLocations[8]),
                RightShoulder = Shoulder(bodyState.JointLocations[13]),
                Confidence = bodyState.Confidence,
                SourceTime = bodyState.Time,
            };
        }

        // FullBody joint indices match BodyTrackingLogger: LeftShoulder=8, RightShoulder=13.
        private static ControllerSample Shoulder(OVRPlugin.BodyJointLocation joint) => new()
        {
            PositionTracked = (joint.LocationFlags & OVRPlugin.SpaceLocationFlags.PositionValid) != 0,
            OrientationTracked = (joint.LocationFlags & OVRPlugin.SpaceLocationFlags.OrientationValid) != 0,
            Pose = joint.Pose,
        };

        private static ControllerSample SampleController(
            OVRInput.Controller controller, OVRPlugin.Node node, OVRPlugin.PoseStatef headPose, bool headTracked)
        {
            var poseState = OVRPlugin.GetNodePoseStateImmediate(node);
            var sample = new ControllerSample
            {
                Connected = OVRInput.IsControllerConnected(controller),
                PositionTracked = OVRPlugin.GetNodePositionTracked(node),
                OrientationTracked = OVRPlugin.GetNodeOrientationTracked(node),
                Pose = poseState.Pose,
                SourceTime = poseState.Time,
            };
            // An untracked position is stale or predicted, not where the controller is, so
            // no distance is reported for it.
            if (sample.Connected && sample.PositionTracked && headTracked)
            {
                var c = sample.Pose.Position;
                var h = headPose.Pose.Position;
                float dx = c.x - h.x, dy = c.y - h.y, dz = c.z - h.z;
                sample.DistanceToHeadM = Mathf.Sqrt(dx * dx + dy * dy + dz * dz);
            }
            return sample;
        }

        private static double? SourceAgeMs(double timestamp, double now)
        {
            if (timestamp <= 0 || double.IsNaN(timestamp) || double.IsInfinity(timestamp)) return null;
            var age = (now - timestamp) * 1000.0;
            // Retain the existing monitor's bounded 100 ms future allowance. Body
            // Step.Render predicts ahead; the SDK does not specify a zero-skew
            // comparison between immediate pose time and GetTimeInSeconds either.
            if (double.IsNaN(age) || double.IsInfinity(age) || age < -100.0) return null;
            return Math.Max(0, age);
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
        /// Why a recording must not start now. Safe from any thread; the start caller must
        /// first call RefreshNow on Unity's thread so flags and source ages are current.
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
                ControllerSample head, left, right;
                BodySample body;
                long sampledAt;
                lock (controllerLock)
                {
                    head = headSample;
                    left = leftController;
                    right = rightController;
                    body = bodySample;
                    sampledAt = controllerSampleTicks;
                }
                var snapshotAgeMs = sampledAt == NoPose ? double.PositiveInfinity
                    : TicksToMs(Stopwatch.GetTimestamp() - sampledAt);
                var refusal = PoseStartRefusal("head", head, snapshotAgeMs, needsConnection: false)
                    ?? PoseStartRefusal("left controller", left, snapshotAgeMs, needsConnection: true)
                    ?? PoseStartRefusal("right controller", right, snapshotAgeMs, needsConnection: true);
                if (refusal != null) return refusal;
                if (!Fresh(body.AgeMs, snapshotAgeMs))
                    return "body tracking has no fresh active sample (max 100 ms); wear the headset and wait for tracking";
                return null;
            }
        }

        private static string? PoseStartRefusal(string name, ControllerSample sample, double snapshotAgeMs, bool needsConnection)
        {
            if (needsConnection && !sample.Connected)
                return $"{name} is not connected; wake it and keep it visible to the headset";
            if (!sample.PositionTracked || !sample.OrientationTracked)
                return needsConnection
                    ? $"{name} position and orientation must both be tracked; keep it visible to the headset"
                    : "head position and orientation must both be tracked; put on the headset and wait for tracking";
            if (!Fresh(sample.SourceAgeMs, snapshotAgeMs))
                return $"{name} has no fresh source pose (max 100 ms); wait for tracking";
            return null;
        }

        private static bool Fresh(double? sourceAgeMs, double snapshotAgeMs) =>
            sourceAgeMs is double age && age + snapshotAgeMs <= StartMaxSourceAgeMs;

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
            BodySample body;
            ControllerSample head;
            ControllerSample left;
            ControllerSample right;
            long sampleTicks;
            lock (controllerLock)
            {
                body = bodySample;
                head = headSample;
                left = leftController;
                right = rightController;
                sampleTicks = controllerSampleTicks;
            }
            var sampleAge = sampleTicks == NoPose ? (double?)null : TicksToMs(Stopwatch.GetTimestamp() - sampleTicks);
            return "{\n" +
                $"{indent}  \"body\": {BodyJson(body, sampleAge)},\n" +
                $"{indent}  \"head\": {ControllerJson(head)},\n" +
                $"{indent}  \"left\": {ControllerJson(left)},\n" +
                $"{indent}  \"right\": {ControllerJson(right)},\n" +
                $"{indent}  \"sampleAgeMs\": {Number(sampleAge)}\n" +
                $"{indent}}}";
        }

        private static string BodyJson(BodySample body, double? statusAgeMs)
        {
            var age = body.AgeMs + statusAgeMs;
            return $"{{\"sampleAgeMs\": {Number(age)}, \"confidence\": {Finite(body.Confidence)}, " +
                $"\"leftShoulder\": {ControllerJson(body.LeftShoulder)}, " +
                $"\"rightShoulder\": {ControllerJson(body.RightShoulder)}}}";
        }

        private static string ControllerJson(ControllerSample sample)
        {
            var distance = sample.DistanceToHeadM is float d
                ? d.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)
                : "null";
            return $"{{\"connected\": {Bool(sample.Connected)}, " +
                $"\"positionTracked\": {Bool(sample.PositionTracked)}, " +
                $"\"orientationTracked\": {Bool(sample.OrientationTracked)}, " +
                $"\"pose\": {PoseJson(sample)}, " +
                $"\"distanceToHeadM\": {distance}}}";
        }

        // Raw OVR coordinates, as in the pose CSVs. Never mix Unity-converted rotations
        // with these positions. The pod owns the start-pose policy.
        private static string PoseJson(ControllerSample sample)
        {
            if (!sample.PositionTracked || !sample.OrientationTracked) return "null";
            var p = sample.Pose.Position;
            var q = sample.Pose.Orientation;
            return $"{{\"position\": {{\"x\": {Finite(p.x)}, \"y\": {Finite(p.y)}, \"z\": {Finite(p.z)}}}, " +
                $"\"orientation\": {{\"x\": {Finite(q.x)}, \"y\": {Finite(q.y)}, \"z\": {Finite(q.z)}, \"w\": {Finite(q.w)}}}}}";
        }

        private static string Finite(float value) => float.IsNaN(value) || float.IsInfinity(value)
            ? "null" : value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

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
