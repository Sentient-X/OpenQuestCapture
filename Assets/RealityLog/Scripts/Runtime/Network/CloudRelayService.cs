# nullable enable

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using UnityEngine;
using UnityEngine.Networking;
using RealityLog.Common;
using RealityLog.UI;

namespace RealityLog.Network
{
    /// <summary>
    /// Polls Cloudflare for commands and executes them via RecordingManager.
    /// Sends heartbeat with device status every 1.5 seconds.
    /// Works alongside the existing local HTTP server as a cloud-based control channel.
    /// </summary>
    public class CloudRelayService : MonoBehaviour
    {
        private const string API_BASE = "https://fielddata-pro-api.sarthak-46e.workers.dev";
        private const string API_KEY = "fielddata-pro-2024";
        private const float POLL_INTERVAL = 1.5f;

        private string deviceId = "";
        private string deviceLabel = "";
        private RecordingManager? recordingManager;
        private HttpServerController? httpController;
        private int assignedDeviceNumber = 0;
        private TextMesh? deviceNumberLabel;

        // Recording state tracked for response metadata (mirrors HttpServerController pattern)
        private string? currentRecordingFile;
        private string? currentRecordingStartedAt;

        // Cached status (read from HttpServerController or computed directly)
        private volatile int cachedBattery = 100;
        private long cachedStorageFree = long.MaxValue;
        private long cachedStorageTotal = 0;
        private string cachedAppVersion = "1.0.0";
        private float lastStorageRefresh = 0f;

        private bool isRunning = false;

        private void Awake()
        {
            deviceId = SystemInfo.deviceUniqueIdentifier;
            deviceLabel = SystemInfo.deviceName;
            cachedAppVersion = Application.version;
        }

        private IEnumerator Start()
        {
            // Wait for other systems to initialize
            yield return new WaitForSeconds(2f);

            recordingManager = FindFirstObjectByType<RecordingManager>();
            httpController = FindFirstObjectByType<HttpServerController>();

            if (recordingManager == null)
            {
                Debug.LogError($"[{Constants.LOG_TAG}] CloudRelay: RecordingManager not found!");
                yield break;
            }

            isRunning = true;
            Debug.Log($"[{Constants.LOG_TAG}] CloudRelay: Started — deviceId={deviceId}, label={deviceLabel}");
            StartCoroutine(HeartbeatLoop());
        }

        private void Update()
        {
            // Refresh cached battery every frame (cheap)
            cachedBattery = SystemInfo.batteryLevel >= 0 ? (int)(SystemInfo.batteryLevel * 100) : 100;

            // Refresh storage every 30s (expensive JNI)
            if (Time.realtimeSinceStartup - lastStorageRefresh > 30f)
            {
                cachedStorageFree = GetStorageFreeBytes();
                cachedStorageTotal = GetStorageTotalBytes();
                cachedAppVersion = Application.version;
                lastStorageRefresh = Time.realtimeSinceStartup;
            }
        }

        private void OnDestroy()
        {
            isRunning = false;
        }

        // ── Heartbeat Loop ──

        private IEnumerator HeartbeatLoop()
        {
            while (isRunning)
            {
                yield return SendHeartbeat();
                yield return new WaitForSeconds(POLL_INTERVAL);
            }
        }

