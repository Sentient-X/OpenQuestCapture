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
        /// <summary>False only when a Factory floor says no collector is signed in.</summary>
        public bool CollectorReady;
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
            if (!facts.CollectorReady)
            {
                return SignInToRecord();
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

        /// <summary>The seal's operator in Local only mode, or on a headset not enrolled: no collector signs in there.</summary>
        public const string LocalOperatorId = "local-only";

        /// <summary>An enrolled headset with no collector signed in: a start is refused.</summary>
        public static RigRefusal SignInToRecord() => new RigRefusal("collector_signed_out",
            "Sign in to record: no collector is signed in on this headset; POST /api/sign-in and approve the code");

        /// <summary>
        /// The operator_id an episode's seal names, as sx-pod's does: on an enrolled headset
        /// that is not Local only, the signed-in collector (<paramref name="collectorName"/>,
        /// the Factory's display name), whatever a start body says, and null when nobody is
        /// signed in (refuse with <see cref="SignInToRecord"/>). Otherwise no collector can sign
        /// in: the start body's <c>operator_id</c> when it names one, else
        /// <see cref="LocalOperatorId"/>. Never pod_config.json.
        /// </summary>
        public static string? SealOperatorId(bool enrolled, bool localOnly, string? collectorName, string? bodyOperatorId)
        {
            if (enrolled && !localOnly)
            {
                return collectorName?.Trim() is { Length: > 0 } name ? name : null;
            }
            return bodyOperatorId?.Trim() is { Length: > 0 } named ? named : LocalOperatorId;
        }

        /// <summary>
        /// The sx_rig_start_armed episode body. The seal needs operator_id
        /// (<see cref="SealOperatorId"/>), quest_apk_build_id and quest_session_dir.
        /// </summary>
        public static JObject EpisodeBody(string sessionName, string operatorId, string apkBuildId,
            string sessionDir, string appVersion, string? taskAssignmentId, string? mode)
        {
            var episode = new JObject
            {
                ["episode_id"] = sessionName,
                ["operator_id"] = operatorId,
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
