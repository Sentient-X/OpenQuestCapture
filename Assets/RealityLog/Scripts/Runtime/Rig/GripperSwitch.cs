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

    /// <summary>What the app does with one <see cref="GripperSwitchPress"/>.</summary>
    public enum GripperSwitchAction
    {
        /// <summary>Pulse the right Touch controller and run the menu button's start or stop.</summary>
        StartOrStop,
        /// <summary>Pulse long and buzz: the rig refused the hold.</summary>
        Refuse,
        /// <summary>Nothing at all, no pulse and no cue: a start or stop is already running.</summary>
        Ignore,
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
        private string? sidesSeen;

        /// <summary>
        /// One log line when what the rig reads from the encoders' status lines changes: which
        /// roles' encoders send them (firmware side, switch enabled), or that none does; null when
        /// nothing changed. The proof on a device that the right encoder's <c># {...}</c> status
        /// lines are read, without a line per status poll.
        /// </summary>
        public string? SidesLine(JObject? block)
        {
            if (block?["sides"] is not JArray sides) return null;
            var parts = new System.Collections.Generic.List<string>();
            foreach (var side in sides)
            {
                if (side is not JObject s || s["firmware"] is not JValue { Type: JTokenType.String } firmware) continue;
                var role = s["role"] is JValue { Type: JTokenType.String } r ? r.ToString() : "?";
                var enabled = s["switch_enabled"] is JValue { Type: JTokenType.Boolean } e ? ((bool)e ? "switch enabled" : "switch disabled") : "switch unknown";
                parts.Add($"{role} encoder reads as the {firmware} gripper, {enabled}");
            }
            var seen = parts.Count == 0 ? "no encoder status lines read yet" : string.Join("; ", parts);
            if (seen == sidesSeen) return null;
            sidesSeen = seen;
            return "gripper switch status: " + seen;
        }

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

        /// <summary>
        /// What one hold does. While a start or stop is in progress (a start arming the rig, a
        /// stop sealing) a hold is ignored silently, exactly as the left menu button is then: no
        /// pulse, no cue, so the collector never feels a hold that did nothing.
        /// </summary>
        public static GripperSwitchAction Act(GripperSwitchPress press, bool startOrStopInProgress) =>
            startOrStopInProgress ? GripperSwitchAction.Ignore
            : press == GripperSwitchPress.Refused ? GripperSwitchAction.Refuse
            : GripperSwitchAction.StartOrStop;

        /// <summary>The notice the rig shows while a role's encoder names the other gripper, or null.</summary>
        public static OperatorNotice? Notice(JObject? block) => OperatorNotice.FromJson(block?["notice"]);

        private static long? Count(JToken? token) =>
            token is JValue { Type: JTokenType.Integer } value ? (long)value : (long?)null;
    }
}
