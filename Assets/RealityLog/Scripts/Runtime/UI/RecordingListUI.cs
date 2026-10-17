# nullable enable

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using RealityLog.Common;
using RealityLog.FileOperations;
using RealityLog.Rig;

namespace RealityLog.UI
{
    /// <summary>
    /// Main UI component that manages the recording list display and operations.
    /// </summary>
    public class RecordingListUI : MonoBehaviour
    {
        [Header("References")]
        [Tooltip("RecordingListManager to get the list of recordings")]
        [SerializeField] private RecordingListManager listManager = default!;

        [Tooltip("RecordingOperations to perform file operations")]
        [SerializeField] private RecordingOperations operations = default!;

        [Header("UI Elements")]
        [Tooltip("Container/ScrollView content for recording list items")]
        [SerializeField] private Transform listContainer = default!;

        [Tooltip("Prefab for recording list item UI")]
        [SerializeField] private GameObject recordingItemPrefab = default!;

        [Tooltip("Text component to show when no recordings are found")]
        [SerializeField] private GameObject emptyListMessage = default!;

        [Tooltip("Text component to show operation status/feedback")]
        [SerializeField] private TextMeshProUGUI statusText = default!;

        private List<RecordingListItemUI> itemInstances = new List<RecordingListItemUI>();
        private Coroutine? statusHideCoroutine;
        private GameObject? deleteAllButtonObj;
        private bool deleteAllConfirmPending;
        private Coroutine? deleteAllResetCoroutine;

        private void OnEnable()
        {
            if (listManager != null)
            {
                listManager.OnRecordingsUpdated += OnRecordingsUpdated;
            }

            if (operations != null)
            {
                operations.OnOperationComplete += OnOperationComplete;
                operations.OnOperationProgress += OnOperationProgress;
            }

            // Ensure status text is hidden initially
            if (statusText != null)
            {
                statusText.text = string.Empty;
            }

            RefreshList();
        }

        private void OnDisable()
        {
            if (listManager != null)
            {
                listManager.OnRecordingsUpdated -= OnRecordingsUpdated;
            }

            if (operations != null)
            {
                operations.OnOperationComplete -= OnOperationComplete;
                operations.OnOperationProgress -= OnOperationProgress;
            }

        }

        /// <summary>
        /// Refreshes the recording list.
        /// </summary>
        public void RefreshList()
        {
            if (listManager != null)
            {
                listManager.RefreshRecordings();
            }
        }

        private void OnRecordingsUpdated(List<RecordingListManager.RecordingInfo> recordings)
        {
            Debug.Log($"[{Constants.LOG_TAG}] RecordingListUI: OnRecordingsUpdated called with {recordings.Count} recordings");

            // Collapse expanded items before destroying to clean up detail panels
            foreach (var item in itemInstances)
            {
                if (item != null)
                    item.CollapseIfExpanded();
            }

            // Clear existing items
            foreach (var item in itemInstances)
            {
                if (item != null)
                    Destroy(item.gameObject);
            }
            itemInstances.Clear();

            // Destroy old Delete All button
            if (deleteAllButtonObj != null)
            {
                Destroy(deleteAllButtonObj);
                deleteAllButtonObj = null;
            }
            deleteAllConfirmPending = false;

            // Show/hide empty message
            if (emptyListMessage != null)
            {
                emptyListMessage.SetActive(recordings.Count == 0);
            }

            // Create list items
            if (recordingItemPrefab == null)
            {
                Debug.LogError($"[{Constants.LOG_TAG}] RecordingListUI: recordingItemPrefab is null!");
                return;
            }

            if (listContainer == null)
            {
                Debug.LogError($"[{Constants.LOG_TAG}] RecordingListUI: listContainer is null!");
                return;
            }

            Debug.Log($"[{Constants.LOG_TAG}] RecordingListUI: Creating {recordings.Count} list items...");
            int createdCount = 0;

            foreach (var recording in recordings)
            {
                var itemObj = Instantiate(recordingItemPrefab, listContainer);
                var itemUI = itemObj.GetComponent<RecordingListItemUI>();
                
                if (itemUI != null)
                {
                    itemUI.SetRecording(recording);
                    itemUI.OnDeleteClicked += HandleDelete;
                    itemUI.OnExportClicked += HandleExport;
                    itemUI.OnToggleExpand += HandleAccordion;
                    itemInstances.Add(itemUI);
                    createdCount++;
                }
                else
                {
                    Debug.LogWarning($"[{Constants.LOG_TAG}] RecordingListUI: Prefab doesn't have RecordingListItemUI component!");
                }
            }

            Debug.Log($"[{Constants.LOG_TAG}] RecordingListUI: Created {createdCount} list items");

            CreatePodSetupRows();
            CreateLocalOnlyToggle();

            // Delete All button disabled for now
            // if (recordings.Count > 0 && listContainer != null)
            // {
            //     CreateDeleteAllButton(recordings.Count);
            // }
        }

