#nullable enable

using TMPro;
using UnityEngine;
using RealityLog.Rig;

namespace RealityLog.UI
{
    /// <summary>Keeps the recordings menu's Pod setup rows in step with PodSetupController.</summary>
    public class PodSetupRowsView : MonoBehaviour
    {
        private PodSetupController? setup;
        private TextMeshProUGUI? status, scan, site, auth, code, enroll;

        public void Bind(PodSetupController controller, TextMeshProUGUI statusText, TextMeshProUGUI scanText,
            TextMeshProUGUI siteText, TextMeshProUGUI authText, TextMeshProUGUI codeText, TextMeshProUGUI enrollText)
        {
            auth = authText;
            setup = controller;
            status = statusText;
            scan = scanText;
            site = siteText;
            code = codeText;
            enroll = enrollText;
            Refresh();
        }

        private void Update() => Refresh();

        private void Refresh()
        {
            if (setup == null) return;
            if (status != null) status.text = "Pod setup: " + setup.Status;
            if (scan != null) scan.text = setup.Scanning ? "Cancel scan" : "Scan QR code";
            if (site != null) site.text = $"Factory URL: {setup.FactoryUrl}";
            if (auth != null) auth.text = $"Auth URL: {setup.AuthUrl}";
            if (code != null) code.text = setup.JoinCode.Length > 0 ? "Join code: entered" : "Join code: tap to type";
            if (enroll != null) enroll.text = setup.Busy && !setup.Scanning ? "Enrolling..." : "Enroll";
        }
    }
}
