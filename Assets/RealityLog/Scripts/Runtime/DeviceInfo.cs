# nullable enable

using System;
using System.IO;
using UnityEngine;
using RealityLog.Common;

namespace RealityLog
{
    /// <summary>
    /// Captures and writes device identity information for a recording session.
    /// Written as device_info.json in each session directory.
    /// </summary>
    public static class DeviceInfo
    {
        private const string FileName = "device_info.json";

        /// <summary>
        /// Writes device_info.json to the given session directory.
        /// Should be called once at the start of each recording session.
        /// </summary>
        public static void WriteToSession(string sessionDirPath)
        {
            try
            {
                Directory.CreateDirectory(sessionDirPath);

                var headsetType = GetHeadsetType();
                var deviceSerial = GetDeviceSerial();
                var json = $"{{\n" +
                    $"  \"device_unique_id\": \"{EscapeJson(SystemInfo.deviceUniqueIdentifier)}\",\n" +
                    $"  \"device_name\": \"{EscapeJson(SystemInfo.deviceName)}\",\n" +
                    $"  \"device_model\": \"{EscapeJson(SystemInfo.deviceModel)}\",\n" +
                    $"  \"device_serial\": \"{EscapeJson(deviceSerial)}\",\n" +
                    $"  \"headset_type\": \"{EscapeJson(headsetType)}\",\n" +
                    $"  \"os\": \"{EscapeJson(SystemInfo.operatingSystem)}\",\n" +
                    $"  \"gpu\": \"{EscapeJson(SystemInfo.graphicsDeviceName)}\",\n" +
                    $"  \"app_version\": \"{EscapeJson(Application.version)}\",\n" +
                    $"  \"unity_version\": \"{EscapeJson(Application.unityVersion)}\",\n" +
                    $"  \"captured_at_utc\": \"{DateTime.UtcNow:O}\"\n" +
                    $"}}";

                var path = Path.Combine(sessionDirPath, FileName);
                File.WriteAllText(path, json);
                Debug.Log($"[{Constants.LOG_TAG}] DeviceInfo: Wrote {path}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[{Constants.LOG_TAG}] DeviceInfo: Failed to write: {ex.Message}");
            }
        }

        private static string GetHeadsetType()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                return OVRPlugin.GetSystemHeadsetType().ToString();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[{Constants.LOG_TAG}] DeviceInfo: OVRPlugin.GetSystemHeadsetType() failed: {ex.Message}");
            }
#endif
            return "Unknown";
        }

        private static string GetDeviceSerial()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using var buildClass = new AndroidJavaClass("android.os.Build");
                return buildClass.GetStatic<string>("SERIAL") ?? "unavailable";
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[{Constants.LOG_TAG}] DeviceInfo: Build.SERIAL failed: {ex.Message}");
            }
#endif
            return "editor";
        }

        private static string EscapeJson(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            return value
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\n", "\\n")
                .Replace("\r", "\\r")
                .Replace("\t", "\\t");
        }
    }
}
