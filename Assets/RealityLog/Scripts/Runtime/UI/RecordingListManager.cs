# nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using RealityLog.Common;

namespace RealityLog.UI
{
    /// <summary>
    /// Manages scanning and listing recording directories from the file system.
    /// </summary>
    public class RecordingListManager : MonoBehaviour
    {
        [Header("Settings")]
        [Tooltip("Base directory name pattern for recordings (e.g., YYYYMMDD_HHMMSS)")]
        [SerializeField] private string recordingDirectoryPattern = "\\d{8}_\\d{6}";

        private List<RecordingInfo> recordings = new List<RecordingInfo>();

        /// <summary>
        /// Information about a recording session.
        /// </summary>
        [Serializable]
        public class RecordingInfo
        {
            public string DirectoryName { get; set; } = string.Empty;
            public string FullPath { get; set; } = string.Empty;
            public DateTime CreationTime { get; set; }
            public long SizeBytes { get; set; }
            public HealthLevel QuickHealth { get; set; } = HealthLevel.Good;
            public double DurationSeconds { get; set; } = -1;
            public int EpisodeMarkerCount { get; set; } = 0;
            public string FormattedSize => FormatBytes(SizeBytes);
            public string FormattedDate => CreationTime.ToString("yyyy-MM-dd HH:mm:ss");

            public string FriendlyTitle
            {
                get
                {
                    if (DirectoryName.Length >= 15 &&
                        int.TryParse(DirectoryName.Substring(0, 4), out int year) &&
                        int.TryParse(DirectoryName.Substring(4, 2), out int month) &&
                        int.TryParse(DirectoryName.Substring(6, 2), out int day) &&
                        int.TryParse(DirectoryName.Substring(9, 2), out int hour) &&
                        int.TryParse(DirectoryName.Substring(11, 2), out int minute))
                    {
                        var dt = new DateTime(year, month, day, hour, minute, 0);
                        var today = DateTime.Today;

                        if (dt.Date == today)
                            return $"Today {dt:h:mm tt}";
                        if (dt.Date == today.AddDays(-1))
                            return $"Yesterday {dt:h:mm tt}";
                        if (dt.Year == today.Year)
                            return dt.ToString("MMM d, h:mm tt");
                        return dt.ToString("MMM d yyyy, h:mm tt");
                    }

                    return DirectoryName;
                }
            }

            public string FormattedDuration
            {
                get
                {
                    if (DurationSeconds < 0) return "--:--";
                    int totalSec = (int)DurationSeconds;
                    int min = totalSec / 60;
                    int sec = totalSec % 60;
                    return $"{min:D2}:{sec:D2}";
                }
            }

            private static string FormatBytes(long bytes)
            {
                string[] sizes = { "B", "KB", "MB", "GB" };
                double len = bytes;
                int order = 0;
                while (len >= 1024 && order < sizes.Length - 1)
                {
                    order++;
                    len = len / 1024;
                }
                return $"{len:0.##} {sizes[order]}";
            }
        }

        /// <summary>
        /// Event fired when the recording list is updated.
        /// </summary>
        public event Action<List<RecordingInfo>>? OnRecordingsUpdated;

        /// <summary>
        /// Gets the current list of recordings.
        /// </summary>
        public List<RecordingInfo> Recordings => new List<RecordingInfo>(recordings);

        private readonly RecordingListRefreshGate gate = new RecordingListRefreshGate();
        private readonly ConcurrentQueue<List<RecordingInfo>> scanned = new ConcurrentQueue<List<RecordingInfo>>();
        private RecordingManager? recordingManager;

        /// <summary>
        /// True while an episode records or stops. The list neither scans nor rebuilds then: the
        /// pose loggers sample on the main thread, and a stalled main thread leaves the cameras
        /// without head-pose coverage, which the seal refuses.
        /// </summary>
        public bool RecordingActive
        {
            get
            {
                if (recordingManager == null) recordingManager = FindFirstObjectByType<RecordingManager>();
                return recordingManager != null && recordingManager.State != RecordingState.Idle;
            }
        }

        /// <summary>
        /// Asks for the recordings to be scanned again. The scan runs off the main thread and its
        /// result reaches <see cref="OnRecordingsUpdated"/> on the main thread. Asked while
        /// recording, it waits for the recording to end; asked while a scan runs, it is one more scan.
        /// </summary>
        public void RefreshRecordings()
        {
            if (gate.Request(RecordingActive))
            {
                StartScan();
            }
            else if (gate.Deferred)
            {
                Debug.Log($"[{Constants.LOG_TAG}] RecordingListManager: refresh deferred until the recording ends");
            }
        }

        private void StartScan()
        {
            string dataPath = Application.persistentDataPath; // main thread only
            string pattern = recordingDirectoryPattern;
            System.Threading.Tasks.Task.Run(() =>
            {
                List<RecordingInfo> found;
                try
                {
                    found = Scan(dataPath, pattern);
                }
                catch (Exception e)
                {
                    Debug.LogError($"[{Constants.LOG_TAG}] RecordingListManager: Error scanning recordings: {e.Message}");
                    found = new List<RecordingInfo>();
                }
                scanned.Enqueue(found);
            });
        }

