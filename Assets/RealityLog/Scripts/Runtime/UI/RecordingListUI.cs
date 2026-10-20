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

        private Coroutine? buildCoroutine;

        private void OnRecordingsUpdated(List<RecordingListManager.RecordingInfo> recordings)
        {
            Debug.Log($"[{Constants.LOG_TAG}] RecordingListUI: OnRecordingsUpdated called with {recordings.Count} recordings");

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

            ShowPodCard();

            if (buildCoroutine != null)
            {
                StopCoroutine(buildCoroutine);
            }
            buildCoroutine = StartCoroutine(BuildRows(new List<RecordingListManager.RecordingInfo>(recordings)));

            // Delete All button disabled for now
            // if (recordings.Count > 0 && listContainer != null)
            // {
            //     CreateDeleteAllButton(recordings.Count);
            // }
        }

        /// <summary>
        /// Fills the rows a few per frame, reusing the rows already built and creating only the
        /// missing ones; rows past the end are hidden, not destroyed. It pauses while an episode
        /// records: the pose loggers sample on the main thread, and a stall there costs the
        /// cameras their head-pose coverage.
        /// </summary>
        private System.Collections.IEnumerator BuildRows(List<RecordingListManager.RecordingInfo> recordings)
        {
            int created = 0;
            int inFrame = 0;
            for (int i = 0; i < recordings.Count; i++)
            {
                while (listManager != null && listManager.RecordingActive)
                {
                    yield return null;
                }

                RecordingListItemUI? itemUI;
                if (i < itemInstances.Count && itemInstances[i] != null)
                {
                    itemUI = itemInstances[i];
                    itemUI.CollapseIfExpanded();
                    itemUI.gameObject.SetActive(true);
                }
                else
                {
                    var itemObj = Instantiate(recordingItemPrefab, listContainer);
                    itemUI = itemObj.GetComponent<RecordingListItemUI>();
                    if (itemUI == null)
                    {
                        Debug.LogWarning($"[{Constants.LOG_TAG}] RecordingListUI: Prefab doesn't have RecordingListItemUI component!");
                        Destroy(itemObj);
                        buildCoroutine = null;
                        yield break;
                    }
                    itemUI.OnDeleteClicked += HandleDelete;
                    itemUI.OnExportClicked += HandleExport;
                    itemUI.OnToggleExpand += HandleAccordion;
                    if (i < itemInstances.Count) itemInstances[i] = itemUI;
                    else itemInstances.Add(itemUI);
                    created++;
                }
                itemUI.SetRecording(recordings[i]);

                if (++inFrame >= RecordingListRefreshGate.RowsPerFrame)
                {
                    inFrame = 0;
                    yield return null;
                }
            }

            // Rows past the end stay built for the next refresh, hidden.
            for (int i = recordings.Count; i < itemInstances.Count; i++)
            {
                if (itemInstances[i] != null)
                {
                    itemInstances[i].CollapseIfExpanded();
                    itemInstances[i].gameObject.SetActive(false);
                }
            }
            shownCount = recordings.Count;
            buildCoroutine = null;
            Debug.Log($"[{Constants.LOG_TAG}] RecordingListUI: Showing {recordings.Count} rows ({created} created)");
        }

        private int shownCount;

        private PodCardView? podCard;

        /// <summary>
        /// The Pod card first in the list (PodCardView): status, sign-in, Scan QR, Local only and
        /// the manual setup. Built once, so a list refresh keeps its expanded state, and styled
        /// with the recording list item's rounded sprite and font.
        /// </summary>
        private void ShowPodCard()
        {
            if (listContainer == null) return;
            if (podCard == null)
            {
                if (RigCoordinator.Instance is null && PodSetupController.Instance is null) return;
                var sprite = recordingItemPrefab != null && recordingItemPrefab.TryGetComponent<Image>(out var image) ? image.sprite : null;
                var font = recordingItemPrefab != null ? recordingItemPrefab.GetComponentInChildren<TextMeshProUGUI>(true)?.font : null;
                podCard = PodCardView.Create(listContainer, sprite, font, error => OnOperationComplete("Local only", false, error));
            }
            podCard.transform.SetAsFirstSibling();
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
            for (int i = 0; i < shownCount && i < itemInstances.Count; i++)
            {
                if (itemInstances[i] != null)
                    dirs.Add(itemInstances[i].GetDirectoryName());
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
                buttonText.text = $"Delete All ({shownCount})";
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

