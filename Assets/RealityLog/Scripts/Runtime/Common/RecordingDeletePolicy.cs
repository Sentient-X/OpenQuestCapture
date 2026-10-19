# nullable enable

using System;
using System.IO;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RealityLog.Common
{
    /// <summary>What a request to delete a recording may do.</summary>
    public enum DeleteVerdict
    {
        /// <summary>The session may be deleted.</summary>
        Allowed,
        /// <summary>Not an OQC session name, or it resolves outside the files root's direct children.</summary>
        InvalidName,
        /// <summary>No such session.</summary>
        NotFound,
        /// <summary>The session's rig episode is still recording, queued or sealing (no settled verdict).</summary>
        RigUnsettled,
        /// <summary>The session's rig episode is sealed: deleting needs confirm=&lt;episode_id&gt;.</summary>
        NeedsConfirm,
    }

    /// <summary>
    /// The one rule for deleting a recording, from the HTTP API and the in-headset menu alike.
    /// Only an OQC session directory (yyyyMMdd_HHmmss, a direct child of the files root) can be
    /// deleted, so rig_staging (the sealed episodes, the only copy: the rig never deletes
    /// locally), the config files and the credentials never can. A session whose rig episode is
    /// not settled is refused; one whose rig episode is sealed needs an explicit confirmation
    /// naming its episode id. Deleting a session never touches its rig episode. No UnityEngine
    /// dependency, so it is tested on the host (tools/sx-rig-stub/test_binding.sh).
    /// </summary>
    public static class RecordingDeletePolicy
    {
        public const string RigStagingDirectoryName = "rig_staging";
        private static readonly Regex SessionName = new(@"^\d{8}_\d{6}$", RegexOptions.Compiled);

        /// <summary>The verdict and a sentence for the caller; <paramref name="path"/> is set when allowed.</summary>
        public static (DeleteVerdict verdict, string message, string? path) Check(string filesRoot, string name, string? confirm)
        {
            if (string.IsNullOrEmpty(name) || !SessionName.IsMatch(name))
            {
                return (DeleteVerdict.InvalidName, $"'{name}' is not a recording session name (yyyyMMdd_HHmmss)", null);
            }
            var root = Path.GetFullPath(filesRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var path = Path.GetFullPath(Path.Combine(root, name));
            var parent = Path.GetDirectoryName(path)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!string.Equals(parent, root, StringComparison.Ordinal) || Path.GetFileName(path) != name)
            {
                return (DeleteVerdict.InvalidName, $"'{name}' does not resolve to a session in the files root", null);
            }
            if (!Directory.Exists(path))
            {
                return (DeleteVerdict.NotFound, $"Recording not found: {name}", null);
            }
            var episode = Path.Combine(root, RigStagingDirectoryName, name);
            if (!Directory.Exists(episode))
            {
                return (DeleteVerdict.Allowed, "", path); // recorded without the rig
            }
            var state = SealState(Path.Combine(episode, "seal.json"));
            switch (state)
            {
                case "refused":
                    return (DeleteVerdict.Allowed, "", path);
                case "sealed":
                    return confirm == name
                        ? (DeleteVerdict.Allowed, "", path)
                        : (DeleteVerdict.NeedsConfirm,
                            $"the rig sealed episode {name} from this recording; delete it with confirm={name} " +
                            "(the sealed episode in rig_staging is kept)", null);
                default:
                    return (DeleteVerdict.RigUnsettled,
                        $"the rig's episode {name} is not settled ({(state.Length == 0 ? "no seal verdict yet" : state)}); " +
                        "wait for its seal verdict", null);
            }
        }

        private static string SealState(string sealPath)
        {
            if (!File.Exists(sealPath)) return "";
            try
            {
                return JToken.Parse(File.ReadAllText(sealPath)) is JObject seal
                    && seal["state"] is JValue { Type: JTokenType.String } state
                    ? (string?)state ?? "" : "unreadable";
            }
            catch (Exception ex) when (ex is IOException || ex is JsonException)
            {
                return "unreadable";
            }
        }
    }
}
