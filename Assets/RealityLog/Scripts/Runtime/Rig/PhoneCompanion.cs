# nullable enable

using System;
using System.Threading.Tasks;
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
    /// recorder keeps open; never while recording), then the companion library pairs over
    /// Bluetooth LE with that phone, whose operator confirms on the phone. Once paired, it
    /// reconnects on every launch; the phone's SignInAsk and SignOutAsk run the Pod card's own
    /// sign-in and sign-out; a pending sign-in code goes to the phone, and so does its result;
    /// and the status screen (HeadsetStatusMap) is pushed on every change and at least every 5 s.
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
        // A phase this side holds over the link's (scanning, a refusal, a denied permission).
        private CompanionPhase? local;
        private bool scanning;
        private float scanStarted;
        private VideoRecorderSurfaceProvider? scanCamera;
        private bool wasConnected;
        private readonly CompanionSignInWatch signIn = new();
        private readonly StreamLiveness liveness = new();
        private string lastStatusPrint = "";
        private float lastStatusSent = float.NegativeInfinity;
        private float lastStatusCheck = float.NegativeInfinity;
        private bool asking; // waiting for a Bluetooth permission answer

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
            if (PodSetupController.Instance is { } setup) setup.SignInFailed += OnSignInFailed;
            // Reconnect to the paired phone on launch, once Bluetooth is allowed.
            if (Safe(() => link.Paired, false)) WithPermissions(() => Safe(link.Resume));
        }

        private void OnDestroy()
        {
            StopScan();
            if (PodSetupController.Instance is { } setup) setup.SignInFailed -= OnSignInFailed;
            link?.Dispose();
            link = null;
        }

        /// <summary>The Pod card's first Phone pill: Pair phone, or Cancel while pairing.</summary>
        public void PairOrCancel()
        {
            if (link == null) return;
            if (scanning || Phase is CompanionPhase.Connecting or CompanionPhase.Confirming)
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

        public void Cancel()
        {
            StopScan();
            local = null;
            Error = "";
            Safe(() => link?.Cancel());
        }

        /// <summary>Forgets the paired phone: it can no longer connect until paired again from a fresh QR.</summary>
        public void Forget()
        {
            StopScan();
            local = null;
            Error = "";
            Safe(() => link?.Forget());
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

        private void Update()
        {
            if (link == null)
            {
                Phase = CompanionPhase.Unavailable;
                return;
            }
            PollScan();
            ReadLink();
            if (Phase != CompanionPhase.Connected)
            {
                wasConnected = false;
                return;
            }
            if (!wasConnected)
            {
                // A new session: the phone hears a pending code again and gets the status at once.
                wasConnected = true;
                signIn.LinkRestarted();
                lastStatusPrint = "";
            }
            Asks();
            HandOffSignIn();
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
                RigCoordinator.Instance?.QueueCue(RigCues.Cue.Sealed);
                local = null;
                if (!Safe(() => link!.Pair(text, PodId(), PodName(), HeadsetId())))
                {
                    Refuse("the companion did not start");
                }
            }
            else if (Time.unscaledTime - scanStarted > ScanTimeoutSeconds)
            {
                StopScan();
                Refuse("no phone QR seen in 60 s");
            }
        }

        private void ReadLink()
        {
            var phase = Safe(() => link!.Phase, CompanionPhase.Failed);
            PhoneName = Safe(() => link!.PhoneName, "");
            OperatorName = Safe(() => link!.OperatorName, "");
            if (phase == CompanionPhase.Failed)
            {
                var error = Safe(() => link!.Error, "");
                if (error.Length > 0) Error = error;
            }
            // The link's own progress (connecting onward) replaces a held refusal.
            if (local is CompanionPhase.Failed or CompanionPhase.PermissionDenied
                && phase is CompanionPhase.Connecting or CompanionPhase.Confirming or CompanionPhase.Connected)
            {
                local = null;
            }
            Phase = local ?? phase;
        }

        private void Asks()
        {
            for (var i = 0; i < 4; i++)
            {
                var ask = Safe(() => link!.NextAsk(), "");
                if (ask.Length == 0) return;
                if (ask == "sign_in") SignInAsked();
                else if (ask == "sign_out") SignOutAsked();
                else Debug.LogWarning($"[{Constants.LOG_TAG}] PhoneCompanion: an ask this build does not take: {ask}");
            }
        }

        /// <summary>The phone's SignInAsk: the Pod card's Sign in, or the collector already signed in.</summary>
        private void SignInAsked()
        {
            var rig = RigCoordinator.Instance;
            if (rig is { CollectorSignedInNow: true })
            {
                var name = HeadsetStatusMap.Collector(Status());
                Safe(() => link!.SendSignInResult(name.Length > 0 ? name : "a collector", ""));
                return;
            }
            if (PodSetupController.Instance is not { } setup)
            {
                Safe(() => link!.SendSignInResult("", "The headset's pod setup is not running"));
                return;
            }
            if (setup.SignIn() is string refused) signIn.SignInFailed(refused);
        }

        /// <summary>The phone's SignOutAsk: the HTTP route's sign-out rules (not while recording).</summary>
        private void SignOutAsked()
        {
            var rig = RigCoordinator.Instance;
            if (rig is not { Enrolled: true, IsOpen: true } || rig.LocalOnly)
            {
                Debug.LogWarning($"[{Constants.LOG_TAG}] PhoneCompanion: sign-out asked, but this headset has no collector to sign out");
                return;
            }
            if (FindFirstObjectByType<RecordingManager>() is { State: not RecordingState.Idle })
            {
                Debug.LogWarning($"[{Constants.LOG_TAG}] PhoneCompanion: sign-out asked while recording; refused");
                rig.QueueCue(RigCues.Cue.Refused);
                return;
            }
            Task.Run(() =>
            {
                try
                {
                    rig.SignOut();
                }
                catch (SxRigException ex)
                {
                    Debug.LogWarning($"[{Constants.LOG_TAG}] PhoneCompanion: sign-out failed: {ex.Message}");
                }
            });
        }

        private void OnSignInFailed(string error) => signIn.SignInFailed(error);

        private void HandOffSignIn()
        {
            var rig = RigCoordinator.Instance;
            var send = signIn.Observe(Status(), true, code => rig?.SignInCodeExpiresAtMs(code) ?? 0);
            switch (send.Kind)
            {
                case CompanionSignInSendKind.Request:
                    Safe(() => link!.SendSignInRequest(send.UserCode, send.VerificationUriComplete, send.ExpiresAtMs));
                    break;
                case CompanionSignInSendKind.Result:
                    Safe(() => link!.SendSignInResult(send.Collector, send.Error));
                    break;
            }
        }

        /// <summary>The status screen to the phone: on every change, and at least every 5 s.</summary>
        private void PushStatus()
        {
            var now = Time.unscaledTime;
            if (now - lastStatusCheck < StatusCheckSeconds) return;
            lastStatusCheck = now;
            var json = RigCoordinator.StatusJsonOrDefault;
            var status = Parse(json);
            var screen = PodScreenBoard.Build(json, PodStartLog.Shared.Last, PodStartLog.Shared.Succeeded, liveness, DateTime.UtcNow);
            var print = new HeadsetStatusFingerprint();
            var podName = PodName();
            HeadsetStatusMap.Write(screen, status, podName, print);
            if (print.Text == lastStatusPrint && now - lastStatusSent < StatusEverySeconds) return;
            if (Safe(() => link!.SendStatus(writer => HeadsetStatusMap.Write(screen, status, podName, writer))))
            {
                lastStatusPrint = print.Text;
                lastStatusSent = now;
            }
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
            RigCoordinator.Instance?.QueueCue(RigCues.Cue.Refused);
        }

        private static JObject? Status() => Parse(RigCoordinator.StatusJsonOrDefault);

        private static JObject? Parse(string json)
        {
            try
            {
                return JToken.Parse(json) as JObject;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static string PodId()
        {
            var status = Status();
            return status?["enrolled"] is JValue { Type: JTokenType.Boolean } enrolled && (bool)enrolled
                && status["podId"] is JValue { Type: JTokenType.String } id ? (string?)id ?? "" : "";
        }

        private static string PodName() => RigCoordinator.Instance?.PodName ?? "";

        private static string HeadsetId() =>
            Status()?["headsetSerial"] is JValue { Type: JTokenType.String } serial ? (string?)serial ?? "" : "";

        /// <summary>A bridge call; a Java exception is logged and reads as false, never thrown into Update.</summary>
        private static bool Safe(Action call)
        {
            try
            {
                call();
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[{Constants.LOG_TAG}] PhoneCompanion: {ex.Message}");
                return false;
            }
        }

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
