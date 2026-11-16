# nullable enable

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.Events;
using RealityLog.Camera;
using RealityLog.Common;
using RealityLog.Depth;
using RealityLog.OVR;
using RealityLog.Rig;

namespace RealityLog
{
    /// <summary>
    /// Recording lifecycle as reported by <c>/api/status</c> (<c>recording.state</c>).
    /// <see cref="Stopping"/> covers the whole of <see cref="RecordingManager.StopRecording"/>,
    /// during which <see cref="RecordingManager.IsRecording"/> is still true.
    /// </summary>
    public enum RecordingState
    {
        Idle,
        Recording,
        Stopping,
    }

    /// <summary>
    /// Central coordinator for all recording subsystems.
    /// Handles proper sequencing and lifecycle management of depth, camera, and pose recording.
    /// </summary>
    public class RecordingManager : MonoBehaviour
    {
        [Header("Recording Components")]
        [Tooltip("Manages depth map export")]
        [SerializeField] private DepthMapExporter? depthMapExporter = default!;
        
        [Tooltip("Manages camera image capture (left, right, etc.)")]
        [SerializeField] private SurfaceProviderBase[] cameraProviders = default!;
        
        [Tooltip("Manages pose logging (HMD, controllers, etc.)")]
        [SerializeField] private PoseLogger[] poseLoggers = default!;
        
        [Tooltip("Manages IMU logging (accelerometer, gyroscope)")]
        [SerializeField] private IMULogger[] imuLoggers = default!;

        [Tooltip("Manages body tracking logging (full body skeleton)")]
        [SerializeField] private BodyTrackingLogger[] bodyTrackingLoggers = default!;

        [Tooltip("Manages FPS timing for synchronized capture")]
        [SerializeField] private CaptureTimer captureTimer = default!;

        [Header("Recording Settings")]
        [SerializeField] private bool generateTimestampedDirectories = true;
        [SerializeField] private bool recordDepthMaps = false;

        [Header("Events")]
        [Tooltip("Invoked when recording stops and files are saved. Passes the directory name where files were saved.")]
        [SerializeField] private UnityEvent<string> onRecordingSaved = default!;

        [Tooltip("Invoked when recording starts.")]
        [SerializeField] private UnityEvent onRecordingStarted = default!;

        private bool isRecording = false;
        private float recordingStartTime = 0f;
        private string? currentSessionDirectory = null;
        private Coroutine? stopCoroutine;
        private const long MinExpectedVideoBytes = 1024;
        private const long MinExpectedTimestampBytes = 128;
        private const string TrackingOriginFileName = "tracking_origin.json";
        private const string RigStopReceiptFileName = "rig_stop_receipt.json";
        private TrackingOriginSession? trackingOriginSession;
        private bool recenterEventSubscribed;

        // Grace period: don't stop recording on a brief OS pause (proximity sensor misfire,
        // system overlay, Guardian boundary glitch). Only stop if the pause lasts longer than
        // this threshold. If the app is killed while paused, OnDestroy handles the stop.
        private DateTime? recordingPauseStartTime;
        private const double PauseGraceSeconds = 8.0;

        private static readonly string[] MotionFileNames = new[]
        {
            "imu.csv",
            "hmd_poses.csv",
            "left_controller_poses.csv",
            "right_controller_poses.csv",
        };

        // Cached reference to InfoCanvas animator for hiding standby label during recording
        private Animator? infoCanvasAnimator;
        private static readonly int IsRunningParam = Animator.StringToHash("IsRunning");

        public bool IsRecording => isRecording;

        /// <summary>
        /// The rig's stop receipt from the last stop (a JSON object), or null when that
        /// recording ran without a rig. Written to the session as rig_stop_receipt.json.
        /// </summary>
        public string? LastRigStopReceiptJson { get; private set; }
        public string? CurrentSessionDirectory => currentSessionDirectory;

        // RecordingState as an int so the HTTP thread can read it and flip
        // Recording -> Stopping the moment it accepts a stop request.
        private int state = (int)RecordingState.Idle;
        private long lastStopDurationMs = -1;

        /// <summary>Current lifecycle state. Safe to read from any thread.</summary>
        public RecordingState State => (RecordingState)System.Threading.Volatile.Read(ref state);

        /// <summary>Wall time of the last completed <see cref="StopRecording"/>; -1 before the first.</summary>
        public long LastStopDurationMs => System.Threading.Interlocked.Read(ref lastStopDurationMs);

        /// <summary>
        /// Marks a recording as stopping before the main thread gets to it, so status shows
        /// the stop at once. Safe to call from any thread; does nothing unless recording.
        /// </summary>
        public void MarkStopRequested()
        {
            System.Threading.Interlocked.CompareExchange(
                ref state, (int)RecordingState.Stopping, (int)RecordingState.Recording);
        }
        
        /// <summary>
        /// Gets the elapsed recording time in seconds.
        /// Returns 0 if not currently recording.
        /// </summary>
        public float RecordingDuration => isRecording ? Time.time - recordingStartTime : 0f;

