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
    }

    static Recorder Map(string body, StreamLiveness? live = null, DateTime? at = null)
    {
        var json = "{" + body + "}";
        var screen = PodScreenBoard.Build(json, null, false, live ?? new StreamLiveness(), at ?? T0);
        var recorder = new Recorder();
        HeadsetStatusMap.Write(screen, JObject.Parse(json), "Bench pod 1", recorder);
        return recorder;
    }

    static string Fingerprint(string body, DateTime at)
    {
        var json = "{" + body + "}";
        var fingerprint = new HeadsetStatusFingerprint();
        HeadsetStatusMap.Write(PodScreenBoard.Build(json, null, false, new StreamLiveness(), at), JObject.Parse(json), "p", fingerprint);
        return fingerprint.Text;
    }

    static int Main()
    {
        StatusMapping();
        SignInHandOff();
        Offers();
        Card();
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
}
