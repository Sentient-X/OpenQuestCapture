# nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using RealityLog.Common;

namespace RealityLog.Rig
{
    /// <summary>Why a start was refused for the rig: a closed code, a sentence, the rig's JSON.</summary>
    public sealed class RigRefusal
    {
        /// <summary>
        /// One of <c>rig_library_missing</c>, <c>rig_open_failed</c>, <c>rig_usb_permission</c>,
        /// <c>rig_check_failed</c>, <c>rig_start_failed</c>.
        /// </summary>
        public string Code { get; }
        public string Message { get; }
        /// <summary>A JSON value (the library's error object, or null).</summary>
        public string DetailJson { get; }

        public RigRefusal(string code, string message, string? detailJson = null)
        {
            Code = code;
            Message = message;
            DetailJson = string.IsNullOrEmpty(detailJson) ? "null" : detailJson!;
        }

        public string ToJson() =>
            "{\"code\":\"" + RigJson.Escape(Code) + "\",\"message\":\"" + RigJson.Escape(Message) +
            "\",\"detail\":" + DetailJson + "}";
    }

    /// <summary>
    /// A recording start refused because the rig is expected and not ready. An
    /// <see cref="InvalidOperationException"/>, so every start path that reports a refusal's
    /// message keeps doing so; the HTTP API answers it with a typed 503.
    /// </summary>
    public sealed class RigRefusedException : InvalidOperationException
    {
        public RigRefusal Refusal { get; }

        public RigRefusedException(RigRefusal refusal)
            : base($"RecordingManager: refusing to start — {refusal.Code}: {refusal.Message}")
        {
            Refusal = refusal;
        }
    }

    /// <summary>
    /// Owns the wrist cameras and gripper encoders through libsx_rig.so (<see cref="SxRig"/>)
    /// and the Kotlin <c>RigUsbBridge</c> that opens their USB devices.
    ///
    /// <para><b>Expected.</b> The rig is expected when <c>rig_config.json</c> is in the app's
    /// persistent data directory, or any rig USB device (32e4:9230, 303a:*) is on the bus. Only
    /// then is the library loaded, opened or called; otherwise every method here is a no-op and
    /// recording behaves exactly as it did before the rig existed (the laptop-pod flow).</para>
    ///
    /// <para><b>USB.</b> Every 0.5 s the bridge re-enumerates; a newly opened device's fd is
    /// attached to the rig, and a vanished one is detached from the rig before the bridge
    /// closes its connection (the ABI leaves fd ownership with Kotlin until the detach).</para>
    ///
    /// <para><b>Episode.</b> <see cref="CheckBeforeStart"/> runs <c>sx_rig_check</c> and returns
    /// a refusal; <see cref="StartEpisode"/> passes CLOCK_MONOTONIC and UTC anchors read back to
    /// back; <see cref="StopEpisode"/> returns the stop receipt.</para>
    ///
    /// Main thread only, except <see cref="StatusJson"/>, which any thread may read.
    /// </summary>
    public class RigCoordinator : MonoBehaviour
    {
        public const string ConfigFileName = "rig_config.json";
        public const string StagingDirectoryName = "rig_staging";
        private const string BridgeClassName = "com.samusynth.questcamera.usb.RigUsbBridge";
        private const float PollIntervalSeconds = 0.5f;
        private const float OpenRetrySeconds = 5f;

        public static RigCoordinator? Instance { get; private set; }

        [Serializable]
        private sealed class UsbEntry
        {
            public string key = "";
            public string deviceName = "";
            public int vendorId;
            public int productId;
            public int fd = -1;
            public string serial = "";
        }

        [Serializable]
        private sealed class UsbSnapshot
        {
            public UsbEntry[] devices = Array.Empty<UsbEntry>();
            public int present;
            public string[] awaitingPermission = Array.Empty<string>();
            public string[] pendingRelease = Array.Empty<string>();
            public string error = "";
        }

        private AndroidJavaObject? bridge;
        private string configPath = "";
        private string stagingPath = "";
        private UsbSnapshot snapshot = new();
        private readonly Dictionary<string, UsbEntry> attached = new();
        private readonly Dictionary<string, string> attachErrors = new();
        private bool configPresent;
        private bool opened;
        private bool libraryMissing;
        private string? openErrorJson;
        private float lastOpenAttempt = float.NegativeInfinity;
        private float lastPoll = float.NegativeInfinity;
        private bool episodeActive;
        private string? episodeReceiptJson;
        private RigRefusal? lastRefusal;
        private volatile string statusJson = NotExpectedStatus;

