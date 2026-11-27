// Host check of Assets/RealityLog/Scripts/Runtime/Rig/BreakAlarmPolicy.cs: any running headset with no
// take for 45 s since the last one ended (good or failed) sounds, climbing to a loud beep at 60 s and
// the siren at 90 s, until a take records. Signing in or out, the headset on or off and a rig refusing
// Start change nothing. And of BreakAlarmVoice.cs: what it says is "Pod N, start now" for the pod number
// in the headset's pod name, read out of the shipped Resources/sx_break_alarm_voice.bytes (its path is
// the first argument). Run by test_binding.sh; not part of the Unity project.
using System;
using System.IO;
using RealityLog.Rig;

static class BreakAlarmHarness
{
    static int failures;
    static bool Loud(float[] samples)
    {
        double sum = 0;
        foreach (var v in samples) sum += v * v;
        return Math.Sqrt(sum / samples.Length) > 0.05;
    }

    static bool Same(float[] a, float[] b)
    {
        if (a.Length != b.Length) return false;
        for (var i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }

    static void Expect(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) failures++; }

    static int Main(string[] args)
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
        // The pod number comes from the pod name; these are names on the floor on 2026-11-25.
        Expect(BreakAlarmVoice.PodNumber("Pod 1") == 1 && BreakAlarmVoice.PodNumber("Pod 11") == 11 && BreakAlarmVoice.PodNumber("Pod 20") == 20, "\"Pod 11\" is pod 11");
        Expect(BreakAlarmVoice.PodNumber("pod6") == 6 && BreakAlarmVoice.PodNumber("pod4-quest") == 4, "\"pod6\" and \"pod4-quest\" are pods 6 and 4");
        Expect(BreakAlarmVoice.PodNumber("pod5-quest-3") == 5, "\"pod5-quest-3\" is pod 5: the number right after the word");
        Expect(BreakAlarmVoice.PodNumber("Pod 6 (Pi)") == 6 && BreakAlarmVoice.PodNumber("POD-07") == 7, "\"Pod 6 (Pi)\" and \"POD-07\" are pods 6 and 7");
        Expect(BreakAlarmVoice.PodNumber("Nikita's pod") == null && BreakAlarmVoice.PodNumber("pico-teleop") == null, "a name with no number after \"pod\" has none");
        Expect(BreakAlarmVoice.PodNumber("") == null && BreakAlarmVoice.PodNumber(null) == null, "no name, no number");
        Expect(BreakAlarmVoice.PodNumber("Pod 0") == null && BreakAlarmVoice.PodNumber("Pod 100") == null && BreakAlarmVoice.PodNumber("Pod 99") == 99, "only 1 to 99 are pod numbers");
        Expect(BreakAlarmVoice.PodNumber("Room 3 pod 12") == 12, "a number elsewhere in the name is not the pod's");

        // The shipped recording holds a phrase for every pod number and one with no number.
        var file = File.ReadAllBytes(args[0]);
        var every = true;
        for (var pod = 1; pod <= 99; pod++)
        {
            var said = BreakAlarmVoice.Read(file, "Pod " + pod);
            every &= said is BreakAlarmVoice.Phrase ph && ph.Pod == pod && ph.Rate == 16000
                && ph.Samples.Length > 0.8 * ph.Rate && ph.Samples.Length < 3.5 * ph.Rate && Loud(ph.Samples);
        }
        Expect(every, "the shipped recording has a phrase of 0.8 to 3.5 s, not silent, for each of pods 1 to 99");
        var eleven = BreakAlarmVoice.Read(file, "Pod 11");
        Expect(eleven?.Words == "Pod 11, start now", "pod 11 says \"Pod 11, start now\"");
        var nameless = BreakAlarmVoice.Read(file, "Nikita's pod");
        Expect(nameless is BreakAlarmVoice.Phrase n && n.Pod == null && n.Words == "Start now" && Loud(n.Samples), "a pod with no number says \"Start now\"");
        Expect(BreakAlarmVoice.Read(file, "") is BreakAlarmVoice.Phrase u && u.Pod == null, "a headset not yet enrolled says \"Start now\"");
        var two = BreakAlarmVoice.Read(file, "Pod 2");
        var twelve = BreakAlarmVoice.Read(file, "Pod 12");
        Expect(two is BreakAlarmVoice.Phrase a && twelve is BreakAlarmVoice.Phrase b && !Same(a.Samples, b.Samples), "pods 2 and 12 say different phrases");

        // A file that is not the recording gives no phrase (the alarm then beeps as before), never a throw.
        Expect(BreakAlarmVoice.Read(null, "Pod 1") == null && BreakAlarmVoice.Read(new byte[0], "Pod 1") == null, "no file: no phrase");
        Expect(BreakAlarmVoice.Read(new byte[64], "Pod 1") == null, "not the recording: no phrase");
        var cut = new byte[file.Length / 2];
        Array.Copy(file, cut, cut.Length);
        Expect(BreakAlarmVoice.Read(cut, "Pod 99") == null, "a recording cut short: no phrase for a pod past the cut");
        var head = new byte[20];
        Array.Copy(file, head, head.Length);
        Expect(BreakAlarmVoice.Read(head, "Pod 1") == null, "a recording cut inside its table: no phrase");

        Console.WriteLine(failures == 0 ? "ALL PASS break alarm" : failures + " FAILED break alarm");
        return failures == 0 ? 0 : 1;
    }
}
