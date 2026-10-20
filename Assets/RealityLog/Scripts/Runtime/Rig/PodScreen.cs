# nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RealityLog.Rig
{
    /// <summary>What a status reads as: its colour on the Pod card.</summary>
    public enum PodTone
    {
        Good,
        Warning,
        Error,
        Neutral,
    }

    /// <summary>The one thing in the way, and the action that clears it.</summary>
    public readonly struct PodBlocker
    {
        public string Headline { get; }
        public string Action { get; }
        public PodTone Tone { get; }

        public PodBlocker(string headline, string action, PodTone tone)
        {
            Headline = headline;
            Action = action;
            Tone = tone;
        }
    }

    /// <summary>
    /// A device's state in the closed vocabulary the phone companion mirrors
    /// (companion.proto HeadsetDeviceState; the values are its numbers).
    /// </summary>
    public enum PodDeviceState
    {
        Ready = 1,
        Live = 2,
        Starting = 3,
        Silent = 4,
        Missing = 5,
        Failed = 6,
    }

    /// <summary>
    /// One rig device as a row: its name, a short value, a state pill and a hint; and, for the
    /// phone companion, its role, side, port and state.
    /// </summary>
    public readonly struct PodDeviceRow
    {
        public string Name { get; }
        public string Value { get; }
        public string Pill { get; }
        public PodTone Tone { get; }
        /// <summary>What to do about it; "" when nothing.</summary>
        public string Hint { get; }
        /// <summary>The rig role ("left_hand_camera"), or the kind ("camera") before roles are known.</summary>
        public string Role { get; }
        /// <summary>"left", "right", or "" before roles are known.</summary>
        public string Side { get; }
        /// <summary>The USB port, "" when not attached or unknown.</summary>
        public string Port { get; }
        public PodDeviceState State { get; }

        public PodDeviceRow(string name, string value, string pill, PodTone tone, string hint,
            string role, string side, string port, PodDeviceState state)
        {
            Name = name;
            Value = value;
            Pill = pill;
            Tone = tone;
            Hint = hint;
            Role = role;
            Side = side;
            Port = port;
            State = state;
        }
    }

    /// <summary>
    /// One attempt as a row: its id, how many of the six pipeline steps it completed
    /// (<see cref="PodScreenBoard.Steps"/>), the step it failed at (or -1), its stage word and detail.
    /// </summary>
    public readonly struct PodEpisodeRow
    {
        public string Id { get; }
        public int Reached { get; }
        public int FailedAt { get; }
        public string Stage { get; }
        public PodTone Tone { get; }
        /// <summary>What the card shows beneath the row: the failure, with its fix when one is known.</summary>
        public string Detail { get; }
        /// <summary>The action that clears a failure we know; "" when none.</summary>
        public string Fix { get; }

        public PodEpisodeRow(string id, int reached, int failedAt, string stage, PodTone tone, string detail, string fix = "")
        {
            Id = id;
            Reached = reached;
            FailedAt = failedAt;
            Stage = stage;
            Tone = tone;
            Detail = detail;
            Fix = fix;
        }
    }

    /// <summary>The Pod card's status screen: the blocker, the devices, the collection and the episodes.</summary>
    public sealed class PodScreen
    {
        public PodBlocker Blocker { get; set; }
        public List<PodDeviceRow> Devices { get; } = new();
        /// <summary>A line under the devices (no rig resolved yet, a USB prompt); "" when none.</summary>
        public string DevicesNote { get; set; } = "";
        public string Task { get; set; } = "";
        public string Progress { get; set; } = "";
        public string Next { get; set; } = "";
        public string Link { get; set; } = "";
        public PodTone LinkTone { get; set; } = PodTone.Neutral;
        public List<PodEpisodeRow> Episodes { get; } = new();
        // The collection's numbers and the link, for the phone companion (Progress and Link word them).
        public long Credited { get; set; }
        /// <summary>The task's target; 0 when it has none or no task is known.</summary>
        public long Requested { get; set; }
        public long AwaitingReview { get; set; }
        public bool LinkOk { get; set; }
        /// <summary>Seconds since the Factory last heard the headset; null when it never has.</summary>
        public long? HeardSecondsAgo { get; set; }
        /// <summary>True while an episode records.</summary>
        public bool Recording { get; set; }
    }

    /// <summary>
    /// Whether each recording stream is delivering: a counter that rose in the last
    /// <see cref="SilentAfterSeconds"/> is live. Any thread is not supported: the card's only.
    /// </summary>
    public sealed class StreamLiveness
    {
        public const double SilentAfterSeconds = 2.5;
        private readonly Dictionary<string, (long count, DateTime changed, DateTime first)> seen = new();

        /// <summary>True when <paramref name="count"/> rose within the window; a first reading of 0 is not live.</summary>
        public bool Live(string role, long count, DateTime now)
        {
            if (!seen.TryGetValue(role, out var last))
            {
                seen[role] = (count, count > 0 ? now : DateTime.MinValue, now);
            }
            else if (count != last.count)
            {
                seen[role] = (count, count > last.count ? now : last.changed, last.first);
            }
            return (now - seen[role].changed).TotalSeconds <= SilentAfterSeconds;
        }

        /// <summary>True while a stream that never delivered is within its first window (arming).</summary>
        public bool Starting(string role, DateTime now) =>
            seen.TryGetValue(role, out var last) && last.changed == DateTime.MinValue
            && (now - last.first).TotalSeconds <= SilentAfterSeconds;

        public void Reset() => seen.Clear();
    }

    /// <summary>
    /// The Pod card's status screen from RigCoordinator's /api/status rig object: libsx_rig's
    /// "roles", "attempts" and "factory" (lifted from the native status), the floor, the USB
    /// snapshot, the last check and the last start's refusal. Pure: no UnityEngine, so it is
    /// tested on the host (tools/sx-rig-stub/test_binding.sh). Every reader tolerates a missing
    /// key, a null and an unexpected type.
    /// </summary>
    public static class PodScreenBoard
    {
        /// <summary>The pipeline's steps, as the episode rows draw them.</summary>
        public static readonly string[] Steps = { "Recorded", "Sealed", "Reported", "Reviewed", "Finalized", "In Catalog" };

        public static PodScreen Build(string? rigStatusJson, StartRefusal? lastStart, bool lastStartSucceeded,
            StreamLiveness liveness, DateTime now)
        {
            JObject? status = null;
            try
            {
                status = string.IsNullOrEmpty(rigStatusJson) ? null : JToken.Parse(rigStatusJson!) as JObject;
            }
            catch (JsonException)
            {
            }
            var screen = new PodScreen { Recording = Recording(status) };
            var refusal = lastStart;
            if (refusal == null && !lastStartSucceeded && status?["lastRefusal"] is JObject rigRefusal)
            {
                refusal = new StartRefusal(Str(rigRefusal["code"]), Str(rigRefusal["message"]), null);
            }
            var failures = FailureTexts(status, refusal);
            Devices(screen, status, failures, liveness, now);
            Collection(screen, status);
            Episodes(screen, status);
            screen.Blocker = Blocker(status, refusal, screen);
            return screen;
        }

        // ---- The blocker ----

        /// <summary>The first thing in the way, in the order a person clears them, else ready.</summary>
        public static PodBlocker Blocker(JObject? status, StartRefusal? refusal, PodScreen screen)
        {
            var recording = Recording(status);
            if (Bool(status, "expected") != true && Bool(status, "enrolled") != true)
            {
                return new PodBlocker("No rig and not enrolled", "Scan the Console's QR code below to set up this headset", PodTone.Neutral);
            }
            if (Str(status?["library"]) == "missing")
            {
                return new PodBlocker("This build has no rig library", "Install a build that carries libsx_rig", PodTone.Error);
            }
            if (Bool(status, "open") != true && status?["openError"] is JObject openError)
            {
                return new PodBlocker("The rig did not open: " + Clip(Str(openError["detail"]), 70), "Fix what it names, then restart the app", PodTone.Error);
            }
            var localOnly = Bool(status, "localOnly") ?? true;
            var enrolled = Bool(status, "enrolled") == true;
            if (!localOnly && !enrolled)
            {
                return new PodBlocker("Not enrolled", "Scan the Console's QR code below, or set up manually", PodTone.Warning);
            }
            var factory = Lifted(status, "factory") as JObject;
            if (!localOnly && factory?["link"] is JObject link && Bool(link, "ok") != true && Str(link["error"]).Length > 0)
            {
                return new PodBlocker("The Factory does not hear this headset", "Check Wi-Fi and the tailnet: " + Clip(Str(link["error"]), 60), PodTone.Error);
            }
            if (!localOnly && RigStatusJson.CollectorSignedIn(status) == false)
            {
                var (code, _) = PodStatusBoard.PendingSignIn(status, false);
                return code.Length > 0
                    ? new PodBlocker("Collector sign-in pending", "Approve code " + code + " on your phone", PodTone.Warning)
                    : new PodBlocker("Collector signed out", "Tap Sign in below and approve the code on your phone", PodTone.Warning);
            }
            if (status?["usb"] is JObject usb && usb["awaitingPermission"] is JArray waiting && waiting.Count > 0)
            {
                return new PodBlocker("A rig device waits for USB access", "Choose OpenQuestCapture when Android asks (replug it if no prompt shows)", PodTone.Warning);
            }
            foreach (var device in screen.Devices)
            {
                if (device.Tone == PodTone.Error)
                {
                    return new PodBlocker(device.Name + ": " + device.Pill.ToLowerInvariant(),
                        device.Hint.Length > 0 ? device.Hint : "Replug it, then check it with a start", PodTone.Error);
                }
            }
            if (!localOnly && factory != null)
            {
                if (factory["blocking"] is JArray blocking)
                {
                    foreach (var check in blocking.OfType<JObject>())
                    {
                        var id = Str(check["id"]);
                        if (id == "operator") continue; // the collector, above
                        if (id == "recorder_build")
                        {
                            return new PodBlocker("This build's trial is not admitted",
                                "Admit the HIL trial for build " + ShortBuild(Str(status?["apkBuildId"])) + " in the Console", PodTone.Error);
                        }
                        if (id == "task_contract" && factory["task"] is not JObject)
                        {
                            return NoTask();
                        }
                        var remediation = Str(check["remediation"]);
                        return new PodBlocker(Clip(Str(check["summary"]), 80),
                            remediation.Length > 0 ? Clip(remediation, 90) : Clip(Str(check["detail"]), 90), PodTone.Error);
                    }
                }
                if (factory.ContainsKey("task") && factory["task"] is not JObject) return NoTask();
            }
            if (refusal != null)
            {
                var text = PodStatusBoard.Explain(refusal.Code, refusal.Message);
                if (refusal.Message.ToLowerInvariant().Contains("no assigned task")) return NoTask();
                var hint = Hint(refusal.Message);
                return new PodBlocker("Last start refused: " + Clip(text, 70),
                    hint.Length > 0 ? hint : "Fix it, then press the menu button to start again", PodTone.Error);
            }
            if (recording)
            {
                return new PodBlocker("Recording", "Press the menu button to stop", PodTone.Good);
            }
            if (LastFailedAttempt(status) is { Length: > 0 } failed)
            {
                var hint = Hint(failed);
                return new PodBlocker("Last episode failed: " + Clip(failed, 70),
                    hint.Length > 0 ? hint : "Check the devices, then press the menu button to record again", PodTone.Warning);
            }
            if (Bool(status, "expected") == true && status?["lastCheck"] is JObject lastCheck
                && RigStatusJson.Check(lastCheck.ToString(Formatting.None)) is { ready: false } failedCheck)
            {
                var problems = failedCheck.problems;
                var hint = Hint(problems);
                return new PodBlocker("The rig's last check failed: " + Clip(problems, 60),
                    hint.Length > 0 ? hint : "Fix it, then press the menu button to check again", PodTone.Error);
            }
            return localOnly
                ? new PodBlocker("Ready (Local only)", "Press the menu button to record; nothing goes to the Factory", PodTone.Good)
                : new PodBlocker("Ready to record", "Press the menu button to start", PodTone.Good);
        }

        private static PodBlocker NoTask() =>
            new("The Factory shows this pod no task", "Assign a task to this pod in the Console", PodTone.Error);

        /// <summary>sha256:abcdef... -> the first 12 hex digits; "this build" when unknown.</summary>
        public static string ShortBuild(string buildId)
        {
            var hex = buildId.StartsWith("sha256:", StringComparison.Ordinal) ? buildId.Substring(7) : buildId;
            return hex.Length == 0 ? "this build" : hex.Length > 12 ? hex.Substring(0, 12) : hex;
        }

        /// <summary>The action that clears a failure we know, from its words; "" when none is known.</summary>
        public static string Hint(string detail)
        {
            var lower = detail.ToLowerInvariant();
            if (lower.Contains("sends nothing") || lower.Contains("0 bytes received") || lower.Contains("another app"))
                return "Another app holds it: replug it and choose OpenQuestCapture when Android asks";
            if (lower.Contains("os error 12") || lower.Contains("out of memory"))
                return "Not enough USB bandwidth: put both cameras on one hub";
            if (lower.Contains("was detached") || lower.Contains("device_disconnected") || lower.Contains("disconnected"))
                return "It dropped off USB: reseat its cable at the hub";
            if (lower.Contains("no device fills this role") || lower.Contains("no device on"))
                return "Plug it into the hub";
            if (lower.Contains("permission"))
                return "Choose OpenQuestCapture when Android asks for USB access";
            if (lower.Contains("storage") || lower.Contains("free space"))
                return "Free space on the headset: let sealed episodes upload, or delete old recordings";
            return "";
        }

        // ---- Devices ----

        private static readonly (string role, string name, string side, string part)[] Roles =
        {
            ("left_hand_camera", "Left camera", "left", "camera"),
            ("right_hand_camera", "Right camera", "right", "camera"),
            ("left_hand_encoder", "Left encoder", "left", "encoder"),
            ("right_hand_encoder", "Right encoder", "right", "encoder"),
        };

        /// <summary>Every failure sentence that may name a device: the refusal, the episode's failure, the newest attempt's.</summary>
        private static List<string> FailureTexts(JObject? status, StartRefusal? refusal)
        {
            var texts = new List<string>();
            if (refusal != null) texts.Add(refusal.Message);
            if (Native(status)?["episode"] is JObject episode && episode["failure"] is JObject failure) texts.Add(Str(failure["detail"]));
            return texts;
        }

        /// <summary>
        /// The clause naming this side's part ("right camera: ...", "left_encoder: ...",
        /// "left_hand_camera: ...") in any of <paramref name="texts"/>, from that name to the next
        /// "; ", or "".
        /// </summary>
        public static string FailureFor(IEnumerable<string> texts, string side, string part)
        {
            foreach (var text in texts)
            {
                var lower = text.ToLowerInvariant();
                foreach (var name in new[] { side + " " + part + ":", side + "_" + part + ":", side + "_hand_" + part + ":" })
                {
                    var at = lower.IndexOf(name, StringComparison.Ordinal);
                    if (at < 0) continue;
                    var end = text.IndexOf("; ", at, StringComparison.Ordinal);
                    return (end < 0 ? text.Substring(at) : text.Substring(at, end - at)).Trim();
                }
            }
            return "";
        }

        private static void Devices(PodScreen screen, JObject? status, List<string> failures, StreamLiveness liveness, DateTime now)
        {
            if (Bool(status, "expected") != true)
            {
                screen.DevicesNote = "No rig configured or plugged in";
                return;
            }
            var recording = Recording(status);
            if (!recording) liveness.Reset();
            var roles = Lifted(status, "roles") as JArray;
            var placed = roles?.OfType<JObject>().Any(r => Str(r["registered"]).Length > 0) == true;
            if (!placed)
            {
                // No rig resolved yet: list what is attached by kind, two of each expected.
                screen.DevicesNote = "Roles are known after the first check (a start runs it)";
                var devices = Lifted(status, "devices") as JArray;
                foreach (var (kind, name) in new[] { ("camera", "Camera"), ("encoder", "Encoder") })
                {
                    var ofKind = devices?.OfType<JObject>().Where(d => Str(d["kind"]) == kind).ToList() ?? new List<JObject>();
                    for (var i = 0; i < 2; i++)
                    {
                        var label = name + " " + (i + 1).ToString(CultureInfo.InvariantCulture);
                        if (i < ofKind.Count)
                        {
                            var port = Str(ofKind[i]["port"]);
                            screen.Devices.Add(new PodDeviceRow(label, port.Length > 0 ? "port " + port : "attached", "Attached", PodTone.Neutral, "",
                                kind, "", port, PodDeviceState.Starting));
                        }
                        else
                        {
                            screen.Devices.Add(new PodDeviceRow(label, "not on USB", "Missing", PodTone.Error, "Plug it into the hub",
                                kind, "", "", PodDeviceState.Missing));
                        }
                    }
                }
            }
            else
            {
                var lastFailed = LastFailedAttempt(status);
                foreach (var (role, name, side, part) in Roles)
                {
                    var entry = roles!.OfType<JObject>().FirstOrDefault(r => Str(r["role"]) == role);
                    var row = DeviceRow(entry, role, name, side, part, recording, FailureFor(failures, side, part), liveness, now);
                    // The last episode failed on it: a warning with the hint, until the next attempt.
                    var earlier = lastFailed.Length > 0 ? FailureFor(new[] { lastFailed }, side, part) : "";
                    if (row.Tone is PodTone.Good or PodTone.Neutral && !recording && earlier.Length > 0)
                    {
                        var hint = Hint(earlier);
                        row = new PodDeviceRow(name, row.Value, "Failed last episode", PodTone.Warning, hint.Length > 0 ? hint : "Replug it before the next start",
                            role, side, row.Port, PodDeviceState.Failed);
                    }
                    screen.Devices.Add(row);
                }
            }
            if (status?["usb"] is JObject usb && usb["awaitingPermission"] is JArray waiting && waiting.Count > 0)
            {
                screen.DevicesNote = waiting.Count + " device(s) wait for USB access: choose OpenQuestCapture when Android asks";
            }
        }

        public static PodDeviceRow DeviceRow(JObject? entry, string role, string name, string side, string part, bool recording, string failure,
            StreamLiveness liveness, DateTime now)
        {
            var attached = Bool(entry, "attached") == true;
            var port = Str(entry?["port"]);
            var where = port.Length > 0 ? "port " + port : attached ? "attached" : "";
            var check = Str(entry?["check"]);
            var checkDetail = Str(entry?["check_detail"]);
            PodDeviceRow Row(string value, string pill, PodTone tone, string hint, PodDeviceState state) =>
                new(name, value, pill, tone, hint, role, side, port, state);
            if (!attached)
            {
                return Row("not on USB", "Missing", PodTone.Error, failure.Length > 0 && Hint(failure).Length > 0 ? Hint(failure) : "Plug it into the hub",
                    PodDeviceState.Missing);
            }
            if (recording && entry?["stream"] is JObject stream)
            {
                var counter = part == "camera" ? Long(stream["source_frames"]) : Long(stream["samples"]);
                var rate = part == "camera" && stream["source_fps"] is JValue fps && fps.Type is JTokenType.Float or JTokenType.Integer
                    ? ((double)fps).ToString("F0", CultureInfo.InvariantCulture) + " fps" : counter.ToString(CultureInfo.InvariantCulture) + (part == "camera" ? " frames" : " samples");
                var key = side + "_" + part;
                if (liveness.Live(key, counter, now)) return Row(where + " · " + rate, "Live", PodTone.Good, "", PodDeviceState.Live);
                return liveness.Starting(key, now)
                    ? Row(where + " · arming", "Starting", PodTone.Warning, "", PodDeviceState.Starting)
                    : Row(where + " · nothing for " + StreamLiveness.SilentAfterSeconds.ToString("F0", CultureInfo.InvariantCulture) + " s",
                        "Silent", PodTone.Error, failure.Length > 0 && Hint(failure).Length > 0 ? Hint(failure) : "Replug it; if it stays silent, choose OpenQuestCapture when Android asks",
                        PodDeviceState.Silent);
            }
            if (failure.Length > 0)
            {
                var hint = Hint(failure);
                return Row(where + " · " + Clip(failure.Substring(Math.Min(failure.Length, failure.IndexOf(':') + 1)).Trim(), 50),
                    "Failed", PodTone.Error, hint.Length > 0 ? hint : "Replug it, then start again", PodDeviceState.Failed);
            }
            return check switch
            {
                "ready" => Row(where, "Ready", PodTone.Good, "", PodDeviceState.Ready),
                "error" or "unavailable" => Row(where + " · " + Clip(checkDetail, 50), "Check failed", PodTone.Warning, Hint(checkDetail), PodDeviceState.Failed),
                // Attached, not checked yet: the next start checks it.
                _ => Row(where, "Attached", PodTone.Neutral, "", PodDeviceState.Starting),
            };
        }

        /// <summary>The newest attempt's failure sentence when the newest attempt failed while recording; else "".</summary>
        private static string LastFailedAttempt(JObject? status)
        {
            if (Attempts(status).FirstOrDefault() is not JObject newest) return "";
            var outcome = Str(newest["outcome"]);
            var failed = Str(newest["stage"]) == "failed" || (outcome.Length > 0 && outcome != "complete" && Str(newest["stage"]) != "recording");
            if (!failed) return "";
            var detail = Str(newest["detail"]);
            return detail.Length > 0 ? detail : outcome.Length > 0 ? "the recording " + outcome : "the recording failed";
        }

        // ---- Collection ----

        private static void Collection(PodScreen screen, JObject? status)
        {
            var localOnly = Bool(status, "localOnly") ?? true;
            if (localOnly || Bool(status, "enrolled") != true)
            {
                screen.Task = localOnly ? "none (Local only records without one)" : "none (not enrolled)";
                screen.Link = localOnly ? "not used (Local only)" : "not enrolled";
                return;
            }
            var factory = Lifted(status, "factory") as JObject;
            if (factory?["link"] is JObject link)
            {
                var ok = Bool(link, "ok") == true;
                var ago = link["heard_seconds_ago"] is JValue { Type: JTokenType.Integer } seconds ? (long)seconds : -1;
                screen.LinkOk = ok;
                screen.HeardSecondsAgo = ago >= 0 ? ago : (long?)null;
                if (ok)
                {
                    screen.Link = "OK · heard " + (ago <= 1 ? "just now" : ago.ToString(CultureInfo.InvariantCulture) + " s ago");
                    screen.LinkTone = ago > 30 ? PodTone.Warning : PodTone.Good;
                }
                else if (Str(link["error"]).Length > 0)
                {
                    screen.Link = "not heard: " + Clip(Str(link["error"]), 60);
                    screen.LinkTone = PodTone.Error;
                }
                else
                {
                    screen.Link = "connecting...";
                    screen.LinkTone = PodTone.Warning;
                }
            }
            else
            {
                screen.Link = "waiting for the floor";
                screen.LinkTone = PodTone.Warning;
            }
            if (factory == null || !factory.ContainsKey("task"))
            {
                screen.Task = "unknown until the Factory answers";
                return;
            }
            var task = factory["task"] as JObject;
            screen.Task = task == null ? "none assigned" : Str(task["name"]).Length > 0 ? Str(task["name"]) : "assigned";
            var progress = factory["progress"] as JObject;
            var target = progress != null ? Long(progress["target"]) : Long(task?["target"]);
            var credited = progress != null ? Long(progress["credited"]) : Long(task?["done"]);
            screen.Credited = credited;
            screen.Requested = target;
            screen.AwaitingReview = Long(progress?["awaiting_review"]);
            if (task != null)
            {
                var parts = new List<string>
                {
                    target > 0 ? credited + " of " + target + " credited" : credited + " credited",
                };
                foreach (var (key, word) in new[] { ("recording", "recording"), ("awaiting_review", "awaiting review"), ("processing", "processing"),
                    ("sealing", "sealing"), ("registering", "registering") })
                {
                    var n = Long(progress?[key]);
                    if (n > 0) parts.Add(n + " " + word);
                }
                screen.Progress = string.Join(" · ", parts);
            }
            screen.Next = Next(factory, task, progress, status, target, credited);
        }

        /// <summary>What the Factory expects next, in a few words.</summary>
        private static string Next(JObject factory, JObject? task, JObject? progress, JObject? status, long target, long credited)
        {
            if (task == null) return "a task assigned to this pod in the Console";
            if (factory["blocking"] is JArray blocking && blocking.OfType<JObject>().FirstOrDefault() is JObject check)
            {
                if (Str(check["id"]) == "recorder_build") return "the HIL trial admitted for build " + ShortBuild(Str(status?["apkBuildId"]));
                if (Str(check["id"]) == "operator") return "a collector signed in";
                var remediation = Str(check["remediation"]);
                return Clip(remediation.Length > 0 ? remediation : Str(check["summary"]), 70);
            }
            if (Str(progress?["registration_error"]).Length > 0) return "Catalog registration (failing: " + Clip(Str(progress?["registration_error"]), 40) + ")";
            if (Long(progress?["awaiting_review"]) > 0) return "a review verdict on the recorded episode";
            if (Long(progress?["sealing"]) > 0) return "the seal and its upload";
            if (Long(progress?["registering"]) > 0) return "the Catalog to register the collection";
            if (target > 0 && credited >= target) return "nothing: the task is complete";
            if (target > 0) return "episode " + (credited + 1) + " of " + target;
            return "the next episode";
        }

        // ---- Episodes ----

        private static IEnumerable<JObject> Attempts(JObject? status) =>
            (Lifted(status, "attempts") as JArray)?.OfType<JObject>() ?? Enumerable.Empty<JObject>();

        private static void Episodes(PodScreen screen, JObject? status)
        {
            foreach (var attempt in Attempts(status))
            {
                var row = EpisodeRow(attempt);
                var hint = row.Tone == PodTone.Error ? Hint(row.Detail) : "";
                screen.Episodes.Add(hint.Length > 0
                    ? new PodEpisodeRow(row.Id, row.Reached, row.FailedAt, row.Stage, row.Tone, Clip(row.Detail, 60) + " · " + hint, hint)
                    : row);
            }
        }

        /// <summary>One attempt's row: the steps it completed, where it failed, its word and its detail.</summary>
        public static PodEpisodeRow EpisodeRow(JObject attempt)
        {
            var id = Str(attempt["id"]);
            var stage = Str(attempt["stage"]);
            var detail = Str(attempt["detail"]);
            var outcome = Str(attempt["outcome"]);
            var failedRecording = outcome.Length > 0 && outcome != "complete";
            var refusedSeal = Str(attempt["seal"]) == "refused";
            var integrity = Str(attempt["integrity"]);
            switch (stage)
            {
                case "recording": return new PodEpisodeRow(id, 0, -1, "Recording", PodTone.Good, "");
                case "unsettled": return new PodEpisodeRow(id, 0, -1, "Stopping", PodTone.Warning, "");
                case "failed": return new PodEpisodeRow(id, 0, 0, outcome == "interrupted" ? "Interrupted" : "Failed", PodTone.Error, detail);
                case "sealing": return new PodEpisodeRow(id, 1, -1, "Sealing", PodTone.Warning, detail);
                case "refused": return new PodEpisodeRow(id, 1, 1, "Seal refused", PodTone.Error, detail);
                case "sealed": return new PodEpisodeRow(id, 2, -1, "Sealed", PodTone.Neutral, "");
            }
            // Reported onward: a failed recording or refused seal reported to the Factory stays failed.
            var failedAt = failedRecording ? 0 : refusedSeal ? 1 : -1;
            var reached = stage switch
            {
                "reported" => 3,
                "reviewed" => 4,
                "finalized" => 5,
                "registered" => 6,
                _ => 0,
            };
            if (failedAt >= 0)
            {
                return new PodEpisodeRow(id, failedAt, failedAt, (failedRecording ? "Failed" : "Seal refused") + ", reported", PodTone.Error, detail);
            }
            if (stage == "reviewed" && integrity == "failed")
            {
                return new PodEpisodeRow(id, 3, 3, "Review failed", PodTone.Error, detail);
            }
            return stage switch
            {
                "reported" => new PodEpisodeRow(id, reached, -1, "Awaiting review", PodTone.Neutral, ""),
                "reviewed" => new PodEpisodeRow(id, reached, -1, "Reviewed", PodTone.Good, ""),
                "finalized" => new PodEpisodeRow(id, reached, -1, "Uploading", PodTone.Good, ""),
                "registered" => new PodEpisodeRow(id, reached, -1, "In Catalog", PodTone.Good, ""),
                _ => new PodEpisodeRow(id, 0, -1, stage.Length > 0 ? stage : "unknown", PodTone.Neutral, detail),
            };
        }

        // ---- Readers ----

        private static JObject? Native(JObject? status) => status?["native"] as JObject;

        /// <summary>A key RigCoordinator lifts from the native status (kept while the rig is busy), else the native status's own.</summary>
        private static JToken? Lifted(JObject? status, string key) =>
            status?[key] is JToken lifted && lifted.Type != JTokenType.Null ? lifted : Native(status)?[key];

        public static bool Recording(JObject? status) =>
            Bool(status, "episodeActive") == true || Str(Native(status)?["state"]) == "recording";

        private static bool? Bool(JObject? obj, string key) =>
            obj?[key] is JValue { Type: JTokenType.Boolean } value ? (bool?)(bool)value : null;

        private static string Str(JToken? token) =>
            token is JValue { Type: JTokenType.String } value ? (string?)value ?? "" : "";

        private static long Long(JToken? token) =>
            token is JValue { Type: JTokenType.Integer } value ? (long)value : 0;

        private static string Clip(string text, int max) =>
            text.Length <= max ? text : text.Substring(0, max - 3) + "...";
    }
}
