# nullable enable

using UnityEngine;
using RealityLog.Common;
using OVR;

namespace RealityLog.UI
{
    /// <summary>
    /// Controls the recording menu visibility with Y button input.
    /// </summary>
    public class RecordingMenuController : MonoBehaviour
    {
        [Header("UI Elements")]
        [Tooltip("The recording menu panel GameObject (should have Canvas with OVROverlayCanvas)")]
        [SerializeField] private GameObject menuPanel = default!;

        [Tooltip("WorldSpaceMenuPositioner component to position menu in front of player when opened")]
        [SerializeField] private WorldSpaceMenuPositioner? menuPositioner = default!;

        [Tooltip("The head-locked hint ('Press the left menu button...'), hidden while the menu is open so it never sits over it")]
        [SerializeField] private GameObject? closedMenuHint;

        [Header("Calibration")]
        [SerializeField] private CalibrationSession? calibrationSession;
        [SerializeField] private CalibrationOverlayUI? calibrationOverlay;

        [Header("Input Settings")]
        [Tooltip("Button to toggle menu (Y button on Quest controllers)")]
        [SerializeField] private OVRInput.Button toggleButton = OVRInput.Button.Four;

        [Tooltip("Controller to check for button input: Touch only. All also reads Android's gamepad, which maps Four to Y a second time")]
        [SerializeField] private OVRInput.Controller controller = OVRInput.Controller.Touch;

        /// <summary>
        /// A press within this long of the last toggle is ignored. On pod5 (2026-11-15) one
        /// collector's Y opened the menu and closed it again 200-360 ms later, five times in a
        /// row (21:15:56-21:17:44): the menu builds 313 rows after opening and the collector,
        /// seeing nothing yet, pressed again; the menu with the refusal and the Y hint never
        /// stayed up. Controller.All also resolved Four through OVRControllerGamepadAndroid.
        /// </summary>
        public const float ToggleDebounceSeconds = 0.75f;

        private bool isMenuVisible = false;
        private float lastToggleAt = float.NegativeInfinity;

        private CanvasGroup? menuCanvasGroup;
        private RecordingListManager? listManager;

        private void Start()
        {
            if (menuPanel != null)
            {
                // Ensure we have a CanvasGroup
                menuCanvasGroup = menuPanel.GetComponent<CanvasGroup>();

                // Ensure the GameObject is active so Oculus tracker works
                menuPanel.SetActive(true);
                
                // Initialize state (hidden)
                UpdateMenuVisibility();
            }

            listManager = GetComponent<RecordingListManager>();
        }

        private void Update()
        {
            if (OVRInput.GetDown(toggleButton, controller))
            {
                var now = Time.unscaledTime;
                if (now - lastToggleAt < ToggleDebounceSeconds)
                {
                    Debug.Log($"[{Constants.LOG_TAG}] RecordingMenuController: press {(now - lastToggleAt) * 1000f:F0} ms after the last toggle ignored (menu stays {(isMenuVisible ? "open" : "closed")})");
                    return;
                }
                lastToggleAt = now;
                ToggleMenu();
            }
        }

        /// <summary>
        /// Toggles the recording menu visibility.
        /// </summary>
        public void ToggleMenu()
        {
            isMenuVisible = !isMenuVisible;
            
            if (menuPanel != null)
            {
                // Only opening refreshes, so closing stays cheap: a start closes the menu
                // (onRecordingStarted -> HideMenu) inside RecordingManager.StartRecording, and a
                // rebuild there stalled the pose loggers. Opened while recording, the refresh
                // waits for the recording to end (RecordingListManager).
                if (isMenuVisible && listManager != null)
                {
                    listManager.RefreshRecordings();
                }
                UpdateMenuVisibility();
                
                if (isMenuVisible)
                {
                    Debug.Log($"[{Constants.LOG_TAG}] RecordingMenuController: Menu opened");
                    
                    // Position menu in front of player when opening
                    if (menuPositioner != null)
                    {
                        menuPositioner.PositionInFront();
                    }
                    else
                    {
                        Debug.LogWarning($"[{Constants.LOG_TAG}] RecordingMenuController: Menu opened but menuPositioner is null!");
                    }
                }
                else
                {
                    Debug.Log($"[{Constants.LOG_TAG}] RecordingMenuController: Menu closed");
                    if (menuPositioner != null)
                    {
                        menuPositioner.PositionAway();
                    }
                }
            }
        }

        private void UpdateMenuVisibility()
        {
            if (menuCanvasGroup != null)
            {
                menuCanvasGroup.alpha = isMenuVisible ? 1f : 0f;
                menuCanvasGroup.interactable = isMenuVisible;
                menuCanvasGroup.blocksRaycasts = isMenuVisible;
            }
            // The hint's animator owns its active state, so it is faded instead: a CanvasGroup
            // on it outlasts the animator's toggles.
            if (closedMenuHint != null)
            {
                if (!closedMenuHint.TryGetComponent<CanvasGroup>(out var hint)) hint = closedMenuHint.AddComponent<CanvasGroup>();
                hint.alpha = isMenuVisible ? 0f : 1f;
                hint.blocksRaycasts = false;
                hint.interactable = false;
            }
        }

        /// <summary>
        /// Shows the recording menu.
        /// </summary>
        public void ShowMenu()
        {
            if (!isMenuVisible)
            {
                ToggleMenu();
            }
        }

        /// <summary>
        /// Starts the guided calibration sequence.
        /// Hides the menu and shows the calibration overlay.
        /// </summary>
        public void StartCalibration()
        {
            if (calibrationSession == null || calibrationSession.IsRunning) return;
            HideMenu();
            calibrationOverlay?.Show();
            calibrationSession.StartCalibration();
        }

        /// <summary>
        /// Hides the recording menu.
        /// </summary>
        public void HideMenu()
        {
            if (isMenuVisible)
            {
                ToggleMenu();
            }
        }

        private void OnValidate()
        {
            if (menuPanel == null)
                Debug.LogWarning($"[{Constants.LOG_TAG}] RecordingMenuController: Missing menu panel GameObject reference!");
            if (menuPositioner == null)
                Debug.LogWarning($"[{Constants.LOG_TAG}] RecordingMenuController: Missing menu positioner reference!");
        }
    }
}
