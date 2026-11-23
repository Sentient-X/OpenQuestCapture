// Host check of Assets/RealityLog/Scripts/Runtime/Rig/BreakAlarmPolicy.cs: with a collector signed
// into the pod, 30 s with nothing recording sounds the alarm, and it keeps sounding until a take
// records or the collector signs out. Run by test_binding.sh; not part of the Unity project.
using System;
using RealityLog.Rig;

static class BreakAlarmHarness
{
    static int failures;
    static void Expect(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) failures++; }

    static int Main()
    {
        var p = new BreakAlarmPolicy();
        Expect(p.Observe(0, armed: false, collecting: false) == BreakAlarmVerdict.Off, "no collector signed in: silent");
        Expect(p.Observe(500, armed: false, collecting: false) == BreakAlarmVerdict.Off, "no collector, however long: silent");
        Expect(p.IdleSeconds(500) == null, "no collector: no clock");
        Expect(p.Observe(1000, armed: true, collecting: false) == BreakAlarmVerdict.Counting, "collector signs in: the clock starts");
        Expect(p.Observe(1029.9, armed: true, collecting: false) == BreakAlarmVerdict.Counting, "29.9 s idle: still quiet");
        Expect(p.Observe(1030, armed: true, collecting: false) == BreakAlarmVerdict.Sounding, "30 s idle: the alarm sounds");
        Expect(p.Observe(1300, armed: true, collecting: false) == BreakAlarmVerdict.Sounding, "it keeps sounding while nothing records");
        Expect(p.Observe(1301, armed: true, collecting: true) == BreakAlarmVerdict.Off, "a take starts: silent");
        Expect(p.IdleSeconds(1301) == null, "recording: no clock");
        Expect(p.Observe(2000, armed: true, collecting: true) == BreakAlarmVerdict.Off, "a long take never alarms");
        Expect(p.Observe(2001, armed: true, collecting: false) == BreakAlarmVerdict.Counting, "the take ends: the clock starts from its end");
        Expect(p.Observe(2020, armed: true, collecting: false) == BreakAlarmVerdict.Counting, "19 s between takes: quiet");
        Expect(Math.Abs(p.IdleSeconds(2020)!.Value - 19) < 1e-9, "the idle seconds count from the take's end");
        Expect(p.Observe(2025, armed: true, collecting: true) == BreakAlarmVerdict.Off, "next take within the grace: never sounded");
        Expect(p.Observe(2100, armed: true, collecting: false) == BreakAlarmVerdict.Counting, "a fresh idle spell gets a fresh 30 s");
        Expect(p.Observe(2131, armed: true, collecting: false) == BreakAlarmVerdict.Sounding, "and sounds after it");
        Expect(p.Observe(2132, armed: false, collecting: false) == BreakAlarmVerdict.Off, "the collector signs out: silent");
        Expect(p.Observe(2133, armed: true, collecting: false) == BreakAlarmVerdict.Counting, "a new sign-in gets its own 30 s, not the old clock");

        Console.WriteLine(failures == 0 ? "ALL PASS break alarm" : failures + " FAILED break alarm");
        return failures == 0 ? 0 : 1;
    }
}
