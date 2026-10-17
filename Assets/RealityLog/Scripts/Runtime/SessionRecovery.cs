# nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace RealityLog
{
    /// <summary>
    /// Closes out sessions a killed process left open. stream_manifest.json is written last by
    /// a stop, so a session without one (the app was force-stopped or crashed mid-recording)
    /// never completes: the rig's seal waits for it forever. At launch every such session gets
    /// a manifest built from what is on disk, marked <c>"interrupted": true</c>, so the seal can
    /// judge it and the recordings list can say so. No UnityEngine dependency, so it is tested
    /// on the host (tools/sx-rig-stub/test_binding.sh).
    /// </summary>
    public static class SessionRecovery
    {
        public const string ManifestFileName = StreamManifest.FileName;
        private static readonly Regex SessionName = new(@"^\d{8}_\d{6}$", RegexOptions.Compiled);

        /// <summary>
        /// Writes a recovered manifest into every session under <paramref name="dataPath"/> that
        /// has none, except <paramref name="activeSession"/>. <paramref name="streamFiles"/> are
        /// the streams a stop lists, in its order (RecordingManager's loggers), so the recovered
        /// manifest names exactly what the stop's would. Returns the sessions it closed out.
        /// Never throws for one bad session; the rest are still closed out.
        /// </summary>
        public static List<string> CloseOut(string dataPath, string? activeSession, IReadOnlyList<string> streamFiles,
            Action<string, Exception>? onError = null)
        {
            var closed = new List<string>();
            if (!Directory.Exists(dataPath)) return closed;
            foreach (var directory in Directory.GetDirectories(dataPath))
            {
                var name = Path.GetFileName(directory);
                if (!SessionName.IsMatch(name) || name == activeSession) continue;
                if (File.Exists(Path.Combine(directory, ManifestFileName))) continue;
                try
                {
                    WriteAtomically(Path.Combine(directory, ManifestFileName), BuildManifest(directory, streamFiles));
                    closed.Add(name);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    onError?.Invoke(name, ex);
                }
            }
            return closed;
        }

        /// <summary>
        /// The recovered manifest, by the stop's own writer (<see cref="StreamManifest"/>): the
        /// same streams a stop lists, each with its complete rows counted from the file (a torn
        /// last line is not counted; a stream whose file is missing has 0 rows) and empty
        /// stats, plus only <c>interrupted</c> and <c>recovered_at_utc</c>.
        /// </summary>
        public static string BuildManifest(string sessionDirectory, IReadOnlyList<string> streamFiles)
        {
            var entries = streamFiles.Select(file =>
            {
                var path = Path.Combine(sessionDirectory, file);
                return new StreamEntry(file, File.Exists(path) ? CompleteRows(path) : 0, "{}");
            });
            return StreamManifest.Document(StreamManifest.StreamsJson(entries),
                DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture));
        }

        /// <summary>True when the session's manifest says it was closed out after a kill.</summary>
        public static bool IsInterrupted(string sessionDirectory)
        {
            var path = Path.Combine(sessionDirectory, ManifestFileName);
            if (!File.Exists(path)) return false;
            try
            {
                return File.ReadAllText(path).Contains("\"interrupted\":true");
            }
            catch (IOException)
            {
                return false;
            }
        }

        /// <summary>Lines ending in a newline, minus the header line; 0 for an empty file.</summary>
        public static long CompleteRows(string csvPath)
        {
            long newlines = 0;
            using (var stream = File.OpenRead(csvPath))
            {
                var buffer = new byte[1 << 16];
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    for (var i = 0; i < read; i++)
                    {
                        if (buffer[i] == (byte)'\n') newlines++;
                    }
                }
            }
            return Math.Max(0, newlines - 1);
        }

        private static void WriteAtomically(string path, string content)
        {
            var temporary = path + ".tmp";
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write))
            {
                var bytes = Encoding.UTF8.GetBytes(content);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
            File.Move(temporary, path);
        }

    }
}
