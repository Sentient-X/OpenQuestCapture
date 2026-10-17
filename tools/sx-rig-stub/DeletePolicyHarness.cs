// Host check of RecordingDeletePolicy: only an OQC session directory directly under the files
// root can be deleted; rig_staging, configs and traversal never; an unsettled rig episode is
// refused; a sealed one needs confirm=<episode_id>. Run by test_binding.sh.
using System;
using System.IO;
using RealityLog.Common;

static class DeletePolicyHarness
{
    static int failures;
    static void Expect(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) failures++; }

    static int Main()
    {
        var root = Path.Combine(Path.GetTempPath(), "oqc-delete-" + Guid.NewGuid().ToString("N"));
        var staging = Path.Combine(root, "rig_staging");
        void Session(string name, string? seal)
        {
            Directory.CreateDirectory(Path.Combine(root, name));
            if (seal == "none") return;
            Directory.CreateDirectory(Path.Combine(staging, name));
            if (seal != null) File.WriteAllText(Path.Combine(staging, name, "seal.json"), "{\"state\":\"" + seal + "\",\"detail\":\"\"}");
        }
        Session("20261017_100000", "none");     // recorded without the rig
        Session("20261017_110000", "sealed");
        Session("20261017_120000", "refused");
        Session("20261017_130000", "pending");
        Session("20261017_140000", null);        // rig episode, no verdict yet (recording / queued)
        File.WriteAllText(Path.Combine(root, "pod_config.json"), "{}");

        DeleteVerdict V(string name, string? confirm = null) => RecordingDeletePolicy.Check(root, name, confirm).verdict;

        Expect(V("rig_staging") == DeleteVerdict.InvalidName, "rig_staging is never deletable");
        Expect(V("pod_config.json") == DeleteVerdict.InvalidName, "a config file is never deletable");
        Expect(V("../20261017_100000") == DeleteVerdict.InvalidName, "traversal is refused");
        Expect(V("rig_staging/20261017_110000") == DeleteVerdict.InvalidName, "a path into rig_staging is refused");
        Expect(V(root) == DeleteVerdict.InvalidName && V("") == DeleteVerdict.InvalidName, "an absolute path or empty name is refused");
        Expect(V("20261017_999999") == DeleteVerdict.NotFound, "a missing session is not found");
        var (verdict, _, path) = RecordingDeletePolicy.Check(root, "20261017_100000", null);
        Expect(verdict == DeleteVerdict.Allowed && path == Path.Combine(Path.GetFullPath(root), "20261017_100000"),
            "a session without the rig is deletable, at its own path");
        Expect(V("20261017_120000") == DeleteVerdict.Allowed, "a session whose rig episode was refused is deletable");
        Expect(V("20261017_130000") == DeleteVerdict.RigUnsettled, "a pending rig episode refuses the delete");
        Expect(V("20261017_140000") == DeleteVerdict.RigUnsettled, "a rig episode with no verdict yet refuses the delete");
        Expect(V("20261017_110000") == DeleteVerdict.NeedsConfirm, "a sealed rig episode needs confirmation");
        Expect(V("20261017_110000", "20261017_120000") == DeleteVerdict.NeedsConfirm, "the confirmation must name this episode");
        Expect(V("20261017_110000", "20261017_110000") == DeleteVerdict.Allowed, "confirm=<episode_id> allows it");

        Directory.Delete(root, true);
        Console.WriteLine(failures == 0 ? "ALL PASS" : failures + " FAILED");
        return failures;
    }
}
