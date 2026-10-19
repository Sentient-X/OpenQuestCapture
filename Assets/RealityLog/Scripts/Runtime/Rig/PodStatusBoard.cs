# nullable enable

using System;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RealityLog.Rig
{
    /// <summary>The Pod card's status chip: what its colour means.</summary>
    public enum PodReadiness
    {
        /// <summary>Enrolled, a collector signed in, and the rig (when one is expected) checked ready.</summary>
        Ready,
        /// <summary>Not set up, signed out, or the rig not checked ready yet.</summary>
        NotReady,
        /// <summary>The last start was refused, or the rig failed to open or load.</summary>
        Refused,
        /// <summary>Local only: the headset never contacts the Factory.</summary>
        LocalOnly,
    }

    /// <summary>The recordings menu's Pod card: its six lines, its chip and a pending sign-in code.</summary>
    public readonly struct PodStatusLines
    {
        public string Pod { get; }
        public string Collector { get; }
        public string Task { get; }
        public string Rig { get; }
        public string LastRefusal { get; }
        public string LastEpisode { get; }
        /// <summary>The pod's id, or "" when not enrolled.</summary>
        public string PodName { get; }
        public PodReadiness Readiness { get; }
        /// <summary>The chip's word for <see cref="Readiness"/>.</summary>
        public string ReadinessText { get; }
        /// <summary>True when there is a refusal to show (LastRefusal is not "none").</summary>
        public bool HasRefusal { get; }
        /// <summary>The collector's sign-in code, only while the sign-in is pending; else "".</summary>
        public string SignInCode { get; }
        /// <summary>Where to approve <see cref="SignInCode"/>; "" when unknown or not pending.</summary>
        public string SignInAddress { get; }

        public PodStatusLines(string pod, string collector, string task, string rig, string lastRefusal, string lastEpisode,
            string podName, PodReadiness readiness, string readinessText, bool hasRefusal, string signInCode, string signInAddress)
        {
            Pod = pod;
            Collector = collector;
            Task = task;
            Rig = rig;
            LastRefusal = lastRefusal;
            LastEpisode = lastEpisode;
            PodName = podName;
            Readiness = readiness;
            ReadinessText = readinessText;
            HasRefusal = hasRefusal;
            SignInCode = signInCode;
            SignInAddress = signInAddress;
        }
    }

    /// <summary>A refused start: its code, its sentence, and when (null when the rig's status said it).</summary>
    public sealed class StartRefusal
    {
        public string Code { get; }
        public string Message { get; }
        public DateTime? At { get; }

        public StartRefusal(string code, string message, DateTime? at)
        {
            Code = code;
            Message = message;
            At = at;
        }
    }

    /// <summary>
    /// The outcome of the last POST /api/start-recording (the HTTP route and the left menu
    /// button): a refusal with its time, cleared by a start that succeeds. Any thread.
    /// </summary>
    public sealed class PodStartLog
    {
        public static readonly PodStartLog Shared = new();

        private readonly object gate = new();
        private StartRefusal? last;
        private bool succeeded;

        /// <summary>The last start's refusal, or null (none yet, or the last start succeeded).</summary>
        public StartRefusal? Last { get { lock (gate) return last; } }

        /// <summary>True once a start succeeded after any refusal; the rig's own refusal is then stale.</summary>
        public bool Succeeded { get { lock (gate) return succeeded; } }

        /// <summary>Records a start's answer: 2xx clears, anything else is the refusal its body names.</summary>
        public void Record(int statusCode, string? body, DateTime at)
        {
            lock (gate)
            {
                if (statusCode is >= 200 and < 300)
                {
                    last = null;
                    succeeded = true;
                    return;
                }
                var (code, message) = PodStatusBoard.ParseError(body, statusCode);
                last = new StartRefusal(code, message, at);
                succeeded = false;
            }
        }
    }

    /// <summary>
    /// What the recordings menu's Pod card says, from RigCoordinator's /api/status rig
    /// object (pod id, floor, seals, uplink, lastCheck, lastRefusal, native) and the last start's
    /// refusal. Pure: no UnityEngine, so it is tested on the host (tools/sx-rig-stub/test_binding.sh).
    /// Every reader tolerates a missing key, a null and an unexpected type.
    /// </summary>
    public static class PodStatusBoard
    {
        /// <summary>How long a step's message stays on the Pod setup line before the current state shows.</summary>
        public const double SetupMessageSeconds = 60;

        public static PodStatusLines Lines(string? rigStatusJson, StartRefusal? lastStart, bool lastStartSucceeded,
            bool askingForCode)
        {
            JObject? status = null;
            try
            {
                status = string.IsNullOrEmpty(rigStatusJson) ? null : JToken.Parse(rigStatusJson!) as JObject;
            }
            catch (JsonException)
            {
            }
            var refusal = RefusalLine(status, lastStart, lastStartSucceeded);
            var hasRefusal = refusal != NoRefusal;
            var (readiness, readinessText) = Readiness(status, hasRefusal);
            var (code, address) = PendingSignIn(status, askingForCode);
            var enrolled = Bool(status, "enrolled") == true;
            return new PodStatusLines(
                PodLine(status),
                CollectorLine(status, askingForCode),
                TaskLine(status),
                RigLine(status),
                refusal,
                EpisodeLine(status),
                enrolled ? Str(status?["podId"]) : "",
                readiness, readinessText, hasRefusal, code, address);
        }

        private const string NoRefusal = "Last refusal: none";

        /// <summary>
        /// The chip: Local only first; then not set up; a rig that failed to load or open is an
        /// error; a signed-in collector with the rig checked ready (or no rig expected) is ready;
        /// else a standing refusal shows as refused, and anything else is not ready.
        /// </summary>
        public static (PodReadiness readiness, string text) Readiness(JObject? status, bool hasRefusal)
        {
            if (Bool(status, "localOnly") ?? true) return (PodReadiness.LocalOnly, "Local only");
            if (Bool(status, "enrolled") != true) return (PodReadiness.NotReady, "Not set up");
            var rigExpected = Bool(status, "expected") == true;
            if (rigExpected && (Str(status!["library"]) == "missing" || (Bool(status, "open") != true && status["openError"] is JObject)))
            {
                return (PodReadiness.Refused, "Rig error");
            }
            var signedIn = status!["floor"] is JObject floor && floor["operator"] is JObject collector
                && collector["signed_in"] is JValue { Type: JTokenType.Boolean } flag && (bool)flag;
            var rigReady = !rigExpected
                || (Bool(status, "open") == true && status["lastCheck"] is JObject check && RigStatusJson.Check(check.ToString(Formatting.None)).ready);
            if (signedIn && rigReady) return (PodReadiness.Ready, "Ready");
            return hasRefusal ? (PodReadiness.Refused, "Refused") : (PodReadiness.NotReady, "Not ready");
        }

        /// <summary>The collector's sign-in code and address, only when the Collector line shows them; else ("", "").</summary>
        public static (string code, string address) PendingSignIn(JObject? status, bool askingForCode)
        {
            if (askingForCode || Bool(status, "enrolled") != true || (Bool(status, "localOnly") ?? true)) return ("", "");
            if (status?["floor"] is not JObject floor || floor["operator"] is not JObject collector) return ("", "");
            if (collector["signed_in"] is JValue { Type: JTokenType.Boolean } signedIn && (bool)signedIn) return ("", "");
            var code = Str(collector["user_code"]);
            return code.Length == 0 ? ("", "") : (code, Str(collector["verification_uri_complete"]));
        }

        /// <summary>
        /// A line split for the card's label and value columns: "Rig: ready" -> ("Rig", "ready");
        /// a time in the label moves to the value ("Last refusal (14:03:07): x" -> ("Last refusal",
        /// "14:03:07 · x")), as does an episode's name ("Last episode e1: sealed" -> ("Last episode", "e1 · sealed")).
        /// </summary>
        public static (string label, string value) Field(string line)
        {
            var colon = line.IndexOf(": ", StringComparison.Ordinal);
            if (colon < 0) return ("", line);
            var label = line.Substring(0, colon);
            var value = line.Substring(colon + 2);
            var open = label.IndexOf(" (", StringComparison.Ordinal);
            if (open > 0 && label.EndsWith(")", StringComparison.Ordinal))
            {
                return (label.Substring(0, open), label.Substring(open + 2, label.Length - open - 3) + " · " + value);
            }
            const string episode = "Last episode ";
            if (label.StartsWith(episode, StringComparison.Ordinal))
            {
                return ("Last episode", label.Substring(episode.Length) + " · " + value);
            }
            return (label, value);
        }

        public static string PodLine(JObject? status)
        {
            var localOnly = Bool(status, "localOnly") ?? true;
            var local = "Local only " + (localOnly ? "ON" : "OFF");
            if (Bool(status, "enrolled") != true) return "Pod: not enrolled · " + local;
            var pod = Str(status?["podId"]);
            return "Pod: " + (pod.Length > 0 ? pod : "enrolled") + " · " + local;
        }

        /// <summary>
        /// floor.operator: signed in (with the collector's name), a pending sign-in (its code
        /// and address, shown only while the floor says pending), or signed out (with the
        /// floor's message, e.g. the code expired).
        /// </summary>
        public static string CollectorLine(JObject? status, bool askingForCode)
        {
            if (Bool(status, "enrolled") != true) return "Collector: enroll this headset first";
            if (Bool(status, "localOnly") ?? true) return "Collector: not needed (Local only)";
            if (askingForCode) return "Collector: asking the Factory for a sign-in code...";
            if (status?["floor"] is not JObject floor || floor["operator"] is not JObject collector)
            {
                var floorError = Str((status?["native"] as JObject)?["floor_error"]);
                return "Collector: waiting for the Factory" + (floorError.Length > 0 ? " (" + Clip(floorError, 60) + ")" : "");
            }
            if (collector["signed_in"] is JValue { Type: JTokenType.Boolean } signedIn && (bool)signedIn)
            {
                var name = Str(collector["display_name"]);
                return name.Length > 0 ? "Collector: signed in as " + name : "Collector: signed in";
            }
            var code = Str(collector["user_code"]);
            if (code.Length > 0)
            {
                var address = Str(collector["verification_uri_complete"]);
                return "Collector: sign-in pending, approve code " + code + (address.Length > 0 ? " at " + address : "");
            }
            var message = Str(floor["message"]);
            return "Collector: signed out" + (message.Length > 0 ? " (" + Clip(message, 60) + ")" : "") + ", tap Sign in collector";
        }

        /// <summary>
        /// floor.collection: the collection the rig records under, its status and episodes. The
        /// rig's status carries no task name or target, so neither is shown.
        /// </summary>
        public static string TaskLine(JObject? status)
        {
            if (Bool(status, "enrolled") != true) return "Task: none (not enrolled)";
            if (Bool(status, "localOnly") ?? true) return "Task: none (Local only records without one)";
            if (status?["floor"] is not JObject floor) return "Task: unknown until the Factory answers";
            if (floor["collection"] is not JObject collection) return "Task: no collection open; the first start opens the assigned task's";
            var state = Str(collection["status"]).Replace('_', ' ');
            var episodes = collection["episodes"] is JArray list ? list.Count : 0;
            return "Task: collection " + (state.Length > 0 ? state : "open") + ", " + episodes
                + (episodes == 1 ? " episode" : " episodes");
        }

        /// <summary>The rig: not expected, library missing, not open, or the last check (ready, or its problems), with the device count.</summary>
        public static string RigLine(JObject? status)
        {
            if (status == null || Bool(status, "expected") != true) return "Rig: not expected (no rig configured or plugged in)";
            if (Str(status["library"]) == "missing") return "Rig: the rig library is missing from this build";
            var devices = DeviceCount(status);
            var count = devices == 1 ? "1 device" : devices + " devices";
            if (Bool(status, "open") != true)
            {
                var error = status["openError"] is JObject openError ? Str(openError["detail"]) : "";
                return "Rig: not open" + (error.Length > 0 ? ": " + Clip(error, 80) : "") + " · " + count;
            }
            if (status["lastCheck"] is not JObject check) return "Rig: not checked yet (a start checks it) · " + count;
            var (ready, problems) = RigStatusJson.Check(check.ToString(Formatting.None));
            if (ready) return "Rig: ready · " + count;
            if (check["error"] is JObject error2 && Str(error2["detail"]) is { Length: > 0 } detail) problems = detail;
            return "Rig: not ready: " + Clip(problems, 90) + " · " + count;
        }

        /// <summary>
        /// The last start's refusal in plain words with its time; else the rig's own last
        /// refusal (no time) unless a start succeeded since; else none.
        /// </summary>
        public static string RefusalLine(JObject? status, StartRefusal? lastStart, bool lastStartSucceeded)
        {
            var refusal = lastStart;
            if (refusal == null && !lastStartSucceeded && status?["lastRefusal"] is JObject rig)
            {
                refusal = new StartRefusal(Str(rig["code"]), Str(rig["message"]), null);
            }
            if (refusal == null) return NoRefusal;
            var at = refusal.At is DateTime time ? " (" + time.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + ")" : "";
            return "Last refusal" + at + ": " + Clip(Explain(refusal.Code, refusal.Message), 110);
        }

        /// <summary>A start refusal's code and message in plain words.</summary>
        public static string Explain(string code, string message)
        {
            var lower = message.ToLowerInvariant();
            if (lower.Contains("readiness checks are not passing")) return "The Factory's readiness checks are not passing";
            if (lower.Contains("no assigned task")) return "No task assigned to this pod";
            string Detail(string prefix) => message.Length > 0 ? prefix + ": " + message : prefix;
            return code switch
            {
                "collector_signed_out" => "Collector signed out: sign in first",
                "rig_check_failed" => Detail("The rig's device check failed"),
                "rig_storage_low" or "storage_full" => "Storage is too low to record",
                "rig_usb_permission" => "Allow USB access to the rig's devices",
                "rig_library_missing" => "The rig library is missing from this build",
                "rig_open_failed" => Detail("The rig did not open"),
                "rig_identity_pending" => Detail("The rig's identity is not known yet"),
                "rig_not_armed" => Detail("The rig was not armed"),
                "pod_config_incomplete" => Detail("Pod setup is incomplete"),
                "rig_start_failed" => Detail("The rig did not start"),
                "already_recording" => "Already recording",
                "app_paused" => "The app was paused (headset off the face?)",
                "main_thread_timeout" => "The app did not start the recording in time",
                _ => message.Length > 0 ? message : code.Length > 0 ? code : "the start was refused",
            };
        }

        /// <summary>
        /// The recording episode (native.episode) or the newest seal verdict (seals, keyed by the
        /// session name, which sorts by time), with the uplink's last pass.
        /// </summary>
        public static string EpisodeLine(JObject? status)
        {
            if (status?["native"] is JObject native && native["episode"] is JObject running
                && Str(running["episode_id"]) is { Length: > 0 } recording)
            {
                return "Episode: recording " + recording;
            }
            string line;
            if (status?["seals"] is JObject seals && seals.Properties().Any())
            {
                var newest = seals.Properties().OrderBy(p => p.Name, StringComparer.Ordinal).Last();
                var verdict = RigStatusJson.SealSummary(newest.Value.ToString(Formatting.None), null, null);
                line = "Last episode " + newest.Name + ": " + (verdict.Length > 0 ? verdict : "no verdict");
            }
            else
            {
                line = "Last episode: none sealed yet";
            }
            var upload = UploadSummary(status);
            return upload.Length > 0 ? line + " · " + upload : line;
        }

        /// <summary>uplink: "upload: starting", its first error, "upload: ok", or "" without an uplink.</summary>
        public static string UploadSummary(JObject? status)
        {
            if (status?["uplink"] is not JObject uplink) return "";
            var state = Str(uplink["state"]);
            if (state != "idle") return "upload: " + (state.Length > 0 ? state : "unknown");
            if (uplink["log"] is not JArray log || log.Count == 0) return "upload: nothing to send";
            foreach (var entry in log.OfType<JObject>())
            {
                var error = Str(entry["error"]);
                if (error.Length == 0)
                {
                    var result = entry["result"];
                    error = result is JObject outcome ? Str(outcome["Err"]) : Str(result);
                }
                if (error.Length > 0) return "upload failed: " + Clip(error, 60);
            }
            return "upload: ok";
        }

        /// <summary>
        /// The Pod setup line: a step's message while a step runs or for
        /// <see cref="SetupMessageSeconds"/> after it, then the current state.
        /// </summary>
        public static string SetupStatus(string? lastMessage, double messageAgeSeconds, bool busy, bool enrolled, bool localOnly)
        {
            if (SetupMessage(lastMessage, messageAgeSeconds, busy) is { } message) return message;
            return enrolled
                ? "Enrolled as a pod. Local only is " + (localOnly ? "on" : "off") + ". Scan a new code only to enroll again."
                : "Not set up: scan the console's QR code, or type the join code.";
        }

        /// <summary>A step's message while the step runs or for <see cref="SetupMessageSeconds"/> after it; else null.</summary>
        public static string? SetupMessage(string? lastMessage, double messageAgeSeconds, bool busy) =>
            lastMessage != null && (busy || messageAgeSeconds < SetupMessageSeconds) ? lastMessage : null;

        /// <summary>An HTTP error body's code (reason, else error) and message.</summary>
        public static (string code, string message) ParseError(string? body, int statusCode)
        {
            try
            {
                if (!string.IsNullOrEmpty(body) && JToken.Parse(body!) is JObject error)
                {
                    var reason = Str(error["reason"]);
                    return (reason.Length > 0 ? reason : Str(error["error"]), Str(error["message"]));
                }
            }
            catch (JsonException)
            {
            }
            return ("http_" + statusCode.ToString(CultureInfo.InvariantCulture), "");
        }

        private static int DeviceCount(JObject status)
        {
            if (status["native"] is JObject native && native["devices"] is JArray devices) return devices.Count;
            if (status["lastCheck"] is JObject check && check["devices"] is JArray checkDevices) return checkDevices.Count;
            return status["usb"] is JObject usb && usb["attached"] is JArray attached ? attached.Count : 0;
        }

        private static bool? Bool(JObject? status, string key) =>
            status?[key] is JValue { Type: JTokenType.Boolean } value ? (bool?)(bool)value : null;

        private static string Str(JToken? token) =>
            token is JValue { Type: JTokenType.String } value ? (string?)value ?? "" : "";

        private static string Clip(string text, int max) =>
            text.Length <= max ? text : text.Substring(0, max - 3) + "...";
    }
}
