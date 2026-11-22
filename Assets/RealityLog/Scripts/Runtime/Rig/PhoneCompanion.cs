# nullable enable

using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using RealityLog.Camera;
using RealityLog.Common;
#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine.Android;
#endif

namespace RealityLog.Rig
{
    /// <summary>
    /// The headset's phone companion (spec: ~/p3/tmp/headset-companion-spec.md). <b>Pair phone</b>
    /// on the Pod card reads the phone's sxc1: QR through the passthrough camera (the session the
    /// recorder keeps open; never while recording), then the companion library (CompanionLink) pairs over
    /// Bluetooth LE with that phone, whose operator confirms on the phone. Once paired, it
    /// reconnects on every launch, and the status screen (HeadsetStatusMap) is pushed on every
    /// change and at least every 5 s. The headset signs nobody in: on Accept the phone opens the
    /// pod's Factory shift with its own person's key, so that person is the collector the rig
    /// seals and the status names; with no phone paired the pod records unattributed.
    /// </summary>
    public sealed class PhoneCompanion : MonoBehaviour
    {
        private const float ScanTimeoutSeconds = 60f;
        private const float StatusEverySeconds = 5f;
        private const float StatusCheckSeconds = 0.5f;
        private const string ScanPermission = "android.permission.BLUETOOTH_SCAN";
        private const string ConnectPermission = "android.permission.BLUETOOTH_CONNECT";

        public static PhoneCompanion? Instance { get; private set; }

        /// <summary>True while the passthrough camera looks for the phone's QR.</summary>
        public bool Scanning => scanning;

        /// <summary>Where the link stands, as the Pod card shows it.</summary>
        public CompanionPhase Phase { get; private set; } = CompanionPhase.Unavailable;
        public string PhoneName { get; private set; } = "";
        public string OperatorName { get; private set; } = "";
        /// <summary>The last failure's words, "" when none.</summary>
        public string Error { get; private set; } = "";
        /// <summary>The pod's name for the Pod card's words ("Pair with ..."): pod_config's pod_name, else the pod id.</summary>
        public string PodLabel => PodName().Length > 0 ? PodName() : PodId();

        private CompanionLink? link;
        private readonly CompanionLinkTracker tracker = new();
        private readonly PhoneLinkReport phoneLink = new();
        // A phase this side holds over the link's (scanning, a refusal, a denied permission).
        private CompanionPhase? local;
        private bool scanning;
        private float scanStarted;
        private VideoRecorderSurfaceProvider? scanCamera;
        private bool wasConnected;
        private readonly StreamLiveness liveness = new();
        private string lastStatusPrint = "";
        private float lastStatusSent = float.NegativeInfinity;
        private float lastStatusCheck = float.NegativeInfinity;
        private bool asking; // waiting for a Bluetooth permission answer
        // The rig status as last parsed, and the text it was parsed from: parsed again only
        // when the text changes, never every frame (main thread only).

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (Instance is not null) return;
            var go = new GameObject(nameof(PhoneCompanion));
            Instance = go.AddComponent<PhoneCompanion>();
            DontDestroyOnLoad(go);
        }

        private void Start()
        {
            link = CompanionLink.Open();
            if (link == null) return;
            if (PodControls.Instance is { } controls) controls.Finished += OnControlFinished;
            tracker.Reset(link.Paired);
            // Reconnect to the paired phone on launch, once Bluetooth is allowed.
            if (link.Paired) WithPermissions(() => Safe(link.Connect));
        }

        private void OnDestroy()
        {
            StopScan();
            if (PodControls.Instance is { } controls) controls.Finished -= OnControlFinished;
            link?.Dispose();
            link = null;
        }

        /// <summary>The Pod card's first Phone pill: Pair phone, or Cancel while pairing.</summary>
        public void PairOrCancel()
        {
            if (link == null) return;
            if (scanning || tracker.Pairing)
            {
                Cancel();
                return;
            }
            if (FindFirstObjectByType<RecordingManager>() is { IsRecording: true })
            {
                Refuse("stop the recording first");
                return;
            }
            if (PodSetupController.Instance is { Busy: true })
            {
                Refuse("wait for the pod setup step to finish");
                return;
            }
            WithPermissions(StartScan);
        }

        /// <summary>Stops a scan or a pairing in progress; a phone already paired is looked for again.</summary>
        public void Cancel()
        {
            StopScan();
            local = null;
            Error = "";
            if (link == null || !tracker.Pairing) return;
            Safe(link.Disconnect);
            tracker.Reset(link.Paired);
            if (link.Paired) Safe(link.Connect);
        }