        private GameObject? localOnlyToggleObj;
        private readonly List<GameObject> podSetupRows = new();

        /// <summary>
        /// Pod setup at the top of the list: a status line, Scan QR (or Cancel), Site and Join
        /// code (the Quest keyboard) and Enroll (PodSetupController). The texts refresh while the
        /// menu is open.
        /// </summary>
        private void CreatePodSetupRows()
        {
            foreach (var row in podSetupRows) if (row != null) Destroy(row);
            podSetupRows.Clear();
            var setup = PodSetupController.Instance;
            if (setup is null || listContainer == null) return;

            // Created in reverse: each SetAsFirstSibling puts the row on top.
            var enroll = CreateRow("PodSetupEnroll", new Color(0.15f, 0.3f, 0.55f, 0.85f), setup.Enroll, out var enrollText);
            var code = CreateRow("PodSetupJoinCode", new Color(0.3f, 0.3f, 0.3f, 0.85f), setup.EditJoinCode, out var codeText);
            var site = CreateRow("PodSetupSite", new Color(0.3f, 0.3f, 0.3f, 0.85f), setup.EditSite, out var siteText);
            var scan = CreateRow("PodSetupScan", new Color(0.15f, 0.3f, 0.55f, 0.85f),
                () => { if (setup.Scanning) setup.CancelScan(); else setup.StartScan(); }, out var scanText);
            var status = CreateRow("PodSetupStatus", new Color(0.1f, 0.1f, 0.1f, 0.85f), null, out var statusText);
            statusText.enableAutoSizing = true;
            statusText.fontSizeMin = 10;
            statusText.fontSizeMax = 14;
            podSetupRows.AddRange(new[] { enroll, code, site, scan, status });

            var view = status.AddComponent<PodSetupRowsView>();
            view.Bind(setup, statusText, scanText, siteText, codeText, enrollText);
        }

        private GameObject CreateRow(string name, Color color, System.Action? onClick, out TextMeshProUGUI text)
        {
            var row = new GameObject(name, typeof(RectTransform));
            row.transform.SetParent(listContainer, false);
            row.transform.SetAsFirstSibling();
            var layout = row.AddComponent<LayoutElement>();
            layout.preferredHeight = 40;
            layout.flexibleWidth = 1;
            var background = row.AddComponent<Image>();
            background.color = color;
            if (onClick != null)
            {
                var button = row.AddComponent<Button>();
                button.onClick.AddListener(() => onClick());
            }
            var textObj = new GameObject("Text", typeof(RectTransform));
            textObj.transform.SetParent(row.transform, false);
            var textRt = textObj.GetComponent<RectTransform>();
            textRt.anchorMin = Vector2.zero;
            textRt.anchorMax = Vector2.one;
            textRt.sizeDelta = new Vector2(-12, 0);
            text = textObj.AddComponent<TextMeshProUGUI>();
            text.fontSize = 15;
            text.alignment = TextAlignmentOptions.Center;
            text.color = Color.white;
            return row;
        }