        private IEnumerator SendHeartbeat()
        {
            bool isRecording = recordingManager != null && recordingManager.IsRecording;
            int durationMs = isRecording && recordingManager != null
                ? (int)(recordingManager.RecordingDuration * 1000)
                : 0;

            // Build IP address hint
            string ipAddress = "";
            try
            {
                var hostName = System.Net.Dns.GetHostName();
                foreach (var addr in System.Net.Dns.GetHostAddresses(hostName))
                {
                    if (addr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    {
                        ipAddress = addr.ToString();
                        break;
                    }
                }
            }
            catch { }

            var heartbeat = JsonUtility.ToJson(new HeartbeatPayload
            {
                deviceId = deviceId,
                label = deviceLabel,
                batteryPercent = cachedBattery,
                storageFreeBytes = cachedStorageFree,
                storageTotalBytes = cachedStorageTotal,
                isRecording = isRecording,
                recordingDurationMs = durationMs,
                recordingFile = isRecording ? (currentRecordingFile ?? "") : "",
                apkVersion = cachedAppVersion,
                ipAddress = ipAddress,
            });

            var request = new UnityWebRequest($"{API_BASE}/api/v1/relay/devices/heartbeat", "POST");
            request.SetRequestHeader("Content-Type", "application/json");
            request.SetRequestHeader("X-API-Key", API_KEY);
            request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(heartbeat));
            request.downloadHandler = new DownloadHandlerBuffer();
            request.timeout = 10;

            yield return request.SendWebRequest();

            if (request.result != UnityWebRequest.Result.Success)
            {
                // Silent failure — will retry next heartbeat
                Debug.LogWarning($"[{Constants.LOG_TAG}] CloudRelay: Heartbeat failed: {request.error}");
                request.Dispose();
                yield break;
            }

            // Parse response for pending commands
            var responseText = request.downloadHandler.text;
            request.Dispose();

            HeartbeatResponse? response = null;
            try
            {
                response = JsonUtility.FromJson<HeartbeatResponse>(responseText);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[{Constants.LOG_TAG}] CloudRelay: Failed to parse heartbeat response: {ex.Message}");
                yield break;
            }

            // Update device number display
            if (response != null && response.deviceNumber > 0 && response.deviceNumber != assignedDeviceNumber)
            {
                assignedDeviceNumber = response.deviceNumber;
                Debug.Log($"[{Constants.LOG_TAG}] CloudRelay: Assigned device number VR-{assignedDeviceNumber}");
                UpdateDeviceNumberLabel();
            }

            if (response?.commands == null || response.commands.Length == 0)
                yield break;

            // Execute each command
            foreach (var cmd in response.commands)
            {
                yield return ExecuteCommand(cmd);
            }
        }

        // ── Command Execution ──

        private IEnumerator ExecuteCommand(RelayCommand cmd)
        {
            Debug.Log($"[{Constants.LOG_TAG}] CloudRelay: Executing command {cmd.id} type={cmd.type}");

            string resultStatus = "completed";
            string resultJson = "{}";

            try
            {
                switch (cmd.type)
                {
                    case "start-recording":
                        resultJson = ExecuteStartRecording(cmd.payload);
                        break;

                    case "stop-recording":
                        resultJson = ExecuteStopRecording();
                        break;

                    case "status":
                        resultJson = BuildStatusJson();
                        break;

                    case "mark-episode":
                        resultJson = ExecuteMarkEpisode(cmd.payload);
                        break;

                    case "keep-awake":
                        resultJson = "{\"status\": \"applied\"}";
                        // Keep-awake is already handled by KeepAwakeBootstrap
                        break;

                    default:
                        resultStatus = "failed";
                        resultJson = $"{{\"error\": \"unknown_command\", \"message\": \"Unknown command type: {EscapeJson(cmd.type)}\"}}";
                        break;
                }
            }
            catch (Exception ex)
            {
                resultStatus = "failed";
                resultJson = $"{{\"error\": \"execution_error\", \"message\": \"{EscapeJson(ex.Message)}\"}}";
                Debug.LogError($"[{Constants.LOG_TAG}] CloudRelay: Command {cmd.id} failed: {ex}");
            }

            // Report result back to Cloudflare
            yield return ReportResult(cmd.id, resultStatus, resultJson);
        }

