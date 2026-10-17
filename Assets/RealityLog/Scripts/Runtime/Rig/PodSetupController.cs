# nullable enable

using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using UnityEngine;
using RealityLog.Camera;
using RealityLog.Common;

namespace RealityLog.Rig
{
    /// <summary>
    /// The in-headset Pod setup: enroll this headset as a Factory pod. <b>Scan QR</b> reads the
    /// console's code ({"site", "join_code"}) through the left passthrough camera and enrolls at
    /// once (the wearer cannot see a button); the Quest system keyboard fills <b>Site</b> and
    /// <b>Join code</b> as a fallback, then <b>Enroll</b>. Each step answers with a status line
    /// and a sound: scanned (blips), enrolled (chime), any failure (buzz, with its reason). A
    /// successful enrollment leaves Local only off; a failed one restores it. The headset serial
    /// stays pod_config.json's. Shown by the recordings menu (RecordingListUI).
    /// </summary>
    public class PodSetupController : MonoBehaviour
    {
        private const float ScanTimeoutSeconds = 60f;

        public static PodSetupController? Instance { get; private set; }

        public string Site { get; private set; } = "";
        public string JoinCode { get; private set; } = "";
        public string Status { get; private set; } = "Not set up: scan the console's QR code, or type the site and join code.";
        public bool Busy => scanning || enrolling;
        public bool Scanning => scanning;

        private bool scanning;
        private bool enrolling;
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

        /// <summary>Opens the system keyboard for the site.</summary>
        public void EditSite() => OpenKeyboard(Site, false, text =>
        {
            var (site, error) = PodSetupJson.NormalizeSite(text);
            if (error != null) Fail(error);
            else { Site = site!; Status = $"Site: {Site}"; }
        });

        /// <summary>Opens the system keyboard for the join code.</summary>
        public void EditJoinCode() => OpenKeyboard(JoinCode, true, text =>
        {
            if (PodSetupJson.CheckJoinCode(text) is string error) Fail(error);
            else { JoinCode = text.Trim(); Status = "Join code entered."; }
        });

        /// <summary>Enrolls with the site and join code entered.</summary>
        public void Enroll()
        {
            if (Busy) return;
            var (site, siteError) = PodSetupJson.NormalizeSite(Site);
            if (siteError != null) { Fail(siteError); return; }
            if (PodSetupJson.CheckJoinCode(JoinCode) is string codeError) { Fail(codeError); return; }
            var rig = RigCoordinator.Instance;
            if (rig is null) { Fail("The rig coordinator is not running."); return; }
            if (FindFirstObjectByType<RecordingManager>() is { IsRecording: true })
            {
                Fail("Stop the recording before setting up the pod.");
                return;
            }
            enrolling = true;
            Status = $"Enrolling with {site}...";
            if (rig.BeginEnrollment(site) is RigRefusal refusal)
            {
                enrolling = false;
                Fail($"Cannot enroll: {refusal.Message}");
                return;
            }
            var code = JoinCode;
            Task.Run(() =>
            {
                try
                {
                    var answer = rig.Enroll(code);
                    mainThread.Enqueue(() =>
                    {
                        rig.FinishEnrollment(true);
                        enrolling = false;
                        JoinCode = "";
                        Status = "Enrolled: " + RigStatusJson.EnrollmentSummary(answer) + ". Local only is off.";
                        rig.QueueCue(RigCues.Cue.SignedIn);
                    });
                }
                catch (SxRigException ex)
                {
                    mainThread.Enqueue(() =>
                    {
                        rig.FinishEnrollment(false);
                        enrolling = false;
                        Fail("Enrollment failed: " + RigStatusJson.ErrorDetail(ex.ErrorJson, ex.Message));
                    });
                }
            });
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
                    var (site, code, error) = PodSetupJson.ParseQr(text);
                    if (error != null)
                    {
                        Fail(error);
                    }
                    else
                    {
                        Site = site!;
                        JoinCode = code!;
                        Status = $"Scanned the code for {Site}.";
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
