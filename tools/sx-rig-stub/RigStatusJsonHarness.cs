// Host check of Assets/RealityLog/Scripts/Runtime/Rig/RigStatusJson.cs: local mode's
// "floor": null and other odd shapes never throw, and only a ready check passes.
// Run by test_binding.sh; not part of the Unity project.
using System;
using Newtonsoft.Json.Linq;
using RealityLog.Rig;

static class RigStatusJsonHarness
{
    static int failures;
    static void Expect(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) failures++; }

    static int Main()
    {
        Expect(RigStatusJson.CollectorSignedIn(JObject.Parse("{\"floor\":null}")) == null, "floor null -> no collector");
        Expect(RigStatusJson.CollectorSignedIn(JObject.Parse("{}")) == null, "no floor -> no collector");
        Expect(RigStatusJson.CollectorSignedIn(JObject.Parse("{\"floor\":{\"operator\":null}}")) == null, "operator null -> no collector");
        Expect(RigStatusJson.CollectorSignedIn(JObject.Parse("{\"floor\":7}")) == null, "floor a number -> no collector");
        Expect(RigStatusJson.CollectorSignedIn(JObject.Parse("{\"floor\":{\"operator\":{\"signed_in\":true}}}")) == true, "signed in");
        Expect(RigStatusJson.CollectorSignedIn(JObject.Parse("{\"floor\":{\"operator\":{\"signed_in\":false}}}")) == false, "signed out");
        Expect(RigStatusJson.CollectorSignedIn(null) == null, "no status -> no collector");
        Expect(RigStatusJson.CollectorName(JObject.Parse("{\"floor\":{\"operator\":{\"signed_in\":true,\"display_name\":\" Asha \"}}}")) == "Asha", "signed in: the collector's display name");
        Expect(RigStatusJson.CollectorName(JObject.Parse("{\"floor\":{\"operator\":{\"signed_in\":false,\"display_name\":\"Asha\"}}}")) == null, "signed out: no name");
        Expect(RigStatusJson.CollectorName(JObject.Parse("{\"floor\":{\"operator\":{\"signed_in\":true,\"display_name\":\"\"}}}")) == null, "an empty name is no name");
        Expect(RigStatusJson.CollectorName(JObject.Parse("{\"floor\":{\"operator\":{\"signed_in\":true}}}")) == null, "no display_name: no name");
        Expect(RigStatusJson.CollectorName(JObject.Parse("{\"floor\":null}")) == null && RigStatusJson.CollectorName(null) == null, "local mode: no name");

        Expect(RigStatusJson.SealState(JObject.Parse("{\"state\":\"sealed\"}")) == "sealed", "seal state");
        Expect(RigStatusJson.SealState(JValue.CreateNull()) == "", "seal verdict null -> empty");
        Expect(RigStatusJson.SealState(new JValue(3)) == "", "seal verdict number -> empty");

        var pass = RigStatusJson.Check("{\"ready\":true,\"problems\":[]}");
        Expect(pass.ready, "ready check passes");
        var fail = RigStatusJson.Check("{\"ready\":false,\"problems\":[\"left_camera: no device on usb-port:1-1.1\",\"calibration left.json missing\"]}");
        Expect(!fail.ready && fail.problems.Contains("1-1.1") && fail.problems.Contains("; "), "failing check lists its problems: " + fail.problems);
        Expect(!RigStatusJson.Check("{\"problems\":[]}").ready, "no ready flag -> not ready");
        Expect(!RigStatusJson.Check("not json").ready, "unreadable -> not ready");
        Expect(!RigStatusJson.Check("[]").ready, "not an object -> not ready");

        // A rig episode failing mid-recording: the decision that stops OQC's session.
        var failedStatus = JObject.Parse("{\"state\":\"recording\",\"episode\":{\"episode_id\":\"20261017_132350\",\"failure\":{\"cause\":\"hardware\",\"part\":\"left_camera\",\"detail\":\"left camera: selected camera exposure gap is 50.37 ms, need 0-50 ms\"},\"streams\":{}}}");
        var f = RigStatusJson.EpisodeFailure(failedStatus, "20261017_132350");
        Expect(f != null && RigStatusJson.FailureDetail(f!).Contains("50.37 ms"), "a failed running episode is reported with its detail");
        Expect(RigStatusJson.EpisodeFailure(failedStatus, "20261017_120000") == null, "another episode's failure is not this one's");
        Expect(RigStatusJson.EpisodeFailure(JObject.Parse("{\"episode\":{\"episode_id\":\"e\",\"failure\":null}}"), "e") == null, "a clean episode has no failure");
        Expect(RigStatusJson.EpisodeFailure(JObject.Parse("{\"episode\":null}"), "e") == null, "no episode -> no failure");
        Expect(RigStatusJson.EpisodeFailure(failedStatus, null) == null, "nothing recording -> no failure");
        Expect(RigStatusJson.FailureDetail(JObject.Parse("{\"cause\":\"hardware\"}")) == "hardware", "detail falls back to the cause");

        Expect(RigStatusJson.StopFailed("{\"outcome\":\"failed\",\"cause\":\"hardware\"}"), "failed receipt");
        Expect(!RigStatusJson.StopFailed("{\"outcome\":\"complete\"}"), "complete receipt");
        Expect(RigStatusJson.StopFailed("{\"error\":{\"code\":\"state\"}}"), "error receipt counts as failed");

        // The in-headset list's rig line.
        Expect(RigStatusJson.SealSummary("{\"state\":\"sealed\",\"size_bytes\":47395635}", null, null) == "sealed 45.2 MB", "sealed with size");
        Expect(RigStatusJson.SealSummary("{\"state\":\"pending\",\"detail\":\"waiting for capture\"}", null, null) == "pending: waiting for capture", "pending with reason");
        Expect(RigStatusJson.SealSummary("{\"state\":\"refused\",\"detail\":\"seal gate failed: pose coverage\"}", null, null).StartsWith("refused: seal gate"), "refused with reason");
        Expect(RigStatusJson.SealSummary(null, "{\"code\":\"rig_episode_failed\",\"message\":\"the rig's episode failed while recording: gap\"}", null).StartsWith("refused: the rig"), "an app-recorded failure wins");
        Expect(RigStatusJson.SealSummary(null, null, null) == "", "no rig -> no line");
        Expect(RigStatusJson.SealSummary("garbage", null, null) == "seal unreadable", "unreadable seal");

        Console.WriteLine(failures == 0 ? "ALL PASS" : failures + " FAILED");
        return failures;
    }
}
