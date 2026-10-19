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
        Expect(Lines(Base + ",\"floor\":null,\"native\":{\"floor_error\":\"no route to host\"}").Collector == "Collector: waiting for the Factory (no route to host)", "no floor: waiting, with the floor error");

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
        Expect(PodStatusBoard.Explain("rig_start_failed", "start_session: http_422 required readiness checks are not passing") == "The Factory's readiness checks are not passing", "readiness refusal");
        Expect(PodStatusBoard.Explain("rig_start_failed", "start_session: http_404 pod has no assigned task") == "No task assigned to this pod", "no task refusal");
        Expect(PodStatusBoard.Explain("collector_signed_out", "the collector signed out; POST /api/sign-in") == "Collector signed out: sign in first", "signed out refusal");
        Expect(PodStatusBoard.Explain("rig_check_failed", "left camera missing") == "The rig's device check failed: left camera missing", "check refusal");
        Expect(PodStatusBoard.Explain("storage_full", "Insufficient storage") == "Storage is too low to record", "storage refusal");

        // The start log: a refusal with its time, cleared by a successful start.
        var log = new PodStartLog();
        log.Record(503, "{\"error\":\"rig_not_ready\",\"reason\":\"rig_start_failed\",\"message\":\"start_session: http_422 required readiness checks are not passing\",\"rig\":null}",
            new DateTime(2026, 10, 19, 14, 3, 7));
        var refused = Lines(Base, log.Last, log.Succeeded);
        Expect(refused.LastRefusal == "Last refusal (14:03:07): The Factory's readiness checks are not passing", "refusal with time: " + refused.LastRefusal);
        log.Record(409, "{\"error\":\"rig_not_ready\",\"reason\":\"collector_signed_out\",\"message\":\"x\"}", new DateTime(2026, 10, 19, 14, 4, 0));
        Expect(Lines(Base, log.Last, log.Succeeded).LastRefusal == "Last refusal (14:04:00): Collector signed out: sign in first", "the newest refusal wins");
        log.Record(200, "{\"status\":\"recording\"}", new DateTime(2026, 10, 19, 14, 5, 0));
        Expect(log.Last == null && Lines(Base + ",\"lastRefusal\":{\"code\":\"rig_check_failed\",\"message\":\"old\"}", log.Last, log.Succeeded).LastRefusal == "Last refusal: none", "a successful start clears the refusal");
        Expect(Lines(Base + ",\"lastRefusal\":{\"code\":\"collector_signed_out\",\"message\":\"x\"}").LastRefusal == "Last refusal: Collector signed out: sign in first", "the rig's own refusal shows (no time) before any start here");
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

        Console.WriteLine(failures == 0 ? "ALL PASS" : failures + " FAILED");
        return failures;
    }
}
