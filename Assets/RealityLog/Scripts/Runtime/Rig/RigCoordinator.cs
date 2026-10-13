# nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using RealityLog.Common;

namespace RealityLog.Rig
{
    /// <summary>Why a start was refused for the rig: a closed code, a sentence, the rig's JSON.</summary>
    public sealed class RigRefusal
    {
        /// <summary>
        /// One of <c>rig_library_missing</c>, <c>rig_open_failed</c>, <c>rig_usb_permission</c>,
        /// <c>rig_check_failed</c>, <c>rig_start_failed</c>, <c>rig_not_armed</c>,
        /// <c>rig_identity_pending</c>, <c>collector_signed_out</c>, <c>rig_storage_low</c>,
        /// <c>pod_config_incomplete</c>.
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
    /// A recording start refused because the rig is expected and not ready (or, from a start
    /// path other than the HTTP API, not armed). An <see cref="InvalidOperationException"/>, so
    /// every start path that reports a refusal's message keeps doing so.
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
    /// persistent data directory, the headset is enrolled as a pod (POST /api/enroll left a
    /// machine credential in app-private storage), or any rig USB device (32e4:9230, 303a:*)
    /// is on the bus. Only
    /// then is the library loaded, opened or called; otherwise every method here is a no-op and
    /// recording behaves exactly as it did before the rig existed (the laptop-pod flow).</para>
    ///
    /// <para><b>USB.</b> Every 0.5 s the bridge re-enumerates; a newly opened device's fd is
    /// attached to the rig, and a vanished one is detached from the rig before the bridge
    /// closes its connection (the ABI leaves fd ownership with Kotlin until the detach).</para>
    ///
    /// <para><b>Episode.</b> <see cref="Arm"/> (a worker thread: POST /api/start-recording's)
    /// runs <c>sx_rig_check</c> and <c>sx_rig_start</c> with CLOCK_MONOTONIC and UTC anchors
    /// read back to back; RecordingManager then records under <see cref="ArmedSession"/>;
    /// <see cref="StopEpisode"/> returns the stop receipt.</para>
    ///
    /// USB polling is main-thread; <see cref="Arm"/>, <see cref="StopEpisode"/>,
    /// <see cref="ArmedSession"/>, <see cref="IsExpected"/> and <see cref="StatusJson"/> are
    /// safe from any thread.
    /// </summary>
    public class RigCoordinator : MonoBehaviour
    {
        public const string ConfigFileName = "rig_config.json";
        /// <summary>
        /// Optional site keys merged into the sx_rig_open config (factory_url, auth_url,
        /// pod_name, rig_type_id, upload_limit_mib_s, seal, min_free_bytes, tailscale_ip), and
        /// headset_serial, which provisioning writes over adb (apps cannot read ro.serialno on
        /// the Quest 3S); the rig also reads it through pod_config_path. POST /api/enroll with
        /// a base_url writes factory_url and auth_url here.
        /// </summary>
        public const string PodConfigFileName = "pod_config.json";
        /// <summary>The site an enrollment without base_url (and no site in pod_config.json) uses.</summary>
        public const string DefaultBaseUrl = "https://app.sentientx.io";
        private const string CredentialsDirectoryName = "sx_pod";
        private const string CredentialFileName = "machine-credential.json"; // libsx_rig's
        private const float IdentityPollSeconds = 5f;
        public const string StagingDirectoryName = "rig_staging";
        private const string BridgeClassName = "com.samusynth.questcamera.usb.RigUsbBridge";
        private const string PodIdentityClassName = "com.samusynth.questcamera.pod.PodIdentity";
        private const string EmbodimentAssetsClassName = "com.samusynth.questcamera.pod.EmbodimentAssets";
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
        private AndroidJavaObject? activity;
        private string configPath = "";
        private string podConfigPath = "";
        private string credentialsPath = "";
        private volatile bool enrolled;
        private float lastIdentityPoll = float.NegativeInfinity;
        private volatile string headsetSerial = "";
        private volatile string tailscaleIp = "";
        private volatile string apkBuildId = "";
        // EmbodimentAssets' install of the seal's meshes into filesDir; the rig opens only once
        // it is settled, because the library verifies the directory at open.
        private string embodimentDir = "";
        private volatile string embodimentStateJson = "{\"state\":\"pending\"}";
        private string EmbodimentState
        {
            get
            {
                try
                {
                    return (string?)JObject.Parse(embodimentStateJson)["state"] ?? "pending";
                }
                catch (JsonException)
                {
                    return "pending";
                }
            }
        }
        // "absent" (no meshes in this APK), "ready" and "failed" are settled; on a non-Android
        // player nothing installs, so the state stays pending and the rig opens without them.
        private bool EmbodimentSettled => activity == null || EmbodimentState is "ready" or "failed" or "absent";
        // What the rig was opened with; a change reopens it before an enrollment.
        private string openedConfigJson = "";
        private string stagingPath = "";
        private UsbSnapshot snapshot = new();
        private readonly Dictionary<string, UsbEntry> attached = new();
        private readonly Dictionary<string, string> attachErrors = new();
        private bool configPresent;
        // Read by Arm on a worker thread; written on the main thread.
        private volatile bool expected;
        private volatile bool opened;
        private volatile bool libraryMissing;
        private volatile string? openErrorJson;
        private volatile string[] awaitingPermission = Array.Empty<string>();
        private string dataPath = "";
        private string appVersion = "";
        private float lastOpenAttempt = float.NegativeInfinity;
        private float lastPoll = float.NegativeInfinity;
        private readonly object episodeLock = new();
        private bool episodeActive;
        private bool episodeRecording;
        private string? armedSession;
        private string? episodeReceiptJson;
        private volatile RigRefusal? lastRefusal;
        private volatile string statusJson = NotExpectedStatus;
        // sx_rig_status may wait up to 50 ms, so it is polled off the main thread.
        private volatile string nativeStatusJson = "null";
        private volatile string nativeSummaryJson = "";
        private System.Threading.Timer? nativeStatusTimer;
        private int nativeStatusBusy;
        private const string DeviceReportClassName = "com.samusynth.questcamera.pod.DeviceReport";
        private const int DeviceReportIntervalMs = 15000;
        private System.Threading.Thread? deviceReportThread;
        private readonly System.Threading.ManualResetEvent deviceReportStop = new(false);
        private volatile int deviceReportsSent;
        private volatile string deviceReportJson = "null";
        private volatile string? deviceReportError;
        private volatile bool collectorSignedIn;
        private volatile bool signInCuePending;
        // Seal verdicts seen so far (native-status timer thread); a change to sealed or refused
        // after the first observation queues a cue for the main thread.
        private readonly Dictionary<string, string> sealStates = new();
        private bool sealsObserved;
        private readonly System.Collections.Concurrent.ConcurrentQueue<RigCues.Cue> cues = new();
        private bool floorObserved; // native-status timer thread only

