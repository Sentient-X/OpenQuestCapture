#nullable enable
// FrameWatchdog: every main-thread frame over 100 ms is logged with its gen-0 collections and
// placed on the body rows' monotonic clock; the take's count is stream_manifest.json's
// "main_thread", so a body gap is told the app's stall or the tracker's drop. Run by
// test_binding.sh.

using System;
using System.Linq;
using RealityLog;

internal static class FrameWatchdogHarness
{
    private static int failures;

    private static void Expect(bool condition, string what)
    {
        Console.WriteLine((condition ? "PASS " : "FAIL ") + what);
        if (!condition) failures++;
    }

    private static int Main()
    {
        var watch = new FrameWatchdog();
        Expect(watch.Frame(1_000_000_000, 0) == null && watch.End(0) == null, "nothing is watched or reported outside a take");

        const long ms = 1_000_000;
        long t = 5_000 * ms;
        watch.Begin(t, 10);
        var logged = new System.Collections.Generic.List<string>();
        void Frame(long frameMs, int gc)
        {
            t += frameMs * ms;
            if (watch.Frame(t, gc) is string line) logged.Add(line);
        }
        for (var i = 0; i < 100; i++) Frame(14, 10);
        Frame(100, 10);           // exactly the bound: not slow
        Frame(620, 11);           // a stall with a collection in it
        var stallEnd = t;
        Frame(14, 11);
        Frame(180, 11);           // a stall without one
        var report = watch.End(13)!;
        Expect(logged.Count == 2, $"each frame over 100 ms is logged once ({logged.Count})");
        Expect(logged[0] == $"main thread: slow frame 620 ms (gc0 +1) from mono {stallEnd - 620 * ms} to {stallEnd} ns",
            "a slow frame names its length, its collections and its span on the monotonic clock: " + logged[0]);
        Expect(report.StartsWith("{\"slow_frame_ms\":100,\"frames\":104,\"slow_frames\":2,\"slow_frames_with_gc\":1,\"longest_ms\":620,\"gc0_collections\":3,\"slowest\":[")
            && report.Contains($"{{\"start_mono_ns\":{stallEnd - 620 * ms},\"end_mono_ns\":{stallEnd},\"ms\":620,\"gc0\":1}}")
            && report.IndexOf("\"ms\":620", StringComparison.Ordinal) < report.IndexOf("\"ms\":180", StringComparison.Ordinal),
            "the take's report counts frames, slow frames, collections, and lists the slowest first: " + report);
        Expect(watch.End(13) == null, "a take's report is made once");

        watch.Begin(t, 0);
        for (var i = 0; i < FrameWatchdog.Logged + 10; i++) Frame(150, 0);
        var many = watch.End(0)!;
        Expect(many.Contains($"\"slow_frames\":{FrameWatchdog.Logged + 10}") && many.Split(new[] { "start_mono_ns" }, StringSplitOptions.None).Length - 1 == FrameWatchdog.Kept,
            "past the logged bound slow frames are still counted, and the manifest keeps the slowest " + FrameWatchdog.Kept);

        var streams = StreamManifest.StreamsJson(new[] { new StreamEntry("body.csv", 3, "{}") });
        Expect(StreamManifest.Document(streams, null, report) == "{\"schema\":1,\"streams\":" + streams + ",\"main_thread\":" + report + "}"
            && StreamManifest.Document(streams, null, null) == StreamManifest.Document(streams),
            "the stop's manifest carries main_thread; without a report it is the plain stop document");
        return failures == 0 ? 0 : 1;
    }
}