        /// <summary>
        /// "Local only: ON|OFF" at the top of the list: on (the default), the headset never
        /// contacts the Factory and the rig records and seals locally; persisted in
        /// pod_config.json (local_only), the same setting as POST /api/settings.
        /// </summary>
        private void CreateLocalOnlyToggle()
        {
            if (localOnlyToggleObj != null)
            {
                Destroy(localOnlyToggleObj);
                localOnlyToggleObj = null;
            }
            var rig = RigCoordinator.Instance;
            if (rig is null || listContainer == null) return;

            localOnlyToggleObj = new GameObject("LocalOnlyToggle", typeof(RectTransform));
            localOnlyToggleObj.transform.SetParent(listContainer, false);
            localOnlyToggleObj.transform.SetAsFirstSibling();
            var layout = localOnlyToggleObj.AddComponent<LayoutElement>();
            layout.preferredHeight = 40;
            layout.flexibleWidth = 1;
            var background = localOnlyToggleObj.AddComponent<Image>();
            var button = localOnlyToggleObj.AddComponent<Button>();

            var textObj = new GameObject("Text", typeof(RectTransform));
            textObj.transform.SetParent(localOnlyToggleObj.transform, false);
            var textRt = textObj.GetComponent<RectTransform>();
            textRt.anchorMin = Vector2.zero;
            textRt.anchorMax = Vector2.one;
            textRt.sizeDelta = Vector2.zero;
            var text = textObj.AddComponent<TextMeshProUGUI>();
            text.fontSize = 16;
            text.alignment = TextAlignmentOptions.Center;
            text.color = Color.white;

            void Show()
            {
                var on = rig.LocalOnly;
                text.text = on ? "Local only: ON  (never contacts the Factory)" : "Local only: OFF  (Factory allowed)";
                background.color = on ? new Color(0.15f, 0.45f, 0.2f, 0.85f) : new Color(0.35f, 0.35f, 0.35f, 0.85f);
            }
            Show();
            button.onClick.AddListener(() =>
            {
                var error = rig.SetLocalOnly(!rig.LocalOnly);
                if (error != null)
                {
                    OnOperationComplete("Local only", false, error);
                }
                Show();
            });
        }

        private void CreateDeleteAllButton(int count)
        {
            deleteAllButtonObj = new GameObject("DeleteAllButton", typeof(RectTransform));
            deleteAllButtonObj.transform.SetParent(listContainer, false);
            deleteAllButtonObj.transform.SetAsFirstSibling();

            var rt = deleteAllButtonObj.GetComponent<RectTransform>();
            rt.sizeDelta = new Vector2(0, 40);

            var layout = deleteAllButtonObj.AddComponent<LayoutElement>();
            layout.preferredHeight = 40;
            layout.flexibleWidth = 1;

            var bgImage = deleteAllButtonObj.AddComponent<Image>();
            bgImage.color = new Color(0.6f, 0.15f, 0.15f, 0.8f);

            var btn = deleteAllButtonObj.AddComponent<Button>();
            var colors = btn.colors;
            colors.highlightedColor = new Color(0.8f, 0.2f, 0.2f, 1f);
            colors.pressedColor = new Color(0.5f, 0.1f, 0.1f, 1f);
            btn.colors = colors;

            var textObj = new GameObject("Text", typeof(RectTransform));
            textObj.transform.SetParent(deleteAllButtonObj.transform, false);

            var textRt = textObj.GetComponent<RectTransform>();
            textRt.anchorMin = Vector2.zero;
            textRt.anchorMax = Vector2.one;
            textRt.sizeDelta = Vector2.zero;

            var text = textObj.AddComponent<TextMeshProUGUI>();
            text.text = $"Delete All ({count})";
            text.fontSize = 16;
            text.alignment = TextAlignmentOptions.Center;
            text.color = Color.white;

            btn.onClick.AddListener(() => HandleDeleteAllClick(text));
        }