        /// <summary>Forgets the paired phone: it can no longer connect until paired again from a fresh QR.</summary>
        public void Forget()
        {
            StopScan();
            local = null;
            Error = "";
            if (link == null) return;
            Safe(link.Forget);
            tracker.Reset(false);
        }

        private void StartScan()
        {
            if (FindFirstObjectByType<RecordingManager>() is { IsRecording: true })
            {
                Refuse("stop the recording first");
                return;
            }
            scanCamera = PodSetupController.PassthroughQrCamera();
            if (scanCamera == null || !scanCamera.StartQrScan())
            {
                scanCamera = null;
                Refuse("the passthrough camera is not available");
                return;
            }
            scanning = true;
            scanStarted = Time.unscaledTime;
            local = CompanionPhase.Scanning;
            Error = "";
        }

        /// <summary>Ends the scan on every path; the camera session is the recorder's and stays open.</summary>
        private void StopScan()
        {
            if (!scanning) return;
            scanning = false;
            scanCamera?.StopQrScan();
            scanCamera = null;
            if (local == CompanionPhase.Scanning) local = null;
        }

        private static bool Safe(Action call) => Safe(() => { call(); return true; }, false);

        private void Update()
        {
            if (link == null)
            {
                Phase = CompanionPhase.Unavailable;
                return;
            }
            PollScan();
            ReadLink();
            ReportPhoneLink();
            // The session's work follows the link itself, whatever this side shows over it.
            if (tracker.Phase != CompanionPhase.Connected)
            {
                wasConnected = false;
                return;
            }
            if (!wasConnected)
            {
                // A new session: the phone gets the status at once.
                wasConnected = true;
                lastStatusPrint = "";
            }
            PushStatus();
        }

        private void PollScan()
        {
            if (!scanning || scanCamera == null) return;
            if (FindFirstObjectByType<RecordingManager>() is { IsRecording: true })
            {
                StopScan();
                Refuse("a recording started");
                return;
            }
            if (scanCamera.QrResult() is string text)
            {
                StopScan();
                if (!CompanionCard.IsOffer(text))
                {
                    Refuse(text.TrimStart().StartsWith("{", StringComparison.Ordinal)
                        ? "that is the Console's setup code, not a phone's"
                        : "that QR is not a phone's pairing code");
                    return;
                }
                RigCoordinator.Instance?.QueueCue(RigCues.Cue.Sealed, "phone pairing code scanned");
                local = null;
                tracker.PairStarted();
                // A refused code (expired, malformed) arrives as an error, then the tracker shows it.
                if (!Safe(() => link!.Pair(text, PodId(), PodName(), HeadsetId()), false)) tracker.OnState("idle", link!.Paired);
            }
            else if (Time.unscaledTime - scanStarted > ScanTimeoutSeconds)
            {
                StopScan();
                Refuse("no phone QR seen in 60 s");
            }
        }

        private void ReadLink()
        {
            var before = tracker.Phase;
            Safe(() => link!.Drain(tracker, ControlAsked));
            PhoneName = link!.PhoneName;
            OperatorName = link.OperatorName;
            // The link's own progress replaces a refusal this side held.
            if (local is CompanionPhase.Failed or CompanionPhase.PermissionDenied && (tracker.Pairing || tracker.Phase != before)) local = null;
            Phase = local ?? tracker.Phase;
            Error = local != null ? Error : tracker.Error;
        }

