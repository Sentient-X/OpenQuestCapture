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

        // No shift from 01:00 to 07:00 (owner, 2026-11-25): silent, and the clock starts again at 07:00.
        Expect(!BreakAlarmPolicy.Quiet(0) && BreakAlarmPolicy.Quiet(1) && BreakAlarmPolicy.Quiet(6) && !BreakAlarmPolicy.Quiet(7), "quiet from 01:00 until 07:00");
        Expect(!BreakAlarmPolicy.Quiet(13) && !BreakAlarmPolicy.Quiet(19) && !BreakAlarmPolicy.Quiet(23), "every shift hour can sound");
        Expect(p.Observe(3000, false, quiet: false) == BreakAlarmVerdict.Counting, "00:59, idle: counting");
        Expect(p.Observe(3100, false, quiet: false) == BreakAlarmVerdict.Siren, "idle 100 s at 00:59: siren");
        Expect(p.Observe(3101, false, quiet: true) == BreakAlarmVerdict.Off, "01:00: silent at once");
        Expect(p.IdleSeconds(3101) == null, "the night keeps no clock");
        Expect(p.Observe(9000, false, quiet: true) == BreakAlarmVerdict.Off, "all night: silent");
        Expect(p.Observe(9001, false, quiet: false) == BreakAlarmVerdict.Counting, "07:00: the clock starts again");
        Expect(p.Observe(9046, false, quiet: false) == BreakAlarmVerdict.Soft, "45 s after 07:00 with no take: the soft beep");

        // The hardware fault alarm (owner, 2026-11-26): a middling rate slowing to once every 5 s.
        var h = new HardwareAlarmPolicy();
        Expect(!h.Beep(0, faulted: false), "no fault: silent");
        var beeps = new System.Collections.Generic.List<double>();
        for (var t = 100.0; t < 160.0; t += 0.25) if (h.Beep(t, faulted: true)) beeps.Add(t);
        Expect(beeps.Count > 0 && beeps[0] == 100.0, "a fault beeps at once");
        Expect(Math.Abs(beeps[1] - beeps[0] - HardwareAlarmPolicy.FirstEverySeconds) < 0.26, "first wait about 1 s: " + (beeps[1] - beeps[0]));
        var gaps = new System.Collections.Generic.List<double>();
        for (var i = 1; i < beeps.Count; i++) gaps.Add(beeps[i] - beeps[i - 1]);
        var slowing = true;
        for (var i = 1; i < gaps.Count; i++) if (gaps[i] < gaps[i - 1] - 1e-9) slowing = false;
        Expect(slowing, "it only slows down: " + string.Join(", ", gaps));
        Expect(Math.Abs(gaps[gaps.Count - 1] - 5.0) < 0.26, "it settles at once every 5 s: " + gaps[gaps.Count - 1]);
        Expect(!h.Beep(160.1, faulted: false), "the fault clears: silent");
        Expect(h.Beep(170, faulted: true) && !h.Beep(170.5, faulted: true) && h.Beep(171, faulted: true), "a new fault starts again at the middling rate");

        // The volume any alarm holds (owner, 2026-11-26): 70 %, 50 % after five minutes ringing.
        var v = new AlarmVolumePolicy();
        Expect(v.Fraction(0, sounding: false) == null, "no alarm: the collector's volume");
        Expect(v.Fraction(10, sounding: true) == 0.7, "an alarm starts at 70 %");
        Expect(v.Fraction(309.9, sounding: true) == 0.7, "still 70 % at 4:59");
        Expect(v.Fraction(310, sounding: true) == 0.5, "50 % from five minutes ringing");
        Expect(v.Fraction(311, sounding: false) == null && v.Fraction(312, sounding: true) == 0.7, "a break in the ringing starts again at 70 %");

        Console.WriteLine(failures == 0 ? "ALL PASS break alarm" : failures + " FAILED break alarm");
        return failures == 0 ? 0 : 1;
    }
}
