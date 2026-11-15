// Host check of OsHeadsetSerial (Assets/RealityLog/Scripts/Runtime/Rig/OsServiceDump.cs): the
// headset serial from CompanionService's dumpsys text, as pod5 printed it on 2026-11-15.
using System;
using RealityLog.Rig;

static class OsHeadsetSerialHarness
{
    static int failures;
    static void Expect(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) failures++; }

    static int Main()
    {
        var dump = "CompanionService state:\n  Device ID: 6f1c2d3e-0000-4000-8000-000000000000\n  Provisioned serial: 3487C10H3P07B9\n  Paired: true\n";
        Expect(OsHeadsetSerial.Parse(dump) == "3487C10H3P07B9", "the provisioned serial is read");
        Expect(OsHeadsetSerial.Parse("Device ID: x\nSerial Number: 2L0YNC6H3902Y5\n") == null, "a controller's serial number is not the headset's");
        Expect(OsHeadsetSerial.Parse("Provisioned serial: \n") == null, "an empty serial is none");
        Expect(OsHeadsetSerial.Parse("Provisioned serial: ab$cd!") == null, "a serial of other characters is none");
        Console.WriteLine(failures == 0 ? "ALL PASS" : failures + " FAILED");
        return failures == 0 ? 0 : 1;
    }
}
