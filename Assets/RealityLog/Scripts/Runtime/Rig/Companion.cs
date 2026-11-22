# nullable enable

using System;
using System.Globalization;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RealityLog.Rig
{
    /// <summary>
    /// Receives one HeadsetStatus (companion.proto) field group at a time. The Android writer
    /// fills the library's protobuf message; the fingerprint writer tells whether anything changed.
    /// </summary>
    public interface IHeadsetStatusWriter
    {
        void Header(bool recording, int? batteryPct, string podId, string podName, string collector);
        /// <summary>Called only when something stops recording.</summary>
        void Blocker(string headline, string action);
        void Device(string role, string side, string port, PodDeviceState state, string detail, string fix);
        void Collection(string task, long credited, long requested, long awaitingReview, string expects, bool factoryLinkOk, long? heardSecondsAgo);
        /// <summary><paramref name="stage"/> is companion.proto's HeadsetEpisodeStage number, 1 (RECORDED) to 7 (FAILED).</summary>
        void Episode(string id, int stage, string reason, string fix);
        /// <summary>Each controller's charge, 0..100; null when it cannot be read.</summary>
        void Controllers(int? leftPct, int? rightPct);
        /// <summary>A start now would certainly be refused; the blocker says why. The phone then offers no Start.</summary>
        void StartBlocked(bool blocked);
        /// <summary>The blocker's notice (after <see cref="Blocker"/>): its code, who acts (collector, floor_manager, technician), its arguments and words.</summary>
        void BlockerNotice(OperatorNotice notice, OperatorMessage words);
        /// <summary>One warning before a start that may still go ahead.</summary>
        void Warning(OperatorNotice notice, OperatorMessage words);
        /// <summary>The last episode's notice (after <see cref="Episode"/>): why it failed or was refused.</summary>
        void EpisodeNotice(OperatorNotice notice, OperatorMessage words);
        /// <summary>How long the last episode has waited for its seal.</summary>
        void EpisodeSealing(long seconds);
        /// <summary>Local only: nothing recorded is delivered.</summary>
        void LocalOnly(bool localOnly);
    }

    /// <summary>The Touch controllers' charge (<see cref="ControllerBattery"/>); null where unknown.</summary>
    public readonly struct ControllerCharge
    {
        public static readonly ControllerCharge Unknown = default;

        public int? Left { get; }
        public int? Right { get; }
        /// <summary>Whether each controller is connected to the headset now, as a read said it.</summary>
        public bool LeftConnected { get; }
        public bool RightConnected { get; }
        /// <summary>A read made this value; <see cref="Unknown"/> and a failed read are not one.</summary>
        public bool Known { get; }

        public ControllerCharge(int? left, int? right)
        {
            Left = left;
            Right = right;
            LeftConnected = false;
            RightConnected = false;
            Known = false;
        }

        /// <summary>A read of both controllers: each charge (null where no source read it) and connection.</summary>
        public ControllerCharge(int? left, int? right, bool leftConnected, bool rightConnected)
        {
            Left = left;
            Right = right;
            LeftConnected = leftConnected;
            RightConnected = rightConnected;
            Known = true;
        }

        /// <summary>
        /// One controller's reading: OVRInput answers 0 for a controller that is off, not
        /// connected or does not report, so 0 is unknown rather than empty.
        /// </summary>
        public static int? Reading(bool connected, int percent) =>
            connected && percent > 0 ? Math.Min(percent, 100) : (int?)null;

        /// <summary>
        /// Unity XR's batteryLevel (0..1) as a percent: a read that failed, or 0, is unknown (a
        /// runtime that does not report answers 0), as is anything outside 0..1.
        /// </summary>
        public static int? FromLevel(bool ok, float level) =>
            ok && level > 0f && level <= 1f ? (int?)Math.Max(1, (int)Math.Round(level * 100f)) : null;

        /// <summary>The first source that read the charge.</summary>
        public static int? First(int? preferred, int? fallback) => preferred ?? fallback;
    }

    /// <summary>
    /// The operator's controls, on the Pod card and from the paired phone (companion.proto
    /// CompanionCommand). Ending a collection is not one: the Factory ends it between episodes
    /// when a change needs it.
    /// </summary>
    public enum PodControl
    {
        Check,
        Start,
        Stop,
    }

    /// <summary>How one control ended, in the card's words; the phone gets the same.</summary>
    public readonly struct PodControlResult
    {
        public PodControl Control { get; }
        public bool Ok { get; }
        public string Message { get; }

        public PodControlResult(PodControl control, bool ok, string message)
        {
            Control = control;
            Ok = ok;
            Message = message;
        }
    }

    /// <summary>The controls' words and wire names. Pure: tested on the host.</summary>
    public static class PodControlWords
    {
        /// <summary>companion.proto's CompanionCommand name without its prefix.</summary>
        public static string WireName(PodControl control) => control switch
        {
            PodControl.Check => "CHECK",
            PodControl.Start => "START",
            _ => "STOP",
        };

        public static string Label(PodControl control) => control switch
        {
            PodControl.Check => "Check",
            PodControl.Start => "Start",
            _ => "Stop",
        };

        /// <summary>The pill's word while the control runs.</summary>
        public static string Running(PodControl control) => control switch
        {
            PodControl.Check => "Checking...",
            PodControl.Start => "Starting...",
            _ => "Stopping...",
        };

        /// <summary>The result line under the controls: "Check: Ready to record".</summary>
        public static string Line(PodControlResult result) =>
            Label(result.Control) + ": " + (result.Message.Length > 0 ? result.Message : result.Ok ? "done" : "refused");

        /// <summary>The words of an HTTP start or stop answer: its message, else its error code, else the status.</summary>
        public static string HttpWords(int statusCode, string? body, bool start)
        {
            if (statusCode is >= 200 and < 300) return start ? "Recording" : "Stopped; the episode seals next";
            // The notice table's words for it: never the message it carries (start_session: {...},
            // os error 32, a path), which stays in the log.
            var (code, message) = PodStatusBoard.ParseError(body, statusCode);
            if (!start) return "The stop was refused (code " + (code.Length > 0 ? code : statusCode.ToString(CultureInfo.InvariantCulture)) + ")";
            return PodStatusBoard.Words(PodStatusBoard.NoticeOf(body, code, message));
        }

        /// <summary>A controller's charge on the strip: "64%", or "not available" when no source reads it.</summary>
        public static string Percent(int? pct) => pct is int p ? p + "%" : "not available";

        /// <summary>
        /// <see cref="Percent(int?)"/>, saying why when the charge is not available for want of the
        /// DUMP grant (the only source of it on Horizon OS, granted over adb at provisioning).
        /// </summary>
        public static string Percent(int? pct, bool? dumpGranted) =>
            pct is null && dumpGranted == false ? "needs the DUMP grant (adb pm grant)" : Percent(pct);
    }

    /// <summary>
    /// The phone companion's HeadsetStatus from the Pod card's status screen (PodScreenBoard) and
    /// the rig status it was built from: the one mapping, so the phone shows what the card shows.
    /// Pure: no UnityEngine, tested on the host (tools/sx-rig-stub/test_binding.sh).
    /// </summary>
    public static class HeadsetStatusMap
    {
        /// <summary>The episode stages, by PodEpisodeRow.Reached (companion.proto HeadsetEpisodeStage numbers).</summary>
        public const int StageFailed = 7;
        public const int StageSealing = 8;
        public const int StageSealRefused = 9;
        public const int StageAwaitingReview = 10;
        public const int StageReviewFailed = 11;

        /// <summary>companion.proto's HeadsetEpisodeStage names without their prefix, by number (0 has none).</summary>
        public static readonly string[] StageNames =
            { "", "RECORDED", "SEALED", "REPORTED", "REVIEWED", "FINALIZED", "IN_CATALOG", "FAILED", "SEALING", "SEAL_REFUSED", "AWAITING_REVIEW", "REVIEW_FAILED" };

        /// <summary>companion.proto's HeadsetDeviceState name without its prefix ("READY", "LIVE", ...).</summary>
        public static string StateName(PodDeviceState state) => state.ToString().ToUpperInvariant();

        public static void Write(PodScreen screen, JObject? status, string podName, ControllerCharge controllers, IHeadsetStatusWriter writer)
        {
            var podId = Bool(status, "enrolled") == true ? Str(status?["podId"]) : "";
            writer.Header(screen.Recording, Battery(status), podId, podName, Collector(status));
            writer.Controllers(controllers.Left, controllers.Right);
            // Ready and Recording stop nothing: the phone shows the headset as ready from that.
            var blocker = screen.Blocker;
            if (screen.StartBlocked && blocker.Tone == PodTone.Good && PodScreenBoard.StartBlocker(status) is { } start) blocker = start;
            if (blocker.Tone != PodTone.Good)
            {
                writer.Blocker(blocker.Headline, blocker.Action);
                if (blocker.Notice is { } notice) writer.BlockerNotice(notice, OperatorNotices.Render(notice));
            }
            writer.StartBlocked(screen.StartBlocked);
            writer.LocalOnly(screen.LocalOnly);
            foreach (var warning in screen.Warnings)
            {
                if (warning.Notice is { } notice) writer.Warning(notice, OperatorNotices.Render(notice));
            }
            foreach (var device in screen.Devices)
            {
                writer.Device(device.Role, device.Side, device.Port, device.State, device.Value, device.Hint);
            }
            writer.Collection(screen.Task, screen.Credited, screen.Requested, screen.AwaitingReview, screen.Next, screen.LinkOk, screen.HeardSecondsAgo);
            foreach (var episode in screen.Episodes)
            {
                // An episode not recorded yet (recording, stopping) has no stage: `recording` tells it.
                var stage = Stage(episode);
                if (stage == 0) continue;
                writer.Episode(episode.Id, stage, episode.Detail.Length > 0 ? episode.Stage + ": " + episode.Detail : episode.Stage, episode.Fix);
                if (episode.Notice is { } why) writer.EpisodeNotice(why, OperatorNotices.Render(why));
                if (episode.SealingSeconds is long seconds) writer.EpisodeSealing(seconds);
            }
        }

        /// <summary>
        /// The row's stage on the wire, by its key: SEALING, SEAL_REFUSED, AWAITING_REVIEW,
        /// REVIEW_FAILED and FAILED by name; else RECORDED (1) .. IN_CATALOG (6) by the steps
        /// completed; 0 before it is recorded (recording, stopping).
        /// </summary>
        public static int Stage(PodEpisodeRow episode) => episode.StageKey switch
        {
            "recording" => 0,
            "sealing" => StageSealing,
            "seal_refused" => StageSealRefused,
            "awaiting_review" => StageAwaitingReview,
            "review_failed" => StageReviewFailed,
            "failed" => StageFailed,
            _ => episode.FailedAt >= 0 ? StageFailed : Math.Max(0, Math.Min(episode.Reached, 6)),
        };

        /// <summary>
        /// HeadsetStatus.collector: floor.collector's name (the Factory shift's collector, the
        /// paired phone's person); "" while the pod records unattributed.
        /// </summary>
        public static string Collector(JObject? status) => RigStatusJson.CollectorName(status) ?? "";

        /// <summary>deviceReport.battery_percent (0..100); null when the headset has not read it.</summary>
        public static int? Battery(JObject? status) =>
            status?["deviceReport"] is JObject report && report["battery_percent"] is JValue { Type: JTokenType.Integer } pct
            && (long)pct is >= 0 and <= 100
                ? (int?)(int)(long)pct
                : null;

        private static bool? Bool(JObject? obj, string key) =>
            obj?[key] is JValue { Type: JTokenType.Boolean } value ? (bool?)(bool)value : null;

        private static string Str(JToken? token) =>
            token is JValue { Type: JTokenType.String } value ? (string?)value ?? "" : "";
    }

    /// <summary>A HeadsetStatus written as text, to tell whether it changed since the last push.</summary>
    public sealed class HeadsetStatusFingerprint : IHeadsetStatusWriter
    {
        private readonly StringBuilder text = new();

        public string Text => text.ToString();

        public void Header(bool recording, int? batteryPct, string podId, string podName, string collector) =>
            text.Append("H|").Append(recording).Append('|').Append(batteryPct?.ToString(CultureInfo.InvariantCulture) ?? "-")
                .Append('|').Append(podId).Append('|').Append(podName).Append('|').Append(collector).Append('\n');

        public void Blocker(string headline, string action) => text.Append("B|").Append(headline).Append('|').Append(action).Append('\n');

        public void Device(string role, string side, string port, PodDeviceState state, string detail, string fix) =>
            text.Append("D|").Append(role).Append('|').Append(side).Append('|').Append(port).Append('|').Append((int)state)
                .Append('|').Append(detail).Append('|').Append(fix).Append('\n');

        public void Collection(string task, long credited, long requested, long awaitingReview, string expects, bool factoryLinkOk, long? heardSecondsAgo) =>
            text.Append("C|").Append(task).Append('|').Append(credited).Append('|').Append(requested).Append('|').Append(awaitingReview)
                .Append('|').Append(expects).Append('|').Append(factoryLinkOk)
                // The link's age moves every second: only whether it is known counts as a change.
                .Append('|').Append(heardSecondsAgo.HasValue).Append('\n');

        public void Episode(string id, int stage, string reason, string fix) =>
            text.Append("E|").Append(id).Append('|').Append(stage).Append('|').Append(reason).Append('|').Append(fix).Append('\n');

        public void StartBlocked(bool blocked) => text.Append("S|").Append(blocked).Append('\n');

        public void Controllers(int? leftPct, int? rightPct) =>
            text.Append("K|").Append(leftPct?.ToString(CultureInfo.InvariantCulture) ?? "-").Append('|')
                .Append(rightPct?.ToString(CultureInfo.InvariantCulture) ?? "-").Append('\n');

        public void BlockerNotice(OperatorNotice notice, OperatorMessage words) => Notice("BN", notice);

        public void Warning(OperatorNotice notice, OperatorMessage words) => Notice("W", notice);

        public void EpisodeNotice(OperatorNotice notice, OperatorMessage words) => Notice("EN", notice);

        // The minutes, not the seconds: a sealing episode's age moves every second.
        public void EpisodeSealing(long seconds) => text.Append("ES|").Append(seconds / 60).Append('\n');

        public void LocalOnly(bool localOnly) => text.Append("L|").Append(localOnly).Append('\n');

        private void Notice(string tag, OperatorNotice notice)
        {
            text.Append(tag).Append('|').Append(notice.Code);
            foreach (var arg in notice.Args.OrderBy(a => a.Key, StringComparer.Ordinal)) text.Append('|').Append(arg.Key).Append('=').Append(arg.Value);
            text.Append('\n');
        }
    }

    /// <summary>Where the headset's link with its phone stands, as the Pod card shows it.</summary>
    public enum CompanionPhase
    {
        /// <summary>This build carries no companion library.</summary>
        Unavailable,
        NotPaired,
        /// <summary>Looking for the phone's QR through the passthrough camera.</summary>
        Scanning,
        /// <summary>Scanned: finding and connecting to the phone that showed it.</summary>
        Connecting,
        /// <summary>Connected: the operator confirms the pairing on the phone.</summary>
        Confirming,
        /// <summary>Paired and connected.</summary>
        Connected,
        /// <summary>Paired, looking for the phone (out of range, or its app closed).</summary>
        Reconnecting,
        /// <summary>The last pairing attempt failed (see the error).</summary>
        Failed,
        PermissionDenied,
        BluetoothOff,
    }

    /// <summary>
    /// Where the link stands, from the companion library's CompanionHeadset callbacks: its state
    /// words, its errors, a finished pairing. A pairing is one attempt: its error is held until the
    /// library goes idle, then shown. While a paired phone is being resumed, a Bluetooth hiccup is
    /// the library's to retry and never shows; a refusal from the phone is final. Pure: tested on
    /// the host. Main thread.
    /// </summary>
    public sealed class CompanionLinkTracker
    {
        private bool pairing;
        private string pairError = "";

        public CompanionPhase Phase { get; private set; } = CompanionPhase.NotPaired;
        /// <summary>The last failure's words, "" when none.</summary>
        public string Error { get; private set; } = "";
        /// <summary>True from <see cref="PairStarted"/> until that attempt ends.</summary>
        public bool Pairing => pairing;

        /// <summary>Launch, or after a cancel: a paired phone is being looked for.</summary>
        public void Reset(bool paired)
        {
            pairing = false;
            pairError = "";
            Error = "";
            Phase = paired ? CompanionPhase.Reconnecting : CompanionPhase.NotPaired;
        }

        public void PairStarted()
        {
            pairing = true;
            pairError = "";
            Error = "";
            Phase = CompanionPhase.Connecting;
        }

        /// <summary>A CompanionHeadset.State word.</summary>
        public void OnState(string state, bool paired)
        {
            switch (state)
            {
                case "connected":
                    pairing = false;
                    Error = "";
                    Phase = CompanionPhase.Connected;
                    break;
                case "awaiting_operator":
                    Phase = CompanionPhase.Confirming;
                    break;
                case "scanning":
                case "connecting":
                case "handshaking":
                case "waiting_to_retry":
                    // A waiting retry keeps a reason the person must clear (Bluetooth off) on the card.
                    if (state == "waiting_to_retry" && Phase is CompanionPhase.BluetoothOff or CompanionPhase.PermissionDenied) break;
                    Phase = pairing ? CompanionPhase.Connecting : CompanionPhase.Reconnecting;
                    break;
                case "idle":
                    if (pairing)
                    {
                        pairing = false;
                        Phase = CompanionPhase.Failed;
                        Error = pairError.Length > 0 ? pairError : "the pairing stopped";
                    }
                    else if (Phase is not (CompanionPhase.Failed or CompanionPhase.PermissionDenied or CompanionPhase.BluetoothOff))
                    {
                        Phase = paired ? CompanionPhase.Reconnecting : CompanionPhase.NotPaired;
                    }
                    break;
                default:
                    Phase = CompanionPhase.Failed;
                    Error = "the companion said '" + state + "'";
                    break;
            }
        }

        /// <summary>
        /// A CompanionHeadset error (a CompanionFailure name or a Platform word). True when the
        /// phone no longer holds this pairing, so the headset forgets it too.
        /// </summary>
        public bool OnError(string failure, string detail)
        {
            switch (failure)
            {
                case "BLUETOOTH_OFF":
                    Phase = CompanionPhase.BluetoothOff;
                    return false;
                case "PERMISSION":
                    pairing = false;
                    Phase = CompanionPhase.PermissionDenied;
                    return false;
                case "NOT_PAIRED":
                    pairing = false;
                    Phase = CompanionPhase.NotPaired;
                    return false;
            }
            var words = ErrorWords(failure, detail);
            if (pairing)
            {
                pairError = words;
                return false;
            }
            if (failure != "REJECTED") return false; // resuming: the library retries
            Phase = CompanionPhase.Failed;
            Error = words;
            return detail.Contains("UNKNOWN_PAIRING");
        }

        /// <summary>A companion failure in the card's words.</summary>
        public static string ErrorWords(string failure, string detail)
        {
            if (failure == "REJECTED")
            {
                if (detail.Contains("DECLINED")) return "the operator declined on the phone";
                if (detail.Contains("UNKNOWN_PAIRING")) return "the phone no longer holds this pairing: pair again";
                if (detail.Contains("EXPIRED")) return "nobody confirmed on the phone in time";
                return "the phone refused";
            }
            return failure switch
            {
                "BAD_OFFER" => "that QR is not a phone's pairing code",
                "OFFER_EXPIRED" => "the QR expired: scan the code the phone shows now",
                "NOT_FOUND" => "the phone showing the code is not in range",
                "BAD_MAC" => "the phone did not prove the code: scan again",
                "GATT" => "Bluetooth: " + detail,
                _ => failure.ToLowerInvariant().Replace('_', ' ') + (detail.Length > 0 ? ": " + detail : ""),
            };
        }
    }

    /// <summary>The Pod card's Phone section: its line, its panel (when there is one) and its pills.</summary>
    public readonly struct CompanionCardLines
    {
        /// <summary>The Phone line: "Paired with Pixel 8 (Asha)", "Not paired".</summary>
        public string Line { get; }
        public PodTone LineTone { get; }
        /// <summary>A panel above the line while pairing or after a failure; "" when none.</summary>
        public string Headline { get; }
        public string Action { get; }
        public PodTone PanelTone { get; }
        /// <summary>The first pill's word ("Pair phone", "Cancel"); "" hides it.</summary>
        public string PairPill { get; }
        /// <summary>True: the first pill is the primary action (no phone paired yet).</summary>
        public bool PairPrimary { get; }
        public bool ShowForget { get; }

        public CompanionCardLines(string line, PodTone lineTone, string headline, string action, PodTone panelTone,
            string pairPill, bool pairPrimary, bool showForget)
        {
            Line = line;
            LineTone = lineTone;
            Headline = headline;
            Action = action;
            PanelTone = panelTone;
            PairPill = pairPill;
            PairPrimary = pairPrimary;
            ShowForget = showForget;
        }
    }

    /// <summary>The Pod card's Phone section in words. Pure: tested on the host.</summary>
    public static class CompanionCard
    {
        public const string ScanHeadline = "Look at the QR on your phone";

        /// <summary>
        /// <paramref name="phoneName"/> and <paramref name="operatorName"/> name the paired phone
        /// ("" when none is paired); <paramref name="error"/> is the last failure's words.
        /// </summary>
        public static CompanionCardLines Describe(CompanionPhase phase, string phoneName, string operatorName, string podName, string error)
        {
            var paired = phoneName.Length > 0 || operatorName.Length > 0;
            var who = paired ? "Paired with " + Named(phoneName, operatorName) : "Not paired";
            switch (phase)
            {
                case CompanionPhase.Unavailable:
                    return new CompanionCardLines("Not available in this build", PodTone.Neutral, "", "", PodTone.Neutral, "", false, false);
                case CompanionPhase.Scanning:
                    return new CompanionCardLines(who, PodTone.Neutral, ScanHeadline,
                        "Open Pair headset in the sx app and hold the phone in front of the headset", PodTone.Warning, "Cancel", false, false);
                case CompanionPhase.Connecting:
                    return new CompanionCardLines(who, PodTone.Neutral, "Connecting to the phone...",
                        "Keep the phone near the headset with its Pair screen open", PodTone.Warning, "Cancel", false, false);
                case CompanionPhase.Confirming:
                    return new CompanionCardLines(who, PodTone.Neutral, "Confirm on your phone",
                        "Tap Pair on the phone to pair it with " + (podName.Length > 0 ? podName : "this headset"), PodTone.Warning, "Cancel", false, false);
                case CompanionPhase.Connected:
                    return new CompanionCardLines(who + " · connected", PodTone.Good, "", "", PodTone.Neutral, "Pair another phone", false, true);
                case CompanionPhase.Reconnecting:
                    return new CompanionCardLines(who + " · looking for it", PodTone.Warning, "", "", PodTone.Neutral, "Pair another phone", false, true);
                case CompanionPhase.Failed:
                    return new CompanionCardLines(who, paired ? PodTone.Warning : PodTone.Neutral, "Pairing failed" + (error.Length > 0 ? ": " + Clip(error, 70) : ""),
                        "Open Pair headset on the phone again, then tap Pair phone", PodTone.Error, "Pair phone", !paired, paired);
                case CompanionPhase.PermissionDenied:
                    return new CompanionCardLines(who, PodTone.Neutral, "Bluetooth permission denied",
                        "Allow Nearby devices for OpenQuestCapture in Settings > Apps > Permissions, then tap Pair phone", PodTone.Error,
                        "Pair phone", !paired, paired);
                case CompanionPhase.BluetoothOff:
                    return new CompanionCardLines(who, PodTone.Neutral, "Bluetooth is off",
                        "Turn Bluetooth on in the headset's Quick Settings", PodTone.Error, "Pair phone", !paired, paired);
                default:
                    return paired
                        ? new CompanionCardLines(who, PodTone.Neutral, "", "", PodTone.Neutral, "Pair another phone", false, true)
                        : new CompanionCardLines("Not paired", PodTone.Neutral, "", "", PodTone.Neutral, "Pair phone", true, false);
            }
        }

        /// <summary>"Pixel 8 (Asha)", "Pixel 8", or "Asha's phone".</summary>
        public static string Named(string phoneName, string operatorName) =>
            phoneName.Length > 0
                ? operatorName.Length > 0 ? phoneName + " (" + operatorName + ")" : phoneName
                : operatorName + "'s phone";

        /// <summary>
        /// True when <paramref name="text"/> is a companion offer: "sxc1:" then unpadded base64url
        /// that decodes. Its contents are the library's to check (version, expiry, keys).
        /// </summary>
        public static bool IsOffer(string text)
        {
            const string prefix = "sxc1:";
            if (!text.StartsWith(prefix, StringComparison.Ordinal)) return false;
            var body = text.Substring(prefix.Length);
            if (body.Length < 8 || body.Length % 4 == 1) return false;
            foreach (var c in body)
            {
                if (!(c is >= 'A' and <= 'Z' || c is >= 'a' and <= 'z' || c is >= '0' and <= '9' || c == '-' || c == '_')) return false;
            }
            return true;
        }

        private static string Clip(string text, int max) =>
            text.Length <= max ? text : text.Substring(0, max - 3) + "...";
    }

    /// <summary>
    /// What the rig is told of the paired phone (<c>sx_rig_phone_link</c>), and when: once at
    /// start-up with the restored pairing, then on every change of the pairing (paired, forgotten,
    /// another phone) or of the link (connected, dropped). The heartbeat carries it as
    /// <c>paired_phone</c>, and the Factory credits the phone-opened shift's person only while it
    /// names that person connected, so a dead or absent phone stops crediting within a beat. A
    /// call the rig refused is tried again a second later, never every frame; one is in flight at
    /// a time (the call waits on the rig's lock, so it runs off the main thread). Pure: tested on
    /// the host. <see cref="Due"/> on the main thread, <see cref="Done"/> from any.
    /// </summary>
    public sealed class PhoneLinkReport
    {
        public const double RetrySeconds = 1.0;

        private volatile string? accepted;
        private volatile string? inFlight;
        private volatile bool failed;
        private volatile bool refusing;
        private double retryAt = double.NegativeInfinity;

        /// <summary>
        /// The rig's word: <c>{}</c> unpaired, else <c>{"collector_id": id, "connected": bool}</c>;
        /// a phone paired before it named its person gives <c>""</c>, which the Factory reads as
        /// unattributed.
        /// </summary>
        public static string Json(bool paired, string collectorId, bool connected) =>
            paired
                ? "{\"collector_id\":" + JsonConvert.ToString(collectorId) + ",\"connected\":" + (connected ? "true" : "false") + "}"
                : "{}";

        /// <summary>The word to send now, marked in flight, or null: the rig holds it, one is in flight, or a refusal waits out its second.</summary>
        public string? Due(bool paired, string collectorId, bool connected, double now)
        {
            if (inFlight != null) return null;
            if (failed)
            {
                failed = false;
                retryAt = now + RetrySeconds;
            }
            var json = Json(paired, collectorId, connected);
            if (json == accepted || now < retryAt) return null;
            inFlight = json;
            return json;
        }

        /// <summary>
        /// The call for <see cref="Due"/>'s word ended: taken, or refused (tried again after
        /// <see cref="RetrySeconds"/>). True for the first refusal after a call the rig took (or
        /// none yet), so a lasting refusal is logged once, not every second.
        /// </summary>
        public bool Done(bool ok)
        {
            if (inFlight is not string json) return false;
            var first = !ok && !refusing;
            refusing = !ok;
            if (ok) accepted = json;
            else failed = true;
            inFlight = null;
            return first;
        }
    }
}
