// Host check of SessionRecovery: a session a killed process left without stream_manifest.json
// is closed out once, as interrupted, with rows and frames counted from its files; complete and
// active sessions are left alone. Run by test_binding.sh; not part of the Unity project.
using System;
using System.IO;
using RealityLog;

static class SessionRecoveryHarness
{
    static int failures;
    static void Expect(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) failures++; }

    static int Main()
    {
        var root = Path.Combine(Path.GetTempPath(), "oqc-recovery-" + Guid.NewGuid().ToString("N"));
        var killed = Path.Combine(root, "20261017_153232");
        var complete = Path.Combine(root, "20261017_150000");
        var active = Path.Combine(root, "20261017_160000");
        foreach (var d in new[] { killed, complete, active, Path.Combine(root, "rig_staging") }) Directory.CreateDirectory(d);
        // imu: header + 3 rows + a torn 4th line (no newline, the kill landed mid-write).
        File.WriteAllText(Path.Combine(killed, "imu.csv"), "t,ax\n1,0\n2,0\n3,0\n4,");
        File.WriteAllText(Path.Combine(killed, "hmd_poses.csv"), "t,x\n1,0\n");
        File.WriteAllText(Path.Combine(killed, "body_tracking.csv"), "");
        File.WriteAllText(Path.Combine(killed, "left_camera_timestamps.csv"), "i,t\n0,1\n1,2\n2,3\n");
        File.WriteAllText(Path.Combine(complete, "stream_manifest.json"), "{\"schema\":1,\"streams\":[]}");

        var closed = SessionRecovery.CloseOut(root, "20261017_160000");
        Expect(closed.Count == 1 && closed[0] == "20261017_153232", "only the killed session is closed out: " + string.Join(",", closed));
        var manifest = File.ReadAllText(Path.Combine(killed, "stream_manifest.json"));
        Expect(manifest.Contains("\"interrupted\":true"), "the manifest says interrupted");
        Expect(manifest.Contains("{\"file\":\"imu.csv\",\"rows\":3,\"stats\":{}}"), "a torn last line is not counted");
        Expect(manifest.Contains("{\"file\":\"hmd_poses.csv\",\"rows\":1,"), "pose rows counted");
        Expect(manifest.Contains("{\"file\":\"body_tracking.csv\",\"rows\":0,"), "an empty stream is listed with 0 rows");
        Expect(manifest.Contains("{\"file\":\"left_camera_timestamps.csv\",\"frames\":3}") && !manifest.Contains("\"left_camera_timestamps.csv\",\"rows\""),
            "camera frames come from the timestamp sidecar, not as a stream");
        Expect(SessionRecovery.IsInterrupted(killed) && !SessionRecovery.IsInterrupted(complete), "interrupted is told apart from complete");
        Expect(!File.Exists(Path.Combine(active, "stream_manifest.json")), "the active session is left alone");
        Expect(File.ReadAllText(Path.Combine(complete, "stream_manifest.json")) == "{\"schema\":1,\"streams\":[]}", "a complete session is untouched");
        Expect(SessionRecovery.CloseOut(root, null).Count == 1 && !Directory.Exists(Path.Combine(killed, "stream_manifest.json.tmp")),
            "a second launch closes out only what is still open (here the formerly active one)");
        Expect(!File.Exists(Path.Combine(root, "rig_staging", "stream_manifest.json")), "non-session directories are ignored");

        Directory.Delete(root, true);
        Console.WriteLine(failures == 0 ? "ALL PASS" : failures + " FAILED");
        return failures;
    }
}