        private const string NotExpectedStatus =
            "{\"expected\":false,\"configured\":false,\"enrolled\":false,\"headsetSerial\":null," +
            "\"tailscaleIp\":null,\"apkBuildId\":null,\"library\":\"unknown\",\"open\":false," +
            "\"openError\":null,\"episodeActive\":false,\"usb\":{\"present\":0,\"attached\":[],\"awaitingPermission\":[]," +
            "\"attachErrors\":{}},\"podId\":null,\"registeredRig\":null,\"seals\":null,\"uplink\":null,\"floor\":null," +
            "\"embodiment_assets\":null,\"embodimentInstall\":null,\"nativeState\":null," +
            "\"deviceReport\":null,\"deviceReportError\":null," +
            "\"native\":null,\"lastRefusal\":null}";

        /// <summary>
        /// True when the rig is configured (rig_config.json), the headset is enrolled as a pod
        /// (it holds a machine credential), or rig devices are present; see the class remarks.
        /// </summary>
        private bool Expected => configPresent || enrolled || snapshot.present > 0 || attached.Count > 0;

        /// <summary><see cref="Expected"/> as of the last main-thread poll. Any thread.</summary>
        public bool IsExpected => expected;

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
            dataPath = Application.persistentDataPath;
            appVersion = Application.version;
            configPath = Path.Combine(Application.persistentDataPath, ConfigFileName);
            podConfigPath = Path.Combine(Application.persistentDataPath, PodConfigFileName);
            // The machine credential lives in app-private storage, never on shared storage.
            credentialsPath = Path.Combine(Application.persistentDataPath, CredentialsDirectoryName);
            stagingPath = Path.Combine(Application.persistentDataPath, StagingDirectoryName);
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");
                using var identity = new AndroidJavaClass(PodIdentityClassName);
                credentialsPath = Path.Combine(
                    identity.CallStatic<string>("privateFilesDir", activity), CredentialsDirectoryName);
                bridge = new AndroidJavaObject(BridgeClassName, activity);
                using var embodiment = new AndroidJavaClass(EmbodimentAssetsClassName);
                embodimentDir = embodiment.CallStatic<string>("installDir", activity);
                embodiment.CallStatic("installAsync", activity);
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
            if (signInCuePending)
            {
                signInCuePending = false;
                RigCues.Play(RigCues.Cue.SignedIn, gameObject);
                Debug.Log($"[{Constants.LOG_TAG}] RigCoordinator: collector signed in");
            }
            while (cues.TryDequeue(out var cue))
            {
                RigCues.Play(cue, gameObject);
            }
            if (Time.unscaledTime - lastPoll >= PollIntervalSeconds)
            {
                Poll();
            }
        }

