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
    /// <para><b>Episode.</b> POST /api/start-recording, on its worker thread: <see cref="CheckAndReserve"/>
    /// (<c>sx_rig_check</c>, which must report ready, then the session name), OQC's session starts on the
    /// main thread under <see cref="ReservedSession"/>, then <see cref="StartArmed"/>
    /// (<c>sx_rig_start_armed</c>: the rig arms and anchors the episode itself, inside OQC's
    /// recording). <see cref="StopEpisode"/>, after OQC's files are final, returns the receipt.</para>
    ///
    /// USB polling is main-thread; the episode calls, <see cref="IsExpected"/> and
    /// <see cref="StatusJson"/> are safe from any thread.
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
        private volatile string podName = "";
        private volatile string tailscaleIp = "";
        private volatile string apkBuildId = "";
        // EmbodimentAssets' install of the seal's meshes into filesDir; the rig opens only once
        // it is settled, because the library verifies the directory at open.
        private string embodimentDir = "";
        // What failed while the coordinator started (by part), surfaced in /api/status.
        private readonly Dictionary<string, JToken> startupErrors = new();
        // libsx_rig's open-config keys (Resources/sx_rig_config_keys.json, shipped with the .so).
        private HashSet<string>? rigKeys;
        // The pod_config.json key problem last reported (so the refused cue plays once per change).
        private string? podConfigProblem;
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
        private string? reservedSession;
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
        // The last sx_rig_check report (or its error), for /api/status rig.lastCheck.
        private volatile string lastCheckJson = "null";
        // The rig episode recording now (read by the status timer without episodeLock, which a
        // start may hold for seconds) and the failure the status reported for it, once.
        private volatile string? activeEpisodeId;
        private string? failureHandledFor; // status timer thread only
        private readonly System.Collections.Concurrent.ConcurrentQueue<(string session, JObject failure)> failures = new();
        // pod_config.json's local_only (default true), re-read with the identity poll.
        private volatile bool localOnly = true;
        private volatile bool collectorSignedIn;
        private volatile bool signInCuePending;
        // Seal verdicts seen so far (native-status timer thread); a change to sealed or refused
        // after the first observation queues a cue for the main thread.
        private readonly Dictionary<string, string> sealStates = new();
        private bool sealsObserved;
        private readonly System.Collections.Concurrent.ConcurrentQueue<RigCues.Cue> cues = new();
        private bool floorObserved; // native-status timer thread only

        private const string NotExpectedStatus =
            "{\"expected\":false,\"configured\":false,\"enrolled\":false,\"localOnly\":true,\"headsetSerial\":null," +
            "\"tailscaleIp\":null,\"apkBuildId\":null,\"library\":\"unknown\",\"open\":false," +
            "\"openError\":null,\"episodeActive\":false,\"usb\":{\"present\":0,\"attached\":[],\"awaitingPermission\":[]," +
            "\"attachErrors\":{}},\"podId\":null,\"registeredRig\":null,\"seals\":null,\"uplink\":null,\"floor\":null," +
            "\"embodiment_assets\":null,\"embodimentInstall\":null,\"nativeState\":null," +
            "\"deviceReport\":null,\"deviceReportError\":null,\"startupErrors\":{},\"lastCheck\":null," +
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
            localOnly = ReadLocalOnly(); // before the first open
            try
            {
                var keysAsset = Resources.Load<TextAsset>("sx_rig_config_keys")
                    ?? throw new InvalidOperationException("Resources/sx_rig_config_keys.json is not in this build");
                rigKeys = RigOpenConfig.ParseRigKeys(keysAsset.text);
                if (RigOpenConfig.PolicyKeysUnknownToRig(rigKeys) is { Length: > 0 } stale)
                {
                    throw new InvalidOperationException(
                        "this build sends keys the shipped libsx_rig does not know: " + string.Join(", ", stale));
                }
            }
            catch (InvalidOperationException ex)
            {
                rigKeys = null;
                RecordStartupError("rig_config_keys", ex);
            }
            // At launch, rig or no rig: a pod_config.json the app would partly not understand is
            // reported (startupErrors.pod_config) and buzzed, never silently ignored.
            if (rigKeys != null && File.Exists(podConfigPath))
            {
                try
                {
                    if (RigOpenConfig.UnknownPodConfigKeys(ReadPodConfig(), rigKeys) is { Length: > 0 } unknownAtLaunch)
                    {
                        startupErrors["pod_config"] = new JObject
                        {
                            ["code"] = "pod_config_unknown_keys",
                            ["keys"] = new JArray(unknownAtLaunch),
                            ["detail"] = $"{PodConfigFileName} has keys neither the app nor the rig accepts: {string.Join(", ", unknownAtLaunch)}",
                        };
                        podConfigProblem = string.Join(",", unknownAtLaunch);
                        QueueCue(RigCues.Cue.Refused);
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is JsonException || ex is InvalidDataException)
                {
                    RecordStartupError("pod_config", ex);
                }
            }
#if UNITY_ANDROID && !UNITY_EDITOR
            // Each Android piece starts on its own, and a failure is kept for /api/status: on the
            // 3S a bridge constructor failure (an androidx call the app's core lacked) once
            // skipped the embodiment install too, and its log line had left logcat's buffer by
            // the time anyone looked.
            try
            {
                using var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");
            }
            catch (Exception ex)
            {
                RecordStartupError("activity", ex);
            }
            if (activity != null)
            {
                try
                {
                    using var identity = new AndroidJavaClass(PodIdentityClassName);
                    credentialsPath = Path.Combine(
                        identity.CallStatic<string>("privateFilesDir", activity), CredentialsDirectoryName);
                }
                catch (Exception ex)
                {
                    RecordStartupError("pod_identity", ex);
                }
                try
                {
                    using var embodiment = new AndroidJavaClass(EmbodimentAssetsClassName);
                    embodimentDir = embodiment.CallStatic<string>("installDir", activity);
                    embodiment.CallStatic("installAsync", activity);
                }
                catch (Exception ex)
                {
                    // Settled as failed, so the rig still opens and its own readiness says why.
                    embodimentStateJson = new JObject { ["state"] = "failed", ["error"] = ex.Message }
                        .ToString(Formatting.None);
                    RecordStartupError("embodiment_assets", ex);
                }
                try
                {
                    bridge = new AndroidJavaObject(BridgeClassName, activity);
                }
                catch (Exception ex)
                {
                    // Without the bridge no USB device can be opened; a rig_config.json still
                    // makes the rig expected, so a configured headset refuses instead of
                    // recording without its wrist streams.
                    bridge = null;
                    RecordStartupError("usb_bridge", ex);
                }
            }
#endif
            Poll();
        }

        private void RecordStartupError(string what, Exception ex)
        {
            startupErrors[what] = ex.ToString();
            QueueCue(RigCues.Cue.Refused); // the operator cannot read /api/status
            Debug.LogError($"[{Constants.LOG_TAG}] RigCoordinator: {what} failed at startup: {ex}");
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
            while (failures.TryDequeue(out var failed))
            {
                // The operator hears it at once; the recording stops through its normal path.
                RigCues.Play(RigCues.Cue.Refused, gameObject);
                Debug.LogError($"[{Constants.LOG_TAG}] RigCoordinator: rig episode '{failed.session}' failed: {failed.failure.ToString(Formatting.None)}");
                EpisodeFailed?.Invoke(failed.session, failed.failure);
            }
            if (Time.unscaledTime - lastPoll >= PollIntervalSeconds)
            {
                Poll();
            }
        }

        /// <summary>
        /// Steps 1–2 of a rig start, for a caller that decided the rig takes part (any thread,
        /// never Unity's main thread: <c>sx_rig_check</c> is bounded about 16 s on a Factory: it opens each device for live data): runs the device check
        /// every time (<see cref="RigStartGate"/>) and, only when it passed, reserves
        /// <paramref name="sessionName"/> (the rig's episode_id) for the OQC start that follows,
        /// which <see cref="RecordingManager"/> reads as <see cref="ReservedSession"/>; a stop
        /// before the arming cancels it. Returns why not, or null.
        /// </summary>
        public RigRefusal? CheckAndReserve(string sessionName)
        {
            // Check and reservation under one lock: no second start can slip between a passing
            // check and the reservation it allows.
            lock (episodeLock)
            {
                var refusal = CheckReadyLocked();
                lastRefusal = refusal;
                if (refusal == null)
                {
                    reservedSession = sessionName;
                }
                return refusal;
            }
        }

        private RigRefusal? CheckReadyLocked()
        {
            // No "is the rig expected" test here: the caller decided that once (IsExpected), and
            // re-reading it here once let a start skip sx_rig_check and arm on no passing check.
            var facts = new RigStartFacts
            {
                EpisodeBusy = episodeActive || reservedSession != null,
                BusySession = armedSession ?? reservedSession,
                LibraryMissing = libraryMissing,
                Opened = opened,
                OpenErrorJson = openErrorJson,
                AwaitingPermission = awaitingPermission,
                ApkBuildIdKnown = apkBuildId.Length > 0,
                CollectorReady = !enrolled || localOnly || CollectorSignedIn(SafeStatus()) != false,
            };
            var decision = RigStartGate.Evaluate(facts, SxRig.RunCheck, report => lastCheckJson = report);
            // The API is closed to anyone without the pod token once enrolled, so a refused start
            // says why in the log as well (the check's problems name devices, never a secret).
            if (decision is { } refused)
            {
                Debug.LogWarning($"[{Constants.LOG_TAG}] RigCoordinator: start refused: {refused.Code}: {refused.Message}");
            }
            return decision;
        }

        /// <summary>Cancels a reservation whose OQC start did not happen.</summary>
        public void Unreserve(string sessionName)
        {
            lock (episodeLock)
            {
                if (reservedSession == sessionName) reservedSession = null;
            }
        }

        /// <summary>The session reserved for a rig episode not yet armed; null otherwise.</summary>
        public string? ReservedSession
        {
            get { lock (episodeLock) { return reservedSession; } }
        }

        /// <summary>
        /// Step 3, after OQC's session is recording (any thread, never Unity's main thread):
        /// <c>sx_rig_start_armed</c> arms every stream and, once all deliver, anchors the episode
        /// itself, so the episode's window starts while OQC is already recording (the seal
        /// requires both to cover it). Returns the rig's start answer
        /// (<c>{"episode_id", "monotonic_anchor_ns", "utc_anchor_ns", "arm_utc_anchor",
        /// "arming_ms"}</c>) or a refusal; on a refusal the caller stops OQC's session.
        /// </summary>
        public (string? start, RigRefusal? refusal) StartArmed(string sessionName, string operatorId,
            string? taskAssignmentId = null, string? mode = null)
        {
            lock (episodeLock)
            {
                var result = StartArmedLocked(sessionName, operatorId, taskAssignmentId, mode);
                if (result.refusal != null) lastRefusal = result.refusal;
                return result;
            }
        }

        private (string? start, RigRefusal? refusal) StartArmedLocked(string sessionName, string operatorId,
            string? taskAssignmentId, string? mode)
        {
            if (reservedSession != sessionName)
            {
                return (null, new RigRefusal("rig_start_failed",
                    $"the recording '{sessionName}' stopped before the rig armed"));
            }
            reservedSession = null;
            // The seal needs operator_id, quest_apk_build_id and quest_session_dir.
            var episode = new JObject
            {
                ["episode_id"] = sessionName,
                ["operator_id"] = operatorId,
                ["quest_apk_build_id"] = apkBuildId,
                ["quest_session_dir"] = Path.Combine(dataPath, sessionName),
                ["app_version"] = appVersion,
            };
            if (taskAssignmentId != null) episode["task_assignment_id"] = taskAssignmentId;
            if (mode != null) episode["mode"] = mode;
            string started;
            try
            {
                started = SxRig.StartArmed(episode.ToString(Formatting.None));
            }
            catch (SxRigException ex)
            {
                // A sign-out between the check and the start.
                if (ex.Error == SxRigError.State && enrolled && CollectorSignedIn(SafeStatus()) == false)
                {
                    return (null, new RigRefusal("collector_signed_out",
                        "the collector signed out; POST /api/sign-in and approve the code", ex.ErrorJson));
                }
                return (null, Refusal(ex, "rig_start_failed"));
            }
            episodeReceiptJson = null;
            episodeActive = true;
            armedSession = sessionName;
            activeEpisodeId = sessionName;
            Debug.Log($"[{Constants.LOG_TAG}] RigCoordinator: episode '{sessionName}' armed and anchored: {started}");
            return (started, null);
        }

        /// <summary>
        /// A rig error as a refusal: a storage error is rig_storage_low (the library's last
        /// error then carries "refusal": {"kind": "storage_low", free_bytes, required_bytes,
        /// episode_bytes, backlog_bytes, backlog_episodes, max_episode_seconds}).
        /// </summary>
        private static RigRefusal Refusal(SxRigException ex, string otherwise) =>
            RigStartGate.FromError(ex, otherwise);

        /// <summary>
        /// Raised on the main thread, once per episode, when the rig reports its running
        /// episode failed (a stream dropped out, a gap broke the law): (session, failure
        /// {cause, part, detail}). The HTTP controller stops the OQC recording on it; the
        /// refused cue has already played.
        /// </summary>
        public event Action<string, JObject>? EpisodeFailed;

        /// <summary>
        /// pod_config.json's <c>local_only</c> (default true): the app never contacts the
        /// Factory (no enrollment, sign-in, commands or uplink, whatever factory_url says) and
        /// the rig opens in local mode. Any thread.
        /// </summary>
        public bool LocalOnly => localOnly;

        /// <summary>True when this headset holds a machine credential: it is enrolled as a pod.</summary>
        public bool Enrolled => enrolled;

        /// <summary>True while the rig's floor says a collector is signed in on this headset.</summary>
        public bool CollectorSignedInNow => collectorSignedIn;

        /// <summary>
        /// Main thread: sets local_only in pod_config.json and reopens the rig with the new
        /// config when no episode is active. Returns why not, or null.
        /// </summary>
        public string? SetLocalOnly(bool value)
        {
            lock (episodeLock)
            {
                if (episodeActive || reservedSession != null) return "a rig episode is active";
            }
            try
            {
                var config = ReadPodConfig();
                config["local_only"] = value;
                WritePodConfig(config);
            }
            catch (Exception ex) when (ex is IOException || ex is JsonException || ex is InvalidDataException
                || ex is UnauthorizedAccessException)
            {
                return $"cannot write {PodConfigFileName}: {ex.Message}";
            }
            localOnly = value;
            if (opened && BuildConfig() != openedConfigJson)
            {
                CloseRig(); // the next poll opens it with the new config
            }
            Poll();
            return null;
        }

        private bool ReadLocalOnly()
        {
            try
            {
                return ReadPodConfig()["local_only"] is not JValue { Type: JTokenType.Boolean } flag || (bool)flag;
            }
            catch (Exception)
            {
                return true;
            }
        }

        /// <summary>Queues a cue for the main thread (any thread).</summary>
        public void QueueCue(RigCues.Cue cue) => cues.Enqueue(cue);

        /// <summary>
        /// Main thread, before <see cref="Enroll"/>: opens the rig (even when no rig is expected
        /// yet) or reopens it when its config changed and no episode is active, so the open
        /// config carries what sx_rig_enroll_site needs (credentials_dir, quest_apk_build_id,
        /// the serial through pod_config_path). The site travels in the enroll request, so
        /// nothing is written to pod_config.json. Returns a refusal or null.
        /// </summary>
        public RigRefusal? BeginEnrollment()
        {
            PollIdentity(force: true);
            if (libraryMissing)
            {
                return new RigRefusal("rig_library_missing", "this build has no libsx_rig.so", openErrorJson);
            }
            if (apkBuildId.Length == 0)
            {
                return new RigRefusal("rig_identity_pending",
                    "the installed APK's build id is still being computed; retry in a few seconds");
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
        /// Main thread, after <see cref="Enroll"/>: a successful enrollment turns Local only off
        /// (the headset is a Factory pod now) and reads the API token.
        /// </summary>
        public void FinishEnrollment(bool enrolledNow)
        {
            if (!enrolledNow) return;
            if (localOnly) SetLocalOnly(false);
            RefreshApiToken();
        }

        /// <summary>
        /// <c>sx_rig_enroll_site</c> on the calling (worker) thread, after
        /// <see cref="BeginEnrollment"/>: returns the answer (<c>{"pod_id", "pod", "steps"}</c>);
        /// throws <see cref="SxRigException"/> (a failed join or commission carries its steps in
        /// the error's refusal). rig_type_id comes from pod_config.json when it names one.
        /// </summary>
        public string Enroll(string factoryUrl, string authUrl, string joinCode)
        {
            string? rigTypeId = null;
            try
            {
                rigTypeId = ReadPodConfig()["rig_type_id"] is JValue { Type: JTokenType.String } type ? (string?)type : null;
            }
            catch (Exception ex) when (ex is IOException || ex is JsonException || ex is InvalidDataException)
            {
            }
            var result = SxRig.EnrollSite(PodSetupJson.Request(factoryUrl, authUrl, joinCode, rigTypeId));
            enrolled = true;
            expected = true;
            return result;
        }

        /// <summary>
        /// The HTTP API's bearer token (sx_rig_api_token), or null before enrollment or while
        /// the rig is closed. Never logged or put in a status. Any thread.
        /// </summary>
        public string? ApiToken => apiToken;
        private volatile string? apiToken;

        private void RefreshApiToken()
        {
            if (!opened || !enrolled)
            {
                apiToken = null;
                return;
            }
            try
            {
                apiToken = JToken.Parse(SxRig.ApiToken())["token"] is JValue { Type: JTokenType.String } t ? (string?)t : null;
            }
            catch (Exception ex) when (ex is SxRigException || ex is JsonException)
            {
                apiToken = null;
            }
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
        public string SignIn()
        {
            var answer = SxRig.SignIn();
            try
            {
                // {"user_code", "verification_uri_complete", "expires_in"}: remember when the code
                // lapses, for the phone companion's SignInRequest.
                if (JToken.Parse(answer) is JObject shown && shown["user_code"] is JValue { Type: JTokenType.String } code
                    && shown["expires_in"] is JValue { Type: JTokenType.Integer } seconds)
                {
                    lastSignInCode = new IssuedSignInCode((string)code!, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + (long)seconds * 1000);
                }
            }
            catch (JsonException)
            {
            }
            return answer;
        }

        /// <summary>A collector sign-in code this headset was issued, and when it lapses (Unix ms).</summary>
        public sealed class IssuedSignInCode
        {
            public string Code { get; }
            public long ExpiresAtMs { get; }

            public IssuedSignInCode(string code, long expiresAtMs)
            {
                Code = code;
                ExpiresAtMs = expiresAtMs;
            }
        }

        private volatile IssuedSignInCode? lastSignInCode;

        /// <summary>When <paramref name="code"/> lapses (Unix ms), or 0 when this run did not issue it.</summary>
        public long SignInCodeExpiresAtMs(string code) =>
            lastSignInCode is { } issued && issued.Code == code ? issued.ExpiresAtMs : 0;

        /// <summary>pod_config.json's pod_name, "" when it has none (read with the identity).</summary>
        public string PodName => podName;

        /// <summary><c>sx_rig_sign_out</c>; throws <see cref="SxRigException"/>.</summary>
        public void SignOut()
        {
            SxRig.SignOut();
            collectorSignedIn = false;
        }

        /// <summary><c>sx_rig_command</c> on the calling (worker) thread.</summary>
        public string Command(string commandJson) => SxRig.Command(commandJson);

        /// <summary>
        /// Check, on the Pod card and from the paired phone: <c>sx_rig_check</c> now, between
        /// episodes, on the calling (worker) thread (bounded about 16 s on a Factory). It opens each bound
        /// encoder and camera and requires live data in the arm's 2 s startup window, so a device that is
        /// attached but silent fails here, with the arm's words and fix, rather than at the next start. The report is
        /// kept as <c>lastCheck</c>, so the card's devices show what it found.
        /// </summary>
        public PodControlResult CheckNow()
        {
            if (!opened)
            {
                return new PodControlResult(PodControl.Check, false,
                    libraryMissing ? "this build has no libsx_rig.so" : "the rig is not open yet");
            }
            lock (episodeLock)
            {
                if (episodeActive || reservedSession != null)
                {
                    return new PodControlResult(PodControl.Check, false, "an episode is recording; check between episodes");
                }
                try
                {
                    var report = SxRig.RunCheck();
                    lastCheckJson = report;
                    var (ready, problems) = RigStatusJson.Check(report);
                    Debug.Log($"[{Constants.LOG_TAG}] RigCoordinator: check asked by the operator: ready={ready} {problems}");
                    return new PodControlResult(PodControl.Check, ready, ready ? "Ready to record" : problems);
                }
                catch (SxRigException ex)
                {
                    lastCheckJson = "{\"ready\":false,\"error\":" + ex.ErrorJson + "}";
                    return new PodControlResult(PodControl.Check, false, ErrorDetail(ex));
                }
            }
        }

        /// <summary>
        /// End collection now, on the Pod card and from the paired phone (after the operator
        /// confirmed): <c>sx_rig_command {"command": "end_recording"}</c>, sx-pod's End collection.
        /// The rig ends the open collection, tells the Factory, and sends what a collection that
        /// filled sends (End collection, reviews, finalize), so its episodes finalize and upload
        /// as at the cap. Refused while an episode records. Calling (worker) thread: it waits on
        /// the network, holding the episode lock so no start slips in while it ends.
        /// </summary>
        public PodControlResult EndCollectionNow()
        {
            if (!opened || !enrolled || localOnly)
            {
                return new PodControlResult(PodControl.EndCollection, false,
                    localOnly ? "Local only records no Factory collection" : "this headset is not on the Factory");
            }
            lock (episodeLock)
            {
                if (episodeActive || reservedSession != null)
                {
                    return new PodControlResult(PodControl.EndCollection, false, "an episode is recording; stop it first");
                }
                try
                {
                    var answer = SxRig.Command("{\"command\":\"end_recording\"}");
                    var heard = JToken.Parse(answer)["answer"]?["heard"] is JValue { Type: JTokenType.Boolean } flag && (bool)flag;
                    Debug.Log($"[{Constants.LOG_TAG}] RigCoordinator: collection ended by the operator: {answer}");
                    return new PodControlResult(PodControl.EndCollection, true, heard
                        ? "Ended; its episodes are reviewed and uploaded"
                        : "Ended; the Factory is told when it hears the headset again");
                }
                catch (SxRigException ex)
                {
                    Debug.LogWarning($"[{Constants.LOG_TAG}] RigCoordinator: end collection refused: {ex.Message}");
                    return new PodControlResult(PodControl.EndCollection, false, ErrorDetail(ex));
                }
                catch (JsonException)
                {
                    return new PodControlResult(PodControl.EndCollection, true, "Ended");
                }
            }
        }

        /// <summary>The rig's own words for a refusal: its error's detail, else the error.</summary>
        private static string ErrorDetail(SxRigException ex)
        {
            try
            {
                if (JToken.Parse(ex.ErrorJson) is JObject error && error["detail"] is JValue { Type: JTokenType.String } detail
                    && ((string?)detail)?.Length > 0)
                {
                    return (string)detail!;
                }
            }
            catch (JsonException)
            {
            }
            return ex.Error.ToString();
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
                reservedSession = null; // a stop before the rig armed cancels the arming
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
                armedSession = null;
                activeEpisodeId = null;
                return episodeReceiptJson;
            }
        }

        private void Poll()
        {
            lastPoll = Time.unscaledTime;
            configPresent = File.Exists(configPath);
            enrolled = File.Exists(Path.Combine(credentialsPath, CredentialFileName));
            if (opened && enrolled && apiToken == null) RefreshApiToken();
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
            // A rig that takes the APK's identity (quest_apk_build_id) opens once it is hashed.
            var identityReady = rigKeys == null || !rigKeys.Contains("quest_apk_build_id") || apkBuildId.Length > 0 || activity == null;
            if (Expected && !opened && !libraryMissing && EmbodimentSettled && identityReady
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
                if (rigKeys == null)
                {
                    openErrorJson = new JObject
                    {
                        ["code"] = "rig_config_keys_missing",
                        ["detail"] = "the rig's open-config key set did not load (see startupErrors.rig_config_keys)",
                    }.ToString(Formatting.None);
                    return;
                }
                // pod_config.json is validated, never partly ignored: a misspelled or foreign key
                // refuses the open, with the keys, until the file is fixed.
                var unknown = RigOpenConfig.UnknownPodConfigKeys(ReadPodConfig(), rigKeys);
                if (unknown.Length > 0)
                {
                    var problem = new JObject
                    {
                        ["code"] = "pod_config_unknown_keys",
                        ["keys"] = new JArray(unknown),
                        ["detail"] = $"{PodConfigFileName} has keys neither the app nor the rig accepts: {string.Join(", ", unknown)}",
                    };
                    startupErrors["pod_config"] = problem;
                    openErrorJson = problem.ToString(Formatting.None);
                    var signature = string.Join(",", unknown);
                    if (podConfigProblem != signature)
                    {
                        podConfigProblem = signature;
                        QueueCue(RigCues.Cue.Refused);
                        Debug.LogError($"[{Constants.LOG_TAG}] RigCoordinator: {problem["detail"]}");
                    }
                    return;
                }
                startupErrors.Remove("pod_config");
                podConfigProblem = null;
                string config;
                try
                {
                    config = BuildConfig();
                }
                catch (Exception ex) when (ex is JsonException || ex is InvalidDataException || ex is IOException
                    || ex is InvalidOperationException)
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
                RefreshApiToken();
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
            // An explicit allow-list (RigOpenConfig): the rig refuses unknown keys, and
            // pod_config.json carries app-only ones (operator_id, local_only). Local only (the
            // default) drops the site, so the rig never contacts the Factory. Apps cannot read
            // ro.serialno on the 3S: provisioning writes headset_serial into pod_config.json,
            // which the rig also reads through pod_config_path. The seal's meshes are passed
            // when installed, and when the install failed (the rig's readiness then names what
            // is missing); omitted when this APK carries none.
            var meshes = embodimentDir.Length > 0 && EmbodimentState is "ready" or "failed" ? embodimentDir : null;
            JToken? rig = File.Exists(configPath) ? JObject.Parse(File.ReadAllText(configPath)) : null;
            return RigOpenConfig.Build(ReadPodConfig(), rigKeys ?? new HashSet<string>(), stagingPath, credentialsPath, podConfigPath,
                meshes, tailscaleIp, rig, localOnly, apkBuildId).ToString(Formatting.None);
        }

        private string ReadPodConfigName()
        {
            try
            {
                return ReadPodConfig()["pod_name"] is JValue { Type: JTokenType.String } name ? (string?)name ?? "" : "";
            }
            catch (Exception ex) when (ex is IOException || ex is JsonException || ex is InvalidDataException)
            {
                return "";
            }
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

        /// <summary>
        /// Writes pod_config.json as a new file of the app's own, swapped in by rename: a
        /// pod_config.json provisioned over adb belongs to the shell user, which the app may
        /// read but not write in place, and a rename needs only the app's own directory.
        /// </summary>
        private void WritePodConfig(JObject config)
        {
            var staged = podConfigPath + ".tmp";
            File.WriteAllText(staged, config.ToString(Formatting.Indented));
            try
            {
                if (File.Exists(podConfigPath)) File.Replace(staged, podConfigPath, null);
                else File.Move(staged, podConfigPath);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException
                || ex is PlatformNotSupportedException)
            {
                // A runtime without rename-over: the old file steps aside, the new one takes its
                // name, and only then does the old one go; a failed move puts the old one back,
                // so pod_config.json (the headset serial with it) is never lost.
                var aside = podConfigPath + ".old";
                if (File.Exists(aside)) File.Delete(aside);
                File.Move(podConfigPath, aside);
                try
                {
                    File.Move(staged, podConfigPath);
                }
                catch
                {
                    File.Move(aside, podConfigPath);
                    throw;
                }
                File.Delete(aside);
            }
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
                podName = ReadPodConfigName();
                localOnly = ReadLocalOnly();
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
            if (!Expected && !opened && lastRefusal == null && startupErrors.Count == 0)
            {
                statusJson = NotExpectedStatus;
                return;
            }

            var nativeJson = opened ? nativeStatusJson : "null";
            // A start's check holds episodeLock while it asks the Factory for the rig and
            // opens each device (up to about 16 s); the main thread never waits on it, it keeps the status it has this poll.
            if (!System.Threading.Monitor.TryEnter(episodeLock)) return;
            bool episodeIsActive;
            try { episodeIsActive = episodeActive; }
            finally { System.Threading.Monitor.Exit(episodeLock); }

            var json = new StringBuilder(512);
            json.Append("{\"expected\":").Append(Expected ? "true" : "false")
                .Append(",\"configured\":").Append(configPresent ? "true" : "false")
                .Append(",\"enrolled\":").Append(enrolled ? "true" : "false")
                .Append(",\"localOnly\":").Append(localOnly ? "true" : "false")
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
                json.Append(",\"nativeState\":null,\"podId\":null,\"registeredRig\":null,\"seals\":null,\"uplink\":null,\"floor\":null")
                    .Append(",\"devices\":null,\"roles\":null,\"attempts\":null,\"factory\":null");
                // The rig's verdict on the meshes ({ready, configured, assets, missing}) comes
                // with its status; until the rig is open, say only that it has none yet.
                json.Append(",\"embodiment_assets\":{\"ready\":false,\"configured\":false}");
            }
            // The last battery/charging/network-time report the heartbeat sees, and its error.
            json.Append(",\"startupErrors\":").Append(JsonConvert.SerializeObject(startupErrors));
            json.Append(",\"lastCheck\":").Append(lastCheckJson);
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
                        // The Pod card's status screen (PodScreenBoard), kept while the rig is busy.
                        ["devices"] = status["devices"],
                        ["roles"] = status["roles"],
                        ["attempts"] = status["attempts"],
                        ["factory"] = status["factory"],
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
                    var episodeId = activeEpisodeId;
                    if (episodeId != null && failureHandledFor != episodeId
                        && RigStatusJson.EpisodeFailure(status, episodeId) is JObject failure)
                    {
                        failureHandledFor = episodeId;
                        failures.Enqueue((episodeId, failure));
                    }
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
                var state = RigStatusJson.SealState(entry.Value);
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
        private static bool? CollectorSignedIn(JObject? status) => RigStatusJson.CollectorSignedIn(status);

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
}
