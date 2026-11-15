# nullable enable

using Newtonsoft.Json.Linq;

namespace RealityLog.Rig
{
    /// <summary>What the right gripper's start/stop switch asks of the app, once per hold.</summary>
    public enum GripperSwitchPress
    {
        /// <summary>A new 2 s hold on the right gripper: start or stop, as the menu button would.</summary>
        Toggle,
        /// <summary>A hold libsx_rig refused (its role and firmware name different sides).</summary>
        Refused,
    }

    /// <summary>
    /// Reads libsx_rig's <c>gripper_switch</c> status block (any thread) and says, once per new
    /// hold, what to do. The rig counts only holds of the right gripper (the one leader for
    /// episode start and stop): <c>holds</c> grows by one per hold to act on, <c>refusals</c> by
    /// one per hold it refused. The first block read is a baseline, so a hold counted before the
    /// app (re)started is never replayed; counters that went back (the rig reopened) baseline
    /// again. Several new holds between two reads are one toggle: the app's own recorder state
    /// decides start or stop, never the gripper's firmware.
    /// </summary>
    public sealed class GripperSwitch
    {
        private long? holds;
        private long? refusals;

        /// <summary>What this status block newly asks for, or null.</summary>
        public GripperSwitchPress? Observe(JObject? block)
        {
            if (block == null) return null;
            var newHolds = Count(block["holds"]);
            var newRefusals = Count(block["refusals"]);
            if (newHolds is not long h || newRefusals is not long r) return null;
            var baseline = holds is not long lastHolds || refusals is not long lastRefusals
                || h < lastHolds || r < lastRefusals;
            var held = !baseline && h > holds;
            var refused = !baseline && r > refusals;
            holds = h;
            refusals = r;
            if (held) return GripperSwitchPress.Toggle;
            if (refused) return GripperSwitchPress.Refused;
            return null;
        }

        /// <summary>The notice the rig shows while a role's encoder names the other gripper, or null.</summary>
        public static OperatorNotice? Notice(JObject? block) => OperatorNotice.FromJson(block?["notice"]);

        private static long? Count(JToken? token) =>
            token is JValue { Type: JTokenType.Integer } value ? (long)value : (long?)null;
    }
}
