#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;
using RealityLog.Common;
using RealityLog.IO;

namespace RealityLog.OVR
{
    /// <summary>
    /// Logs full body tracking skeleton data to CSV during recording sessions.
    /// Uses OVRPlugin.GetBodyState4 with FullBody joint set (84 joints).
    /// Each row contains a timestamp and all joint positions/orientations.
    ///
    /// <para><b>Timing (measured on Quest 3S):</b> <c>GetBodyState4(Step.Render)</c> returns
    /// <c>Time</c> = the predicted display time of the frame, so body timestamps land on
    /// display frames. Rows are polled in <c>FixedUpdate</c> (50 Hz, like the pose loggers)
    /// and a sample whose <c>Time</c> did not advance is skipped. At the 90 Hz display the
    /// pods use, the body runtime itself produces a new sample on about 2 of every 3
    /// frames (~60 Hz), and this gives ~49 Hz rows with gaps of 1–3 frames (max ~34 ms),
    /// 1–2% stale polls. Even spacing is not available from the source: the only evenly
    /// spaced decimation of a 2-in-3 cadence is every 3rd frame (30 Hz, the QA floor), so
    /// none is applied. At a healthy battery, 72 and 90 Hz both measured ~49-50 Hz with
    /// 0-1.6% stale polls; sessions at very low battery (~6-13%) showed ~10% stale polls.
    /// Gaps of 580 ms or more indicate tracking loss (e.g. body out of view).</para>
    ///
    /// <para><b>Extra columns</b> (after all joint columns): <c>skeleton_changed_count</c>
    /// (BodyState.SkeletonChangedCount) and <c>joint_valid_mask_0..2</c>: bit (i % 32) of
    /// mask (i / 32) is set when joint i has both PositionValid and OrientationValid
    /// (mask_0 = joints 0–31, mask_1 = 32–63, mask_2 = 64–83). Each mask is below 2^32 so
    /// it round-trips exactly through the CSV's double formatting. There is no is_active
    /// column: SDK 81 does not expose IsActive on BodyState; GetBodyState4 returns false
    /// when the body is inactive, so every written row is active (inactive polls are
    /// counted in failed_polls).</para>
    ///
    /// <para><b>Stats</b> (<see cref="StatsJson"/>, frozen at StopLogging): polls,
    /// stale_polls (Time did not advance: the runtime had no new sample; excludes
    /// same-frame polls), same_frame_polls (another poll in the same rendered frame),
    /// failed_polls (GetBodyState4 false: body inactive), rows, display_hz,
    /// fidelity_granted.</para>
    ///
    /// <para><b>Root joint (index 0):</b> Floor-projected origin (Y ≈ 0). For actual
    /// pelvis height, use Hips (index 1).</para>
    ///
    /// <para><b>Joint names:</b> Hardcoded from the XR_META_body_tracking_full_body spec
    /// to avoid an assembly dependency on the Interaction SDK (BodyJointId enum).</para>
    /// </summary>
    public class BodyTrackingLogger : MonoBehaviour
    {
        private static readonly OVRPlugin.BodyJointSet JOINT_SET = OVRPlugin.BodyJointSet.FullBody;
        private const int FULL_BODY_JOINT_COUNT = 84;
        // 7 values per joint (pos xyz + rot xyzw)
        private const int VALUES_PER_JOINT = 7;
        // unix_time, ovr_timestamp, mono_time_ns, confidence, calibration_status, fidelity
        private const int LEADING_COLUMNS = 6;
        // skeleton_changed_count, joint_valid_mask_0, joint_valid_mask_1, joint_valid_mask_2
        private const int TRAILING_COLUMNS = 4;
        private const int ROW_LENGTH = LEADING_COLUMNS + FULL_BODY_JOINT_COUNT * VALUES_PER_JOINT + TRAILING_COLUMNS;
        private const OVRPlugin.SpaceLocationFlags JOINT_VALID_FLAGS =
            OVRPlugin.SpaceLocationFlags.PositionValid | OVRPlugin.SpaceLocationFlags.OrientationValid;

