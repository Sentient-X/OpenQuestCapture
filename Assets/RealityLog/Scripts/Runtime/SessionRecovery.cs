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
        public const string ManifestFileName = "stream_manifest.json";
        private const string CameraTimestampsSuffix = "_camera_timestamps.csv";
        private static readonly Regex SessionName = new(@"^\d{8}_\d{6}$", RegexOptions.Compiled);

        /// <summary>
        /// Writes a recovered manifest into every session under <paramref name="dataPath"/> that
        /// has none, except <paramref name="activeSession"/>. Returns the sessions it closed out.
        /// Never throws for one bad session; the rest are still closed out.
        /// </summary>
        public static List<string> CloseOut(string dataPath, string? activeSession, Action<string, Exception>? onError = null)
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
                    WriteAtomically(Path.Combine(directory, ManifestFileName), BuildManifest(directory));
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
        /// The recovered manifest: the same <c>{"schema": 1, "streams": [...]}</c> a stop writes,
        /// one stream per CSV (rows = complete lines after the header; a torn last line is not
        /// counted), the head cameras' encoded frames from their timestamp sidecars, and
        /// <c>"interrupted": true</c>.
        /// </summary>
        public static string BuildManifest(string sessionDirectory)
        {
            var streams = new List<string>();
            var cameras = new List<string>();
            foreach (var file in Directory.GetFiles(sessionDirectory, "*.csv").OrderBy(f => f, StringComparer.Ordinal))
            {
                var name = Path.GetFileName(file);
                var rows = CompleteRows(file);
                if (name.EndsWith(CameraTimestampsSuffix, StringComparison.Ordinal))
                {
                    cameras.Add("{\"file\":\"" + Escape(name) + "\",\"frames\":" + rows.ToString(CultureInfo.InvariantCulture) + "}");
                }
                else
                {
                    streams.Add("{\"file\":\"" + Escape(name) + "\",\"rows\":" + rows.ToString(CultureInfo.InvariantCulture) + ",\"stats\":{}}");
                }
            }
            return "{\"schema\":1,\"interrupted\":true,\"recovered_at_utc\":\"" +
                DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture) +
                "\",\"streams\":[" + string.Join(",", streams) + "],\"cameras\":[" + string.Join(",", cameras) + "]}";
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

        private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}
