# nullable enable

using System.Collections.Generic;
using UnityEngine;
using TMPro;
using RealityLog.Common;
using RealityLog.FileOperations;

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

            // Clear existing items
            foreach (var item in itemInstances)
            {
                if (item != null)
                    Destroy(item.gameObject);
            }
            itemInstances.Clear();

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
                    itemInstances.Add(itemUI);
                    createdCount++;
                }
                else
                {
                    Debug.LogWarning($"[{Constants.LOG_TAG}] RecordingListUI: Prefab doesn't have RecordingListItemUI component!");
                }
            }

            Debug.Log($"[{Constants.LOG_TAG}] RecordingListUI: Created {createdCount} list items");
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

