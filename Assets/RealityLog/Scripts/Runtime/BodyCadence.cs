# nullable enable

using System;
using System.Globalization;
using System.Threading;

namespace RealityLog
{
    /// <summary>
    /// How often the body runtime delivers a new state, over the last <see cref="WindowSeconds"/>
    /// of the OVR source clock. A degraded Meta body tracker keeps answering but advances its
    /// timestamp only every 160-320 ms (OQC#22); a single fresh sample can still pass a 100 ms
    /// age check, so admission also needs the rate. "Any change counts", as for the head pose:
    /// a runtime that restarts its clock never reads as frozen forever. Main thread writes,
    /// any thread reads. No UnityEngine dependency (tools/sx-rig-stub/HeadTrackingMonitorHarness.cs).
    /// </summary>
    public sealed class BodyRateWindow
    {
        /// <summary>The window the rate is measured over, seconds of OVR source time.</summary>
        public const double WindowSeconds = 1.5;
        // 90 Hz Update over 1.5 s is 135 advances; the ring holds more than any window can.
        private const int Capacity = 256;

        private readonly double[] advances = new double[Capacity];
        private int head;
        private int count;
        private double lastSourceTime = double.NaN;
        private double observingSince = double.NaN;
        private double? hz;
        private readonly object gate = new();

        /// <summary>
        /// One read of the body state: <paramref name="sourceTime"/> is the state's time (null when
        /// the read failed or carried no joints), <paramref name="now"/> the OVR source clock.
        /// </summary>
        public void Observe(double? sourceTime, double now)
        {
            lock (gate)
            {
                if (!Finite(now) || (Finite(observingSince) && now < observingSince))
                {
                    // An invalid or restarted source clock starts the measurement over.
                    ResetLocked();
                    if (!Finite(now)) return;
                }
                if (!Finite(observingSince)) observingSince = now;
                if (sourceTime is double t && Finite(t) && t > 0 && t != lastSourceTime)
                {
                    lastSourceTime = t;
                    advances[head] = t;
                    head = (head + 1) % Capacity;
                    if (count < Capacity) count++;
                }
                if (now - observingSince < WindowSeconds)
                {
                    hz = null;
                    return;
                }
                var n = 0;
                for (var i = 0; i < count; i++)
                {
                    var at = advances[(head - 1 - i + Capacity) % Capacity];
                    // Step.Render predicts ahead (the monitor allows 100 ms of future time).
                    if (at > now - WindowSeconds && at <= now + 0.1) n++;
                }
                hz = n / WindowSeconds;
            }
        }

        /// <summary>New body states per second over the window; null until a whole window was observed.</summary>
        public double? Hz { get { lock (gate) return hz; } }

        public void Reset() { lock (gate) ResetLocked(); }

        private void ResetLocked()
        {
            head = count = 0;
            lastSourceTime = observingSince = double.NaN;
            hz = null;
        }

        private static bool Finite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);
    }

    /// <summary>
    /// Gaps between consecutive body rows of one recording, by their OVR timestamps: the
    /// interval sx-seal's body check bounds at 100 ms (<c>quality.rs</c> <c>(30, 55, 100 ms)</c>).
    /// BodyTrackingLogger feeds the rows it writes, so the stop's stream manifest says how many
    /// gaps the seal will find; <see cref="Current"/> is the live recording's, for /api/status.
    /// Writer: the logger's thread. Readers: any thread.
    /// </summary>
    public sealed class BodyGapCounter
    {
        public const double GapLimitSeconds = 0.1;

        private static BodyGapCounter? current;
        /// <summary>The recording's counter while one logs; the last recording's after it stops.</summary>
        public static BodyGapCounter? Current => Volatile.Read(ref current);
        public static void Publish(BodyGapCounter counter) => Volatile.Write(ref current, counter);

        private double last = double.NaN;
        private long gaps;
        private long longestTicks; // max gap in 100 ns units, for Interlocked reads

        /// <summary>One written row's OVR timestamp (seconds). Rows only advance, as the logger writes them.</summary>
        public void Row(double ovrSeconds)
        {
            if (double.IsNaN(ovrSeconds) || double.IsInfinity(ovrSeconds)) return;
            if (!double.IsNaN(last))
            {
                var gap = ovrSeconds - last;
                if (gap > GapLimitSeconds) Interlocked.Increment(ref gaps);
                var ticks = (long)(gap * 1e7);
                if (ticks > Interlocked.Read(ref longestTicks)) Interlocked.Exchange(ref longestTicks, ticks);
            }
            last = ovrSeconds;
        }

        /// <summary>Gaps over 100 ms so far.</summary>
        public long Gaps => Interlocked.Read(ref gaps);

        /// <summary>The longest interval between rows so far, ms (0 before two rows).</summary>
        public double LongestGapMs => Interlocked.Read(ref longestTicks) / 1e4;

        /// <summary><c>"gaps_over_100ms":n,"max_gap_ms":x</c>, for a stats object.</summary>
        public string StatsFields() =>
            "\"gaps_over_100ms\":" + Gaps.ToString(CultureInfo.InvariantCulture)
            + ",\"max_gap_ms\":" + LongestGapMs.ToString("F1", CultureInfo.InvariantCulture);
    }
}