        /// <summary>
        /// Checks and starts the rig's episode <paramref name="sessionName"/> (also its
        /// <c>episode_id</c>), or returns why not. Any thread, and never the Unity main thread:
        /// <c>sx_rig_check</c> is bounded 5 s and <c>sx_rig_start</c> blocks until every stream
        /// is armed, up to 12 s. Uses the USB snapshot of the last main-thread poll (≤ 0.5 s old).
        /// On success the episode is armed: <see cref="RecordingManager"/> records the session
        /// under <see cref="ArmedSession"/>, and whoever armed it stops it with
        /// <see cref="StopEpisode"/> if that start does not happen.
        /// </summary>
        public RigRefusal? Arm(string sessionName, string operatorId,
            string? taskAssignmentId = null, string? mode = null)
        {
            lock (episodeLock)
            {
                var refusal = ArmLocked(sessionName, operatorId, taskAssignmentId, mode);
                lastRefusal = refusal;
                return refusal;
            }
        }

        private RigRefusal? ArmLocked(string sessionName, string operatorId,
            string? taskAssignmentId, string? mode)
        {
            if (!expected) return null;
            if (episodeActive)
            {
                return new RigRefusal("rig_start_failed", $"rig episode '{armedSession ?? "?"}' is still active");
            }
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
            var awaiting = awaitingPermission;
            if (awaiting.Length > 0)
            {
                return new RigRefusal("rig_usb_permission",
                    "no USB permission for " + string.Join(", ", awaiting) +
                    "; someone must accept the prompt in the headset once with \"always open\" checked");
            }
            var buildId = apkBuildId;
            if (buildId.Length == 0)
            {
                return new RigRefusal("rig_identity_pending",
                    "the installed APK's build id is still being computed; retry in a few seconds");
            }
            // An enrolled headset's start begins the Factory episode in the collector's open
            // collection, so it needs a signed-in collector (sx_rig_start answers
            // SX_RIG_E_STATE otherwise). Asked fresh: the cached status may be a second old.
            if (enrolled && CollectorSignedIn(SafeStatus()) == false)
            {
                return new RigRefusal("collector_signed_out",
                    "no collector is signed in on this headset; POST /api/sign-in and approve the code");
            }
            try
            {
                SxRig.RunCheck();
            }
            catch (SxRigException ex)
            {
                return ex.Error == SxRigError.Storage
                    ? new RigRefusal("rig_storage_low", ex.Message, ex.ErrorJson)
                    : new RigRefusal("rig_check_failed", ex.Message, ex.ErrorJson);
            }

            var (mono, utc) = SxRig.ReadAnchors(MonotonicClock.Nanos);
            // The seal needs operator_id, quest_apk_build_id and quest_session_dir.
            var episode = new JObject
            {
                ["episode_id"] = sessionName,
                ["operator_id"] = operatorId,
                ["quest_apk_build_id"] = buildId,
                ["quest_session_dir"] = Path.Combine(dataPath, sessionName),
                ["app_version"] = appVersion,
                ["task_assignment_id"] = taskAssignmentId,
                ["mode"] = mode,
                ["monotonic_anchor_ns"] = mono,
                ["utc_anchor_ns"] = utc,
            };
            foreach (var key in new[] { "task_assignment_id", "mode" })
            {
                if (episode[key]?.Type == JTokenType.Null) episode.Remove(key);
            }
            var episodeJson = episode.ToString(Formatting.None);
            try
            {
                SxRig.Start(episodeJson, mono, utc);
            }
            catch (SxRigException ex)
            {
                // A sign-out between the check above and the start.
                if (ex.Error == SxRigError.State && enrolled && CollectorSignedIn(SafeStatus()) == false)
                {
                    return new RigRefusal("collector_signed_out",
                        "the collector signed out; POST /api/sign-in and approve the code", ex.ErrorJson);
                }
                if (ex.Error == SxRigError.Storage)
                {
                    return new RigRefusal("rig_storage_low",
                        "not enough free space to record the rig's streams", ex.ErrorJson);
                }
                return new RigRefusal("rig_start_failed", ex.Message, ex.ErrorJson);
            }
            episodeReceiptJson = null;
            episodeActive = true;
            armedSession = sessionName;
            Debug.Log($"[{Constants.LOG_TAG}] RigCoordinator: episode '{sessionName}' armed (mono={mono} utc={utc})");
            return null;
        }