        /// <summary>
        /// Full-body joint names matching XR_META_body_tracking_full_body / BodyJointId enum.
        /// Index 0 = Root (floor-projected, Y≈0; use Hips for pelvis height).
        /// </summary>
        private static readonly string[] JOINT_NAMES = new string[]
        {
            "Root",                          // 0
            "Hips",                          // 1
            "SpineLower",                    // 2
            "SpineMiddle",                   // 3
            "SpineUpper",                    // 4
            "Chest",                         // 5
            "Neck",                          // 6
            "Head",                          // 7
            "LeftShoulder",                  // 8
            "LeftScapula",                   // 9
            "LeftArmUpper",                  // 10
            "LeftArmLower",                  // 11
            "LeftHandWristTwist",            // 12
            "RightShoulder",                 // 13
            "RightScapula",                  // 14
            "RightArmUpper",                 // 15
            "RightArmLower",                 // 16
            "RightHandWristTwist",           // 17
            "LeftHandPalm",                  // 18
            "LeftHandWrist",                 // 19
            "LeftHandThumbMetacarpal",       // 20
            "LeftHandThumbProximal",         // 21
            "LeftHandThumbDistal",           // 22
            "LeftHandThumbTip",              // 23
            "LeftHandIndexMetacarpal",       // 24
            "LeftHandIndexProximal",         // 25
            "LeftHandIndexIntermediate",     // 26
            "LeftHandIndexDistal",           // 27
            "LeftHandIndexTip",              // 28
            "LeftHandMiddleMetacarpal",      // 29
            "LeftHandMiddleProximal",        // 30
            "LeftHandMiddleIntermediate",    // 31
            "LeftHandMiddleDistal",          // 32
            "LeftHandMiddleTip",             // 33
            "LeftHandRingMetacarpal",        // 34
            "LeftHandRingProximal",          // 35
            "LeftHandRingIntermediate",      // 36
            "LeftHandRingDistal",            // 37
            "LeftHandRingTip",               // 38
            "LeftHandLittleMetacarpal",      // 39
            "LeftHandLittleProximal",        // 40
            "LeftHandLittleIntermediate",    // 41
            "LeftHandLittleDistal",          // 42
            "LeftHandLittleTip",             // 43
            "RightHandPalm",                 // 44
            "RightHandWrist",                // 45
            "RightHandThumbMetacarpal",      // 46
            "RightHandThumbProximal",        // 47
            "RightHandThumbDistal",          // 48
            "RightHandThumbTip",             // 49
            "RightHandIndexMetacarpal",      // 50
            "RightHandIndexProximal",        // 51
            "RightHandIndexIntermediate",    // 52
            "RightHandIndexDistal",          // 53
            "RightHandIndexTip",             // 54
            "RightHandMiddleMetacarpal",     // 55
            "RightHandMiddleProximal",       // 56
            "RightHandMiddleIntermediate",   // 57
            "RightHandMiddleDistal",         // 58
            "RightHandMiddleTip",            // 59
            "RightHandRingMetacarpal",       // 60
            "RightHandRingProximal",         // 61
            "RightHandRingIntermediate",     // 62
            "RightHandRingDistal",           // 63
            "RightHandRingTip",              // 64
            "RightHandLittleMetacarpal",     // 65
            "RightHandLittleProximal",       // 66
            "RightHandLittleIntermediate",   // 67
            "RightHandLittleDistal",         // 68
            "RightHandLittleTip",            // 69
            "LeftUpperLeg",                  // 70
            "LeftLowerLeg",                  // 71
            "LeftFootAnkleTwist",            // 72
            "LeftFootAnkle",                 // 73
            "LeftFootSubtalar",              // 74
            "LeftFootTransverse",            // 75
            "LeftFootBall",                  // 76
            "RightUpperLeg",                 // 77
            "RightLowerLeg",                 // 78
            "RightFootAnkleTwist",           // 79
            "RightFootAnkle",                // 80
            "RightFootSubtalar",             // 81
            "RightFootTransverse",           // 82
            "RightFootBall",                 // 83
        };

        [SerializeField] private string fileName = "body_tracking.csv";
        [SerializeField] private string directoryName = "";
        [SerializeField] private bool startLoggingOnStart = false;

        private CsvWriter? writer = null;
        private OVRPlugin.BodyState bodyState;
        private bool bodyTrackingStarted = false;

        private double baseOvrTimeSec;
        private long baseUnixTimeMs;
        private double latestTimestamp;

        // Display frequency at the session start, reported in the stats.
        private float displayHz = 0f;
        private int lastPollFrame = -1;

        // Per-session counters: reset at StartLogging, frozen once the writer is stopped.
        private long polls;
        private long stalePolls;
        private long sameFramePolls;
        private long failedPolls;
        private long rows;
        private int fidelityGranted;

        public string DirectoryName
        {
            get => directoryName;
            set => directoryName = value;
        }

        public string FileName => fileName;

