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

        /// <summary>
        /// A stop's document with the take's <see cref="FrameWatchdog"/> report under
        /// <c>"main_thread"</c>; with none it is the plain stop document.
        /// </summary>
        public static string Document(string streamsJson, string? recoveredAtUtc, string? mainThreadJson)
        {
            var document = Document(streamsJson, recoveredAtUtc);
            return mainThreadJson == null ? document
                : document.Substring(0, document.Length - 1) + ",\"main_thread\":" + mainThreadJson + "}";
        }

        private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    /// <summary>
    /// The whole take's main-thread watchdog. Body tracking is polled on Unity's main thread
    /// (BodyTrackingLogger.FixedUpdate), and a stall cannot be caught up, so a main-thread
    /// stall of X ms is a body gap of X ms. Every frame over <see cref="SlowFrameMs"/> is logged
    /// with how many gen-0 collections ran in it, placed on the monotonic clock the body rows
    /// carry (<c>mono_time_ns</c>), and the take's count goes into stream_manifest.json as
    /// <c>"main_thread"</c>: a body gap that lines up with a slow frame is the app's stall, one
    /// with none the tracker's drop. No UnityEngine: the caller passes the clock and the count.
    /// </summary>
    public sealed class FrameWatchdog
    {
        public const double SlowFrameMs = 100;
        /// <summary>The slowest frames kept for the manifest.</summary>
        public const int Kept = 8;
        /// <summary>Slow frames logged one by one per take; the rest are only counted.</summary>
        public const int Logged = 40;

        private readonly object gate = new();
        private bool watching;
        private long lastNs;
        private int lastGc0;
        private int startGc0;
        private long frames;
        private int slowFrames;
        private int slowWithGc;
        private double longestMs;
        private readonly List<(long endNs, double ms, int gc0)> slowest = new();

        /// <summary>Starts a take's watch at monotonic <paramref name="nowNs"/>.</summary>
        public void Begin(long nowNs, int gc0)
        {
            lock (gate)
            {
                watching = true;
                lastNs = nowNs;
                lastGc0 = gc0;
                startGc0 = gc0;
                frames = 0;
                slowFrames = 0;
                slowWithGc = 0;
                longestMs = 0;
                slowest.Clear();
            }
        }

        /// <summary>One frame ending at <paramref name="nowNs"/>; the line to log when it was slow, else null.</summary>
        public string? Frame(long nowNs, int gc0)
        {
            lock (gate)
            {
                if (!watching) return null;
                var ms = (nowNs - lastNs) / 1e6;
                var collections = gc0 - lastGc0;
                lastNs = nowNs;
                lastGc0 = gc0;
                frames++;
                if (ms > longestMs) longestMs = ms;
                if (ms <= SlowFrameMs) return null;
                slowFrames++;
                if (collections > 0) slowWithGc++;
                slowest.Add((nowNs, ms, collections));
                slowest.Sort((a, b) => b.ms.CompareTo(a.ms));
                if (slowest.Count > Kept) slowest.RemoveAt(slowest.Count - 1);
                if (slowFrames > Logged) return null;
                return "main thread: slow frame " + ms.ToString("F0", CultureInfo.InvariantCulture) + " ms (gc0 +"
                    + collections.ToString(CultureInfo.InvariantCulture) + ") from mono "
                    + (nowNs - (long)(ms * 1e6)).ToString(CultureInfo.InvariantCulture) + " to "
                    + nowNs.ToString(CultureInfo.InvariantCulture) + " ns"
                    + (slowFrames == Logged ? "; further slow frames this take are only counted" : "");
            }
        }

        /// <summary>Ends the watch; the manifest's <c>"main_thread"</c> object, or null when none ran.</summary>
        public string? End(int gc0)
        {
            lock (gate)
            {
                if (!watching) return null;
                watching = false;
                var inv = CultureInfo.InvariantCulture;
                var json = new StringBuilder("{\"slow_frame_ms\":").Append(SlowFrameMs.ToString(inv))
                    .Append(",\"frames\":").Append(frames.ToString(inv))
                    .Append(",\"slow_frames\":").Append(slowFrames.ToString(inv))
                    .Append(",\"slow_frames_with_gc\":").Append(slowWithGc.ToString(inv))
                    .Append(",\"longest_ms\":").Append(System.Math.Round(longestMs, 1).ToString(inv))
                    .Append(",\"gc0_collections\":").Append((gc0 - startGc0).ToString(inv))
                    .Append(",\"slowest\":[");
                for (var i = 0; i < slowest.Count; i++)
                {
                    var (endNs, ms, collections) = slowest[i];
                    if (i > 0) json.Append(',');
                    json.Append("{\"start_mono_ns\":").Append((endNs - (long)(ms * 1e6)).ToString(inv))
                        .Append(",\"end_mono_ns\":").Append(endNs.ToString(inv))
                        .Append(",\"ms\":").Append(System.Math.Round(ms, 1).ToString(inv))
                        .Append(",\"gc0\":").Append(collections.ToString(inv)).Append('}');
                }
                return json.Append("]}").ToString();
            }
        }
    }
}
