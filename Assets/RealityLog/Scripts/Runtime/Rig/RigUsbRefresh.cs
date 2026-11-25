#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;

namespace RealityLog.Rig
{
    /// <summary>
    /// The whole USB cycle off Unity's sampling thread: enumerate (Kotlin's bridge), apply the
    /// snapshot to the native rig (attach new devices, detach vanished ones), then release the
    /// descriptors the rig no longer owns. One cycle may be pending or running, and one completed
    /// result waits for the main thread; a slow Android binder call or a native attach waiting
    /// on the rig lock cannot accumulate work, and neither can stall Update or FixedUpdate.
    /// Before build 107 the apply ran on Unity's thread: on Pod 7 (2026-11-26, build 105) each
    /// encoder replug blocked it behind libsx_rig's global lock and a USB identity read, and the
    /// body poll in FixedUpdate went 822 ms without a sample (TrackingLost).
    /// Dispose stops accepting work immediately, discards a late result, and closes the bridge
    /// on the worker only after an in-flight cycle returns. It never waits on USB from Unity.
    /// </summary>
    public sealed class RigUsbRefresh : IDisposable
    {
        public sealed class Result
        {
            public string? Json { get; }
            public Exception? Error { get; }
            public Result(string? json, Exception? error) { Json = json; Error = error; }
        }

        private readonly object gate = new();
        private readonly AutoResetEvent wake = new(false);
        private readonly Func<string> read;
        private readonly Func<string, IReadOnlyCollection<string>> apply;
        private readonly Action<string> release;
        private readonly Action close;
        private bool pending;
        private bool running;
        private bool disposed;
        private Result? completed;
        private long reads;
        private long failures;
        private long slowReads;
        private double lastMs;
        private double maxMs;

        /// <param name="read">Enumerates the bus (a JSON snapshot).</param>
        /// <param name="apply">Applies a snapshot to the native rig on this worker and returns
        /// the keys whose descriptors the rig no longer owns; they are released before the next read.</param>
        /// <param name="close">Closes the bridge, once, after the last cycle.</param>
        /// <param name="release">Gives one descriptor back to the bridge.</param>
        public RigUsbRefresh(Func<string> read, Action close,
            Func<string, IReadOnlyCollection<string>>? apply = null, Action<string>? release = null)
        {
            this.read = read;
            this.close = close;
            this.apply = apply ?? (_ => Array.Empty<string>());
            this.release = release ?? (_ => throw new InvalidOperationException("USB release callback is missing"));
            new Thread(Run) { IsBackground = true, Name = "RigUsbRefresh" }.Start();
        }

        public bool Request()
        {
            lock (gate)
            {
                // The main thread consumes the previous result before another cycle runs.
                if (disposed || pending || running || completed != null) return false;
                pending = true;
                wake.Set();
                return true;
            }
        }

        public Result? Take()
        {
            lock (gate)
            {
                var result = completed;
                completed = null;
                return result;
            }
        }

        /// <summary>Bounded process-lifetime measurements; durations are worker time, not a Unity stall.</summary>
        public string StatsJson()
        {
            lock (gate)
            {
                var inv = CultureInfo.InvariantCulture;
                return "{\"completed\":" + reads.ToString(inv) + ",\"failed\":" + failures.ToString(inv)
                    + ",\"over_20ms\":" + slowReads.ToString(inv) + ",\"last_ms\":" + lastMs.ToString("F3", inv)
                    + ",\"max_ms\":" + maxMs.ToString("F3", inv)
                    + ",\"in_flight\":" + (pending || running ? "true" : "false") + "}";
            }
        }

        private void Run()
        {
            try
            {
                // Keep the loop in a separate method. Unity 6000.3.6f1 IL2CPP emitted
                // its back-edge outside this try's C++ RAII scope, running cleanup
                // after the FIRST refresh. Mono tests alone do not catch that output.
                RunLoop();
            }
            finally
            {
                // The close callback owns JNI attach/detach and reports its own failure.
                // No connection can close while a read or a native attach is still using it.
                try { close(); }
                finally { wake.Dispose(); }
            }
        }