        /// <summary>
        /// Rows the CSV writer actually wrote in the current/last session (0 if none), like
        /// the other loggers; the stats' <c>rows</c> counts rows handed to the writer.
        /// </summary>
        public long RowsWritten => writer?.RowsWritten ?? lastRowsWritten;
        private long lastRowsWritten;

        public void StartLogging()
        {
            try
            {
                StopLogging();

                baseOvrTimeSec = OVRPlugin.GetTimeInSeconds();
                baseUnixTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                latestTimestamp = 0;
                ResetSession();

                Debug.Log($"[{Constants.LOG_TAG}] {fileName} - Reset base times: OVR={baseOvrTimeSec:F3}s, Unix={baseUnixTimeMs}ms");

                if (!EnsureBodyTrackingStarted())
                {
                    return;
                }

                var filePath = Path.Combine(Application.persistentDataPath, DirectoryName, fileName);
                writer = new CsvWriter(filePath, BuildHeader());
            }
            catch (Exception ex)
            {
                Debug.LogError($"[{Constants.LOG_TAG}] BodyTrackingLogger - Failed to start: {ex.Message}");
                writer = null;
            }
        }

        public void StopLogging()
        {
            try
            {
                writer?.Dispose();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[{Constants.LOG_TAG}] BodyTrackingLogger - Failed to dispose writer: {ex.Message}");
            }
            if (writer != null)
            {
                lastRowsWritten = writer.RowsWritten;
            }
            writer = null;
            // Counters only change while a writer exists, so from here on they are the
            // session snapshot read by StatsJson() and RowsWritten.
        }

        /// <summary>Body sampling counters of the current/last session as a JSON object.</summary>
        public string StatsJson()
        {
            var inv = CultureInfo.InvariantCulture;
            float hz = (float.IsNaN(displayHz) || float.IsInfinity(displayHz)) ? 0f : displayHz;
            return "{"
                + $"\"polls\":{polls.ToString(inv)},"
                + $"\"stale_polls\":{stalePolls.ToString(inv)},"
                + $"\"same_frame_polls\":{sameFramePolls.ToString(inv)},"
                + $"\"failed_polls\":{failedPolls.ToString(inv)},"
                + $"\"rows\":{rows.ToString(inv)},"
                + $"\"display_hz\":{hz.ToString("R", inv)},"
                + $"\"fidelity_granted\":{fidelityGranted.ToString(inv)}"
                + "}";
        }

        private void ResetSession()
        {
            lastRowsWritten = 0;
            polls = 0;
            stalePolls = 0;
            sameFramePolls = 0;
            failedPolls = 0;
            rows = 0;
            fidelityGranted = 0;

            lastPollFrame = -1;
            displayHz = OVRPlugin.systemDisplayFrequency;
        }

        private void Start()
        {
            baseOvrTimeSec = OVRPlugin.GetTimeInSeconds();
            baseUnixTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            bodyState = new OVRPlugin.BodyState
            {
                JointLocations = new OVRPlugin.BodyJointLocation[FULL_BODY_JOINT_COUNT]
            };

            // Start tracking at launch, not at the first recording. Measured on Quest 3S:
            // started with the recording, the first body sample came 534 ms late, then a
            // 393 ms gap, and the skeleton re-estimated ~17 times in the first 2 s, which
            // fails the pod's 500 ms stream-start and 100 ms gap rules for the first
            // episode after every app (re)launch. Warm, the same session shape starts at
            // -10 ms with a 42 ms max gap.
            EnsureBodyTrackingStarted();

            if (startLoggingOnStart)
            {
                StartLogging();
            }
        }

        private bool EnsureBodyTrackingStarted()
        {
            if (bodyTrackingStarted)
            {
                return true;
            }

            bodyTrackingStarted = OVRPlugin.StartBodyTracking2(JOINT_SET);
            if (!bodyTrackingStarted)
            {
                Debug.LogError($"[{Constants.LOG_TAG}] BodyTrackingLogger - Failed to start body tracking");
                return false;
            }
            OVRPlugin.RequestBodyTrackingFidelity(OVRPlugin.BodyTrackingFidelity2.High);
            Debug.Log($"[{Constants.LOG_TAG}] BodyTrackingLogger - Body tracking started (FullBody, High fidelity)");
            return true;
        }

        private void FixedUpdate()
        {
            Poll();
        }

