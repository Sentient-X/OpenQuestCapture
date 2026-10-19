# nullable enable

using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using RealityLog.Camera;
using RealityLog.Common;

namespace RealityLog.Rig
{
    /// <summary>
    /// The in-headset Pod setup: enroll this headset as a Factory pod. <b>Scan QR</b> reads the
    /// console's code ({"factory_url", "auth_url", "join_code"}) through the left passthrough
    /// camera and enrolls at once (the wearer cannot see a button); the Quest system keyboard
    /// fills <b>Factory URL</b>, <b>Auth URL</b> (prefilled with production) and <b>Join code</b>
    /// as a fallback, then <b>Enroll</b>. sx_rig_enroll_site answers with its steps (join,
    /// commission, rig, builds); each is told on the status line and with a sound: succeeded
    /// blips, unavailable a falling tone (it completes later), failed the buzz; enrolled ends
    /// with the chime and turns Local only off. The headset serial stays pod_config.json's.
    /// Shown by the recordings menu (RecordingListUI).
    /// </summary>
    public class PodSetupController : MonoBehaviour
    {
        private const float ScanTimeoutSeconds = 60f;
        private const float StepCueSpacingSeconds = 0.7f;

        public static PodSetupController? Instance { get; private set; }

        public string FactoryUrl { get; private set; } = PodSetupJson.DefaultFactoryUrl;
        public string AuthUrl { get; private set; } = PodSetupJson.DefaultAuthUrl;
        public string JoinCode { get; private set; } = "";
        /// <summary>
        /// What the panel says: a step's message while it runs and for a minute after, then
        /// the current state (enrolled or not). A collector's sign-in code is never kept here:
        /// the Pod card shows it from the rig's floor only while it is pending.
        /// </summary>
        public string Status
        {
            get => PodStatusBoard.SetupStatus(
                endsOnSignIn && RigCoordinator.Instance is { CollectorSignedInNow: true } ? null : status,
                Time.unscaledTime - statusSetAt, Busy,
                RigCoordinator.Instance is { Enrolled: true }, RigCoordinator.Instance?.LocalOnly ?? true);
            private set
            {
                status = value;
                statusSetAt = Time.unscaledTime;
                endsOnSignIn = false;
            }
        }
        /// <summary>A step's message while it runs and for a minute after; null once only the steady state is left.</summary>
        public string? StepMessage => PodStatusBoard.SetupMessage(
            endsOnSignIn && RigCoordinator.Instance is { CollectorSignedInNow: true } ? null : status,
            Time.unscaledTime - statusSetAt, Busy);
        private string? status;
        private float statusSetAt;
        private bool endsOnSignIn; // the message is about a pending sign-in: gone once signed in
        public bool Busy => scanning || enrolling || signingIn;
        /// <summary>True while a collector sign-in code is being asked for.</summary>
        public bool SigningIn => signingIn;
        public bool Scanning => scanning;

        private bool scanning;
        private bool enrolling;
        private bool signingIn;
        private float scanStarted;
        private VideoRecorderSurfaceProvider? scanCamera;
        private TouchScreenKeyboard? keyboard;
        private Action<string>? keyboardDone;
        private readonly ConcurrentQueue<Action> mainThread = new();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (Instance is not null) return;
            var go = new GameObject(nameof(PodSetupController));
            Instance = go.AddComponent<PodSetupController>();
            DontDestroyOnLoad(go);
        }

        /// <summary>Starts scanning for the console's QR code (not while recording).</summary>
        public void StartScan()
        {
            if (Busy) return;
            if (FindFirstObjectByType<RecordingManager>() is { IsRecording: true })
            {
                Fail("Stop the recording before setting up the pod.");
                return;
            }
            scanCamera = LeftCamera();
            if (scanCamera == null || !scanCamera.StartQrScan())
            {
                Fail("The passthrough camera is not available for scanning.");
                return;
            }
            scanning = true;
            scanStarted = Time.unscaledTime;
            Status = "Scanning: hold the console's QR code in front of the headset.";
        }

        public void CancelScan()
        {
            if (!scanning) return;
            scanCamera?.StopQrScan();
            scanning = false;
            Status = "Scan cancelled.";
        }