        private string ExecuteStartRecording(string? payload)
        {
            if (recordingManager == null)
                throw new InvalidOperationException("RecordingManager not available");

            if (recordingManager.IsRecording)
            {
                return $"{{\"error\": \"already_recording\", \"message\": \"Recording is already in progress\", \"currentFile\": \"{EscapeJson(currentRecordingFile ?? "unknown")}\"}}";
            }

            // Check storage
            long freeBytes = GetStorageFreeBytes();
            if (freeBytes > 0 && freeBytes < 524_288_000) // 500 MB minimum
            {
                return $"{{\"error\": \"storage_full\", \"storageFreeBytes\": {freeBytes}, \"storageRequiredBytes\": 524288000}}";
            }

            recordingManager.StartRecording();

            currentRecordingFile = recordingManager.CurrentSessionDirectory;
            currentRecordingStartedAt = DateTimeOffset.UtcNow.ToString("O");

            // Save sidecar metadata if payload provided
            if (!string.IsNullOrEmpty(payload) && currentRecordingFile != null)
            {
                try
                {
                    var dirPath = Path.Combine(Application.persistentDataPath, currentRecordingFile);
                    Directory.CreateDirectory(dirPath);
                    var metaPath = Path.Combine(dirPath, "fielddata_metadata.json");
                    File.WriteAllText(metaPath, payload);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[{Constants.LOG_TAG}] CloudRelay: Failed to save sidecar metadata: {ex.Message}");
                }
            }

            return $"{{\"status\": \"recording\", \"file\": \"{EscapeJson(currentRecordingFile ?? "")}\", \"startedAt\": \"{EscapeJson(currentRecordingStartedAt)}\"}}";
        }

        private string ExecuteStopRecording()
        {
            if (recordingManager == null)
                throw new InvalidOperationException("RecordingManager not available");

            if (!recordingManager.IsRecording)
            {
                return "{\"error\": \"not_recording\", \"message\": \"No recording in progress\"}";
            }

            long durationMs = (long)(recordingManager.RecordingDuration * 1000);
            string? stoppedFile = currentRecordingFile;
            string? startedAt = currentRecordingStartedAt;

            recordingManager.StopRecording();

            // Clear state
            currentRecordingFile = null;
            currentRecordingStartedAt = null;

            // Return immediately — don't block the main thread waiting for
            // file finalization. File size is non-critical metadata and the
            // Thread.Sleep loop was freezing the heartbeat/command pipeline.
            var stoppedAt = DateTimeOffset.UtcNow.ToString("O");
            return $"{{\"status\": \"stopped\", \"file\": \"{EscapeJson(stoppedFile ?? "")}\", " +
                   $"\"durationMs\": {durationMs}, \"fileSizeBytes\": 0, " +
                   $"\"startedAt\": \"{EscapeJson(startedAt ?? "")}\", \"stoppedAt\": \"{EscapeJson(stoppedAt)}\"}}";
        }

        private string BuildStatusJson()
        {
            bool isRecording = recordingManager != null && recordingManager.IsRecording;
            int durationMs = isRecording && recordingManager != null
                ? (int)(recordingManager.RecordingDuration * 1000) : 0;

            return $"{{\"device\": \"MetaQuest3\", \"batteryPercent\": {cachedBattery}, " +
                   $"\"storageFreeBytes\": {cachedStorageFree}, \"storageTotalBytes\": {cachedStorageTotal}, " +
                   $"\"recording\": {{\"active\": {(isRecording ? "true" : "false")}, " +
                   $"\"currentFile\": {(currentRecordingFile != null ? $"\"{EscapeJson(currentRecordingFile)}\"" : "null")}, " +
                   $"\"durationMs\": {durationMs}, \"fileSizeBytes\": 0}}, " +
                   $"\"apkVersion\": \"{EscapeJson(cachedAppVersion)}\"}}";
        }

        private string ExecuteMarkEpisode(string? payload)
        {
            int episodeNumber = 1;
            if (!string.IsNullOrEmpty(payload))
            {
                try
                {
                    var data = JsonUtility.FromJson<MarkEpisodePayload>(payload);
                    if (data.episodeNumber > 0) episodeNumber = data.episodeNumber;
                }
                catch { }
            }

            var marker = FindFirstObjectByType<EpisodeMarkerController>();
            if (marker != null)
            {
                marker.MarkFromPhone(episodeNumber);
                return $"{{\"status\": \"marked\", \"episodeNumber\": {episodeNumber}}}";
            }

            return "{\"error\": \"marker_not_found\", \"message\": \"EpisodeMarkerController not available\"}";
        }

        // ── Report Result ──

        private IEnumerator ReportResult(string commandId, string status, string resultJson)
        {
            var body = $"{{\"status\": \"{status}\", \"result\": {resultJson}}}";

            var request = new UnityWebRequest($"{API_BASE}/api/v1/relay/commands/{commandId}/result", "POST");
            request.SetRequestHeader("Content-Type", "application/json");
            request.SetRequestHeader("X-API-Key", API_KEY);
            request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
            request.downloadHandler = new DownloadHandlerBuffer();
            request.timeout = 10;

            yield return request.SendWebRequest();

            if (request.result != UnityWebRequest.Result.Success)
            {
                Debug.LogWarning($"[{Constants.LOG_TAG}] CloudRelay: Failed to report result for {commandId}: {request.error}");
            }
            else
            {
                Debug.Log($"[{Constants.LOG_TAG}] CloudRelay: Reported {status} for command {commandId}");
            }

            request.Dispose();
        }

        // ── Platform Helpers ──

        private long GetStorageFreeBytes()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using var statFs = new AndroidJavaObject("android.os.StatFs", Application.persistentDataPath);
                long blockSize = statFs.Call<long>("getBlockSizeLong");
                long availBlocks = statFs.Call<long>("getAvailableBlocksLong");
                long result = blockSize * availBlocks;
                if (result > 0) return result;
            }
            catch { }
