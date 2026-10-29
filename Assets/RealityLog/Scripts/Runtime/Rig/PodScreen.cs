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

    /// <summary>
    /// The one thing in the way, in the notice table's words: what prevents it (the headline),
    /// what is still fine, who acts, the next action and its button. Ready and Recording carry no
    /// notice.
    /// </summary>
    public readonly struct PodBlocker
    {
        public string Headline { get; }
        public string Action { get; }
        public PodTone Tone { get; }
        /// <summary>What still works; "" when nothing needs saying (Ready, Recording).</summary>
        public string StillFine { get; }
        /// <summary>"Collector", "Floor manager" or "Technician"; "" without a notice.</summary>
        public string Who { get; }
        /// <summary>"check", "sign_in" or "": the control that clears it.</summary>
        public string Button { get; }
        /// <summary>The notice it words; null for Ready and Recording.</summary>
        public OperatorNotice? Notice { get; }

        public PodBlocker(string headline, string action, PodTone tone)
        {
            Headline = headline;
            Action = action;
            Tone = tone;
            StillFine = "";
            Who = "";
            Button = "";
            Notice = null;
        }

        private PodBlocker(OperatorNotice notice, OperatorMessage words, PodTone tone)
        {
            Headline = words.Headline;
            Action = words.Action;
            Tone = tone;
            StillFine = words.StillFine;
            Who = words.WhoLabel;
            Button = words.Button;
            Notice = new OperatorNotice(words.Code, words.Who, words.Args);
        }

        /// <summary>A notice in the table's words.</summary>
        public static PodBlocker Of(OperatorNotice notice, PodTone tone) => new(notice, OperatorNotices.Render(notice), tone);

        public static PodBlocker Of(string code, PodTone tone, params (string name, string value)[] args) =>
            Of(OperatorNotice.Of(code, args), tone);
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
        /// <summary>The failure's own words, for the logs and to place its notice; never shown.</summary>
        public string Failure { get; }

        public PodDeviceRow(string name, string value, string pill, PodTone tone, string hint,
            string role, string side, string port, PodDeviceState state, string failure = "")
        {
            Failure = failure;
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
        /// <summary>The stage's key in the notice table (sealing, seal_refused, awaiting_review, delivered, ...).</summary>
        public string StageKey { get; }
        /// <summary>Why it failed or was refused; null otherwise.</summary>
        public OperatorNotice? Notice { get; }
        /// <summary>How long it has waited for its seal, while sealing; null otherwise.</summary>
        public long? SealingSeconds { get; }

        public PodEpisodeRow(string id, int reached, int failedAt, string stage, PodTone tone, string detail, string fix = "",
            string stageKey = "", OperatorNotice? notice = null, long? sealingSeconds = null)
        {
            Id = id;
            Reached = reached;
            FailedAt = failedAt;
            Stage = stage;
            Tone = tone;
            Detail = detail;
            Fix = fix;
            StageKey = stageKey;
            Notice = notice;
            SealingSeconds = sealingSeconds;
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
        /// <summary>
        /// A start now would certainly be refused (<see cref="PodScreenBoard.StartBlocker"/>): the
        /// card and the phone offer no Start, and the menu button buzzes with the reason.
        /// </summary>
        public bool StartBlocked { get; set; }
        /// <summary>
        /// What a start may meet but is not refused for, checked before Start: each controller
        /// tracked, connected and charged, the display at 90 Hz, room for the episode, and episodes
        /// held by unfinished attempts. Empty while recording.
        /// </summary>
        public List<PodBlocker> Warnings { get; } = new();
        /// <summary>Local only: nothing recorded is delivered.</summary>
        public bool LocalOnly { get; set; }
        /// <summary>The blocker is the newest episode's failure, shown until the next start.</summary>
        public bool LastFailed { get; set; }
    }

    /// <summary>The rig's storage figures (<see cref="PodScreenBoard.Storage"/>), in bytes.</summary>
    public readonly struct StorageFigures
    {
        public long Free { get; }
        /// <summary>required_bytes: what a start needs, all of it (require_storage).</summary>
        public long Needed { get; }
        /// <summary>The sealed copies the episodes still waiting for their seal will write.</summary>
        public long Backlog { get; }
        public long BacklogEpisodes { get; }
        public long Uploads { get; }
        /// <summary>What the next episode itself needs: required_bytes less the seals' backlog.</summary>
        public long NextEpisode => Math.Max(0, Needed - Backlog);

        public StorageFigures(long free, long needed, long backlog, long backlogEpisodes, long uploads)
        {
            Free = free;
            Needed = needed;
            Backlog = backlog;
            BacklogEpisodes = backlogEpisodes;
            Uploads = uploads;
        }
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
        /// <summary>The pipeline's steps, as the episode rows draw them (the notice table's stage words).</summary>
        public static readonly string[] Steps = { "Recorded", "Sealed", "Reported", "Reviewed", "Delivered", "In Catalog" };

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
            return Build(status, lastStart, lastStartSucceeded, liveness, now);
        }

        /// <summary><see cref="Build(string?, StartRefusal?, bool, StreamLiveness, DateTime)"/> on a status already parsed; it is only read.</summary>
        public static PodScreen Build(JObject? status, StartRefusal? lastStart, bool lastStartSucceeded,
            StreamLiveness liveness, DateTime now)
        {
            var screen = new PodScreen { Recording = Recording(status), LocalOnly = Bool(status, "localOnly") ?? true };
            var refusal = lastStart;
            if (refusal == null && !lastStartSucceeded && status?["lastRefusal"] is JObject rigRefusal)
            {
                refusal = new StartRefusal(Str(rigRefusal["code"]), Str(rigRefusal["message"]), null,
                    OperatorNotices.ForRefusal(Str(rigRefusal["code"]), rigRefusal["detail"]?.ToString(Formatting.None) ?? ""));
            }
            var failures = FailureTexts(status, refusal);
            Devices(screen, status, failures, liveness, now);
            Collection(screen, status);
            Episodes(screen, status);
            screen.Blocker = Blocker(status, refusal, screen);
            screen.StartBlocked = StartBlocker(status) != null;
            if (!screen.Recording) Warnings(screen, status);
            return screen;
        }

        // ---- Start ----

        /// <summary>
        /// What makes a start now certainly refused, as a blocker in the notice table's words; null
        /// when a start may go ahead (or while recording, when the button stops). The refusals named
        /// here are the ones the start itself would meet: a rig that cannot run, no collector, a
        /// device without USB access, too little room for the episode (the rig's own estimate), and
        /// the Factory's own refusals as libsx_rig reads them from its capture workspace
        /// (factory.start_refusal: no task, a readiness check the start's device check cannot fix,
        /// no episodes left, with how many unfinished attempts hold). Anything less certain (a
        /// device the start's check may find again, an unheard Factory, a controller asleep) leaves
        /// Start offered and shows as a warning.
        /// </summary>
        public static PodBlocker? StartBlocker(string? rigStatusJson)
        {
            try
            {
                return StartBlocker(string.IsNullOrEmpty(rigStatusJson) ? null : JToken.Parse(rigStatusJson!) as JObject);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        public static PodBlocker? StartBlocker(JObject? status)
        {
            if (Bool(status, "expected") != true || Recording(status)) return null;
            if (Str(status?["library"]) == "missing") return PodBlocker.Of("library_missing", PodTone.Error);
            if (Bool(status, "open") != true && status?["openError"] is JObject) return PodBlocker.Of("rig_open_failed", PodTone.Error);
            var localOnly = Bool(status, "localOnly") ?? true;
            if (!localOnly && RigStatusJson.CollectorSignedIn(status) == false) return SignIn(status);
            if (status?["usb"] is JObject usb && usb["awaitingPermission"] is JArray waiting && waiting.Count > 0)
            {
                return PodBlocker.Of("usb_permission", PodTone.Warning);
            }
            // Exactly what the rig's require_storage refuses on: free below required_bytes.
            if (Storage(status) is { } space && space.Free < space.Needed) return PodBlocker.Of(StorageNotice(space, low: true), PodTone.Error);
            if (localOnly || Lifted(status, "factory") is not JObject factory || factory["start_refusal"] is not JObject refused) return null;
            if (OperatorNotice.FromJson(refused["notice"]) is { } notice) return PodBlocker.Of(notice, PodTone.Error);
            // A runtime older than its notices: the reason alone.
            return Str(refused["reason"]) switch
            {
                "no_task" => PodBlocker.Of("no_task", PodTone.Error),
                "no_episodes" => PodBlocker.Of("no_episodes", PodTone.Error),
                "readiness" => PodBlocker.Of("readiness_other", PodTone.Error),
                _ => null,
            };
        }

        private static PodBlocker SignIn(JObject? status)
        {
            var (code, _) = PodStatusBoard.PendingSignIn(status, false);
            return code.Length > 0
                ? PodBlocker.Of("sign_in_pending", PodTone.Warning, ("code", code))
                : PodBlocker.Of("signed_out", PodTone.Warning);
        }

        // ---- Before a start ----

        /// <summary>A controller below this charge warns before a start.</summary>
        public const int ControllerLowPercent = 15;
        /// <summary>The display rate the head cameras pair at (CaptureDisplayRate).</summary>
        public const double CaptureHz = 90;

        /// <summary>
        /// The warn-level checks before Start, each a notice: the controllers (the app's
        /// "headset.controllers": tracked, connected, charge), the display rate
        /// ("headset.displayHz"), storage within twice the rig's own estimate of one episode
        /// (native "storage"), and the episodes unfinished attempts hold (factory.warnings).
        /// </summary>
        private static void Warnings(PodScreen screen, JObject? status)
        {
            if (Bool(status, "expected") != true && Bool(status, "enrolled") != true) return;
            if (status?["headset"] is JObject headset)
            {
                foreach (var controller in (headset["controllers"] as JArray)?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
                {
                    var side = Str(controller["side"]);
                    if (side != "left" && side != "right") continue;
                    if (Bool(controller, "connected") == false)
                    {
                        screen.Warnings.Add(PodBlocker.Of("controller_disconnected", PodTone.Warning, ("side", side)));
                    }
                    else if (Bool(controller, "tracked") == false)
                    {
                        screen.Warnings.Add(PodBlocker.Of("controller_not_tracked", PodTone.Warning, ("side", side)));
                    }
                    else if (controller["percent"] is JValue { Type: JTokenType.Integer } pct && (long)pct <= ControllerLowPercent)
                    {
                        screen.Warnings.Add(PodBlocker.Of("controller_battery_low", PodTone.Warning, ("side", side),
                            ("percent", ((long)pct).ToString(CultureInfo.InvariantCulture))));
                    }
                }
                if (headset["displayHz"] is JValue { Type: JTokenType.Float or JTokenType.Integer } hz && (double)hz > 0
                    && Math.Abs((double)hz - CaptureHz) > 1)
                {
                    screen.Warnings.Add(PodBlocker.Of("display_rate", PodTone.Warning, ("hz", ((double)hz).ToString("F0", CultureInfo.InvariantCulture))));
                }
            }
            // Room for this episode, but not for the one after it.
            if (Storage(status) is { } space && space.Free >= space.Needed && space.Free < space.Needed + space.NextEpisode)
            {
                screen.Warnings.Add(PodBlocker.Of(StorageNotice(space, low: false), PodTone.Warning));
            }
            var localOnly = Bool(status, "localOnly") ?? true;
            if (!localOnly && Lifted(status, "factory") is JObject factory && factory["warnings"] is JArray held)
            {
                foreach (var warning in held)
                {
                    if (OperatorNotice.FromJson(warning) is { } notice) screen.Warnings.Add(PodBlocker.Of(notice, PodTone.Warning));
                }
            }
        }

        /// <summary>
        /// The rig's storage numbers (native "storage", the figures its require_storage refuses
        /// on): free_bytes; required_bytes, all a start needs; backlog_bytes, the sealed copies the
        /// episodes still waiting for their seal will write (backlog_episodes of them), part of
        /// required_bytes; and how many attempts wait to upload (they free space once delivered,
        /// but are not counted on). Null when the rig has not said.
        /// </summary>
        public static StorageFigures? Storage(JObject? status)
        {
            if (Native(status)?["storage"] is not JObject storage) return null;
            if (storage["free_bytes"] is not JValue { Type: JTokenType.Integer } free || storage["required_bytes"] is not JValue { Type: JTokenType.Integer } needed) return null;
            var held = storage["held"] as JObject;
            var uploads = Long(held?["unreported"]?["attempts"]) + Long(held?["awaiting_factory"]?["attempts"]);
            return new StorageFigures((long)free, (long)needed, Long(storage["backlog_bytes"]), Long(storage["backlog_episodes"]), uploads);
        }

        /// <summary>
        /// The storage notice, as libsx_rig's notice.rs::of_storage words it: the needed figure is
        /// exactly required_bytes; with episodes still to seal it is split into the next episode
        /// and their sealed copies, so the numbers add up.
        /// </summary>
        public static OperatorNotice StorageNotice(StorageFigures space, bool low)
        {
            var split = space.Backlog > 0 && space.BacklogEpisodes > 0;
            var code = (low ? "storage_low" : "storage_tight") + (split ? "_backlog" : "");
            var args = new List<(string, string)>
            {
                ("free_gb", Gigabytes(space.Free)), ("needed_gb", Gigabytes(space.Needed)),
                ("uploads", space.Uploads.ToString(CultureInfo.InvariantCulture)),
            };
            if (split)
            {
                args.Add(("next_gb", Gigabytes(space.NextEpisode)));
                args.Add(("backlog_gb", Gigabytes(space.Backlog)));
                args.Add(("backlog", space.BacklogEpisodes.ToString(CultureInfo.InvariantCulture)));
            }
            return OperatorNotice.Of(code, args.ToArray());
        }

        /// <summary>
        /// The header chip, following the banner: "Last failed" (red) while the newest episode's
        /// failure shows, until the next start; "Warnings" (amber) when the card is ready but
        /// pre-Start warnings stand; "Not ready" when anything else blocks; else the pod's own
        /// chip (<paramref name="readiness"/>, <paramref name="text"/>).
        /// </summary>
        public static (PodTone tone, string text) Chip(PodScreen screen, PodReadiness readiness, string text)
        {
            if (screen.LastFailed) return (PodTone.Error, OperatorNotices.Phrase("pill_last_failed"));
            var tone = readiness switch
            {
                PodReadiness.Ready => PodTone.Good,
                PodReadiness.NotReady => PodTone.Warning,
                PodReadiness.Refused => PodTone.Error,
                _ => PodTone.Neutral,
            };
            if (screen.Blocker.Tone != PodTone.Good)
            {
                return tone == PodTone.Good || readiness == PodReadiness.LocalOnly
                    ? (screen.Blocker.Tone == PodTone.Error ? PodTone.Error : PodTone.Warning, "Not ready")
                    : (tone, text);
            }
            if (!screen.Recording && screen.Warnings.Count > 0) return (PodTone.Warning, OperatorNotices.Phrase("pill_warnings"));
            return (tone, text);
        }

        /// <summary>Bytes as gigabytes with one decimal ("3.2"), as the rig words them.</summary>
        public static string Gigabytes(long bytes) => (bytes / 1e9).ToString("F1", CultureInfo.InvariantCulture);

        // ---- The blocker ----

        /// <summary>
        /// The first thing in the way, in the order a person clears them; the newest attempt's
        /// failure until the next start; else ready. Every headline is the notice table's, never a
        /// raw string (a cause no code names reads "Recording failed", its code in the action).
        /// </summary>
        public static PodBlocker Blocker(JObject? status, StartRefusal? refusal, PodScreen screen)
        {
            var recording = Recording(status);
            if (Bool(status, "expected") != true && Bool(status, "enrolled") != true) return PodBlocker.Of("not_enrolled", PodTone.Neutral);
            if (Str(status?["library"]) == "missing") return PodBlocker.Of("library_missing", PodTone.Error);
            if (Bool(status, "open") != true && status?["openError"] is JObject) return PodBlocker.Of("rig_open_failed", PodTone.Error);
            var localOnly = Bool(status, "localOnly") ?? true;
            var enrolled = Bool(status, "enrolled") == true;
            if (!localOnly && !enrolled) return PodBlocker.Of("not_enrolled", PodTone.Warning);
            var factory = Lifted(status, "factory") as JObject;
            if (!localOnly && factory?["link"] is JObject link && Bool(link, "ok") != true && Str(link["error"]).Length > 0)
            {
                // Recording goes on on the headset; idle, a start may not open a collection.
                return recording ? PodBlocker.Of("factory_unheard_local", PodTone.Warning) : PodBlocker.Of("factory_unheard", PodTone.Error);
            }
            if (!localOnly && RigStatusJson.CollectorSignedIn(status) == false) return SignIn(status);
            if (status?["usb"] is JObject usb && usb["awaitingPermission"] is JArray waiting && waiting.Count > 0)
            {
                return PodBlocker.Of("usb_permission", PodTone.Warning);
            }
            foreach (var device in screen.Devices)
            {
                if (device.Tone == PodTone.Error) return PodBlocker.Of(DeviceNotice(device), PodTone.Error);
            }
            if (StartBlocker(status) is { } certain) return certain;
            if (!localOnly && factory != null)
            {
                if (factory["blocking"] is JArray blocking)
                {
                    foreach (var check in blocking.OfType<JObject>())
                    {
                        var id = Str(check["id"]);
                        if (id == "operator") continue; // the collector, above
                        // A failing hardware check alone never refuses a start (its own check runs first).
                        if (Str(check["category"]) == "hardware") continue;
                        if (id == "recorder_build") return PodBlocker.Of("build_not_accepted", PodTone.Error);
                        if (id == "task_contract" && factory["task"] is not JObject) return PodBlocker.Of("no_task", PodTone.Error);
                    }
                }
                if (factory.ContainsKey("task") && factory["task"] is not JObject) return PodBlocker.Of("no_task", PodTone.Error);
            }
            if (refusal != null) return PodBlocker.Of(refusal.Notice ?? PodStatusBoard.NoticeOf(null, refusal.Code, refusal.Message), PodTone.Error);
            if (recording) return new PodBlocker(OperatorNotices.Phrase("recording"), OperatorNotices.Phrase("recording_action"), PodTone.Good);
            // The newest attempt failed or its seal refused it: shown until the next start.
            if (LastFailure(status) is { } failed)
            {
                screen.LastFailed = true;
                return PodBlocker.Of(failed, PodTone.Error);
            }
            if (Bool(status, "expected") == true && status?["lastCheck"] is JObject lastCheck
                && RigStatusJson.Check(lastCheck.ToString(Formatting.None)) is { ready: false } failedCheck)
            {
                return PodBlocker.Of(OperatorNotice.FromJson(lastCheck["notice"]) ?? CheckNotice(failedCheck.problems), PodTone.Error);
            }
            if (!localOnly && Unfinished(factory) is { } unfinished) return unfinished;
            return localOnly
                ? new PodBlocker(OperatorNotices.Phrase("ready_local"), OperatorNotices.Phrase("ready_local_action"), PodTone.Good)
                : new PodBlocker(OperatorNotices.Phrase("ready"), OperatorNotices.Phrase("ready_action"), PodTone.Good);
        }

        /// <summary>
        /// A collection that cannot finish (factory.unfinished: a verdict or answer the Factory
        /// refused, a prompt the headset cannot answer, an attempt it cannot tell), as a warning: a
        /// start may still go ahead. The reasons themselves stay in the status for the logs. Null
        /// when nothing holds one back.
        /// </summary>
        public static PodBlocker? Unfinished(JObject? factory)
        {
            if (factory?["unfinished"] is not JArray reasons) return null;
            return reasons.OfType<JObject>().Any(r => Str(r["reason"]).Length > 0) ? PodBlocker.Of("collection_unfinished", PodTone.Warning) : (PodBlocker?)null;
        }

        /// <summary>A failing device row as its notice: missing, silent, or what its failure says.</summary>
        private static OperatorNotice DeviceNotice(PodDeviceRow device)
        {
            var part = device.Role.Contains("camera") ? "wrist_camera" : "gripper_sensor";
            var side = device.Side;
            return device.State switch
            {
                PodDeviceState.Missing => OperatorNotice.Of("device_missing", ("side", side), ("part", part)),
                PodDeviceState.Silent => OperatorNotice.Of("device_silent", ("side", side), ("part", part)),
                _ => FailureNotice(device.Failure, side, part) ?? OperatorNotice.Of("rig_check_failed"),
            };
        }

        /// <summary>A device check's problems as a notice (a runtime older than its notices).</summary>
        private static OperatorNotice CheckNotice(string problems)
        {
            var side = problems.ToLowerInvariant().Contains("right") && !problems.ToLowerInvariant().Contains("left") ? "right" : problems.ToLowerInvariant().Contains("left") ? "left" : "";
            var part = problems.ToLowerInvariant().Contains("camera") ? "wrist_camera" : "gripper_sensor";
            return FailureNotice(problems, side, part) ?? OperatorNotice.Of("rig_check_failed");
        }

        /// <summary>The notice a device failure's words name, for a device row; null when none is known.</summary>
        public static OperatorNotice? FailureNotice(string detail, string side, string part)
        {
            var lower = detail.ToLowerInvariant();
            if (lower.Contains("sends nothing") || lower.Contains("0 bytes received") || lower.Contains("another app"))
                return OperatorNotice.Of("usb_other_app", ("side", side), ("part", part));
            if (lower.Contains("os error 12") || lower.Contains("out of memory")) return OperatorNotice.Of("usb_bandwidth");
            if (lower.Contains("was detached") || lower.Contains("device_disconnected") || lower.Contains("disconnected"))
                return OperatorNotice.Of("device_disconnected", ("side", side), ("part", part));
            if (lower.Contains("no device fills this role") || lower.Contains("no device on"))
                return OperatorNotice.Of("device_missing", ("side", side), ("part", part));
            if (lower.Contains("permission")) return OperatorNotice.Of("usb_permission");
            return null;
        }

        /// <summary>The action that clears a failure we know, in the table's words; "" when none is known.</summary>
        public static string Hint(string detail, string side = "", string part = "") =>
            FailureNotice(detail, side, part) is { } notice ? OperatorNotices.Render(notice).Action : "";

        /// <summary>sha256:abcdef... -> the first 12 hex digits; "this build" when unknown.</summary>
        public static string ShortBuild(string buildId)
        {
            var hex = buildId.StartsWith("sha256:", StringComparison.Ordinal) ? buildId.Substring(7) : buildId;
            return hex.Length == 0 ? "this build" : hex.Length > 12 ? hex.Substring(0, 12) : hex;
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
                        var hint = Hint(earlier, side, NoticePart(part));
                        row = new PodDeviceRow(name, row.Value, "Failed last episode", PodTone.Warning, hint.Length > 0 ? hint : Action("device_silent"),
                            role, side, row.Port, PodDeviceState.Failed, earlier);
                    }
                    screen.Devices.Add(row);
                }
            }
            if (status?["usb"] is JObject usb && usb["awaitingPermission"] is JArray waiting && waiting.Count > 0)
            {
                screen.DevicesNote = OperatorNotices.Render(OperatorNotice.Of("usb_permission")).Action;
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
            var noticePart = NoticePart(part);
            // The fix in the table's words: what the failure names, else the state's own.
            string Fix(string words, string otherwise) => Hint(words, side, noticePart) is { Length: > 0 } hint ? hint : Action(otherwise);
            PodDeviceRow Row(string value, string pill, PodTone tone, string hint, PodDeviceState state, string said = "") =>
                new(name, value, pill, tone, hint, role, side, port, state, said);
            if (!attached)
            {
                return Row("not on USB", "Missing", PodTone.Error, Fix(failure, "device_missing"), PodDeviceState.Missing, failure);
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
                        "Silent", PodTone.Error, Fix(failure, "device_silent"), PodDeviceState.Silent, failure);
            }
            if (failure.Length > 0)
            {
                return Row(where, "Failed", PodTone.Error, Fix(failure, "device_silent"), PodDeviceState.Failed, failure);
            }
            return check switch
            {
                "ready" => Row(where, "Ready", PodTone.Good, "", PodDeviceState.Ready),
                "error" or "unavailable" => Row(where, "Check failed", PodTone.Warning, Fix(checkDetail, "rig_check_failed"), PodDeviceState.Failed, checkDetail),
                // Attached, not checked yet: the next start checks it.
                _ => Row(where, "Attached", PodTone.Neutral, "", PodDeviceState.Starting),
            };
        }

        /// <summary>A role's part as the notice table names it.</summary>
        private static string NoticePart(string part) => part == "camera" ? "wrist_camera" : "gripper_sensor";

        /// <summary>A code's next action in the table's words.</summary>
        private static string Action(string code) => OperatorNotices.Render(OperatorNotice.Of(code)).Action;

        /// <summary>
        /// The newest attempt's notice when it failed or its seal refused it (libsx_rig's
        /// attempts[0].notice; the unknown code for one that carries none): what the blocker shows
        /// until the next start makes a newer attempt. Null otherwise.
        /// </summary>
        public static OperatorNotice? LastFailure(JObject? status)
        {
            if (Attempts(status).FirstOrDefault() is not JObject newest) return null;
            var stage = Str(newest["stage"]);
            if (stage is "recording" or "unsettled") return null;
            var outcome = Str(newest["outcome"]);
            var failed = stage is "failed" or "refused" || (outcome.Length > 0 && outcome != "complete") || Str(newest["seal"]) == "refused";
            if (!failed) return null;
            return OperatorNotice.FromJson(newest["notice"])
                ?? (Str(newest["seal"]) == "refused" || stage == "refused" ? OperatorNotice.Of("seal_quality") : OperatorNotice.Of("unknown", ("code", outcome.Length > 0 ? outcome : "failed")));
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
                    // The error itself stays in the status for the logs.
                    screen.Link = ago >= 0 ? "not heard for " + ago.ToString(CultureInfo.InvariantCulture) + " s" : "not heard yet";
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
                if (Str(check["id"]) == "recorder_build") return "this app version approved in the Console (build " + ShortBuild(Str(status?["apkBuildId"])) + ")";
                if (Str(check["id"]) == "operator") return "a collector signed in";
                var remediation = Str(check["remediation"]);
                return Clip(remediation.Length > 0 ? remediation : Str(check["summary"]), 70);
            }
            if (Str(progress?["registration_error"]).Length > 0) return "Catalog registration, which is failing: a technician checks it in the Console";
            if (factory["unfinished"] is JArray { Count: > 0 }) return "a collection to finish: the floor manager checks it in the Console";
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
            foreach (var attempt in Attempts(status)) screen.Episodes.Add(EpisodeRow(attempt));
        }

        /// <summary>Sealing longer than this reads as stalled.</summary>
        public const long SealingStalledSeconds = 10 * 60;

        /// <summary>
        /// One attempt's row: the steps it completed, where it failed, its stage word (the notice
        /// table's: Recorded, Sealing, Sealed or Seal refused, Reported, Awaiting review, Delivered,
        /// In Catalog) and its detail: a failure's headline and its fix as the table words them,
        /// how long it has been sealing, or delivered's note. Never the raw detail.
        /// </summary>
        public static PodEpisodeRow EpisodeRow(JObject attempt)
        {
            var id = Str(attempt["id"]);
            var stage = Str(attempt["stage"]);
            var outcome = Str(attempt["outcome"]);
            var failedRecording = outcome.Length > 0 && outcome != "complete";
            var refusedSeal = Str(attempt["seal"]) == "refused";
            var integrity = Str(attempt["integrity"]);
            var notice = OperatorNotice.FromJson(attempt["notice"]);
            if (notice == null && (failedRecording || refusedSeal || stage is "failed" or "refused"))
            {
                notice = refusedSeal || stage == "refused" ? OperatorNotice.Of("seal_quality") : OperatorNotice.Of("unknown", ("code", failedRecording ? outcome : "failed"));
            }
            var words = notice is { } n ? OperatorNotices.Render(n) : (OperatorMessage?)null;
            var detail = words?.Headline ?? "";
            var fix = words?.Action ?? "";
            PodEpisodeRow Row(int reached, int failedAt, string key, PodTone tone, string text = "", string action = "", long? sealing = null) =>
                new(id, reached, failedAt, OperatorNotices.Stage(key), tone, text, action, key, notice, sealing);
            switch (stage)
            {
                case "recording": return Row(0, -1, "recording", PodTone.Good);
                case "unsettled": return new PodEpisodeRow(id, 0, -1, "Stopping", PodTone.Warning, "", "", "recording");
                case "failed": return Row(0, 0, "failed", PodTone.Error, detail, fix);
                case "sealing":
                    var seconds = attempt["sealing_seconds"] is JValue { Type: JTokenType.Integer } s ? (long)s : (long?)null;
                    var minutes = seconds is long sec ? Math.Max(1, (sec + 30) / 60).ToString(CultureInfo.InvariantCulture) : "";
                    var note = seconds is long waited
                        ? OperatorNotices.Phrase(waited >= SealingStalledSeconds ? "sealing_stalled" : "sealing_for", ("minutes", minutes))
                        : "";
                    return Row(1, -1, "sealing", seconds >= SealingStalledSeconds ? PodTone.Warning : PodTone.Neutral, note, "", seconds);
                case "refused": return Row(1, 1, "seal_refused", PodTone.Error, detail, fix);
                case "sealed": return Row(2, -1, "sealed", PodTone.Neutral);
            }
            // Reported onward: a failed recording or refused seal reported to the Factory stays failed.
            var failedAt = failedRecording ? 0 : refusedSeal ? 1 : -1;
            if (failedAt >= 0) return Row(failedAt, failedAt, failedRecording ? "failed" : "seal_refused", PodTone.Error, detail, fix);
            if (stage == "reviewed" && integrity == "failed") return Row(3, 3, "review_failed", PodTone.Error);
            return stage switch
            {
                // Reported: the Factory took it; with its episode in the workspace it waits for review.
                "reported" => Row(3, -1, integrity.Length > 0 ? "awaiting_review" : "reported", PodTone.Neutral),
                "reviewed" => Row(4, -1, "reviewed", PodTone.Good),
                // Its receipt is here: delivered, and its bytes leave the headset within the hour. Never "Uploading".
                "finalized" => Row(5, -1, "delivered", PodTone.Good, OperatorNotices.Phrase("delivered_note")),
                "registered" => Row(6, -1, "in_catalog", PodTone.Good),
                _ => new PodEpisodeRow(id, 0, -1, OperatorNotices.Stage("recorded"), PodTone.Neutral, ""),
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
