#nullable enable

using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace RealityLog.Rig
{
    /// <summary>What the frozen-head watch decided on one look.</summary>
    public enum RelaunchVerdict
    {
        /// <summary>Head poses advance, or the app is not running frames: nothing to do.</summary>
        Healthy,
        /// <summary>Frozen, but not yet for <see cref="SelfRelaunchPolicy.HoldSeconds"/>.</summary>
        Waiting,
        /// <summary>Frozen long enough, but a recording, an arming or a seal is in progress.</summary>
        Held,
        /// <summary>Frozen long enough, but the app relaunched itself too recently.</summary>
        RateLimited,
        /// <summary>Relaunch now.</summary>
        Relaunch,
    }

    /// <summary>
    /// When the app relaunches itself because the OVR runtime stopped delivering head poses
    /// (sx#4210: on Quest 3S the stream freezes until the app restarts, and every start is then
    /// refused "head tracking stopped"). Pure: no UnityEngine, tested on the host
    /// (tools/sx-rig-stub/SelfRelaunchHarness.cs).
    ///
    /// It relaunches only when all hold: the app is running frames (not paused by the OS) and
    /// someone is using the headset (the caller folds RealityLog.OVR.Attendance into <c>appRunning</c>); the
    /// head pose has been older than <see cref="FrozenAfterMs"/> for <see cref="HoldSeconds"/>
    /// without a break; nothing records, arms or seals (<c>busy</c> is null); and the last self
    /// relaunch was at least <see cref="MinInterval"/> ago. A relaunch never interrupts capture or
    /// a seal, and a headset whose tracking stays frozen after one relaunch is left for a person,
    /// not restarted in a loop.
    /// </summary>
    public sealed class SelfRelaunchPolicy
    {
        /// <summary>A head pose older than this is frozen (HeadTrackingMonitor.StartMaxPoseAgeMs refuses a start on it).</summary>
        public const double FrozenAfterMs = 1000.0;
        /// <summary>How long the freeze must last, uninterrupted, before the app relaunches.</summary>
        public const double HoldSeconds = 12.0;
        /// <summary>The least time between two self relaunches.</summary>
        public static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(30);

        private double? frozenSince;

        /// <summary>
        /// One look. <paramref name="nowSeconds"/> is a monotonic clock;
        /// <paramref name="headPoseAgeMs"/> null when the runtime never delivered a pose since
        /// launch (a headset not yet worn, never a freeze); <paramref name="busy"/> names what is in
        /// progress, null when nothing is; <paramref name="lastRelaunchUtc"/> the last self relaunch.
        /// </summary>
        public RelaunchVerdict Observe(double nowSeconds, DateTime utcNow, double? headPoseAgeMs, bool appRunning,
            string? busy, DateTime? lastRelaunchUtc)
        {
            if (!appRunning || headPoseAgeMs is not double age || age <= FrozenAfterMs)
            {
                frozenSince = null;
                return RelaunchVerdict.Healthy;
            }
            // The freeze began when the last pose arrived, not when this watch first saw it.
            frozenSince ??= nowSeconds - age / 1000.0;
            if (nowSeconds - frozenSince.Value < HoldSeconds) return RelaunchVerdict.Waiting;
            if (busy != null) return RelaunchVerdict.Held;
            if (lastRelaunchUtc is DateTime last && utcNow - last < MinInterval) return RelaunchVerdict.RateLimited;
            return RelaunchVerdict.Relaunch;
        }

        /// <summary>Seconds the head pose has been frozen, as this watch measures it; null when it is not.</summary>
        public double? FrozenForSeconds(double nowSeconds) => frozenSince is double since ? nowSeconds - since : (double?)null;
    }

    /// <summary>
    /// The self relaunches kept across launches in <c>self_relaunch.json</c> (persistentDataPath):
    /// <c>{"count": n, "last_utc": "...", "last_reason": "..."}</c>. Read at launch for the
    /// status and the rate limit; written, flushed, before the app relaunches. A missing or
    /// unreadable file reads as none.
    /// </summary>
    public sealed class SelfRelaunchLedger
    {
        public const string FileName = "self_relaunch.json";

        public int Count { get; private set; }
        public DateTime? LastUtc { get; private set; }
        public string? LastReason { get; private set; }

        public static SelfRelaunchLedger Read(string directory)
        {
            var ledger = new SelfRelaunchLedger();
            try
            {
                var text = File.ReadAllText(Path.Combine(directory, FileName));
                // Dates stay strings: Newtonsoft would otherwise turn last_utc into a local DateTime.
                using var reader = new Newtonsoft.Json.JsonTextReader(new StringReader(text))
                {
                    DateParseHandling = Newtonsoft.Json.DateParseHandling.None,
                };
                var json = Newtonsoft.Json.Linq.JObject.Load(reader);
                if (json["count"] is Newtonsoft.Json.Linq.JValue { Type: Newtonsoft.Json.Linq.JTokenType.Integer } count && (long)count >= 0)
                    ledger.Count = (int)Math.Min((long)count, int.MaxValue);
                if (json["last_utc"] is Newtonsoft.Json.Linq.JValue { Type: Newtonsoft.Json.Linq.JTokenType.String } last
                    && DateTime.TryParse((string?)last, CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var at))
                    ledger.LastUtc = at;
                if (json["last_reason"] is Newtonsoft.Json.Linq.JValue { Type: Newtonsoft.Json.Linq.JTokenType.String } reason)
                    ledger.LastReason = (string?)reason;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException
                || ex is Newtonsoft.Json.JsonException || ex is InvalidCastException)
            {
            }
            return ledger;
        }

        /// <summary>Counts one relaunch now and writes the file through (fsync) before returning.</summary>
        public void Record(string directory, DateTime utcNow, string reason)
        {
            Count += 1;
            LastUtc = utcNow;
            LastReason = reason;
            var json = new Newtonsoft.Json.Linq.JObject
            {
                ["count"] = Count,
                ["last_utc"] = utcNow.ToString("O", CultureInfo.InvariantCulture),
                ["last_reason"] = reason,
            }.ToString(Newtonsoft.Json.Formatting.None);
            var path = Path.Combine(directory, FileName);
            var temporary = path + ".tmp";
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var bytes = Encoding.UTF8.GetBytes(json);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
            if (File.Exists(path)) File.Delete(path);
            File.Move(temporary, path);
        }
    }
}
