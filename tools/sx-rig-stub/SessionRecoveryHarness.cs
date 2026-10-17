// Host check of SessionRecovery and StreamManifest: a session a killed process left without
// stream_manifest.json is closed out once, by the stop's own writer, listing exactly the
// stop's streams with rows counted from the files, plus only interrupted and
// recovered_at_utc; complete and active sessions are left alone.
// Run by test_binding.sh; not part of the Unity project.
using System;
using System.IO;
using System.Text.RegularExpressions;
using RealityLog;

static class SessionRecoveryHarness
{
    static int failures;
    static void Expect(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) failures++; }

    static int Main()
    {
        // The stop path's streams, in its order (pose loggers, IMU, body).
        var streams = new[] { "hmd_poses.csv", "left_controller_poses.csv", "right_controller_poses.csv", "imu.csv", "body_tracking.csv" };
        var root = Path.Combine(Path.GetTempPath(), "oqc-recovery-" + Guid.NewGuid().ToString("N"));
        var killed = Path.Combine(root, "20261017_153232");
        var complete = Path.Combine(root, "20261017_150000");
        var active = Path.Combine(root, "20261017_160000");
        foreach (var d in new[] { killed, complete, active, Path.Combine(root, "rig_staging") }) Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(killed, "imu.csv"), "t,ax\n1,0\n2,0\n3,0\n4,"); // torn last line
        File.WriteAllText(Path.Combine(killed, "hmd_poses.csv"), "t,x\n1,0\n");
        File.WriteAllText(Path.Combine(killed, "body_tracking.csv"), "");
        File.WriteAllText(Path.Combine(killed, "left_camera_timestamps.csv"), "i,t\n0,1\n1,2\n");
        File.WriteAllText(Path.Combine(killed, "extra_debug.csv"), "a\n1\n");
        File.WriteAllText(Path.Combine(complete, "stream_manifest.json"), "{\"schema\":1,\"streams\":[]}");

        var closed = SessionRecovery.CloseOut(root, "20261017_160000", streams);
        Expect(closed.Count == 1 && closed[0] == "20261017_153232", "only the killed session is closed out: " + string.Join(",", closed));
        var manifest = File.ReadAllText(Path.Combine(killed, "stream_manifest.json"));

        // One shape: the stop's document with these rows, plus only interrupted and recovered_at_utc.
        var expectedStreams = StreamManifest.StreamsJson(new[]
        {
            new StreamEntry("hmd_poses.csv", 1, "{}"),
            new StreamEntry("left_controller_poses.csv", 0, "{}"),
            new StreamEntry("right_controller_poses.csv", 0, "{}"),
            new StreamEntry("imu.csv", 3, "{}"),
            new StreamEntry("body_tracking.csv", 0, "{}"),
        });
        var m = Regex.Match(manifest, "^\\{\"schema\":1,\"streams\":(\\[.*\\]),\"interrupted\":true,\"recovered_at_utc\":\"[0-9T:.\\-]+Z\"\\}$");
        Expect(m.Success, "the manifest is the stop's shape plus only interrupted and recovered_at_utc: " + manifest);
        Expect(m.Success && m.Groups[1].Value == expectedStreams, "exactly the stop's streams, rows from the files (torn line not counted, missing file 0)");
        Expect(!manifest.Contains("camera") && !manifest.Contains("extra_debug"), "no camera sidecars or streams the stop would not list");
        Expect(StreamManifest.Document(expectedStreams) == "{\"schema\":1,\"streams\":" + expectedStreams + "}", "the stop's document is unchanged");

        Expect(SessionRecovery.IsInterrupted(killed) && !SessionRecovery.IsInterrupted(complete), "interrupted is told apart from complete");
        Expect(!File.Exists(Path.Combine(active, "stream_manifest.json")), "the active session is left alone");
        Expect(File.ReadAllText(Path.Combine(complete, "stream_manifest.json")) == "{\"schema\":1,\"streams\":[]}", "a complete session is untouched");
        Expect(SessionRecovery.CloseOut(root, null, streams).Count == 1, "a second launch closes out only what is still open (the formerly active one)");
        Expect(!File.Exists(Path.Combine(root, "rig_staging", "stream_manifest.json")), "non-session directories are ignored");

        Directory.Delete(root, true);
        Console.WriteLine(failures == 0 ? "ALL PASS" : failures + " FAILED");
        return failures;
    }
}
