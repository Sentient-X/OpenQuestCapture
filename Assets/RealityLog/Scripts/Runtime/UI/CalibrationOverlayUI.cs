#nullable enable

using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using RealityLog.Common;

namespace RealityLog.UI
{
    /// <summary>
    /// World-space overlay that displays calibration phase instructions, countdown timer,
    /// progress bars, and provides audio/haptic feedback during calibration.
    /// Builds UI procedurally following the same pattern as RecordingDetailPanelUI.
    /// </summary>
    public class CalibrationOverlayUI : MonoBehaviour
    {
        [SerializeField] private CalibrationSession calibrationSession = default!;

        [Tooltip("Camera transform for positioning overlay in front of user (e.g. CenterEyeAnchor)")]
        [SerializeField] private Transform cameraTransform = default!;

        [Tooltip("Distance in meters to place overlay in front of user")]
        [SerializeField] private float displayDistance = 1.5f;

        [Tooltip("Short beep for phase transitions (optional)")]
        [SerializeField] private AudioClip? phaseTransitionClip;

        [Tooltip("Distinct chime for calibration completion (optional)")]
        [SerializeField] private AudioClip? completionClip;

        [SerializeField] private TMP_FontAsset? font;

        private static readonly Color PanelBg = new Color(0.08f, 0.08f, 0.10f, 0.92f);
        private static readonly Color SectionBg = new Color(0.14f, 0.14f, 0.16f, 1f);
        private static readonly Color AccentColor = new Color(0.3f, 0.7f, 1f);
        private static readonly Color TextColor = new Color(0.9f, 0.9f, 0.9f);
        private static readonly Color DimTextColor = new Color(0.6f, 0.6f, 0.6f);
        private static readonly Color ProgressBg = new Color(0.2f, 0.2f, 0.22f);
        private static readonly Color ProgressFill = new Color(0.3f, 0.7f, 1f);
        private static readonly Color OverallProgressFill = new Color(0.2f, 0.85f, 0.3f);
        private static readonly Color CancelBtnColor = new Color(0.6f, 0.25f, 0.25f);
        private static readonly Color CompleteColor = new Color(0.2f, 0.85f, 0.3f);

        private Canvas? canvas;
        private CanvasGroup? canvasGroup;
        private AudioSource? audioSource;

        // UI references updated each frame
        private TextMeshProUGUI? phaseCounterText;
        private TextMeshProUGUI? phaseNameText;
        private TextMeshProUGUI? instructionsText;
        private TextMeshProUGUI? countdownText;
        private Image? phaseProgressFill;
        private Image? overallProgressFill;
        private GameObject? cancelButton;

        private bool isVisible;
        private CalibrationPhase lastPhase = CalibrationPhase.NotStarted;

        private void Awake()
        {
            canvas = GetComponent<Canvas>();
            if (canvas == null)
            {
                canvas = gameObject.AddComponent<Canvas>();
            }
            canvas.renderMode = RenderMode.WorldSpace;

            canvasGroup = GetComponent<CanvasGroup>();
            if (canvasGroup == null)
                canvasGroup = gameObject.AddComponent<CanvasGroup>();

            audioSource = GetComponent<AudioSource>();
            if (audioSource == null)
                audioSource = gameObject.AddComponent<AudioSource>();
            audioSource.playOnAwake = false;
            audioSource.spatialBlend = 0f; // 2D audio so it's always audible

            // Set canvas size: 0.5m x 0.55m physical
            var canvasRt = canvas.GetComponent<RectTransform>();
            canvasRt.sizeDelta = new Vector2(500, 550);
            transform.localScale = new Vector3(0.001f, 0.001f, 0.001f);

            BuildUI();
            Hide();
        }

        private void OnEnable()
        {
            if (calibrationSession != null)
            {
                calibrationSession.OnPhaseChanged += HandlePhaseChanged;
                calibrationSession.OnCalibrationComplete += HandleCalibrationComplete;
                calibrationSession.OnCalibrationCancelled += HandleCalibrationCancelled;
            }
        }

        private void OnDisable()
        {
            if (calibrationSession != null)
            {
                calibrationSession.OnPhaseChanged -= HandlePhaseChanged;
                calibrationSession.OnCalibrationComplete -= HandleCalibrationComplete;
                calibrationSession.OnCalibrationCancelled -= HandleCalibrationCancelled;
            }
        }

