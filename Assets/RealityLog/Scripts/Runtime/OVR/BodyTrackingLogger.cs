#nullable enable

using System;
using System.Collections.Generic;
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
    /// </summary>
    public class BodyTrackingLogger : MonoBehaviour
    {
        private static readonly OVRPlugin.BodyJointSet JOINT_SET = OVRPlugin.BodyJointSet.FullBody;
        private const int FULL_BODY_JOINT_COUNT = 84;
        // 7 values per joint (pos xyz + rot xyzw)
        private const int VALUES_PER_JOINT = 7;

        [SerializeField] private string fileName = "body_tracking.csv";
        [SerializeField] private string directoryName = "";
        [SerializeField] private bool startLoggingOnStart = false;

        private CsvWriter? writer = null;
        private OVRPlugin.BodyState bodyState;
        private bool bodyTrackingStarted = false;

        private double baseOvrTimeSec;
        private long baseUnixTimeMs;
        private double latestTimestamp;

        public string DirectoryName
        {
            get => directoryName;
            set => directoryName = value;
        }

        public void StartLogging()
        {
            try
            {
                StopLogging();

                baseOvrTimeSec = OVRPlugin.GetTimeInSeconds();
                baseUnixTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                latestTimestamp = 0;

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
            writer = null;
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

        private void FixedUpdate()
        {
            if (writer == null)
                return;

            if (!OVRPlugin.GetBodyState4(OVRPlugin.Step.Render, JOINT_SET, ref bodyState))
                return;

            var timestamp = bodyState.Time;
            if (timestamp <= latestTimestamp)
                return;

            latestTimestamp = timestamp;

            var joints = bodyState.JointLocations;
            if (joints == null || joints.Length == 0)
                return;

            int jointCount = Mathf.Min(joints.Length, FULL_BODY_JOINT_COUNT);

            // Build row: unix_time, ovr_timestamp, confidence, calibration_status, fidelity, then per-joint data
            var row = new double[5 + jointCount * VALUES_PER_JOINT];
            row[0] = ConvertOvrSecToUnixTimeMs(timestamp);
            row[1] = timestamp;
            row[2] = bodyState.Confidence;
            row[3] = (double)bodyState.CalibrationStatus;
            row[4] = (double)bodyState.Fidelity;

            int offset = 5;
            for (int i = 0; i < jointCount; i++)
            {
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
            }

            writer.EnqueueRow(row);
        }

        private string[] BuildHeader()
        {
            var header = new List<string>
            {
                "unix_time", "ovr_timestamp", "confidence", "calibration_status", "fidelity"
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

            return header.ToArray();
        }

        private static string GetJointName(int index)
        {
            if (Enum.IsDefined(typeof(OVRPlugin.BoneId), index))
            {
                return ((OVRPlugin.BoneId)index).ToString();
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
