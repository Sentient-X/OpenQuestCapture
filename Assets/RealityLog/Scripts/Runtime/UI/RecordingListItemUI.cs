#nullable enable

using System;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using RealityLog.Common;

namespace RealityLog.UI
{
    public class RecordingListItemUI : MonoBehaviour
    {
        [Header("UI Elements")]
        [Tooltip("Text component displaying the recording directory name")]
        [SerializeField] private TextMeshProUGUI directoryNameText = default!;

        [Tooltip("Text component displaying the recording date/time")]
        [SerializeField] private TextMeshProUGUI dateText = default!;

        [Tooltip("Text component displaying the recording size")]
        [SerializeField] private TextMeshProUGUI sizeText = default!;

        [Header("Buttons")]
        [Tooltip("Button to delete this recording")]
        [SerializeField] private Button deleteButton = default!;

        [Tooltip("Button to export this recording (compress and move to Downloads)")]
        [SerializeField] private Button exportButton = default!;

        [Header("Detail Panel")]
        [Tooltip("TMP font asset for detail panel text")]
        [SerializeField] private TMP_FontAsset? detailFont;

        private string recordingDirectoryName = string.Empty;
        private RecordingListManager.RecordingInfo? recordingInfo;
        private bool isExpanded;
        private GameObject? detailPanelObj;
        private RecordingDetailPanelUI? detailPanel;
        private GameObject? healthDotObj;

        public event Action<string>? OnDeleteClicked;
        public event Action<string>? OnExportClicked;
        public event Action<RecordingListItemUI>? OnToggleExpand;

        private void Awake()
        {
            if (deleteButton != null)
                deleteButton.onClick.AddListener(() => OnDeleteClicked?.Invoke(recordingDirectoryName));

            if (exportButton != null)
                exportButton.onClick.AddListener(() => OnExportClicked?.Invoke(recordingDirectoryName));

            // Add click handler on the whole row for expand/collapse
            var rowButton = GetComponent<Button>();
            if (rowButton == null)
                rowButton = gameObject.AddComponent<Button>();

            // Make the button transparent (no visual change on click)
            var nav = rowButton.navigation;
            nav.mode = Navigation.Mode.None;
            rowButton.navigation = nav;
            rowButton.transition = Selectable.Transition.None;

            rowButton.onClick.AddListener(ToggleExpand);
        }

        public void SetRecording(RecordingListManager.RecordingInfo info)
        {
            recordingInfo = info;
            recordingDirectoryName = info.DirectoryName;

            if (directoryNameText != null)
                directoryNameText.text = info.DirectoryName;

            if (dateText != null)
                dateText.text = info.FormattedDate;

            if (sizeText != null)
            {
                string statusSuffix = info.UploadStatusText switch
                {
                    "compressing" => " | Compressing...",
                    "uploading" => " | Uploading...",
                    "pending" => " | Pending upload",
                    "uploaded" => " | Uploaded",
                    "failed" => " | Upload failed",
                    _ => ""
                };
                sizeText.text = $"{info.FormattedSize} | {info.FormattedDuration}{statusSuffix}";
            }

            // Create health dot next to the directory name
            if (healthDotObj != null)
            {
                Destroy(healthDotObj);
                healthDotObj = null;
            }

            if (directoryNameText != null)
            {
                healthDotObj = RecordingHealthIndicator.CreateDot(
                    directoryNameText.transform.parent ?? transform,
                    info.QuickHealth,
                    14f
                );
                // Place it as first sibling so it appears before the text
                healthDotObj.transform.SetAsFirstSibling();
            }
        }

        public string GetDirectoryName() => recordingDirectoryName;

        public void ToggleExpand()
        {
            if (isExpanded)
                HideDetailPanel();
            else
                ShowDetailPanel();

            OnToggleExpand?.Invoke(this);
        }

        public void CollapseIfExpanded()
        {
            if (isExpanded)
                HideDetailPanel();
        }

        private void ShowDetailPanel()
        {
            if (recordingInfo == null || isExpanded) return;

            isExpanded = true;

            var data = RecordingDetailData.Parse(recordingInfo.FullPath);

            // Create detail panel as a sibling below this row in the layout
            detailPanelObj = new GameObject("DetailPanel", typeof(RectTransform));
            detailPanelObj.transform.SetParent(transform.parent, false);
            int siblingIndex = transform.GetSiblingIndex() + 1;
            detailPanelObj.transform.SetSiblingIndex(siblingIndex);

            detailPanel = detailPanelObj.AddComponent<RecordingDetailPanelUI>();
            detailPanel.Build(data, detailFont);

            // Wire detail panel button events
            detailPanel.OnDeleteClicked += () => OnDeleteClicked?.Invoke(recordingDirectoryName);
            detailPanel.OnExportClicked += () => OnExportClicked?.Invoke(recordingDirectoryName);
        }

        private void HideDetailPanel()
        {
            isExpanded = false;

            if (detailPanel != null)
            {
                detailPanel.Cleanup();
                detailPanel = null;
            }

            if (detailPanelObj != null)
            {
                Destroy(detailPanelObj);
                detailPanelObj = null;
            }
        }

        private void OnDestroy()
        {
            HideDetailPanel();

            if (healthDotObj != null)
            {
                Destroy(healthDotObj);
                healthDotObj = null;
            }
        }
    }
}
