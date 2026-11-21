// Host check of Assets/RealityLog/Scripts/Runtime/Rig/SxRig.cs against the STUB libsx_rig.so:
// error mapping, last_error JSON, the SX_RIG_E_BUFFER retry, anchors, and the typed
// library-missing failure. Run by test_binding.sh; not part of the Unity project.
using System;
using RealityLog.Rig;
static class Harness {
    static int failures = 0;
    static void Expect(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) failures++; }
    static int Main() {
        Expect(SxRig.Status().Contains("\"closed\""), "status before open: " + SxRig.Status());
        try { SxRig.AttachUsb(5, 0x32e4, 0x9230, "/dev/bus/usb/001/002"); Expect(false, "attach before open throws"); }
        catch (SxRigException e) { Expect(e.Error == SxRigError.State && e.ErrorJson.Contains("\"state\""), "attach before open -> State " + e.ErrorJson); }
        // The paired phone is taken before the rig opens (sx_rig_phone_link keeps it outside the rig).
        SxRig.PhoneLink("{\"collector_id\":\"usr_1\",\"connected\":true}");
        Expect(true, "phone_link before open ok");
        SxRig.PhoneLink("{}");
        Expect(true, "phone_link unpaired ok");
        try { SxRig.PhoneLink("{\"collector_id\":\"usr_1\"}"); Expect(false, "a phone link without connected throws"); }
        catch (SxRigException e) { Expect(e.Error == SxRigError.Argument && e.Function == "sx_rig_phone_link", "phone_link without connected -> Argument " + e.ErrorJson); }
        try { SxRig.PhoneLink("[]"); Expect(false, "a phone link that is not an object throws"); }
        catch (SxRigException e) { Expect(e.Error == SxRigError.Argument && e.Function == "sx_rig_phone_link", "phone_link not an object -> Argument"); }
        try { SxRig.Open("not json"); Expect(false, "bad config throws"); }
        catch (SxRigException e) { Expect(e.Error == SxRigError.Argument, "bad config -> Argument " + e.ErrorJson); }
        SxRig.Open("{\"staging_dir\":\"/tmp/x\",\"rig\":{}}");
        Expect(SxRig.Status().Contains("no_rig"), "status after open: " + SxRig.Status());
        Expect(SxRig.Status().Contains("\"collector\":null") && !SxRig.Status().Contains("\"operator\""),
            "no phone paired: floor.collector is null (unattributed), no floor.operator");
        try { SxRig.RunCheck(); Expect(false, "check throws"); }
        catch (SxRigException e) { Expect(e.Error == SxRigError.Device && e.ErrorJson.Contains("no_rig") && e.Function == "sx_rig_check", "check -> Device " + e.ErrorJson); }
        try { SxRig.Enroll("nope"); Expect(false, "bad join code throws"); }
        catch (SxRigException e) { Expect(e.Error == SxRigError.Argument && e.Function == "sx_rig_enroll", "enroll bad code -> Argument " + e.ErrorJson); }
        try { SxRig.Enroll("sxj_aaaa"); Expect(false, "enroll without site throws"); }
        catch (SxRigException e) { Expect(e.Error == SxRigError.State, "enroll without site -> State " + e.ErrorJson); }
        try { SxRig.EnrollSite("{\"factory_url\":\"https://a/f/\",\"auth_url\":\"https://a/u/\",\"join_code\":\"sxj_aaaa\"}"); Expect(false, "enroll_site throws"); }
        catch (SxRigException e) { Expect(e.Error == SxRigError.Device && e.Function == "sx_rig_enroll_site", "enroll_site -> Device"); }
        try { SxRig.ApiToken(); Expect(false, "api_token throws"); }
        catch (SxRigException e) { Expect(e.Error == SxRigError.State && e.Function == "sx_rig_api_token", "api_token before enrollment -> State"); }
        Expect(SxRig.Command("{\"command\":\"finalize\"}").Contains("\"answer\""), "command finalize -> answer");
        try { SxRig.Command("{\"command\":\"end_collection\"}"); Expect(false, "command throws"); }
        catch (SxRigException e) { Expect(e.Error == SxRigError.State && e.Function == "sx_rig_command", "command -> State " + e.ErrorJson); }
        SxRig.ReportDevice("{\"battery_percent\":80,\"charging\":false,\"network_time_synchronized\":true}");
        Expect(true, "report_device ok");
        try { SxRig.ReportDevice("nope"); Expect(false, "bad report throws"); }
        catch (SxRigException e) { Expect(e.Error == SxRigError.Argument && e.Function == "sx_rig_report_device", "report_device bad -> Argument"); }
        SxRig.ObserveTracking("{\"left\":{},\"right\":{},\"sampleAgeMs\":3}");
        Expect(true, "observe_tracking ok");
        try { SxRig.ObserveTracking("[]"); Expect(false, "a tracking block that is not an object throws"); }
        catch (SxRigException e) { Expect(e.Error == SxRigError.Argument && e.Function == "sx_rig_observe_tracking", "observe_tracking bad -> Argument"); }
        var (mono, utc) = SxRig.ReadAnchors(() => 123L);
        Expect(mono == 123 && Math.Abs(utc / 1_000_000 - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) < 5000, "anchors " + mono + " " + utc);
        try { SxRig.Start("{\"session\":\"ü\"}", mono, utc); Expect(false, "start throws"); }
        catch (SxRigException e) { Expect(e.Error == SxRigError.Device, "start -> Device"); }
        try { SxRig.StartArmed("{\"episode_id\":\"e1\"}"); Expect(false, "start_armed throws"); }
        catch (SxRigException e) { Expect(e.Error == SxRigError.Device && e.Function == "sx_rig_start_armed", "start_armed -> Device"); }
        try { SxRig.RequestStop(); Expect(false, "request_stop throws without an episode"); }
        catch (SxRigException e) { Expect(e.Error == SxRigError.State && e.Function == "sx_rig_request_stop", "request_stop -> State " + e.ErrorJson); }
        try { SxRig.Stop(); Expect(false, "stop throws"); }
        catch (SxRigException e) { Expect(e.Error == SxRigError.State, "stop -> State " + e.ErrorJson); }
        SxRig.Close();
        Console.WriteLine(failures == 0 ? "ALL PASS" : failures + " FAILED");
        failures += Harness2.Run();
        return failures;
    }
}
static class Harness2 {
    public static int Run() {
        SxRig.Open("{\"staging_dir\":\"/tmp/x\",\"rig\":{},\"stub_long_status\":true}");
        var s = SxRig.Status();
        var ok = s.Length == 5999 && s.EndsWith("\"}");
        Console.WriteLine((ok ? "PASS" : "FAIL") + " buffer retry, status length " + s.Length);
        SxRig.Close();
        // A paired phone opened the Factory shift: the floor names its person.
        SxRig.Open("{\"staging_dir\":\"/tmp/x\",\"rig\":{},\"stub_collector\":true}");
        var attributed = SxRig.Status();
        var named = attributed.Contains("\"collector\":{\"id\":\"usr_stub\",\"name\":\"Stub Collector\"}");
        Console.WriteLine((named ? "PASS" : "FAIL") + " attributed: floor.collector names the paired phone's person: " + attributed);
        SxRig.Close();
        return (ok ? 0 : 1) + (named ? 0 : 1);
    }
}
