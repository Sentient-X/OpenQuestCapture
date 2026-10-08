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
    /// <para><b>Timing (measured):</b> <c>GetBodyState4(Step.Render)</c> returns
    /// <c>Time</c> = the predicted display time of the current frame, so body timestamps
    /// land on display frames (13.9 ms at 72 Hz, 11.1 ms at 90 Hz). The previous
    /// <c>FixedUpdate</c> (50 Hz) polling therefore produced 1/2/3-frame gaps and
    /// 44.9–49.2 Hz instead of 50 Hz: two FixedUpdates in one rendered frame see the same
    /// <c>Time</c> (app artifact), and a 3-frame gap means the runtime did not produce a
    /// new sample (headset drop). 50 Hz cannot be evenly spaced on a 72/90 Hz display.</para>
    ///
    /// <para><b>Display-frame-locked sampling</b> (<c>displayFrameLocked</c>, default OFF until
    /// the stats below confirm on a 90 Hz pod that the body runtime delivers a new sample on
    /// (nearly) every display frame; if it runs at 30 or 60 Hz the grid would thin it too far):
    /// the state is read once per rendered frame in <c>Update</c>. Each new sample gets a
    /// display-frame index (index 0 = the session's first sample; each later sample adds
    /// round((Time - previous sample Time) * systemDisplayFrequency), so a nominal
    /// frequency that is slightly off cannot drift the grid), and a sample is written only
    /// if it is at least <c>keepEveryNthDisplayFrame</c> frames (default 2) after the last
    /// written one: with a sample every frame that is exactly every 2nd frame (45 Hz at
    /// 90 Hz, 36 Hz at 72 Hz); a 30 Hz runtime still gives 30 Hz, and a runtime drop gives
    /// a gap of N+1 frames rather than 2N. A sample more than 0.25 frame away from the grid is written anyway (counted in
    /// rows - kept_on_grid) so a wrong grid assumption never loses data. With the lock off,
    /// or when the display frequency is unknown, the old behaviour is used: every new
    /// sample, polled in <c>FixedUpdate</c>. Duplicate timestamps are always filtered.
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
    /// stale_polls (Time did not advance, excluding same-frame polls), same_frame_polls
    /// (another poll in the same rendered frame; always 0 when frame-locked),
    /// failed_polls (GetBodyState4 false), rows, kept_on_grid, skipped_off_grid
    /// (new samples deliberately dropped because they are not on a kept frame),
    /// display_hz, fidelity_granted.</para>
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
        // Max distance (in display frames) from the integer grid before a sample counts as off-grid.
        private const double OFF_GRID_TOLERANCE_FRAMES = 0.25;

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
        [Tooltip("Poll once per rendered frame (Update) and keep only every Nth display frame for evenly spaced rows. Off = legacy FixedUpdate polling.")]
        [SerializeField] private bool displayFrameLocked = false;
        [Tooltip("Keep one sample every N display frames when displayFrameLocked (2 = 45 Hz at 90 Hz, 36 Hz at 72 Hz).")]
        [SerializeField] private int keepEveryNthDisplayFrame = 2;

        private CsvWriter? writer = null;
        private OVRPlugin.BodyState bodyState;
        private bool bodyTrackingStarted = false;

        private double baseOvrTimeSec;
        private long baseUnixTimeMs;
        private double latestTimestamp;

        // Sampling mode of the current session (decided at StartLogging).
        private bool gridLocked = false;
        private int keepEveryN = 1;
        private float displayHz = 0f;
        private bool hasGridAnchor = false;
        private double gridAnchorTime;
        private long gridAnchorIndex;
        private long lastKeptFrameIndex = -1;
        private bool offGridWarned = false;
        private int lastPollFrame = -1;

        // Per-session counters: reset at StartLogging, frozen once the writer is stopped.
        private long polls;
        private long stalePolls;
        private long sameFramePolls;
        private long failedPolls;
        private long rows;
        private long keptOnGrid;
        private long skippedOffGrid;
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

                if (!bodyTrackingStarted)
                {
                    bodyTrackingStarted = OVRPlugin.StartBodyTracking2(JOINT_SET);
                    if (!bodyTrackingStarted)
                    {
                        Debug.LogError($"[{Constants.LOG_TAG}] BodyTrackingLogger - Failed to start body tracking");
                        return;
                    }
                    OVRPlugin.RequestBodyTrackingFidelity(OVRPlugin.BodyTrackingFidelity2.High);
                    Debug.Log($"[{Constants.LOG_TAG}] BodyTrackingLogger - Body tracking started (FullBody, High fidelity)");
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

        /// <summary>
        /// Body sampling counters of the current/last session as a JSON object.
        /// rows - kept_on_grid = rows written off the grid (unlocked mode or off-grid samples).
        /// </summary>
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
                + $"\"kept_on_grid\":{keptOnGrid.ToString(inv)},"
                + $"\"skipped_off_grid\":{skippedOffGrid.ToString(inv)},"
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
            keptOnGrid = 0;
            skippedOffGrid = 0;
            fidelityGranted = 0;

            hasGridAnchor = false;
            gridAnchorTime = 0;
            gridAnchorIndex = 0;
            lastKeptFrameIndex = -1;
            offGridWarned = false;
            lastPollFrame = -1;

            displayHz = OVRPlugin.systemDisplayFrequency;
            keepEveryN = Math.Max(1, keepEveryNthDisplayFrame);
            gridLocked = false;
            if (displayFrameLocked)
            {
                if (float.IsNaN(displayHz) || float.IsInfinity(displayHz) || displayHz <= 0f)
                {
                    Debug.LogWarning($"[{Constants.LOG_TAG}] BodyTrackingLogger - Display frequency unavailable ({displayHz}); falling back to unlocked FixedUpdate sampling");
                }
                else
                {
                    gridLocked = true;
                }
            }

            Debug.Log($"[{Constants.LOG_TAG}] BodyTrackingLogger - Sampling: " +
                (gridLocked ? $"display-frame-locked, every {keepEveryN} frame(s) at {displayHz} Hz" : "unlocked (FixedUpdate)"));
        }

        private void Start()
        {
            baseOvrTimeSec = OVRPlugin.GetTimeInSeconds();
            baseUnixTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            bodyState = new OVRPlugin.BodyState
            {
                JointLocations = new OVRPlugin.BodyJointLocation[FULL_BODY_JOINT_COUNT]
            };

            if (startLoggingOnStart)
            {
                StartLogging();
            }
        }

        // Once per rendered frame: the display-frame-locked path.
        private void Update()
        {
            if (gridLocked)
                Poll();
        }

        // Legacy path (lock disabled or display frequency unknown): 50 Hz physics step.
        private void FixedUpdate()
        {
            if (!gridLocked)
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

            bool onGrid = false;
            if (gridLocked && !ShouldKeepOnGrid(timestamp, out onGrid))
            {
                skippedOffGrid++;
                return;
            }

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
            if (onGrid)
                keptOnGrid++;
        }

        /// <summary>
        /// Display-frame grid decision for a new (strictly newer) sample. The frame index
        /// starts at 0 on the session's first sample and accumulates the rounded frame
        /// delta from the previous sample, so a nominal display frequency that is slightly
        /// off cannot drift the grid over a long session. Returns true to write the sample;
        /// <paramref name="onGrid"/> is true when it is written because it lands on a kept
        /// frame. Samples more than OFF_GRID_TOLERANCE_FRAMES from the grid are always
        /// written: a wrong grid assumption must never lose data.
        /// </summary>
        private bool ShouldKeepOnGrid(double timestamp, out bool onGrid)
        {
            onGrid = false;

            long frameIndex;
            bool offGrid = false;
            if (!hasGridAnchor)
            {
                frameIndex = 0;
                hasGridAnchor = true;
            }
            else
            {
                double frames = (timestamp - gridAnchorTime) * displayHz;
                double rounded = Math.Round(frames);
                offGrid = Math.Abs(frames - rounded) > OFF_GRID_TOLERANCE_FRAMES;
                frameIndex = gridAnchorIndex + (long)rounded;
            }
            gridAnchorTime = timestamp;
            gridAnchorIndex = frameIndex;

            if (offGrid)
            {
                if (!offGridWarned)
                {
                    offGridWarned = true;
                    Debug.LogWarning($"[{Constants.LOG_TAG}] BodyTrackingLogger - Body sample off the {displayHz} Hz display grid (t={timestamp:F6}s); off-grid samples are kept (rows - kept_on_grid in stats)");
                }
                lastKeptFrameIndex = frameIndex;
                return true;
            }

            if (frameIndex == lastKeptFrameIndex)
                return false;

            // Minimum spacing, not a fixed phase: a parity rule would drop every sample of
            // a runtime that delivers on alternate phases (e.g. every 3rd frame -> 15 Hz).
            if (lastKeptFrameIndex >= 0 && frameIndex - lastKeptFrameIndex < keepEveryN)
                return false;

            lastKeptFrameIndex = frameIndex;
            onGrid = true;
            return true;
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
