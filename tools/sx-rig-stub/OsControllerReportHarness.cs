#nullable enable
// OsControllerReport: the controllers' charge from `dumpsys OVRRemoteService`'s text (ControllerBattery
// source (f)), and the cache that ages it. Lines are from the lab Quest 3S, Horizon OS 207,
// 2026-10-29. Run by test_binding.sh.

using System;
using RealityLog.Rig;

internal static class OsControllerReportHarness
{
    private static int failures;

    private static void Expect(bool condition, string what)
    {
        Console.WriteLine((condition ? "PASS " : "FAIL ") + what);
        if (!condition) failures++;
    }

    private const string Right = "  Paired device: fb563d59d549b01d, Type:  Right, Model: RUBYPRQ, HardwareRev: 0x9a, Firmware: 206.3.0, ImuModel: ICM42686, Battery:  30%, isAttached: 0, Serial Number: 2L0YNC5H9Z08SP, Status: Active, ExternalStatus: CONNECTED_INACTIVE, TrackingStatus: NONE, BrightnessLevel: GOOD";
    private const string Left = "  Paired device: 5227d02c79c8ce97, Type:   Left, Model: RUBYPRQ, HardwareRev: 0x9a, Firmware: 206.3.0, ImuModel: ICM42686, Battery:  40%, isAttached: 0, Serial Number: 2L0YMC4H9R03X0, Status: Active, ExternalStatus: CONNECTED_ACTIVE, TrackingStatus: POSITION, BrightnessLevel: GOOD";
    private const string LeftOff = "  Paired device: 5227d02c79c8ce97, Type:   Left, Model: RUBYPRQ, HardwareRev: 0x9a, Firmware: 206.3.0, ImuModel: ICM42686, Battery:  60%, isAttached: 0, Serial Number: 2L0YMC4H9R03X0, Status: Searching, ExternalStatus: DISCONNECTED, TrackingStatus: NONE, BrightnessLevel: GOOD";
    private const string History =
        "    Disconnect history (fb563d59d549b01d):\n" +
        "      16:57:19.729 (15009.657s ago) - from=Active exitingStream=1 battery=30 activeFor=1805.001s\n" +
        "  Active Session Tracking Metrics:\n    session duration         : 11760.489s\n";

    private static int Main()
    {
        var both = OsControllerReport.Parse("ControllerGlue Status\n" + Right + "\r\n" + History + Left + "\n" + History);
        Expect(both.Left?.Percent == 40 && both.Right?.Percent == 30, "both controllers: Left 40 %, Right 30 %");
        Expect(both.Left!.Status == "Active" && both.Left.ExternalStatus == "CONNECTED_ACTIVE" && both.Left.TrackingStatus == "POSITION",
            "Status, ExternalStatus and TrackingStatus are read apart");
        Expect(both.Right!.Connected && both.Right.ExternalStatus == "CONNECTED_INACTIVE", "an idle controller is still connected");
        Expect(both.Paired == 2 && both.Garbled == 0, "history lines (battery=30) are not paired-device lines");

        var off = OsControllerReport.Parse(LeftOff + "\n" + Right);
        Expect(off.Left is { Connected: false, Percent: 60 } && off.Right?.Percent == 30, "a disconnected controller parses but is not connected");

        var garbled = OsControllerReport.Parse(Left.Replace("Battery:  40%", "Battery: forty") + "\n" + Right);
        Expect(garbled.Left == null && garbled.Right?.Percent == 30 && garbled.Garbled == 1, "a garbled line reads as nothing; the other side stands");
        var noSide = OsControllerReport.Parse(Left.Replace("Type:   Left", "Type:   Middle"));
        Expect(!noSide.Parsed && noSide.Garbled == 1, "an unknown side is garbled");

        var over = OsControllerReport.Parse(Left.Replace("40%", "140%") + "\n" + Right.Replace("30%", "1000%"));
        Expect(over.Left == null && over.Right == null && over.Garbled == 2, "a charge over 100 is refused");
        Expect(OsControllerReport.Parse(Left.Replace("40%", "100%")).Left?.Percent == 100
            && OsControllerReport.Parse(Left.Replace("40%", "0%")).Left?.Percent == 0, "0 and 100 are in range");

        var empty = OsControllerReport.Parse("");
        Expect(!empty.Parsed && empty.Paired == 0, "an empty dump parses nothing");
        Expect(!OsControllerReport.Parse(null).Parsed, "no dump parses nothing");
        var denied = OsControllerReport.Parse("Permission Denial: can't dump OVRRemoteService from pid=1, uid=10123\n");
        Expect(!denied.Parsed, "a refusal parses nothing");

        var twoLefts = OsControllerReport.Parse(LeftOff + "\n" + Left);
        Expect(twoLefts.Left?.Percent == 40 && twoLefts.Left.Connected, "of two pairings of one side, the connected one wins");

        // The cache: what parsed is current; a failure changes nothing; five minutes ages it out.
        var t0 = new DateTime(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc);
        var cache = new OsControllerBatteryCache();
        Expect(cache.Read(t0) == (null, null), "nothing read is not available, never 0");
        cache.Record(both, t0);
        Expect(cache.Read(t0) == (40, 30), "a parsed report is read");
        cache.Record(empty, t0.AddMinutes(1));
        cache.Record(denied, t0.AddMinutes(2));
        Expect(cache.Read(t0.AddMinutes(4)) == (40, 30), "a failed report keeps the last good charge");
        Expect(cache.Read(t0.AddMinutes(5).AddSeconds(1)) == (null, null), "a charge older than 5 min is stale");
        cache.Record(off, t0.AddMinutes(6));
        Expect(cache.Read(t0.AddMinutes(6)) == (null, 30), "a disconnected controller is not available at once");
        cache.Record(garbled, t0.AddMinutes(7));
        Expect(cache.Read(t0.AddMinutes(7)) == (null, 30), "a controller missing from the report is not available");
        Expect(cache.Read(t0.AddMinutes(5)) == (null, null), "a read before the charge was taken is not available");

        Expect(both.Describe() == "Left 40% (Active, CONNECTED_ACTIVE, POSITION), Right 30% (Active, CONNECTED_INACTIVE, NONE)",
            "the log line names each side");
        return failures == 0 ? 0 : 1;
    }
}
