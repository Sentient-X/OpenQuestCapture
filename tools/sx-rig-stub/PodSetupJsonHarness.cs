// Host check of PodSetupJson (QR payload, URLs, join code, enroll_site request and steps) and
// RigApiGuard (bearer token when enrolled and not Local only). Run by test_binding.sh.
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
        Expect(RigApiGuard.Authorized(null, token, localOnly: true), "Local only keeps the API open");
        Expect(RigApiGuard.Authorized(null, null, localOnly: false), "not enrolled (no token) keeps the API open");
        Expect(!RigApiGuard.Authorized(null, token, localOnly: false), "enrolled, not Local only: no header is refused");
        Expect(!RigApiGuard.Authorized("Bearer " + new string('b', 64), token, false), "a wrong token is refused");
        Expect(!RigApiGuard.Authorized("Basic " + token, token, false), "another scheme is refused");
        Expect(RigApiGuard.Authorized("Bearer " + token, token, false), "the right bearer token is accepted");

        Console.WriteLine(failures == 0 ? "ALL PASS" : failures + " FAILED");
        return failures;
    }
}