        private void Update()
        {
            if (!isVisible || calibrationSession == null) return;

            var phase = calibrationSession.CurrentPhase;

            if (phase == CalibrationPhase.Complete || phase == CalibrationPhase.NotStarted)
                return;

            // Update countdown
            float remaining = Mathf.Max(0f, calibrationSession.PhaseDuration - calibrationSession.PhaseElapsed);
            if (countdownText != null)
                countdownText.text = $"{Mathf.CeilToInt(remaining)}s";

            // Update phase progress bar
            if (phaseProgressFill != null && calibrationSession.PhaseDuration > 0f)
                phaseProgressFill.fillAmount = Mathf.Clamp01(calibrationSession.PhaseElapsed / calibrationSession.PhaseDuration);

            // Update overall progress bar
            if (overallProgressFill != null && calibrationSession.TotalDuration > 0f)
                overallProgressFill.fillAmount = Mathf.Clamp01(calibrationSession.TotalElapsed / calibrationSession.TotalDuration);
        }

        public void Show()
        {
            isVisible = true;
            lastPhase = CalibrationPhase.NotStarted;
            if (canvasGroup != null)
            {
                canvasGroup.alpha = 1f;
                canvasGroup.interactable = true;
                canvasGroup.blocksRaycasts = true;
            }
            PositionInFront();
            ShowPrerequisites();
        }

        private void ShowPrerequisites()
        {
            if (phaseCounterText != null)
                phaseCounterText.text = "PREREQUISITES";

            if (phaseNameText != null)
            {
                phaseNameText.text = "STARTING CALIBRATION";
                phaseNameText.color = AccentColor;
            }

            if (instructionsText != null)
                instructionsText.text = "Ensure Genrobot grippers are powered on\nand MCAP recording is already started.\n\nStart gripper recording BEFORE Quest recording.\nBoth L+R controller poses captured automatically.";

            if (countdownText != null)
                countdownText.text = "";
        }

        public void Hide()
        {
            isVisible = false;
            if (canvasGroup != null)
            {
                canvasGroup.alpha = 0f;
                canvasGroup.interactable = false;
                canvasGroup.blocksRaycasts = false;
            }
            // Move off-screen
            transform.position = new Vector3(1000f, 1000f, 1000f);
        }

        private void HandlePhaseChanged(CalibrationPhase phase)
        {
            if (phase == CalibrationPhase.Complete)
            {
                ShowComplete();
                return;
            }

            var (name, instructions) = GetPhaseInfo(phase);
            int index = GetPhaseIndex(phase);

            if (phaseCounterText != null)
                phaseCounterText.text = $"PHASE {index} / 4";

            if (phaseNameText != null)
                phaseNameText.text = name;

            if (instructionsText != null)
                instructionsText.text = instructions;

            if (countdownText != null)
                countdownText.text = $"{calibrationSession.PhaseDuration:F0}s";

            if (phaseProgressFill != null)
                phaseProgressFill.fillAmount = 0f;

            if (cancelButton != null)
                cancelButton.SetActive(true);

            // Haptic + audio feedback for phase transition
            if (lastPhase != CalibrationPhase.NotStarted)
            {
                PlayPhaseTransitionFeedback();
            }

            lastPhase = phase;
        }

        private void HandleCalibrationComplete()
        {
            PlayCompletionFeedback();
            StartCoroutine(AutoHideAfterDelay(5f));
        }

        private void HandleCalibrationCancelled()
        {
            Hide();
        }

        private void ShowComplete()
        {
            if (phaseCounterText != null)
                phaseCounterText.text = "";

            if (phaseNameText != null)
            {
                phaseNameText.text = "COMPLETE";
                phaseNameText.color = CompleteColor;
            }

            if (instructionsText != null)
                instructionsText.text = "Calibration recording saved.\nRemember to also stop Genrobot MCAP recording.\nYou may resume normal operation.";

            if (countdownText != null)
                countdownText.text = "";

            if (phaseProgressFill != null)
                phaseProgressFill.fillAmount = 1f;

            if (overallProgressFill != null)
                overallProgressFill.fillAmount = 1f;

            if (cancelButton != null)
                cancelButton.SetActive(false);
        }

        private IEnumerator AutoHideAfterDelay(float delay)
        {
            yield return new WaitForSeconds(delay);
            Hide();
        }

