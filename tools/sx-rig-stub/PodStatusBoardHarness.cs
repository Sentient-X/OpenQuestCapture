// Host check of PodStatusBoard (the recordings menu's Pod status block) and PodStartLog:
// the collector (the paired phone's person, or unattributed), the task line, refusals in plain words, a start
// that succeeds clears the refusal. Run by test_binding.sh; not part of the Unity project.
using System;
using RealityLog.Rig;

static class PodStatusBoardHarness
{
    static int failures;
    static void Expect(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) failures++; }

    const string Base = "\"expected\":true,\"enrolled\":true,\"localOnly\":false,\"library\":\"present\",\"open\":true,\"podId\":\"pod_q1\"";

    static PodStatusLines Lines(string body, StartRefusal? start = null, bool succeeded = false) =>
        PodStatusBoard.Lines("{" + body + "}", start, succeeded);

    static int Main()
    {
        // The operator's words: the shipped table (test_binding.sh names it).
        RealityLog.Rig.OperatorNotices.Load(System.IO.File.ReadAllText(Environment.GetEnvironmentVariable("SX_OPERATOR_NOTICES")!));
        // Nothing parses -> safe defaults, never a throw.
        var empty = PodStatusBoard.Lines("not json", null, false);
        Expect(empty.Pod == "Pod: not enrolled · Local only ON" && empty.Rig.StartsWith("Rig: not expected"), "unreadable status -> not enrolled, no rig");
        var local = PodStatusBoard.Lines("{\"expected\":false,\"enrolled\":false,\"localOnly\":true,\"floor\":null}", null, false);
        Expect(local.Collector == "Collector: enroll this headset first" && local.Task == "Task: none (not enrolled)", "not enrolled: no collector, no task");

        Expect(Lines(Base + ",\"floor\":null").Pod == "Pod: pod_q1 · Local only OFF", "pod line names the pod and Local only");
        Expect(Lines("\"enrolled\":true,\"localOnly\":true,\"podId\":\"pod_q1\"").Collector == "Collector: none (Local only)", "Local only names no collector");

        // The collector: the paired phone's person (floor.collector), or unattributed. Never a sign-in.
        var attributed = Lines(Base + ",\"floor\":{\"collector\":{\"id\":\"usr_1\",\"name\":\"Asha\"},\"collection\":null}");
        Expect(attributed.Collector == "Collector: Recording for Asha", "attributed: names the phone's person: " + attributed.Collector);
        var unattributed = Lines(Base + ",\"floor\":{\"collector\":null,\"collection\":null}");
        Expect(unattributed.Collector == "Collector: " + PodStatusBoard.Unattributed, "no phone paired: unattributed: " + unattributed.Collector);
        Expect(Lines(Base + ",\"floor\":{\"collector\":{\"id\":\"usr_1\",\"name\":\"\"}}").Collector == "Collector: Recording for usr_1", "no name: the collector's id");
        Expect(Lines(Base + ",\"floor\":{\"operator\":{\"signed_in\":true,\"display_name\":\"Asha\"}}").Collector == "Collector: " + PodStatusBoard.Unattributed,
            "the retired floor.operator is never read");
        Expect(!unattributed.Collector.Contains("Sign") && !unattributed.Collector.Contains("sign"), "nothing on the card asks anyone to sign in");
        Expect(Lines(Base + ",\"floor\":null,\"native\":{\"floor_error\":\"no route to host\"}").Collector == "Collector: waiting for the Factory", "no floor: waiting; the floor error stays in the log");

        // The setup line: a step's message for a while, then the current state.
        Expect(PodStatusBoard.SetupStatus("Enrolled as pod_q1", 5, false, true, false) == "Enrolled as pod_q1", "a fresh message shows");
        Expect(PodStatusBoard.SetupStatus("Enrolled as pod_q1", 61, false, true, false).StartsWith("Enrolled as a pod. Local only is off"), "an old message gives way to the state");
        Expect(PodStatusBoard.SetupStatus(null, 0, false, true, false).StartsWith("Enrolled as a pod"), "a cleared message shows the state at once");
        Expect(PodStatusBoard.SetupStatus("Enrolling...", 600, true, false, true) == "Enrolling...", "a running step keeps its message");

        // Task.
        var task = Lines(Base + ",\"floor\":{\"collector\":null,\"collection\":{\"id\":\"c1\",\"status\":\"in_progress\",\"episodes\":[{\"id\":\"e1\"},{\"id\":\"e2\"}]}}");
        Expect(task.Task == "Task: collection in progress, 2 episodes", "task line: " + task.Task);
        Expect(Lines(Base + ",\"floor\":{\"collector\":null,\"collection\":null}").Task.StartsWith("Task: no collection open"), "no collection open");
        Expect(Lines(Base + ",\"floor\":null").Task.StartsWith("Task: unknown"), "no floor: task unknown");

        // Rig.
        Expect(Lines(Base + ",\"lastCheck\":{\"ready\":true,\"problems\":[],\"devices\":[{},{},{},{}]}").Rig == "Rig: ready · 4 devices", "ready rig with its devices");
        var notReady = Lines(Base + ",\"lastCheck\":{\"ready\":false,\"problems\":[\"left_camera: no device on usb-port:1-1.1\"]},\"native\":{\"devices\":[{},{},{}]}");
        Expect(notReady.Rig == "Rig: not ready: left_camera: no device on usb-port:1-1.1 · 3 devices", "not ready with problems: " + notReady.Rig);
        Expect(Lines(Base + ",\"lastCheck\":null").Rig.StartsWith("Rig: not checked yet"), "unchecked rig");
        Expect(Lines("\"expected\":true,\"open\":false,\"openError\":{\"detail\":\"no rig.json\"}").Rig.StartsWith("Rig: not open: no rig.json"), "rig not open, with the reason");

        // Refusals in plain words.
        Expect(PodStatusBoard.Explain("rig_start_failed", "start_session: http_422 required readiness checks are not passing") == "A Factory check is failing: Call a technician. The Console names the check.", "readiness refusal");
        Expect(PodStatusBoard.Explain("rig_start_failed", "start_session: {\"code\":\"http_409\",\"message\":\"no assigned episodes remain available; unfinished attempts reserve demand\"}")
            == "No episodes left in this task: The floor manager adds episodes or assigns a new task in the Console.", "no-episodes refusal, never its JSON");
        Expect(PodStatusBoard.Explain("rig_start_failed", "start_session: http_404 pod has no assigned task") == "No task is assigned to this pod: The floor manager assigns a task to this pod in the Console.", "no task refusal");
        Expect(!PodStatusBoard.Explain("collector_signed_out", "x").Contains("signed in"), "the retired collector_signed_out has no sign-in words");
        Expect(PodStatusBoard.Explain("rig_check_failed", "left camera missing") == "The rig's devices did not pass the check: Press Check and fix the device it names.", "check refusal");
        Expect(PodStatusBoard.Explain("storage_full", "Insufficient storage") == "Not enough space: GB free, GB needed for the next episode: Keep the headset on Wi-Fi so uploads finish, or delete exported recordings.", "storage refusal");

        // The start log: a refusal with its time, cleared by a successful start.
        var log = new PodStartLog();
        log.Record(503, "{\"error\":\"rig_not_ready\",\"reason\":\"rig_start_failed\",\"message\":\"start_session: http_422 required readiness checks are not passing\",\"rig\":null}",
            new DateTime(2026, 10, 19, 14, 3, 7));
        var refused = Lines(Base, log.Last, log.Succeeded);
        Expect(refused.LastRefusal == "Last refusal (14:03:07): " + "A Factory check is failing: Call a technician. The Console names the check.", "refusal with time: " + refused.LastRefusal);
        log.Record(507, "{\"error\":\"rig_not_ready\",\"reason\":\"storage_full\",\"message\":\"Insufficient storage\"}", new DateTime(2026, 10, 19, 14, 4, 0));
        var storageHeadline = RealityLog.Rig.OperatorNotices.Render(OperatorNotice.Of("storage_low")).Headline;
        Expect(Lines(Base, log.Last, log.Succeeded).LastRefusal.StartsWith("Last refusal (14:04:00): " + storageHeadline + ": "), "the newest refusal wins");
        log.Record(200, "{\"status\":\"recording\"}", new DateTime(2026, 10, 19, 14, 5, 0));
        Expect(log.Last == null && Lines(Base + ",\"lastRefusal\":{\"code\":\"rig_check_failed\",\"message\":\"old\"}", log.Last, log.Succeeded).LastRefusal == "Last refusal: none", "a successful start clears the refusal");
        Expect(Lines(Base + ",\"lastRefusal\":{\"code\":\"storage_full\",\"message\":\"Insufficient storage\"}").LastRefusal.StartsWith("Last refusal: " + storageHeadline + ": "), "the rig's own refusal shows (no time) before any start here");
        var bare = new PodStartLog();
        bare.Record(500, "not json", DateTime.Now);
        Expect(bare.Last?.Code == "http_500", "an unreadable error body is its status code");

        // Last episode: recording, newest verdict, upload.
        Expect(Lines(Base + ",\"native\":{\"episode\":{\"episode_id\":\"20261019_140500\"}}").LastEpisode == "Episode: recording 20261019_140500", "recording episode");
        var sealedEp = Lines(Base + ",\"seals\":{\"20261019_120000\":{\"state\":\"refused\",\"detail\":\"gap\"},\"20261019_130000\":{\"state\":\"sealed\",\"size_bytes\":47395635}},\"uplink\":{\"state\":\"idle\",\"log\":[{\"receipt\":\"s1\",\"result\":{\"Ok\":\"acknowledged\"}}]}");
        Expect(sealedEp.LastEpisode == "Last episode 20261019_130000: sealed 45.2 MB · upload: ok", "newest seal and upload: " + sealedEp.LastEpisode);
        var uploadFailed = Lines(Base + ",\"seals\":{\"e\":{\"state\":\"pending\",\"detail\":\"waiting for the clock\"}},\"uplink\":{\"state\":\"idle\",\"log\":[{\"artifact\":\"a1\",\"result\":{\"Err\":\"lease expired\"}}]}");
        Expect(uploadFailed.LastEpisode == "Last episode e: pending: waiting for the clock · upload failed: lease expired", "pending seal and failed upload: " + uploadFailed.LastEpisode);
        Expect(Lines(Base + ",\"seals\":{},\"uplink\":null").LastEpisode == "Last episode: none sealed yet", "no episodes");

        // The card: its chip and the label/value split.
        Expect(empty.Readiness == PodReadiness.LocalOnly && empty.ReadinessText == "Local only" && empty.PodName == "", "unreadable status -> Local only chip, no pod name");
        Expect(PodStatusBoard.Lines("{\"enrolled\":false,\"localOnly\":false}", null, false).Readiness == PodReadiness.NotReady, "not enrolled -> not ready");
        var ready = Lines(Base + ",\"floor\":{\"collector\":{\"id\":\"usr_1\",\"name\":\"Asha\"}},\"lastCheck\":{\"ready\":true,\"problems\":[]}");
        Expect(ready.Readiness == PodReadiness.Ready && ready.PodName == "pod_q1", "attributed with the rig checked ready -> Ready");
        var readyUnattributed = Lines(Base + ",\"floor\":{\"collector\":null},\"lastCheck\":{\"ready\":true,\"problems\":[]}");
        Expect(readyUnattributed.Readiness == PodReadiness.Ready, "no phone paired, the rig checked ready -> Ready (records unattributed)");
        Expect(Lines("\"expected\":false,\"enrolled\":true,\"localOnly\":false,\"floor\":{\"collector\":null}").Readiness == PodReadiness.Ready, "no rig expected: the Factory answering is enough");
        Expect(Lines(Base + ",\"floor\":null,\"lastCheck\":{\"ready\":true,\"problems\":[]}").Readiness == PodReadiness.NotReady, "the Factory not answering yet -> not ready");
        Expect(Lines(Base + ",\"floor\":{\"collector\":null},\"lastCheck\":null").Readiness == PodReadiness.NotReady, "an unchecked rig is not ready");
        Expect(Lines(Base + ",\"lastRefusal\":{\"code\":\"rig_check_failed\",\"message\":\"x\"}").Readiness == PodReadiness.Refused, "a standing refusal -> Refused");
        Expect(Lines("\"expected\":true,\"enrolled\":true,\"localOnly\":false,\"library\":\"missing\"").ReadinessText == "Rig error", "a missing rig library -> Rig error");
        Expect(PodStatusBoard.RecordingFor("Asha") == "Recording for Asha" && PodStatusBoard.RecordingFor("") == PodStatusBoard.Unattributed
            && PodStatusBoard.RecordingFor(null) == PodStatusBoard.Unattributed, "who the pod records for, or unattributed");
        Expect(PodStatusBoard.Field("Rig: not ready: left_camera: gone · 3 devices") == ("Rig", "not ready: left_camera: gone · 3 devices"), "field split at the first colon");
        Expect(PodStatusBoard.Field("Last refusal (14:03:07): No task") == ("Last refusal", "14:03:07 · No task"), "field split moves the time to the value");
        Expect(PodStatusBoard.Field(sealedEp.LastEpisode) == ("Last episode", "20261019_130000 · sealed 45.2 MB · upload: ok"), "field split moves the episode name to the value");
        Expect(PodStatusBoard.Field("Last episode: none sealed yet") == ("Last episode", "none sealed yet"), "no episode yet");
        Expect(PodStatusBoard.SetupMessage("Enrolling...", 600, true) == "Enrolling..." && PodStatusBoard.SetupMessage("Scan cancelled.", 61, false) == null, "a step message lasts while busy or a minute");

        Console.WriteLine(failures == 0 ? "ALL PASS" : failures + " FAILED");
        return failures;
    }
}
