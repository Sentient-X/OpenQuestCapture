// Host check of the operator's words: the shipped table is libsx_rig's byte for byte, every code
// renders a headline, still-fine line, owner and action with no placeholder left and no raw text,
// and the production failures of 2026-10-15..29 reach the Pod card as their own words until the next
// start. Run by test_binding.sh with the shipped table and the sx checkout's; not part of the Unity
// project.
using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using RealityLog.Rig;

static class OperatorNoticesHarness
{
    static int failures;
    static void Expect(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) failures++; }

    static readonly string[] Raw = { "processor:", "start_session", "os error", "stereo_pair_fraction", "/data/", "SentientX", "{" };

    static string Role(string role, string port) =>
        "{\"role\":\"" + role + "\",\"registered\":\"id-" + role + "\",\"device\":\"/dev/" + role + "\",\"attached\":true,\"port\":\"" + port + "\",\"check\":\"ready\"}";
    static readonly string Pod = "\"expected\":true,\"enrolled\":true,\"localOnly\":false,\"library\":\"present\",\"open\":true,\"podId\":\"pod_q1\",\"apkBuildId\":\"sha256:0123456789abcdef0123\","
        + "\"roles\":[" + Role("left_hand_camera", "1-1.1") + "," + Role("right_hand_camera", "1-1.2") + "," + Role("left_hand_encoder", "1-1.3") + "," + Role("right_hand_encoder", "1-1.4") + "]";
    const string SignedIn = "\"floor\":{\"operator\":{\"signed_in\":true,\"display_name\":\"Asha\"}}";
    const string Factory = "\"factory\":{\"link\":{\"ok\":true,\"heard_seconds_ago\":3,\"error\":null},\"ready\":true,\"blocking\":[],\"task\":{\"name\":\"fold towel\",\"target\":10,\"done\":6}}";

    static PodScreen Screen(string body) =>
        PodScreenBoard.Build("{" + body + "}", null, false, new StreamLiveness(), new DateTime(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc));

    static int Main(string[] args)
    {
        var shipped = args[0];
        var source = args[1];
        Expect(File.ReadAllBytes(shipped).SequenceEqual(File.ReadAllBytes(source)), "the shipped table is libsx_rig's, byte for byte (" + source + ")");
        OperatorNotices.Load(File.ReadAllText(shipped));
        var codes = OperatorNotices.Codes.ToList();
        Expect(codes.Count >= 40 && codes.Contains("unknown"), "the table names " + codes.Count + " codes");

        var sample = new (string, string)[] { ("side", "left"), ("camera", "head"), ("part", "gripper_sensor"), ("code", "x1"), ("held", "3"),
            ("free_gb", "1.2"), ("needed_gb", "7.5"), ("uploads", "4"), ("percent", "12"), ("hz", "72") };
        foreach (var code in codes)
        {
            foreach (var notice in new[] { OperatorNotice.Of(code, sample), OperatorNotice.Of(code) })
            {
                var m = OperatorNotices.Render(notice);
                var ok = m.Headline.Length > 0 && m.StillFine.Length > 0 && m.Action.Length > 0
                    && m.Who is "collector" or "floor_manager" or "technician" && m.WhoLabel.Length > 0
                    && !Raw.Any(r => m.Headline.Contains(r) || m.Action.Contains(r) || m.StillFine.Contains(r));
                if (!ok) Expect(false, code + ": " + m.Headline + " | " + m.StillFine + " | " + m.Action);
            }
        }
        Expect(true, "every code renders its four parts, with or without arguments, and no raw text");

        var unknown = OperatorNotices.Render(OperatorNotice.Of("seal_hyperdrive"));
        Expect(unknown.Headline == "Recording failed" && unknown.Action == "Tell a technician (code seal_hyperdrive)." && unknown.Who == "technician",
            "an unknown code: Recording failed, tell a technician with the code");

        // The top production failures, as libsx_rig's attempts carry them: the blocker shows each until the next start.
        string Attempt(string stage, string seal, string outcome, string notice) =>
            Pod + "," + SignedIn + "," + Factory + ",\"attempts\":[{\"id\":\"20261029_1200\",\"stage\":\"" + stage + "\",\"seal\":\"" + seal
            + "\",\"outcome\":\"" + outcome + "\",\"detail\":\"raw detail stays in the log\",\"notice\":" + notice + "}]";
        var cases = new (string notice, string stage, string seal, string outcome, string headline, string who)[]
        {
            ("{\"code\":\"seal_video_gap\",\"args\":{\"side\":\"left\",\"camera\":\"head\"}}", "refused", "refused", "complete",
                "Last episode not kept: the left head camera skipped frames", "Collector"),
            ("{\"code\":\"seal_controller_frozen\",\"args\":{\"side\":\"left\"}}", "refused", "refused", "complete",
                "Last episode not kept: the left controller stopped tracking", "Collector"),
            ("{\"code\":\"video_behind\",\"args\":{\"side\":\"right\"}}", "failed", "", "failed",
                "Last episode stopped: the right wrist camera video could not keep up", "Collector"),
            ("{\"code\":\"gripper_link_lost\",\"args\":{\"side\":\"left\"}}", "failed", "", "failed",
                "Last episode stopped: the left gripper sensor lost its connection", "Collector"),
            ("{\"code\":\"seal_gripper_timing\",\"args\":{}}", "reported", "refused", "complete",
                "Last episode not kept: the gripper sensor's timing was off", "Technician"),
        };
        foreach (var c in cases)
        {
            var screen = Screen(Attempt(c.stage, c.seal, c.outcome, c.notice));
            Expect(screen.Blocker.Headline == c.headline && screen.Blocker.Who == c.who && screen.Blocker.StillFine.Length > 0
                && screen.Blocker.Tone == PodTone.Error, "after a failed episode the blocker shows it: " + screen.Blocker.Headline);
            Expect(screen.Episodes[0].Detail == c.headline && !screen.Episodes[0].Detail.Contains("raw"), "its row words it too: " + screen.Episodes[0].Stage);
        }
        var link = Screen(Attempt("failed", "", "failed", "{\"code\":\"gripper_link_lost\",\"args\":{\"side\":\"left\"}}"));
        Expect(link.Blocker.Button == "check", "a reseated cable is checked with the Check button");
        // A failure with no notice (an older library) is the unknown code, never its words.
        var bare = Screen(Pod + "," + SignedIn + "," + Factory + ",\"attempts\":[{\"id\":\"a\",\"stage\":\"failed\",\"outcome\":\"failed\",\"detail\":\"left encoder: read: Broken pipe (os error 32)\"}]");
        Expect(bare.Blocker.Headline == "Recording failed" && !bare.Blocker.Action.Contains("os error"), "no notice: Recording failed, " + bare.Blocker.Action);
        // The next start makes a newer attempt: the failure gives way.
        var next = Screen(Pod + "," + SignedIn + "," + Factory + ",\"attempts\":[{\"id\":\"b\",\"stage\":\"sealing\",\"seal\":\"pending\",\"outcome\":\"complete\",\"sealing_seconds\":130},"
            + "{\"id\":\"a\",\"stage\":\"refused\",\"seal\":\"refused\",\"outcome\":\"complete\",\"notice\":{\"code\":\"seal_video_gap\",\"args\":{}}}]");
        Expect(next.Blocker.Headline == "Ready to record", "a newer attempt clears it: " + next.Blocker.Headline);
        Expect(next.Episodes[0].Stage == "Sealing" && next.Episodes[0].Detail == "Sealing for 2 min", "sealing with its time: " + next.Episodes[0].Detail);
        var stalled = PodScreenBoard.EpisodeRow(JObject.Parse("{\"id\":\"b\",\"stage\":\"sealing\",\"sealing_seconds\":900}"));
        Expect(stalled.Detail.StartsWith("Sealing for 15 min, longer than usual") && stalled.Tone == PodTone.Warning, "a stalled seal says so: " + stalled.Detail);
        var delivered = PodScreenBoard.EpisodeRow(JObject.Parse("{\"id\":\"b\",\"stage\":\"finalized\",\"outcome\":\"complete\",\"seal\":\"sealed\"}"));
        Expect(delivered.Stage == "Delivered" && delivered.Detail == "Freeing space in about 1 h", "finalized is Delivered, never Uploading");

        // Start blocks, each with its owner.
        var held = Screen(Pod + "," + SignedIn + ",\"factory\":{\"link\":{\"ok\":true,\"heard_seconds_ago\":3},\"ready\":true,\"blocking\":[],\"task\":{\"name\":\"t\"},"
            + "\"start_refusal\":{\"reason\":\"no_episodes\",\"held\":4,\"notice\":{\"code\":\"no_episodes_held\",\"who\":\"floor_manager\",\"args\":{\"held\":\"4\"}}}}");
        Expect(held.StartBlocked && held.Blocker.Headline == "No episodes free: 4 held by attempts awaiting review" && held.Blocker.Who == "Floor manager",
            "no episodes, held by review: " + held.Blocker.Headline);
        var build = Screen(Pod + "," + SignedIn + ",\"factory\":{\"link\":{\"ok\":true},\"ready\":false,\"blocking\":[{\"id\":\"recorder_build\",\"state\":\"fail\",\"category\":\"build\"}],\"task\":{\"name\":\"t\"},"
            + "\"start_refusal\":{\"reason\":\"readiness\",\"notice\":{\"code\":\"build_not_accepted\",\"args\":{}}}}");
        Expect(build.Blocker.Headline == "App version not approved" && build.Blocker.Action.Contains("approves the trial in the Console"), "build not accepted: " + build.Blocker.Action);
        var registration = new StartRefusal("rig_start_failed", "start_session: {\"detail\":\"rig registration changed; run setup again\"}", DateTime.UtcNow);
        var reg = PodScreenBoard.Build("{" + Pod + "," + SignedIn + "," + Factory + "}", registration, false, new StreamLiveness(), DateTime.UtcNow);
        Expect(reg.Blocker.Headline == "The rig's registration changed" && !reg.Blocker.Headline.Contains("start_session"), "registration changed, never clipped raw: " + reg.Blocker.Headline);
        // The real defaults (rig.rs): 600 s x worst bytes/s = 1.32 GB, OQC's session 0.9288 GB: 2.2488 GB, above min_free (2 GiB).
        const long Defaults = 1_320_000_000L + 928_800_000L;
        var storage = Screen(Pod + "," + SignedIn + "," + Factory + ",\"native\":{\"storage\":{\"free_bytes\":1900000000,\"required_bytes\":" + Defaults
            + ",\"backlog_bytes\":0,\"backlog_episodes\":0,\"held\":{\"unreported\":{\"attempts\":2},\"awaiting_factory\":{\"attempts\":3}}}}");
        Expect(storage.StartBlocked && storage.Blocker.Headline == "Not enough space: 1.9 GB free, 2.2 GB needed for the next episode"
            && storage.Blocker.StillFine.StartsWith("5 episodes are waiting to upload"), "storage with numbers: " + storage.Blocker.Headline + " / " + storage.Blocker.StillFine);
        // Episodes still to seal are part of what the rig refuses on: shown apart, adding up.
        var sealing = Screen(Pod + "," + SignedIn + "," + Factory + ",\"native\":{\"storage\":{\"free_bytes\":3000000000,\"required_bytes\":" + (Defaults + 2_200_000_000)
            + ",\"backlog_bytes\":2200000000,\"backlog_episodes\":2,\"held\":{}}}");
        Expect(sealing.StartBlocked && sealing.Blocker.Headline == "Not enough space: 3.0 GB free, 4.4 GB needed (2.2 GB for the next episode, 2.2 GB for 2 episodes still to seal)",
            "storage split: " + sealing.Blocker.Headline);
        // At exactly required_bytes the rig starts, so the card does too (a warning, room for one more only).
        var exact = Screen(Pod + "," + SignedIn + "," + Factory + ",\"native\":{\"storage\":{\"free_bytes\":" + Defaults + ",\"required_bytes\":" + Defaults + ",\"held\":{}}}");
        Expect(!exact.StartBlocked && exact.Warnings.Any(w => w.Headline == "Space is tight: 2.2 GB free, 2.2 GB needed for the next episode"), "at the rig's own figure Start is offered, with a warning");
        var roomy = Screen(Pod + "," + SignedIn + "," + Factory + ",\"native\":{\"storage\":{\"free_bytes\":" + 2 * Defaults + ",\"required_bytes\":" + Defaults + ",\"held\":{}}}");
        Expect(!roomy.StartBlocked && roomy.Warnings.Count == 0, "room for two episodes: no storage warning");

        // The chip follows the banner.
        var failedChip = PodScreenBoard.Chip(Screen(Attempt("refused", "refused", "complete", "{\"code\":\"seal_video_gap\",\"args\":{}}")), PodReadiness.Ready, "Ready");
        Expect(failedChip == (PodTone.Error, "Last failed"), "after a failed episode the chip says Last failed: " + failedChip);

        // Warnings before Start: they never block it.
        var warn = Screen(Pod + "," + SignedIn + "," + Factory + ",\"headset\":{\"displayHz\":72.0,\"controllers\":["
            + "{\"side\":\"left\",\"connected\":true,\"tracked\":false,\"percent\":80},{\"side\":\"right\",\"connected\":true,\"tracked\":true,\"percent\":12}]},"
            + "\"native\":{\"storage\":{\"free_bytes\":9000000000,\"required_bytes\":7450000000,\"held\":{}}}");
        var words = warn.Warnings.Select(w => w.Headline).ToList();
        Expect(!warn.StartBlocked && warn.Blocker.Headline == "Ready to record", "warnings leave Start offered");
        Expect(PodScreenBoard.Chip(warn, PodReadiness.Ready, "Ready") == (PodTone.Warning, "Warnings"), "only warnings: the chip says Warnings");
        Expect(PodScreenBoard.Chip(next, PodReadiness.Ready, "Ready") == (PodTone.Good, "Ready"), "ready: the pod's own chip");
        Expect(words.Contains("The left controller is not tracked") && words.Contains("The right controller is at 12%")
            && words.Contains("The display runs at 72 Hz, not 90 Hz") && words.Any(w => w == "Space is tight: 9.0 GB free, 7.5 GB needed for the next episode"), "pre-start warnings: " + string.Join(" | ", words));
        var recording = Screen(Pod + "," + SignedIn + "," + Factory + ",\"episodeActive\":true,\"headset\":{\"displayHz\":72.0}");
        Expect(recording.Warnings.Count == 0, "no warnings while recording");

        // The one app name, everywhere the operator is told to choose one.
        Expect(codes.Select(c => OperatorNotices.Render(OperatorNotice.Of(c)).Action).Where(a => a.Contains("choose")).All(a => a.Contains("OpenQuestCapture")),
            "every 'choose' names OpenQuestCapture");

        Console.WriteLine(failures == 0 ? "ALL PASS" : failures + " FAILED");
        return failures == 0 ? 0 : 1;
    }
}
