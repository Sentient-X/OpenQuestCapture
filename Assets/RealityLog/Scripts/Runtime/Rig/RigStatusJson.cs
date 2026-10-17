# nullable enable

using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RealityLog.Rig
{
    /// <summary>
    /// Reads libsx_rig's status and check JSON. Every reader tolerates a missing key, a JSON
    /// null and an unexpected type (local mode's status has <c>"floor": null</c>) and never
    /// throws. No UnityEngine dependency, so it is tested on the host
    /// (tools/sx-rig-stub/test_binding.sh).
    /// </summary>
    public static class RigStatusJson
    {
        /// <summary>
        /// floor.operator.signed_in, or null when the status carries no floor (local mode, not
        /// enrolled): no collector, and none required.
        /// </summary>
        public static bool? CollectorSignedIn(JObject? status) =>
            status?["floor"] is JObject floor
            && floor["operator"] is JObject collector
            && collector["signed_in"] is JValue { Type: JTokenType.Boolean } signedIn
                ? (bool?)(bool)signedIn
                : null;

        /// <summary>The <c>state</c> of one seal verdict ("sealed", "refused", "pending"), or "".</summary>
        public static string SealState(JToken? verdict) =>
            verdict is JObject fields && fields["state"] is JValue { Type: JTokenType.String } state
                ? (string?)state ?? ""
                : "";

        /// <summary>
        /// A library error's <c>refusal</c>: its <c>kind</c> ("" when none) and, for
        /// check_failed, its <c>problems</c> joined.
        /// </summary>
        public static (string kind, string problems) Refusal(string errorJson)
        {
            try
            {
                if (JToken.Parse(errorJson) is JObject error && error["refusal"] is JObject refusal)
                {
                    var kind = refusal["kind"] is JValue { Type: JTokenType.String } k ? (string?)k ?? "" : "";
                    var problems = refusal["problems"] is JArray list
                        ? string.Join("; ", list.Select(p => p.Type == JTokenType.String ? (string?)p : p.ToString(Formatting.None)))
                        : "";
                    return (kind, problems.Length > 0 ? problems : "the rig's device check did not pass");
                }
            }
            catch (JsonException)
            {
            }
            return ("", "");
        }

        /// <summary>
        /// A sx_rig_check report: whether it passed (<c>"ready": true</c>) and its problems
        /// joined for a sentence. A report that is not an object, or has no ready true, did
        /// not pass: the start never proceeds on a check it cannot read.
        /// </summary>
        public static (bool ready, string problems) Check(string reportJson)
        {
            JObject? report;
            try
            {
                report = JToken.Parse(reportJson) as JObject;
            }
            catch (JsonException)
            {
                return (false, "the device check answered unreadable JSON");
            }
            if (report == null)
            {
                return (false, "the device check answered no report");
            }
            var ready = report["ready"] is JValue { Type: JTokenType.Boolean } flag && (bool)flag;
            var problems = report["problems"] is JArray list
                ? string.Join("; ", list.Select(p => p.Type == JTokenType.String ? (string?)p : p.ToString(Formatting.None)))
                : "";
            if (!ready && problems.Length == 0)
            {
                problems = "the device check did not report ready";
            }
            return (ready, problems);
        }
    }
}
