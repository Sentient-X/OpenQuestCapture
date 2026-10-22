#nullable enable

using System;
using System.IO;
using System.Linq;
using System.Text;

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
    /// </summary>
    public sealed class AppLogFile : IDisposable
    {
        public const string ActiveName = "app.log";
        public const long RollBytes = 1L << 20;
        public const int MaxClosedFiles = 16;

        private readonly string directory;
        private readonly object gate = new();
        private FileStream? stream;
        private long length;

        public AppLogFile(string directory)
        {
            this.directory = directory;
            Directory.CreateDirectory(directory);
            lock (gate)
            {
                Roll(DateTime.UtcNow);
                Open();
            }
        }

        public string ActivePath => Path.Combine(directory, ActiveName);

        /// <summary>One line, its continuation lines indented; rolls first when it would pass the bound.</summary>
        public void Write(DateTime at, string level, string message)
        {
            var line = at.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ") + " " + level + " "
                + message.Replace("\r", "").Replace("\n", "\n    ") + "\n";
            var bytes = Encoding.UTF8.GetBytes(line);
            lock (gate)
            {
                if (stream == null) return;
                if (length > 0 && length + bytes.Length > RollBytes)
                {
                    Close();
                    Roll(at);
                    Open();
                    if (stream == null) return;
                }
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush();
                length += bytes.Length;
            }
        }

        public void Dispose()
        {
            lock (gate)
            {
                Close();
            }
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
