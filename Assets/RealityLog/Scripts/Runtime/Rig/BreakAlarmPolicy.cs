#nullable enable

namespace RealityLog.Rig
{
    /// <summary>Where the break alarm stands after one look.</summary>
    public enum BreakAlarmVerdict
    {
        /// <summary>No collector on the pod, or a take is recording: silent, no clock running.</summary>
        Off,
        /// <summary>A collector is on the pod and nothing records, for less than the grace.</summary>
        Counting,
        /// <summary>A collector is on the pod and nothing has recorded for the grace or longer: the alarm sounds.</summary>
        Sounding,
    }

    /// <summary>
    /// The floor's rule (owner, 2026-11-23): the pods collect 18 hours a day in three shifts with no
    /// breaks inside a shift, so while a collector is signed into the pod (floor.collector) and the
    /// pod records nothing for longer than <see cref="GraceSeconds"/>, the headset sounds an alarm
    /// until a take records again. Signing out of the pod is the only way to stop it without
    /// recording. The clock starts when the pod became idle with a collector on it (the last take
    /// ended, the collector signed in, or the app started); a take that starts resets it.
    /// Pure, no UnityEngine (tools/sx-rig-stub/BreakAlarmHarness.cs). Main thread only.
    /// </summary>
    public sealed class BreakAlarmPolicy
    {
        public const double GraceSeconds = 30.0;

        private double? idleSince;

        /// <summary>
        /// One look. <paramref name="now"/> is a monotonic clock in seconds; <paramref name="armed"/>
        /// a collector is signed into this enrolled pod; <paramref name="collecting"/> a take is
        /// recording or stopping.
        /// </summary>
        public BreakAlarmVerdict Observe(double now, bool armed, bool collecting)
        {
            if (!armed || collecting)
            {
                idleSince = null;
                return BreakAlarmVerdict.Off;
            }
            idleSince ??= now;
            return now - idleSince.Value >= GraceSeconds ? BreakAlarmVerdict.Sounding : BreakAlarmVerdict.Counting;
        }

        /// <summary>Seconds the pod has been idle with a collector on it; null when the clock is not running.</summary>
        public double? IdleSeconds(double now) => idleSince is double since ? now - since : (double?)null;
    }
}
