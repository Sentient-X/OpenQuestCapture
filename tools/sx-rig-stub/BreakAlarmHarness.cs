// Host check of Assets/RealityLog/Scripts/Runtime/Rig/BreakAlarmPolicy.cs: with a collector signed
// into the pod and the headset worn, 45 s since the last take ended (good or failed) sounds the alarm,
// and it keeps sounding until a take records or the collector signs out. Taking the headset off
// silences it without resetting the clock. Run by test_binding.sh; not part of the Unity project.
using System;
using RealityLog.Rig;

static class BreakAlarmHarness
{
    static int failures;
    static void Expect(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) failures++; }

    static int Main()
    {
        var p = new BreakAlarmPolicy();
        BreakAlarmVerdict Look(double t, bool signedIn = true, bool? worn = true, bool fault = false, bool collecting = false) =>
            p.Observe(t, signedIn, worn, fault, collecting);

        Expect(BreakAlarmPolicy.GraceSeconds == 45.0, "the grace is 45 s");
        Expect(BreakAlarmPolicy.LoudAfterSeconds == 60.0 && BreakAlarmPolicy.SirenAfterSeconds == 90.0, "loud from 60 s, siren from 90 s");
        Expect(Look(0, signedIn: false) == BreakAlarmVerdict.Off, "no collector signed in: silent");
        Expect(Look(500, signedIn: false) == BreakAlarmVerdict.Off, "no collector, however long: silent");
        Expect(p.IdleSeconds(500) == null, "no collector: no clock");
        Expect(Look(1000) == BreakAlarmVerdict.Counting, "collector signs in: the clock starts");
        Expect(Look(1044.9) == BreakAlarmVerdict.Counting, "44.9 s idle: still quiet");
        Expect(Look(1045) >= BreakAlarmVerdict.Soft, "45 s idle: the alarm sounds");
        Expect(Look(1045) == BreakAlarmVerdict.Soft, "45 s: a soft beep first");
        Expect(Look(1059.9) == BreakAlarmVerdict.Soft, "59.9 s: still the soft beep");
        Expect(Look(1060) == BreakAlarmVerdict.Loud, "60 s: the loud beep");
        Expect(Look(1089.9) == BreakAlarmVerdict.Loud, "89.9 s: still the loud beep");
        Expect(Look(1090) == BreakAlarmVerdict.Siren, "90 s: the siren");
        Expect(Look(1300) == BreakAlarmVerdict.Siren, "it keeps sounding the siren while nothing records");
        Expect(Look(1301, collecting: true) == BreakAlarmVerdict.Off, "a take starts: silent");
        Expect(p.IdleSeconds(1301) == null, "recording: no clock");
        Expect(Look(2000, collecting: true) == BreakAlarmVerdict.Off, "a long take never alarms");
        Expect(Look(2001) == BreakAlarmVerdict.Counting, "the take ends (good or failed, both return to idle): the clock starts from its end");
        Expect(Look(2030) == BreakAlarmVerdict.Counting, "29 s between takes: quiet");
        Expect(Math.Abs(p.IdleSeconds(2030)!.Value - 29) < 1e-9, "the idle seconds count from the take's end");
        Expect(Look(2035, collecting: true) == BreakAlarmVerdict.Off, "next take within the grace: never sounded");

        // A refused Start never leaves idle, so it is not a take: the clock keeps the last take's end.
        Expect(Look(2100) == BreakAlarmVerdict.Counting, "a fresh idle spell starts at the take's end");
        Expect(Look(2120) == BreakAlarmVerdict.Counting, "refused starts in between change nothing (still idle)");
        Expect(Look(2145) >= BreakAlarmVerdict.Soft, "45 s after the last take: sounds, whatever was refused");

        // The headset off a head silences it but keeps the clock (build 90 reset it here).
        Expect(Look(2146, worn: false) == BreakAlarmVerdict.Counting, "headset off: silent, the clock keeps running");
        Expect(Math.Abs(p.IdleSeconds(2146)!.Value - 46) < 1e-9, "off the head the clock still counts from the take's end");
        Expect(Look(2147, worn: null) == BreakAlarmVerdict.Counting, "worn state unread: silent, clock kept");
        Expect(Look(2148, worn: true) == BreakAlarmVerdict.Soft, "back on the head past the grace: sounds at once, at the level the clock has reached");
        Expect(Look(2195, worn: false) == BreakAlarmVerdict.Counting, "off again at 95 s: silent");
        Expect(Look(2196, worn: true) == BreakAlarmVerdict.Siren, "back on at 96 s: straight to the siren, no fresh ramp");
        Expect(Look(2200, collecting: true) == BreakAlarmVerdict.Off, "a take: silent");
        Expect(Look(2210, worn: false) == BreakAlarmVerdict.Counting, "take ended, headset lifted at once");
        Expect(Look(2250, worn: true) == BreakAlarmVerdict.Counting, "back on at 40 s: still inside the grace");
        Expect(Look(2255) >= BreakAlarmVerdict.Soft, "45 s from the take's end: sounds; lifting the headset bought nothing");

        // A device fault is not a break the collector can end: it stops the clock.
        Expect(Look(2256, fault: true) == BreakAlarmVerdict.Off, "the rig refuses Start for a device fault: silent");
        Expect(p.IdleSeconds(2256) == null, "device fault: no clock");
        Expect(Look(2400) == BreakAlarmVerdict.Counting, "the fault clears: a fresh 45 s");

        Expect(Look(2445) >= BreakAlarmVerdict.Soft, "and sounds after it");
        Expect(Look(2446, signedIn: false) == BreakAlarmVerdict.Off, "the collector signs out: silent");
        Expect(Look(2447) == BreakAlarmVerdict.Counting, "a new sign-in gets its own 45 s, not the old clock");

        Console.WriteLine(failures == 0 ? "ALL PASS break alarm" : failures + " FAILED break alarm");
        return failures == 0 ? 0 : 1;
    }
}
