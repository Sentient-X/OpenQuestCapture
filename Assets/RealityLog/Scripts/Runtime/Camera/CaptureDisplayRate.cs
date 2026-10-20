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
    /// measured on the bench 3S, 2026-10-19). Re-asserted every few seconds, since the system
    /// may change the rate when the headset sleeps or another app has focus.
    /// </summary>
    public class CaptureDisplayRate : MonoBehaviour
    {
        public const float CaptureHz = 90f;
        private const float CheckSeconds = 5f;

        private float lastCheck = float.NegativeInfinity;
        private bool reportedUnavailable;

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
            if (Mathf.Approximately(OVRPlugin.systemDisplayFrequency, CaptureHz)) return;
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
            Debug.Log($"[{Constants.LOG_TAG}] CaptureDisplayRate: display {OVRPlugin.systemDisplayFrequency} Hz -> {CaptureHz} Hz");
            OVRPlugin.systemDisplayFrequency = CaptureHz;
        }
    }
}
