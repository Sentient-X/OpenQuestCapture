// Host check of the phone companion's pure parts: HeadsetStatus from the Pod card's status
// screen (HeadsetStatusMap), the sign-in hand-off (CompanionSignInWatch), the QR offer check and
// the Pod card's Phone section in each pairing state (CompanionCard). Run by test_binding.sh.
using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using RealityLog.Rig;

static class CompanionHarness
{
    static int failures;
    static void Expect(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) failures++; }

    static readonly DateTime T0 = new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);

    const string Pod = "\"expected\":true,\"enrolled\":true,\"localOnly\":false,\"library\":\"present\",\"open\":true,\"podId\":\"pod_q1\",\"apkBuildId\":\"sha256:0123456789abcdef0123\"";
    const string SignedIn = "\"floor\":{\"operator\":{\"signed_in\":true,\"display_name\":\"Asha\"}}";
    const string Heard = "\"link\":{\"ok\":true,\"heard_seconds_ago\":3,\"error\":null}";
    const string Task = "\"task\":{\"name\":\"fold towel\",\"status\":\"active\",\"target\":2,\"done\":1}";

    static string Role(string role, bool attached, string port, string check, string stream = "null") =>
        "{\"role\":\"" + role + "\",\"registered\":\"id-" + role + "\",\"attached\":" + (attached ? "true" : "false")
        + ",\"port\":" + (port.Length > 0 ? "\"" + port + "\"" : "null") + ",\"check\":\"" + check + "\",\"check_detail\":\"\",\"stream\":" + stream + "}";

    /// <summary>Records each call, so a check can read what the phone would get.</summary>
    sealed class Recorder : IHeadsetStatusWriter
    {
        public (bool recording, int? battery, string podId, string podName, string collector) Head;
        public (string headline, string action)? Block;
        public readonly List<(string role, string side, string port, PodDeviceState state, string detail, string fix)> Devices = new();
        public (string task, long credited, long requested, long awaiting, string expects, bool ok, long? heard) Coll;
        public readonly List<(string id, int stage, string reason, string fix)> Episodes = new();

        public void Header(bool recording, int? batteryPct, string podId, string podName, string collector) => Head = (recording, batteryPct, podId, podName, collector);
        public void Blocker(string headline, string action) => Block = (headline, action);
        public void Device(string role, string side, string port, PodDeviceState state, string detail, string fix) => Devices.Add((role, side, port, state, detail, fix));
        public void Collection(string task, long credited, long requested, long awaitingReview, string expects, bool factoryLinkOk, long? heardSecondsAgo) =>
            Coll = (task, credited, requested, awaitingReview, expects, factoryLinkOk, heardSecondsAgo);
        public void Episode(string id, int stage, string reason, string fix) => Episodes.Add((id, stage, reason, fix));
        public (int? left, int? right) Pads;
        public void Controllers(int? leftPct, int? rightPct) => Pads = (leftPct, rightPct);
        public bool? Blocked;
        public void StartBlocked(bool blocked) => Blocked = blocked;
    }

    static Recorder Map(string body, StreamLiveness? live = null, DateTime? at = null, ControllerCharge pads = default)
    {
        var json = "{" + body + "}";
        var screen = PodScreenBoard.Build(json, null, false, live ?? new StreamLiveness(), at ?? T0);
        var recorder = new Recorder();
        HeadsetStatusMap.Write(screen, JObject.Parse(json), "Bench pod 1", pads, recorder);
        return recorder;
    }

    static string Fingerprint(string body, DateTime at, ControllerCharge pads = default)
    {
        var json = "{" + body + "}";
        var fingerprint = new HeadsetStatusFingerprint();
        HeadsetStatusMap.Write(PodScreenBoard.Build(json, null, false, new StreamLiveness(), at), JObject.Parse(json), "p", pads, fingerprint);
        return fingerprint.Text;
    }

    static int Main()
    {
        StatusMapping();
        SignInHandOff();
        Offers();
        Card();
        Tracker();
        Controls();
        Console.WriteLine(failures == 0 ? "companion harness: all passed" : "companion harness: " + failures + " failed");
        return failures == 0 ? 0 : 1;
    }

    static void StatusMapping()
    {
        var roles = "[" + Role("left_hand_camera", true, "1-1.1", "ready") + "," + Role("right_hand_camera", false, "", "") + ","
            + Role("left_hand_encoder", true, "1-1.3", "ready") + "," + Role("right_hand_encoder", true, "1-1.4", "") + "]";
        var attempts = "[{\"id\":\"ep-2\",\"stage\":\"failed\",\"outcome\":\"failed\",\"detail\":\"right camera: device was detached\"},"
            + "{\"id\":\"ep-1\",\"stage\":\"reviewed\",\"outcome\":\"complete\"}]";
        var status = Map(Pod + "," + SignedIn + ",\"deviceReport\":{\"battery_percent\":64,\"charging\":false},\"roles\":" + roles
            + ",\"attempts\":" + attempts + ",\"factory\":{" + Heard + ",\"ready\":true,\"blocking\":[]," + Task
            + ",\"progress\":{\"target\":2,\"credited\":1,\"awaiting_review\":1}}");
        Expect(status.Head == (false, 64, "pod_q1", "Bench pod 1", "Asha"), "header: not recording, battery 64, pod, collector " + status.Head);
        Expect(status.Block is { } b && b.headline.StartsWith("Right camera"), "blocker: the missing camera " + status.Block);
        Expect(status.Devices.Count == 4, "four roles");
        Expect(status.Blocked == false, "a missing device leaves Start offered: the start checks first");
        var allReady = "[" + Role("left_hand_camera", true, "1-1.1", "ready") + "," + Role("right_hand_camera", true, "1-1.2", "ready") + ","
            + Role("left_hand_encoder", true, "1-1.3", "ready") + "," + Role("right_hand_encoder", true, "1-1.4", "ready") + "]";
        var full = Map(Pod + "," + SignedIn + ",\"roles\":" + allReady + ",\"factory\":{" + Heard + ",\"ready\":true,\"blocking\":[]," + Task
            + ",\"start_refusal\":{\"reason\":\"no_episodes\",\"checks\":[]}}");
        Expect(full.Blocked == true && full.Block is { } nb && nb.headline == "No episodes left in this assignment"
            && nb.action == "Add episodes or assign a task in the Console", "no episodes: start_blocked with its reason " + full.Block);
        Expect(status.Devices[0] == ("left_hand_camera", "left", "1-1.1", PodDeviceState.Ready, "port 1-1.1", ""), "left camera ready on its port: " + status.Devices[0]);
        Expect(status.Devices[1].role == "right_hand_camera" && status.Devices[1].side == "right" && status.Devices[1].state == PodDeviceState.Missing
            && status.Devices[1].port == "" && status.Devices[1].fix.Length > 0, "right camera missing with a fix: " + status.Devices[1]);
        Expect(status.Devices[3].state == PodDeviceState.Starting, "an attached, unchecked encoder is starting: " + status.Devices[3].state);
        Expect(status.Coll == ("fold towel", 1, 2, 1, "a review verdict on the recorded episode", true, 3), "collection numbers: " + status.Coll);
        Expect(status.Episodes.Count == 2 && status.Episodes[0].id == "ep-2" && status.Episodes[0].stage == HeadsetStatusMap.StageFailed
            && status.Episodes[0].reason == "Failed: right camera: device was detached"
            && status.Episodes[0].fix == "It dropped off USB: reseat its cable at the hub", "failed episode: reason and fix apart: " + status.Episodes[0]);
        Expect(status.Episodes[1] == ("ep-1", 4, "Reviewed", ""), "reviewed episode -> REVIEWED (4): " + status.Episodes[1]);

        // Ready: no blocker (nothing stops recording); battery unknown stays absent; nobody signed in.
        var readyRoles = "[" + Role("left_hand_camera", true, "1-1.1", "ready") + "," + Role("right_hand_camera", true, "1-1.2", "ready") + ","
            + Role("left_hand_encoder", true, "1-1.3", "ready") + "," + Role("right_hand_encoder", true, "1-1.4", "ready") + "]";
        var ready = Map(Pod + "," + SignedIn + ",\"roles\":" + readyRoles + ",\"factory\":{" + Heard + ",\"ready\":true,\"blocking\":[]," + Task + "}");
        Expect(ready.Block == null, "ready: no blocker");
        Expect(ready.Head.battery == null, "no device report: battery absent");
        var signedOut = Map(Pod + ",\"floor\":{\"operator\":{\"signed_in\":false}},\"roles\":" + readyRoles + ",\"factory\":{" + Heard + "}");
        Expect(signedOut.Head.collector == "" && signedOut.Block is { headline: "Collector signed out" }, "signed out: no collector, the blocker says so");

        // Recording: LIVE while counters rise; the header says recording.
        var live = new StreamLiveness();
        string Streaming(long n) => "[" + Role("left_hand_camera", true, "1-1.1", "ready", "{\"source_frames\":" + n + ",\"source_fps\":30}") + "]";
        Map(Pod + "," + SignedIn + ",\"episodeActive\":true,\"roles\":" + Streaming(10), live, T0);
        var recording = Map(Pod + "," + SignedIn + ",\"episodeActive\":true,\"roles\":" + Streaming(40), live, T0.AddSeconds(1));
        Expect(recording.Head.recording && recording.Block is { headline: "Right camera: missing" }, "recording: header true; a missing role still blocks");
        Expect(recording.Devices[0].state == PodDeviceState.Live, "rising frames -> LIVE: " + recording.Devices[0].state);
        var midEpisode = Map(Pod + "," + SignedIn + ",\"episodeActive\":true,\"attempts\":[{\"id\":\"ep-3\",\"stage\":\"recording\"},{\"id\":\"ep-2\",\"stage\":\"sealed\"}]");
        Expect(midEpisode.Episodes.Count == 1 && midEpisode.Episodes[0] == ("ep-2", 2, "Sealed", ""), "the episode still recording has no stage and is left to `recording`");
        Expect(HeadsetStatusMap.StateName(PodDeviceState.Missing) == "MISSING" && HeadsetStatusMap.StageNames[6] == "IN_CATALOG"
            && HeadsetStatusMap.StageNames[HeadsetStatusMap.StageFailed] == "FAILED", "proto names: MISSING, IN_CATALOG, FAILED");

        // Not enrolled, no rig: kinds before roles; no pod id.
        var bare = Map("\"expected\":false,\"enrolled\":false,\"localOnly\":true");
        Expect(bare.Head.podId == "" && bare.Devices.Count == 0 && bare.Block != null, "bare headset: no pod id, no devices, a blocker");
        var unplaced = Map(Pod + "," + SignedIn + ",\"devices\":[{\"kind\":\"camera\",\"port\":\"1-1.1\"}]");
        Expect(unplaced.Devices.Count == 4 && unplaced.Devices[0] == ("camera", "", "1-1.1", PodDeviceState.Starting, "port 1-1.1", "")
            && unplaced.Devices[1].state == PodDeviceState.Missing, "unplaced: kinds, attached starting, absent missing: " + unplaced.Devices[0]);

        // The fingerprint ignores the link's age ticking, and sees a real change.
        var a = Fingerprint(Pod + "," + SignedIn + ",\"factory\":{\"link\":{\"ok\":true,\"heard_seconds_ago\":3}}", T0);
        var b2 = Fingerprint(Pod + "," + SignedIn + ",\"factory\":{\"link\":{\"ok\":true,\"heard_seconds_ago\":9}}", T0);
        var c = Fingerprint(Pod + ",\"floor\":{\"operator\":{\"signed_in\":false}},\"factory\":{\"link\":{\"ok\":true,\"heard_seconds_ago\":9}}", T0);
        Expect(a == b2, "fingerprint: the link's age alone is no change");
        Expect(a != c, "fingerprint: a sign-out is a change");
    }

    static void SignInHandOff()
    {
        JObject Floor(string op, string message = "") => JObject.Parse("{\"floor\":{\"operator\":" + op + ",\"message\":\"" + message + "\"}}");
        long Expiry(string code) => code == "WXYZ-1234" ? 1_760_000_000_000 : 0;
        var pending = Floor("{\"signed_in\":false,\"user_code\":\"WXYZ-1234\",\"verification_uri_complete\":\"https://auth/device?code=WXYZ-1234\"}");

        var watch = new CompanionSignInWatch();
        Expect(watch.Observe(pending, false, Expiry).Kind == CompanionSignInSendKind.None, "no link: nothing sent");
        var request = watch.Observe(pending, true, Expiry);
        Expect(request.Kind == CompanionSignInSendKind.Request && request.UserCode == "WXYZ-1234"
            && request.VerificationUriComplete == "https://auth/device?code=WXYZ-1234" && request.ExpiresAtMs == 1_760_000_000_000,
            "pending code -> SignInRequest with its address and expiry");
        Expect(watch.Observe(pending, true, Expiry).Kind == CompanionSignInSendKind.None, "the same code is sent once");
        watch.LinkRestarted();
        Expect(watch.Observe(pending, true, Expiry).Kind == CompanionSignInSendKind.Request, "a new session sends the pending code again");
        var result = watch.Observe(Floor("{\"signed_in\":true,\"display_name\":\"Asha\"}"), true, Expiry);
        Expect(result.Kind == CompanionSignInSendKind.Result && result.Collector == "Asha" && result.Error == "", "approved -> SignInResult with the collector");
        Expect(watch.Observe(Floor("{\"signed_in\":true,\"display_name\":\"Asha\"}"), true, Expiry).Kind == CompanionSignInSendKind.None, "the result is sent once");

        var expired = new CompanionSignInWatch();
        expired.Observe(pending, true, Expiry);
        Expect(expired.Observe(JObject.Parse("{}"), true, Expiry).Kind == CompanionSignInSendKind.None, "no floor yet: keep waiting");
        var lapse = expired.Observe(Floor("{\"signed_in\":false}", "the code expired"), true, Expiry);
        Expect(lapse.Kind == CompanionSignInSendKind.Result && lapse.Error == "the code expired", "expired -> SignInResult with the floor's words");

        var quiet = new CompanionSignInWatch();
        Expect(quiet.Observe(Floor("{\"signed_in\":true,\"display_name\":\"Asha\"}"), true, Expiry).Kind == CompanionSignInSendKind.None,
            "already signed in, no request sent: no result");
        quiet.SignInFailed("Sign-in failed: auth unreachable");
        var failed = quiet.Observe(Floor("{\"signed_in\":false}"), true, Expiry);
        Expect(failed.Kind == CompanionSignInSendKind.Result && failed.Error == "Sign-in failed: auth unreachable", "a failed sign-in call -> SignInResult error");
        var other = new CompanionSignInWatch();
        Expect(other.Observe(Floor("{\"signed_in\":false,\"user_code\":\"AB-CD\"}"), true, Expiry).ExpiresAtMs == 0, "unknown expiry stays 0");
    }

    static void Offers()
    {
        Expect(CompanionCard.IsOffer("sxc1:CAESEAECAwQFBgcICQoLDA0ODxAaIQI"), "sxc1: base64url -> an offer");
        Expect(!CompanionCard.IsOffer("{\"factory_url\":\"https://x\",\"join_code\":\"abc\"}"), "the Console's enroll QR is not an offer");
        Expect(!CompanionCard.IsOffer("sxc1:abc+/def=="), "standard base64 with padding is refused");
        Expect(!CompanionCard.IsOffer("sxc1:"), "empty body is refused");
        Expect(!CompanionCard.IsOffer("SXC1:CAESEAECAwQFBgcI"), "the prefix is exact");
    }

    static void Card()
    {
        var none = CompanionCard.Describe(CompanionPhase.NotPaired, "", "", "Bench pod 1", "");
        Expect(none.Line == "Not paired" && none.PairPill == "Pair phone" && none.PairPrimary && !none.ShowForget && none.Headline == "", "not paired: Pair phone, primary");
        var scan = CompanionCard.Describe(CompanionPhase.Scanning, "", "", "Bench pod 1", "");
        Expect(scan.Headline == "Look at the QR on your phone" && scan.PairPill == "Cancel", "scanning: the instruction and Cancel");
        var confirm = CompanionCard.Describe(CompanionPhase.Confirming, "", "", "Bench pod 1", "");
        Expect(confirm.Action.Contains("Bench pod 1"), "confirming names the pod: " + confirm.Action);
        var connected = CompanionCard.Describe(CompanionPhase.Connected, "Pixel 8", "Asha", "", "");
        Expect(connected.Line == "Paired with Pixel 8 (Asha) · connected" && connected.LineTone == PodTone.Good && connected.ShowForget, "connected: " + connected.Line);
        var away = CompanionCard.Describe(CompanionPhase.Reconnecting, "Pixel 8", "Asha", "", "");
        Expect(away.Line.EndsWith("looking for it") && away.LineTone == PodTone.Warning && away.ShowForget, "reconnecting: " + away.Line);
        var denied = CompanionCard.Describe(CompanionPhase.PermissionDenied, "", "", "", "");
        Expect(denied.Headline == "Bluetooth permission denied" && denied.Action.Contains("Nearby devices") && denied.PanelTone == PodTone.Error,
            "permission denied: says where to allow it");
        var failed = CompanionCard.Describe(CompanionPhase.Failed, "", "", "", "the operator declined");
        Expect(failed.Headline == "Pairing failed: the operator declined" && failed.PairPill == "Pair phone", "failed: " + failed.Headline);
        Expect(CompanionCard.Named("", "Asha") == "Asha's phone", "no phone name: the operator's phone");
        Expect(CompanionCard.Describe(CompanionPhase.Unavailable, "", "", "", "").PairPill == "", "no library: no pill");
    }

    static void Tracker()
    {
        // A pairing: scanning, connecting, the operator asked, connected.
        var t = new CompanionLinkTracker();
        t.Reset(false);
        Expect(t.Phase == CompanionPhase.NotPaired, "launch, nothing paired: not paired");
        t.PairStarted();
        t.OnState("scanning", false);
        Expect(t.Phase == CompanionPhase.Connecting && t.Pairing, "pairing: scanning reads as connecting");
        t.OnState("awaiting_operator", false);
        Expect(t.Phase == CompanionPhase.Confirming, "the operator is asked: confirming");
        t.OnState("connected", true);
        Expect(t.Phase == CompanionPhase.Connected && !t.Pairing, "accepted: connected");

        // A session drops: the library retries; Bluetooth hiccups never show.
        t.OnState("waiting_to_retry", true);
        Expect(t.Phase == CompanionPhase.Reconnecting, "a dropped session: looking for it");
        Expect(!t.OnError("GATT", "subscribe failed (133)") && t.Phase == CompanionPhase.Reconnecting, "a GATT error while resuming stays quiet");
        t.OnError("BLUETOOTH_OFF", "turn Bluetooth on");
        t.OnState("waiting_to_retry", true);
        Expect(t.Phase == CompanionPhase.BluetoothOff, "Bluetooth off stays on the card through retries");
        t.OnState("scanning", true);
        Expect(t.Phase == CompanionPhase.Reconnecting, "Bluetooth back: looking again");

        // A declined pairing: the error waits for idle, then shows.
        var d = new CompanionLinkTracker();
        d.PairStarted();
        d.OnState("handshaking", false);
        d.OnError("REJECTED", "COMPANION_REJECT_REASON_DECLINED");
        Expect(d.Phase == CompanionPhase.Connecting, "a refusal waits for the attempt to end");
        d.OnState("idle", false);
        Expect(d.Phase == CompanionPhase.Failed && d.Error == "the operator declined on the phone", "declined: " + d.Error);

        var gone = new CompanionLinkTracker();
        gone.PairStarted();
        gone.OnError("NOT_FOUND", "the phone showing the code is not in range");
        gone.OnState("idle", false);
        Expect(gone.Error == "the phone showing the code is not in range", "not found: " + gone.Error);
        var expired = new CompanionLinkTracker();
        expired.PairStarted();
        expired.OnError("OFFER_EXPIRED", "the code expired; scan the phone again");
        expired.OnState("idle", false);
        Expect(expired.Error.StartsWith("the QR expired"), "expired code: " + expired.Error);

        // The phone forgot this headset: final, and the headset forgets too.
        var forgot = new CompanionLinkTracker();
        forgot.Reset(true);
        Expect(forgot.Phase == CompanionPhase.Reconnecting, "launch, paired: looking for it");
        Expect(forgot.OnError("REJECTED", "COMPANION_REJECT_REASON_UNKNOWN_PAIRING"), "unknown pairing -> forget it here too");
        forgot.OnState("idle", false);
        Expect(forgot.Phase == CompanionPhase.Failed && forgot.Error.Contains("pair again"), "unknown pairing: " + forgot.Error);

        var denied = new CompanionLinkTracker();
        denied.PairStarted();
        denied.OnError("PERMISSION", "need BLUETOOTH_SCAN");
        denied.OnState("idle", false);
        Expect(denied.Phase == CompanionPhase.PermissionDenied, "a permission error: permission denied, not a failed pairing");
        var odd = new CompanionLinkTracker();
        odd.OnState("teleporting", false);
        Expect(odd.Phase == CompanionPhase.Failed, "an unknown state word never reads as connected");
    }

    static void Controls()
    {
        // Controller charge: OVRInput's 0 and a disconnected controller are unknown; the phone gets both sides.
        Expect(ControllerCharge.Reading(true, 64) == 64 && ControllerCharge.Reading(true, 0) == null
            && ControllerCharge.Reading(false, 80) == null && ControllerCharge.Reading(true, 140) == 100, "controller readings: 64, 0 unknown, off unknown, clamped");
        var mapped = Map(Pod + "," + SignedIn, pads: new ControllerCharge(72, null));
        Expect(mapped.Pads == (72, null), "controllers mapped: " + mapped.Pads);
        Expect(Map(Pod).Pads == (null, null), "unread controllers stay absent");
        Expect(Fingerprint(Pod, T0, new ControllerCharge(72, 40)) != Fingerprint(Pod, T0, new ControllerCharge(71, 40)), "a controller's charge changing is a change");

        // Words: wire names are companion.proto's, the question carries the numbers, a result line names its control.
        Expect(PodControlWords.WireName(PodControl.Check) == "CHECK" && PodControlWords.WireName(PodControl.EndCollection) == "END_COLLECTION"
            && PodControlWords.WireName(PodControl.Start) == "START" && PodControlWords.WireName(PodControl.Stop) == "STOP", "wire names");
        Expect(PodControlWords.EndQuestion(3, 10) == "End collection now? 3 of 10 recorded", "end question with numbers");
        Expect(PodControlWords.EndQuestion(0, 0) == "End collection now?", "end question without a target");
        Expect(PodControlWords.Line(new PodControlResult(PodControl.Check, true, "Ready to record")) == "Check: Ready to record", "result line");
        Expect(PodControlWords.Line(new PodControlResult(PodControl.Stop, false, "")) == "Stop: refused", "an empty refusal still says refused");
        Expect(PodControlWords.HttpWords(200, "{}", true) == "Recording", "a start that worked");
        Expect(PodControlWords.HttpWords(503, "{\"error\":\"rig_not_ready\",\"message\":\"the device check did not pass: right camera missing\"}", true)
            == "the device check did not pass: right camera missing", "a refused start in the rig's words");
        Expect(PodControlWords.HttpWords(409, "{\"error\":\"not_recording\"}", false) == "not_recording", "a refusal with no message names its error");
        Expect(PodControlWords.HttpWords(503, "{\"error\":\"rig_not_ready\",\"reason\":\"rig_start_failed\",\"message\":\"sx_rig_start_armed failed: Device {\\\"code\\\":\\\"device\\\",\\\"detail\\\":\\\"start_session: {\\\\\\\"code\\\\\\\":\\\\\\\"http_409\\\\\\\",\\\\\\\"message\\\\\\\":\\\\\\\"no assigned episodes remain available; unfinished attempts reserve demand\\\\\\\"}\\\"}\"}", true)
            == "No episodes left in this assignment: add episodes or assign a task in the Console", "a Factory 409 reaches the phone in plain words");
        Expect(PodControlWords.HttpWords(503, "{\"error\":\"rig_not_ready\",\"reason\":\"rig_start_failed\",\"message\":\"start_session: http_422 required readiness checks are not passing\"}", true)
            == "Readiness checks are failing: run Check", "a Factory 422 reaches the phone in plain words");
        Expect(PodControlWords.HttpWords(500, "not json", false) == "the stop was refused (500)", "an unreadable refusal names the status");
        Expect(PodControlWords.Percent(null) == "not available" && PodControlWords.Percent(7) == "7%", "percent words");
        Expect(ControllerCharge.FromLevel(true, 0.6f) == 60 && ControllerCharge.FromLevel(true, 0f) == null
            && ControllerCharge.FromLevel(false, 0.6f) == null && ControllerCharge.FromLevel(true, 1.5f) == null, "XR batteryLevel 0..1 -> percent, 0 unknown");
        Expect(ControllerCharge.First(null, 40) == 40 && ControllerCharge.First(60, 40) == 60 && ControllerCharge.First(null, null) == null, "the first source that read it");
    }
}