        public void EditFactoryUrl() => OpenKeyboard(FactoryUrl, false, text =>
        {
            var (url, error) = PodSetupJson.NormalizeUrl(text, "Factory URL");
            if (error != null) Fail(error);
            else { FactoryUrl = url!; Status = $"Factory URL: {FactoryUrl}"; }
        });

        public void EditAuthUrl() => OpenKeyboard(AuthUrl, false, text =>
        {
            var (url, error) = PodSetupJson.NormalizeUrl(text, "Auth URL");
            if (error != null) Fail(error);
            else { AuthUrl = url!; Status = $"Auth URL: {AuthUrl}"; }
        });

        public void EditJoinCode() => OpenKeyboard(JoinCode, true, text =>
        {
            if (PodSetupJson.CheckJoinCode(text) is string error) Fail(error);
            else { JoinCode = text.Trim(); Status = "Join code entered."; }
        });

        /// <summary>
        /// Signs a collector in on this enrolled headset, as a laptop pod's kiosk does: the rig
        /// asks the Factory for a code, the panel shows it with the address to approve it at,
        /// and the headset chimes when the approval lands (a start needs a signed-in collector).
        /// </summary>
        public void SignIn()
        {
            if (Busy) return;
            var rig = RigCoordinator.Instance;
            if (rig is not { Enrolled: true }) { Fail("Enroll this headset as a pod before signing in."); return; }
            if (rig.LocalOnly) { Fail("Turn Local only off to sign in: a local headset needs no collector."); return; }
            if (FindFirstObjectByType<RecordingManager>() is { IsRecording: true })
            {
                Fail("Stop the recording before signing in.");
                return;
            }
            signingIn = true;
            Status = "Asking the Factory for a sign-in code...";
            Task.Run(() =>
            {
                try
                {
                    // The code and its address come back in the rig's floor status, which the
                    // Pod card shows only while the sign-in is pending.
                    Newtonsoft.Json.Linq.JObject.Parse(rig.SignIn());
                    mainThread.Enqueue(() =>
                    {
                        signingIn = false;
                        Status = "Sign-in code ready: approve it at the address shown. The headset chimes once you are signed in.";
                        endsOnSignIn = true;
                    });
                }
                catch (SxRigException ex)
                {
                    mainThread.Enqueue(() =>
                    {
                        signingIn = false;
                        Fail("Sign-in failed: " + RigStatusJson.ErrorDetail(ex.ErrorJson, ex.Message));
                    });
                }
                catch (Newtonsoft.Json.JsonException ex)
                {
                    mainThread.Enqueue(() =>
                    {
                        signingIn = false;
                        Fail("Sign-in failed: " + ex.Message);
                    });
                }
            });
        }

        /// <summary>Enrolls with the URLs and join code entered (or scanned).</summary>
        public void Enroll()
        {
            if (Busy) return;
            var (factory, factoryError) = PodSetupJson.NormalizeUrl(FactoryUrl, "Factory URL");
            if (factoryError != null) { Fail(factoryError); return; }
            var (auth, authError) = PodSetupJson.NormalizeUrl(AuthUrl, "Auth URL");
            if (authError != null) { Fail(authError); return; }
            if (PodSetupJson.CheckJoinCode(JoinCode) is string codeError) { Fail(codeError); return; }
            var rig = RigCoordinator.Instance;
            if (rig is null) { Fail("The rig coordinator is not running."); return; }
            if (FindFirstObjectByType<RecordingManager>() is { IsRecording: true })
            {
                Fail("Stop the recording before setting up the pod.");
                return;
            }
            if (rig.BeginEnrollment() is RigRefusal refusal)
            {
                Fail($"Cannot enroll: {refusal.Message}");
                return;
            }
            enrolling = true;
            Status = $"Enrolling at {factory}...";
            var code = JoinCode;
            Task.Run(() =>
            {
                try
                {
                    var answer = rig.Enroll(factory!, auth!, code);
                    var steps = PodSetupJson.Steps(answer);
                    mainThread.Enqueue(() =>
                    {
                        rig.FinishEnrollment(true);
                        enrolling = false;
                        JoinCode = "";
                        Status = "Enrolled (" + RigStatusJson.EnrollmentSummary(answer) + "): " + PodSetupJson.Describe(steps) + ". Local only is off.";
                        StartCoroutine(PlaySteps(steps, enrolled: true));
                    });
                }
                catch (SxRigException ex)
                {
                    var steps = PodSetupJson.Steps(ex.ErrorJson);
                    mainThread.Enqueue(() =>
                    {
                        enrolling = false;
                        var detail = steps.Count > 0 ? PodSetupJson.Describe(steps) : RigStatusJson.ErrorDetail(ex.ErrorJson, ex.Message);
                        Status = "Enrollment failed: " + detail;
                        Debug.LogWarning($"[{Constants.LOG_TAG}] PodSetup: {Status}");
                        if (steps.Count > 0) StartCoroutine(PlaySteps(steps, enrolled: false));
                        else rig.QueueCue(RigCues.Cue.Refused);
                    });
                }
            });
        }

