# nullable enable

using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace RealityLog
{
    /// <summary>One CSV stream of a session: its file, rows written, and its stats JSON.</summary>
    public readonly struct StreamEntry
    {
        public readonly string File;
        public readonly long Rows;
        public readonly string StatsJson;

        public StreamEntry(string file, long rows, string statsJson)
        {
            File = file;
            Rows = rows;
            StatsJson = statsJson;
        }
    }

    /// <summary>
    /// The one writer of stream_manifest.json: <c>{"schema":1,"streams":[{"file","rows","stats"}]}</c>,
    /// written by a stop, and by a launch closing out a session a killed process left open,
    /// which adds only <c>"interrupted":true</c> and <c>"recovered_at_utc"</c>. No UnityEngine
    /// dependency, so it is tested on the host (tools/sx-rig-stub/test_binding.sh).
    /// </summary>
    public static class StreamManifest
    {
        public const string FileName = "stream_manifest.json";

        /// <summary>The <c>streams</c> array (also returned by /api/stop-recording).</summary>
        public static string StreamsJson(IEnumerable<StreamEntry> streams)
        {
            var json = new StringBuilder("[");
            foreach (var stream in streams)
            {
                if (json.Length > 1) json.Append(',');
                json.Append("{\"file\":\"").Append(Escape(stream.File))
                    .Append("\",\"rows\":").Append(stream.Rows.ToString(CultureInfo.InvariantCulture))
                    .Append(",\"stats\":").Append(string.IsNullOrEmpty(stream.StatsJson) ? "{}" : stream.StatsJson)
                    .Append('}');
            }
            return json.Append(']').ToString();
        }

        /// <summary>
        /// The manifest document. A stop passes no <paramref name="recoveredAtUtc"/>; a close-out
        /// passes when it recovered the session, which marks it interrupted.
        /// </summary>
        public static string Document(string streamsJson, string? recoveredAtUtc = null) =>
            recoveredAtUtc == null
                ? "{\"schema\":1,\"streams\":" + streamsJson + "}"
                : "{\"schema\":1,\"streams\":" + streamsJson + ",\"interrupted\":true,\"recovered_at_utc\":\"" +
                  Escape(recoveredAtUtc) + "\"}";

        private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}
