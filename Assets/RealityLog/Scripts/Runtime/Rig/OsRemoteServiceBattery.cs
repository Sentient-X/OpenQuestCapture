#nullable enable

using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using UnityEngine;
using RealityLog.Common;

namespace RealityLog.Rig
{
    /// <summary>
    /// Source (f), the OS remote-service report: the controllers' charge as Horizon OS's
    /// <c>OVRRemoteService</c> prints it for <c>dumpsys</c>, the one place the charge is readable on
    /// Horizon OS 207 (every app API answers nothing; <c>Controllers.getBatteryLevel</c> needs a
    /// signature permission). Its binder (<c>ServiceManager.getService</c>) is asked to dump into a
    /// pipe, read with a timeout, and parsed by <see cref="OsControllerReport"/>. The service writes
    /// its report only to a caller holding <c>android.permission.DUMP</c>, which the manifest
    /// declares and provisioning grants (<c>adb shell pm grant ... android.permission.DUMP</c>).
    /// Off the main thread: once a minute, and on a controller's wake. Anything that fails (no
    /// grant, no binder, a throw, a timeout, nothing parsed) leaves the charge not available, with
    /// the reason logged once under <c>[RealityLog] ControllerBattery: (f)</c>; it is never 0.
    /// </summary>
    public sealed class OsRemoteServiceBattery
    {
        private const string Service = "OVRRemoteService";
        private const int EveryMs = 60_000;
        private const int WakeGapMs = 5_000;
        private const int TimeoutMs = 3_000;
        private const int MaxBytes = 512 * 1024;

        private readonly OsControllerBatteryCache cache = new();
        private readonly AutoResetEvent wake = new(false);
        private Thread? thread;
        private string lastLog = "";

        /// <summary>Each controller's charge, 0..100, from the last good report; null where not available.</summary>
        public (int? Left, int? Right) Read()
        {
            Start();
            return cache.Read(DateTime.UtcNow);
        }

        /// <summary>A controller woke: report again now (at most once in 5 s).</summary>
        public void Wake() => wake.Set();

        private void Start()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (thread != null) return;
            thread = new Thread(Run) { IsBackground = true, Name = "ControllerBatteryOs" };
            thread.Start();
#endif
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        private void Run()
        {
            AndroidJNI.AttachCurrentThread();
            try
            {
                while (true)
                {
                    var started = Environment.TickCount;
                    ReportOnce();
                    wake.WaitOne(EveryMs);
                    var since = unchecked(Environment.TickCount - started);
                    if (since < WakeGapMs) Thread.Sleep(WakeGapMs - since);
                }
            }
            finally
            {
                AndroidJNI.DetachCurrentThread();
            }
        }

        private void ReportOnce()
        {
            string? dump;
            try
            {
                dump = Dump(out var refused);
                if (dump == null)
                {
                    Note(refused!);
                    return;
                }
            }
            catch (Exception ex)
            {
                Note($"dump threw {ex.GetType().Name}: {OneLine(ex.Message)}");
                return;
            }
            var report = OsControllerReport.Parse(dump);
            if (!report.Parsed)
            {
                Note($"nothing parsed from {dump.Length} chars ({report.Paired} paired-device line(s)); first line: '{OneLine(FirstLine(dump))}'");
                return;
            }
            cache.Record(report, DateTime.UtcNow);
            Note(report.Describe());
        }

        /// <summary>The service's report text; null with <paramref name="refused"/> saying why when there is none.</summary>
        private static string? Dump(out string? refused)
        {
            refused = null;
            using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
            {
                // PackageManager.PERMISSION_GRANTED is 0.
                if (activity == null || activity.Call<int>("checkSelfPermission", "android.permission.DUMP") != 0)
                {
                    refused = "android.permission.DUMP is not granted (adb shell pm grant <package> android.permission.DUMP)";
                    return null;
                }
            }
            using var serviceManager = new AndroidJavaClass("android.os.ServiceManager");
            using var binder = serviceManager.CallStatic<AndroidJavaObject>("getService", Service);
            if (binder == null)
            {
                refused = $"ServiceManager.getService(\"{Service}\") returned null";
                return null;
            }
            using var pfd = new AndroidJavaClass("android.os.ParcelFileDescriptor");
            var pipe = pfd.CallStatic<AndroidJavaObject[]>("createPipe");
            try
            {
                using (var writeFd = pipe[1].Call<AndroidJavaObject>("getFileDescriptor"))
                {
                    // One-way: the service writes and closes its copy of the write end when done.
                    binder.Call("dumpAsync", writeFd, new string[0]);
                }
                pipe[1].Call("close");
                var text = ReadAll(pipe[0].Call<int>("getFd"), out var why);
                if (text == null) refused = why;
                return text;
            }
            finally
            {
                foreach (var end in pipe)
                {
                    try { end.Call("close"); } catch (Exception) { /* already closed */ }
                    end.Dispose();
                }
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PollFd
        {
            public int Fd;
            public short Events;
            public short Revents;
        }

        private const short PollIn = 0x1;
        private const int EIntr = 4;
        private const int EAgain = 11;

        [DllImport("libc", EntryPoint = "poll", SetLastError = true)]
        private static extern int Poll([In, Out] PollFd[] fds, UIntPtr count, int timeoutMs);

        [DllImport("libc", EntryPoint = "read", SetLastError = true)]
        private static extern IntPtr ReadFd(int fd, byte[] buffer, UIntPtr count);

        /// <summary>The pipe to its end, within the timeout; null with <paramref name="why"/> otherwise.</summary>
        private static string? ReadAll(int fd, out string? why)
        {
            why = null;
            var deadline = Environment.TickCount + TimeoutMs;
            var buffer = new byte[16 * 1024];
            using var text = new System.IO.MemoryStream();
            var fds = new PollFd[1];
            while (true)
            {
                var left = unchecked(deadline - Environment.TickCount);
                if (left <= 0)
                {
                    why = $"timed out after {TimeoutMs} ms with {text.Length} bytes read";
                    return null;
                }
                fds[0] = new PollFd { Fd = fd, Events = PollIn };
                var ready = Poll(fds, (UIntPtr)1, left);
                if (ready < 0)
                {
                    var errno = Marshal.GetLastWin32Error();
                    if (errno == EIntr) continue;
                    why = $"poll failed, errno {errno}";
                    return null;
                }
                if (ready == 0) continue; // the deadline check above ends it
                var count = (long)ReadFd(fd, buffer, (UIntPtr)buffer.Length);
                if (count == 0) return Encoding.UTF8.GetString(text.GetBuffer(), 0, (int)text.Length);
                if (count < 0)
                {
                    var errno = Marshal.GetLastWin32Error();
                    if (errno == EIntr || errno == EAgain) continue;
                    why = $"read failed, errno {errno}";
                    return null;
                }
                if (text.Length + count > MaxBytes)
                {
                    why = $"report larger than {MaxBytes} bytes";
                    return null;
                }
                text.Write(buffer, 0, (int)count);
            }
        }
#endif

        /// <summary>Logged once until it changes.</summary>
        private void Note(string line)
        {
            if (line == lastLog) return;
            lastLog = line;
            Debug.Log($"[{Constants.LOG_TAG}] ControllerBattery: (f) OS remote-service report: {line}");
        }

        private static string FirstLine(string text)
        {
            var end = text.IndexOf('\n');
            return end < 0 ? text : text.Substring(0, end);
        }

        private static string OneLine(string text)
        {
            var line = text.Replace('\n', ' ').Replace('\r', ' ').Trim();
            return line.Length > 160 ? line.Substring(0, 160) + "..." : line;
        }
    }
}