#endif
            return long.MaxValue;
        }

        private long GetStorageTotalBytes()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using var statFs = new AndroidJavaObject("android.os.StatFs", Application.persistentDataPath);
                long blockSize = statFs.Call<long>("getBlockSizeLong");
                long totalBlocks = statFs.Call<long>("getBlockCountLong");
                long result = blockSize * totalBlocks;
                if (result > 0) return result;
            }
            catch { }
#endif
            return 0;
        }

        private static string EscapeJson(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");
        }

        // ── Device number HUD ──

        private void UpdateDeviceNumberLabel()
        {
            if (deviceNumberLabel == null)
            {
                var go = new GameObject("DeviceNumberHUD");
                deviceNumberLabel = go.AddComponent<TextMesh>();
                deviceNumberLabel.characterSize = 0.005f;
                deviceNumberLabel.fontSize = 100;
                deviceNumberLabel.anchor = TextAnchor.MiddleCenter;
                deviceNumberLabel.alignment = TextAlignment.Center;
                deviceNumberLabel.color = new Color(1f, 1f, 1f, 0.35f);

                StartCoroutine(FollowCamera(go.transform));
            }

            deviceNumberLabel.text = $"VR-{assignedDeviceNumber}";
        }

        private IEnumerator FollowCamera(Transform hudTransform)
        {
            while (hudTransform != null)
            {
                var cam = UnityEngine.Camera.main;
                if (cam != null)
                {
                    var pos = cam.transform.position + cam.transform.forward * 1.5f
                              - cam.transform.up * 0.55f - cam.transform.right * 0.45f;
                    hudTransform.position = Vector3.Lerp(hudTransform.position, pos, Time.deltaTime * 3f);
                    hudTransform.rotation = Quaternion.LookRotation(hudTransform.position - cam.transform.position);
                }
                yield return null;
            }
        }

        // ── JSON Serialization Types ──

        [Serializable]
        private class HeartbeatPayload
        {
            public string deviceId = "";
            public string label = "";
            public int batteryPercent;
            public long storageFreeBytes;
            public long storageTotalBytes;
            public bool isRecording;
            public int recordingDurationMs;
            public string recordingFile = "";
            public string apkVersion = "";
            public string ipAddress = "";
        }

        [Serializable]
        private class HeartbeatResponse
        {
            public bool ok;
            public RelayCommand[]? commands;
            public int deviceNumber;
        }

        [Serializable]
        private class RelayCommand
        {
            public string id = "";
            public string type = "";
            public string? payload;
        }

        [Serializable]
        private class MarkEpisodePayload
        {
            public int episodeNumber;
        }
    }
}