        /// <summary>
        /// The pairing and the link to the rig (sx_rig_phone_link), first with the pairing restored at
        /// launch, then on each change: its heartbeat's paired_phone is what lets the Factory credit
        /// the phone-opened shift's person. Off the main thread, since the call waits on the rig's
        /// lock; the id is never logged.
        /// </summary>
        private void ReportPhoneLink()
        {
            var json = phoneLink.Due(link!.Paired, link.CollectorId, tracker.Phase == CompanionPhase.Connected, Time.unscaledTimeAsDouble);
            if (json == null) return;
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    SxRig.PhoneLink(json);
                    phoneLink.Done(true);
                }
                catch (Exception ex)
                {
                    if (phoneLink.Done(false)) Debug.LogWarning($"[{Constants.LOG_TAG}] PhoneCompanion: the rig did not take the phone link (tried again each second): {ex.Message}");
                }
            });
        }

        /// <summary>The phone's Check, Start or Stop: the Pod card's own control.</summary>
        private void ControlAsked(PodControl control)
        {
            Debug.Log($"[{Constants.LOG_TAG}] PhoneCompanion: the phone asked {control}");
            if (PodControls.Instance is { } controls)
            {
                controls.Run(control);
                return;
            }
            Safe(() => link!.SendCommandResult(new PodControlResult(control, false, "the headset's controls are not running")), false);
        }

        /// <summary>Every control's result goes to the phone, whichever screen it was pressed on.</summary>
        private void OnControlFinished(PodControlResult result)
        {
            if (link == null || tracker.Phase != CompanionPhase.Connected) return;
            Safe(() => link.SendCommandResult(result), false);
            lastStatusPrint = ""; // a check or a start changes the screen: push it now
            lastStatusCheck = float.NegativeInfinity;
        }

        /// <summary>The status screen to the phone: on every change, and at least every 5 s.</summary>
        private void PushStatus()
        {
            var now = Time.unscaledTime;
            if (now - lastStatusCheck < StatusCheckSeconds) return;
            lastStatusCheck = now;
            var status = Status();
            var screen = PodScreenBoard.Build(status, PodStartLog.Shared.Last, PodStartLog.Shared.Succeeded, liveness, DateTime.UtcNow);
            var print = new HeadsetStatusFingerprint();
            var podName = PodName();
            var controllers = PodControls.Instance?.Controllers ?? ControllerCharge.Unknown;
            HeadsetStatusMap.Write(screen, status, podName, controllers, print);
            if (print.Text == lastStatusPrint && now - lastStatusSent < StatusEverySeconds) return;
            // The library keeps the latest and repeats it every 5 s; this side resends at 5 s too so
            // the link's age is fresh. A send with no session open is still the one sent on reconnect.
            Safe(() => link!.SendStatus(writer => HeadsetStatusMap.Write(screen, status, podName, controllers, writer)), false);
            lastStatusPrint = print.Text;
            lastStatusSent = now;
        }

        // ---- Permissions ----

        /// <summary>Runs <paramref name="then"/> once BLUETOOTH_SCAN and BLUETOOTH_CONNECT are granted; a denial shows on the card.</summary>
        private void WithPermissions(Action then)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (Permission.HasUserAuthorizedPermission(ScanPermission) && Permission.HasUserAuthorizedPermission(ConnectPermission))
            {
                then();
                return;
            }
            if (asking) return;
            asking = true;
            var callbacks = new PermissionCallbacks();
            void Answered(string _)
            {
                var scan = Permission.HasUserAuthorizedPermission(ScanPermission);
                var connect = Permission.HasUserAuthorizedPermission(ConnectPermission);
                if (scan && connect)
                {
                    asking = false;
                    if (local == CompanionPhase.PermissionDenied) local = null;
                    then();
                }
            }
            void Denied(string permission)
            {
                asking = false;
                local = CompanionPhase.PermissionDenied;
                Debug.LogWarning($"[{Constants.LOG_TAG}] PhoneCompanion: {permission} denied");
            }
            callbacks.PermissionGranted += Answered;
            callbacks.PermissionDenied += Denied;
            callbacks.PermissionDeniedAndDontAskAgain += Denied;
            Permission.RequestUserPermissions(new[] { ScanPermission, ConnectPermission }, callbacks);
#else
            then();
#endif
        }

        // ---- Helpers ----

        private void Refuse(string reason)
        {
            local = CompanionPhase.Failed;
            Error = reason;
            Debug.LogWarning($"[{Constants.LOG_TAG}] PhoneCompanion: {reason}");
            RigCoordinator.Instance?.QueueCue(RigCues.Cue.Refused, "phone: " + reason);
        }

        /// <summary>
        /// The shared status snapshot, parsed off the capture thread. Readers never change it.
        /// </summary>
        private JObject? Status() => RigCoordinator.ParsedStatusOrDefault;

        private string PodId()
        {
            var status = Status();
            return status?["enrolled"] is JValue { Type: JTokenType.Boolean } enrolled && (bool)enrolled
                && status["podId"] is JValue { Type: JTokenType.String } id ? (string?)id ?? "" : "";
        }

        private static string PodName() => RigCoordinator.Instance?.PodName ?? "";

        private string HeadsetId() =>
            Status()?["headsetSerial"] is JValue { Type: JTokenType.String } serial ? (string?)serial ?? "" : "";

        /// <summary>A library call; a Java exception is logged and reads as the fallback, never thrown into Update.</summary>
        private static T Safe<T>(Func<T> call, T fallback)
        {
            try
            {
                return call();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[{Constants.LOG_TAG}] PhoneCompanion: {ex.Message}");
                return fallback;
            }
        }
    }
}
