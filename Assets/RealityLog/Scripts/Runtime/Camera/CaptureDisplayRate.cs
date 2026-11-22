# nullable enable

using UnityEngine;
using RealityLog.Common;

namespace RealityLog.Camera
{
    /// <summary>
    /// Holds the headset display at 90 Hz while the app runs. The Quest 3S passthrough cameras
    /// time their frames to the display: at 72 Hz the [60,60] source range delivers a 36 Hz
    /// lattice, whose frames fill a 30 Hz grid unevenly and leave the two cameras' frames
    /// unpaired (stereo_pair_fraction 0.89-0.96, refused by the seal), while at 90 Hz it
    /// delivers the 50 Hz lattice the 30 Hz selection was built on (pairs 0.9985, 0 gaps;
    /// measured on the bench 3S, 2026-10-19). Re-asserted every few seconds while idle, since the
    /// system may change the rate when the headset sleeps or another app has focus.
    ///
    /// Never while a recording runs, and never endlessly: a display mode set re-times the cameras,
    /// and on 2026-11-22 Pod 3 read 72 Hz on every check and set 90 Hz every 5 s all day, 17,903
    /// times, inside every one of its 96 takes (its takes still paired, so the cameras ran the 50 Hz
    /// lattice regardless); it was refused for head-video gaps at 4.66 per recorded hour, two to eight
    /// times the other pods. A rate the system will not hold after <see cref="MaxRefusals"/> tries is
    /// said once and retried only every <see cref="RefusedRetrySeconds"/>; a recording in progress
    /// is never touched, and the rate read at its start is logged beside the session, so a take that
    /// ran at 72 Hz says so. <see cref="HeldHz"/> and <see cref="Refused"/> are for the status.
    /// </summary>
    public class CaptureDisplayRate : MonoBehaviour
    {
        public const float CaptureHz = 90f;
        private const float CheckSeconds = 5f;
        /// <summary>Consecutive sets the system did not hold before the app stops hammering it.</summary>
        public const int MaxRefusals = 3;
        /// <summary>Once refused, a set is tried again this often while idle.</summary>
        public const float RefusedRetrySeconds = 60f;

        private float lastCheck = float.NegativeInfinity;
        private bool reportedUnavailable;
        private int refusals;
        private bool reportedRefused;
        private RecordingManager? recordingManager;

        /// <summary>The display rate at the last check, Hz; 0 before the first.</summary>
        public static float HeldHz { get; private set; }

        /// <summary>True once the system refused to hold <see cref="CaptureHz"/> <see cref="MaxRefusals"/> times in a row.</summary>
        public static bool Refused { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            var go = new GameObject(nameof(CaptureDisplayRate));
            go.AddComponent<CaptureDisplayRate>();
            DontDestroyOnLoad(go);
        }

        private void Update()
        {
            if (Time.unscaledTime - lastCheck < CheckSeconds) return;
            lastCheck = Time.unscaledTime;
            var current = OVRPlugin.systemDisplayFrequency;
            HeldHz = current;
            if (Mathf.Approximately(current, CaptureHz))
            {
                if (refusals > 0 && reportedRefused)
                {
                    Debug.Log($"[{Constants.LOG_TAG}] CaptureDisplayRate: the system holds {CaptureHz} Hz again");
                }
                refusals = 0;
                reportedRefused = false;
                Refused = false;
                return;
            }
            recordingManager ??= FindFirstObjectByType<RecordingManager>();
            if (recordingManager != null && recordingManager.IsRecording)
            {
                // A mode set mid-take re-times both cameras; the rate the take started at is logged
                // by Describe() at its start, and the seal judges its pairing.
                return;
            }
            var available = OVRPlugin.systemDisplayFrequenciesAvailable;
            if (available == null || System.Array.FindIndex(available, hz => Mathf.Approximately(hz, CaptureHz)) < 0)
            {
                if (!reportedUnavailable)
                {
                    Debug.LogWarning($"[{Constants.LOG_TAG}] CaptureDisplayRate: {CaptureHz} Hz is not offered; the head cameras' frames may not pair");
                    reportedUnavailable = true;
                }
                return;
            }
            if (refusals >= MaxRefusals)
            {
                Refused = true;
                if (!reportedRefused)
                {
                    Debug.LogWarning($"[{Constants.LOG_TAG}] CaptureDisplayRate: the system did not hold {CaptureHz} Hz after {refusals} sets (reads {current} Hz); retrying every {RefusedRetrySeconds:F0} s while idle");
                    reportedRefused = true;
                }
                if (Time.unscaledTime - lastSet < RefusedRetrySeconds) return;
            }
            Debug.Log($"[{Constants.LOG_TAG}] CaptureDisplayRate: display {current} Hz -> {CaptureHz} Hz");
            OVRPlugin.systemDisplayFrequency = CaptureHz;
            lastSet = Time.unscaledTime;
            refusals++;
        }

        private float lastSet = float.NegativeInfinity;

        /// <summary>"display 90 Hz" or "display 72 Hz (90 Hz refused by the system)", for a recording's start log.</summary>
        public static string Describe() =>
            Refused ? $"display {HeldHz} Hz ({CaptureHz} Hz refused by the system)" : $"display {HeldHz} Hz";
    }
}
