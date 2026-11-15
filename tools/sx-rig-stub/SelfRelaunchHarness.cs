// Host check of SelfRelaunchPolicy and SelfRelaunchLedger
// (Assets/RealityLog/Scripts/Runtime/Rig/SelfRelaunch.cs): the app relaunches itself only for a
// head pose frozen long enough, never while anything records, arms or seals, and at most once per
// interval across launches. Run by test_binding.sh; not part of the Unity project.
using System;
using System.IO;
using RealityLog.Rig;

static class SelfRelaunchHarness
{
    static int failures;
    static void Expect(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) failures++; }

    static int Main()
    {
        var utc = new DateTime(2026, 11, 15, 10, 0, 0, DateTimeKind.Utc);
        var policy = new SelfRelaunchPolicy();
        Expect(policy.Observe(0, utc, 15, true, null, null) == RelaunchVerdict.Healthy, "fresh head poses: healthy");
        Expect(policy.Observe(1, utc, null, true, null, null) == RelaunchVerdict.Healthy,
            "no pose since launch (not yet worn) is not a freeze");
        Expect(policy.Observe(2, utc, 900, true, null, null) == RelaunchVerdict.Healthy, "under a second old: healthy");
        Expect(policy.Observe(3, utc, 2000, true, null, null) == RelaunchVerdict.Waiting, "frozen 2 s: waiting");
        Expect(policy.Observe(4, utc, 3000, false, null, null) == RelaunchVerdict.Healthy, "a paused app is never frozen");
        Expect(policy.Observe(5, utc, 2000, true, null, null) == RelaunchVerdict.Waiting, "the freeze is measured again after a pause");
        Expect(policy.Observe(14, utc, 11000, true, null, null) == RelaunchVerdict.Waiting, "11 s frozen: still waiting");
        Expect(policy.Observe(16, utc, 13000, true, "a recording is running", null) == RelaunchVerdict.Held,
            "frozen 13 s while recording: held, never relaunched");
        Expect(policy.Observe(17, utc, 14000, true, "the seal of 20261115_100000 is being written", null) == RelaunchVerdict.Held,
            "frozen while a seal is written: held");
        Expect(policy.Observe(18, utc, 15000, true, null, utc.AddMinutes(-10)) == RelaunchVerdict.RateLimited,
            "relaunched 10 min ago: rate limited");
        Expect(policy.Observe(19, utc, 16000, true, null, utc.AddMinutes(-31)) == RelaunchVerdict.Relaunch,
            "frozen 16 s, nothing in progress, last relaunch 31 min ago: relaunch");
        Expect(policy.Observe(20, utc, 50, true, null, null) == RelaunchVerdict.Healthy && policy.FrozenForSeconds(20) == null,
            "a pose arrives: healthy, the freeze forgotten");
        var late = new SelfRelaunchPolicy();
        Expect(late.Observe(100, utc, 20000, true, null, null) == RelaunchVerdict.Relaunch,
            "a freeze already 20 s old when first seen counts from the last pose");

        var dir = Path.Combine(Environment.GetEnvironmentVariable("TMPDIR") ?? ".", "self-relaunch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var none = SelfRelaunchLedger.Read(dir);
            Expect(none.Count == 0 && none.LastUtc == null, "no ledger: no relaunches");
            none.Record(dir, utc, "head poses frozen for 13 s");
            var read = SelfRelaunchLedger.Read(dir);
            Expect(read.Count == 1 && read.LastUtc == utc && read.LastReason == "head poses frozen for 13 s", "a relaunch is kept across launches");
            read.Record(dir, utc.AddHours(1), "again");
            Expect(SelfRelaunchLedger.Read(dir).Count == 2, "the count grows");
            File.WriteAllText(Path.Combine(dir, SelfRelaunchLedger.FileName), "{not json");
            Expect(SelfRelaunchLedger.Read(dir).Count == 0, "an unreadable ledger reads as none");
            File.WriteAllText(Path.Combine(dir, SelfRelaunchLedger.FileName), "{\"count\":\"3\",\"last_utc\":5}");
            var odd = SelfRelaunchLedger.Read(dir);
            Expect(odd.Count == 0 && odd.LastUtc == null, "fields of the wrong type read as unsaid");
        }
        finally
        {
            Directory.Delete(dir, true);
        }

        Console.WriteLine(failures == 0 ? "ALL PASS" : failures + " FAILED");
        return failures == 0 ? 0 : 1;
    }
}
