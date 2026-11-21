# nullable enable

using System;
using Newtonsoft.Json.Linq;

namespace RealityLog.Rig
{
    /// <summary>What a start found before the device check (all read at the moment of the start).</summary>
    public struct RigStartFacts
    {
        public bool EpisodeBusy;
        public string? BusySession;
        public bool LibraryMissing;
        public bool Opened;
        public string? OpenErrorJson;
        public string[] AwaitingPermission;
        public bool ApkBuildIdKnown;
        /// <summary>
        /// The rig's <c>tracking.start_refusal</c> (<c>{"kind", "detail", "notice"}</c>) judged on
        /// the controllers block handed over right before the check; null when the tracking lets
        /// a start go ahead or the rig does not judge it (PICO, an older library).
        /// </summary>
        public string? TrackingRefusalJson;
    }

    /// <summary>
    /// Whether a rig start may arm, as one pure decision: a precondition refusal, else the device
    /// check, run every time (no rig state ever skips it), and only a check whose report says
    /// <c>"ready": true</c> lets the start arm. Whether the rig takes part at all is the
    /// caller's decision, made once before this; this gate never revisits it. No UnityEngine
    /// dependency, so it is tested on the host (tools/sx-rig-stub/test_binding.sh).
    /// </summary>
    public static class RigStartGate
    {
        /// <summary>
        /// Null when the start may arm. <paramref name="runCheck"/> returns the check report or
        /// throws <see cref="SxRigException"/>; <paramref name="recordCheck"/> receives what the
        /// check answered (its report, or <c>{"ready": false, "error": ...}</c>).
        /// </summary>
        public static RigRefusal? Evaluate(RigStartFacts facts, Func<string> runCheck, Action<string> recordCheck)
        {
            if (facts.EpisodeBusy)
            {
                return new RigRefusal("rig_start_failed", $"rig episode '{facts.BusySession ?? "?"}' is still active");
            }
            if (facts.LibraryMissing)
            {
                return new RigRefusal("rig_library_missing",
                    "rig devices are configured or plugged in, but this build has no libsx_rig.so", facts.OpenErrorJson);
            }
            if (!facts.Opened)
            {
                return new RigRefusal("rig_open_failed", "sx_rig_open failed", facts.OpenErrorJson);
            }
            if (facts.AwaitingPermission is { Length: > 0 } awaiting)
            {
                return new RigRefusal("rig_usb_permission",
                    "no USB permission for " + string.Join(", ", awaiting) +
                    "; someone must accept the camera permissions and the USB prompt in the headset once");
            }
            if (!facts.ApkBuildIdKnown)
            {
                return new RigRefusal("rig_identity_pending",
                    "the installed APK's build id is still being computed; retry in a few seconds");
            }
            // Before the check (seconds) and before OQC's session exists: a start the rig's
            // tracking law refuses leaves no stopped session behind (sx#4210).
            if (Tracking(facts.TrackingRefusalJson) is { } tracking)
            {
                return tracking;
            }
            string report;
            try
            {
                report = runCheck();
            }
            catch (SxRigException ex)
            {
                recordCheck("{\"ready\":false,\"error\":" + ex.ErrorJson + "}");
                return FromError(ex, "rig_check_failed");
            }
            recordCheck(report);
            // A check that answers is not a check that passed.
            var (ready, problems) = RigStatusJson.Check(report);
            return ready ? null : new RigRefusal("rig_check_failed", "the device check did not pass: " + problems, report);
        }

        /// <summary>
        /// The refusal kinds of the rig's tracking law (sx_pod_core::tracking, sx_rig.h
        /// sx_rig_observe_tracking), each also its refusal code and its notice's code.
        /// </summary>
        public static readonly string[] TrackingKinds =
        {
            "controllers_not_in_hand", "controllers_wrong_side", "body_tracking_unavailable", "head_tracking_stopped",
        };

        /// <summary>
        /// A rig <c>tracking.start_refusal</c> (or an error's <c>refusal</c>) of one of
        /// <see cref="TrackingKinds"/> as a refusal coded by its kind, carrying the JSON (with the
        /// rig's notice, when it has one); null for anything else.
        /// </summary>
        public static RigRefusal? Tracking(string? startRefusalJson)
        {
            if (string.IsNullOrEmpty(startRefusalJson)) return null;
            JObject? refusal;
            try { refusal = JToken.Parse(startRefusalJson!) as JObject; }
            catch (Newtonsoft.Json.JsonException) { return null; }
            var kind = refusal?["kind"] is JValue { Type: JTokenType.String } k ? (string?)k ?? "" : "";
            if (Array.IndexOf(TrackingKinds, kind) < 0) return null;
            var detail = refusal!["detail"] is JValue { Type: JTokenType.String } d ? (string?)d ?? "" : "";
            return new RigRefusal(kind, detail.Length > 0 ? detail : kind.Replace('_', ' '), startRefusalJson);
        }

        /// <summary>
        /// The sx_rig_start_armed episode body: quest_apk_build_id and quest_session_dir for the
        /// seal. It names no operator: the rig seals the collector of the pod's Factory shift
        /// (the paired phone's person), or none when the pod records unattributed.
        /// </summary>
        public static JObject EpisodeBody(string sessionName, string apkBuildId,
            string sessionDir, string appVersion, string? taskAssignmentId, string? mode)
        {
            var episode = new JObject
            {
                ["episode_id"] = sessionName,
                ["quest_apk_build_id"] = apkBuildId,
                ["quest_session_dir"] = sessionDir,
                ["app_version"] = appVersion,
            };
            if (taskAssignmentId != null) episode["task_assignment_id"] = taskAssignmentId;
            if (mode != null) episode["mode"] = mode;
            return episode;
        }

        /// <summary>
        /// A rig error as a refusal: SX_RIG_E_STORAGE is rig_storage_low; a refusal of kind
        /// check_failed (sx_rig_start_armed running the check itself) is rig_check_failed with
        /// its problems; anything else is <paramref name="otherwise"/>.
        /// </summary>
        public static RigRefusal FromError(SxRigException ex, string otherwise)
        {
            if (ex.Error == SxRigError.Storage)
            {
                return new RigRefusal("rig_storage_low",
                    "not enough free space for a worst-case episode and the unsealed backlog", ex.ErrorJson);
            }
            var (kind, problems) = RigStatusJson.Refusal(ex.ErrorJson);
            if (kind == "check_failed")
            {
                return new RigRefusal("rig_check_failed", "the device check did not pass: " + problems, ex.ErrorJson);
            }
            if (Array.IndexOf(TrackingKinds, kind) >= 0)
            {
                // The library judged the controllers block handed over right before the start;
                // the error's notice (sx_rig_last_error) is the operator's words for it.
                return new RigRefusal(kind, ex.Message, ex.ErrorJson);
            }
            if (kind == "factory_unanswered")
            {
                // The library bounds a start's Factory exchange; nothing was recorded.
                return new RigRefusal("factory_unanswered",
                    "the Factory did not answer the start in time; nothing was recorded, start again", ex.ErrorJson);
            }
            return new RigRefusal(otherwise, ex.Message, ex.ErrorJson);
        }
    }
}
