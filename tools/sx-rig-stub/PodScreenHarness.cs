// Host check of PodScreenBoard (the Pod card's status screen): the one blocker and its action,
// each device's state and fix, the collection's progress and what comes next, and each
// episode's steps through the pipeline. Run by test_binding.sh; not part of the Unity project.
using System;
using Newtonsoft.Json.Linq;
using RealityLog.Rig;

static class PodScreenHarness
{
    static int failures;
    static void Expect(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) failures++; }

    static readonly DateTime T0 = new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);

    const string Pod = "\"expected\":true,\"enrolled\":true,\"library\":\"present\",\"open\":true,\"podId\":\"pod_q1\",\"apkBuildId\":\"sha256:0123456789abcdef0123\"";
    const string Attributed = "\"floor\":{\"collector\":{\"id\":\"usr_1\",\"name\":\"Asha\"}}";
    const string Unattributed = "\"floor\":{\"collector\":null}";
    const string Heard = "\"link\":{\"ok\":true,\"heard_seconds_ago\":3,\"error\":null}";
    const string Task = "\"task\":{\"name\":\"fold towel\",\"status\":\"active\",\"target\":2,\"done\":1}";

    static string Role(string role, string device, bool attached, string port, string check, string detail = "", string stream = "null") =>
        "{\"role\":\"" + role + "\",\"registered\":\"id-" + role + "\",\"device\":" + (device.Length > 0 ? "\"" + device + "\"" : "null")
        + ",\"attached\":" + (attached ? "true" : "false") + ",\"port\":" + (port.Length > 0 ? "\"" + port + "\"" : "null")
        + ",\"check\":\"" + check + "\",\"check_detail\":\"" + detail + "\",\"stream\":" + stream + "}";

    static string ReadyRoles =>
        "[" + Role("left_hand_camera", "/dev/bus/usb/001/002", true, "1-1.1", "ready") + ","
        + Role("right_hand_camera", "/dev/bus/usb/001/003", true, "1-1.2", "ready") + ","
        + Role("left_hand_encoder", "/dev/bus/usb/001/004", true, "1-1.3", "ready") + ","
        + Role("right_hand_encoder", "/dev/bus/usb/001/005", true, "1-1.4", "ready") + "]";

    static PodScreen Screen(string body, StartRefusal? start = null, StreamLiveness? live = null, DateTime? at = null) =>
        PodScreenBoard.Build("{" + body + "}", start, false, live ?? new StreamLiveness(), at ?? T0);

    static int Main()
    {
        // The operator's words: the shipped table (test_binding.sh names it).
        RealityLog.Rig.OperatorNotices.Load(System.IO.File.ReadAllText(Environment.GetEnvironmentVariable("SX_OPERATOR_NOTICES")!));
        // Never throws on nonsense.
        var nothing = PodScreenBoard.Build("not json", null, false, new StreamLiveness(), T0);
        Expect(nothing.Blocker.Headline.Length > 0 && nothing.Devices.Count == 0, "unreadable status -> a blocker, no devices");

        // Ready: every role ready, the paired phone's person collecting, a task, the link heard.
        var ready = Screen(Pod + "," + Attributed + ",\"roles\":" + ReadyRoles + ",\"factory\":{" + Heard + ",\"ready\":true,\"blocking\":[]," + Task
            + ",\"progress\":{\"target\":2,\"credited\":1,\"awaiting_review\":1},\"collection\":null}");
        Expect(ready.Blocker.Headline == "Ready to record" && ready.Blocker.Tone == PodTone.Good, "ready: " + ready.Blocker.Headline);

        // The controller warnings read the app's own headset.controllers (RigCoordinator.ReadHeadset):
        // "tracked" is position AND orientation, so an IMU-only controller warns before a start.
        // The native device report carries no "tracked" (sx_rig.h refuses unknown keys).
        string Controllers(string left, string right) => Pod + "," + Attributed + ",\"roles\":" + ReadyRoles
            + ",\"factory\":{" + Heard + ",\"ready\":true,\"blocking\":[]," + Task + "}"
            + ",\"headset\":{\"displayHz\":90,\"controllers\":[" + left + "," + right + "]}";
        const string held = "\"connected\":true,\"tracked\":true,\"percent\":80}";
        var untracked = Screen(Controllers("{\"side\":\"left\"," + held, "{\"side\":\"right\",\"connected\":true,\"tracked\":false,\"percent\":80}"));
        Expect(untracked.Warnings.Count == 1 && untracked.Warnings[0].Headline == "The right controller is not tracked"
            && !untracked.StartBlocked, "an untracked controller warns (and leaves Start offered): "
            + string.Join("; ", untracked.Warnings.ConvertAll(w => w.Headline)));
        var bothHeld = Screen(Controllers("{\"side\":\"left\"," + held, "{\"side\":\"right\"," + held));
        Expect(bothHeld.Warnings.Count == 0, "tracked, connected, charged controllers raise no warning");
        // The right gripper's sensor names the wrong side (libsx_rig's gripper_switch.notice):
        // a warning before a start, in the table's words; Start stays offered (the menu works).
        var mismatch = Screen(Controllers("{\"side\":\"left\"," + held, "{\"side\":\"right\"," + held)
            + ",\"native\":{\"gripper_switch\":{\"holds\":0,\"refusals\":1,\"notice\":{\"code\":\"gripper_switch_mismatch\",\"who\":\"technician\",\"args\":{\"side\":\"right\"}}}}");
        Expect(mismatch.Warnings.Count == 1 && mismatch.Warnings[0].Headline == "The right gripper sensor names the wrong side"
            && !mismatch.StartBlocked, "a gripper switch mismatch warns: "
            + string.Join("; ", mismatch.Warnings.ConvertAll(w => w.Headline)));
        var agreed = Screen(Controllers("{\"side\":\"left\"," + held, "{\"side\":\"right\"," + held)
            + ",\"native\":{\"gripper_switch\":{\"holds\":2,\"refusals\":0,\"notice\":null}}");
        Expect(agreed.Warnings.Count == 0, "a switch whose sides agree raises no warning");
        var asleep = Screen(Controllers("{\"side\":\"left\",\"connected\":false,\"tracked\":false,\"percent\":80}", "{\"side\":\"right\"," + held));
        Expect(asleep.Warnings.Count == 1 && asleep.Warnings[0].Headline == "The left controller is not connected",
            "a disconnected controller says so, not 'not tracked'");
        Expect(ready.Devices.Count == 4 && ready.Devices[0].Name == "Left camera" && ready.Devices[0].Pill == "Ready" && ready.Devices[0].Value == "port 1-1.1", "devices by role with port: " + ready.Devices[0].Value);
        Expect(ready.Task == "fold towel" && ready.Progress == "1 of 2 credited · 1 awaiting review", "progress: " + ready.Progress);
        Expect(ready.Next == "a review verdict on the recorded episode", "next: " + ready.Next);
        Expect(ready.Link == "OK · heard 3 s ago" && ready.LinkTone == PodTone.Good, "link: " + ready.Link);

        Expect(!ready.StartBlocked && PodScreenBoard.StartBlocker(JObject.Parse("{" + Pod + "," + Attributed + "}")) == null, "ready: Start offered");

        // A collection that cannot finish (factory.unfinished, from quest-rig) is shown, never
        // hidden behind "Ready", and goes to the phone as the blocker; Start stays offered.
        var stuck = Screen(Pod + "," + Attributed + ",\"roles\":" + ReadyRoles + ",\"factory\":{" + Heard + ",\"ready\":true,\"blocking\":[]," + Task
            + ",\"progress\":{\"target\":2,\"credited\":1},\"unfinished\":["
            + "{\"collection\":\"cs_1\",\"reason\":\"the Factory refused its verdict on episode 1 (no confirmed recorder stop); sent again once the collection changes\"},"
            + "{\"collection\":null,\"reason\":\"a7: its record cannot be read\"}]}");
        Expect(stuck.Blocker.Tone == PodTone.Warning && stuck.Blocker.Headline == "A collection cannot finish yet"
            && stuck.Blocker.Who == "Floor manager" && !stuck.Blocker.Headline.Contains("verdict") && !stuck.StartBlocked, "unfinished, in words, the reason kept for the log: " + stuck.Blocker.Headline);
        Expect(stuck.Next.StartsWith("a collection to finish") && !stuck.Next.Contains("a7"), "unfinished next: " + stuck.Next);
        var none = Screen(Pod + "," + Attributed + ",\"roles\":" + ReadyRoles + ",\"factory\":{" + Heard + ",\"ready\":true,\"blocking\":[]," + Task + ",\"unfinished\":[]}");
        Expect(none.Blocker.Headline == "Ready to record", "nothing unfinished: ready: " + none.Blocker.Headline);

        // Start only when the Factory would take it (factory.start_refusal, from quest-rig).
        var full = Screen(Pod + "," + Attributed + ",\"roles\":" + ReadyRoles + ",\"factory\":{" + Heard + ",\"ready\":true,\"blocking\":[],\"admissible\":0," + Task
            + ",\"start_refusal\":{\"reason\":\"no_episodes\",\"checks\":[]}}");
        Expect(full.StartBlocked && full.Blocker.Headline == "No episodes left in this task" && full.Blocker.Who == "Floor manager"
            && full.Blocker.Action == "The floor manager adds episodes or assigns a new task in the Console.", "no episodes left: Start blocked: " + full.Blocker.Headline);
        var clock = Screen(Pod + "," + Attributed + ",\"roles\":" + ReadyRoles + ",\"factory\":{" + Heard + ",\"ready\":false,\"blocking\":["
            + "{\"id\":\"rig_check\",\"state\":\"fail\",\"category\":\"hardware\",\"summary\":\"rig check is stale\"},"
            + "{\"id\":\"clock\",\"state\":\"fail\",\"category\":\"clock\",\"summary\":\"clock is not synchronized\",\"remediation\":\"Enable network time\"}]," + Task
            + ",\"start_refusal\":{\"reason\":\"readiness\",\"checks\":[\"clock is not synchronized\"],\"notice\":{\"code\":\"readiness_clock\",\"who\":\"collector\",\"args\":{}}}}");
        Expect(clock.StartBlocked && clock.Blocker.Headline == "The headset's clock is not synced" && clock.Blocker.Who == "Collector",
            "a non-hardware check blocks Start, named with its owner: " + clock.Blocker.Headline);
        var clockBlocker = PodScreenBoard.StartBlocker(JObject.Parse("{" + Pod + "," + Attributed + ",\"factory\":{\"start_refusal\":{\"reason\":\"readiness\",\"checks\":[\"x\"]}}}"));
        Expect(clockBlocker is { } c && c.Headline == "A Factory check is failing" && c.Who == "Technician" && !c.Action.Contains("run Check"),
            "a readiness refusal with no notice (an older library) names no check and never says run Check");
        var stale = Screen(Pod + "," + Attributed + ",\"roles\":" + ReadyRoles + ",\"factory\":{" + Heard + ",\"ready\":false,\"blocking\":["
            + "{\"id\":\"rig_check\",\"state\":\"fail\",\"category\":\"hardware\",\"summary\":\"rig check is stale\"}]," + Task + ",\"start_refusal\":null}");
        Expect(!stale.StartBlocked, "a stale rig check alone leaves Start offered (the start checks first)");
        // Recording is never blocked by the absence of a person: no phone paired records unattributed.
        var unpaired = Screen(Pod + "," + Unattributed + ",\"roles\":" + ReadyRoles + ",\"factory\":{" + Heard + ",\"ready\":true,\"blocking\":[]," + Task + "}");
        Expect(!unpaired.StartBlocked && PodScreenBoard.StartBlocker(JObject.Parse("{" + Pod + "," + Unattributed + "}")) == null
            && unpaired.Blocker.Headline == "Ready to record", "no phone paired: Start offered, ready: " + unpaired.Blocker.Headline);
        var retired = Screen(Pod + ",\"floor\":{\"operator\":{\"signed_in\":false}},\"roles\":" + ReadyRoles + ",\"factory\":{" + Heard + ",\"ready\":true,\"blocking\":[]," + Task + "}");
        Expect(!retired.StartBlocked && retired.Blocker.Headline == "Ready to record", "an older library's floor.operator signed_in false blocks nothing");
        var operatorCheck = Screen(Pod + "," + Unattributed + ",\"roles\":" + ReadyRoles + ",\"factory\":{" + Heard + ",\"ready\":false,\"blocking\":["
            + "{\"id\":\"operator\",\"state\":\"fail\",\"category\":\"operator\",\"summary\":\"no collector\"}]," + Task + "}");
        Expect(!operatorCheck.StartBlocked && operatorCheck.Blocker.Headline == "Ready to record" && !operatorCheck.Next.Contains("signed in"),
            "a Factory operator check never blocks the headset, nor names a sign-in as next: " + operatorCheck.Next);
        var refused409 = Screen(Pod + "," + Attributed + ",\"roles\":" + ReadyRoles + ",\"factory\":{" + Heard + "," + Task + "}",
            new StartRefusal("rig_start_failed", "start_session: {\"code\":\"http_409\",\"message\":\"no assigned episodes remain available; unfinished attempts reserve demand\"}", T0));
        Expect(refused409.Blocker.Headline == "No episodes left in this task", "a 409 refusal in plain words: " + refused409.Blocker.Headline);
        var refused422 = Screen(Pod + "," + Attributed + ",\"roles\":" + ReadyRoles + ",\"factory\":{" + Heard + ",\"ready\":true,\"blocking\":[]," + Task + "}",
            new StartRefusal("rig_start_failed", "start_session: {\"code\":\"http_422\",\"message\":\"required readiness checks are not passing\"}", T0));
        Expect(refused422.Blocker.Headline == "A Factory check is failing" && !refused422.Blocker.Headline.Contains("start_session"), "a 422 refusal in plain words: " + refused422.Blocker.Headline);

        // No task.
        var noTask = Screen(Pod + "," + Attributed + ",\"roles\":" + ReadyRoles + ",\"factory\":{" + Heard + ",\"ready\":false,\"blocking\":[{\"id\":\"task_contract\",\"state\":\"fail\",\"summary\":\"no task\"}],\"task\":null,\"progress\":null,\"collection\":null}");
        Expect(noTask.Blocker.Headline == "No task is assigned to this pod" && noTask.Blocker.Who == "Floor manager", "no task -> the floor manager assigns one");
        Expect(noTask.Task == "none assigned" && noTask.Next == "a task assigned to this pod in the Console", "no task: collection says so");

        // Any build records (2026-11-22); a failing recorder_build check carries the Factory's own remediation, never a build approval.
        var unread = Screen(Pod + "," + Attributed + ",\"roles\":" + ReadyRoles + ",\"factory\":{" + Heard + ",\"ready\":false,\"blocking\":[{\"id\":\"recorder_build\",\"state\":\"fail\",\"summary\":\"Recorder fingerprint is not established\",\"remediation\":\"Run the hardware check to establish the original Quest identity.\"}]," + Task + "}");
        Expect(!unread.Blocker.Headline.Contains("approved") && !unread.Blocker.Action.Contains("approve"), "unread build: no approval to ask for: " + unread.Blocker.Headline);
        Expect(unread.Next == "Run the hardware check to establish the original Quest identity.", "unread build next: " + unread.Next);

        // The notice table no longer words a sign-in: no blocker ever asks for one.
        Expect(!System.Linq.Enumerable.Contains(OperatorNotices.Codes, "signed_out") && !System.Linq.Enumerable.Contains(OperatorNotices.Codes, "sign_in_pending"), "the notice table carries no sign-in notices");

        // The link down.
        var down = Screen(Pod + "," + Attributed + ",\"factory\":{\"link\":{\"ok\":false,\"heard_seconds_ago\":90,\"error\":\"heartbeat: connection refused\"}}");
        Expect(down.Blocker.Headline == "The Factory is not answering" && down.LinkTone == PodTone.Error && down.Link == "not heard for 90 s", "link down blocks, the error kept for the log: " + down.Link);

        // A missing device; a silent encoder from the start refusal; out of memory on a camera.
        var missingRoles = "[" + Role("left_hand_camera", "/dev/bus/usb/001/002", true, "1-1.1", "ready") + ","
            + Role("right_hand_camera", "", false, "", "unavailable", "no device fills this role") + "]";
        var missing = Screen(Pod + "," + Attributed + ",\"roles\":" + missingRoles + ",\"factory\":{" + Heard + "," + Task + "}");
        Expect(missing.Devices[1].Pill == "Missing" && missing.Devices[1].Hint == "Plug it into the hub, then press Check.", "missing camera: plug it in: " + missing.Devices[1].Hint);
        Expect(missing.Blocker.Headline == "The right wrist camera is not plugged in" && missing.Blocker.Tone == PodTone.Error && missing.Blocker.Button == "check", "missing device blocks: " + missing.Blocker.Headline);
        var silentRefusal = new StartRefusal("rig_start_failed",
            "left encoder: the encoder is attached but sends nothing during its two-second startup window (0 bytes received): is another app holding it? Replug it and choose OpenQuestCapture when Android asks", T0);
        var silent = Screen(Pod + "," + Attributed + ",\"roles\":" + ReadyRoles + ",\"factory\":{" + Heard + "," + Task + "}", silentRefusal);
        Expect(silent.Devices[2].Pill == "Failed" && silent.Devices[2].Hint.Contains("choose OpenQuestCapture"), "silent encoder: replug, choose OQC: " + silent.Devices[2].Hint);
        Expect(silent.Blocker.Headline == "Another app holds the left gripper sensor" && silent.Blocker.Action.Contains("choose OpenQuestCapture"), "silent encoder blocks with its fix: " + silent.Blocker.Headline);
        var oom = Screen(Pod + "," + Attributed + ",\"roles\":" + ReadyRoles + ",\"factory\":{" + Heard + "," + Task + "}",
            new StartRefusal("rig_start_failed", "the rig did not arm: right camera: streaming: Out of memory (os error 12)", T0));
        Expect(oom.Devices[1].Pill == "Failed" && oom.Devices[1].Hint == "Record again. If it happens twice in a row, restart the app and call a technician." && !oom.Devices[1].Value.Contains("os error"), "OOM camera: record again (camera_memory), no os error shown");
        Expect(oom.Devices[0].Pill == "Ready", "the other camera stays ready");
        Expect(PodScreenBoard.FailureFor(new[] { "x; right_camera: streaming: Out of memory (os error 12); y" }, "right", "camera") == "right_camera: streaming: Out of memory (os error 12)", "a receipt's clause by stream key");

        // USB permission prompt.
        var asking = Screen(Pod + "," + Attributed + ",\"usb\":{\"awaitingPermission\":[\"/dev/bus/usb/001/007\"]},\"roles\":" + ReadyRoles + ",\"factory\":{" + Heard + "}");
        Expect(asking.Blocker.Action.StartsWith("Choose OpenQuestCapture"), "USB permission -> choose OQC");

        // Live or silent while recording.
        var liveness = new StreamLiveness();
        string Recording(long frames, long samples) => Pod + "," + Attributed + ",\"episodeActive\":true,\"roles\":["
            + Role("left_hand_camera", "/dev/bus/usb/001/002", true, "1-1.1", "ready", "", "{\"source_fps\":30.0,\"source_frames\":" + frames + "}") + ","
            + Role("right_hand_camera", "/dev/bus/usb/001/003", true, "1-1.2", "ready", "", "{\"source_fps\":30.0,\"source_frames\":" + frames + "}") + ","
            + Role("left_hand_encoder", "/dev/bus/usb/001/004", true, "1-1.3", "ready", "", "{\"samples\":" + samples + "}") + ","
            + Role("right_hand_encoder", "/dev/bus/usb/001/005", true, "1-1.4", "ready", "", "{\"samples\":0}") + "],\"factory\":{" + Heard + "," + Task + "}";
        var first = Screen(Recording(30, 100), live: liveness, at: T0);
        Expect(first.Devices[0].Pill == "Live" && first.Devices[0].Value == "port 1-1.1 · 30 fps", "camera live: " + first.Devices[0].Value);
        Expect(first.Devices[3].Pill == "Starting" && first.Devices[3].Tone == PodTone.Warning, "an encoder at 0 samples is starting at first");
        var later = Screen(Recording(30, 100), live: liveness, at: T0.AddSeconds(4));
        Expect(later.Devices[0].Pill == "Silent" && later.Devices[2].Pill == "Silent", "counters that stopped rising go silent");
        Expect(later.Devices[3].Pill == "Silent" && later.Devices[3].Tone == PodTone.Error, "an encoder still at 0 samples goes silent");
        var again = Screen(Recording(60, 150), live: liveness, at: T0.AddSeconds(5));
        Expect(again.Devices[0].Pill == "Live" && again.Devices[2].Pill == "Live", "rising again is live");
        Expect(again.Blocker.Headline == "The right gripper sensor sends nothing", "a silent stream while recording blocks: " + again.Blocker.Headline);

        // The rig's controller-lost watch while recording (sx#4210): the collector hears it now.
        var lostLive = new StreamLiveness();
        var inHand = Screen(Recording(30, 100) + ",\"tracking\":{\"start_refusal\":null,\"controllers_lost\":[]}", live: lostLive, at: T0);
        Expect(inHand.Blocker.Tone == PodTone.Good && inHand.Blocker.Notice == null, "both controllers in hand while recording: recording");
        var lostLeft = Screen(Recording(31, 101) + ",\"tracking\":{\"start_refusal\":null,\"controllers_lost\":["
            + "{\"code\":\"controller_lost\",\"args\":{\"side\":\"left\"}}]}", live: lostLive, at: T0.AddSeconds(1));
        Expect(lostLeft.Blocker.Headline == "The left controller is out of hand" && lostLeft.Blocker.Tone == PodTone.Warning
            && lostLeft.Recording, "a controller lost while recording -> its notice, amber, recording goes on: " + lostLeft.Blocker.Headline);
        Expect(PodScreenBoard.Chip(lostLeft, PodReadiness.Ready, "Recording") == (PodTone.Warning, "Recording"),
            "the chip still says Recording (amber), never Not ready");
        Expect(PodScreenBoard.ControllerLost(JObject.Parse("{\"native\":{\"tracking\":{\"controllers_lost\":[{\"code\":\"controller_lost\",\"args\":{\"side\":\"right\"}}]}}}"))?.Code
            == "controller_lost", "controllers_lost is read from the native status when not lifted");

        // The take's 15 min ceiling (libsx_rig episode_length): amber a minute before it while
        // recording goes on; once it stopped itself, idle, why it stopped until the next start.
        var nearLimit = Screen(Recording(32, 102) + ",\"episode_length\":{\"episode_id\":\"e1\",\"recording\":true,\"phase\":\"near_limit\","
            + "\"notice\":{\"code\":\"recording_limit_near\",\"who\":\"collector\",\"args\":{\"minutes\":\"15\",\"left\":\"1\"}}}", live: lostLive, at: T0.AddSeconds(2));
        Expect(nearLimit.Blocker.Headline == "This take stops in 1 min, at the 15-minute limit" && nearLimit.Blocker.Tone == PodTone.Warning
            && nearLimit.Recording, "a minute before the limit: its notice, amber, recording goes on: " + nearLimit.Blocker.Headline);
        var stoppedAtLimit = Screen(Pod + "," + Attributed + ",\"roles\":" + ReadyRoles + ",\"factory\":{" + Heard + ",\"ready\":true,\"blocking\":[]," + Task + "}"
            + ",\"native\":{\"episode_length\":{\"episode_id\":\"e1\",\"recording\":false,\"phase\":\"reached\","
            + "\"notice\":{\"code\":\"recording_limit_reached\",\"who\":\"collector\",\"args\":{\"minutes\":\"15\"}}}}");
        Expect(stoppedAtLimit.Blocker.Headline == "This take reached the 15-minute limit and was stopped"
            && stoppedAtLimit.Blocker.Action == "Start a new one to continue." && stoppedAtLimit.Blocker.Tone == PodTone.Warning,
            "stopped at the limit, idle: why, and to start a new one: " + stoppedAtLimit.Blocker.Headline + " / " + stoppedAtLimit.Blocker.Action);
        Expect(!stoppedAtLimit.StartBlocked, "and Start stays offered");

        // No rig resolved yet: devices by kind, the missing ones named.
        var unresolved = Screen(Pod + "," + Attributed + ",\"devices\":[{\"kind\":\"camera\",\"port\":\"1-1.1\"},{\"kind\":\"encoder\",\"port\":\"1-1.3\"}],\"roles\":["
            + "{\"role\":\"left_hand_camera\",\"registered\":null}]");
        Expect(unresolved.Devices.Count == 4 && unresolved.Devices[0].Value == "port 1-1.1" && unresolved.Devices[1].Pill == "Missing", "unresolved: by kind, missing named");
        Expect(unresolved.DevicesNote.StartsWith("Roles are known"), "unresolved: says roles come with the check");

        // Episodes through the pipeline.
        JObject A(string stage, string outcome = "complete", string seal = "sealed", string integrity = "", string detail = "") =>
            JObject.Parse("{\"id\":\"e\",\"stage\":\"" + stage + "\",\"outcome\":\"" + outcome + "\",\"seal\":\"" + seal + "\",\"integrity\":\"" + integrity + "\",\"detail\":\"" + detail + "\"}");
        Expect(PodScreenBoard.EpisodeRow(A("recording", "", "")).Stage == "Recording", "recording");
        var sealing = PodScreenBoard.EpisodeRow(A("sealing", seal: "pending"));
        Expect(sealing.Reached == 1 && sealing.FailedAt == -1 && sealing.Stage == "Sealing", "sealing: recorded, sealing next");
        var refused = PodScreenBoard.EpisodeRow(A("refused", seal: "refused", detail: "gap"));
        Expect(refused.FailedAt == 1 && refused.Tone == PodTone.Error && refused.Stage == "Seal refused" && refused.Detail == "Last episode not kept: it did not pass the quality check",
            "seal refused at step 2, in words: " + refused.Detail);
        var reported = PodScreenBoard.EpisodeRow(A("reported"));
        Expect(reported.Reached == 3 && reported.Stage == "Reported", "reported, not yet in the workspace: " + reported.Stage);
        Expect(PodScreenBoard.EpisodeRow(A("reported", integrity: "pending")).Stage == "Awaiting review", "reported with its episode: awaiting review");
        var failedReported = PodScreenBoard.EpisodeRow(A("reported", "failed", "", detail: "right_camera: x"));
        Expect(failedReported.FailedAt == 0 && failedReported.Stage == "Failed" && !failedReported.Detail.Contains("right_camera"), "a failed recording stays failed once reported");
        Expect(PodScreenBoard.EpisodeRow(A("reviewed", integrity: "failed")).Stage == "Review failed", "review failed");
        Expect(PodScreenBoard.EpisodeRow(A("finalized")).Reached == 5, "finalized: five steps");
        var catalog = PodScreenBoard.EpisodeRow(A("registered"));
        Expect(catalog.Reached == 6 && catalog.Stage == "In Catalog", "registered: all six");

        // The newest attempt failed: its device warns with the fix, and the blocker says so.
        var afterOom = Screen(Pod + "," + Attributed + ",\"roles\":" + ReadyRoles + ",\"factory\":{" + Heard + "," + Task + "},\"attempts\":["
            + "{\"id\":\"20261020_120000\",\"stage\":\"failed\",\"outcome\":\"failed\",\"detail\":\"right_camera: streaming: Out of memory (os error 12)\"}]");
        Expect(afterOom.Devices[1].Pill == "Failed last episode" && afterOom.Devices[1].Tone == PodTone.Warning, "the last episode's failed device warns");
        Expect(afterOom.Blocker.Headline == "Recording failed" && afterOom.Blocker.Action == "Share this error code with a technician.",
            "last episode failed without a notice (an older library): the unknown code, never its words: " + afterOom.Blocker.Headline);
        var withNotice = Screen(Pod + "," + Attributed + ",\"roles\":" + ReadyRoles + ",\"factory\":{" + Heard + "," + Task + "},\"attempts\":["
            + "{\"id\":\"20261020_120000\",\"stage\":\"failed\",\"outcome\":\"failed\",\"detail\":\"right_camera: streaming: Out of memory (os error 12)\",\"notice\":{\"code\":\"usb_bandwidth\",\"args\":{}}}]");
        Expect(withNotice.Blocker.Headline == "The USB hub ran out of bandwidth" && withNotice.Blocker.Who == "Technician", "last episode failed -> its notice: " + withNotice.Blocker.Headline);
        Expect(withNotice.Episodes[0].Fix == "Put both wrist cameras on one powered hub, then press Check.", "the episode row carries the fix");

        // A gripper sensor the rig's readers saw fall silent in the last minute (OQC #44, Pod 19):
        // the rig's encoders.start_refusal blocks Start before any check, in the table's words.
        var silentSensor = Screen(Pod + "," + Attributed + ",\"roles\":" + ReadyRoles + ",\"factory\":{" + Heard + ",\"ready\":true,\"blocking\":[]," + Task + "}"
            + ",\"encoders\":{\"start_refusal\":{\"kind\":\"gripper_sensor_silent\",\"side\":\"right\",\"detail\":\"right encoder: the gripper sensor stopped sending 2 times in the last 60 s\","
            + "\"notice\":{\"code\":\"gripper_sensor_silent\",\"who\":\"collector\",\"args\":{\"side\":\"right\",\"part\":\"gripper_sensor\"}}},\"sides\":[]}");
        Expect(silentSensor.StartBlocked && silentSensor.Blocker.Headline == "The right gripper sensor keeps stopping" && silentSensor.Blocker.Who == "Collector"
            && silentSensor.Blocker.Action.StartsWith("Reseat the right gripper sensor's cable at the sensor and at the hub"),
            "a silent gripper sensor blocks Start with its cable named: " + silentSensor.Blocker.Headline + " / " + silentSensor.Blocker.Action);
        Expect(PodScreenBoard.EncoderRefusal(JObject.Parse("{\"native\":{\"encoders\":{\"start_refusal\":null,\"sides\":[]}}}")) == null
            && PodScreenBoard.EncoderRefusal(JObject.Parse("{\"native\":{}}")) == null, "no encoder refusal, or an older library: nothing");
        var noticeFromWords = PodScreenBoard.FailureNotice("right encoder: the gripper sensor stopped sending 2 times in the last 60 s", "right", "gripper_sensor");
        Expect(noticeFromWords?.Code == "gripper_sensor_silent" && noticeFromWords.Args["side"] == "right", "the check's words name the sensor's notice");
        var noticeMidTake = PodScreenBoard.FailureNotice("right encoder: encoder has emitted no sample for 500 ms [link: 18003 bulk reads]", "right", "gripper_sensor");
        Expect(noticeMidTake?.Code == "gripper_sensor_silent", "a take the stale law stopped names the same notice");
        Expect(PodScreenBoard.FailureNotice("right camera: no sample", "right", "wrist_camera") == null, "a camera's words do not");

        // Not set up: there is no Local only mode, so nothing records until it is (not_enrolled).
        var unenrolled = Screen("\"expected\":true,\"enrolled\":false,\"library\":\"present\",\"open\":true,\"roles\":" + ReadyRoles);
        Expect(unenrolled.StartBlocked && unenrolled.Blocker.Headline == "This headset is not set up as a pod"
            && unenrolled.Link == "not enrolled", "not set up: Start blocked with not_enrolled, no Factory link: " + unenrolled.Blocker.Headline);
        Expect(PodScreenBoard.StartBlocker(JObject.Parse("{\"expected\":false,\"enrolled\":false}")) is { } bare
            && bare.Headline == "This headset is not set up as a pod", "no rig and not set up: a start is refused too");
        Expect(PodScreenBoard.StartBlocker(JObject.Parse("{\"expected\":false,\"enrolled\":false,\"localOnly\":true}")) != null,
            "a status an earlier build wrote with localOnly still refuses the start of a headset not set up");

        Console.WriteLine(failures == 0 ? "ALL PASS" : failures + " FAILED");
        return failures == 0 ? 0 : 1;
    }
}