        private void HandleDeleteAllClick(TextMeshProUGUI buttonText)
        {
            if (!deleteAllConfirmPending)
            {
                deleteAllConfirmPending = true;
                buttonText.text = "Tap again to confirm";
                buttonText.color = Color.yellow;

                if (deleteAllResetCoroutine != null)
                    StopCoroutine(deleteAllResetCoroutine);
                deleteAllResetCoroutine = StartCoroutine(ResetDeleteAllConfirm(buttonText));
                return;
            }

            deleteAllConfirmPending = false;
            if (deleteAllResetCoroutine != null)
            {
                StopCoroutine(deleteAllResetCoroutine);
                deleteAllResetCoroutine = null;
            }

            // Delete all recordings
            var dirs = new List<string>();
            foreach (var item in itemInstances)
            {
                if (item != null)
                    dirs.Add(item.GetDirectoryName());
            }

            foreach (var dir in dirs)
            {
                if (operations != null)
                    operations.DeleteRecording(dir);
            }

            RefreshList();
        }

        private System.Collections.IEnumerator ResetDeleteAllConfirm(TextMeshProUGUI buttonText)
        {
            yield return new WaitForSeconds(3f);
            deleteAllConfirmPending = false;
            if (buttonText != null)
            {
                buttonText.text = $"Delete All ({itemInstances.Count})";
                buttonText.color = Color.white;
            }
            deleteAllResetCoroutine = null;
        }

        private void HandleAccordion(RecordingListItemUI expandedItem)
        {
            // Accordion: collapse all other items when one expands
            foreach (var item in itemInstances)
            {
                if (item != null && item != expandedItem)
                    item.CollapseIfExpanded();
            }
        }

        private void HandleDelete(string directoryName)
        {
            if (operations != null)
            {
                operations.DeleteRecording(directoryName);
            }
        }

        private void HandleExport(string directoryName)
        {
            if (operations != null)
            {
                operations.ExportRecording(directoryName);
            }
        }

        private void OnOperationComplete(string operation, bool success, string message)
        {
            // Keep export success message for much longer (5 minutes) so user sees it if they took headset off
            float duration = (operation == "Export" && success) ? 1000f : 5f;
            ShowStatus($"{operation}: {message}", success, duration);
            
            // Refresh list after operations that modify files
            if (operation == "Delete")
            {
                RefreshList();
            }
        }

        private void OnOperationProgress(string operation, float progress)
        {
            // Don't auto-hide while progress is updating
            if (statusText != null)
            {
                statusText.text = $"{operation}: {progress:P0}";
                statusText.color = Color.yellow; // Use a different color for in-progress? Or just white/green.
                
                if (statusHideCoroutine != null)
                {
                    StopCoroutine(statusHideCoroutine);
                    statusHideCoroutine = null;
                }
            }
        }

        private void ShowStatus(string message, bool isSuccess, float duration = 5f)
        {
            if (statusText != null)
            {
                statusText.text = message;
                statusText.color = isSuccess ? Color.green : Color.red;

                if (statusHideCoroutine != null)
                {
                    StopCoroutine(statusHideCoroutine);
                }
                statusHideCoroutine = StartCoroutine(HideStatusAfterDelay(duration));
            }
        }

        private System.Collections.IEnumerator HideStatusAfterDelay(float delay)
        {
            yield return new WaitForSeconds(delay);
            if (statusText != null)
            {
                statusText.text = string.Empty;
            }
            statusHideCoroutine = null;
        }

        private void OnValidate()
        {
            if (listManager == null)
                Debug.LogWarning($"[{Constants.LOG_TAG}] RecordingListUI: Missing RecordingListManager reference!");
            
            if (operations == null)
                Debug.LogWarning($"[{Constants.LOG_TAG}] RecordingListUI: Missing RecordingOperations reference!");
            
            if (listContainer == null)
                Debug.LogWarning($"[{Constants.LOG_TAG}] RecordingListUI: Missing list container Transform reference!");
            
            if (recordingItemPrefab == null)
                Debug.LogWarning($"[{Constants.LOG_TAG}] RecordingListUI: Missing recording item prefab reference!");
        }
    }
}