        private void RunLoop()
        {
            while (true)
            {
                wake.WaitOne();
                lock (gate)
                {
                    if (disposed) return;
                    pending = false;
                    running = true;
                }

                var started = Stopwatch.GetTimestamp();
                Result result;
                try
                {
                    var json = read();
                    // Native detach happens inside apply, so every key it returns is released
                    // only after the rig let its descriptor go. Both JNI methods share Kotlin's
                    // monitor, including broadcast-triggered reads; none of it runs on Unity.
                    foreach (var key in apply(json)) release(key);
                    result = new Result(json, null);
                }
                catch (Exception ex) { result = new Result(null, ex); }
                var elapsedMs = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;

                lock (gate)
                {
                    running = false;
                    reads++;
                    if (result.Error != null) failures++;
                    if (elapsedMs > 20) slowReads++;
                    lastMs = elapsedMs;
                    maxMs = Math.Max(maxMs, elapsedMs);
                    if (disposed) return;
                    completed = result;
                }
            }
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
                completed = null;
                wake.Set();
            }
        }
    }

    /// <summary>One USB device as Kotlin's bridge lists it (RigUsbBridge snapshot JSON).</summary>
    [Serializable]
    public sealed class RigUsbDevice
    {
        public string key = "";
        public string deviceName = "";
        public int vendorId;
        public int productId;
        public int fd = -1;
        public string serial = "";
    }

    /// <summary>Kotlin's bridge snapshot. Published whole and never changed after.</summary>
    [Serializable]
    public sealed class RigUsbSnapshot
    {
        public RigUsbDevice[] devices = Array.Empty<RigUsbDevice>();
        public int present;
        public string[] awaitingPermission = Array.Empty<string>();
        // Vanished devices whose descriptors Kotlin still holds; listed until released.
        public string[] pendingRelease = Array.Empty<string>();
        public string error = "";
    }

    /// <summary>
    /// Which devices the native rig owns, applied from a snapshot on the USB worker. The gate is
    /// held only for dictionary work, never across a native call, so the main thread reading
    /// status never waits on USB or the rig lock. Invariants:
    /// a vanished device is detached before its key is returned for release; an attach error
    /// is final for its key (a replug is a new key); a cycle that overlapped a native close
    /// (<see cref="Closed"/>) or shutdown commits nothing, and Kotlin lists its vanished keys
    /// again, so the next cycle releases them.
    /// </summary>
    public sealed class RigUsbAttachments
    {
        private readonly object gate = new();
        private readonly Dictionary<string, RigUsbDevice> attached = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> attachErrors = new(StringComparer.Ordinal);
        private bool open;
        private bool shutDown;
        private bool syncing;
        private long epoch;

        /// <summary>The native rig opened: the next cycle attaches every listed device to it.</summary>
        public void Opened() { lock (gate) open = true; }

        /// <summary>The native rig closed: it forgot every device, and an in-flight cycle commits nothing.</summary>
        public void Closed()
        {
            lock (gate)
            {
                open = false;
                epoch++;
                attached.Clear();
                attachErrors.Clear();
            }
        }

        /// <summary>
        /// Whether a cycle is between its native calls. The rig must not reopen meanwhile: a late
        /// attach would land on the new rig without being recorded.
        /// </summary>
        public bool SyncInFlight { get { lock (gate) return syncing; } }

        /// <summary>Waits for an in-flight cycle (bounded by one native attach or detach).</summary>
        public void WaitForSync() { lock (gate) while (syncing) Monitor.Wait(gate); }

        public int Count { get { lock (gate) return attached.Count; } }

        public RigUsbDevice[] Attached() { lock (gate) return new List<RigUsbDevice>(attached.Values).ToArray(); }

        public KeyValuePair<string, string>[] AttachErrors()
        {
            lock (gate) return new List<KeyValuePair<string, string>>(attachErrors).ToArray();
        }

        /// <summary>
        /// Quit: no cycle starts or commits after this; returns what the caller detaches. A cycle
        /// already inside a native attach finishes it before the caller's close takes the rig
        /// lock, and the bridge closes descriptors only after the worker's last cycle.
        /// </summary>
        public RigUsbDevice[] ShutDown()
        {
            lock (gate)
            {
                shutDown = true;
                var all = new List<RigUsbDevice>(attached.Values).ToArray();
                attached.Clear();
                return all;
            }
        }

        /// <summary>
        /// Worker: applies <paramref name="snapshot"/> and returns the keys to release. <paramref name="attach"/>
        /// returns null or the attach error JSON; <paramref name="detach"/> reports its own failure.
        /// </summary>
        public IReadOnlyCollection<string> Sync(RigUsbSnapshot snapshot, Func<RigUsbDevice, string?> attach,
            Action<RigUsbDevice> detach)
        {
            long began;
            var toAttach = new List<RigUsbDevice>();
            var toDetach = new List<RigUsbDevice>();
            lock (gate)
            {
                if (!open || shutDown) return Array.Empty<string>();
                syncing = true;
                began = epoch;
                var current = new HashSet<string>(StringComparer.Ordinal);
                foreach (var device in snapshot.devices)
                {
                    current.Add(device.key);
                    if (!attached.ContainsKey(device.key) && !attachErrors.ContainsKey(device.key)) toAttach.Add(device);
                }
                foreach (var entry in attached)
                {
                    if (!current.Contains(entry.Key)) toDetach.Add(entry.Value);
                }
            }

            var attachedNow = new List<RigUsbDevice>();
            var failed = new List<KeyValuePair<string, string>>();
            try
            {
                foreach (var device in toAttach)
                {
                    if (attach(device) is string error) failed.Add(new KeyValuePair<string, string>(device.key, error));
                    else attachedNow.Add(device);
                }
                foreach (var device in toDetach) detach(device);
            }
            finally
            {
                lock (gate)
                {
                    syncing = false;
                    Monitor.PulseAll(gate);
                }
            }

            lock (gate)
            {
                if (began != epoch || shutDown) return Array.Empty<string>();
                var releases = new List<string>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var device in attachedNow) attached[device.key] = device;
                foreach (var error in failed) attachErrors[error.Key] = error.Value;
                foreach (var device in toDetach)
                {
                    attached.Remove(device.key);
                    if (seen.Add(device.key)) releases.Add(device.key);
                }
                // Vanished devices that were never attached (their attach failed) hold an fd too.
                foreach (var key in snapshot.pendingRelease)
                {
                    if (attached.ContainsKey(key)) continue;
                    attachErrors.Remove(key);
                    if (seen.Add(key)) releases.Add(key);
                }
                return releases;
            }
        }
    }
}
