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
            && recording.Contains("rig.RequestStopCutoff()"),
            "RecordingManager takes the cutoff through the worker forms only");
        var stopRig = Regex.Matches(recording, @"\bStopRig\(").Count;
        Expect(stopRig == 2 && Body(recording, "void FinishStop(bool cutoff)").Contains("receipt = StopRig(dataPath, savedDirectory);"),
            "StopRig (sx_rig_stop) runs on the stop worker only");
        // Quit and destroy: the same stop as a press, Unity's thread waiting at most about 1 s
        // (Android counts 5 s held as an ANR; #79 left 3 s + 22 s + 8 s here and 10 s at ShutDown).
        var teardown = Body(recording, "private void StopForTeardown(string reason)");
        double Bound(string text, string name)
        {
            var m = Regex.Match(text, name + @"\s*=\s*TimeSpan\.From(Seconds|Milliseconds)\(([0-9.]+)\)");
            if (!m.Success) return double.PositiveInfinity;
            var v = double.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
            return m.Groups[1].Value == "Seconds" ? v : v / 1000;
        }
        var waits = Regex.Matches(teardown, @"\.(Wait|Join)\(((?:[^()]|\(\))*)\)").Cast<Match>().Select(m => m.Groups[2].Value).ToList();
        Expect(teardown.Contains("StopRecordingAsync();") && Bound(recording, "TeardownBound") <= 1.0
            && waits.Count > 0 && waits.All(w => w == "Left()") && !teardown.Contains("StopRecordingSession()"),
            $"teardown stops as a press does and waits only within TeardownBound ({Bound(recording, "TeardownBound")} s): waits [{string.Join(", ", waits)}]");
        var shutWaits = Regex.Matches(Body(coordinator, "public void ShutDown()"), @"\.(Wait|Join)\(((?:[^()]|\(\))*)\)").Cast<Match>().Select(m => m.Groups[2].Value).ToList();
        Expect(Bound(coordinator, "ShutDownBound") <= 1.0
            && shutWaits.All(w => w == "ShutDownBound" || (int.TryParse(w, out var ms) && ms <= 250)),
            $"quit waits at most ShutDownBound ({Bound(coordinator, "ShutDownBound")} s) for the rig and 250 ms per thread: [{string.Join(", ", shutWaits)}]");
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

        // ── 3. Entry points ──
        // Unity's frame callbacks, the coordinator's Poll and the UI callbacks never reach an
        // SxRig call, through any chain of methods in the app's runtime sources: a call inside a
        // hand-off (a lane post, a Task.Run, a thread or timer, a continuation) runs elsewhere
        // and is cut out before the walk. An unqualified call is followed into its own file's
        // declaration (else every file's); a call on another object (x.Name) is followed when
        // Name is declared in one file only and is not a common verb (Start, Read, Play...):
        // one shared by several classes cannot be told apart by name here.
        var runtime = Path.Combine(root, "Assets/RealityLog/Scripts/Runtime");
        var sources = Directory.GetFiles(runtime, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith("SxRig.cs")).ToDictionary(f => f.Substring(runtime.Length + 1), File.ReadAllText);
        string[] handOffs = { "lane.Post(", "Task.Run(", "Task.Factory.StartNew(", "new System.Threading.Thread(", "new Thread(",
            "new System.Threading.Timer(", "RunBounded(", "ContinueWith(", "ThreadPool.QueueUserWorkItem(" };
        string Strip(string body)
        {
            var text = Regex.Replace(body, @"//[^\n]*", "");
            foreach (var marker in handOffs)
            {
                int at;
                while ((at = text.IndexOf(marker, StringComparison.Ordinal)) >= 0)
                {
                    int open = at + marker.Length - 1, depth = 0, end = open;
                    for (; end < text.Length; end++)
                    {
                        if (text[end] == '(') depth++;
                        else if (text[end] == ')' && --depth == 0) break;
                    }
                    text = text.Substring(0, at) + text.Substring(Math.Min(end + 1, text.Length));
                }
            }
            return text;
        }
        var bodies = new Dictionary<string, List<(string file, string body)>>();
        foreach (var (file, text) in sources)
        {
            foreach (Match m in decl.Matches(text))
            {
                // A block body, or an expression body (=> ...;); a declaration without one is skipped.
                int close = m.Index + m.Length - 1, depth = 0;
                for (; close < text.Length; close++)
                {
                    if (text[close] == '(') depth++;
                    else if (text[close] == ')' && --depth == 0) break;
                }
                int brace = text.IndexOf('{', close), semi = text.IndexOf(';', close), arrow = text.IndexOf("=>", close, StringComparison.Ordinal);
                string body;
                if (arrow >= 0 && (brace < 0 || arrow < brace) && (semi < 0 || arrow < semi))
                    body = text.Substring(m.Index, (semi < 0 ? text.Length : semi + 1) - m.Index);
                else if (brace >= 0 && (semi < 0 || brace < semi))
                    body = Body(text.Substring(m.Index), text.Substring(m.Index, m.Length));
                else continue;
                if (body.Length == 0) continue;
                if (!bodies.TryGetValue(m.Groups[1].Value, out var list)) bodies[m.Groups[1].Value] = list = new();
                list.Add((file, Strip(body)));
            }
        }
        // UI callbacks: what a button, toggle or event subscribes, and the On* handlers.
        var subscribed = sources.Values.SelectMany(t => Regex.Matches(t, @"(?:AddListener\(|\+=\s*)([A-Z][A-Za-z0-9]*)\b(?!\s*\()").Cast<Match>())
            .Select(m => m.Groups[1].Value);
        var entries = new[] { "Update", "LateUpdate", "FixedUpdate", "OnGUI", "Poll", "OnApplicationPause", "OnApplicationFocus" }
            .Concat(subscribed).Concat(bodies.Keys.Where(k => Regex.IsMatch(k, @"^On[A-Z]\w*(Click|Clicked|Press|Pressed|Button|Toggle|Toggled|Changed|Selected)$")))
            .Where(bodies.ContainsKey).Distinct().ToList();
        var reached = new Dictionary<string, string>(); // method -> the entry chain that reached it
        var queue = new Queue<string>();
        foreach (var entry in entries) { reached[entry] = entry; queue.Enqueue(entry); }
        var callee = new Regex(@"(\.)?\b([A-Z][A-Za-z0-9]*)\s*\(");
        var common = new HashSet<string> { "Start", "Stop", "Read", "Write", "Play", "Build", "Run", "Close", "Dispose", "Add", "Remove",
            "Clear", "Set", "Get", "Update", "Open", "Reset", "Refresh", "Begin", "End", "Frame", "Parse", "Join", "Wait", "Post", "Request", "Take" };
        var reachesRig = new List<string>();
        while (queue.Count > 0)
        {
            var method = queue.Dequeue();
            foreach (var (file, body) in bodies[method])
            {
                foreach (Match rigCall in call.Matches(body))
                {
                    if (!guardApi.Contains(rigCall.Groups[1].Value)) reachesRig.Add($"{reached[method]} -> SxRig.{rigCall.Groups[1].Value} ({file})");
                }
                // The body after its own signature: a declaration's name is not a call.
                var calls = Regex.Replace(body.Substring(Math.Min(body.Length, body.IndexOf('{') + 1)), @"\bSxRig\.\w+\s*\(", "(");
                foreach (Match next in callee.Matches(calls))
                {
                    var name = next.Groups[2].Value;
                    if (!bodies.TryGetValue(name, out var declared)) continue;
                    var key = name;
                    if (next.Groups[1].Success)
                    {
                        // x.Name(...): only a name one file declares.
                        if (common.Contains(name) || declared.Select(d => d.file).Distinct().Count() != 1) continue;
                    }
                    else if (declared.Any(d => d.file == file))
                    {
                        key = file + "#" + name;
                        if (!bodies.ContainsKey(key)) bodies[key] = declared.Where(d => d.file == file).ToList();
                    }
                    if (reached.ContainsKey(key)) continue;
                    reached[key] = reached[method] + " -> " + name;
                    queue.Enqueue(key);
                }
            }
        }
        Expect(entries.Contains("Update") && entries.Contains("FixedUpdate") && entries.Contains("Poll") && reached.Count > 50 && reachesRig.Count == 0,
            $"no SxRig call is reached from {entries.Count} main-thread entry points ({reached.Count} methods walked)"
            + (reachesRig.Count > 0 ? ": " + string.Join("; ", reachesRig.Distinct().Take(12)) : ""));

        Console.WriteLine(failures == 0 ? "ALL PASS" : failures + " FAILED");
        return failures;
    }
}
