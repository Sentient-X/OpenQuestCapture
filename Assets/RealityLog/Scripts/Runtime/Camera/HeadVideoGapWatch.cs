# nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace RealityLog.Camera
{
    /// <summary>
    /// One eye's recorder counters at one poll, read through the recorder's existing JNI surface
    /// (<c>getSelectedFrameCount</c> and the live <c>stage_max_ns</c> of <c>getCaptureReportJson</c>).
    /// </summary>
    public readonly struct HeadVideoSample
    {
        /// <summary>Milliseconds since the recording was started (unscaled, monotonic).</summary>
        public long ElapsedMs { get; }
        /// <summary>Frames the encoder has emitted so far (the recorder's selected timeline).</summary>
        public long WrittenFrames { get; }
        /// <summary>The longest interval between two camera frame callbacks so far, ns; 0 while unknown.</summary>
        public long FrameIntervalMaxNs { get; }
        /// <summary>The longest single camera frame callback so far, ns; 0 while unknown.</summary>
        public long FrameCallbackMaxNs { get; }

        public HeadVideoSample(long elapsedMs, long writtenFrames, long frameIntervalMaxNs, long frameCallbackMaxNs)
        {
            ElapsedMs = elapsedMs;
            WrittenFrames = writtenFrames;
            FrameIntervalMaxNs = frameIntervalMaxNs;
            FrameCallbackMaxNs = frameCallbackMaxNs;
        }
    }

    /// <summary>A head camera's exposure stream stalling mid-episode: which eye, how long, when, and where.</summary>
    public sealed class HeadVideoGap
    {
        public string Eye { get; }
        public string Side { get; }
        /// <summary>Selected frames the eye fell behind the 30 Hz grid by (the seal's inferred drops).</summary>
        public long LostFrames { get; }
        /// <summary>The longest interval between two camera frames of this eye, ms.</summary>
        public double GapMs { get; }
        /// <summary>About when the stall began, seconds into the recording.</summary>
        public double AtSeconds { get; }
        /// <summary>
        /// True when the eye's own camera thread took the whole gap inside one frame callback (it was
        /// blocked rendering into or swapping onto the encoder); false when the camera simply delivered
        /// no frame for that long (the camera service or HAL withheld them).
        /// </summary>
        public bool CameraThreadBlocked { get; }
        public double FrameCallbackMaxMs { get; }

        /// <summary>The notice the seal shows for this refusal (sx packages/sx-pod-runtime notice.rs), shown now instead.</summary>
        public const string NoticeCode = "seal_video_gap";

        public HeadVideoGap(string eye, long lostFrames, double gapMs, double atSeconds, bool cameraThreadBlocked, double frameCallbackMaxMs)
        {
            Eye = eye;
            Side = CaptureFailureWatch.SideOf(eye);
            LostFrames = lostFrames;
            GapMs = gapMs;
            AtSeconds = atSeconds;
            CameraThreadBlocked = cameraThreadBlocked;
            FrameCallbackMaxMs = frameCallbackMaxMs;
        }

        /// <summary>One sentence for logs and the session's refusal record.</summary>
        public string Describe()
        {
            var where = CameraThreadBlocked
                ? $"its camera thread was blocked for {FrameCallbackMaxMs / 1000.0:F2} s"
                : "the camera delivered no frames";
            return string.Format(CultureInfo.InvariantCulture,
                "the {0} head camera ({1}) skipped {2} frames ({3:F2} s) about {4:F0} s into the take; {5}; the seal refuses any gap over 50 ms",
                Side, Eye, LostFrames, GapMs / 1000.0, AtSeconds, where);
        }
    }

    /// <summary>
    /// Watches both head cameras' exposure streams while an episode records, through the counters the
    /// recorder already exposes, and reports a stall the seal is certain to refuse as soon as it happens.
    ///
    /// On 2026-11-22, 109 of 891 takes across 12 pods and three builds were refused for a
    /// <c>head_video interior gap</c>: 66 of exactly one lost 20 ms lattice exposure (a 60 ms gap) and
    /// 43 of a 1.00–1.02 s stall of one eye, found only at the seal, minutes after the fact (the refused
    /// takes averaged 434 s). The recorder names a failed eye at once (<see cref="CaptureFailureWatch"/>)
    /// but not one that silently lost frames. This watch reads two witnesses per eye at ~4 Hz:
    /// the encoder's written frame count against the 30 Hz grid (a stall leaves the count permanently
    /// behind), and the recorder's longest camera-frame interval (a stall is a long interval). A stall
    /// of at least <see cref="MinLostFrames"/> frames, seen by both for <see cref="PersistPolls"/> polls,
    /// is reported once, with which eye, how long, when, and whether the eye's own camera thread took the
    /// gap (blocked on the encoder) or the camera delivered nothing (the camera service withheld frames):
    /// the attribution the refusal record lacked. The encoder's latency and the poll's phase move the
    /// count by a frame or two, so a single lost exposure (the 60 ms gap, 60% of the refusals) is below
    /// this watch's floor; the stop logs the recorder's capture report, which names it.
    ///
    /// Pure: no UnityEngine, tested on the host (tools/sx-rig-stub/HeadVideoGapHarness.cs).
    /// </summary>
    public sealed class HeadVideoGapWatch
    {
        /// <summary>The recorder's selected timeline, frames per second (capture contract 1.4.x).</summary>
        public const double FrameRateHz = 30.0;
        /// <summary>A stall this many selected frames long (0.3 s) is far beyond encoder latency and poll phase.</summary>
        public const int MinLostFrames = 9;
        /// <summary>The camera-frame interval that corroborates it: 0.3 s against a 20 ms lattice.</summary>
        public const long MinIntervalNs = 300_000_000;
        /// <summary>Both witnesses must agree on this many consecutive polls.</summary>
        public const int PersistPolls = 2;
        /// <summary>The first frames settle the encoder's lag; no floor is taken before this many are written.</summary>
        public const long SettleFrames = 30;
        /// <summary>A callback this share of the gap long means the thread, not the camera, took the gap.</summary>
        public const double BlockedShare = 0.8;

        private sealed class Eye
        {
            public readonly string Name;
            public readonly Func<HeadVideoSample?> Read;
            public double Floor = double.PositiveInfinity;
            public int Exceeding;
            public double LastDeficit;
            public long LastIntervalNs;

            public Eye(string name, Func<HeadVideoSample?> read)
            {
                Name = name;
                Read = read;
            }
        }

        private readonly Eye[] eyes;
        private bool reported;

        /// <param name="eyes">Each eye's file name and its reader: the counters, or null while it is not recording or cannot be asked.</param>
        public HeadVideoGapWatch((string eye, Func<HeadVideoSample?> read)[] eyes)
        {
            this.eyes = new Eye[eyes.Length];
            for (var i = 0; i < eyes.Length; i++)
            {
                this.eyes[i] = new Eye(eyes[i].eye, eyes[i].read);
            }
        }

        /// <summary>Frames an eye is behind the 30 Hz grid at this sample (its encoder lag plus any lost exposures).</summary>
        public static double Deficit(HeadVideoSample sample) =>
            sample.ElapsedMs * FrameRateHz / 1000.0 - sample.WrittenFrames;

        /// <summary>The first stall, once; null while both eyes keep their cadence and after it was reported.</summary>
        public HeadVideoGap? Poll()
        {
            if (reported) return null;
            foreach (var eye in eyes)
            {
                HeadVideoSample? sample;
                try
                {
                    sample = eye.Read();
                }
                catch (Exception)
                {
                    // A reader that cannot ask (an older AAR, a closed instance) is not a stall.
                    continue;
                }
                if (sample is not { } s) continue;
                var deficit = Deficit(s);
                eye.LastDeficit = deficit;
                eye.LastIntervalNs = s.FrameIntervalMaxNs;
                if (s.WrittenFrames < SettleFrames) continue;
                if (deficit < eye.Floor) eye.Floor = deficit;
                var lost = deficit - eye.Floor;
                var stalled = lost >= MinLostFrames && s.FrameIntervalMaxNs >= MinIntervalNs;
                eye.Exceeding = stalled ? eye.Exceeding + 1 : 0;
                if (eye.Exceeding < PersistPolls) continue;
                reported = true;
                var gapMs = s.FrameIntervalMaxNs / 1e6;
                var callbackMs = s.FrameCallbackMaxNs / 1e6;
                var at = Math.Max(0.0, s.ElapsedMs / 1000.0 - gapMs / 1000.0 - (PersistPolls - 1) * CaptureFailureWatch.PollIntervalMs / 1000.0);
                return new HeadVideoGap(
                    eye.Name,
                    (long)Math.Round(lost),
                    gapMs,
                    at,
                    callbackMs >= BlockedShare * gapMs,
                    callbackMs);
            }
            return null;
        }

        /// <summary>Each eye's last deficit and interval, for the stop's log line: "left_camera.mp4 deficit 3.2 interval 25 ms".</summary>
        public IEnumerable<string> Describe()
        {
            foreach (var eye in eyes)
            {
                yield return string.Format(CultureInfo.InvariantCulture, "{0} behind grid {1:F1} frames (floor {2:F1}), longest frame interval {3:F0} ms",
                    eye.Name, eye.LastDeficit, double.IsPositiveInfinity(eye.Floor) ? 0.0 : eye.Floor, eye.LastIntervalNs / 1e6);
            }
        }
    }

    /// <summary>
    /// Reads the recorder's compact capture report (<c>getCaptureReportJson</c>) without a JSON library,
    /// so the watch stays pure: the report is one object the recorder formats itself, with
    /// <c>"stage_max_ns":{"camera_frame_callback":N,"camera_frame_interval":N,...}</c>.
    /// </summary>
    public static class CaptureReportJson
    {
        private static readonly Dictionary<string, Regex> Patterns = new();

        /// <summary>The integer value of <paramref name="key"/> in <paramref name="json"/>, or null when it is absent or not an integer.</summary>
        public static long? Integer(string? json, string key)
        {
            if (string.IsNullOrEmpty(json)) return null;
            Regex pattern;
            lock (Patterns)
            {
                if (!Patterns.TryGetValue(key, out pattern))
                {
                    pattern = new Regex("\"" + Regex.Escape(key) + "\"\\s*:\\s*(-?\\d+)(?![.\\d])", RegexOptions.CultureInvariant);
                    Patterns[key] = pattern;
                }
            }
            var match = pattern.Match(json!);
            if (!match.Success) return null;
            return long.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : (long?)null;
        }

        /// <summary>One stage's maximum from <c>stage_max_ns</c>, ns; 0 when the report lacks it.</summary>
        public static long StageMaxNs(string? json, string stage) => Integer(json, stage) ?? 0L;
    }
}