        private void Update()
        {
            bool recording = RecordingActive;
            while (scanned.TryDequeue(out var found))
            {
                var (apply, scanAgain) = gate.Completed(recording);
                if (apply)
                {
                    recordings = found;
                    Debug.Log($"[{Constants.LOG_TAG}] RecordingListManager: Found {recordings.Count} recordings");
                    OnRecordingsUpdated?.Invoke(recordings);
                }
                if (scanAgain)
                {
                    StartScan();
                }
            }
            if (gate.Tick(recording))
            {
                StartScan();
            }
        }

        /// <summary>The recording directories under <paramref name="dataPath"/>, newest first (any thread).</summary>
        private List<RecordingInfo> Scan(string dataPath, string pattern)
        {
            var result = new List<RecordingInfo>();
            if (!Directory.Exists(dataPath))
            {
                Debug.LogWarning($"[{Constants.LOG_TAG}] RecordingListManager: Data path does not exist: {dataPath}");
                return result;
            }

            foreach (var dirPath in Directory.GetDirectories(dataPath))
            {
                string dirName = Path.GetFileName(dirPath);

                // Check if directory name matches recording pattern (YYYYMMDD_HHMMSS)
                if (!System.Text.RegularExpressions.Regex.IsMatch(dirName, pattern))
                    continue;
                try
                {
                    var info = new DirectoryInfo(dirPath);
                    result.Add(new RecordingInfo
                    {
                        DirectoryName = dirName,
                        FullPath = dirPath,
                        CreationTime = info.CreationTime,
                        SizeBytes = CalculateDirectorySize(dirPath),
                        QuickHealth = QuickHealthCheck(dirPath),
                        DurationSeconds = QuickParseDuration(dirPath),
                        EpisodeMarkerCount = QuickParseEpisodeMarkerCount(dirPath)
                    });
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[{Constants.LOG_TAG}] RecordingListManager: Error reading directory {dirName}: {e.Message}");
                }
            }

            // Sort by creation time, newest first
            return result.OrderByDescending(r => r.CreationTime).ToList();
        }

        /// <summary>
        /// Calculates the total size of a directory in bytes.
        /// </summary>
        private long CalculateDirectorySize(string directoryPath)
        {
            long size = 0;
            try
            {
                var files = Directory.GetFiles(directoryPath, "*", SearchOption.AllDirectories);
                foreach (var file in files)
                {
                    try
                    {
                        var fileInfo = new FileInfo(file);
                        size += fileInfo.Length;
                    }
                    catch
                    {
                        // Skip files we can't access
                    }
                }
            }
            catch
            {
                // Return 0 if we can't calculate size
            }
            return size;
        }

        [Serializable]
        private class VideoMetadataQuick
        {
            public long recording_start_unix_ms;
            public long recording_stop_unix_ms;
        }

        private static HealthLevel QuickHealthCheck(string dirPath)
        {
            // Check video exists and has reasonable size
            string videoPath = Path.Combine(dirPath, "left_camera.mp4");
            bool videoOk = File.Exists(videoPath);
            long videoSize = 0;
            if (videoOk)
            {
                try { videoSize = new FileInfo(videoPath).Length; } catch { }
            }

            if (!videoOk || videoSize < 1024)
                return HealthLevel.Error;

            // Check for any motion data
            bool hasMotion = false;
            string[] motionFiles = { "hmd_poses.csv", "imu.csv", "left_controller_poses.csv", "right_controller_poses.csv" };
            foreach (var f in motionFiles)
            {
                string p = Path.Combine(dirPath, f);
                if (File.Exists(p))
                {
                    try
                    {
                        if (new FileInfo(p).Length > 0)
                        {
                            hasMotion = true;
                            break;
                        }
                    }
                    catch { }
                }
            }

            if (!hasMotion)
                return HealthLevel.Error;

            if (videoSize < 51200)
                return HealthLevel.Warning;

            return HealthLevel.Good;
        }

        private static double QuickParseDuration(string dirPath)
        {
            string metadataPath = Path.Combine(dirPath, "left_camera_metadata.json");
            if (!File.Exists(metadataPath))
                return -1;

            try
            {
                string json = File.ReadAllText(metadataPath);
                var metadata = JsonUtility.FromJson<VideoMetadataQuick>(json);
                if (metadata != null && metadata.recording_stop_unix_ms > metadata.recording_start_unix_ms)
                {
                    return (metadata.recording_stop_unix_ms - metadata.recording_start_unix_ms) / 1000.0;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[{Constants.LOG_TAG}] RecordingListManager: Failed to parse duration from {metadataPath}: {e.Message}");
            }

            return -1;
        }

        [Serializable]
        private class EpisodeMarkersQuick
        {
            public int marker_count;
        }

        private static int QuickParseEpisodeMarkerCount(string dirPath)
        {
            string markersPath = Path.Combine(dirPath, "episode_markers.json");
            if (!File.Exists(markersPath))
                return 0;

            try
            {
                string json = File.ReadAllText(markersPath);
                var data = JsonUtility.FromJson<EpisodeMarkersQuick>(json);
                return data?.marker_count ?? 0;
            }
            catch
            {
                return 0;
            }
        }

        private void Start()
        {
            RefreshRecordings();
        }
    }
}
