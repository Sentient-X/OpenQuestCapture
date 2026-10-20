// Host check of RecordingListRefreshGate: the recordings list never scans or rebuilds while an
// episode records (a request then waits for the stop), requests during a scan collapse into one
// more scan, and a scan that ends after a recording began is not applied. Run by test_binding.sh.
using System;
using RealityLog.Common;

static class RecordingListRefreshGateHarness
{
    static int failures;
    static void Expect(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) failures++; }

    static int Main()
    {
        var g = new RecordingListRefreshGate();
        Expect(g.Request(recording: false), "idle: a request scans at once");
        Expect(!g.Request(false) && !g.Request(false), "requests while scanning do not start a second scan");
        var (apply, again) = g.Completed(false);
        Expect(apply && again, "the scan is shown, then exactly one more scan covers the requests made meanwhile");
        (apply, again) = g.Completed(false);
        Expect(apply && !again, "that one rescan ends it");

        // The 2026-10-20 failure: a start closed the open menu, the user reopened and closed it,
        // each rebuilding 118 rows while the pose loggers waited on the main thread.
        g = new RecordingListRefreshGate();
        Expect(!g.Request(recording: true) && g.Deferred, "recording: a request is deferred, nothing scans");
        Expect(!g.Request(true) && !g.Request(true) && !g.Scanning, "more requests while recording still scan nothing");
        Expect(!g.Tick(recording: true), "no scan while the episode records or stops");
        Expect(g.Tick(recording: false) && !g.Deferred && g.Scanning, "the recording ended: the deferred requests become one scan");
        Expect(!g.Tick(false), "and only one");
        (apply, again) = g.Completed(false);
        Expect(apply && !again, "its result is shown");

        g = new RecordingListRefreshGate();
        Expect(g.Request(false), "a scan starts while idle");
        (apply, again) = g.Completed(recording: true);
        Expect(!apply && !again && g.Deferred, "a recording began meanwhile: the result is not applied, the refresh waits");
        Expect(g.Tick(false), "and runs after the stop");

        Expect(RecordingListRefreshGate.RowsPerFrame > 0 && RecordingListRefreshGate.RowsPerFrame <= 8,
            "rows are built a few per frame, never 118 in one");

        Console.WriteLine(failures == 0 ? "ALL PASS" : failures + " FAILED");
        return failures;
    }
}
