// Host check of PodStatusBoard (the recordings menu's Pod status block) and PodStartLog:
// a sign-in code shows only while pending, the task line, refusals in plain words, a start
// that succeeds clears the refusal. Run by test_binding.sh; not part of the Unity project.
using System;
using RealityLog.Rig;

static class PodStatusBoardHarness
{
    static int failures;
    static void Expect(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) failures++; }

    const string Base = "\"expected\":true,\"enrolled\":true,\"localOnly\":false,\"library\":\"present\",\"open\":true,\"podId\":\"pod_q1\"";

    static PodStatusLines Lines(string body, StartRefusal? start = null, bool succeeded = false, bool asking = false) =>
        PodStatusBoard.Lines("{" + body + "}", start, succeeded, asking);

    static int Main()
    {
        // The operator's words: the shipped table (test_binding.sh names it).
        RealityLog.Rig.OperatorNotices.Load(System.IO.File.ReadAllText(Environment.GetEnvironmentVariable("SX_OPERATOR_NOTICES")!));
        // Nothing parses -> safe defaults, never a throw.
        var empty = PodStatusBoard.Lines("not json", null, false, false);
        Expect(empty.Pod == "Pod: not enrolled · Local only ON" && empty.Rig.StartsWith("Rig: not expected"), "unreadable status -> not enrolled, no rig");
        var local = PodStatusBoard.Lines("{\"expected\":false,\"enrolled\":false,\"localOnly\":true,\"floor\":null}", null, false, false);
        Expect(local.Collector == "Collector: enroll this headset first" && local.Task == "Task: none (not enrolled)", "not enrolled: no collector, no task");

        Expect(Lines(Base + ",\"floor\":null").Pod == "Pod: pod_q1 · Local only OFF", "pod line names the pod and Local only");
        Expect(Lines("\"enrolled\":true,\"localOnly\":true,\"podId\":\"pod_q1\"").Collector == "Collector: not needed (Local only)", "Local only needs no collector");

        // Sign-in: pending shows the code; signed in hides it; expired hides it.
        var pending = Lines(Base + ",\"floor\":{\"operator\":{\"signed_in\":false,\"user_code\":\"WXYZ-1234\",\"verification_uri_complete\":\"https://auth/device?code=WXYZ-1234\"},\"collection\":null,\"message\":\"\"}");
        Expect(pending.Collector.Contains("WXYZ-1234") && pending.Collector.Contains("pending"), "pending sign-in shows the code: " + pending.Collector);
        var signedIn = Lines(Base + ",\"floor\":{\"operator\":{\"signed_in\":true,\"display_name\":\"Asha\"},\"collection\":null,\"message\":\"\"}");
        Expect(signedIn.Collector == "Collector: signed in as Asha" && !signedIn.Collector.Contains("WXYZ"), "signed in hides the code and names the collector");
        var expired = Lines(Base + ",\"floor\":{\"operator\":{\"signed_in\":false},\"message\":\"the sign-in code expired\"}");
        Expect(expired.Collector.StartsWith("Collector: signed out (the sign-in code expired)") && !expired.Collector.Contains("WXYZ"), "expired code: signed out with the reason");
        Expect(Lines(Base + ",\"floor\":{\"operator\":{\"signed_in\":false,\"user_code\":\"WXYZ-1234\"}}", asking: true).Collector.Contains("asking"), "asking for a code says so");
        Expect(Lines(Base + ",\"floor\":null,\"native\":{\"floor_error\":\"no route to host\"}").Collector == "Collector: waiting for the Factory", "no floor: waiting; the floor error stays in the log");

        // The setup line: a step's message for a while, then the current state.
        Expect(PodStatusBoard.SetupStatus("Sign-in code ready", 5, false, true, false) == "Sign-in code ready", "a fresh message shows");
        Expect(PodStatusBoard.SetupStatus("Sign-in code ready", 61, false, true, false).StartsWith("Enrolled as a pod. Local only is off"), "an old message gives way to the state");
        Expect(PodStatusBoard.SetupStatus(null, 0, false, true, false).StartsWith("Enrolled as a pod"), "a cleared message (signed in) shows the state at once");
        Expect(PodStatusBoard.SetupStatus("Enrolling...", 600, true, false, true) == "Enrolling...", "a running step keeps its message");

        // Task.
        var task = Lines(Base + ",\"floor\":{\"operator\":{\"signed_in\":true},\"collection\":{\"id\":\"c1\",\"status\":\"in_progress\",\"episodes\":[{\"id\":\"e1\"},{\"id\":\"e2\"}]}}");
        Expect(task.Task == "Task: collection in progress, 2 episodes", "task line: " + task.Task);
        Expect(Lines(Base + ",\"floor\":{\"operator\":{\"signed_in\":true},\"collection\":null}").Task.StartsWith("Task: no collection open"), "no collection open");
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
        Expect(PodStatusBoard.Explain("collector_signed_out", "the collector signed out; POST /api/sign-in") == "Nobody is signed in: Tap Sign in and approve the code on your phone.", "signed out refusal");
        Expect(PodStatusBoard.Explain("rig_check_failed", "left camera missing") == "The rig's devices did not pass the check: Press Check and fix the device it names.", "check refusal");
        Expect(PodStatusBoard.Explain("storage_full", "Insufficient storage") == "Not enough space: GB free, GB needed: Keep the headset on Wi-Fi so uploads finish, or delete exported recordings.", "storage refusal");

        // The start log: a refusal with its time, cleared by a successful start.
        var log = new PodStartLog();
        log.Record(503, "{\"error\":\"rig_not_ready\",\"reason\":\"rig_start_failed\",\"message\":\"start_session: http_422 required readiness checks are not passing\",\"rig\":null}",
            new DateTime(2026, 10, 19, 14, 3, 7));
        var refused = Lines(Base, log.Last, log.Succeeded);
        Expect(refused.LastRefusal == "Last refusal (14:03:07): " + "A Factory check is failing: Call a technician. The Console names the check.", "refusal with time: " + refused.LastRefusal);
        log.Record(409, "{\"error\":\"rig_not_ready\",\"reason\":\"collector_signed_out\",\"message\":\"x\"}", new DateTime(2026, 10, 19, 14, 4, 0));
        Expect(Lines(Base, log.Last, log.Succeeded).LastRefusal == "Last refusal (14:04:00): " + "Nobody is signed in: Tap Sign in and approve the code on your phone.", "the newest refusal wins");
        log.Record(200, "{\"status\":\"recording\"}", new DateTime(2026, 10, 19, 14, 5, 0));
        Expect(log.Last == null && Lines(Base + ",\"lastRefusal\":{\"code\":\"rig_check_failed\",\"message\":\"old\"}", log.Last, log.Succeeded).LastRefusal == "Last refusal: none", "a successful start clears the refusal");
        Expect(Lines(Base + ",\"lastRefusal\":{\"code\":\"collector_signed_out\",\"message\":\"x\"}").LastRefusal == "Last refusal: " + "Nobody is signed in: Tap Sign in and approve the code on your phone.", "the rig's own refusal shows (no time) before any start here");
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

        // The card: its chip, the pending code and the label/value split.
        Expect(empty.Readiness == PodReadiness.LocalOnly && empty.ReadinessText == "Local only" && empty.PodName == "", "unreadable status -> Local only chip, no pod name");
        Expect(PodStatusBoard.Lines("{\"enrolled\":false,\"localOnly\":false}", null, false, false).Readiness == PodReadiness.NotReady, "not enrolled -> not ready");
        var ready = Lines(Base + ",\"floor\":{\"operator\":{\"signed_in\":true}},\"lastCheck\":{\"ready\":true,\"problems\":[]}");
        Expect(ready.Readiness == PodReadiness.Ready && ready.PodName == "pod_q1", "signed in with the rig checked ready -> Ready");
        Expect(Lines("\"expected\":false,\"enrolled\":true,\"localOnly\":false,\"floor\":{\"operator\":{\"signed_in\":true}}").Readiness == PodReadiness.Ready, "no rig expected: a signed-in collector is enough");
        Expect(Lines(Base + ",\"floor\":{\"operator\":{\"signed_in\":true}},\"lastCheck\":null").Readiness == PodReadiness.NotReady, "an unchecked rig is not ready");
        Expect(Lines(Base + ",\"lastRefusal\":{\"code\":\"collector_signed_out\",\"message\":\"x\"}").Readiness == PodReadiness.Refused, "a standing refusal -> Refused");
        Expect(Lines("\"expected\":true,\"enrolled\":true,\"localOnly\":false,\"library\":\"missing\"").ReadinessText == "Rig error", "a missing rig library -> Rig error");
        Expect(pending.SignInCode == "WXYZ-1234" && pending.SignInAddress == "https://auth/device?code=WXYZ-1234", "pending sign-in carries the code and address");
        Expect(signedIn.SignInCode == "" && expired.SignInCode == "", "no code once signed in or expired");
        Expect(Lines(Base + ",\"floor\":{\"operator\":{\"signed_in\":false,\"user_code\":\"WXYZ-1234\"}}", asking: true).SignInCode == "", "no code while asking for a new one");
        Expect(PodStatusBoard.Field("Rig: not ready: left_camera: gone · 3 devices") == ("Rig", "not ready: left_camera: gone · 3 devices"), "field split at the first colon");
        Expect(PodStatusBoard.Field("Last refusal (14:03:07): No task") == ("Last refusal", "14:03:07 · No task"), "field split moves the time to the value");
        Expect(PodStatusBoard.Field(sealedEp.LastEpisode) == ("Last episode", "20261019_130000 · sealed 45.2 MB · upload: ok"), "field split moves the episode name to the value");
        Expect(PodStatusBoard.Field("Last episode: none sealed yet") == ("Last episode", "none sealed yet"), "no episode yet");
        Expect(PodStatusBoard.SetupMessage("Enrolling...", 600, true) == "Enrolling..." && PodStatusBoard.SetupMessage("Scan cancelled.", 61, false) == null, "a step message lasts while busy or a minute");

        Console.WriteLine(failures == 0 ? "ALL PASS" : failures + " FAILED");
        return failures;
    }
}