        private void PlayPhaseTransitionFeedback()
        {
            // Haptic pulse on both controllers (150ms)
            StartCoroutine(HapticPulse(0.5f, 0.5f, 0.15f));

            if (phaseTransitionClip != null && audioSource != null)
                audioSource.PlayOneShot(phaseTransitionClip);
        }

        private void PlayCompletionFeedback()
        {
            // Longer haptic for completion (300ms)
            StartCoroutine(HapticPulse(0.8f, 0.8f, 0.3f));

            if (completionClip != null && audioSource != null)
                audioSource.PlayOneShot(completionClip);
        }

        private IEnumerator HapticPulse(float frequency, float amplitude, float duration)
        {
            OVRInput.SetControllerVibration(frequency, amplitude, OVRInput.Controller.All);
            yield return new WaitForSeconds(duration);
            OVRInput.SetControllerVibration(0f, 0f, OVRInput.Controller.All);
        }

        private void PositionInFront()
        {
            if (cameraTransform == null) return;

            Vector3 cameraPos = cameraTransform.position;
            Vector3 forward = cameraTransform.forward;
            Vector3 position = cameraPos + forward * displayDistance;

            transform.position = position;

            // Face the camera (keep upright)
            Vector3 directionToCamera = cameraPos - position;
            directionToCamera.y = 0;
            if (directionToCamera.sqrMagnitude > 0.001f)
            {
                transform.rotation = Quaternion.LookRotation(-directionToCamera.normalized, Vector3.up);
            }
        }

        // --- UI Construction ---

        private void BuildUI()
        {
            var canvasRt = canvas!.GetComponent<RectTransform>();

            // Root panel
            var panel = CreatePanel("Panel", canvasRt);
            var panelRt = panel.GetComponent<RectTransform>();
            panelRt.anchorMin = Vector2.zero;
            panelRt.anchorMax = Vector2.one;
            panelRt.sizeDelta = Vector2.zero;
            panelRt.offsetMin = Vector2.zero;
            panelRt.offsetMax = Vector2.zero;

            var panelImg = panel.AddComponent<Image>();
            panelImg.color = PanelBg;

            var layout = panel.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(20, 20, 16, 16);
            layout.spacing = 10;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childAlignment = TextAnchor.UpperCenter;

            // Phase counter
            phaseCounterText = CreateTextElement("PhaseCounter", "PHASE 1 / 4", 18, FontStyles.Normal, DimTextColor, panel.transform);

            // Phase name
            phaseNameText = CreateTextElement("PhaseName", "STATIONARY BIAS", 24, FontStyles.Bold, AccentColor, panel.transform);

            // Instructions
            instructionsText = CreateTextElement("Instructions", "", 16, FontStyles.Normal, TextColor, panel.transform);

            // Spacer
            CreateSpacer(panel.transform, 8);

            // Countdown
            countdownText = CreateTextElement("Countdown", "10s", 48, FontStyles.Bold, TextColor, panel.transform);
            if (countdownText != null)
                countdownText.alignment = TextAlignmentOptions.Center;

            // Spacer
            CreateSpacer(panel.transform, 4);

            // Phase progress bar
            var phaseBarLabel = CreateTextElement("PhaseBarLabel", "Phase", 12, FontStyles.Normal, DimTextColor, panel.transform);
            phaseProgressFill = CreateProgressBar("PhaseProgress", ProgressFill, panel.transform);

            // Overall progress bar
            var overallBarLabel = CreateTextElement("OverallBarLabel", "Overall", 12, FontStyles.Normal, DimTextColor, panel.transform);
            overallProgressFill = CreateProgressBar("OverallProgress", OverallProgressFill, panel.transform);

            // Spacer
            CreateSpacer(panel.transform, 8);

            // Cancel button
            cancelButton = CreateCancelButton(panel.transform);
        }

        private GameObject CreatePanel(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return go;
        }

        private TextMeshProUGUI CreateTextElement(string name, string text, float fontSize, FontStyles style, Color color, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI));
            go.transform.SetParent(parent, false);

            var tmp = go.GetComponent<TextMeshProUGUI>();
            tmp.text = text;
            tmp.fontSize = fontSize;
            tmp.fontStyle = style;
            tmp.color = color;
            tmp.enableWordWrapping = true;
            tmp.alignment = TextAlignmentOptions.Center;
            if (font != null) tmp.font = font;

