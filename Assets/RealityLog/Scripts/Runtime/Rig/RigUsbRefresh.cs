#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;

namespace RealityLog.Rig
{
    /// <summary>
    /// Serializes USB release and enumeration off Unity's sampling thread. One refresh may be pending or
    /// running, and one completed snapshot waits for the main thread; a slow Android binder
    /// call cannot accumulate work. The caller still applies attachment changes on its thread.
    /// Dispose stops accepting work immediately, discards a late result, and closes the bridge
    /// on the worker only after an in-flight read returns. It never waits on USB from Unity.
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
        private readonly Action<string> release;
        private readonly Action close;
        private string[] pendingReleases = Array.Empty<string>();
        private bool pending;
        private bool running;
        private bool disposed;
        private Result? completed;
        private long reads;
        private long failures;
        private long slowReads;
        private double lastMs;
        private double maxMs;

        public RigUsbRefresh(Func<string> read, Action close, Action<string>? release = null)
        {
            this.read = read;
            this.close = close;
            this.release = release ?? (_ => throw new InvalidOperationException("USB release callback is missing"));
            new Thread(Run) { IsBackground = true, Name = "RigUsbRefresh" }.Start();
        }

        /// <summary>Owns the release-key array after success; the caller must not modify it.</summary>
        public bool Request(string[]? releases = null)
        {
            lock (gate)
            {
                // Consume the previous snapshot before enumerating again. In particular a
                // detach must reach the native owner before its fd can be released/reused.
                if (disposed || pending || running || completed != null) return false;
                pendingReleases = releases ?? Array.Empty<string>();
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
                // No connection can close while read() is still using it.
                try { close(); }
                finally { wake.Dispose(); }
            }
        }

        private void RunLoop()
        {
            while (true)
            {
                wake.WaitOne();
                string[] releases;
                lock (gate)
                {
                    if (disposed) return;
                    pending = false;
                    running = true;
                    releases = pendingReleases;
                    pendingReleases = Array.Empty<string>();
                }

                var started = Stopwatch.GetTimestamp();
                Result result;
                try
                {
                    // The main thread queued these only after native detach. Both JNI
                    // methods share Kotlin's monitor, including broadcast-triggered reads;
                    // even a no-op release must never acquire that monitor on Unity.
                    foreach (var key in releases) release(key);
                    result = new Result(read(), null);
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

    /// <summary>
    /// The coordinator's main-thread snapshot/apply/request protocol. A retained snapshot is
    /// for status, not another detach instruction. Opening/reopening the native rig explicitly
    /// marks it for reapplication so live descriptors are attached to the new native owner.
    /// </summary>
    public sealed class RigUsbSnapshotFlow
    {
        private bool needsApply = true;
        private readonly HashSet<string> releases = new(StringComparer.Ordinal);

        public void SnapshotChanged() => needsApply = true;
        public void RigClosed() => needsApply = true;

        public void ApplyIfNeeded(bool opened, Action apply)
        {
            if (!opened || !needsApply) return;
            apply();
            needsApply = false;
        }

        /// <summary>Called only after native detach, or for a descriptor never attached there.</summary>
        public void QueueRelease(string key) => releases.Add(key);

        public bool Request(RigUsbRefresh worker)
        {
            var keys = releases.Count == 0 ? Array.Empty<string>() : new string[releases.Count];
            releases.CopyTo(keys);
            if (!worker.Request(keys)) return false;
            releases.Clear();
            return true;
        }
    }
}
