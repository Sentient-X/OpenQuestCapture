#nullable enable

using System;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using RealityLog.Common;
using RealityLog.Rig;

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
                directoryNameText.text = info.FriendlyTitle;

            if (dateText != null)
            {
                string episodeText = info.EpisodeMarkerCount > 0
                    ? $" | {info.EpisodeMarkerCount} ep{(info.EpisodeMarkerCount != 1 ? "s" : "")}"
                    : "";
                dateText.text = $"{info.FormattedDuration}{episodeText}";
            }

            if (sizeText != null)
            {
                // The rig's outcome for this recording, when the rig took part: sealed (and the
                // sealed episode.mcap's size), pending, or refused with the reason.
                var rigLine = RigOutcome(info);
                sizeText.text = rigLine.Length == 0 ? info.FormattedSize : $"{info.FormattedSize} | {rigLine}";
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

        private static string RigOutcome(RecordingListManager.RecordingInfo info)
        {
            try
            {
                var episode = RigCoordinator.EpisodeDirectory(Application.persistentDataPath, info.DirectoryName);
                var sealPath = System.IO.Path.Combine(episode, "seal.json");
                var mcapPath = System.IO.Path.Combine(episode, "episode.mcap");
                var refusalPath = System.IO.Path.Combine(info.FullPath, "rig_refusal.json");
                return RigStatusJson.SealSummary(
                    System.IO.File.Exists(sealPath) ? System.IO.File.ReadAllText(sealPath) : null,
                    System.IO.File.Exists(refusalPath) ? System.IO.File.ReadAllText(refusalPath) : null,
                    System.IO.File.Exists(mcapPath) ? new System.IO.FileInfo(mcapPath).Length : (long?)null);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[{Constants.LOG_TAG}] RecordingListItemUI: rig outcome for {info.DirectoryName}: {ex.Message}");
                return "";
            }
        }

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
