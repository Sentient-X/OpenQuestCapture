// Host check of PodSetupJson (QR payload, URLs, join code, enroll_site request and steps) and
// RigApiGuard (bearer token when enrolled; a headset not set up serves only its setup). Run by test_binding.sh.
using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using RealityLog.Rig;

static class PodSetupJsonHarness
{
    static int failures;
    static void Expect(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) failures++; }

    static int Main()
    {
        var ok = PodSetupJson.ParseQr("{\"factory_url\":\"https://app.preview.sentientx.io/d/factory/\",\"auth_url\":\"https://app.preview.sentientx.io/d/auth\",\"join_code\":\"sxj_0123456789abcdefghijklmn\"}");
        Expect(ok.error == null && ok.factoryUrl == "https://app.preview.sentientx.io/d/factory/" && ok.authUrl == "https://app.preview.sentientx.io/d/auth/"
            && ok.joinCode == "sxj_0123456789abcdefghijklmn", "a console QR payload parses; URLs keep their path and end in /");
        Expect(PodSetupJson.ParseQr("{\"site\":\"https://x\",\"join_code\":\"sxj_abcdefgh\"}").error != null, "the old {site} payload is refused");
        Expect(PodSetupJson.ParseQr("not json").error != null, "a non-JSON QR is refused");
        Expect(PodSetupJson.ParseQr("{\"factory_url\":\"http://a/d/factory/\",\"auth_url\":\"https://a/d/auth/\",\"join_code\":\"sxj_abcdefgh\"}").error == "the factory_url must be https", "plain http is refused");
        Expect(PodSetupJson.ParseQr("{\"factory_url\":\"https://a/f/\",\"auth_url\":\"https://a/u/\",\"join_code\":\"abc\"}").error != null, "a code that is not sxj_ is refused");
        Expect(PodSetupJson.NormalizeUrl("app.sentientx.io/d/factory", "factory_url").url == "https://app.sentientx.io/d/factory/", "a bare URL becomes https with a trailing slash");
        Expect(PodSetupJson.NormalizeUrl(PodSetupJson.DefaultFactoryUrl, "f").url == PodSetupJson.DefaultFactoryUrl, "the production default is normal already");

        var request = JObject.Parse(PodSetupJson.Request("https://a/f/", "https://a/u/", "sxj_abcdefgh", "rig_yubi"));
        Expect((string?)request["factory_url"] == "https://a/f/" && (string?)request["auth_url"] == "https://a/u/"
            && (string?)request["join_code"] == "sxj_abcdefgh" && (string?)request["rig_type_id"] == "rig_yubi"
            && request["site"] == null && request.Count == 4, "the enroll_site request is the QR's flat shape");
        Expect(JObject.Parse(PodSetupJson.Request("https://a/f/", "https://a/u/", "sxj_abcdefgh", null))["rig_type_id"] == null, "rig_type_id is optional");

        var answer = "{\"pod_id\":\"pod_1\",\"pod\":{},\"steps\":[{\"step\":\"join\",\"state\":\"succeeded\",\"detail\":\"\"},{\"step\":\"commission\",\"state\":\"succeeded\",\"detail\":\"\"},{\"step\":\"rig\",\"state\":\"unavailable\",\"detail\":\"attach both cameras and both encoders\"},{\"step\":\"builds\",\"state\":\"unavailable\",\"detail\":\"the Factory does not serve the list yet\"}]}";
        var steps = PodSetupJson.Steps(answer);
        Expect(steps.Count == 4 && steps[2].Step == "rig" && steps[2].State == "unavailable", "the answer's steps parse");
        Expect(PodSetupJson.Describe(steps) == "join: succeeded · commission: succeeded · rig: unavailable (attach both cameras and both encoders) · builds: unavailable (the Factory does not serve the list yet)", "the status line");
        var failed = PodSetupJson.Steps("{\"code\":\"device\",\"detail\":\"x\",\"refusal\":{\"kind\":\"enrollment_failed\",\"steps\":[{\"step\":\"join\",\"state\":\"failed\",\"detail\":\"the join code was already used\"}]}}");
        Expect(failed.Count == 1 && failed[0].State == "failed" && failed[0].Detail.Contains("already used"), "a failed enrollment's steps come from the refusal");
        Expect(PodSetupJson.Steps("garbage").Count == 0, "unreadable -> no steps");

        var token = new string('a', 64);
        const string Get = "GET", Post = "POST", Status = "/api/status";
        // A headset not set up serves only what setting it up needs; it records nothing.
        Expect(RigApiGuard.Authorized(null, null, false, Get, Status), "unenrolled: status is open");
        Expect(RigApiGuard.Authorized(null, null, false, Get, "/api/diagnostics"), "unenrolled: diagnostics are open");
        Expect(RigApiGuard.Authorized(null, null, false, Post, "/api/enroll"), "unenrolled: enrollment is open");
        foreach (var (method, path) in new[] { (Post, "/api/start-recording"), (Post, "/api/stop-recording"), (Post, "/api/mark-episode"),
            (Post, "/api/command"), (Post, "/api/settings"), (Get, "/api/recordings"), (Get, "/api/recordings/x.zip"), ("DELETE", "/api/recordings/x"),
            (Get, "/api/preview.jpg"), (Post, "/api/keep-awake"), (Post, "/api/timesync/ping"), (Post, Status), (Get, "/api/enroll") })
        {
            Expect(!RigApiGuard.Authorized(null, null, false, method, path), "unenrolled: " + method + " " + path + " is refused");
        }
        Expect(!RigApiGuard.Authorized(null, null, true, Get, Status), "enrolled token lookup failure never opens the API");
        Expect(!RigApiGuard.Authorized("Bearer " + token, "", true, Get, Status), "an empty enrolled token is refused even with a header");
        Expect(!RigApiGuard.Authorized(null, token, true, Get, Status), "enrolled: no header is refused, even for status");
        Expect(!RigApiGuard.Authorized("Bearer " + new string('b', 64), token, true, Get, Status), "a wrong token is refused");
        Expect(!RigApiGuard.Authorized("Basic " + token, token, true, Get, Status), "another scheme is refused");
        Expect(RigApiGuard.Authorized("Bearer " + token, token, true, Post, "/api/start-recording"), "the right bearer token is accepted");

        Console.WriteLine(failures == 0 ? "ALL PASS" : failures + " FAILED");
        return failures;
    }
}
