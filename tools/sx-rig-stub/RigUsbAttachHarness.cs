#nullable enable
// Regression for build 105 (Pod 7, 2026-11-26): an encoder replug ran native attach/detach on
// Unity's thread, behind libsx_rig's global rig lock and a USB identity read, and the body poll
// in FixedUpdate went 822 ms without a sample. Runs the production RigUsbRefresh worker with the
// production RigUsbAttachments ledger; the native calls are fakes that hold a "rig lock" the way
// sx_rig_check does. No Unity/JNI behaviour is claimed as tested here.
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using RealityLog.Rig;

static class RigUsbAttachHarness
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
        if (!SpinWait.SpinUntil(() => (result = worker.Take()) != null, 5000))
            throw new Exception("worker did not complete");
        return result!;
    }

    static RigUsbDevice Device(string key) =>
        new() { key = key, deviceName = "/dev/bus/usb/001/" + key, vendorId = 0x303a, productId = 0x1001, fd = 7 };

    static RigUsbSnapshot Snap(string[] keys, params string[] pendingRelease) =>
        new() { devices = keys.Select(Device).ToArray(), present = keys.Length, pendingRelease = pendingRelease };

    static int Main(string[] args)
    {
        int mainThread = Thread.CurrentThread.ManagedThreadId;
        var rigLock = new object();          // libsx_rig's RIG mutex
        var bus = Snap(new[] { "enc-a" });   // what the bridge lists now
        var usb = new RigUsbAttachments();
        var log = new System.Collections.Concurrent.ConcurrentQueue<string>();
        int attachThread = mainThread, detachThread = mainThread, releaseThread = mainThread;
        var failKeys = new System.Collections.Concurrent.ConcurrentDictionary<string, bool>();
        var attachEntered = new ManualResetEventSlim(false);

        Func<RigUsbDevice, string?> Attach = d =>
        {
            attachEntered.Set();
            lock (rigLock)
            {
                attachThread = Thread.CurrentThread.ManagedThreadId;
                Thread.Sleep(30); // the identity read over USB
                log.Enqueue("attach " + d.key);
                return failKeys.ContainsKey(d.key) ? "{\"code\":\"device\",\"detail\":\"no identity\"}" : null;
            }
        };
        Action<RigUsbDevice> Detach = d =>
        {
            lock (rigLock)
            {
                detachThread = Thread.CurrentThread.ManagedThreadId;
                log.Enqueue("detach " + d.key);
            }
        };
        var closed = new ManualResetEventSlim(false);
        var worker = new RigUsbRefresh(() => "bus", () => { log.Enqueue("close"); closed.Set(); },
            _ => usb.Sync(Volatile.Read(ref bus)!, Attach, Detach),
            key => { releaseThread = Thread.CurrentThread.ManagedThreadId; log.Enqueue("release " + key); });

        RigUsbRefresh.Result Cycle() { if (!worker.Request()) throw new Exception("request refused"); return Await(worker); }

        // 1. Nothing touches the rig before it opens.
        Cycle();
        Expect(log.IsEmpty && usb.Count == 0, "a closed rig gets no attach");

        // 2. A replug while a check holds the rig for 1.5 s: the caller never waits.
        usb.Opened();
        var checkHolds = new ManualResetEventSlim(false);
        var check = new Thread(() => { lock (rigLock) { checkHolds.Set(); Thread.Sleep(1500); } });
        check.Start();
        checkHolds.Wait();
        attachEntered.Reset();
        Expect(worker.Request(), "main thread asks for a cycle while the rig is held");
        Expect(attachEntered.Wait(3000), "the worker reached native attach");
        var caller = Stopwatch.StartNew();
        long worst = 0;
        for (int i = 0; i < 500; i++)
        {
            var t = Stopwatch.GetTimestamp();
            worker.Request(); _ = usb.Count; usb.Attached(); usb.AttachErrors(); _ = usb.SyncInFlight;
            worst = Math.Max(worst, Stopwatch.GetTimestamp() - t);
        }
        double worstMs = worst * 1000.0 / Stopwatch.Frequency;
        Expect(caller.ElapsedMilliseconds < 200 && worstMs < 20,
            $"500 main-thread polls and status reads while attach waits on the rig lock: {caller.ElapsedMilliseconds} ms total, worst {worstMs:F2} ms");
        Await(worker);
        check.Join();
        Expect(attachThread != mainThread && usb.Count == 1, "native attach ran on the USB worker, not the caller");

        // 3. Unplug: detach on the worker, then release, in that order.
        while (log.TryDequeue(out _)) { }
        Volatile.Write(ref bus, Snap(Array.Empty<string>(), "enc-a"));
        Cycle();
        var order = log.ToArray();
        Expect(order.SequenceEqual(new[] { "detach enc-a", "release enc-a" }),
            "vanished device detached before its fd is released, released once: " + string.Join(", ", order));
        Expect(detachThread != mainThread && releaseThread != mainThread && usb.Count == 0,
            "detach and release ran on the USB worker");

        // 4. An attach error is final for its key; its fd is released when it vanishes; a replug is a new key.
        while (log.TryDequeue(out _)) { }
        failKeys["enc-b"] = true;
        Volatile.Write(ref bus, Snap(new[] { "enc-b" }));
        Cycle(); Cycle(); Cycle();
        Expect(log.Count(l => l == "attach enc-b") == 1 && usb.AttachErrors().Length == 1 && usb.Count == 0,
            "a failed attach is not retried for the same key");
        Volatile.Write(ref bus, Snap(new[] { "enc-b2" }, "enc-b"));
        Cycle();
        Expect(log.Contains("release enc-b") && log.Contains("attach enc-b2") && usb.Count == 1
            && usb.AttachErrors().Length == 0, "a never-attached vanished fd is released; the replug is tried afresh");

        // 5. The rig closes while a cycle is inside native attach: nothing stale is committed,
        //    the rig cannot reopen meanwhile, and the next cycle attaches to the reopened rig.
        usb.Closed();
        usb.Opened();
        while (log.TryDequeue(out _)) { }
        var holdAttach = new ManualResetEventSlim(false);
        attachEntered.Reset();
        var slowUsb = usb;
        var slowWorker = new RigUsbRefresh(() => "bus", () => { },
            _ => slowUsb.Sync(Snap(new[] { "cam-l" }), d => { attachEntered.Set(); holdAttach.Wait(3000); return null; }, Detach),
            _ => { });
        slowWorker.Request();
        Expect(attachEntered.Wait(3000) && usb.SyncInFlight, "a cycle is in flight inside native attach");
        usb.Closed();
        Expect(usb.SyncInFlight, "close does not wait for it; a reopen sees the cycle in flight and defers");
        var waiter = new Thread(() => usb.WaitForSync());
        waiter.Start();
        holdAttach.Set();
        Await(slowWorker);
        Expect(waiter.Join(3000) && !usb.SyncInFlight, "WaitForSync returns when the native call does");
        Expect(usb.Count == 0 && usb.AttachErrors().Length == 0, "the stale cycle committed nothing after close");
        usb.Opened();
        Volatile.Write(ref bus, Snap(new[] { "enc-b2" }));
        Cycle();
        Expect(usb.Count == 1 && log.Contains("attach enc-b2"), "the reopened rig gets every listed device again");
        slowWorker.Dispose();

        // 6. Quit while a cycle is inside native attach: ShutDown returns what to detach and the
        //    late attach is not committed; the bridge closes only after that cycle returns.
        holdAttach.Reset(); attachEntered.Reset();
        var quitUsb = new RigUsbAttachments();
        quitUsb.Opened();
        quitUsb.Sync(Snap(new[] { "enc-a" }), _ => null, _ => { });
        var quitClosed = new ManualResetEventSlim(false);
        var quitWorker = new RigUsbRefresh(() => "bus", () => quitClosed.Set(),
            _ => quitUsb.Sync(Snap(new[] { "enc-a", "enc-c" }), d => { attachEntered.Set(); holdAttach.Wait(3000); return null; }, _ => { }),
            _ => { });
        quitWorker.Request();
        attachEntered.Wait(3000);
        caller.Restart();
        var toDetach = quitUsb.ShutDown();
        quitWorker.Dispose();
        Expect(caller.ElapsedMilliseconds < 50 && toDetach.Length == 1 && toDetach[0].key == "enc-a",
            "quit returns at once with the attached devices to detach");
        Expect(!quitClosed.IsSet, "the bridge stays open while the in-flight attach uses its fd");
        holdAttach.Set();
        Expect(quitClosed.Wait(3000) && quitUsb.Count == 0, "bridge closes after the cycle; nothing committed after quit");
        Expect(quitUsb.Sync(Snap(new[] { "enc-d" }), _ => throw new Exception("attach after quit"), _ => { }).Count == 0,
            "no cycle runs after quit");

        worker.Dispose();
        Expect(closed.Wait(3000), "worker closes the bridge");

        // 7. Wiring: the coordinator reaches native attach/detach only through the worker's apply.
        var source = File.ReadAllText(args[0]);
        string Body(string signature)
        {
            int start = source.IndexOf(signature, StringComparison.Ordinal);
            if (start < 0) return "";
            int brace = source.IndexOf('{', start), depth = 0;
            for (int i = brace; i < source.Length; i++)
            {
                if (source[i] == '{') depth++;
                else if (source[i] == '}' && --depth == 0) return source.Substring(start, i - start + 1);
            }
            return "";
        }
        var apply = Body("private IReadOnlyCollection<string> ApplyUsbSnapshot(string json)");
        var shutDown = Body("public void ShutDown()");
        var outside = source.Replace(apply, "").Replace(shutDown, "");
        Expect(apply.Contains("SxRig.AttachUsb(") && apply.Contains("SxRig.DetachUsb(") && apply.Contains("usb.Sync("),
            "ApplyUsbSnapshot holds the native attach and detach");
        Expect(!Regex.IsMatch(outside, @"SxRig\.(AttachUsb|DetachUsb)\(") && !outside.Contains("usb.Sync("),
            "no other coordinator code calls native attach/detach (only the quit path detaches)");
        Expect(Regex.IsMatch(Body("private void StartUsbRefresh("), @"\},\s*ApplyUsbSnapshot,"),
            "ApplyUsbSnapshot is the USB worker's apply callback");
        Expect(!Body("private void Poll()").Contains("ApplyUsbSnapshot") && !Body("private void ConsumeUsbRefresh()").Contains("ApplyUsbSnapshot")
            && !Body("private void Update()").Contains("ApplyUsbSnapshot"),
            "Update, Poll and ConsumeUsbRefresh never apply a snapshot");
        Expect(Body("private Task<RigRefusal?> StartOpen()").Contains("usb.WaitForSync();")
            && Body("private void CloseRig()").Contains("usb.Closed();") && shutDown.Contains("usb.ShutDown()"),
            "open waits out an in-flight cycle; close and quit go through the ledger");

        // 8. No -1 stands in for a capture counter the app could not read (item 3).
        if (args.Length > 1)
        {
            var camera = File.ReadAllText(args[1]);
            Expect(!Regex.IsMatch(camera, @"FrameCount\s*=\s*-1"), "capture counters are never set to -1");
            Expect(Regex.IsMatch(camera, @"""source_frame_count\\"": \{JsonCount\(sourceFrameCount\)\}")
                && camera.Contains("?? \"null\""), "an unread counter is written as JSON null");
        }

        Console.WriteLine(failures == 0 ? "ALL PASS" : failures + " FAILED");
        return failures;
    }
}