            return tmp;
        }

        private Image CreateProgressBar(string name, Color fillColor, Transform parent)
        {
            // Background
            var bgGo = new GameObject(name, typeof(RectTransform), typeof(Image));
            bgGo.transform.SetParent(parent, false);

            var bgImg = bgGo.GetComponent<Image>();
            bgImg.color = ProgressBg;

            var bgLayout = bgGo.AddComponent<LayoutElement>();
            bgLayout.preferredHeight = 12;

            // Fill (child, anchored left, fill amount controls width)
            var fillGo = new GameObject("Fill", typeof(RectTransform), typeof(Image));
            fillGo.transform.SetParent(bgGo.transform, false);

            var fillImg = fillGo.GetComponent<Image>();
            fillImg.color = fillColor;
            fillImg.type = Image.Type.Filled;
            fillImg.fillMethod = Image.FillMethod.Horizontal;
            fillImg.fillAmount = 0f;

            var fillRt = fillGo.GetComponent<RectTransform>();
            fillRt.anchorMin = Vector2.zero;
            fillRt.anchorMax = Vector2.one;
            fillRt.sizeDelta = Vector2.zero;
            fillRt.offsetMin = Vector2.zero;
            fillRt.offsetMax = Vector2.zero;

            return fillImg;
        }

        private GameObject CreateCancelButton(Transform parent)
        {
            var btnGo = new GameObject("CancelBtn", typeof(RectTransform), typeof(Image), typeof(Button));
            btnGo.transform.SetParent(parent, false);

            var btnImg = btnGo.GetComponent<Image>();
            btnImg.color = CancelBtnColor;

            var btnLayout = btnGo.AddComponent<LayoutElement>();
            btnLayout.preferredHeight = 40;

            var textGo = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
            textGo.transform.SetParent(btnGo.transform, false);

            var textRt = textGo.GetComponent<RectTransform>();
            textRt.anchorMin = Vector2.zero;
            textRt.anchorMax = Vector2.one;
            textRt.sizeDelta = Vector2.zero;

            var tmp = textGo.GetComponent<TextMeshProUGUI>();
            tmp.text = "Cancel";
            tmp.fontSize = 18;
            tmp.fontStyle = FontStyles.Bold;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = Color.white;
            if (font != null) tmp.font = font;

            var btn = btnGo.GetComponent<Button>();
            btn.onClick.AddListener(() =>
            {
                if (calibrationSession != null)
                    calibrationSession.CancelCalibration();
            });

            return btnGo;
        }

        private void CreateSpacer(Transform parent, float height)
        {
            var go = new GameObject("Spacer", typeof(RectTransform));
            go.transform.SetParent(parent, false);

            var layoutElement = go.AddComponent<LayoutElement>();
            layoutElement.preferredHeight = height;
        }

        private static (string name, string instructions) GetPhaseInfo(CalibrationPhase phase) => phase switch
        {
            CalibrationPhase.StationaryBias => (
                "STATIONARY BIAS",
                "Place both grippers flat on a stable surface.\nDo NOT touch them.\nEstimating gyroscope DC bias."
            ),
            CalibrationPhase.StaticOrientations => (
                "STATIC ORIENTATION HOLDS",
                "Pick up ONE gripper at a time.\nHold genuinely still at 5+ orientations, ~3s each:\nUpright · 45° L/R · 45° Fwd/Back · Upside-down\nRest elbow on surface. No hand tremor."
            ),
            CalibrationPhase.DynamicExcitation => (
                "DYNAMIC EXCITATION",
                "Wave each gripper VIGOROUSLY through all axes.\nRoll · Pitch · Yaw · Figure-8 patterns\nMove FAST — higher angular velocity = better SNR.\nCover all 3 rotation axes or calibration degrades."
            ),
            CalibrationPhase.FinalStationary => (
                "FINAL STATIONARY",
                "Place both grippers back on the surface.\nDo NOT touch them.\nValidation check against Phase 1."
            ),
            _ => ("", "")
        };

        private static int GetPhaseIndex(CalibrationPhase phase) => phase switch
        {
            CalibrationPhase.StationaryBias => 1,
            CalibrationPhase.StaticOrientations => 2,
            CalibrationPhase.DynamicExcitation => 3,
            CalibrationPhase.FinalStationary => 4,
            _ => 0
        };
    }
}