        private const string NotExpectedStatus =
            "{\"expected\":false,\"configured\":false,\"library\":\"unknown\",\"open\":false," +
            "\"openError\":null,\"episodeActive\":false,\"usb\":{\"present\":0,\"attached\":[],\"awaitingPermission\":[]," +
            "\"attachErrors\":{}},\"native\":null,\"lastRefusal\":null}";

        /// <summary>True when rig devices are configured or present; see the class remarks.</summary>
        public bool Expected => configPresent || snapshot.present > 0 || attached.Count > 0;

        /// <summary>The rig's /api/status object, cached on the main thread. Any thread.</summary>
        public string StatusJson => statusJson;

        /// <summary>The /api/status <c>rig</c> value when no coordinator exists.</summary>
        public static string StatusJsonOrDefault => Instance?.StatusJson ?? NotExpectedStatus;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (Instance is not null) return;
            var go = new GameObject(nameof(RigCoordinator));
            go.AddComponent<RigCoordinator>();
            DontDestroyOnLoad(go);
        }

        private void Awake()
        {
            if (Instance is { } other && !ReferenceEquals(other, this))
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            configPath = Path.Combine(Application.persistentDataPath, ConfigFileName);
            stagingPath = Path.Combine(Application.persistentDataPath, StagingDirectoryName);
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                using var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");
                bridge = new AndroidJavaObject(BridgeClassName, activity);
            }
            catch (Exception ex)
            {
                // Without the bridge no USB device can be opened; a rig_config.json still
                // makes the rig expected, so a configured headset refuses instead of
                // recording without its wrist streams.
                Debug.LogError($"[{Constants.LOG_TAG}] RigCoordinator: USB bridge unavailable: {ex.Message}");
                bridge = null;
            }
#endif
            Poll();
        }

        private void Update()
        {
            if (Time.unscaledTime - lastPoll >= PollIntervalSeconds)
            {
                Poll();
            }
        }

        /// <summary>
        /// The reason a start must be refused, or null to proceed (always null when the rig is
        /// not expected). Runs <c>sx_rig_check</c>, bounded 5 s by the ABI, on this thread.
        /// </summary>
        public RigRefusal? CheckBeforeStart()
        {
            Poll();
            var refusal = Refusal();
            lastRefusal = refusal;
            RefreshStatus();
            return refusal;
        }

        private RigRefusal? Refusal()
        {
            if (!Expected) return null;
            if (libraryMissing)
            {
                return new RigRefusal("rig_library_missing",
                    "rig devices are configured or plugged in, but this build has no libsx_rig.so",
                    openErrorJson);
            }
            if (!opened)
            {
                return new RigRefusal("rig_open_failed", "sx_rig_open failed", openErrorJson);
            }
            if (snapshot.awaitingPermission.Length > 0)
            {
                return new RigRefusal("rig_usb_permission",
                    "no USB permission for " + string.Join(", ", snapshot.awaitingPermission) +
                    "; accept the prompt in the headset once with \"always open\" checked");
            }
            try
            {
                SxRig.RunCheck();
                return null;
            }
            catch (SxRigException ex)
            {
                return new RigRefusal("rig_check_failed", ex.Message, ex.ErrorJson);
            }
        }

        /// <summary>
        /// Starts the rig's episode, if the rig is expected. Reads the two clock anchors back to
        /// back. Throws <see cref="RigRefusedException"/> (rig_start_failed) when the rig
        /// refuses; nothing else has started by then.
        /// </summary>
        public void StartEpisode(string sessionName, string sessionDirectory)
        {
            episodeReceiptJson = null;
            if (!Expected) return;
            var (mono, utc) = SxRig.ReadAnchors(MonotonicClock.Nanos);
            var episode = "{\"session\":\"" + RigJson.Escape(sessionName) +
                "\",\"session_dir\":\"" + RigJson.Escape(sessionDirectory) +
                "\",\"app_version\":\"" + RigJson.Escape(Application.version) +
                "\",\"monotonic_anchor_ns\":" + mono.ToString(CultureInfo.InvariantCulture) +
                ",\"utc_anchor_ns\":" + utc.ToString(CultureInfo.InvariantCulture) + "}";
            try
            {
                SxRig.Start(episode, mono, utc);
            }
            catch (SxRigException ex)
            {
                var refusal = new RigRefusal("rig_start_failed", ex.Message, ex.ErrorJson);
                lastRefusal = refusal;
                RefreshStatus();
                throw new RigRefusedException(refusal);
            }
            episodeActive = true;
            Debug.Log($"[{Constants.LOG_TAG}] RigCoordinator: episode '{sessionName}' started (mono={mono} utc={utc})");
            RefreshStatus();
        }

        /// <summary>
        /// Stops the rig's episode and returns its stop receipt (a JSON object; on failure
        /// <c>{"error": {...}}</c>), or null when no rig episode ran. Idempotent: a second call
        /// for the same episode returns the same receipt. Never throws.
        /// </summary>
        public string? StopEpisode()
        {
            if (!episodeActive) return episodeReceiptJson;
            try
            {
                episodeReceiptJson = SxRig.Stop();
            }
            catch (SxRigException ex)
            {
                Debug.LogError($"[{Constants.LOG_TAG}] RigCoordinator: {ex.Message}");
                episodeReceiptJson = "{\"error\":" + ex.ErrorJson + "}";
            }
            episodeActive = false;
            RefreshStatus();
            return episodeReceiptJson;
        }

        private void Poll()
        {
            lastPoll = Time.unscaledTime;
            configPresent = File.Exists(configPath);
            if (bridge != null)
            {
                try
                {
                    var json = bridge.Call<string>("refresh");
                    snapshot = JsonUtility.FromJson<UsbSnapshot>(json) ?? new UsbSnapshot();
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[{Constants.LOG_TAG}] RigCoordinator: USB refresh failed: {ex.Message}");
                }
            }

            if (Expected && !opened && !libraryMissing
                && Time.unscaledTime - lastOpenAttempt >= OpenRetrySeconds)
            {
                TryOpen();
            }
            if (opened)
            {
                SyncAttachments();
            }
            RefreshStatus();
        }

        private void TryOpen()
        {
            lastOpenAttempt = Time.unscaledTime;
            string rigJson = "{}";
            try
            {
                if (configPresent)
                {
                    rigJson = File.ReadAllText(configPath).Trim();
                    if (!rigJson.StartsWith("{", StringComparison.Ordinal))
                    {
                        openErrorJson = "{\"code\":\"argument\",\"detail\":\"" +
                            RigJson.Escape(ConfigFileName + " is not a JSON object") + "\"}";
                        return;
                    }
                }
                Directory.CreateDirectory(stagingPath);
                SxRig.Open("{\"staging_dir\":\"" + RigJson.Escape(stagingPath) + "\",\"rig\":" + rigJson + "}");
                opened = true;
                openErrorJson = null;
                Debug.Log($"[{Constants.LOG_TAG}] RigCoordinator: rig opened (config={(configPresent ? ConfigFileName : "none")})");
            }
            catch (SxRigLibraryMissingException ex)
            {
                libraryMissing = true;
                openErrorJson = ex.ErrorJson;
                Debug.LogError($"[{Constants.LOG_TAG}] RigCoordinator: rig expected but libsx_rig.so is missing: {ex.Message}");
            }
            catch (SxRigException ex)
            {
                openErrorJson = ex.ErrorJson;
                Debug.LogError($"[{Constants.LOG_TAG}] RigCoordinator: {ex.Message}");
            }
            catch (Exception ex)
            {
                openErrorJson = "{\"code\":\"internal\",\"detail\":\"" + RigJson.Escape(ex.Message) + "\"}";
                Debug.LogError($"[{Constants.LOG_TAG}] RigCoordinator: open failed: {ex.Message}");
            }
        }

        /// <summary>Attaches new devices and detaches vanished ones, then releases their fds.</summary>
        private void SyncAttachments()
        {
            var current = new HashSet<string>();
            foreach (var device in snapshot.devices)
            {
                current.Add(device.key);
                if (attached.ContainsKey(device.key) || attachErrors.ContainsKey(device.key)) continue;
                try
                {
                    SxRig.AttachUsb(device.fd, (ushort)device.vendorId, (ushort)device.productId, device.deviceName);
                    attached[device.key] = device;
                    Debug.Log($"[{Constants.LOG_TAG}] RigCoordinator: attached {device.vendorId:x4}:{device.productId:x4} {device.deviceName}");
                }
                catch (SxRigException ex)
                {
                    // Final for this attachment: a replug is a new key and is tried afresh.
                    attachErrors[device.key] = ex.ErrorJson;
                    Debug.LogError($"[{Constants.LOG_TAG}] RigCoordinator: attach {device.deviceName}: {ex.Message}");
                }
            }

            foreach (var key in new List<string>(attached.Keys))
            {
                if (current.Contains(key)) continue;
                var device = attached[key];
                attached.Remove(key);
                try
                {
                    SxRig.DetachUsb(device.deviceName);
                }
                catch (SxRigException ex)
                {
                    Debug.LogError($"[{Constants.LOG_TAG}] RigCoordinator: detach {device.deviceName}: {ex.Message}");
                }
                Release(key);
                Debug.Log($"[{Constants.LOG_TAG}] RigCoordinator: detached {device.deviceName}");
            }

            // Vanished devices that were never attached (their attach failed) hold an fd too.
            foreach (var key in snapshot.pendingRelease)
            {
                if (!attached.ContainsKey(key))
                {
                    attachErrors.Remove(key);
                    Release(key);
                }
            }
        }

        private void Release(string key)
        {
            try
            {
                bridge?.Call("release", key);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[{Constants.LOG_TAG}] RigCoordinator: release {key}: {ex.Message}");
            }
        }

        private void RefreshStatus()
        {
            if (!Expected && !opened && lastRefusal == null)
            {
                statusJson = NotExpectedStatus;
                return;
            }

            string nativeJson = "null";
            if (opened)
            {
                try
                {
                    nativeJson = SxRig.Status();
                }
                catch (SxRigException ex)
                {
                    nativeJson = "{\"error\":" + ex.ErrorJson + "}";
                }
            }

            var json = new StringBuilder(512);
            json.Append("{\"expected\":").Append(Expected ? "true" : "false")
                .Append(",\"configured\":").Append(configPresent ? "true" : "false")
                .Append(",\"library\":\"").Append(libraryMissing ? "missing" : opened ? "present" : "unknown").Append('"')
                .Append(",\"open\":").Append(opened ? "true" : "false")
                .Append(",\"openError\":").Append(openErrorJson ?? "null")
                .Append(",\"episodeActive\":").Append(episodeActive ? "true" : "false")
                .Append(",\"usb\":{\"present\":").Append(snapshot.present.ToString(CultureInfo.InvariantCulture))
                .Append(",\"attached\":[");
            var first = true;
            foreach (var device in attached.Values)
            {
                if (!first) json.Append(',');
                first = false;
                json.Append("{\"deviceName\":\"").Append(RigJson.Escape(device.deviceName))
                    .Append("\",\"vendorId\":").Append(device.vendorId.ToString(CultureInfo.InvariantCulture))
                    .Append(",\"productId\":").Append(device.productId.ToString(CultureInfo.InvariantCulture))
                    .Append(",\"serial\":").Append(string.IsNullOrEmpty(device.serial)
                        ? "null" : "\"" + RigJson.Escape(device.serial) + "\"")
                    .Append('}');
            }
            json.Append("],\"awaitingPermission\":[");
            for (var i = 0; i < snapshot.awaitingPermission.Length; i++)
            {
                if (i > 0) json.Append(',');
                json.Append('"').Append(RigJson.Escape(snapshot.awaitingPermission[i])).Append('"');
            }
            json.Append("],\"attachErrors\":{");
            first = true;
            foreach (var entry in attachErrors)
            {
                if (!first) json.Append(',');
                first = false;
                json.Append('"').Append(RigJson.Escape(entry.Key)).Append("\":").Append(entry.Value);
            }
            json.Append("}},\"native\":").Append(nativeJson)
                .Append(",\"lastRefusal\":").Append(lastRefusal?.ToJson() ?? "null")
                .Append('}');
            statusJson = json.ToString();
        }

        private void OnDestroy()
        {
            if (!ReferenceEquals(Instance, this)) return;
            StopEpisode();
            foreach (var entry in attached)
            {
                try
                {
                    SxRig.DetachUsb(entry.Value.deviceName);
                }
                catch (SxRigException)
                {
                }
            }
            attached.Clear();
            if (opened)
            {
                SxRig.Close();
                opened = false;
            }
            try
            {
                bridge?.Call("close");
            }
            catch (Exception)
            {
            }
            bridge?.Dispose();
            bridge = null;
            // Instance is kept: a RecordingManager destroy-stop that runs after this still
            // reads the episode's receipt through StopEpisode.
        }
    }

    internal static class RigJson
    {
        public static string Escape(string value) =>
            value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");
    }
}
