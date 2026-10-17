// Host check of PodSetupJson: the console's QR payload and the keyboard's site and join code.
// Run by test_binding.sh; not part of the Unity project.
using System;
using RealityLog.Rig;

static class PodSetupJsonHarness
{
    static int failures;
    static void Expect(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) failures++; }

    static int Main()
    {
        var ok = PodSetupJson.ParseQr("{\"site\":\"https://app.preview.sentientx.io\",\"join_code\":\"sxj_0123456789abcdefghijklmn\"}");
        Expect(ok.error == null && ok.site == "https://app.preview.sentientx.io" && ok.joinCode == "sxj_0123456789abcdefghijklmn", "a console QR payload parses");
        Expect(PodSetupJson.ParseQr("{\"site\":\"app.sentientx.io\",\"join_code\":\"sxj_abcdefgh\"}").site == "https://app.sentientx.io", "a bare host becomes https");
        Expect(PodSetupJson.ParseQr("https://example.com").error != null, "a non-JSON QR is refused");
        Expect(PodSetupJson.ParseQr("{\"site\":\"https://x\"}").error != null, "a payload without join_code is refused");
        Expect(PodSetupJson.ParseQr("{\"site\":\"http://app.sentientx.io\",\"join_code\":\"sxj_abcdefgh\"}").error == "the site must be https", "plain http is refused");
        Expect(PodSetupJson.ParseQr("{\"site\":\"https://x.io\",\"join_code\":\"abc\"}").error != null, "a code that is not sxj_ is refused");
        Expect(PodSetupJson.NormalizeSite("https://app.sentientx.io/console/pods").site == "https://app.sentientx.io", "the site keeps scheme and host only");
        Expect(PodSetupJson.NormalizeSite("  ").error != null, "an empty site is refused");
        Expect(PodSetupJson.CheckJoinCode(" sxj_abcdefgh ") == null, "a join code with spaces around is accepted");
        Console.WriteLine(failures == 0 ? "ALL PASS" : failures + " FAILED");
        return failures;
    }
}