        /// <summary>
        /// Main thread, before <see cref="Enroll"/>: records the site to enroll with (when
        /// <paramref name="baseUrl"/> is given, into pod_config.json), then opens the rig — even
        /// when no rig is expected yet — or reopens it when its config changed (a new tailnet
        /// address, say) and no episode is active. Returns a refusal or null.
        /// </summary>
        public RigRefusal? PrepareEnrollment(string? baseUrl)
        {
            // The site is chosen at enrollment: base_url when given, else the one pod_config.json
            // names, else production. rig_type_id is provisioning's (pod_config.json), never a
            // default in code: the rig commissions as exactly that registered type.
            try
            {
                var site = ReadPodConfig();
                if (site["rig_type_id"] is not JValue { Type: JTokenType.String })
                {
                    return new RigRefusal("pod_config_incomplete",
                        $"{PodConfigFileName} has no rig_type_id (provisioning writes it, e.g. \"rig_yubi\")");
                }
                if (!string.IsNullOrEmpty(baseUrl) || site["factory_url"] == null)
                {
                    var trimmed = (string.IsNullOrEmpty(baseUrl) ? DefaultBaseUrl : baseUrl!).TrimEnd('/');
                    site["factory_url"] = trimmed + "/d/factory/";
                    site["auth_url"] = trimmed + "/d/auth/";
                    File.WriteAllText(podConfigPath, site.ToString(Formatting.Indented));
                }
            }
            catch (Exception ex) when (ex is IOException || ex is JsonException || ex is InvalidDataException
                || ex is UnauthorizedAccessException)
            {
                return new RigRefusal("pod_config_incomplete", $"{PodConfigFileName}: {ex.Message}");
            }
            PollIdentity(force: true);
            if (libraryMissing)
            {
                return new RigRefusal("rig_library_missing", "this build has no libsx_rig.so", openErrorJson);
            }
            if (opened && BuildConfig() is string config && config != openedConfigJson)
            {
                lock (episodeLock)
                {
                    if (!episodeActive)
                    {
                        CloseRig();
                    }
                }
            }
            if (!opened)
            {
                TryOpen();
            }
            RefreshStatus();
            return opened ? null : new RigRefusal("rig_open_failed", "sx_rig_open failed", openErrorJson);
        }

