// Host check of Assets/RealityLog/Scripts/Runtime/Rig/BreakAlarmPolicy.cs: any running headset with no
// take for 45 s since the last one ended (good or failed) sounds, climbing to a loud beep at 60 s and
// the siren at 90 s, until a take records. Signing in or out, the headset on or off and a rig refusing
// Start change nothing. Run by test_binding.sh; not part of the Unity project.
using System;
using RealityLog.Rig;

static class BreakAlarmHarness
{
    static int failures;
    static void Expect(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) failures++; }

    static int Main()
    {
        var p = new BreakAlarmPolicy();
        BreakAlarmVerdict Look(double t, bool collecting = false) => p.Observe(t, collecting);

        Expect(BreakAlarmPolicy.GraceSeconds == 45.0, "the grace is 45 s");
        Expect(BreakAlarmPolicy.LoudAfterSeconds == 60.0 && BreakAlarmPolicy.SirenAfterSeconds == 90.0, "loud from 60 s, siren from 90 s");

        Expect(Look(0) == BreakAlarmVerdict.Counting, "the app starts with no take: the clock starts, nobody signed in or not");
        Expect(Look(44.9) == BreakAlarmVerdict.Counting, "44.9 s: quiet");
        Expect(Look(45) == BreakAlarmVerdict.Soft, "45 s with no take since the app started: soft beep");
        Expect(Look(60) == BreakAlarmVerdict.Loud, "60 s: loud beep");
        Expect(Look(90) == BreakAlarmVerdict.Siren, "90 s: siren");
        Expect(Look(600) == BreakAlarmVerdict.Siren, "it keeps sounding the siren while nothing records");
        Expect(Look(601, collecting: true) == BreakAlarmVerdict.Off, "a take starts: silent");
        Expect(p.IdleSeconds(601) == null, "recording: no clock");
        Expect(Look(2000, collecting: true) == BreakAlarmVerdict.Off, "a long take never alarms");

        Expect(Look(2001) == BreakAlarmVerdict.Counting, "the take ends (good or failed): the clock starts from its end");
        Expect(Look(2030) == BreakAlarmVerdict.Counting, "29 s between takes: quiet");
        Expect(Math.Abs(p.IdleSeconds(2030)!.Value - 29) < 1e-9, "the idle seconds count from the take's end");
        Expect(Look(2035, collecting: true) == BreakAlarmVerdict.Off, "next take within the grace: never sounded");

        // A refused Start never leaves idle: the clock keeps the last take's end (Pod 1, 2026-11-24:
        // 89 s idle with refusals and no sound on build 96).
        Expect(Look(2100) == BreakAlarmVerdict.Counting, "a fresh idle spell starts at the take's end");
        Expect(Look(2120) == BreakAlarmVerdict.Counting, "refused starts in between change nothing");
        Expect(Look(2145) == BreakAlarmVerdict.Soft, "45 s after the last take: soft beep, whatever was refused");
        Expect(Look(2160) == BreakAlarmVerdict.Loud, "60 s: loud beep");
        Expect(Look(2189.9) == BreakAlarmVerdict.Loud, "89.9 s: still the loud beep");
        Expect(Look(2190) == BreakAlarmVerdict.Siren, "90 s: siren");
        Expect(Look(2191, collecting: true) == BreakAlarmVerdict.Off, "only a take silences it");

        Console.WriteLine(failures == 0 ? "ALL PASS break alarm" : failures + " FAILED break alarm");
        return failures == 0 ? 0 : 1;
    }
}
