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

        /// <summary>
        /// The signed-in collector's name (floor.operator.display_name while signed_in is true),
        /// the identity sx-pod seals an episode under; null when nobody is signed in.
        /// </summary>
        public static string? CollectorName(JObject? status) =>
            CollectorSignedIn(status) == true
            && status!["floor"]!["operator"]!["display_name"] is JValue { Type: JTokenType.String } name
            && ((string?)name)?.Trim() is { Length: > 0 } trimmed
                ? trimmed
                : null;

        /// <summary>The <c>state</c> of one seal verdict ("sealed", "refused", "pending"), or "".</summary>
        public static string SealState(JToken? verdict) =>
            verdict is JObject fields && fields["state"] is JValue { Type: JTokenType.String } state
                ? (string?)state ?? ""
                : "";

        /// <summary>
        /// The failure of the rig's running episode <paramref name="episodeId"/> as the status
        /// reports it (<c>episode.failure</c>: cause, part, detail), or null while it records
        /// clean, when no episode runs, or when the status names another episode.
        /// </summary>
        public static JObject? EpisodeFailure(JObject? status, string? episodeId)
        {
            if (episodeId == null || status?["episode"] is not JObject episode) return null;
            if (episode["episode_id"] is not JValue { Type: JTokenType.String } id || (string?)id != episodeId) return null;
            return episode["failure"] as JObject;
        }

        /// <summary>A sentence for an enrollment answer: its pod id, or that it enrolled.</summary>
        public static string EnrollmentSummary(string answerJson)
        {
            try
            {
                if (JToken.Parse(answerJson) is JObject answer
                    && answer["pod_id"] is JValue { Type: JTokenType.String } pod)
                {
                    return "pod " + (string?)pod;
                }
            }
            catch (JsonException)
            {
            }
            return "pod commissioned";
        }

        /// <summary>A library error's detail sentence, or <paramref name="fallback"/>.</summary>
        public static string ErrorDetail(string errorJson, string fallback)
        {
            try
            {
                if (JToken.Parse(errorJson) is JObject error
                    && error["detail"] is JValue { Type: JTokenType.String } detail
                    && ((string?)detail)?.Length > 0)
                {
                    return (string)detail!;
                }
            }
            catch (JsonException)
            {
            }
            return fallback;
        }

        /// <summary>True when a stop receipt's outcome is not complete (failed, an error).</summary>
        public static bool StopFailed(string receiptJson)
        {
            try
            {
                return !(JToken.Parse(receiptJson) is JObject receipt
                    && receipt["outcome"] is JValue { Type: JTokenType.String } outcome
                    && (string?)outcome == "complete");
            }
            catch (JsonException)
            {
                return true;
            }
        }

        /// <summary>The failure's detail sentence (or its cause, or a fallback).</summary>
        public static string FailureDetail(JObject failure) =>
            failure["detail"] is JValue { Type: JTokenType.String } detail ? (string?)detail ?? ""
            : failure["cause"] is JValue { Type: JTokenType.String } cause ? (string?)cause ?? ""
            : "the rig's episode failed";

        /// <summary>
        /// One line for a recording's rig outcome in the in-headset list: a refusal or failure
        /// the app recorded beside the session (rig_refusal.json) wins; else the seal verdict
        /// (seal.json) — "sealed 45.2 MB", "pending: ...", "refused: ..."; "" without a rig.
        /// </summary>
        public static string SealSummary(string? sealJson, string? refusalJson, long? mcapBytes)
        {
            static string Clip(string text) => text.Length <= 60 ? text : text.Substring(0, 57) + "...";
            if (refusalJson != null)
            {
                try
                {
                    if (JToken.Parse(refusalJson) is JObject refusal)
                    {
                        var message = refusal["message"] is JValue { Type: JTokenType.String } m ? (string?)m ?? "" : "";
                        return "refused: " + Clip(message.Length > 0 ? message : (string?)refusal["code"] ?? "rig refused");
                    }
                }
                catch (JsonException)
                {
                }
                return "refused";
            }
            if (sealJson == null) return "";
            try
            {
                if (JToken.Parse(sealJson) is not JObject seal) return "seal unreadable";
                var state = SealState(seal);
                var detail = seal["detail"] is JValue { Type: JTokenType.String } d ? (string?)d ?? "" : "";
                var bytes = seal["size_bytes"] is JValue { Type: JTokenType.Integer } size ? (long)size : mcapBytes;
                return state switch
                {
                    "sealed" => bytes is long b ? $"sealed {b / 1048576.0:F1} MB" : "sealed",
                    "" => "seal unreadable",
                    _ => detail.Length > 0 ? $"{state}: {Clip(detail)}" : state,
                };
            }
            catch (JsonException)
            {
                return "seal unreadable";
            }
        }

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
