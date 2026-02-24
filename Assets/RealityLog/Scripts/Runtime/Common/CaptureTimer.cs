# nullable enable

using UnityEngine;

namespace RealityLog.Common
{
    /// <summary>
    /// Manages FPS-based timing for synchronized data capture.
    /// Provides a frame-accurate timing signal for camera and depth capture.
    /// </summary>
    public class CaptureTimer : MonoBehaviour
    {
        [Header("Capture Timing")]
        [Tooltip("Target FPS for synchronized camera and depth capture. Set to 0 to capture at maximum rate (~25 FPS)")]
        [SerializeField] private float targetCaptureFPS = 30f;
        [SerializeField] private bool verboseTimingLogs = false;

        private float lastCaptureTime = 0f;
        private float captureInterval = 0f;
        private bool shouldCaptureThisFrame = false;
        private bool isCapturing = false;

        /// <summary>
        /// Returns true if a capture should happen this frame.
        /// Both camera and depth should check this flag.
        /// </summary>
        public bool ShouldCaptureThisFrame => shouldCaptureThisFrame;

        /// <summary>
        /// Returns true if the timer is currently active.
        /// </summary>
        public bool IsCapturing => isCapturing;

        /// <summary>
        /// Gets the target capture FPS setting.
        /// </summary>
        public float TargetCaptureFPS => targetCaptureFPS;

        /// <summary>
        /// Starts the capture timer.
        /// </summary>
        public void StartCapture()
        {
            isCapturing = true;
            captureInterval = (targetCaptureFPS > 0) ? (1f / targetCaptureFPS) : 0f;
            // Set lastCaptureTime to (now - interval) so first Update() triggers immediately
            // This ensures lastCaptureTime is properly set when first capture happens
            lastCaptureTime = Time.unscaledTime - captureInterval;
            shouldCaptureThisFrame = false; // Let Update() handle the first capture properly

            if (verboseTimingLogs)
            {
                Debug.Log($"[CaptureTimer] Started at {targetCaptureFPS} FPS (interval: {captureInterval}s)");
            }
        }

        /// <summary>
        /// Stops the capture timer.
        /// </summary>
        public void StopCapture()
        {
            isCapturing = false;
            shouldCaptureThisFrame = false;

            if (verboseTimingLogs)
            {
                Debug.Log($"[CaptureTimer] Stopped");
            }
        }

        private void Update()
        {
            if (!isCapturing)
            {
                shouldCaptureThisFrame = false;
                return;
            }

            // If no FPS limit (interval == 0), always capture
            if (captureInterval <= 0f)
            {
                shouldCaptureThisFrame = true;
                return;
            }

            // Use unscaled time for consistency
            float currentTime = Time.unscaledTime;
            
                // Check if enough time has passed for next capture
                if ((currentTime - lastCaptureTime) >= captureInterval)
                {
                    shouldCaptureThisFrame = true;
                    lastCaptureTime = currentTime;
                    if (verboseTimingLogs)
                    {
                        Debug.Log($"[CaptureTimer] Capture signal at time={currentTime:F3}s (interval={captureInterval:F3}s)");
                    }
                }
                else
                {
                    shouldCaptureThisFrame = false;
                }
        }
    }
}