        /// <summary>
        /// <c>sx_rig_enroll</c> on the calling (worker) thread, after
        /// <see cref="PrepareEnrollment"/>. Returns the pod record JSON; throws
        /// <see cref="SxRigException"/>.
        /// </summary>
        public string Enroll(string joinCode)
        {
            var result = SxRig.Enroll(joinCode);
            enrolled = true;
            expected = true;
            return result;
        }

        /// <summary>
        /// The operator a local headset records under when the start body names none:
        /// pod_config.json's <c>operator_id</c>. Null when absent or unreadable. Any thread.
        /// </summary>
        public string? DefaultOperatorId
        {
            get
            {
                try
                {
                    return ReadPodConfig()["operator_id"] is JValue { Type: JTokenType.String } value
                        && ((string?)value)?.Trim() is { Length: > 0 } id ? id : null;
                }
                catch (Exception)
                {
                    return null;
                }
            }
        }

        /// <summary>
        /// The rig's episode directory for an OQC session (the session name is its episode_id):
        /// <c>&lt;persistentDataPath&gt;/rig_staging/&lt;session&gt;</c>, holding episode.mcap and
        /// seal.json once sealed. Any thread.
        /// </summary>
        public static string EpisodeDirectory(string persistentDataPath, string session) =>
            Path.Combine(persistentDataPath, StagingDirectoryName, session);

        /// <summary>True once the rig is open (an enrolled headset opens it on its own).</summary>
        public bool IsOpen => opened;

        /// <summary>
        /// <c>sx_rig_sign_in</c> on the calling (worker) thread; throws
        /// <see cref="SxRigException"/>. The cue plays when the approval lands.
        /// </summary>
        public string SignIn() => SxRig.SignIn();

        /// <summary><c>sx_rig_sign_out</c>; throws <see cref="SxRigException"/>.</summary>
        public void SignOut()
        {
            SxRig.SignOut();
            collectorSignedIn = false;
        }

        /// <summary><c>sx_rig_command</c> on the calling (worker) thread.</summary>
        public string Command(string commandJson) => SxRig.Command(commandJson);

        /// <summary>
        /// The session an armed, not yet recording, rig episode belongs to; null otherwise.
        /// </summary>
        public string? ArmedSession
        {
            get { lock (episodeLock) { return episodeActive && !episodeRecording ? armedSession : null; } }
        }

        /// <summary>RecordingManager started the armed session's OQC streams.</summary>
        public void MarkRecording()
        {
            lock (episodeLock) { episodeRecording = episodeActive; }
        }

        /// <summary>
        /// Stops the rig's episode and returns its stop receipt (a JSON object: the library's
        /// <c>{"outcome": ...}</c>, or <c>{"error": {...}}</c> when the stop call itself failed),
        /// or null when no rig episode ran. Bounded 5 s. Any thread. Idempotent: a second call
        /// for the same episode returns the same receipt. Never throws.
        /// </summary>
        public string? StopEpisode()
        {
            lock (episodeLock)
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
                episodeRecording = false;
                armedSession = null;
                return episodeReceiptJson;
            }
        }