        private void Poll()
        {
            if (writer == null)
                return;

            polls++;
            int frame = Time.frameCount;
            bool sameFrame = frame == lastPollFrame;
            lastPollFrame = frame;
            if (sameFrame)
                sameFramePolls++;

            if (!OVRPlugin.GetBodyState4(OVRPlugin.Step.Render, JOINT_SET, ref bodyState))
            {
                failedPolls++;
                return;
            }

            fidelityGranted = (int)bodyState.Fidelity;

            var timestamp = bodyState.Time;
            if (timestamp <= latestTimestamp)
            {
                // Same-frame repeats are counted above; stale = the runtime had no new sample.
                if (!sameFrame)
                    stalePolls++;
                return;
            }

            latestTimestamp = timestamp;

            var joints = bodyState.JointLocations;
            if (joints == null || joints.Length == 0)
                return;

            int jointCount = Mathf.Min(joints.Length, FULL_BODY_JOINT_COUNT);

            // Build row: unix_time, ovr_timestamp, mono_time_ns, confidence,
            // calibration_status, fidelity, per-joint data, then skeleton_changed_count
            // and joint_valid_mask_0..2. Fixed length so the trailing columns never shift.
            // A fresh array per row: the writer may format it later on its own thread.
            var row = new double[ROW_LENGTH];
            row[0] = ConvertOvrSecToUnixTimeMs(timestamp);
            row[1] = timestamp;
            row[2] = MonotonicClock.Nanos();
            row[3] = bodyState.Confidence;
            row[4] = (double)bodyState.CalibrationStatus;
            row[5] = (double)bodyState.Fidelity;

            uint validMask0 = 0, validMask1 = 0, validMask2 = 0;
            int offset = LEADING_COLUMNS;
            for (int i = 0; i < FULL_BODY_JOINT_COUNT; i++)
            {
                if (i >= jointCount)
                {
                    // Joint not reported (never expected for FullBody): NaN pose, invalid.
                    for (int k = 0; k < VALUES_PER_JOINT; k++)
                        row[offset + k] = double.NaN;
                    offset += VALUES_PER_JOINT;
                    continue;
                }

                var joint = joints[i];
                var pose = joint.Pose;
                row[offset + 0] = pose.Position.x;
                row[offset + 1] = pose.Position.y;
                row[offset + 2] = pose.Position.z;
                row[offset + 3] = pose.Orientation.x;
                row[offset + 4] = pose.Orientation.y;
                row[offset + 5] = pose.Orientation.z;
                row[offset + 6] = pose.Orientation.w;
                offset += VALUES_PER_JOINT;

                if ((joint.LocationFlags & JOINT_VALID_FLAGS) == JOINT_VALID_FLAGS)
                {
                    if (i < 32)
                        validMask0 |= 1u << i;
                    else if (i < 64)
                        validMask1 |= 1u << (i - 32);
                    else
                        validMask2 |= 1u << (i - 64);
                }
            }

            row[offset + 0] = bodyState.SkeletonChangedCount;
            row[offset + 1] = validMask0;
            row[offset + 2] = validMask1;
            row[offset + 3] = validMask2;

            writer.EnqueueRow(row);
            rows++;
        }

        private string[] BuildHeader()
        {
            var header = new List<string>
            {
                "unix_time", "ovr_timestamp", "mono_time_ns", "confidence", "calibration_status", "fidelity"
            };

            for (int i = 0; i < FULL_BODY_JOINT_COUNT; i++)
            {
                string jointName = GetJointName(i);
                header.Add($"{jointName}_pos_x");
                header.Add($"{jointName}_pos_y");
                header.Add($"{jointName}_pos_z");
                header.Add($"{jointName}_rot_x");
                header.Add($"{jointName}_rot_y");
                header.Add($"{jointName}_rot_z");
                header.Add($"{jointName}_rot_w");
            }

            header.Add("skeleton_changed_count");
            header.Add("joint_valid_mask_0");
            header.Add("joint_valid_mask_1");
            header.Add("joint_valid_mask_2");

            return header.ToArray();
        }

        private static string GetJointName(int index)
        {
            if (index >= 0 && index < JOINT_NAMES.Length)
            {
                return JOINT_NAMES[index];
            }
            return $"Joint_{index}";
        }

        private long ConvertOvrSecToUnixTimeMs(double ovrTime)
        {
            var deltaSec = ovrTime - baseOvrTimeSec;
            var deltaMs = (long)(deltaSec * 1000.0);
            return baseUnixTimeMs + deltaMs;
        }

        private void OnDestroy()
        {
            if (bodyTrackingStarted)
            {
                OVRPlugin.StopBodyTracking();
                bodyTrackingStarted = false;
            }
            writer?.Dispose();
            writer = null;
        }
    }
}