        private void Start()
        {
            // Auto-discover body tracking loggers if not assigned in Inspector
            if (bodyTrackingLoggers == null || bodyTrackingLoggers.Length == 0)
            {
                bodyTrackingLoggers = FindObjectsByType<BodyTrackingLogger>(FindObjectsSortMode.None);
                if (bodyTrackingLoggers.Length > 0)
                {
                    Debug.Log($"[{Constants.LOG_TAG}] RecordingManager: Auto-discovered {bodyTrackingLoggers.Length} BodyTrackingLogger(s)");
                }
            }

            if (depthMapExporter != null)
            {
                depthMapExporter.IsExportEnabled = recordDepthMaps;
            }

            // Find InfoCanvas animator so we can hide the standby label when recording
            // starts from any source (cloud relay, HTTP, toggle, calibration)
            var infoCanvas = GameObject.Find("InfoCanvas");
            if (infoCanvas != null)
            {
                infoCanvasAnimator = infoCanvas.GetComponent<Animator>();
            }

            // The on-device gate's camera self-test, only when adb left its request (sx#4210).
            StartCoroutine(CameraSelfTest.RunIfRequested(
                cameraProviders.OfType<VideoRecorderSurfaceProvider>().ToList(),
                () => State == RecordingState.Idle));

            // After the body loggers are discovered: the close-out lists the stop's own streams.
            // Sessions a killed process left without stream_manifest.json are closed out as
            // interrupted, so the rig's seal can judge them instead of waiting forever. Off the
            // main thread (it counts CSV rows); no recording is running yet at launch.
            var dataPath = Application.persistentDataPath;
            var streamFiles = CurrentStreams().Select(e => e.File).ToList(); // the stop's own list
            System.Threading.Tasks.Task.Run(() =>
            {
                var closed = SessionRecovery.CloseOut(dataPath, currentSessionDirectory, streamFiles,
                    (session, ex) => Debug.LogError($"[{Constants.LOG_TAG}] RecordingManager: could not close out {session}: {ex.Message}"));
                foreach (var session in closed)
                {
                    Debug.LogWarning($"[{Constants.LOG_TAG}] RecordingManager: closed out interrupted session {session} (no stream manifest)");
                }
            });
        }

        /// <summary>
        /// Starts recording from all subsystems in the proper order. Throws
        /// <see cref="InvalidOperationException"/> — before any subsystem starts or a
        /// session directory exists — when a video provider cannot record its eye
        /// (<see cref="VideoRecorderSurfaceProvider.StartRefusal"/>): a session must
        /// not start without head video, and every remote start path reports the
        /// exception's message to its caller.
        /// </summary>
        public void StartRecording()
        {
            if (isRecording)
            {
                Debug.LogWarning($"[{Constants.LOG_TAG}] RecordingManager: Already recording!");
                return;
            }

            if (CameraSelfTest.Running)
            {
                const string busy = "RecordingManager: refusing to start — the camera self-test (adb, scripts/device_smoke.sh) is recording";
                Debug.LogError($"[{Constants.LOG_TAG}] {busy}");
                throw new InvalidOperationException(busy);
            }

            foreach (var provider in cameraProviders)
            {
                if (provider is VideoRecorderSurfaceProvider videoProvider
                    && videoProvider.StartRefusal is string refusal)
                {
                    var message =
                        $"RecordingManager: refusing to start — {videoProvider.OutputVideoFileName} " +
                        $"cannot be recorded: {refusal}";
                    Debug.LogError($"[{Constants.LOG_TAG}] {message}");
                    throw new InvalidOperationException(message);
                }
            }

            // Without live head poses the HMD, controller, IMU and body CSVs come out
            // header-only while both cameras record, and the session is unusable.
            // Sample now: the monitor's last Update may predate a long main-thread stall.
            HeadTrackingMonitor.RefreshNow();
            if (HeadTrackingMonitor.StartRefusal is string trackingRefusal)
            {
                var message = $"RecordingManager: refusing to start — {trackingRefusal}";
                Debug.LogError($"[{Constants.LOG_TAG}] {message}");
                throw new InvalidOperationException(message);
            }

            // The wrist cameras and gripper encoders, when configured or plugged in: a session
            // that expects them never starts without them. POST /api/start-recording checks the
            // rig and reserves this session's name (the rig's episode_id); once this start has
            // OQC recording, it arms the rig, which anchors the episode inside this recording.
            // Any other start path finds nothing reserved and is refused. With no rig expected,
            // rigSession is null and nothing here changes the laptop-pod flow.
            var rig = RigCoordinator.Instance;
            var rigSession = rig is null ? null : rig.ReservedSession;
            if (rigSession == null && rig is not null && rig.IsExpected)
            {
                var rigRefused = new RigRefusedException(new RigRefusal("rig_not_armed",
                    "the wrist rig is configured or plugged in; start recording through POST /api/start-recording, which arms it"));
                Debug.LogError($"[{Constants.LOG_TAG}] {rigRefused.Message}");
                throw rigRefused;
            }
            LastRigStopReceiptJson = null;

            // Generate session directory name if needed
            if (generateTimestampedDirectories)
            {
                var timestamp = rigSession ?? DateTime.Now.ToString("yyyyMMdd_HHmmss");
                currentSessionDirectory = timestamp;
                if (recordDepthMaps && depthMapExporter != null)
                {
                    depthMapExporter.DirectoryName = timestamp;
                }
                foreach (var provider in cameraProviders)
                {
                    provider.SetDataDirectoryName(timestamp);
                }
                foreach (var logger in poseLoggers)
                {
                    logger.DirectoryName = timestamp;
                }
                foreach (var logger in imuLoggers)
                {
                    logger.DirectoryName = timestamp;
                }
                foreach (var logger in bodyTrackingLoggers)
                {
                    logger.DirectoryName = timestamp;
                }
            }
            else
            {
                currentSessionDirectory = recordDepthMaps && depthMapExporter != null && !string.IsNullOrEmpty(depthMapExporter.DirectoryName)
                    ? depthMapExporter.DirectoryName
                    : "manual_session";

                foreach (var provider in cameraProviders)
                {
                    provider.SetDataDirectoryName(currentSessionDirectory);
                }
                foreach (var logger in poseLoggers)
                {
                    logger.DirectoryName = currentSessionDirectory;
                }
                foreach (var logger in imuLoggers)
                {
                    logger.DirectoryName = currentSessionDirectory;
                }
                foreach (var logger in bodyTrackingLoggers)
                {
                    logger.DirectoryName = currentSessionDirectory;
                }
            }

            Debug.Log($"[{Constants.LOG_TAG}] RecordingManager: Starting recording session '{currentSessionDirectory}'");
            MainThreadFrames.Watch("start", 5);

            try
            {
                // Step 1: Update camera paths for new session
                // This ensures format info and images are written to the new directory
                foreach (var provider in cameraProviders)
                {
                    provider.PrepareRecordingSession();
                }

                // Step 2: Setup file writers and directories
                // (Depth and camera systems are already initialized from app start)
                if (depthMapExporter != null)
                {
                    depthMapExporter.IsExportEnabled = recordDepthMaps;
                    if (recordDepthMaps)
                    {
                        depthMapExporter.StartExport();
                    }
                }
                foreach (var logger in poseLoggers)
                {
                    logger.StartLogging();
                }
                foreach (var logger in imuLoggers)
                {
                    logger.StartLogging();
                }
                foreach (var logger in bodyTrackingLoggers)
                {
                    logger.StartLogging();
                }

                foreach (var provider in cameraProviders)
                {
                    provider.StartRecordingSession();
                }
            
                // Optional: Reset camera base time to sync with depth timestamps
                // Currently commented out as both use system monotonic clock
                // Uncomment if timestamp alignment issues occur
                // foreach (var provider in cameraProviders)
                // {
                //     provider.ResetBaseTime();
                // }

                // Step 3: Start synchronized capture
                // This begins the actual frame capture loop
                captureTimer.StartCapture();

                isRecording = true;
                System.Threading.Volatile.Write(ref state, (int)RecordingState.Recording);
                recordingStartTime = Time.time;
                StartTrackingOriginRecord();
                HeadTrackingMonitor.BeginRecording();

                // Hide the standby label (green instruction box) regardless of how recording was started
                infoCanvasAnimator?.SetBool(IsRunningParam, true);

                WriteVideoStartTime();
                DeviceInfo.WriteToSession(Path.Combine(Application.persistentDataPath, currentSessionDirectory ?? ""));

                onRecordingStarted?.Invoke();

                Debug.Log($"[{Constants.LOG_TAG}] RecordingManager: Recording started successfully");
                StartCaptureFailureWatch(currentSessionDirectory ?? "");
            }
            catch
            {
                // The rig is not armed yet; a start that failed releases its reservation.
                if (rigSession != null && rig is not null)
                {
                    rig.Unreserve(rigSession);
                }
                throw;
            }
        }

