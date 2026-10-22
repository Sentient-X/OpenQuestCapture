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

            recordingCount = recordings.Count;

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
            ShowTab(tab);

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
        private int recordingCount;

        /// <summary>The menu's two pages: the Pod card or the recordings list, never both at once.</summary>
        public enum MenuTab
        {
            Pod,
            Recordings,
        }

        private MenuTab tab = MenuTab.Pod;
        private ScrollRect? podScroll;
        private ScrollRect? listScroll;
        private Image? podTabImage, recordingsTabImage;
        private TextMeshProUGUI? podTabText, recordingsTabText;

        // The menu's layout, in the panel's units (1000 x 1000, centred): the title on top, the
        // tabs under it, then the page down to the Calibrate button.
        private const float TabsTop = 92, TabsHeight = 50, PageTop = 156, PageBottom = 100;
        private static readonly Color TabOn = new(0.2f, 0.5f, 0.85f, 1f);
        private static readonly Color TabOff = new(0.3137255f, 0.3137255f, 0.3137255f, 1f);

        /// <summary>The page the menu shows now.</summary>
        public MenuTab Tab => tab;

        /// <summary>The Pod page's scroll view, once built.</summary>
        public ScrollRect? PodScroll => podScroll;

        /// <summary>The recordings list's scroll view.</summary>
        public ScrollRect? ListScroll => listScroll;

        /// <summary>
        /// The Pod page (PodCardView in a scroll view of its own) and the Pod | Recordings tabs.
        /// Built once, so a list refresh keeps the card's expanded state, and styled with the
        /// recording list item's rounded sprite and font.
        /// </summary>
        private void ShowPodCard()
        {
            if (podCard == null && (RigCoordinator.Instance is not null || PodSetupController.Instance is not null))
            {
                BuildPodPage();
            }
        }

        /// <summary>
        /// Builds the Pod page and the tabs (the Editor snapshot calls it without a rig). The
        /// page is a copy of the list's ScrollRect, emptied, so it scrolls, clips and looks the
        /// same; both pages sit under the tabs, and only the shown one is active.
        /// </summary>
        public PodCardView? BuildPodPage()
        {
            if (podCard != null) return podCard;
            if (listContainer == null) return null;
            listScroll = listContainer.GetComponentInParent<ScrollRect>(true);
            if (listScroll == null) return null;
            var panel = (RectTransform)listScroll.transform.parent;

            // Nothing on the panel but the controls takes the ray: a full-panel text (the empty
            // message, 800 x 800, drawn over the list) swallowed every click and drag under it.
            foreach (Transform child in panel)
            {
                if (child.TryGetComponent<TMP_Text>(out var label)) label.raycastTarget = false;
            }
            Place((RectTransform)listScroll.transform, PageTop, PageBottom);
            if (emptyListMessage != null && emptyListMessage.transform is RectTransform empty)
            {
                Place(empty, 220, 580); // near the top of the list's page
            }

            var page = Instantiate(listScroll.gameObject, panel);
            page.name = "Pod Scroll View";
            page.transform.SetSiblingIndex(listScroll.transform.GetSiblingIndex() + 1);
            podScroll = page.GetComponent<ScrollRect>();
            var content = podScroll.content;
            for (var i = content.childCount - 1; i >= 0; i--)
            {
                var child = content.GetChild(i).gameObject;
                if (!Application.isPlaying)
                {
                    DestroyImmediate(child);
                    continue;
                }
                child.transform.SetParent(null, false);
                Destroy(child);
            }
            if (content.TryGetComponent<VerticalLayoutGroup>(out var column))
            {
                column.padding = new RectOffset(8, 8, 4, 12);
                column.childControlHeight = true;
                column.childForceExpandHeight = false;
                column.childControlWidth = true;
                column.childForceExpandWidth = true;
            }

            var sprite = recordingItemPrefab != null && recordingItemPrefab.TryGetComponent<Image>(out var image) ? image.sprite : null;
            var font = recordingItemPrefab != null ? recordingItemPrefab.GetComponentInChildren<TextMeshProUGUI>(true)?.font : null;
            podCard = PodCardView.Create(content, sprite, font, error => OnOperationComplete("Local only", false, error));

            BuildTabs(panel, sprite, font);
            ShowTab(tab);
            return podCard;
        }

        /// <summary>Shows one page: the Pod card, or the list with its empty message when it has no rows.</summary>
        public void ShowTab(MenuTab shown)
        {
            tab = shown;
            if (podScroll == null || listScroll == null)
            {
                if (emptyListMessage != null) emptyListMessage.SetActive(recordingCount == 0);
                return;
            }
            var pod = shown == MenuTab.Pod;
            podScroll.gameObject.SetActive(pod);
            listScroll.gameObject.SetActive(!pod);
            if (emptyListMessage != null) emptyListMessage.SetActive(!pod && recordingCount == 0);
            if (podTabImage != null) podTabImage.color = pod ? TabOn : TabOff;
            if (recordingsTabImage != null) recordingsTabImage.color = pod ? TabOff : TabOn;
            if (podTabText != null) podTabText.color = pod ? Color.white : new Color(0.9f, 0.9f, 0.9f, 1f);
            if (recordingsTabText != null)
            {
                recordingsTabText.color = pod ? new Color(0.9f, 0.9f, 0.9f, 1f) : Color.white;
                recordingsTabText.text = recordingCount > 0 ? $"Recordings ({recordingCount})" : "Recordings";
            }
        }

        private void BuildTabs(RectTransform panel, Sprite? sprite, TMP_FontAsset? font)
        {
            var tabs = new GameObject("Tabs", typeof(RectTransform));
            tabs.transform.SetParent(panel, false);
            var rect = (RectTransform)tabs.transform;
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.sizeDelta = new Vector2(560, TabsHeight);
            rect.anchoredPosition = new Vector2(0, -TabsTop);
            var row = tabs.AddComponent<HorizontalLayoutGroup>();
            row.spacing = 12;
            row.childControlWidth = row.childControlHeight = true;
            row.childForceExpandWidth = row.childForceExpandHeight = true;
            (podTabImage, podTabText) = TabPill(tabs.transform, "Pod", sprite, font, () => ShowTab(MenuTab.Pod));
            (recordingsTabImage, recordingsTabText) = TabPill(tabs.transform, "Recordings", sprite, font, () => ShowTab(MenuTab.Recordings));
        }

        private static (Image, TextMeshProUGUI) TabPill(Transform parent, string word, Sprite? sprite, TMP_FontAsset? font, UnityEngine.Events.UnityAction onClick)
        {
            var pill = new GameObject(word + "Tab", typeof(RectTransform));
            pill.transform.SetParent(parent, false);
            var image = pill.AddComponent<Image>();
            image.sprite = sprite;
            image.type = sprite != null ? Image.Type.Sliced : Image.Type.Simple;
            image.color = TabOff;
            var button = pill.AddComponent<Button>();
            button.targetGraphic = image;
            button.onClick.AddListener(onClick);
            var textObj = new GameObject("Text", typeof(RectTransform));
            textObj.transform.SetParent(pill.transform, false);
            var textRect = (RectTransform)textObj.transform;
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.sizeDelta = Vector2.zero;
            var text = textObj.AddComponent<TextMeshProUGUI>();
            if (font != null) text.font = font;
            text.text = word;
            text.fontSize = 24;
            text.fontStyle = FontStyles.Bold;
            text.alignment = TextAlignmentOptions.Center;
            text.raycastTarget = false;
            return (image, text);
        }

        /// <summary>Stretches <paramref name="rect"/> across the panel from <paramref name="top"/> to <paramref name="bottom"/> (insets).</summary>
        private static void Place(RectTransform rect, float top, float bottom)
        {
            var parent = (RectTransform)rect.parent;
            var height = parent.rect.height > 0 ? parent.rect.height : 1000;
            var width = rect.sizeDelta.x > 0 && rect.anchorMin.x == rect.anchorMax.x ? rect.sizeDelta.x : parent.rect.width;
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(width, height - top - bottom);
            rect.anchoredPosition = new Vector2(0, (bottom - top) / 2);
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

