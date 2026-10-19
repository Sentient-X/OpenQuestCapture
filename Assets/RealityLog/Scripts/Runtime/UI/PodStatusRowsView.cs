#nullable enable

using TMPro;
using UnityEngine;
using RealityLog.Rig;

namespace RealityLog.UI
{
    /// <summary>
    /// Keeps the recordings menu's Pod status block (pod, collector, task, rig, last refusal,
    /// last episode) in step with the rig's status; the words are PodStatusBoard's.
    /// </summary>
    public class PodStatusRowsView : MonoBehaviour
    {
        private TextMeshProUGUI[] rows = System.Array.Empty<TextMeshProUGUI>();
        private string? shownStatus;
        private StartRefusal? shownRefusal;
        private bool shownSucceeded;
        private bool shownAsking;

        /// <summary>Rows in order: pod, collector, task, rig, last refusal, last episode.</summary>
        public void Bind(params TextMeshProUGUI[] texts)
        {
            rows = texts;
            shownStatus = null;
            Refresh(force: true);
        }

        private void Update() => Refresh(force: false);

        private void Refresh(bool force)
        {
            if (rows.Length < 6) return;
            var status = RigCoordinator.StatusJsonOrDefault;
            var refusal = PodStartLog.Shared.Last;
            var succeeded = PodStartLog.Shared.Succeeded;
            var asking = PodSetupController.Instance is { SigningIn: true };
            // The status string is replaced, never mutated: parse only when something changed.
            if (!force && ReferenceEquals(status, shownStatus) && ReferenceEquals(refusal, shownRefusal)
                && succeeded == shownSucceeded && asking == shownAsking)
            {
                return;
            }
            shownStatus = status;
            shownRefusal = refusal;
            shownSucceeded = succeeded;
            shownAsking = asking;
            var lines = PodStatusBoard.Lines(status, refusal, succeeded, asking);
            rows[0].text = lines.Pod;
            rows[1].text = lines.Collector;
            rows[2].text = lines.Task;
            rows[3].text = lines.Rig;
            rows[4].text = lines.LastRefusal;
            rows[5].text = lines.LastEpisode;
        }
    }
}
