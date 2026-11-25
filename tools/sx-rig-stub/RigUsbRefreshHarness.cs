// Regression: Android USB enumeration may stall for many pose periods. It must not hold
// Unity's caller, overlap a second enumeration, close its in-flight fd, or publish after stop.
using System;
using System.Diagnostics;
using System.Threading;
using RealityLog.Rig;

static class RigUsbRefreshHarness
{
    static int failures;
    static void Expect(bool ok, string what)
    {
        Console.WriteLine((ok ? "PASS " : "FAIL ") + what);
        if (!ok) failures++;
    }

    static RigUsbRefresh.Result Await(RigUsbRefresh refresh)
    {
        RigUsbRefresh.Result? result = null;
        if (!SpinWait.SpinUntil(() => (result = refresh.Take()) != null, 3000))
            throw new Exception("refresh did not finish");
        return result!;
    }

    static int Main()
    {
        int main = Thread.CurrentThread.ManagedThreadId;
        var started = new ManualResetEventSlim(false);
        var unblock = new ManualResetEventSlim(false);
        var closed = new ManualResetEventSlim(false);
        int calls = 0, closes = 0, readThread = main;
        var refresh = new RigUsbRefresh(() =>
        {
            Interlocked.Increment(ref calls);
            readThread = Thread.CurrentThread.ManagedThreadId;
            started.Set();
            if (!unblock.Wait(3000)) throw new Exception("test did not release reader");
            return "first";
        }, () => { Interlocked.Increment(ref closes); closed.Set(); });

        Expect(refresh.Request(), "first request accepted");
        Expect(started.Wait(3000) && readThread != main, "USB enumeration executes on worker");
        var caller = Stopwatch.StartNew();
        for (int i = 0; i < 500; i++)
        {
            if (refresh.Request() || refresh.Take() != null) failures++;
        }
        Expect(caller.ElapsedMilliseconds < 250 && calls == 1,
            "500 caller polls continue while USB is blocked; no overlapping refresh");
        Thread.Sleep(25); // exercise the bounded slow-read measurement, not a scheduling assertion
        unblock.Set();
        var result = Await(refresh);
        Expect(result.Json == "first" && result.Error == null, "completed snapshot delivered unchanged");
        Expect(refresh.StatsJson().Contains("\"completed\":1") && refresh.StatsJson().Contains("\"over_20ms\":1"),
            "bounded stats retain worker count and slow read");
        refresh.Dispose();
        refresh.Dispose();
        Expect(closed.Wait(3000) && closes == 1 && !refresh.Request(), "idle stop closes once and rejects further work");

        started.Reset(); unblock.Reset(); closed.Reset();
        refresh = new RigUsbRefresh(() =>
        {
            started.Set();
            if (!unblock.Wait(3000)) throw new Exception("test did not release reader");
            return "late";
        }, () => closed.Set());
        refresh.Request();
        Expect(started.Wait(3000), "shutdown case has an active USB read");
        caller.Restart();
        refresh.Dispose();
        Expect(caller.ElapsedMilliseconds < 250 && !closed.IsSet,
            "shutdown returns while USB is blocked and leaves its connection open");
        unblock.Set();
        Expect(closed.Wait(3000) && refresh.Take() == null,
            "shutdown closes after read and discards its late snapshot");

        closed.Reset(); calls = 0;
        refresh = new RigUsbRefresh(() =>
        {
            if (Interlocked.Increment(ref calls) == 1) throw new InvalidOperationException("USB service stalled");
            return "recovered";
        }, () => closed.Set());
        refresh.Request();
        result = Await(refresh);
        Expect(result.Error is InvalidOperationException && result.Json == null,
            "read failure reaches caller without fabricating an empty bus");
        Expect(refresh.Request() && Await(refresh).Json == "recovered", "next poll recovers after USB error");
        Expect(refresh.StatsJson().Contains("\"failed\":1"), "failed read counted");
        refresh.Dispose();
        Expect(closed.Wait(3000), "recovered worker closes");

        // Exercise repeated loop back-edges before shutdown. On Quest this also
        // needs the generated C++ scope audit: Mono does not reproduce the IL2CPP
        // cleanup emitted on each iteration of the former try/finally loop.
        closed.Reset(); calls = 0; closes = 0;
        int releases = 0, applies = 0;
        bool releaseAfterApply = true;
        refresh = new RigUsbRefresh(() =>
        {
            Expect(!closed.IsSet && Volatile.Read(ref releases) == Math.Max(0, Volatile.Read(ref calls) - 1),
                "connection stays open and the previous cycle's release precedes this read");
            return Interlocked.Increment(ref calls).ToString();
        }, () => { Interlocked.Increment(ref closes); closed.Set(); },
        json =>
        {
            Interlocked.Increment(ref applies);
            return json == "1" ? Array.Empty<string>() : new[] { "detached-device" };
        },
        _ =>
        {
            releaseAfterApply &= Volatile.Read(ref applies) == Volatile.Read(ref calls);
            Interlocked.Increment(ref releases);
        });
        bool repeated = true;
        for (int i = 1; i <= 100; i++)
        {
            repeated &= refresh.Request();
            repeated &= Await(refresh).Json == i.ToString() && !closed.IsSet;
        }
        Expect(repeated && calls == 100 && applies == 100 && releases == 99 && closes == 0 && releaseAfterApply,
            "100 cycles apply then release in order and retain bridge ownership");
        refresh.Dispose();
        Expect(closed.Wait(3000) && closes == 1 && !refresh.Request(),
            "repeated-refresh worker closes exactly once at shutdown");

        Console.WriteLine(failures == 0 ? "ALL PASS" : failures + " FAILED");
        return failures;
    }
}