        /// <summary>
        /// Stops recording from all subsystems in the proper order, without blocking Unity's
        /// main thread on the parts that wait (sx#4210): on the main thread the rig's cutoff
        /// (only when the rig is free; never waiting on an arming), the capture timer, both
        /// eyes' exposure freeze, the CSV loggers, the stream manifest and the tracking origin;
        /// then on a worker thread with the JVM attached each eye's encoder drain and file
        /// close (7.5 s for the right eye on pod5 2026-11-15, which froze every pose, controller
        /// and body stream) and the rig's stop (bounded 5 s; the seal reads this session, so it
        /// runs after every OQC file is final); then back on the main thread the state turns
        /// Idle. <see cref="State"/> reads Stopping and <see cref="IsRecording"/> true until then.
        /// Returns the stop's completion; <see cref="LastStopDurationMs"/>,
        /// <see cref="LastStreamManifestJson"/> and <see cref="LastRigStopReceiptJson"/> are this
        /// stop's once it completes. Main thread only.
        /// </summary>
        public void StopRecording() => StopRecordingAsync();

        /// <inheritdoc cref="StopRecording"/>
        public System.Threading.Tasks.Task StopRecordingAsync()
        {
            if (!isRecording)
            {
                Debug.LogWarning($"[{Constants.LOG_TAG}] RecordingManager: Not currently recording!");
                return System.Threading.Tasks.Task.CompletedTask;
            }
            if (stopInFlight is { } running)
            {
                return running.Task;
            }

            Debug.Log($"[{Constants.LOG_TAG}] RecordingManager: Stopping recording session");
            System.Threading.Volatile.Write(ref state, (int)RecordingState.Stopping);
            System.Threading.Interlocked.Increment(ref captureWatchGeneration); // the stop reports its own failures
            MainThreadFrames.Watch("stop", 10);

            var stopTimer = System.Diagnostics.Stopwatch.StartNew();
            var phases = new List<string>();
            long phaseStartMs = 0;
            void EndPhase(string phase)
            {
                var nowMs = stopTimer.ElapsedMilliseconds;
                lock (phases) phases.Add($"{phase}={nowMs - phaseStartMs}");
                phaseStartMs = nowMs;
            }

            // Store directory name before resetting state
            string savedDirectory = currentSessionDirectory ?? string.Empty;
            var finishers = new List<(string name, Action finish)>();
            try
            {
                // The actual end of acquisition, before any drain or flush. A rig still arming
                // (sx_rig_start_armed, seconds) is never waited on here: the worker's stop waits
                // for it, and sx_rig_stop then ends its capture.
                var rig = RigCoordinator.Instance;
                var cutoff = rig is null || rig.TryRequestStopEpisode();
                EndPhase(cutoff ? "rig_cutoff" : "rig_cutoff_deferred");
                captureTimer.StopCapture();
                EndPhase("capture_timer");

                // Freeze both camera exposure timelines before either encoder drains, so the
                // second eye never records on while the first finalizes.
                foreach (var provider in cameraProviders)
                {
                    provider.RequestStopRecordingSession();
                    EndPhase($"request_stop:{provider.name}");
                }
                foreach (var provider in cameraProviders)
                {
                    if (provider.BeginStopRecordingSession() is { } finish) finishers.Add((provider.name, finish));
                    EndPhase($"begin_stop:{provider.name}");
                }

                if (recordDepthMaps && depthMapExporter != null)
                {
                    depthMapExporter.StopExport();
                    EndPhase("depth");
                }
                foreach (var logger in poseLoggers)
                {
                    logger.StopLogging();
                    EndPhase($"stop_logging:{logger.name}");
                }
                foreach (var logger in imuLoggers)
                {
                    logger.StopLogging();
                    EndPhase($"stop_logging:{logger.name}");
                }
                foreach (var logger in bodyTrackingLoggers)
                {
                    logger.StopLogging();
                    EndPhase($"stop_logging:{logger.name}");
                }

                WriteStreamManifest(savedDirectory);
                EndPhase("stream_manifest");
                WriteTrackingOriginRecord(savedDirectory);
                EndPhase("tracking_origin");
            }
            catch
            {
                // A throw above leaves isRecording true; report "recording", not a stop that
                // never finishes, so a retried stop is not mistaken for one in progress.
                System.Threading.Volatile.Write(ref state, (int)RecordingState.Recording);
                throw;
            }
            var mainMs = stopTimer.ElapsedMilliseconds;
            EndPhase("main_thread");

            var completion = new System.Threading.Tasks.TaskCompletionSource<bool>(
                System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
            stopInFlight = completion;
            var dataPath = Application.persistentDataPath;
            var worker = new System.Threading.Thread(() =>
            {
                string? receipt = null;
#if UNITY_ANDROID && !UNITY_EDITOR
                var attached = AndroidJNI.AttachCurrentThread() == 0;
#endif
                try
                {
                    foreach (var (name, finish) in finishers)
                    {
                        try { finish(); }
                        catch (Exception ex) { Debug.LogError($"[{Constants.LOG_TAG}] RecordingManager: {name} stop failed: {ex}"); }
                        EndPhase($"stop_session:{name}");
                    }
                    // Last: the rig's seal reads this session, so every OQC file is final first.
                    receipt = StopRig(dataPath, savedDirectory);
                    EndPhase("rig");
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[{Constants.LOG_TAG}] RecordingManager: stop worker failed: {ex}");
                }
                finally
                {
#if UNITY_ANDROID && !UNITY_EDITOR
                    if (attached) AndroidJNI.DetachCurrentThread();
#endif
                    mainThreadWork.Enqueue(() => CompleteStop(savedDirectory, receipt, stopTimer, mainMs, phases, completion));
                }
            }) { IsBackground = true, Name = "RecordingStop" };
            stopWorker = worker;
            worker.Start();
            return completion.Task;
        }

        /// <summary>Main thread, once the stop worker is done: the recording reads Idle.</summary>
        private void CompleteStop(string savedDirectory, string? receipt, System.Diagnostics.Stopwatch stopTimer,
            long mainMs, List<string> phases, System.Threading.Tasks.TaskCompletionSource<bool> completion)
        {
            LastRigStopReceiptJson = receipt;
            // A failed rig episode, or a camera that failed mid-episode, already buzzed when it
            // failed; a clean one chimes stop.
            var cameraFailed = CaptureFailed is { } failedCamera && failedCamera.session == savedDirectory;
            if (receipt != null && RigCoordinator.Instance is { } rigForCue && !RigStatusJson.StopFailed(receipt) && !cameraFailed)
            {
                rigForCue.Cue(RigCues.Cue.Stop, "recording stopped");
            }

            HeadTrackingMonitor.EndRecording();
            isRecording = false;
            recordingStartTime = 0f;
            currentSessionDirectory = null;
            stopInFlight = null;
            stopWorker = null;

            var stopMs = stopTimer.ElapsedMilliseconds;
            System.Threading.Interlocked.Exchange(ref lastStopDurationMs, stopMs);
            System.Threading.Volatile.Write(ref state, (int)RecordingState.Idle);
            string phaseList;
            lock (phases) phaseList = string.Join(", ", phases);
            Debug.Log($"[{Constants.LOG_TAG}] RecordingManager: StopRecording took {stopMs} ms, {mainMs} ms of it on the main thread ({phaseList})");

            // Show the standby label again now that recording has stopped
            infoCanvasAnimator?.SetBool(IsRunningParam, false);

            // Wait for video finalization on a background thread, then fire events
            if (!string.IsNullOrEmpty(savedDirectory))
            {
                if (stopCoroutine != null)
                    StopCoroutine(stopCoroutine);
                stopCoroutine = StartCoroutine(WaitForFinalizationThenNotify(savedDirectory));
            }
            else
            {
                Debug.Log($"[{Constants.LOG_TAG}] RecordingManager: Recording stopped (no session directory)");
            }
            completion.TrySetResult(true);
        }

        // ── A head camera failing mid-episode (sx#4210) ──

        // Bumped by every start and stop: a watch whose generation is gone exits.
        private int captureWatchGeneration;

        /// <summary>
        /// The session whose head camera failed mid-episode, with the failure, or null. Set on
        /// the main thread when the watch reports, before <see cref="CameraFailed"/> runs; kept
        /// until the next start.
        /// </summary>
        public (string session, CaptureFailure failure)? CaptureFailed { get; private set; }

        /// <summary>
        /// Main thread, once per recording whose head camera failed while it recorded (the
        /// recording is still running): the HTTP controller buzzes, shows the notice and stops it
        /// as failed through the stop path.
        /// </summary>
        public event Action<string, CaptureFailure>? CameraFailed;

        // This session's watch, polled from Update on the main thread; null while none records.
        private (CaptureFailureWatch watch, int generation, string session)? captureWatch;
        private float nextCaptureWatchPollAt;

        /// <summary>
        /// Arms the poll of both eyes' recorders for their first failure (<see cref="CaptureFailureWatch"/>),
        /// at ~4 Hz from <see cref="Update"/>, on Unity's main thread: one JNI call per eye
        /// (sx#4210). Build 75 polled from a worker thread; the JNI objects are touched only from
        /// the main thread, or through <see cref="JniObject"/> on a thread attached to the JVM.
        /// </summary>
        private void StartCaptureFailureWatch(string session)
        {
            CaptureFailed = null;
            var generation = System.Threading.Interlocked.Increment(ref captureWatchGeneration);
            var eyes = cameraProviders.OfType<VideoRecorderSurfaceProvider>()
                .Select(p => (p.OutputVideoFileName, (Func<string?>)p.PollCaptureError)).ToArray();
            captureWatch = eyes.Length == 0 ? null : (new CaptureFailureWatch(eyes), generation, session);
            nextCaptureWatchPollAt = Time.realtimeSinceStartup + CaptureFailureWatch.PollIntervalMs / 1000f;
        }

        /// <summary>Main thread, from Update: one poll when due; the watch ends with its generation.</summary>
        private void PollCaptureWatch()
        {
            if (captureWatch is not { } armed) return;
            if (System.Threading.Volatile.Read(ref captureWatchGeneration) != armed.generation
                || State != RecordingState.Recording)
            {
                captureWatch = null;
                return;
            }
            if (Time.realtimeSinceStartup < nextCaptureWatchPollAt) return;
            nextCaptureWatchPollAt = Time.realtimeSinceStartup + CaptureFailureWatch.PollIntervalMs / 1000f;
            if (armed.watch.Poll() is { } failure)
            {
                captureWatch = null;
                Debug.LogError($"[{Constants.LOG_TAG}] RecordingManager: {failure.Describe()}");
                OnCaptureFailure(armed.generation, armed.session, failure);
            }
        }

        private void OnCaptureFailure(int generation, string session, CaptureFailure failure)
        {
            if (System.Threading.Volatile.Read(ref captureWatchGeneration) != generation
                || State != RecordingState.Recording || currentSessionDirectory != session)
            {
                Debug.Log($"[{Constants.LOG_TAG}] RecordingManager: {failure.Eye} failure arrived after '{session}' stopped; the stop reports it");
                return;
            }
            CaptureFailed = (session, failure);
            CameraFailed?.Invoke(session, failure);
        }

        // The stop that runs (worker started, completion not yet applied); null otherwise.
        private System.Threading.Tasks.TaskCompletionSource<bool>? stopInFlight;
        private System.Threading.Thread? stopWorker;
        // Work a worker hands back to Unity's main thread, run in Update.
        private readonly System.Collections.Concurrent.ConcurrentQueue<Action> mainThreadWork = new();

        private void Update()
        {
            while (mainThreadWork.TryDequeue(out var work))
            {
                try { work(); }
                catch (Exception ex) { Debug.LogError($"[{Constants.LOG_TAG}] RecordingManager: main-thread work failed: {ex}"); }
            }
            PollCaptureWatch();
            MainThreadFrames.Frame(Time.unscaledDeltaTime);
        }

        private IEnumerator WaitForFinalizationThenNotify(string savedDirectory)
        {
            // Yield until all video providers finish background finalization
            bool anyFinalizing = true;
            while (anyFinalizing)
            {
                anyFinalizing = false;
                foreach (var provider in cameraProviders)
                {
                    if (provider is VideoRecorderSurfaceProvider vp && vp.IsFinalizingVideo)
                    {
                        anyFinalizing = true;
                        break;
                    }
                }
                if (anyFinalizing)
                    yield return null;
            }

            onRecordingSaved?.Invoke(savedDirectory);
            ValidateSavedSession(savedDirectory);

            Debug.Log($"[{Constants.LOG_TAG}] RecordingManager: Recording stopped successfully. Files saved to '{savedDirectory}'");
            stopCoroutine = null;
        }

        /// <summary>
        /// Toggle recording on/off. Useful for UI buttons.
        /// </summary>
        public void ToggleRecording()
        {
            if (isRecording)
                StopRecording();
            else
                StartRecording();
        }

        private void OnValidate()
        {
            // Validate required references in editor
            if (recordDepthMaps && depthMapExporter == null)
                Debug.LogWarning($"[{Constants.LOG_TAG}] RecordingManager: recordDepthMaps is enabled but DepthMapExporter is missing!");
            
            if (cameraProviders == null || cameraProviders.Length == 0)
                Debug.LogWarning($"[{Constants.LOG_TAG}] RecordingManager: No camera surface providers assigned!");
            
            if (poseLoggers == null || poseLoggers.Length == 0)
                Debug.LogWarning($"[{Constants.LOG_TAG}] RecordingManager: No PoseLoggers assigned! Add HMD, controllers, etc.");
            
            if (captureTimer == null)
                Debug.LogWarning($"[{Constants.LOG_TAG}] RecordingManager: Missing CaptureTimer reference!");
        }

        private void OnApplicationQuit()
        {
            // Unity calls this on every object before destroying any, so the stop below still
            // sees every logger alive; OnDestroy's stop is the fallback when it is skipped.
            recordingPauseStartTime = null;
            StopForTeardown("OnApplicationQuit");
        }

        private void OnDestroy()
        {
            recordingPauseStartTime = null; // Clear any pending grace period on destroy.

            if (isRecording)
            {
                StopForTeardown("OnDestroy");
            }
            else if (stopCoroutine != null)
            {
                // A finalization coroutine from a prior StopRecording() is still waiting.
                // The bg thread will finish on its own; fire the events synchronously now
                // since the coroutine won't survive OnDestroy.
                // (savedDirectory was already cleared, so nothing further to do — the bg
                //  thread's PollVideoFinalization will still log its result.)
                stopCoroutine = null;
            }
        }

        /// <summary>
        /// Safety: ensure recording stops on teardown. Can't rely on the coroutine since
        /// Unity is tearing down — do a synchronous stop and validate immediately. Native
        /// encoder stop/finalization is synchronous, and the bg poll is just a verification
        /// safety net, so skipping it here is fine.
        /// </summary>
        private void StopForTeardown(string reason)
        {
            if (stopInFlight != null)
            {
                // A stop already runs on its worker: let it finish (the camera's Java stop is
                // bounded 15 s, the rig's 5 s), then apply its completion here, since Update
                // will not run again.
                Debug.Log($"[{Constants.LOG_TAG}] RecordingManager: {reason} while a stop runs; waiting for it.");
                stopWorker?.Join(TimeSpan.FromSeconds(22));
                while (mainThreadWork.TryDequeue(out var work))
                {
                    try { work(); }
                    catch (Exception ex) { Debug.LogError($"[{Constants.LOG_TAG}] RecordingManager: teardown work failed: {ex}"); }
                }
                return;
            }
            if (isRecording)
            {
                Debug.Log($"[{Constants.LOG_TAG}] RecordingManager: {reason} while recording; performing synchronous stop.");

                RigCoordinator.Instance?.RequestStopEpisode();
                captureTimer.StopCapture();
                HeadTrackingMonitor.EndRecording();

                foreach (var provider in cameraProviders)
                    provider.RequestStopRecordingSession();
                foreach (var provider in cameraProviders)
                    provider.StopRecordingSession();

                if (recordDepthMaps && depthMapExporter != null)
                    depthMapExporter.StopExport();
                foreach (var logger in poseLoggers)
                    logger.StopLogging();
                foreach (var logger in imuLoggers)
                    logger.StopLogging();
                foreach (var logger in bodyTrackingLoggers)
                    logger.StopLogging();

                string savedDirectory = currentSessionDirectory ?? string.Empty;
                WriteStreamManifest(savedDirectory);
                WriteTrackingOriginRecord(savedDirectory);
                LastRigStopReceiptJson = StopRig(Application.persistentDataPath, savedDirectory); // last: the rig's seal reads this session
                isRecording = false;
                System.Threading.Volatile.Write(ref state, (int)RecordingState.Idle);
                recordingStartTime = 0f;
                currentSessionDirectory = null;
                infoCanvasAnimator?.SetBool(IsRunningParam, false);

                if (!string.IsNullOrEmpty(savedDirectory))
                {
                    onRecordingSaved?.Invoke(savedDirectory);
                    ValidateSavedSession(savedDirectory);
                }
            }
        }

        private void OnApplicationPause(bool pauseStatus)
        {
            if (pauseStatus)
            {
                // App is pausing. Don't stop immediately — brief pauses (proximity sensor
                // misfire, system overlay, Guardian glitch) should not kill the recording.
                // Record the real-world timestamp and decide on resume.
                if (isRecording)
                {
                    recordingPauseStartTime = DateTime.UtcNow;
                    Debug.LogWarning($"[{Constants.LOG_TAG}] RecordingManager: App paused during recording — grace period is {PauseGraceSeconds}s.");
                }
            }
            else
            {
                // App is resuming. Decide whether the pause was long enough to warrant stopping.
                if (recordingPauseStartTime.HasValue)
                {
                    var elapsed = (DateTime.UtcNow - recordingPauseStartTime.Value).TotalSeconds;
                    recordingPauseStartTime = null;

                    if (!isRecording)
                    {
                        // Recording was stopped by another path (e.g., user pressed stop via cloud relay).
                        return;
                    }

                    if (elapsed > PauseGraceSeconds)
                    {
                        Debug.LogWarning($"[{Constants.LOG_TAG}] RecordingManager: App was paused for {elapsed:F1}s (> {PauseGraceSeconds}s grace) — stopping recording.");
                        StopRecordingForInterruption("extended_pause");
                    }
                    else
                    {
                        Debug.Log($"[{Constants.LOG_TAG}] RecordingManager: App resumed after {elapsed:F1}s (within grace period) — recording continues.");
                    }
                }
            }
        }

        private void OnApplicationFocus(bool hasFocus)
        {
            // Do NOT stop recording on focus loss. On Quest, focus loss is frequent
            // (guardian boundary, quick settings, notifications) and the camera keeps
            // delivering frames. Only OnApplicationPause (headset removal, app switch)
            // requires stopping the recording.
        }

        private void StopRecordingForInterruption(string reason)
        {
            if (!isRecording)
            {
                return;
            }

            Debug.LogWarning($"[{Constants.LOG_TAG}] RecordingManager: App interruption ({reason}) during recording; forcing stop to finalize files.");
            StopRecording();
        }

        /// <summary>
        /// Stops the rig's episode, if one ran, and writes its receipt into the session.
        /// Never throws: a rig that fails to stop reports it in the receipt. Returns the
        /// receipt (null when no rig episode ran). Any thread.
        /// </summary>
        private static string? StopRig(string dataPath, string sessionDirectoryName)
        {
            var rig = RigCoordinator.Instance;
            var receipt = rig is null ? null : rig.StopEpisode();
            if (receipt == null || string.IsNullOrEmpty(sessionDirectoryName))
            {
                return receipt;
            }
            try
            {
                var sessionDir = Path.Join(dataPath, sessionDirectoryName);
                Directory.CreateDirectory(sessionDir);
                File.WriteAllText(Path.Join(sessionDir, RigStopReceiptFileName), receipt);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[{Constants.LOG_TAG}] Failed to write {RigStopReceiptFileName}: {ex.Message}");
            }
            return receipt;
        }

        private void WriteVideoStartTime()
        {
            // Back-compat anchor for the primary stream (the first video provider in
            // `cameraProviders`). Per-stream start/stop stamps — including the monotonic
            // ones used for cross-stream alignment — live in each provider's own metadata
            // JSON, which is the file to read when more than one camera is recording.
            long videoStartMs = 0;
            foreach (var provider in cameraProviders)
            {
                if (provider is VideoRecorderSurfaceProvider videoProvider
                    && videoProvider.VideoStartUnixTimeMs > 0)
                {
                    videoStartMs = videoProvider.VideoStartUnixTimeMs;
                    break;
                }
            }

            if (videoStartMs <= 0)
            {
                Debug.LogWarning($"[{Constants.LOG_TAG}] No video start timestamp available");
                return;
            }

            try
            {
                var filePath = Path.Combine(
                    Application.persistentDataPath,
                    currentSessionDirectory ?? "",
                    "video_start_time.txt"
                );
                File.WriteAllText(filePath, videoStartMs.ToString());
                Debug.Log($"[{Constants.LOG_TAG}] Wrote video_start_time.txt: {videoStartMs}ms");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[{Constants.LOG_TAG}] Failed to write video_start_time.txt: {ex.Message}");
            }
        }

        private void ValidateSavedSession(string sessionDirectoryName)
        {
            try
            {
                var sessionDir = Path.Join(Application.persistentDataPath, sessionDirectoryName);

                // Every video provider in the session must have produced a usable file —
                // with more than one camera stream recording, a single healthy MP4 is not
                // enough to call the session good.
                var videoOk = true;
                var videoReport = string.Empty;
                foreach (var provider in cameraProviders)
                {
                    if (provider is VideoRecorderSurfaceProvider videoProvider)
                    {
                        var videoPath = Path.Join(sessionDir, videoProvider.OutputVideoFileName);
                        var timestampPath = Path.Join(
                            sessionDir,
                            videoProvider.FrameTimestampsFileName
                        );
                        var videoBytes = File.Exists(videoPath) ? new FileInfo(videoPath).Length : 0L;
                        var timestampBytes = File.Exists(timestampPath)
                            ? new FileInfo(timestampPath).Length
                            : 0L;
                        if (
                            videoBytes < MinExpectedVideoBytes
                            || timestampBytes < MinExpectedTimestampBytes
                        )
                        {
                            videoOk = false;
                        }

                        videoReport +=
                            $" {videoProvider.OutputVideoFileName}={videoBytes}B" +
                            $" {videoProvider.FrameTimestampsFileName}={timestampBytes}B";
                    }
                }

                var trackingOriginPath = Path.Join(sessionDir, TrackingOriginFileName);
                var trackingOriginOk =
                    File.Exists(trackingOriginPath)
                    && new FileInfo(trackingOriginPath).Length > 0;

                var motionOk = false;
                foreach (var fileName in MotionFileNames)
                {
                    var motionPath = Path.Join(sessionDir, fileName);
                    if (!File.Exists(motionPath))
                    {
                        continue;
                    }

                    if (new FileInfo(motionPath).Length > 0)
                    {
                        motionOk = true;
                        break;
                    }
                }

                if (videoOk && motionOk && trackingOriginOk)
                {
                    return;
                }

                Debug.LogWarning(
                    $"[{Constants.LOG_TAG}] RecordingManager: Session integrity warning for '{sessionDirectoryName}'. " +
                    $"videos:{videoReport}, has_motion_stream={motionOk}, " +
                    $"has_tracking_origin={trackingOriginOk}"
                );
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[{Constants.LOG_TAG}] RecordingManager: Failed to validate session '{sessionDirectoryName}': {ex.Message}");
            }
        }

        private void StartTrackingOriginRecord()
        {
            TrySubscribeToRecenterEvents();
            var manager = OVRManager.instance;
            var runtimeOrigin = OVRPlugin.GetTrackingOriginType().ToString();
            trackingOriginSession = new TrackingOriginSession
            {
                schema = "openquest.tracking_origin/v1",
                configured_origin = manager != null
                    ? manager.trackingOriginType.ToString()
                    : "Unknown",
                runtime_origin_at_start = runtimeOrigin,
                runtime_origin_at_stop = runtimeOrigin,
                allow_recenter = manager != null && manager.AllowRecenter,
                floor_height_reference = IsFloorReferenced(runtimeOrigin)
                    ? "quest_runtime_calibrated_floor"
                    : "not_floor_referenced",
                recording_start_unix_ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                recording_start_mono_ns = MonotonicClock.Nanos(),
                recenter_count_at_start = OVRPlugin.GetLocalTrackingSpaceRecenterCount(),
                recenter_count_at_stop = OVRPlugin.GetLocalTrackingSpaceRecenterCount(),
                recenter_events = new List<TrackingOriginRecenterEvent>(),
            };
        }

        private void OnTrackingOriginRecentered()
        {
            if (!isRecording || trackingOriginSession == null)
            {
                return;
            }

            trackingOriginSession.recenter_events.Add(
                new TrackingOriginRecenterEvent
                {
                    unix_time_ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    mono_time_ns = MonotonicClock.Nanos(),
                    recenter_count = OVRPlugin.GetLocalTrackingSpaceRecenterCount(),
                    runtime_origin = OVRPlugin.GetTrackingOriginType().ToString(),
                }
            );
        }

        private const string StreamManifestFileName = "stream_manifest.json";

        /// <summary>
        /// JSON array of per-stream entries ({"file","rows","stats"}) from the last stop,
        /// the same array written as "streams" in stream_manifest.json. Null until a stop
        /// produced one.
        /// </summary>
        public string? LastStreamManifestJson { get; private set; }

        /// <summary>
        /// Writes stream_manifest.json with the row count of every CSV stream. Must run after
        /// the loggers are stopped so the counts are final. A stream with 0 rows means the
        /// source never produced data (e.g. a frozen OVR pose stream). Never throws.
        /// Loggers are tested with <c>is null</c>, not Unity's <c>== null</c>: on an app exit
        /// (HOME, then the OS ends the activity) Unity may destroy a logger before this
        /// manager's OnDestroy writes the manifest, and a destroyed component compares equal
        /// to null although its stopped row count and stats are still readable — which is
        /// how imu.csv went missing from the manifest.
        /// </summary>
        private void WriteStreamManifest(string sessionDirectoryName)
        {
            LastStreamManifestJson = null;
            try
            {
                var entries = CurrentStreams();
                var emptyStreams = entries.Where(e => e.Rows == 0).Select(e => e.File).ToList();
                var streamsJson = StreamManifest.StreamsJson(entries);
                LastStreamManifestJson = streamsJson;

                if (emptyStreams.Count > 0)
                {
                    Debug.LogWarning(
                        $"[{Constants.LOG_TAG}] RecordingManager: stream(s) wrote 0 rows: {string.Join(", ", emptyStreams)}. " +
                        "The tracking source produced no new samples during the recording."
                    );
                }

                if (string.IsNullOrEmpty(sessionDirectoryName))
                {
                    return;
                }

                var sessionDir = Path.Join(Application.persistentDataPath, sessionDirectoryName);
                Directory.CreateDirectory(sessionDir);
                File.WriteAllText(
                    Path.Join(sessionDir, StreamManifestFileName),
                    StreamManifest.Document(streamsJson)
                );
            }
            catch (Exception ex)
            {
                Debug.LogError(
                    $"[{Constants.LOG_TAG}] Failed to write {StreamManifestFileName}: {ex.Message}"
                );
            }
        }

        /// <summary>
        /// Every CSV stream a stop lists, in the manifest's order: pose loggers, IMU loggers,
        /// body loggers. Loggers are tested with <c>is null</c>, not Unity's <c>== null</c> (see
        /// <see cref="WriteStreamManifest"/>). The launch close-out lists the same files.
        /// </summary>
        private List<StreamEntry> CurrentStreams()
        {
            var entries = new List<StreamEntry>();
            foreach (var logger in poseLoggers ?? Array.Empty<PoseLogger>())
                if (logger is not null) entries.Add(new StreamEntry(logger.FileName, logger.RowsWritten, logger.StatsJson()));
            foreach (var logger in imuLoggers ?? Array.Empty<IMULogger>())
                if (logger is not null) entries.Add(new StreamEntry(logger.FileName, logger.RowsWritten, logger.StatsJson()));
            foreach (var logger in bodyTrackingLoggers ?? Array.Empty<BodyTrackingLogger>())
                if (logger is not null) entries.Add(new StreamEntry(logger.FileName, logger.RowsWritten, logger.StatsJson()));
            return entries;
        }

        private static string EscapeJson(string value)
        {
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private void WriteTrackingOriginRecord(string sessionDirectoryName)
        {
            try
            {
                if (trackingOriginSession == null || string.IsNullOrEmpty(sessionDirectoryName))
                {
                    return;
                }

                trackingOriginSession.recording_stop_unix_ms =
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                trackingOriginSession.recording_stop_mono_ns = MonotonicClock.Nanos();
                trackingOriginSession.runtime_origin_at_stop =
                    OVRPlugin.GetTrackingOriginType().ToString();
                trackingOriginSession.recenter_count_at_stop =
                    OVRPlugin.GetLocalTrackingSpaceRecenterCount();

                var sessionDir = Path.Join(
                    Application.persistentDataPath,
                    sessionDirectoryName
                );
                Directory.CreateDirectory(sessionDir);
                File.WriteAllText(
                    Path.Join(sessionDir, TrackingOriginFileName),
                    JsonUtility.ToJson(trackingOriginSession, true)
                );
            }
            catch (Exception ex)
            {
                Debug.LogError(
                    $"[{Constants.LOG_TAG}] Failed to write {TrackingOriginFileName}: {ex.Message}"
                );
            }
            finally
            {
                trackingOriginSession = null;
                UnsubscribeFromRecenterEvents();
            }
        }

        private void TrySubscribeToRecenterEvents()
        {
            if (recenterEventSubscribed || OVRManager.display == null)
            {
                return;
            }
            OVRManager.display.RecenteredPose += OnTrackingOriginRecentered;
            recenterEventSubscribed = true;
        }

        private void UnsubscribeFromRecenterEvents()
        {
            if (!recenterEventSubscribed || OVRManager.display == null)
            {
                return;
            }
            OVRManager.display.RecenteredPose -= OnTrackingOriginRecentered;
            recenterEventSubscribed = false;
        }

        private static bool IsFloorReferenced(string origin)
        {
            return origin == "FloorLevel" || origin == "Stage";
        }

        [Serializable]
        private sealed class TrackingOriginSession
        {
            public string schema = string.Empty;
            public string configured_origin = string.Empty;
            public string runtime_origin_at_start = string.Empty;
            public string runtime_origin_at_stop = string.Empty;
            public bool allow_recenter;
            public string floor_height_reference = string.Empty;
            public long recording_start_unix_ms;
            public long recording_stop_unix_ms;
            public long recording_start_mono_ns;
            public long recording_stop_mono_ns;
            public int recenter_count_at_start;
            public int recenter_count_at_stop;
            public List<TrackingOriginRecenterEvent> recenter_events = new();
        }

        [Serializable]
        private sealed class TrackingOriginRecenterEvent
        {
            public long unix_time_ms;
            public long mono_time_ns;
            public int recenter_count;
            public string runtime_origin = string.Empty;
        }
    }

    /// <summary>
    /// The longest Unity main-thread frame over a window opened by a start or a stop, logged
    /// once when the window closes ("main thread: longest frame N ms ..."). A blocked main
    /// thread stops every pose, controller and body stream and ages the tracking sample the rig
    /// judges a start by (sx#4210), so each start and stop says how long its worst frame was.
    /// <see cref="Watch"/> from any thread; <see cref="Frame"/> from RecordingManager.Update.
    /// </summary>
    public static class MainThreadFrames
    {
        private static readonly object Gate = new();
        private static string? label;
        private static long sinceTicks;
        private static long untilTicks;
        private static double longestMs;
        private static int frames;

        /// <summary>Opens (or widens) the window for <paramref name="seconds"/> from now.</summary>
        public static void Watch(string what, double seconds)
        {
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            var until = now + (long)(seconds * System.Diagnostics.Stopwatch.Frequency);
            lock (Gate)
            {
                if (label == null)
                {
                    label = what;
                    sinceTicks = now;
                    longestMs = 0;
                    frames = 0;
                }
                else if (!label.EndsWith(what, StringComparison.Ordinal))
                {
                    label += " + " + what;
                }
                if (until > untilTicks) untilTicks = until;
            }
        }

        /// <summary>One main-thread frame of <paramref name="deltaSeconds"/> (unscaled).</summary>
        public static void Frame(float deltaSeconds)
        {
            string? done = null;
            lock (Gate)
            {
                if (label == null) return;
                frames++;
                if (deltaSeconds * 1000.0 > longestMs) longestMs = deltaSeconds * 1000.0;
                var now = System.Diagnostics.Stopwatch.GetTimestamp();
                if (now < untilTicks) return;
                done = $"main thread: longest frame {longestMs:F0} ms over {frames} frames in the " +
                    $"{(now - sinceTicks) / (double)System.Diagnostics.Stopwatch.Frequency:F1} s from {label}";
                label = null;
                untilTicks = 0;
            }
            Debug.Log($"[{Constants.LOG_TAG}] {done}");
        }
    }
}
