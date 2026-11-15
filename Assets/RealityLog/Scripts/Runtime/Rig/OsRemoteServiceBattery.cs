#nullable enable

using System;
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
                dump = OsServiceDump.Dump(Service, out var refused);
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
