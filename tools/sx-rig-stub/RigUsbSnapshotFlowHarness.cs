#nullable enable
// Runs the production snapshot/apply/request protocol used by RigCoordinator, with a fake
// Android bridge preserving refresh/release's shared monitor. This covers the caller flow
// missing from the original helper-only test; no Unity/JNI behavior is fabricated as tested.
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using RealityLog.Rig;

static class RigUsbSnapshotFlowHarness
{
    static int failures;
    static void Expect(bool ok, string what)
    {
        Console.WriteLine((ok ? "PASS " : "FAIL ") + what);
        if (!ok) failures++;
    }

    static RigUsbRefresh.Result Await(RigUsbRefresh worker)
    {
        RigUsbRefresh.Result? result = null;
        if (!SpinWait.SpinUntil(() => (result = worker.Take()) != null, 3000))
            throw new Exception("worker did not complete");
        return result!;
    }

    static int Main(string[] args)
    {
        var flow = new RigUsbSnapshotFlow();
        int applied = 0;
        flow.SnapshotChanged();
        flow.ApplyIfNeeded(false, () => applied++);
        Expect(applied == 0, "a snapshot received before rig open remains unapplied");
        flow.ApplyIfNeeded(true, () => applied++);
        flow.ApplyIfNeeded(true, () => applied++);
        Expect(applied == 1, "initial open applies retained live descriptors once");
        flow.RigClosed();
        flow.ApplyIfNeeded(false, () => applied++);
        flow.ApplyIfNeeded(true, () => applied++);
        flow.ApplyIfNeeded(true, () => applied++);
        Expect(applied == 2, "native reopen reapplies descriptors once");

        var bridgeLock = new object();
        using var broadcastEntered = new ManualResetEventSlim(false);
        using var finishBroadcast = new ManualResetEventSlim(false);
        using var releaseEntered = new ManualResetEventSlim(false);
        using var secondReadEntered = new ManualResetEventSlim(false);
        using var finishSecondRead = new ManualResetEventSlim(false);
        using var closed = new ManualResetEventSlim(false);
        int reads = 0, releases = 0;
        bool detached = false, releasedWhileAttached = false;
        int mainThread = Thread.CurrentThread.ManagedThreadId, releaseThread = mainThread;
        var worker = new RigUsbRefresh(() =>
        {
            lock (bridgeLock)
            {
                if (Interlocked.Increment(ref reads) == 1) return "gone";
                secondReadEntered.Set();
                if (!finishSecondRead.Wait(3000)) throw new Exception("read timeout");
                return "empty";
            }
        }, () => closed.Set(), key =>
        {
            releaseEntered.Set();
            lock (bridgeLock)
            {
                releaseThread = Thread.CurrentThread.ManagedThreadId;
                releasedWhileAttached |= !detached;
                Interlocked.Increment(ref releases);
            }
        });

        // Exactly the two production flow calls made by Poll, and the apply call made
        // after ConsumeUsbRefresh parses a new snapshot. The apply callback stands in
        // for native detach; QueueRelease is the actual coordinator Release operation.
        void ApplyGone()
        {
            applied++;
            detached = true;
            flow.QueueRelease("old-key");
            flow.QueueRelease("old-key"); // detach plus pendingRelease: one actual close
        }
        void Poll() { flow.ApplyIfNeeded(true, ApplyGone); flow.Request(worker); }

        flow.Request(worker);
        Expect(Await(worker).Json == "gone", "first refresh supplies vanished descriptor");
        flow.SnapshotChanged();
        flow.ApplyIfNeeded(true, ApplyGone);
        int afterApply = applied;
        var broadcast = new Thread(() =>
        {
            lock (bridgeLock)
            {
                broadcastEntered.Set();
                finishBroadcast.Wait(3000);
            }
        });
        broadcast.Start();
        Expect(broadcastEntered.Wait(3000), "independent Android-style broadcast owns bridge monitor");
        Poll();
        Expect(releaseEntered.Wait(3000) && releases == 0, "worker release waits behind broadcast");
        var timer = Stopwatch.StartNew();
        for (int i = 0; i < 500; i++) Poll();
        Expect(timer.ElapsedMilliseconds < 100 && applied == afterApply,
            "500 coordinator polls do not reapply stale release or block on broadcast");
        finishBroadcast.Set();
        broadcast.Join();
        Expect(secondReadEntered.Wait(3000), "next enumeration starts after queued release");
        timer.Restart();
        for (int i = 0; i < 500; i++) Poll();
        Expect(timer.ElapsedMilliseconds < 100 && applied == afterApply,
            "500 coordinator polls do not reapply stale snapshot behind slow next read");
        Expect(releases == 1 && releaseThread != mainThread && !releasedWhileAttached,
            "descriptor release happens exactly once on worker after native detach");
        worker.Dispose();
        Expect(!closed.IsSet, "shutdown does not close bridge during active enumeration");
        finishSecondRead.Set();
        Expect(closed.Wait(3000) && worker.Take() == null, "shutdown closes after read and discards late snapshot");

        // Wiring assertions: the coordinator must use this tested protocol instead of
        // retaining an independent direct SyncAttachments/release path.
        var source = File.ReadAllText(args[0]);
        Expect(!source.Contains("SyncAttachments();")
            && source.Contains("usbFlow.ApplyIfNeeded(opened, SyncAttachments);\n            if (usbRefresh != null) usbFlow.Request(usbRefresh);"),
            "Poll applies through tested one-shot protocol before requesting next read");
        Expect(source.Contains("private void Release(string key) => usbFlow.QueueRelease(key);")
            && !source.Contains("bridge?.Call(\"release\""), "coordinator has no synchronous bridge release");
        Expect(source.Contains("usbFlow.SnapshotChanged();") && source.Contains("usbFlow.RigClosed();")
            && source.Contains("snapshot.pendingRelease = Array.Empty<string>();"),
            "fresh-snapshot and reopen wiring retain one-shot release ownership");

        Console.WriteLine(failures == 0 ? "ALL PASS" : failures + " FAILED");
        return failures;
    }
}
