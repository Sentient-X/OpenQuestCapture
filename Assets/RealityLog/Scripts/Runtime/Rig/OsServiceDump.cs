#nullable enable

using System;
#if UNITY_ANDROID && !UNITY_EDITOR
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;
#endif

namespace RealityLog.Rig
{
    /// <summary>
    /// A Horizon OS system service's <c>dumpsys</c> text, read in-app: its binder
    /// (<c>ServiceManager.getService</c>) dumps into a pipe, read with a timeout. Needs
    /// <c>android.permission.DUMP</c> (granted over adb at provisioning). Used for the controllers'
    /// charge (<c>OVRRemoteService</c>, <see cref="OsRemoteServiceBattery"/>) and the headset's serial
    /// (<c>CompanionService</c>, <see cref="OsHeadsetSerial"/>). Never on Unity's main thread; the
    /// calling thread must be attached to the Android VM.
    /// </summary>
    public static class OsServiceDump
    {
        /// <summary>
        /// Whether the app holds android.permission.DUMP, as the last dump found it; null before
        /// any. A charge not available for want of it says so (PodControlWords.Percent).
        /// </summary>
        public static bool? Granted => granted switch { 1 => true, 0 => false, _ => (bool?)null };
        private static volatile int granted = -1;

        private const int TimeoutMs = 3_000;
        private const int MaxBytes = 512 * 1024;

#if UNITY_ANDROID && !UNITY_EDITOR
        /// <summary>The service's report text; null with <paramref name="refused"/> saying why when there is none.</summary>
        public static string? Dump(string service, out string? refused)
        {
            refused = null;
            using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
            {
                // PackageManager.PERMISSION_GRANTED is 0.
                var held = activity != null && activity.Call<int>("checkSelfPermission", "android.permission.DUMP") == 0;
                granted = held ? 1 : 0;
                if (!held)
                {
                    refused = "android.permission.DUMP is not granted (adb shell pm grant <package> android.permission.DUMP)";
                    return null;
                }
            }
            using var serviceManager = new AndroidJavaClass("android.os.ServiceManager");
            using var binder = serviceManager.CallStatic<AndroidJavaObject>("getService", service);
            if (binder == null)
            {
                refused = $"ServiceManager.getService(\"{service}\") returned null";
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
#else
        public static string? Dump(string service, out string? refused)
        {
            refused = "dumpsys is read only on the headset";
            return null;
        }
#endif
    }

    /// <summary>
    /// The headset's serial as Horizon OS's <c>CompanionService</c> prints it
    /// (<c>Provisioned serial: 3487C10H3P07B9</c>): apps cannot read <c>ro.serialno</c>, so a
    /// pod_config.json without <c>headset_serial</c> takes it from here. Pure parse, host-tested.
    /// </summary>
    public static class OsHeadsetSerial
    {
        private static readonly System.Text.RegularExpressions.Regex Line =
            new(@"Provisioned serial:\s*([A-Za-z0-9]{6,32})\b");

        /// <summary>The serial in a CompanionService dump; null when it names none.</summary>
        public static string? Parse(string dump) => Line.Match(dump) is { Success: true } m ? m.Groups[1].Value : null;
    }
}
