#nullable enable
using System;
using System.Diagnostics;
using System.Threading;
using Newtonsoft.Json.Linq;
using RealityLog.Rig;

internal static class StatusSnapshotHarness
{
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        Console.WriteLine("PASS " + message);
    }

    private static void WaitFor(Func<bool> condition) =>
        Require(SpinWait.SpinUntil(condition, 3000), "worker publishes within timeout");

    private static void Main()
    {
        var caller = Thread.CurrentThread.ManagedThreadId;
        var entered = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        var calls = 0;
        var parserThread = 0;
        var cache = new RigStatusSnapshot(json =>
        {
            parserThread = Thread.CurrentThread.ManagedThreadId;
            if (Interlocked.Increment(ref calls) == 1)
            {
                entered.Set();
                if (!release.Wait(3000)) throw new Exception("parser release timed out");
            }
            return JToken.Parse(json) as JObject;
        });
        Require(cache.Current == null, "no initial status cannot imply ready");
        cache.Offer("{\"version\":0}");
        Require(entered.Wait(3000), "slow parser entered worker");
        Require(parserThread != caller, "JSON parsing stays off caller thread");
        // A blocked parser must not block offers or create one worker per update.
        for (var i = 1; i <= 500; i++) cache.Offer("{\"version\":" + i + "}");
        Require(calls == 1, "500 pending updates do not start overlapping parses");
        release.Set();
        WaitFor(() => (int?)cache.Current?["version"] == 500);
        Require(calls == 2, "only newest pending status is parsed");
        var previous = cache.Current;
        cache.Offer("{\"version\":501,\"episodeActive\":true,\"floor\":{\"review_queue\":[{\"id\":\"ep1\"}]}}");
        WaitFor(() => (int?)cache.Current?["version"] == 501);
        Require((int?)previous?["version"] == 500, "published trees are never modified by later updates");
        Require((bool?)cache.Current?["episodeActive"] == true && (string?)cache.Current?["floor"]?["review_queue"]?[0]?["id"] == "ep1",
            "recording and review facts retain their values");
        cache.Offer("not JSON");
        WaitFor(() => cache.Current == null);
        Require(cache.Current == null, "malformed update clears stale readiness");
        cache.Offer("{\"version\":502}");
        WaitFor(() => (int?)cache.Current?["version"] == 502);
        Require((int?)cache.Current?["version"] == 502, "worker recovers after malformed status");
        cache.Dispose();
        BuilderAndPublication();
        InvalidationAndShutdown();
        NativeObservation();
    }

    private static void BuilderAndPublication()
    {
        var caller = Thread.CurrentThread.ManagedThreadId;
        using var cache = new RigStatusSnapshot();
        var builderThread = 0;
        cache.Offer(() =>
        {
            builderThread = Thread.CurrentThread.ManagedThreadId;
            return "{\"version\":0}";
        });
        WaitFor(() => (int?)cache.Current?["version"] == 0);
        Require(builderThread != caller, "JSON assembly stays off the offering thread");

        var firstEntered = new ManualResetEventSlim();
        var firstRelease = new ManualResetEventSlim();
        var newestEntered = new ManualResetEventSlim();
        var newestRelease = new ManualResetEventSlim();
        var builds = 0;
        cache.Offer(() =>
        {
            Interlocked.Increment(ref builds);
            firstEntered.Set();
            if (!firstRelease.Wait(3000)) throw new Exception("first builder release timed out");
            return "{\"version\":1}";
        });
        Require(firstEntered.Wait(3000), "slow builder entered worker");
        for (var i = 2; i <= 501; i++)
        {
            var value = i;
            cache.Offer(() =>
            {
                Interlocked.Increment(ref builds);
                newestEntered.Set();
                if (!newestRelease.Wait(3000)) throw new Exception("newest builder release timed out");
                return "{\"version\":" + value + "}";
            });
        }
        Require(builds == 1, "500 offers neither block nor run another builder");
        firstRelease.Set();
        Require(newestEntered.Wait(3000), "newest pending builder starts after the in-flight one");
        Require((int?)cache.Current?["version"] == 1, "newer offers cannot starve in-flight publication");
        newestRelease.Set();
        WaitFor(() => (int?)cache.Current?["version"] == 501);
        Require(builds == 2, "only the latest pending builder executes");
        var published = cache.Latest!;
        Require(JToken.DeepEquals(JToken.Parse(published.Json), published.Tree), "raw JSON and tree are one coherent published value");
        cache.Offer(() => throw new InvalidOperationException("deliberate builder failure"));
        WaitFor(() => cache.Current == null);
        Require(cache.Latest?.Json == "null", "builder failure clears ready state and leaves valid raw JSON");
        cache.Offer(() => "{\"version\":502}");
        WaitFor(() => (int?)cache.Current?["version"] == 502);
        Require((int?)published.Tree?["version"] == 501, "later builds never mutate a held publication");
        cache.Offer("invalid JSON");
        WaitFor(() => cache.Current == null);
        Require(cache.Latest?.Json == "null", "parser failure clears raw and parsed readiness together");
    }

    private static void InvalidationAndShutdown()
    {
        using var cache = new RigStatusSnapshot();
        cache.Offer("{\"ready\":true}");
        WaitFor(() => (bool?)cache.Current?["ready"] == true);
        var entered = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        var exited = new ManualResetEventSlim();
        cache.Offer(() =>
        {
            entered.Set();
            if (!release.Wait(3000)) throw new Exception("invalidated builder release timed out");
            return "{\"ready\":true,\"old\":true}";
        });
        Require(entered.Wait(3000), "old-generation builder is in flight");
        cache.Invalidate();
        Require(cache.Latest == null, "invalidation immediately clears both published forms");
        var newRelease = new ManualResetEventSlim();
        cache.Offer(() =>
        {
            exited.Set();
            if (!newRelease.Wait(3000)) throw new Exception("new-generation builder release timed out");
            return "{\"ready\":false,\"new\":true}";
        });
        release.Set();
        Require(exited.Wait(3000), "new generation runs after old builder returns");
        Require(cache.Latest == null, "old generation never becomes visible while replacement builds");
        newRelease.Set();
        WaitFor(() => (bool?)cache.Current?["new"] == true);
        Require((bool?)cache.Current?["ready"] == false && cache.Current?["old"] == null,
            "old rig generation cannot republish readiness after invalidation");

        entered.Reset(); release.Reset(); exited.Reset();
        cache.Offer(() =>
        {
            entered.Set();
            if (!release.Wait(3000)) throw new Exception("disposed builder release timed out");
            exited.Set();
            return "{\"ready\":true}";
        });
        Require(entered.Wait(3000), "shutdown has a builder in flight");
        var began = Stopwatch.StartNew();
        cache.Dispose();
        Require(began.ElapsedMilliseconds < 500 && cache.Latest == null, "dispose neither waits for building nor retains readiness");
        var afterDispose = false;
        cache.Offer(() => { afterDispose = true; return "{}"; });
        release.Set();
        Require(exited.Wait(3000), "disposed builder can finish safely");
        // A later worker callback is forbidden; give it time to expose a broken disposal gate.
        Thread.Sleep(50);
        Require(cache.Latest == null && !afterDispose, "late completion and offers cannot revive a disposed snapshot");
    }

    private static void NativeObservation()
    {
        var first = RigNativeStatusSnapshot.Read("{\"state\":\"ready\",\"pod_id\":\"one\",\"floor\":{\"version\":1}}", RigNativeStatusSnapshot.Empty);
        var second = RigNativeStatusSnapshot.Read("{\"state\":\"ready\",\"pod_id\":\"two\",\"floor\":{\"version\":2}}", first);
        Require((string?)JObject.Parse(first.Json)["pod_id"] == "one" && (string?)JObject.Parse(first.SummaryJson)["podId"] == "one",
            "a captured native observation keeps raw and summary from the same read");
        Require((string?)JObject.Parse(second.Json)["pod_id"] == "two" && (string?)JObject.Parse(second.SummaryJson)["podId"] == "two",
            "next observation changes raw and summary together");
        var busy = RigNativeStatusSnapshot.Read("{\"state\":\"busy\"}", second, out var busyRead);
        Require(busyRead == null && busy.SummaryJson == second.SummaryJson && (string?)JObject.Parse(busy.Json)["state"] == "busy",
            "native busy explicitly retains the last readable summary without replaying cues");
        var error = RigNativeStatusSnapshot.Error("{\"code\":\"state\"}", busy);
        Require(error.SummaryJson == busy.SummaryJson && error.Json.Contains("error"), "native errors retain the prior diagnostic summary");
        var malformed = RigNativeStatusSnapshot.Read("malformed", second);
        using var cache = new RigStatusSnapshot();
        cache.Offer(() => "{\"native\":" + malformed.Json + "}");
        WaitFor(() => cache.Latest != null);
        Require(cache.Current == null && cache.Latest?.Json == "null", "malformed native input cannot leave a published ready tree");
    }
}
