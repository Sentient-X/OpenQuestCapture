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
    /// <para><b>USB.</b> Every 0.5 s a worker asks the bridge to re-enumerate; a newly opened device's fd is
    /// attached to the rig, and a vanished one is detached from the rig before the bridge
    /// closes its connection (the ABI leaves fd ownership with Kotlin until the detach).</para>
    ///
    /// <para><b>Episode.</b> POST /api/start-recording, on its worker thread: <see cref="CheckAndReserve"/>
    /// (<c>sx_rig_check</c>, which must report ready, then the session name), OQC's session starts on the
    /// main thread under <see cref="ReservedSession"/>, then <see cref="StartArmed"/>
    /// (<c>sx_rig_start_armed</c>: the rig arms and anchors the episode itself, inside OQC's
    /// recording). <see cref="StopEpisode"/>, after OQC's files are final, returns the receipt.</para>
    ///
    /// USB results are applied on the main thread, but enumeration never waits there. The episode calls, <see cref="IsExpected"/> and
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
        // Below the credentials directory: where the app's log lines go for the rig to ship.
        private const string AppLogDirectoryName = "logs";
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
        private RigUsbRefresh? usbRefresh;
        private readonly RigUsbSnapshotFlow usbFlow = new();
        private string? usbRefreshError;
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
        // The installed APK's versionCode (PackageManager), for the rig's quest_app_version; -1 unread.
        private long apkVersionCode = -1;
        // What the collector does with the headset (worn, focus, input), for the rig's presence.
        private readonly DevicePresence presence = new();
        // The app's log lines in <credentials>/logs, which the rig ships as diagnostic files.
        private AppLogFile? appLog;
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
        // What holds the rig across native or network work ("check", "start", "stop",
        // "command"), null when nothing does. Claimed and cleared under episodeLock, which is
        // released while the work runs: the main thread and the status never wait on a
        // Factory round trip, and a caller that needs the rig waits on this (Monitor.Wait).
        private string? rigWork;
        private bool episodeActive;
        private string? reservedSession;
        private string? armedSession;
        private string? episodeReceiptJson;
        private volatile RigRefusal? lastRefusal;
        private readonly RigStatusSnapshot parsedStatus = new();
        // sx_rig_status may wait up to 50 ms, so it is polled off the main thread.
        private volatile RigNativeStatusSnapshot nativeStatus = RigNativeStatusSnapshot.Empty;
        private readonly object nativeStatusGate = new();
        private long nativeStatusGeneration;
        private System.Threading.Timer? nativeStatusTimer;
        private int nativeStatusBusy;
        // sx_rig_observe_tracking every TrackingIntervalMs (sx_rig.h): the rig judges a Quest
        // start and the controller-lost watch on the newest controllers block. Off the main
        // thread; a tick that finds the last one still inside the library (a start holds the rig
        // for seconds) is skipped, never queued.
        private const int TrackingIntervalMs = 250;
        private System.Threading.Timer? trackingTimer;
        private int trackingBusy;
        private volatile bool noTrackingObserver;
        private volatile string? trackingObserveError;
        private const string DeviceReportClassName = "com.samusynth.questcamera.pod.DeviceReport";
        private const int DeviceReportIntervalMs = 15000;
        // How often worn, focus and input are looked at for a change worth reporting at once.
        private const int DevicePresencePollMs = 1000;
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
        // The signed-in collector's name from the last readable status: the seal's operator_id.
        private volatile string? collectorName;
        private volatile bool signInCuePending;
        // Seal verdicts seen so far (native-status timer thread); a change to sealed or refused
        // after the first observation queues a cue for the main thread.
        private readonly Dictionary<string, string> sealStates = new();
        private bool sealsObserved;
        // The episode a seal is writing now (a seal state "sealing"), null when none is.
        private volatile string? sealingNow;
        private readonly System.Collections.Concurrent.ConcurrentQueue<RigCues.Cue> cues = new();
        private bool floorObserved; // native-status timer thread only
        private bool collectorRestored; // native-status timer thread only

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

        /// <summary>The rig's /api/status object, built and parsed together off the main thread. Any thread.</summary>
        public string StatusJson => parsedStatus.Latest?.Json ?? NotExpectedStatus;

        /// <summary>The /api/status <c>rig</c> value when no coordinator exists.</summary>
        public static string StatusJsonOrDefault => Instance?.StatusJson ?? NotExpectedStatus;

        /// <summary>Shared read-only status tree. Parsing never runs on Unity's capture thread.</summary>
        public static JObject? ParsedStatusOrDefault => Instance?.parsedStatus.Current;

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
            // The operator's words (sx_operator_notices.json, shipped with the library). Without
            // them every notice reads as its code, never raw text; the failure is reported.
            try
            {
                var notices = Resources.Load<TextAsset>("sx_operator_notices")
                    ?? throw new InvalidOperationException("Resources/sx_operator_notices.json is missing from this build");
                OperatorNotices.Load(notices.text);
            }
            catch (InvalidOperationException ex)
            {
                RecordStartupError("operator_notices", ex);
            }
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
                    StartUsbRefresh(bridge);
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
            // After credentialsPath is the app-private one: the rig ships closed files from there.
            try
            {
                appLog = new AppLogFile(Path.Combine(credentialsPath, AppLogDirectoryName));
                Application.logMessageReceivedThreaded += OnLogLine;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                appLog = null;
                RecordStartupError("app_log", ex);
            }
            Poll();
        }

        /// <summary>Every log line, from any thread, into the app log. A line that cannot be
        /// written costs only itself.</summary>
        private void OnLogLine(string condition, string stackTrace, LogType type)
        {
            try
            {
                var withTrace = type is LogType.Error or LogType.Exception or LogType.Assert && stackTrace.Length > 0;
                appLog?.Write(DateTime.UtcNow, type.ToString().ToUpperInvariant(),
                    withTrace ? condition + "\n" + stackTrace.TrimEnd() : condition);
            }
            catch (Exception)
            {
            }
        }

        private void OnApplicationFocus(bool hasFocus) => presence.Focus(hasFocus);

        private void OnApplicationPause(bool pauseStatus) => presence.Pause(pauseStatus);

        /// <summary>The runtime's user-present (proximity) state; null when it cannot be read.</summary>
        private static bool? ReadUserPresent()
        {
            try
            {
                return OVRManager.instance != null ? OVRManager.instance.isUserPresent : OVRPlugin.userPresent;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private void RecordStartupError(string what, Exception ex)
        {
            startupErrors[what] = ex.ToString();
            QueueCue(RigCues.Cue.Refused); // the operator cannot read /api/status
            Debug.LogError($"[{Constants.LOG_TAG}] RigCoordinator: {what} failed at startup: {ex}");
        }

        /// <summary>
        /// What only the app sees before a start, for the Pod card's warnings (PodScreenBoard): the
        /// display rate and each controller's connection, tracking and charge. Main thread writes,
        /// the status builder reads.
        /// </summary>
        private volatile string headsetJson = "null";

        private void ReadHeadset()
        {
            using var trace = RigTrace.Enter("OQC.Rig.ReadHeadset");
            try
            {
                var charge = PodControls.Instance?.Controllers ?? ControllerCharge.Unknown;
                bool Tracked(OVRInput.Controller touch) =>
                    OVRInput.GetControllerPositionTracked(touch) && OVRInput.GetControllerOrientationTracked(touch);
                // The same read the panel warns on goes to the Factory (PodController.tracked).
                presence.Tracked(Tracked(OVRInput.Controller.LTouch), Tracked(OVRInput.Controller.RTouch));
                string Controller(string side, OVRInput.Controller touch, int? pct, bool connected)
                {
                    var tracked = Tracked(touch);
                    // The OS read, when there was one; else whether OVRInput sees it.
                    var linked = charge.Known ? connected : OVRInput.IsControllerConnected(touch);
                    return "{\"side\":\"" + side + "\",\"connected\":" + (linked ? "true" : "false") + ",\"tracked\":" + (tracked ? "true" : "false")
                        + ",\"percent\":" + (pct is int p ? p.ToString(CultureInfo.InvariantCulture) : "null") + "}";
                }
                headsetJson = "{\"displayHz\":" + OVRPlugin.systemDisplayFrequency.ToString("F1", CultureInfo.InvariantCulture)
                    + ",\"controllers\":[" + Controller("left", OVRInput.Controller.LTouch, charge.Left, charge.LeftConnected) + ","
                    + Controller("right", OVRInput.Controller.RTouch, charge.Right, charge.RightConnected) + "]}";
            }
            catch (Exception ex)
            {
                headsetJson = "null";
                Debug.LogWarning($"[{Constants.LOG_TAG}] RigCoordinator: headset read: {ex.Message}");
            }
        }

        private float lastHeadsetRead = float.NegativeInfinity;

        private void Update()
        {
            using var trace = RigTrace.Enter("OQC.Rig.Update");
            ConsumeUsbRefresh();
            if (Time.unscaledTime - lastHeadsetRead >= 1f)
            {
                lastHeadsetRead = Time.unscaledTime;
                ReadHeadset();
            }
            presence.Worn(ReadUserPresent());
            presence.Controllers(PodControls.Instance?.Controllers ?? ControllerCharge.Unknown);
            if (OVRInput.GetDown(OVRInput.Button.Any))
            {
                presence.KeyPressed();
                RealityLog.OVR.Attendance.Shared.Input(System.Diagnostics.Stopwatch.GetTimestamp());
            }
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
            // Check and reservation under one claim of the rig: no second start can slip
            // between a passing check and the reservation it allows. The lock itself is not
            // held across the check (up to about 16 s on a Factory).
            var collectorReady = !enrolled || localOnly || CollectorSignedIn(SafeStatus()) != false;
            RigStartFacts facts;
            lock (episodeLock)
            {
                ClaimRigLocked("check");
                facts = StartFactsLocked(collectorReady);
            }
            // Read with the rig claimed and episodeLock released: it waits on the library.
            facts.TrackingRefusalJson = facts.Opened ? TrackingRefusalNow() : null;
            RigRefusal? refusal = null;
            try
            {
                refusal = CheckReady(facts);
                // The check holds the rig for seconds (5.5 s on pod5); the collector may have
                // moved meanwhile. Judge the tracking once more, on a sample handed over now,
                // before OQC's session starts: a start the rig's tracking law refuses then never
                // starts and stops a recording (sx#4210, pod5 2026-11-15 21:17:30: the check passed,
                // sx_rig_start_armed refused inside the recording, and the stop took 7.6 s).
                if (refusal == null && facts.Opened
                    && RigStartGate.Tracking(TrackingRefusalNow()) is { } moved)
                {
                    refusal = moved;
                    Debug.LogWarning($"[{Constants.LOG_TAG}] RigCoordinator: start refused after the check: {moved.Code}: {moved.Message}");
                }
            }
            finally
            {
                lock (episodeLock)
                {
                    lastRefusal = refusal;
                    if (refusal == null)
                    {
                        reservedSession = sessionName;
                    }
                    ReleaseRigLocked();
                }
            }
            return refusal;
        }

        /// <summary>Waits (episodeLock held, released while waiting) until nothing holds the rig, then claims it for <paramref name="work"/>.</summary>
        private void ClaimRigLocked(string work)
        {
            while (rigWork != null) System.Threading.Monitor.Wait(episodeLock);
            rigWork = work;
        }

        /// <summary>The rig is free again (episodeLock held): everyone waiting looks again.</summary>
        private void ReleaseRigLocked()
        {
            rigWork = null;
            System.Threading.Monitor.PulseAll(episodeLock);
        }

        private RigStartFacts StartFactsLocked(bool collectorReady) =>
            // No "is the rig expected" test here: the caller decided that once (IsExpected), and
            // re-reading it here once let a start skip sx_rig_check and arm on no passing check.
            new()
            {
                EpisodeBusy = episodeActive || reservedSession != null,
                BusySession = armedSession ?? reservedSession,
                LibraryMissing = libraryMissing,
                Opened = opened,
                OpenErrorJson = openErrorJson,
                AwaitingPermission = awaitingPermission,
                ApkBuildIdKnown = apkBuildId.Length > 0,
                CollectorReady = collectorReady,
            };

        /// <summary>The start gate on <paramref name="facts"/>, with the rig claimed and episodeLock not held.</summary>
        private RigRefusal? CheckReady(RigStartFacts facts)
        {
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
                ClaimRigLocked("start");
                if (reservedSession != sessionName)
                {
                    ReleaseRigLocked();
                    var stopped = new RigRefusal("rig_start_failed",
                        $"the recording '{sessionName}' stopped before the rig armed");
                    lastRefusal = stopped;
                    return (null, stopped);
                }
                reservedSession = null;
            }
            // The rig is claimed, episodeLock is not held: the start asks the Factory for its
            // collection (bounded by the library) and arms without stalling anyone who reads.
            (string? start, RigRefusal? refusal) result = (null, null);
            try
            {
                result = StartArmedClaimed(sessionName, operatorId, taskAssignmentId, mode);
            }
            finally
            {
                lock (episodeLock)
                {
                    if (result.start != null)
                    {
                        episodeReceiptJson = null;
                        episodeActive = true;
                        armedSession = sessionName;
                        activeEpisodeId = sessionName;
                    }
                    if (result.refusal != null) lastRefusal = result.refusal;
                    ReleaseRigLocked();
                }
            }
            return result;
        }

        private (string? start, RigRefusal? refusal) StartArmedClaimed(string sessionName, string operatorId,
            string? taskAssignmentId, string? mode)
        {
            var episode = RigStartGate.EpisodeBody(sessionName, operatorId, apkBuildId,
                Path.Combine(dataPath, sessionName), appVersion, taskAssignmentId, mode);
            string started;
            // The start is judged by the newest controllers block (sx_rig.h), not one up to
            // 250 ms old.
            ObserveTracking();
            try
            {
                started = SxRig.StartArmed(episode.ToString(Formatting.None));
            }
            catch (SxRigException ex)
            {
                // A sign-out between the check and the start.
                if (ex.Error == SxRigError.State && enrolled && CollectorSignedIn(SafeStatus()) == false)
                {
                    return (null, new RigRefusal("collector_signed_out", RigStartGate.SignInToRecord().Message, ex.ErrorJson));
                }
                return (null, Refusal(ex, "rig_start_failed"));
            }
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
                if (rigWork != null) return "the rig is busy (" + rigWork + "); try again in a moment";
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
                    if (!episodeActive && rigWork == null)
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
        /// The operator_id this headset's next episode is sealed under
        /// (<see cref="RigStartGate.SealOperatorId"/>): the signed-in collector on an enrolled
        /// headset, null when nobody is signed in there; else the start body's, or
        /// <see cref="RigStartGate.LocalOperatorId"/>. Reads the rig's status now, falling back
        /// to the last one heard while the rig is busy. Never pod_config.json. Any thread.
        /// </summary>
        public string? SealOperatorId(string? bodyOperatorId)
        {
            var name = collectorName;
            if (enrolled && !localOnly && SafeStatus() is JObject status)
            {
                name = RigStatusJson.CollectorName(status);
            }
            return RigStartGate.SealOperatorId(enrolled, localOnly, name, bodyOperatorId);
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
            collectorName = null;
        }

        /// <summary><c>sx_rig_command</c> on the calling (worker) thread.</summary>
        public string Command(string commandJson) => SxRig.Command(commandJson);

        /// <summary>
        /// An explicit collector review from the Pod card. Its original command ID is kept
        /// across retries; the native journal owns authentication, validation and deduplication.
        /// No native or network work runs on the capture thread.
        /// </summary>
        public System.Threading.Tasks.Task<string> SubmitCollectorReviewAsync(string commandJson)
        {
            var command = JObject.Parse(commandJson);
            var expectedOperator = (string?)command["expected_operator"];
            if ((string?)command["command"] is not ("review_success" or "review_failure")
                || string.IsNullOrWhiteSpace(expectedOperator))
                throw new InvalidOperationException("An explicit episode review and its signed-in collector are required");
            return System.Threading.Tasks.Task.Run(() =>
            {
                lock (episodeLock)
                {
                    if (!opened || !enrolled || localOnly || !collectorSignedIn || collectorRestored)
                        throw new InvalidOperationException("Sign in to the Factory before reviewing episodes");
                    if ((string?)parsedStatus.Current?["floor"]?["operator"]?["subject"] != expectedOperator)
                        throw new InvalidOperationException("The signed-in collector changed; select the review again");
                    if (rigWork != null || episodeActive || reservedSession != null)
                        throw new InvalidOperationException("Finish the current recording or control before reviewing");
                    rigWork = "review";
                }
                try { return SxRig.Command(commandJson); }
                finally { lock (episodeLock) ReleaseRigLocked(); }
            });
        }

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
                ClaimRigLocked("check");
                if (episodeActive || reservedSession != null)
                {
                    ReleaseRigLocked();
                    return new PodControlResult(PodControl.Check, false, "an episode is recording; check between episodes");
                }
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
            finally
            {
                lock (episodeLock) ReleaseRigLocked();
            }
        }

        /// <summary>
        /// End collection now, on the Pod card and from the paired phone (after the operator
        /// confirmed): <c>sx_rig_command {"command": "end_recording"}</c>, sx-pod's End collection.
        /// The rig ends the open collection and tells the Factory. Each episode still needs
        /// its collector's explicit review before finalization and upload. Refused while an episode records. Calling (worker) thread: it waits on
        /// the network with the rig claimed, so no start slips in while it ends, and without the
        /// episode lock, so nothing that only reads waits on the Factory.
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
                ClaimRigLocked("command");
                if (episodeActive || reservedSession != null)
                {
                    ReleaseRigLocked();
                    return new PodControlResult(PodControl.EndCollection, false, "an episode is recording; stop it first");
                }
            }
            try
            {
                var answer = SxRig.Command("{\"command\":\"end_recording\"}");
                var heard = JToken.Parse(answer)["answer"]?["heard"] is JValue { Type: JTokenType.Boolean } flag && (bool)flag;
                Debug.Log($"[{Constants.LOG_TAG}] RigCoordinator: collection ended by the operator: {answer}");
                return new PodControlResult(PodControl.EndCollection, true, heard
                    ? "Ended; review its episodes before they can finalize and upload"
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
            finally
            {
                lock (episodeLock) ReleaseRigLocked();
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

        // Set once the library is found to lack sx_rig_request_stop.
        private volatile bool noEarlyStop;

        /// <summary>
        /// Capture the native stop boundary before the headset drains its files. Keeps the
        /// episode active until StopEpisode finalizes it, so sealing cannot read open files.
        /// Retries retain the first native cutoff. Any thread. Never throws.
        /// </summary>
        public void RequestStopEpisode()
        {
            if (noEarlyStop) return;
            lock (episodeLock)
            {
                while (rigWork != null) System.Threading.Monitor.Wait(episodeLock);
                if (!episodeActive) return;
                ClaimRigLocked("request_stop");
            }
            RequestStopClaimed();
        }

        /// <summary>
        /// <see cref="RequestStopEpisode"/> for Unity's main thread: never waits on a check or a
        /// start holding the rig (seconds), and answers false then; that stop's
        /// <see cref="StopEpisode"/> waits for the arming and its sx_rig_stop ends capture.
        /// True when the cutoff was taken or nothing needed one. Never throws.
        /// </summary>
        public bool TryRequestStopEpisode()
        {
            if (noEarlyStop) return true;
            lock (episodeLock)
            {
                if (rigWork != null) return false;
                if (!episodeActive) return true;
                ClaimRigLocked("request_stop");
            }
            RequestStopClaimed();
            return true;
        }

        private void RequestStopClaimed()
        {
            try
            {
                SxRig.RequestStop();
            }
            catch (SxRigLibraryMissingException ex)
            {
                // A libsx_rig without sx_rig_request_stop (sx main 9c0b8959dd, this tree's
                // v67 library) has no early cutoff: its stop alone ends capture, as before.
                noEarlyStop = true;
                Debug.LogWarning($"[{Constants.LOG_TAG}] RigCoordinator: this libsx_rig has no stop cutoff; the stop ends capture ({ex.Message})");
            }
            catch (SxRigException ex)
            {
                // A clock failure poisons the native attempt; still let final stop drain it.
                Debug.LogError($"[{Constants.LOG_TAG}] RigCoordinator: stop cutoff: {ex.Message}");
            }
            finally
            {
                lock (episodeLock) ReleaseRigLocked();
            }
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
                // A check or start under way finishes first, as when it held the lock: then a
                // reservation it made is cancelled, and an episode it armed is stopped.
                while (rigWork != null) System.Threading.Monitor.Wait(episodeLock);
                reservedSession = null; // a stop before the rig armed cancels the arming
                if (!episodeActive) return episodeReceiptJson;
                ClaimRigLocked("stop");
            }
            string receipt;
            try
            {
                receipt = SxRig.Stop();
            }
            catch (SxRigException ex)
            {
                // The library keeps a stop whose receipt it could not write and finishes it on
                // the next stop or start; this one reports the error.
                Debug.LogError($"[{Constants.LOG_TAG}] RigCoordinator: {ex.Message}");
                receipt = "{\"error\":" + ex.ErrorJson + "}";
            }
            lock (episodeLock)
            {
                episodeReceiptJson = receipt;
                episodeActive = false;
                armedSession = null;
                activeEpisodeId = null;
                ReleaseRigLocked();
                return episodeReceiptJson;
            }
        }

        private void Poll()
        {
            using var trace = RigTrace.Enter("OQC.Rig.Poll");
            var began = System.Diagnostics.Stopwatch.GetTimestamp();
            lastPoll = Time.unscaledTime;
            configPresent = File.Exists(configPath);
            enrolled = File.Exists(Path.Combine(credentialsPath, CredentialFileName));
            if (opened && enrolled && apiToken == null) RefreshApiToken();
            PollIdentity(force: false);
            ReportMainThreadTime("identity", began);

            awaitingPermission = snapshot.awaitingPermission ?? Array.Empty<string>();
            expected = Expected;
            // A rig that takes the APK's identity (quest_apk_build_id) opens once it is hashed.
            var identityReady = rigKeys == null || !rigKeys.Contains("quest_apk_build_id") || apkBuildId.Length > 0 || activity == null;
            if (Expected && !opened && !libraryMissing && EmbodimentSettled && identityReady
                && Time.unscaledTime - lastOpenAttempt >= OpenRetrySeconds)
            {
                TryOpen();
            }
            began = System.Diagnostics.Stopwatch.GetTimestamp();
            usbFlow.ApplyIfNeeded(opened, SyncAttachments);
            if (usbRefresh != null) usbFlow.Request(usbRefresh);
            ReportMainThreadTime("attachments", began);
            RefreshStatus();
        }

        private void StartUsbRefresh(AndroidJavaObject usbBridge)
        {
            // Android's UsbManager uses binder and may wait behind system USB work. Keep
            // that wait off FixedUpdate's thread, while all SDK tracking calls stay there.
            usbRefresh = new RigUsbRefresh(() =>
            {
                if (AndroidJNI.AttachCurrentThread() != 0)
                    throw new InvalidOperationException("cannot attach USB refresh thread to the Android VM");
                try { return usbBridge.Call<string>("refresh"); }
                finally { AndroidJNI.DetachCurrentThread(); }
            }, () =>
            {
                try
                {
                    if (AndroidJNI.AttachCurrentThread() != 0)
                        throw new InvalidOperationException("cannot attach USB close thread to the Android VM");
                    try { usbBridge.Call("close"); }
                    finally
                    {
                        try { usbBridge.Dispose(); }
                        finally { AndroidJNI.DetachCurrentThread(); }
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[{Constants.LOG_TAG}] RigCoordinator: USB close failed: {ex.Message}");
                }
            }, key =>
            {
                if (AndroidJNI.AttachCurrentThread() != 0)
                    throw new InvalidOperationException("cannot attach USB release thread to the Android VM");
                try { usbBridge.Call("release", key); }
                finally { AndroidJNI.DetachCurrentThread(); }
            });
        }

        private void ConsumeUsbRefresh()
        {
            var result = usbRefresh?.Take();
            if (result == null) return;
            try
            {
                if (result.Error != null) throw result.Error;
                snapshot = JsonUtility.FromJson<UsbSnapshot>(result.Json!) ?? new UsbSnapshot();
                usbFlow.SnapshotChanged();
                usbRefreshError = null;
                awaitingPermission = snapshot.awaitingPermission ?? Array.Empty<string>();
                expected = Expected;
                usbFlow.ApplyIfNeeded(opened, SyncAttachments);
                RefreshStatus();
            }
            catch (Exception ex)
            {
                if (usbRefreshError != ex.Message)
                    Debug.LogError($"[{Constants.LOG_TAG}] RigCoordinator: USB refresh failed: {ex.Message}");
                usbRefreshError = ex.Message;
            }
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
                trackingTimer ??= new System.Threading.Timer(_ => ObserveTrackingTick(), null, 0, TrackingIntervalMs);
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
            // pod_config.json carries the app-only local_only (and the retired operator_id, never read). Local only (the
            // default) drops the site, so the rig never contacts the Factory. Apps cannot read
            // ro.serialno on the 3S: provisioning writes headset_serial into pod_config.json,
            // which the rig also reads through pod_config_path. The seal's meshes are passed
            // when installed, and when the install failed (the rig's readiness then names what
            // is missing); omitted when this APK carries none.
            var meshes = embodimentDir.Length > 0 && EmbodimentState is "ready" or "failed" ? embodimentDir : null;
            JToken? rig = File.Exists(configPath) ? JObject.Parse(File.ReadAllText(configPath)) : null;
            var questApp = apkVersionCode >= 0 ? RigOpenConfig.QuestAppVersion(appVersion, apkVersionCode) : null;
            return RigOpenConfig.Build(ReadPodConfig(), rigKeys ?? new HashSet<string>(), stagingPath, credentialsPath, podConfigPath,
                meshes, tailscaleIp, rig, localOnly, apkBuildId, questApp).ToString(Formatting.None);
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
            using var trace = RigTrace.Enter("OQC.Rig.PollIdentity");
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
                if (apkVersionCode < 0)
                {
                    using var packages = activity.Call<AndroidJavaObject>("getPackageManager");
                    using var info = packages.Call<AndroidJavaObject>("getPackageInfo",
                        activity.Call<string>("getPackageName"), 0);
                    apkVersionCode = info.Call<long>("getLongVersionCode");
                }
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
            InvalidateStatus();
            openedConfigJson = "";
            attached.Clear();
            attachErrors.Clear();
            usbFlow.RigClosed();
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
            // The retained snapshot remains useful for status and native reopen, but these
            // one-shot ownership instructions now belong to the worker request queue.
            snapshot.pendingRelease = Array.Empty<string>();
        }

        private void Release(string key) => usbFlow.QueueRelease(key);

        private void InvalidateStatus()
        {
            // Only pointer swaps share this gate with the native reader; never a native call,
            // parse, serializer or main-thread wait for outstanding status work.
            lock (nativeStatusGate)
            {
                nativeStatusGeneration++;
                nativeStatus = RigNativeStatusSnapshot.Empty;
            }
            parsedStatus.Invalidate();
        }

        private void RefreshStatus()
        {
            using var trace = RigTrace.Enter("OQC.Rig.StatusSnapshot");
            var began = System.Diagnostics.Stopwatch.GetTimestamp();
            if (!Expected && !opened && lastRefusal == null && startupErrors.Count == 0)
            {
                parsedStatus.Offer(NotExpectedStatus);
                return;
            }

            var native = opened ? nativeStatus : RigNativeStatusSnapshot.Empty;
            // episodeLock is held only briefly now (the rig's work runs under rigWork), but the
            // main thread still never waits on it: it keeps the status it has this poll.
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
            json.Append("],\"refresh\":").Append(usbRefresh?.StatsJson() ?? "null")
                .Append(",\"refreshError\":").Append(usbRefreshError == null ? "null" : JsonString(usbRefreshError))
                .Append(",\"attachErrors\":{");
            first = true;
            foreach (var entry in attachErrors)
            {
                if (!first) json.Append(',');
                first = false;
                json.Append('"').Append(RigJson.Escape(entry.Key)).Append("\":").Append(entry.Value);
            }
            json.Append("}}");
            // Snapshot only main-owned small state. Large JSON strings are immutable references;
            // the closure must never read this coordinator or its mutable collections later.
            var prefix = json.ToString();
            // These tokens are created once and replaced/removed, never mutated in place.
            var errors = new Dictionary<string, JToken>(startupErrors);
            var check = lastCheckJson;
            var headset = headsetJson;
            var report = deviceReportJson;
            var reportError = deviceReportError;
            var embodiment = embodimentStateJson;
            var refusal = lastRefusal;
            var trackingError = trackingObserveError;
            parsedStatus.Offer(() =>
            {
                using var assemblyTrace = RigTrace.Enter("OQC.Rig.StatusAssemblyWorker");
                var result = new StringBuilder(prefix);
                var summary = native.SummaryJson;
                if (summary.Length > 2)
                {
                    result.Append(',').Append(summary, 1, summary.Length - 2);
                }
                else
                {
                    result.Append(",\"nativeState\":null,\"podId\":null,\"registeredRig\":null,\"seals\":null,\"uplink\":null,\"floor\":null")
                        .Append(",\"devices\":null,\"roles\":null,\"attempts\":null,\"factory\":null,\"tracking\":null")
                        .Append(",\"embodiment_assets\":{\"ready\":false,\"configured\":false}");
                }
                result.Append(",\"startupErrors\":").Append(JsonConvert.SerializeObject(errors))
                    .Append(",\"lastCheck\":").Append(check)
                    .Append(",\"headset\":").Append(headset)
                    .Append(",\"deviceReport\":").Append(report)
                    .Append(",\"deviceReportError\":").Append(reportError is string error ? JsonString(error) : "null")
                    .Append(",\"embodimentInstall\":").Append(embodiment)
                    .Append(",\"native\":").Append(native.Json)
                    .Append(",\"lastRefusal\":").Append(refusal?.ToJson() ?? "null")
                    .Append(",\"trackingObserveError\":").Append(trackingError ?? "null")
                    .Append('}');
                return result.ToString();
            });
            ReportMainThreadTime("status snapshot", began);
        }

        private long lastSlowMainThreadReport;
        private void ReportMainThreadTime(string stage, long began)
        {
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            var elapsedMs = (now - began) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            // Only slow calls are reported, at most once per second. This is diagnostic;
            // it never changes source timestamps or the seal's timing laws.
            if (elapsedMs < 20 || now - lastSlowMainThreadReport < System.Diagnostics.Stopwatch.Frequency) return;
            lastSlowMainThreadReport = now;
            Debug.LogWarning($"[{Constants.LOG_TAG}] RigCoordinator: main-thread stage={stage} elapsed_ms={elapsedMs:F2} status_chars={StatusJson.Length}");
        }

        /// <summary>
        /// Reports battery, charging and the network-time fix (Kotlin DeviceReport), with whether the
        /// headset is worn, whether this app has its focus and the collector's input totals
        /// (<see cref="DevicePresence"/>), to the rig on a background thread: at once when worn,
        /// focus or input change (looked at every second), and at least every 15 s; Kotlin's part
        /// is read every 15 s. The heartbeat trusts a report for 60 s, so a stalled app reads not
        /// ready instead of stale-ready. A report Kotlin cannot make (no battery state) is skipped,
        /// never invented.
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
                    string? battery = null;
                    var batteryAt = DateTime.MinValue;
                    var sent = "";
                    var sentAt = DateTime.MinValue;
                    var wait = 0; // the first report goes at once
                    while (!deviceReportStop.WaitOne(wait))
                    {
                        wait = DevicePresencePollMs;
                        if (!opened) continue;
                        try
                        {
                            var now = DateTime.UtcNow;
                            if (battery == null || now - batteryAt >= TimeSpan.FromMilliseconds(DeviceReportIntervalMs))
                            {
                                battery = report.CallStatic<string>("json", context);
                                batteryAt = now;
                            }
                            if (string.IsNullOrEmpty(battery)) continue;
                            var json = presence.Report(battery!);
                            if (json == null) continue;
                            if (json == sent && now - sentAt < TimeSpan.FromMilliseconds(DeviceReportIntervalMs)) continue;
                            SxRig.ReportDevice(json);
                            sent = json;
                            sentAt = now;
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

        private void ObserveTrackingTick()
        {
            if (!opened || noTrackingObserver || System.Threading.Interlocked.Exchange(ref trackingBusy, 1) == 1) return;
            try
            {
                ObserveTracking();
            }
            finally
            {
                System.Threading.Volatile.Write(ref trackingBusy, 0);
            }
        }

        /// <summary>
        /// Hands the rig the app's controllers block now (any thread but Unity's: the library
        /// may hold the rig for a start). True when the rig took it.
        /// </summary>
        private bool ObserveTracking()
        {
            if (!opened || noTrackingObserver) return false;
            try
            {
                SxRig.ObserveTracking(HeadTrackingMonitor.ControllersJson(""));
                trackingObserveError = null;
                return true;
            }
            catch (SxRigLibraryMissingException ex)
            {
                // A libsx_rig before sx#4210 judges no tracking: say so once and stop asking.
                noTrackingObserver = true;
                Debug.LogWarning($"[{Constants.LOG_TAG}] RigCoordinator: this libsx_rig takes no tracking ({ex.Message})");
            }
            catch (SxRigException ex)
            {
                if (trackingObserveError != ex.ErrorJson)
                {
                    Debug.LogWarning($"[{Constants.LOG_TAG}] RigCoordinator: sx_rig_observe_tracking: {ex.ErrorJson}");
                }
                trackingObserveError = ex.ErrorJson;
            }
            return false;
        }

        /// <summary>
        /// The rig's <c>tracking.start_refusal</c> on a block handed over just now, as JSON; null
        /// when it lets a start go ahead or cannot say (the rig busy, an older library).
        /// </summary>
        private string? TrackingRefusalNow()
        {
            if (!ObserveTracking()) return null;
            // sx_rig_status answers {"state": "busy"} when another call holds the rig for 50 ms
            // (the device report, the status worker); that is no verdict, so ask again for up to
            // a second rather than let a start through unjudged.
            for (var attempt = 0; attempt < 25; attempt++)
            {
                try
                {
                    if (JToken.Parse(SxRig.Status()) is not JObject status) return null;
                    if ((string?)status["state"] == "busy")
                    {
                        System.Threading.Thread.Sleep(40);
                        continue;
                    }
                    return status["tracking"] is JObject tracking && tracking["start_refusal"] is JObject refusal
                        ? refusal.ToString(Formatting.None) : null;
                }
                catch (Exception ex) when (ex is SxRigException || ex is JsonException)
                {
                    return null;
                }
            }
            Debug.LogWarning($"[{Constants.LOG_TAG}] RigCoordinator: the rig stayed busy for 1 s; the start's tracking is judged by sx_rig_start_armed alone");
            return null;
        }

        private void PollNativeStatus()
        {
            if (!opened || System.Threading.Interlocked.Exchange(ref nativeStatusBusy, 1) == 1) return;
            using var trace = RigTrace.Enter("OQC.Rig.NativeStatusWorker");
            long generation;
            lock (nativeStatusGate) generation = nativeStatusGeneration;
            try
            {
                var observation = RigNativeStatusSnapshot.Read(SxRig.Status(), nativeStatus, out var readable);
                lock (nativeStatusGate)
                {
                    if (!opened || generation != nativeStatusGeneration) return;
                    nativeStatus = observation;
                }
                if (readable is JObject status)
                {
                    var signedIn = CollectorSignedIn(status) == true;
                    // Only a transition seen while running cues; a sign-in already held at
                    // launch does not.
                    if (signedIn && !collectorSignedIn && floorObserved)
                    {
                        signInCuePending = true;
                    }
                    // A sign-in kept across the restart: logged, not cued (nobody signed in now).
                    var restored = RigStatusJson.CollectorRestored(status);
                    if (restored && !collectorRestored)
                    {
                        Debug.Log($"[{Constants.LOG_TAG}] RigCoordinator: collector sign-in restored from before this launch");
                    }
                    else if (!restored && collectorRestored)
                    {
                        Debug.Log($"[{Constants.LOG_TAG}] RigCoordinator: restored collector sign-in {(signedIn ? "confirmed by the Factory" : "rejected; sign in again")}");
                    }
                    collectorRestored = restored;
                    collectorSignedIn = signedIn;
                    collectorName = RigStatusJson.CollectorName(status);
                    floorObserved = true;
                    QueueSealCues(status["seals"] as JObject);
                    sealingNow = Sealing(status["seals"] as JObject);
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
                lock (nativeStatusGate)
                {
                    if (opened && generation == nativeStatusGeneration)
                        nativeStatus = RigNativeStatusSnapshot.Error(ex.ErrorJson, nativeStatus);
                }
            }
            finally
            {
                System.Threading.Volatile.Write(ref nativeStatusBusy, 0);
            }
        }

        /// <summary>The episode whose seal is being written now (state "sealing"); null when none is.</summary>
        private static string? Sealing(JObject? seals)
        {
            if (seals == null) return null;
            foreach (var entry in seals)
            {
                if (entry.Value is JObject seal && (string?)(seal["state"] as JValue) == "sealing") return entry.Key;
            }
            return null;
        }

        /// <summary>
        /// What the rig is doing that a relaunch would cut short (any thread): an episode
        /// recording, a reservation or an arming, the rig held for a check, start, stop or
        /// command, or a seal being written. Null when nothing is.
        /// </summary>
        public string? BusyForRelaunch()
        {
            // Never wait on the lock (the main thread asks): a held lock is work in progress.
            if (!System.Threading.Monitor.TryEnter(episodeLock)) return "the rig is busy";
            try
            {
                if (episodeActive) return "episode " + (armedSession ?? "?") + " is recording";
                if (reservedSession != null) return "episode " + reservedSession + " is arming";
                if (rigWork != null) return "the rig is busy (" + rigWork + ")";
            }
            finally
            {
                System.Threading.Monitor.Exit(episodeLock);
            }
            return sealingNow is string sealing ? "the seal of " + sealing + " is being written" : null;
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

        private void OnDestroy() => ShutDown();

        private bool shutDown;

        /// <summary>
        /// Main thread: what a quit does to the rig (the episode stopped, USB detached, the rig
        /// closed, logs flushed), once. A self relaunch (FrozenHeadRecovery) calls it before the
        /// process ends, since OnDestroy never runs then.
        /// </summary>
        public void ShutDown()
        {
            if (!ReferenceEquals(Instance, this) || shutDown) return;
            shutDown = true;
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
            trackingTimer?.Dispose();
            trackingTimer = null;
            deviceReportStop.Set();
            deviceReportThread?.Join(1000);
            if (opened)
            {
                SxRig.Close();
                opened = false;
            }
            InvalidateStatus();
            parsedStatus.Dispose();
            Application.logMessageReceivedThreaded -= OnLogLine;
            appLog?.Dispose();
            appLog = null;
            if (usbRefresh != null)
            {
                // Its worker owns close/dispose after any outstanding refresh completes.
                // The native owners above have already detached, so no fd is closed early.
                usbRefresh.Dispose();
                usbRefresh = null;
            }
            else
            {
                try { bridge?.Call("close"); }
                catch (Exception) { }
                bridge?.Dispose();
            }
            bridge = null;
            activity?.Dispose();
            activity = null;
            // Instance is kept: a RecordingManager destroy-stop that runs after this still
            // reads the episode's receipt through StopEpisode.
        }
    }
}
