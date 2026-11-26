#nullable enable
// OQC build 107's law: no sx_rig call on Unity's main thread. Every call takes libsx_rig's one
// rig lock, which a check, start, stop or a detach joining an encoder stream holds for seconds,
// and the main thread pumps FixedUpdate's body poll and the head-camera frames (Pod 7, build 105,
// 2026-11-26: TrackingLost after an 822 ms body gap during an encoder replug).
//   1. Runtime, against the STUB libsx_rig.so: SxRig counts a call on the thread marked main;
//      the rig lane, the bounded teardown helper and the USB worker make theirs elsewhere.
//   2. Wiring: every SxRig call site in the app runs on a worker (its enclosing method is one
//      that runs off the main thread, or the call sits inside a lane/thread hand-off), and the
//      main-thread entry points use only the non-blocking forms.
// Run by test_main_thread.sh.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using RealityLog.Rig;

static class RigMainThreadHarness
{
    static int failures;
    static void Expect(bool ok, string what)
    {
        Console.WriteLine((ok ? "PASS " : "FAIL ") + what);
        if (!ok) failures++;
    }

    static void Quiet(Action call) { try { call(); } catch (SxRigException) { } }

    static int Main(string[] args)
    {
        // ── 1. Runtime ──
        SxRig.MarkMainThread();
        var seen = new List<string>();
        SxRig.CalledOnMainThread += f => seen.Add(f);
        Quiet(() => SxRig.Status());
        Expect(SxRig.MainThreadCalls == 1 && seen.SequenceEqual(new[] { "sx_rig_status" }),
            "the guard counts an sx_rig call made on the marked main thread: " + string.Join(",", seen));
        var baseline = SxRig.MainThreadCalls;

        var errors = new List<Exception>();
        var lane = new RigLane("RigLifecycle", ex => { lock (errors) errors.Add(ex); });
        var order = new List<string>();
        var laneDone = new ManualResetEventSlim(false);
        lane.Post(() => { SxRig.Open("{\"staging_dir\":\"/tmp/sx-rig-main-thread\",\"rig\":{}}"); lock (order) order.Add("open"); });
        lane.Post(() => { Quiet(() => SxRig.ApiToken()); lock (order) order.Add("token"); });
        lane.Post(() => { Quiet(() => SxRig.RequestStop()); lock (order) order.Add("cutoff"); });
        lane.Post(() => { SxRig.Close(); lock (order) order.Add("close"); });
        lane.Post(() => throw new InvalidOperationException("escaped"));
        lane.Post(() => laneDone.Set());
        Expect(laneDone.Wait(5000), "the rig lane ran its work");
        Expect(order.SequenceEqual(new[] { "open", "token", "cutoff", "close" }), "the lane keeps posted order: " + string.Join(",", order));
        Expect(errors.Count == 1 && errors[0].Message == "escaped", "an escaped exception is reported and the lane runs on");

        // A lane job held behind the rig lock never makes Post wait.
        var rigLock = new object();
        var held = new ManualResetEventSlim(false);
        var holder = new Thread(() => { lock (rigLock) { held.Set(); Thread.Sleep(1000); } });
        holder.Start();
        held.Wait();
        lane.Post(() => { lock (rigLock) { } Quiet(() => SxRig.Status()); });
        var timer = Stopwatch.StartNew();
        for (int i = 0; i < 1000; i++) lane.Post(() => { });
        Expect(timer.ElapsedMilliseconds < 100, $"1000 posts while the lane waits on the rig lock: {timer.ElapsedMilliseconds} ms");
        holder.Join();

        // Teardown's bounded hand-off: the call runs on its own thread; the caller waits at most the bound.
        timer.Restart();
        var finished = RigLane.RunBounded("RigStopCutoff", () => { lock (rigLock) { Thread.Sleep(600); } Quiet(() => SxRig.RequestStop()); },
            TimeSpan.FromMilliseconds(150));
        Expect(!finished && timer.ElapsedMilliseconds < 400, $"a bounded teardown call returns at its bound ({timer.ElapsedMilliseconds} ms) and runs on");
        Expect(RigLane.RunBounded("RigStop", () => Quiet(() => SxRig.Stop()), TimeSpan.FromSeconds(3)), "a quick teardown call finishes within its bound");

        // The USB worker's native attach/detach (the production worker and ledger).
        var usb = new RigUsbAttachments();
        usb.Opened();
        var cycleDone = new ManualResetEventSlim(false);
        var worker = new RigUsbRefresh(() => "bus", () => { },
            _ => usb.Sync(new RigUsbSnapshot { devices = new[] { new RigUsbDevice { key = "k", deviceName = "/dev/bus/usb/001/002", fd = 5, vendorId = 0x303a } } },
                d => { try { SxRig.AttachUsb(d.fd, (ushort)d.vendorId, (ushort)d.productId, d.deviceName); return null; } catch (SxRigException e) { return e.ErrorJson; } },
                d => Quiet(() => SxRig.DetachUsb(d.deviceName))),
            _ => { });
        worker.Request();
        SpinWait.SpinUntil(() => worker.Take() != null, 5000);
        worker.Dispose();
        Thread.Sleep(1200); // the lane's held job and the bounded call finish
        lane.Post(() => cycleDone.Set());
        cycleDone.Wait(3000);
        lane.Dispose();
        Expect(SxRig.MainThreadCalls == baseline,
            $"no sx_rig call ran on the marked main thread from the lane, the bounded teardown or the USB worker (count {SxRig.MainThreadCalls - baseline})");

        // ── 2. Wiring ──
        var root = args[0];
        string Read(string rel) => File.ReadAllText(Path.Combine(root, rel));
        var coordinator = Read("Assets/RealityLog/Scripts/Runtime/Rig/RigCoordinator.cs");
        var recording = Read("Assets/RealityLog/Scripts/Runtime/RecordingManager.cs");
        var setup = Read("Assets/RealityLog/Scripts/Runtime/Rig/PodSetupController.cs");
        var http = Read("Assets/RealityLog/Scripts/Runtime/Network/HttpServerController.cs");
        var phone = Read("Assets/RealityLog/Scripts/Runtime/Rig/PhoneCompanion.cs");

        // Methods that run only off the main thread, with the thread that runs them.
        var offMain = new Dictionary<string, string>
        {
            ["CheckReady"] = "the HTTP start worker (CheckAndReserve)",
            ["StartArmedClaimed"] = "the HTTP start worker (StartArmed)",
            ["Enroll"] = "the enrollment worker (Task.Run / HTTP)",
            ["ReadApiTokenOnLane"] = "the rig lane",
            ["Command"] = "the HTTP worker",
            ["CheckNow"] = "PodControls' Task.Run",
            ["RequestStopClaimed"] = "the cutoff worker or a teardown thread",
            ["StopEpisode"] = "the stop worker or the quit thread",
            ["ApplyUsbSnapshot"] = "the USB worker",
            ["StartDeviceReports"] = "the device report thread",
            ["ObserveTracking"] = "the tracking timer or the HTTP start worker",
            ["StartRefusalNow"] = "the HTTP start worker",
            ["PollNativeStatus"] = "the native status timer",
        };
        // Main-thread methods whose sx_rig calls sit inside a hand-off to another thread.
        var handOff = new Dictionary<string, string>
        {
            ["StartOpen"] = "lane.Post(",
            ["CloseRig"] = "lane.Post(",
            ["ShutDown"] = "new System.Threading.Thread(",
            ["SubmitCollectorReviewAsync"] = "Task.Run(",
            ["ReportPhoneLink"] = "Task.Run(",
        };
        var guardApi = new HashSet<string> { "MarkMainThread", "CalledOnMainThread", "MainThreadCalls" };
        var decl = new Regex(@"^        (?:public|private|internal|protected)[^;=]*?\b([A-Z][A-Za-z0-9]*)\s*\(", RegexOptions.Multiline);
        var call = new Regex(@"\bSxRig\.([A-Z][A-Za-z]*)");
        var stray = new List<string>();
        int sites = 0;
        foreach (var (name, text) in new[] { ("RigCoordinator", coordinator), ("RecordingManager", recording),
                     ("PodSetupController", setup), ("HttpServerController", http), ("PhoneCompanion", phone) })
        {
            var methods = decl.Matches(text).Cast<Match>().Select(m => (m.Index, Name: m.Groups[1].Value)).ToList();
            foreach (Match m in call.Matches(text))
            {
                if (guardApi.Contains(m.Groups[1].Value)) continue;
                var line = text.Substring(0, m.Index).Split('\n').Length;
                var lineText = text.Split('\n')[line - 1].Trim();
                if (lineText.StartsWith("//") || lineText.StartsWith("///")) continue;
                sites++;
                var enclosing = methods.LastOrDefault(x => x.Index < m.Index);
                if (enclosing.Name != null && offMain.ContainsKey(enclosing.Name)) continue;
                if (enclosing.Name != null && handOff.TryGetValue(enclosing.Name, out var marker)
                    && text.IndexOf(marker, enclosing.Index, m.Index - enclosing.Index, StringComparison.Ordinal) >= 0) continue;
                stray.Add($"{name}.cs:{line} SxRig.{m.Groups[1].Value} in {enclosing.Name ?? "?"}");
            }
        }
        Expect(sites >= 20 && stray.Count == 0,
            $"all {sites} sx_rig call sites run off the main thread" + (stray.Count > 0 ? ": " + string.Join("; ", stray) : ""));

        string Body(string text, string signature)
        {
            int start = text.IndexOf(signature, StringComparison.Ordinal);
            if (start < 0) return "";
            int brace = text.IndexOf('{', start), depth = 0;
            for (int i = brace; i < text.Length; i++)
            {
                if (text[i] == '{') depth++;
                else if (text[i] == '}' && --depth == 0) return text.Substring(start, i - start + 1);
            }
            return "";
        }
        // The main-thread entry points use only the forms that never wait on the rig.
        Expect(!recording.Contains("TryRequestStopEpisode") && !Regex.IsMatch(recording, @"\.RequestStopEpisode\(\)")
            && recording.Contains("rig.RequestStopCutoff()") && recording.Contains("RequestStopEpisodeBounded("),
            "RecordingManager takes the cutoff through the worker forms only");
        var stopRig = Regex.Matches(recording, @"\bStopRig\(").Count;
        Expect(stopRig == 3 && recording.Contains("RigLane.RunBounded(\"RecordingStopRig\", () => teardownReceipt = StopRig(")
            && Body(recording, "void FinishStop(bool cutoff)").Contains("receipt = StopRig(dataPath, savedDirectory);"),
            "StopRig (sx_rig_stop) runs on the stop worker or a bounded teardown thread");
        var finish = Body(recording, "void FinishStop(bool cutoff)");
        Expect(finish.IndexOf("EndPhase(cutoff ?", StringComparison.Ordinal) >= 0
            && finish.IndexOf("EndPhase(cutoff ?", StringComparison.Ordinal) < finish.IndexOf("captureTimer.StopCapture()", StringComparison.Ordinal)
            && recording.Contains("taken => mainThreadWork.Enqueue(() => FinishStop(taken.Result))"),
            "OQC's capture ends only after the rig's cutoff is taken (the rig's window ends first)");
        Expect(!setup.Contains("BeginEnrollment()") && !http.Contains("BeginEnrollment()")
            && setup.Contains("rig.BeginEnrollmentAsync()") && http.Contains("rig.BeginEnrollmentAsync()"),
            "enrollment callers never open the rig on the main thread");
        Expect(!coordinator.Contains("TryOpen(") && !coordinator.Contains("RefreshApiToken(")
            && !coordinator.Contains("TryRequestStopEpisode") && !coordinator.Contains("SafeStatus("),
            "the blocking coordinator forms are gone");
        var startOpen = Body(coordinator, "private Task<RigRefusal?> StartOpen()");
        Expect(startOpen.IndexOf("usb.WaitForSync();", StringComparison.Ordinal) >= 0
            && startOpen.IndexOf("usb.WaitForSync();", StringComparison.Ordinal) < startOpen.IndexOf("SxRig.Open(config);", StringComparison.Ordinal),
            "the open waits out a USB cycle that began before a close");
        var shutDown = Body(coordinator, "public void ShutDown()");
        Expect(shutDown.IndexOf("SxRig.Close();", StringComparison.Ordinal) < shutDown.IndexOf("refresh?.Dispose();", StringComparison.Ordinal)
            && shutDown.IndexOf("SxRig.DetachUsb(", StringComparison.Ordinal) < shutDown.IndexOf("SxRig.Close();", StringComparison.Ordinal)
            && shutDown.Contains("finished.Wait(ShutDownBound)") && !Regex.IsMatch(shutDown, @"usbRefresh\.Dispose\(\)"),
            "quit: detach, then close, then the bridge closes the fds; Unity waits at most the bound");
        Expect(Body(coordinator, "private void Awake()").Contains("SxRig.MarkMainThread();"),
            "the coordinator marks Unity's main thread at Awake");

        Console.WriteLine(failures == 0 ? "ALL PASS" : failures + " FAILED");
        return failures;
    }
}