        /// <summary>One sound per step, spaced so each is heard, then the chime when enrolled.</summary>
        private IEnumerator PlaySteps(List<EnrollmentStep> steps, bool enrolled)
        {
            var rig = RigCoordinator.Instance;
            if (rig is null) yield break;
            foreach (var step in steps)
            {
                RigCues.Cue? cue = step.State switch
                {
                    "succeeded" => RigCues.Cue.Sealed,
                    "unavailable" => RigCues.Cue.Stop,
                    "failed" => RigCues.Cue.Refused,
                    _ => null, // skipped: already done
                };
                if (cue is RigCues.Cue play)
                {
                    rig.Cue(play);
                    yield return new WaitForSecondsRealtime(StepCueSpacingSeconds);
                }
            }
            if (enrolled) rig.Cue(RigCues.Cue.SignedIn);
        }

        private void Update()
        {
            while (mainThread.TryDequeue(out var action)) action();

            if (scanning && scanCamera != null)
            {
                if (scanCamera.QrResult() is string text)
                {
                    scanCamera.StopQrScan();
                    scanning = false;
                    var (factory, auth, code, error) = PodSetupJson.ParseQr(text);
                    if (error != null)
                    {
                        Fail(error);
                    }
                    else
                    {
                        FactoryUrl = factory!;
                        AuthUrl = auth!;
                        JoinCode = code!;
                        Status = $"Scanned the code for {FactoryUrl}.";
                        RigCoordinator.Instance?.QueueCue(RigCues.Cue.Sealed);
                        Enroll(); // the wearer cannot see a button: a scanned code enrolls at once
                    }
                }
                else if (Time.unscaledTime - scanStarted > ScanTimeoutSeconds)
                {
                    scanCamera.StopQrScan();
                    scanning = false;
                    Fail("No QR code found in 60 s.");
                }
            }

            if (keyboard != null && keyboard.status != TouchScreenKeyboard.Status.Visible)
            {
                var done = keyboard.status == TouchScreenKeyboard.Status.Done;
                var text = keyboard.text ?? "";
                var apply = keyboardDone;
                keyboard = null;
                keyboardDone = null;
                if (done) apply?.Invoke(text);
            }
        }

        private void OpenKeyboard(string current, bool secure, Action<string> done)
        {
            if (Busy) return;
            keyboardDone = done;
            keyboard = TouchScreenKeyboard.Open(current, TouchScreenKeyboardType.URL, false, false, secure);
        }

        private void Fail(string message)
        {
            Status = message;
            Debug.LogWarning($"[{Constants.LOG_TAG}] PodSetup: {message}");
            RigCoordinator.Instance?.QueueCue(RigCues.Cue.Refused);
        }

        private static VideoRecorderSurfaceProvider? LeftCamera()
        {
            VideoRecorderSurfaceProvider? first = null;
            foreach (var provider in FindObjectsByType<VideoRecorderSurfaceProvider>(FindObjectsSortMode.None))
            {
                first ??= provider;
                if (provider.OutputVideoFileName.StartsWith("left", StringComparison.Ordinal)) return provider;
            }
            return first;
        }
    }
}
