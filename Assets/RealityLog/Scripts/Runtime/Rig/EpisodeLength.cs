# nullable enable

using Newtonsoft.Json.Linq;

namespace RealityLog.Rig
{
    /// <summary>What the take's ceiling asks of the app, once per episode.</summary>
    public enum EpisodeLengthAction
    {
        /// <summary>A minute before the ceiling: the warning cue; the Pod card shows the notice.</summary>
        Warn,
        /// <summary>At the ceiling: the menu button's own stop, with its stop cue.</summary>
        Stop,
    }

    /// <summary>
    /// Reads libsx_rig's <c>episode_length</c> status block (any thread): the take against its
    /// ceiling (sx-pod-core <c>episode_length::Ceiling</c>: the Factory's per-pod value as
    /// libsx_rig last pulled it when the take started, 30 min by default, warned a minute
    /// before; the block's <c>limit_s</c> and the notice's <c>minutes</c> name it).
    /// The rig only says where the take stands; the app acts, once per episode, on the episode
    /// it is recording: <c>near_limit</c> warns, <c>reached</c> stops it through the menu
    /// button's stop, as it acts on a gripper hold, so the take is sealed and kept like any
    /// other. A block for another episode, or one read while the app records none, asks
    /// nothing.
    /// </summary>
    public sealed class EpisodeLength
    {
        private string? warnedFor;
        private string? stoppedFor;

        /// <summary>What this status block newly asks of <paramref name="recording"/> (the app's episode), or null.</summary>
        public EpisodeLengthAction? Observe(JObject? block, string? recording)
        {
            if (recording == null || block == null) return null;
            if (block["recording"] is not JValue { Type: JTokenType.Boolean } live || !(bool)live) return null;
            if (block["episode_id"] is not JValue { Type: JTokenType.String } id || (string?)id != recording) return null;
            switch (block["phase"] is JValue { Type: JTokenType.String } p ? (string?)p : null)
            {
                case "reached" when stoppedFor != recording:
                    stoppedFor = recording;
                    warnedFor = recording;
                    return EpisodeLengthAction.Stop;
                case "near_limit" when warnedFor != recording:
                    warnedFor = recording;
                    return EpisodeLengthAction.Warn;
                default:
                    return null;
            }
        }

        /// <summary>
        /// The block's notice for the Pod card: while recording, the warning (or the stop being
        /// made); idle, why the last take stopped, until the next start. Null otherwise.
        /// </summary>
        public static OperatorNotice? Notice(JObject? block, bool recording)
        {
            if (block?["recording"] is not JValue { Type: JTokenType.Boolean } live || (bool)live != recording) return null;
            return OperatorNotice.FromJson(block["notice"]);
        }
    }
}
