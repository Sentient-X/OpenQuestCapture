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

        Console.WriteLine(failures == 0 ? "ALL PASS" : failures + " FAILED");
        return failures;
    }
}
