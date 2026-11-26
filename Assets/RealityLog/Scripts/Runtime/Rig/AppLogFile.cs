#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace RealityLog.Rig
{
    /// <summary>
    /// The app's log lines (RealityLog, SxCompanion, Unity's own), kept in app-private storage
    /// for libsx_rig to ship as the headset's diagnostic files, as the laptop pod ships its own:
    /// <c>&lt;credentials&gt;/logs/app.log</c>, rolled to a closed <c>app-&lt;when&gt;.log</c> at
    /// <see cref="RollBytes"/> and at every start (so the last run's lines — a crash's — close at
    /// once). The rig takes each closed file, redacted, into its attachment queue and removes it
    /// once the Factory has it; until then at most <see cref="MaxClosedFiles"/> closed files are
    /// kept here, the oldest going first. No UnityEngine dependency, so it is tested on the host
    /// (tools/sx-rig-stub/AppLogHarness.cs).
    ///
    /// <para><see cref="Write"/> never touches the disk: it queues the line and returns, and one
    /// writer thread writes, flushes and rolls. Every Debug.Log of every thread (Unity's main
    /// thread among them) used to write and flush under one lock, and roll inside it; with two
    /// wrist encoders writing to the same flash a flush can take tens to hundreds of ms, and a
    /// main thread stalled past 500 ms is a body-tracking gap that fails the take (2026-11-26).
    /// A full queue drops the line and counts it; the count is written once the writer catches
    /// up, so a lost line is never silent.</para>
    /// </summary>
    public sealed class AppLogFile : IDisposable
    {
        public const string ActiveName = "app.log";
        public const long RollBytes = 1L << 20;
        public const int MaxClosedFiles = 16;
        /// <summary>The most lines waiting for the writer; beyond it a line is dropped and counted.</summary>
        public const int MaxQueuedLines = 4096;

        private readonly string directory;
        private readonly object gate = new();
        private readonly Queue<(DateTime at, string line)> queue = new();
        private readonly Thread writer;
        private FileStream? stream;
        private long length;
        private long dropped;
        private bool closing;
        /// <summary>Lines taken off the queue but not yet on disk (for <see cref="Drain"/>).</summary>
        private int writing;

        public AppLogFile(string directory)
        {
            this.directory = directory;
            Directory.CreateDirectory(directory);
            Roll(DateTime.UtcNow);
            Open();
            writer = new Thread(WriteLoop) { IsBackground = true, Name = "AppLogFile" };
            writer.Start();
        }

        public string ActivePath => Path.Combine(directory, ActiveName);

        /// <summary>Lines dropped because the writer fell behind, since the file opened.</summary>
        public long Dropped => Interlocked.Read(ref dropped);

        /// <summary>
        /// Queue one line, its continuation lines indented; returns at once from any thread. The
        /// writer rolls first when it would pass the bound.
        /// </summary>
        public void Write(DateTime at, string level, string message)
        {
            var line = at.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ") + " " + level + " "
                + message.Replace("\r", "").Replace("\n", "\n    ") + "\n";
            lock (gate)
            {
                if (closing) return;
                if (queue.Count >= MaxQueuedLines)
                {
                    dropped++;
                    return;
                }
                queue.Enqueue((at, line));
                if (queue.Count == 1) Monitor.Pulse(gate);
            }
        }

        /// <summary>Wait until every line queued so far is on disk, at most <paramref name="timeout"/>.</summary>
        public bool Drain(TimeSpan timeout)
        {
            var until = DateTime.UtcNow + timeout;
            lock (gate)
            {
                while (queue.Count > 0 || writing > 0)
                {
                    var left = until - DateTime.UtcNow;
                    if (left <= TimeSpan.Zero) return false;
                    Monitor.Wait(gate, left);
                }
            }
            return true;
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (closing) return;
                closing = true;
                Monitor.PulseAll(gate);
            }
            // The queue is bounded, so what is left is written in well under a second; a disk
            // that will not take it does not hold the quit.
            writer.Join(TimeSpan.FromSeconds(2));
        }

        private void WriteLoop()
        {
            var batch = new List<(DateTime at, string line)>();
            long reportedDrops = 0;
            while (true)
            {
                bool last;
                long drops;
                lock (gate)
                {
                    while (queue.Count == 0 && !closing) Monitor.Wait(gate);
                    while (queue.Count > 0) batch.Add(queue.Dequeue());
                    writing = batch.Count;
                    last = closing;
                    drops = dropped;
                }
                try
                {
                    if (drops > reportedDrops)
                    {
                        Append(DateTime.UtcNow, DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
                            + " WARNING [RealityLog] AppLogFile: " + (drops - reportedDrops)
                            + " log line(s) dropped: the writer fell behind\n");
                        reportedDrops = drops;
                    }
                    foreach (var (at, line) in batch) Append(at, line);
                    stream?.Flush();
                }
                catch (Exception)
                {
                    // A full or failing disk loses log lines, never the app.
                }
                batch.Clear();
                lock (gate)
                {
                    writing = 0;
                    Monitor.PulseAll(gate);
                }
                if (last) break;
            }
            Close();
        }

        private void Append(DateTime at, string line)
        {
            if (stream == null) return;
            var bytes = Encoding.UTF8.GetBytes(line);
            if (length > 0 && length + bytes.Length > RollBytes)
            {
                stream.Flush();
                Close();
                Roll(at);
                Open();
                if (stream == null) return;
            }
            stream.Write(bytes, 0, bytes.Length);
            length += bytes.Length;
        }

        private void Open()
        {
            stream = new FileStream(ActivePath, FileMode.Append, FileAccess.Write, FileShare.Read);
            length = stream.Length;
        }

        private void Close()
        {
            stream?.Dispose();
            stream = null;
        }

        /// <summary>The active file, if it holds anything, becomes a closed one; the oldest closed
        /// files beyond the bound go.</summary>
        private void Roll(DateTime at)
        {
            var active = ActivePath;
            if (File.Exists(active))
            {
                if (new FileInfo(active).Length > 0)
                {
                    var closed = Path.Combine(directory, "app-" + at.ToUniversalTime().ToString("yyyyMMddTHHmmssfffZ") + ".log");
                    for (var n = 1; File.Exists(closed); n++)
                    {
                        closed = Path.Combine(directory, "app-" + at.ToUniversalTime().ToString("yyyyMMddTHHmmssfffZ") + "-" + n + ".log");
                    }
                    File.Move(active, closed);
                }
                else
                {
                    File.Delete(active);
                }
            }
            foreach (var old in Directory.GetFiles(directory, "app-*.log").OrderByDescending(p => p, StringComparer.Ordinal)
                         .Skip(MaxClosedFiles))
            {
                File.Delete(old);
            }
        }
    }
}
