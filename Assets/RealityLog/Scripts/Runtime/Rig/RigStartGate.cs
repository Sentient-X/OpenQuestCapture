# nullable enable

using System;

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
                return new RigRefusal("collector_signed_out",
                    "no collector is signed in on this headset; POST /api/sign-in and approve the code");
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
            return new RigRefusal(otherwise, ex.Message, ex.ErrorJson);
        }
    }
}