        private void Poll()
        {
            lastPoll = Time.unscaledTime;
            configPresent = File.Exists(configPath);
            enrolled = File.Exists(Path.Combine(credentialsPath, CredentialFileName));
            PollIdentity(force: false);
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

            awaitingPermission = snapshot.awaitingPermission ?? Array.Empty<string>();
            expected = Expected;
            if (Expected && !opened && !libraryMissing && EmbodimentSettled
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
            try
            {
                string config;
                try
                {
                    config = BuildConfig();
                }
                catch (Exception ex) when (ex is JsonException || ex is InvalidDataException || ex is IOException)
                {
                    openErrorJson = new JObject { ["code"] = "argument", ["detail"] = ex.Message }.ToString(Formatting.None);
                    return;
                }
                Directory.CreateDirectory(stagingPath);
                Directory.CreateDirectory(credentialsPath);
                SxRig.Open(config);
                openedConfigJson = config;
                opened = true;
                openErrorJson = null;
                nativeStatusTimer ??= new System.Threading.Timer(_ => PollNativeStatus(), null, 0, 1000);
                StartDeviceReports();
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

        /// <summary>
        /// The sx_rig_open config: pod_config.json's keys, the production site unless it names
        /// one, this headset's serial and tailnet address unless it names them, rig_config.json
        /// as "rig" when present (otherwise the rig resolves it from the Factory once enrolled),
        /// and the staging and app-private credentials directories, which are always ours.
        /// </summary>
        private string BuildConfig()
        {
            var config = ReadPodConfig();
            config["staging_dir"] = stagingPath;
            config["credentials_dir"] = credentialsPath;
            // factory_url, auth_url and rig_type_id come only from pod_config.json (provisioning
            // and POST /api/enroll write them); without a site the rig records and seals but
            // does not upload or join the Factory floor.
            // Apps cannot read ro.serialno on the Quest 3S: provisioning writes headset_serial
            // into pod_config.json over adb, and the rig reads it through pod_config_path.
            config["pod_config_path"] = podConfigPath;
            // The seal's meshes, installed by EmbodimentAssets. Passed when installed, and when
            // the install failed, so the rig's own readiness names what is missing; omitted
            // when this APK carries none (the rig then reports configured: false).
            if (embodimentDir.Length > 0 && EmbodimentState is "ready" or "failed")
            {
                config["embodiment_assets_dir"] = embodimentDir;
            }
            if (config["tailscale_ip"] == null && tailscaleIp.Length > 0)
            {
                config["tailscale_ip"] = tailscaleIp;
            }
            if (File.Exists(configPath))
            {
                config["rig"] = JObject.Parse(File.ReadAllText(configPath));
            }
            return config.ToString(Formatting.None);
        }

        private string ReadPodConfigSerial()
        {
            try
            {
                return (string?)ReadPodConfig()["headset_serial"] ?? "";
            }
            catch (Exception)
            {
                return "";
            }
        }

        private JObject ReadPodConfig()
        {
            if (!File.Exists(podConfigPath)) return new JObject();
            var token = JToken.Parse(File.ReadAllText(podConfigPath));
            return token as JObject ?? throw new InvalidDataException($"{PodConfigFileName} is not a JSON object");
        }

        /// <summary>Serial, tailnet address and APK build id from PodIdentity (main thread).</summary>
        private void PollIdentity(bool force)
        {
            if (activity == null) return;
            if (!force && Time.unscaledTime - lastIdentityPoll < IdentityPollSeconds
                && apkBuildId.Length > 0 && EmbodimentSettled) return;
            lastIdentityPoll = Time.unscaledTime;
            try
            {
                using var identity = new AndroidJavaClass(PodIdentityClassName);
                headsetSerial = ReadPodConfigSerial();
                using (var embodiment = new AndroidJavaClass(EmbodimentAssetsClassName))
                {
                    embodimentStateJson = embodiment.CallStatic<string>("stateJson") ?? embodimentStateJson;
                }
                tailscaleIp = identity.CallStatic<string>("tailscaleIp") ?? "";
                apkBuildId = identity.CallStatic<string>("apkBuildId", activity) ?? "";
            }
            catch (Exception ex)
            {
                Debug.LogError($"[{Constants.LOG_TAG}] RigCoordinator: pod identity: {ex.Message}");
            }
        }

        /// <summary>
        /// Closes the rig; the bridge's connections stay open, so the next poll reopens it and
        /// attaches every device again.
        /// </summary>
        private void CloseRig()
        {
            SxRig.Close();
            opened = false;
            openedConfigJson = "";
            attached.Clear();
            attachErrors.Clear();
            lastOpenAttempt = float.NegativeInfinity;
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

            var nativeJson = opened ? nativeStatusJson : "null";
            bool episodeIsActive;
            lock (episodeLock) { episodeIsActive = episodeActive; }

            var json = new StringBuilder(512);
            json.Append("{\"expected\":").Append(Expected ? "true" : "false")
                .Append(",\"configured\":").Append(configPresent ? "true" : "false")
                .Append(",\"enrolled\":").Append(enrolled ? "true" : "false")
                .Append(",\"headsetSerial\":").Append(JsonString(headsetSerial))
                .Append(",\"tailscaleIp\":").Append(JsonString(tailscaleIp))
                .Append(",\"apkBuildId\":").Append(JsonString(apkBuildId))
                .Append(",\"library\":\"").Append(libraryMissing ? "missing" : opened ? "present" : "unknown").Append('"')
                .Append(",\"open\":").Append(opened ? "true" : "false")
                .Append(",\"openError\":").Append(openErrorJson ?? "null")
                .Append(",\"episodeActive\":").Append(episodeIsActive ? "true" : "false")
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
            json.Append("}}");
            var summary = opened ? nativeSummaryJson : "";
            if (summary.Length > 2)
            {
                // podId, registeredRig, seals, uplink, lifted from the native status.
                json.Append(',').Append(summary, 1, summary.Length - 2);
            }
            else
            {
                json.Append(",\"nativeState\":null,\"podId\":null,\"registeredRig\":null,\"seals\":null,\"uplink\":null,\"floor\":null");
                // The rig's verdict on the meshes ({ready, configured, assets, missing}) comes
                // with its status; until the rig is open, say only that it has none yet.
                json.Append(",\"embodiment_assets\":{\"ready\":false,\"configured\":false}");
            }
            // The last battery/charging/network-time report the heartbeat sees, and its error.
            json.Append(",\"deviceReport\":").Append(deviceReportJson)
                .Append(",\"deviceReportError\":").Append(deviceReportError is string error ? JsonString(error) : "null");
            // The app's install of the meshes into filesDir: state, assets, copied, error.
            json.Append(",\"embodimentInstall\":").Append(embodimentStateJson);
            json.Append(",\"native\":").Append(nativeJson)
                .Append(",\"lastRefusal\":").Append(lastRefusal?.ToJson() ?? "null")
                .Append('}');
            statusJson = json.ToString();
        }

        /// <summary>
        /// Reports battery, charging and the network-time fix (Kotlin DeviceReport) to the rig
        /// every 15 s on a background thread; the heartbeat trusts a report for 60 s, so a
        /// stalled app reads not ready instead of stale-ready. A report Kotlin cannot make
        /// (no battery state) is skipped, never invented.
        /// </summary>
        private void StartDeviceReports()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (deviceReportThread != null || activity == null) return;
            var context = activity;
            deviceReportThread = new System.Threading.Thread(() =>
            {
                AndroidJNI.AttachCurrentThread();
                try
                {
                    using var report = new AndroidJavaClass(DeviceReportClassName);
                    var wait = 0; // the first report goes at once
                    while (!deviceReportStop.WaitOne(wait))
                    {
                        wait = DeviceReportIntervalMs;
                        if (!opened) continue;
                        try
                        {
                            var json = report.CallStatic<string>("json", context);
                            if (string.IsNullOrEmpty(json)) continue;
                            SxRig.ReportDevice(json);
                            deviceReportJson = json;
                            deviceReportsSent++;
                        }
                        catch (Exception ex)
                        {
                            deviceReportError = ex.Message;
                        }
                    }
                }
                finally
                {
                    AndroidJNI.DetachCurrentThread();
                }
            }) { IsBackground = true, Name = "RigDeviceReport" };
            deviceReportThread.Start();
#endif
        }

        private void PollNativeStatus()
        {
            if (!opened || System.Threading.Interlocked.Exchange(ref nativeStatusBusy, 1) == 1) return;
            try
            {
                var json = SxRig.Status();
                nativeStatusJson = json;
                // "busy" (a check/start/stop/enroll holds the rig) keeps the last summary.
                if (JToken.Parse(json) is JObject status && (string?)status["state"] != "busy")
                {
                    nativeSummaryJson = new JObject
                    {
                        ["nativeState"] = status["state"],
                        ["podId"] = status["pod_id"],
                        ["registeredRig"] = status["registered_rig"],
                        ["seals"] = status["seals"],
                        ["uplink"] = status["uplink"],
                        ["floor"] = status["floor"],
                        ["embodiment_assets"] = status["embodiment_assets"],
                    }.ToString(Formatting.None);
                    var signedIn = CollectorSignedIn(status) == true;
                    // Only a transition seen while running cues; a sign-in already held at
                    // launch does not.
                    if (signedIn && !collectorSignedIn && floorObserved)
                    {
                        signInCuePending = true;
                    }
                    collectorSignedIn = signedIn;
                    floorObserved = true;
                    QueueSealCues(status["seals"] as JObject);
                }
            }
            catch (SxRigException ex)
            {
                nativeStatusJson = "{\"error\":" + ex.ErrorJson + "}";
            }
            catch (JsonException)
            {
            }
            finally
            {
                System.Threading.Volatile.Write(ref nativeStatusBusy, 0);
            }
        }

        /// <summary>
        /// Queues a sealed or refused cue for each episode whose verdict changed to it since the
        /// last status; verdicts already present at the first status (an earlier run's) do not cue.
        /// </summary>
        private void QueueSealCues(JObject? seals)
        {
            if (seals == null) return;
            foreach (var entry in seals)
            {
                var state = (string?)entry.Value?["state"] ?? "";
                sealStates.TryGetValue(entry.Key, out var previous);
                if (sealsObserved && state != previous)
                {
                    if (state == "sealed") cues.Enqueue(RigCues.Cue.Sealed);
                    else if (state == "refused") cues.Enqueue(RigCues.Cue.Refused);
                }
                sealStates[entry.Key] = state;
            }
            sealsObserved = true;
        }

        /// <summary>Plays a cue (main thread) when the rig took part in the recording.</summary>
        public void Cue(RigCues.Cue cue) => RigCues.Play(cue, gameObject);

        /// <summary>floor.operator.signed_in of a status, or null when the status has no floor.</summary>
        private static bool? CollectorSignedIn(JObject? status) =>
            status?["floor"]?["operator"]?["signed_in"] is JValue { Type: JTokenType.Boolean } value
                ? (bool)value : (bool?)null;

        /// <summary>sx_rig_status parsed, or null (busy, failed, unparsable). Any thread.</summary>
        private static JObject? SafeStatus()
        {
            try
            {
                var status = JToken.Parse(SxRig.Status()) as JObject;
                return (string?)status?["state"] == "busy" ? null : status;
            }
            catch (Exception ex) when (ex is SxRigException || ex is JsonException)
            {
                return null;
            }
        }

        private static string JsonString(string value) =>
            value.Length == 0 ? "null" : "\"" + RigJson.Escape(value) + "\"";

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
            nativeStatusTimer?.Dispose();
            nativeStatusTimer = null;
            deviceReportStop.Set();
            deviceReportThread?.Join(1000);
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
            activity?.Dispose();
            activity = null;
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
