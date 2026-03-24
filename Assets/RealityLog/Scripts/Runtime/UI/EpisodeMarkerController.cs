#nullable enable

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using TMPro;
using RealityLog.Common;

namespace RealityLog.UI
{
    /// <summary>
    /// Listens for B-button presses during recording and saves episode boundary
    /// timestamps to episode_markers.json in the session directory.
    /// Provides haptic and visual feedback on each mark.
    /// </summary>
    public class EpisodeMarkerController : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private RecordingManager recordingManager = default!;

        [Tooltip("CenterEyeAnchor transform for positioning the flash overlay")]
        [SerializeField] private Transform cameraTransform = default!;

        [Header("Input")]
        [SerializeField] private OVRInput.Button markerButton = OVRInput.Button.Two;

        private readonly List<long> markers = new();
        private bool wasRecording;
        private string? pendingDirectory;

        // Flash overlay
        private CanvasGroup? flashCanvasGroup;
        private TextMeshProUGUI? flashText;
        private Coroutine? fadeCoroutine;

        private void Awake()
        {
            BuildFlashOverlay();
        }

        private void Update()
        {
            if (recordingManager == null) return;

            bool isRecording = recordingManager.IsRecording;

            // Edge-detect recording start
            if (!wasRecording && isRecording)
            {
                markers.Clear();
                pendingDirectory = recordingManager.CurrentSessionDirectory;
            }

            // Edge-detect recording stop
            if (wasRecording && !isRecording)
            {
                markers.Clear();
                pendingDirectory = null;
            }

            // Check for marker button press during recording
            if (isRecording && OVRInput.GetDown(markerButton))
            {
                long timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                markers.Add(timestamp);

                // Write immediately for crash safety
                WriteMarkers(pendingDirectory);

                // Haptic feedback on right controller only
                StartCoroutine(HapticPulse(0.15f));

                // Visual flash
                ShowFlash($"EPISODE MARKED (#{markers.Count})");

                Debug.Log($"[{Constants.LOG_TAG}] EpisodeMarker: Marker #{markers.Count} at {timestamp}");
            }

            wasRecording = isRecording;
        }

        private void WriteMarkers(string? sessionDirectoryName)
        {
            if (string.IsNullOrEmpty(sessionDirectoryName) || markers.Count == 0)
                return;

            try
            {
                var sessionDir = Path.Join(Application.persistentDataPath, sessionDirectoryName);
                var markersPath = Path.Join(sessionDir, "episode_markers.json");

                var json = JsonUtility.ToJson(new EpisodeMarkersData
                {
                    marker_count = markers.Count,
                    markers_unix_ms = markers.ToArray()
                }, true);

                File.WriteAllText(markersPath, json);
                Debug.Log($"[{Constants.LOG_TAG}] EpisodeMarker: Wrote {markers.Count} markers to {markersPath}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[{Constants.LOG_TAG}] EpisodeMarker: Failed to write markers: {ex.Message}");
            }
        }

        private IEnumerator HapticPulse(float duration)
        {
            OVRInput.SetControllerVibration(0.5f, 0.5f, OVRInput.Controller.RTouch);
            yield return new WaitForSeconds(duration);
            OVRInput.SetControllerVibration(0f, 0f, OVRInput.Controller.RTouch);
        }

        private void ShowFlash(string message)
        {
            if (flashCanvasGroup == null || flashText == null || cameraTransform == null)
                return;

            if (fadeCoroutine != null)
                StopCoroutine(fadeCoroutine);

            flashText.text = message;

            // Position 1.2m in front of camera
            Vector3 pos = cameraTransform.position + cameraTransform.forward * 1.2f;
            flashCanvasGroup.transform.position = pos;

            // Face the camera (keep upright)
            Vector3 dirToCamera = cameraTransform.position - pos;
            dirToCamera.y = 0;
            if (dirToCamera.sqrMagnitude > 0.001f)
                flashCanvasGroup.transform.rotation = Quaternion.LookRotation(-dirToCamera.normalized, Vector3.up);

            flashCanvasGroup.alpha = 1f;
            fadeCoroutine = StartCoroutine(FadeOut(0.5f));
        }

        private IEnumerator FadeOut(float duration)
        {
            if (flashCanvasGroup == null) yield break;

            float elapsed = 0f;
            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;
                flashCanvasGroup.alpha = Mathf.Lerp(1f, 0f, elapsed / duration);
                yield return null;
            }

            flashCanvasGroup.alpha = 0f;
            fadeCoroutine = null;
        }

        private void BuildFlashOverlay()
        {
            // Create a small world-space canvas for the flash text
            var canvasGo = new GameObject("EpisodeMarkerFlash");
            canvasGo.transform.SetParent(transform, false);

            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;

            flashCanvasGroup = canvasGo.AddComponent<CanvasGroup>();
            flashCanvasGroup.alpha = 0f;
            flashCanvasGroup.interactable = false;
            flashCanvasGroup.blocksRaycasts = false;

            var canvasRt = canvasGo.GetComponent<RectTransform>();
            canvasRt.sizeDelta = new Vector2(400, 60);
            canvasGo.transform.localScale = new Vector3(0.001f, 0.001f, 0.001f);

            // Text element
            var textGo = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
            textGo.transform.SetParent(canvasGo.transform, false);

            var textRt = textGo.GetComponent<RectTransform>();
            textRt.anchorMin = Vector2.zero;
            textRt.anchorMax = Vector2.one;
            textRt.sizeDelta = Vector2.zero;

            flashText = textGo.GetComponent<TextMeshProUGUI>();
            flashText.fontSize = 28;
            flashText.fontStyle = FontStyles.Bold;
            flashText.color = new Color(0.2f, 0.85f, 0.3f);
            flashText.alignment = TextAlignmentOptions.Center;
            flashText.enableWordWrapping = false;
        }

        /// <summary>
        /// Called by HttpServerController or CloudRelayService when the phone
        /// operator presses the Mark button. Records the marker, shows a visual
        /// flash, and triggers haptic feedback so the VR wearer knows a new
        /// episode has started.
        /// </summary>
        public void MarkFromPhone(int episodeNumber)
        {
            long timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            markers.Add(timestamp);

            // Write immediately for crash safety
            WriteMarkers(pendingDirectory);

            // Haptic feedback on right controller
            StartCoroutine(HapticPulse(0.15f));

            // Visual flash
            ShowFlash($"EPISODE {episodeNumber} STARTED");

            Debug.Log($"[{Constants.LOG_TAG}] EpisodeMarker: Phone mark — episode {episodeNumber} at {timestamp}");
        }

        [Serializable]
        private struct EpisodeMarkersData
        {
            public int marker_count;
            public long[] markers_unix_ms;
        }
    }
}
