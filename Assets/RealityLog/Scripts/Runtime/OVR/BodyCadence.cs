# nullable enable

using System;
using System.Globalization;
using System.Threading;

namespace RealityLog.OVR
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
    /// The body tracker's health as HeadTrackingMonitor (assembly RealityLog) measures it, for
    /// BodyTrackingLogger (assembly RealityLog.OVR, which RealityLog references, so the logger
    /// cannot see the monitor): since when the rate has refused starts, and the restarts the
    /// logger made. Any thread.
    /// </summary>
    public static class BodyHealth
    {
        private const long None = long.MinValue;
        private static long degradedSinceTicks = None;
        private static int restarts;
        private static int restartPending;
        private static string summary = "";

        /// <summary>The monitor's verdict this refresh: whether the rate refuses, and its figures for a log line.</summary>
        public static void Observe(bool degraded, string figures)
        {
            Volatile.Write(ref summary, figures);
            if (degraded) Interlocked.CompareExchange(ref degradedSinceTicks, System.Diagnostics.Stopwatch.GetTimestamp(), None);
            else Interlocked.Exchange(ref degradedSinceTicks, None);
        }

        /// <summary>Seconds the rate has refused starts; 0 while it admits or is unmeasured.</summary>
        public static double DegradedSeconds
        {
            get
            {
                var since = Interlocked.Read(ref degradedSinceTicks);
                return since == None ? 0
                    : (System.Diagnostics.Stopwatch.GetTimestamp() - since) / (double)System.Diagnostics.Stopwatch.Frequency;
            }
        }

        public static string Summary => Volatile.Read(ref summary);
        public static int Restarts => Volatile.Read(ref restarts);

        /// <summary>The logger restarted the tracker: count it, and have the monitor measure the rate over.</summary>
        public static void NoteRestart()
        {
            Interlocked.Increment(ref restarts);
            Interlocked.Exchange(ref degradedSinceTicks, None);
            Volatile.Write(ref restartPending, 1);
        }

        /// <summary>True once per restart, for the monitor to reset its window.</summary>
        public static bool TakeRestart() => Interlocked.Exchange(ref restartPending, 0) == 1;

        /// <summary>Back to launch state (tests and Editor play mode).</summary>
        public static void Reset()
        {
            Interlocked.Exchange(ref degradedSinceTicks, None);
            Volatile.Write(ref restarts, 0);
            Volatile.Write(ref restartPending, 0);
            Volatile.Write(ref summary, "");
        }

        /// <summary>For tests: pretend the rate has refused since <paramref name="ticks"/>.</summary>
        internal static void SetDegradedSince(long ticks) => Interlocked.Exchange(ref degradedSinceTicks, ticks);
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

    /// <summary>
    /// Whether someone is using the headset now, for the recoveries that only help a person
    /// (BodyTrackingLogger's idle tracker restart, FrozenHeadRecovery's self relaunch): this
    /// app has the headset's focus, and in the last <see cref="IdleAfterSeconds"/> the head
    /// moved (more than <see cref="MoveMetres"/> or <see cref="TurnDegrees"/> from where it
    /// last rested) or a controller button went down. The runtime's user-present flag cannot
    /// say it: a pod headset kept awake (provision-headset.sh --keep-awake broadcasts
    /// vrpowermanager prox_close) reads "mounted" lying on a table (pod5, 2026-11-15:
    /// "Virtual proximity state: CLOSE", HEADSET_MOUNTED while unworn). Main thread writes,
    /// any thread reads; no UnityEngine dependency (tools/sx-rig-stub/HeadTrackingMonitorHarness.cs).
    /// </summary>
    public sealed class Attendance
    {
        public const double IdleAfterSeconds = 60.0;
        public const float MoveMetres = 0.015f;
        public const float TurnDegrees = 3f;
        private const long None = long.MinValue;

        /// <summary>The app's one watch, fed by HeadTrackingMonitor and RigCoordinator.</summary>
        public static readonly Attendance Shared = new();

        private readonly object gate = new();
        private bool focused;
        private bool anchored;
        private float ax, ay, az, aqx, aqy, aqz, aqw;
        private long lastActivityTicks = None;

        /// <summary>One look at the head (main thread, every frame); <paramref name="nowTicks"/> a Stopwatch timestamp.</summary>
        public void Observe(bool hasFocus, bool headTracked, float x, float y, float z,
            float qx, float qy, float qz, float qw, long nowTicks)
        {
            lock (gate)
            {
                focused = hasFocus;
                if (!headTracked) return;
                if (!anchored)
                {
                    Anchor(x, y, z, qx, qy, qz, qw);
                    return;
                }
                var dx = x - ax; var dy = y - ay; var dz = z - az;
                var moved = Math.Sqrt(dx * dx + dy * dy + dz * dz) > MoveMetres;
                var dot = Math.Min(1.0, Math.Abs(qx * aqx + qy * aqy + qz * aqz + qw * aqw));
                var turned = 2.0 * Math.Acos(dot) * 180.0 / Math.PI > TurnDegrees;
                if (moved || turned)
                {
                    Anchor(x, y, z, qx, qy, qz, qw);
                    lastActivityTicks = nowTicks;
                }
            }
        }

        /// <summary>A controller button went down (any thread).</summary>
        public void Input(long nowTicks)
        {
            lock (gate) lastActivityTicks = nowTicks;
        }

        /// <summary>Focused, and the head moved or a button went down in the last <see cref="IdleAfterSeconds"/>.</summary>
        public bool Attended(long nowTicks)
        {
            lock (gate)
            {
                return focused && lastActivityTicks != None
                    && (nowTicks - lastActivityTicks) / (double)System.Diagnostics.Stopwatch.Frequency < IdleAfterSeconds;
            }
        }

        /// <summary>Back to launch state (tests and Editor play mode).</summary>
        public void Reset()
        {
            lock (gate)
            {
                focused = false;
                anchored = false;
                lastActivityTicks = None;
            }
        }

        private void Anchor(float x, float y, float z, float qx, float qy, float qz, float qw)
        {
            ax = x; ay = y; az = z; aqx = qx; aqy = qy; aqz = qz; aqw = qw;
            anchored = true;
        }
    }
}
