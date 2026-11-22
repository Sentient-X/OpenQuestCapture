# nullable enable

using System;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RealityLog.Rig
{
    /// <summary>
    /// Builds and parses status off the capture thread. Only the newest pending request is
    /// retained. Raw JSON and its read-only tree publish together; readers must not mutate
    /// that tree. The lock covers only pointer swaps, never building/parsing or a callback.
    /// </summary>
    public sealed class RigStatusSnapshot : IDisposable
    {
        public sealed class Value
        {
            public string Json { get; }
            public JObject? Tree { get; }
            public Value(string json, JObject? tree) { Json = json; Tree = tree; }
        }

        private readonly object gate = new();
        private readonly Func<string, JObject?> parse;
        private Func<string>? pending;
        private long generation;
        private bool running;
        private bool disposed;
        private Value? published;

        public RigStatusSnapshot() : this(json => JToken.Parse(json) as JObject) { }
        // A parser seam lets host tests hold a parse while many new snapshots arrive.
        public RigStatusSnapshot(Func<string, JObject?> parse) => this.parse = parse;
        public Value? Latest => Volatile.Read(ref published);
        public JObject? Current => Latest?.Tree;

        public void Offer(string json) => Offer(() => json);

        /// <summary>The callback owns an immutable input snapshot; it must not read Unity or live collections.</summary>
        public void Offer(Func<string> build)
        {
            lock (gate)
            {
                if (disposed) return;
                pending = build;
                if (running) return;
                running = true;
            }
            ThreadPool.QueueUserWorkItem(_ => Drain());
        }

        private void Drain()
        {
            while (true)
            {
                Func<string>? build;
                long acceptedGeneration;
                lock (gate)
                {
                    build = pending;
                    pending = null;
                    acceptedGeneration = generation;
                    if (disposed || build == null)
                    {
                        running = false;
                        return;
                    }
                }
                Value result;
                try
                {
                    var json = build();
                    result = new Value(json, parse(json));
                }
                catch (Exception)
                {
                    // A failed diagnostic build/parse must neither kill the worker nor keep
                    // a stale ready tree. The HTTP form remains valid JSON too.
                    result = new Value("null", null);
                }
                lock (gate)
                {
                    // New offers replace the pending request, not the generation: an
                    // overloaded builder must still publish progress. Only invalidation
                    // or disposal can make an in-flight result unsafe to publish.
                    if (!disposed && acceptedGeneration == generation)
                        Volatile.Write(ref published, result);
                }
            }
        }

        /// <summary>Forget the previous rig generation without waiting for a builder already running.</summary>
        public void Invalidate()
        {
            lock (gate)
            {
                generation++;
                pending = null;
                Volatile.Write(ref published, null);
            }
        }

        public void Dispose()
        {
            lock (gate)
            {
                disposed = true;
                generation++;
                pending = null;
                Volatile.Write(ref published, null);
            }
        }
    }

    /// <summary>One native observation and its lifted summary; both strings cross threads together.</summary>
    public sealed class RigNativeStatusSnapshot
    {
        public static readonly RigNativeStatusSnapshot Empty = new("null", "");
        public string Json { get; }
        public string SummaryJson { get; }

        private RigNativeStatusSnapshot(string json, string summary)
        { Json = json; SummaryJson = summary; }

        public static RigNativeStatusSnapshot Read(string json, RigNativeStatusSnapshot previous) =>
            Read(json, previous, out _);

        /// <summary>The caller consumes a readable observation for cues; the cache retains only strings.</summary>
        public static RigNativeStatusSnapshot Read(string json, RigNativeStatusSnapshot previous, out JObject? readable)
        {
            readable = null;
            JObject? status;
            try { status = JToken.Parse(json) as JObject; }
            catch (JsonException) { return new RigNativeStatusSnapshot(json, previous.SummaryJson); }
            // A busy native read retains the last readable summary, exactly as before.
            if (status == null || (string?)status["state"] == "busy")
                return new RigNativeStatusSnapshot(json, previous.SummaryJson);
            var summary = new JObject
            {
                ["nativeState"] = status["state"], ["podId"] = status["pod_id"],
                ["registeredRig"] = status["registered_rig"], ["seals"] = status["seals"],
                ["uplink"] = status["uplink"], ["floor"] = status["floor"],
                ["embodiment_assets"] = status["embodiment_assets"], ["devices"] = status["devices"],
                ["roles"] = status["roles"], ["attempts"] = status["attempts"], ["factory"] = status["factory"],
                // sx_rig_observe_tracking's verdict: {"start_refusal", "controllers_lost"} (sx#4210).
                ["tracking"] = status["tracking"],
                // The take against its ceiling (30 min unless the Factory set another): {"episode_id", "recording", "phase", "notice"}.
                ["episode_length"] = status["episode_length"],
            }.ToString(Formatting.None);
            readable = status;
            return new RigNativeStatusSnapshot(json, summary);
        }

        public static RigNativeStatusSnapshot Error(string errorJson, RigNativeStatusSnapshot previous) =>
            new("{\"error\":" + errorJson + "}", previous.SummaryJson);
    }

    /// <summary>
    /// Reads libsx_rig's status and check JSON. Every reader tolerates a missing key, a JSON
    /// null and an unexpected type (local mode's status has <c>"floor": null</c>) and never
    /// throws. No UnityEngine dependency, so it is tested on the host
    /// (tools/sx-rig-stub/test_binding.sh).
    /// </summary>
    public static class RigStatusJson
    {
        /// <summary>
        /// The collector the pod records for: <c>floor.collector</c>
        /// (<c>{"id", "name"}</c>), the person whose paired phone opened the Factory shift (or
        /// whom a floor manager put on the pod). Its name, else its id when the name is empty;
        /// null when the pod records unattributed (<c>"collector": null</c>), and when the
        /// status carries no floor (not enrolled, or the Factory not heard yet). The headset never signs anyone
        /// in; the rig seals this collector itself.
        /// </summary>
        public static string? CollectorName(JObject? status)
        {
            if (status?["floor"] is not JObject floor || floor["collector"] is not JObject collector) return null;
            if (Text(collector["name"]) is { } name) return name;
            return Text(collector["id"]);
        }

        /// <summary>
        /// floor.collector.id, the collector's principal id (the subject a collector review is
        /// answered as); null when the pod records unattributed or has no floor.
        /// </summary>
        public static string? CollectorId(JObject? status) =>
            status?["floor"] is JObject floor && floor["collector"] is JObject collector ? Text(collector["id"]) : null;

        private static string? Text(JToken? token) =>
            token is JValue { Type: JTokenType.String } value && ((string?)value)?.Trim() is { Length: > 0 } trimmed
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
