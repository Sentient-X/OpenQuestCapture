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

    const string Pod = "\"expected\":true,\"enrolled\":true,\"localOnly\":false,\"library\":\"present\",\"open\":true,\"podId\":\"pod_q1\",\"apkBuildId\":\"sha256:0123456789abcdef0123\"";
    const string SignedIn = "\"floor\":{\"operator\":{\"signed_in\":true,\"display_name\":\"Asha\"}}";
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
        // Never throws on nonsense.
        var nothing = PodScreenBoard.Build("not json", null, false, new StreamLiveness(), T0);
        Expect(nothing.Blocker.Headline.Length > 0 && nothing.Devices.Count == 0, "unreadable status -> a blocker, no devices");

        // Ready: every role ready, signed in, a task, the link heard.
        var ready = Screen(Pod + "," + SignedIn + ",\"roles\":" + ReadyRoles + ",\"factory\":{" + Heard + ",\"ready\":true,\"blocking\":[]," + Task
            + ",\"progress\":{\"target\":2,\"credited\":1,\"awaiting_review\":1},\"collection\":null}");
        Expect(ready.Blocker.Headline == "Ready to record" && ready.Blocker.Tone == PodTone.Good, "ready: " + ready.Blocker.Headline);
        Expect(ready.Devices.Count == 4 && ready.Devices[0].Name == "Left camera" && ready.Devices[0].Pill == "Ready" && ready.Devices[0].Value == "port 1-1.1", "devices by role with port: " + ready.Devices[0].Value);
        Expect(ready.Task == "fold towel" && ready.Progress == "1 of 2 credited · 1 awaiting review", "progress: " + ready.Progress);
        Expect(ready.Next == "a review verdict on the recorded episode", "next: " + ready.Next);
        Expect(ready.Link == "OK · heard 3 s ago" && ready.LinkTone == PodTone.Good, "link: " + ready.Link);

        // No task.
        var noTask = Screen(Pod + "," + SignedIn + ",\"roles\":" + ReadyRoles + ",\"factory\":{" + Heard + ",\"ready\":false,\"blocking\":[{\"id\":\"task_contract\",\"state\":\"fail\",\"summary\":\"no task\"}],\"task\":null,\"progress\":null,\"collection\":null}");
        Expect(noTask.Blocker.Headline == "The Factory shows this pod no task" && noTask.Blocker.Action == "Assign a task to this pod in the Console", "no task -> assign in the Console");
        Expect(noTask.Task == "none assigned" && noTask.Next == "a task assigned to this pod in the Console", "no task: collection says so");

        // Recorder build trial not admitted.
        var trial = Screen(Pod + "," + SignedIn + ",\"roles\":" + ReadyRoles + ",\"factory\":{" + Heard + ",\"ready\":false,\"blocking\":[{\"id\":\"recorder_build\",\"state\":\"fail\",\"summary\":\"build not accepted\",\"remediation\":null}]," + Task + "}");
        Expect(trial.Blocker.Headline == "This build's trial is not admitted" && trial.Blocker.Action == "Admit the HIL trial for build 0123456789ab in the Console", "trial: " + trial.Blocker.Action);
        Expect(trial.Next == "the HIL trial admitted for build 0123456789ab", "trial next: " + trial.Next);

        // Signed out comes before the Factory's checks; a pending code names the code.
        var out_ = Screen(Pod + ",\"floor\":{\"operator\":{\"signed_in\":false}},\"roles\":" + ReadyRoles + ",\"factory\":{" + Heard + "}");
        Expect(out_.Blocker.Headline == "Collector signed out" && out_.Blocker.Action.StartsWith("Tap Sign in"), "signed out -> sign in");
        var pending = Screen(Pod + ",\"floor\":{\"operator\":{\"signed_in\":false,\"user_code\":\"WXYZ-1234\"}},\"factory\":{" + Heard + "}");
        Expect(pending.Blocker.Action == "Approve code WXYZ-1234 on your phone", "pending sign-in names the code");

        // The link down.
        var down = Screen(Pod + "," + SignedIn + ",\"factory\":{\"link\":{\"ok\":false,\"heard_seconds_ago\":90,\"error\":\"heartbeat: connection refused\"}}");
        Expect(down.Blocker.Headline == "The Factory does not hear this headset" && down.LinkTone == PodTone.Error, "link down blocks: " + down.Link);

        // A missing device; a silent encoder from the start refusal; out of memory on a camera.
        var missingRoles = "[" + Role("left_hand_camera", "/dev/bus/usb/001/002", true, "1-1.1", "ready") + ","
            + Role("right_hand_camera", "", false, "", "unavailable", "no device fills this role") + "]";
        var missing = Screen(Pod + "," + SignedIn + ",\"roles\":" + missingRoles + ",\"factory\":{" + Heard + "," + Task + "}");
        Expect(missing.Devices[1].Pill == "Missing" && missing.Devices[1].Hint == "Plug it into the hub", "missing camera: plug it in");
        Expect(missing.Blocker.Headline == "Right camera: missing" && missing.Blocker.Tone == PodTone.Error, "missing device blocks: " + missing.Blocker.Headline);
        var silentRefusal = new StartRefusal("rig_start_failed",
            "left encoder: the encoder is attached but sends nothing during its two-second startup window (0 bytes received): is another app holding it? Replug it and choose OpenQuestCapture when Android asks", T0);
        var silent = Screen(Pod + "," + SignedIn + ",\"roles\":" + ReadyRoles + ",\"factory\":{" + Heard + "," + Task + "}", silentRefusal);
        Expect(silent.Devices[2].Pill == "Failed" && silent.Devices[2].Hint.Contains("choose OpenQuestCapture"), "silent encoder: replug, choose OQC: " + silent.Devices[2].Hint);
        Expect(silent.Blocker.Headline == "Left encoder: failed" && silent.Blocker.Action.Contains("choose OpenQuestCapture"), "silent encoder blocks with its fix");
        var oom = Screen(Pod + "," + SignedIn + ",\"roles\":" + ReadyRoles + ",\"factory\":{" + Heard + "," + Task + "}",
            new StartRefusal("rig_start_failed", "the rig did not arm: right camera: streaming: Out of memory (os error 12)", T0));
        Expect(oom.Devices[1].Pill == "Failed" && oom.Devices[1].Hint == "Not enough USB bandwidth: put both cameras on one hub", "OOM camera: one hub");
        Expect(oom.Devices[0].Pill == "Ready", "the other camera stays ready");
        Expect(PodScreenBoard.FailureFor(new[] { "x; right_camera: streaming: Out of memory (os error 12); y" }, "right", "camera") == "right_camera: streaming: Out of memory (os error 12)", "a receipt's clause by stream key");

        // USB permission prompt.
        var asking = Screen(Pod + "," + SignedIn + ",\"usb\":{\"awaitingPermission\":[\"/dev/bus/usb/001/007\"]},\"roles\":" + ReadyRoles + ",\"factory\":{" + Heard + "}");
        Expect(asking.Blocker.Action.StartsWith("Choose OpenQuestCapture"), "USB permission -> choose OQC");

        // Live or silent while recording.
        var liveness = new StreamLiveness();
        string Recording(long frames, long samples) => Pod + "," + SignedIn + ",\"episodeActive\":true,\"roles\":["
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
        Expect(again.Blocker.Headline == "Right encoder: silent", "a silent stream while recording blocks: " + again.Blocker.Headline);

        // No rig resolved yet: devices by kind, the missing ones named.
        var unresolved = Screen(Pod + "," + SignedIn + ",\"devices\":[{\"kind\":\"camera\",\"port\":\"1-1.1\"},{\"kind\":\"encoder\",\"port\":\"1-1.3\"}],\"roles\":["
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
        Expect(refused.FailedAt == 1 && refused.Tone == PodTone.Error && refused.Detail == "gap", "seal refused at step 2");
        var reported = PodScreenBoard.EpisodeRow(A("reported"));
        Expect(reported.Reached == 3 && reported.Stage == "Awaiting review", "reported: awaiting review");
        var failedReported = PodScreenBoard.EpisodeRow(A("reported", "failed", "", detail: "right_camera: x"));
        Expect(failedReported.FailedAt == 0 && failedReported.Stage == "Failed, reported", "a failed recording stays failed once reported");
        Expect(PodScreenBoard.EpisodeRow(A("reviewed", integrity: "failed")).Stage == "Review failed", "review failed");
        Expect(PodScreenBoard.EpisodeRow(A("finalized")).Reached == 5, "finalized: five steps");
        var catalog = PodScreenBoard.EpisodeRow(A("registered"));
        Expect(catalog.Reached == 6 && catalog.Stage == "In Catalog", "registered: all six");

        // The newest attempt failed: its device warns with the fix, and the blocker says so.
        var afterOom = Screen(Pod + "," + SignedIn + ",\"roles\":" + ReadyRoles + ",\"factory\":{" + Heard + "," + Task + "},\"attempts\":["
            + "{\"id\":\"20261020_120000\",\"stage\":\"failed\",\"outcome\":\"failed\",\"detail\":\"right_camera: streaming: Out of memory (os error 12)\"}]");
        Expect(afterOom.Devices[1].Pill == "Failed last episode" && afterOom.Devices[1].Tone == PodTone.Warning, "the last episode's failed device warns");
        Expect(afterOom.Blocker.Headline.StartsWith("Last episode failed") && afterOom.Blocker.Action == "Not enough USB bandwidth: put both cameras on one hub", "last episode failed -> its fix");
        Expect(afterOom.Episodes[0].Detail.EndsWith("put both cameras on one hub"), "the episode row carries the fix");

        // Local only.
        var local = Screen("\"expected\":true,\"enrolled\":false,\"localOnly\":true,\"open\":true,\"roles\":" + ReadyRoles);
        Expect(local.Blocker.Headline == "Ready (Local only)" && local.Link == "not used (Local only)", "local only: ready, no Factory");

        Console.WriteLine(failures == 0 ? "ALL PASS" : failures + " FAILED");
        return failures == 0 ? 0 : 1;
    }
}
