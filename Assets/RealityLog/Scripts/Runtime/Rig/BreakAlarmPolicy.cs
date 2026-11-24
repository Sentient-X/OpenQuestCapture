#nullable enable

namespace RealityLog.Rig
{
    /// <summary>Where the break alarm stands after one look.</summary>
    public enum BreakAlarmVerdict
    {
        /// <summary>No collector on the pod, a take is recording, or the rig refuses Start for a device fault: silent, no clock running.</summary>
        Off,
        /// <summary>A collector is on the pod and nothing records: the clock runs, silent (under the grace, or the headset is off a head).</summary>
        Counting,
        /// <summary>A collector is on the pod, the headset is on a head, and the last take ended the grace or longer ago: the alarm sounds.</summary>
        Sounding,
    }

    /// <summary>
    /// The floor's rule (owner, 2026-11-23; 45 s and the clock from the last take, 2026-11-24): the
    /// pods collect 18 hours a day in three shifts with no breaks inside a shift, so while a collector
    /// is signed into the pod (floor.collector) and the last take, good or failed, ended more than
    /// <see cref="GraceSeconds"/> ago, a worn headset sounds an alarm until a take records again.
    /// A Start the rig refuses never recorded, so it is not a take and does not reset the clock.
    /// The clock starts when a take ends; with no take since, when the collector signed in or the app
    /// started. Taking the headset off silences it but does not reset the clock (build 90 reset it,
    /// so lifting the headset bought a fresh grace): back on a head past the grace, it sounds at once.
    /// Signing out of the pod is the only way to stop it without recording. A Start the rig refuses
    /// for a faulted device (the encoders' start refusal) is not a break the collector can end, so a
    /// device fault stops the clock; the pod's fault card and page carry that.
    /// Pure, no UnityEngine (tools/sx-rig-stub/BreakAlarmHarness.cs). Main thread only.
    /// </summary>
    public sealed class BreakAlarmPolicy
    {
        public const double GraceSeconds = 45.0;

        private double? idleSince;

        /// <summary>
        /// One look. <paramref name="now"/> is a monotonic clock in seconds; <paramref name="signedIn"/>
        /// a collector is signed into this enrolled pod; <paramref name="worn"/> the headset is on a head
        /// (null: unread, treated as off); <paramref name="deviceRefusal"/> the rig refuses Start for a
        /// device fault; <paramref name="collecting"/> a take is recording or stopping.
        /// </summary>
        public BreakAlarmVerdict Observe(double now, bool signedIn, bool? worn, bool deviceRefusal, bool collecting)
        {
            if (!signedIn || deviceRefusal || collecting)
            {
                idleSince = null;
                return BreakAlarmVerdict.Off;
            }
            idleSince ??= now;
            return worn == true && now - idleSince.Value >= GraceSeconds ? BreakAlarmVerdict.Sounding : BreakAlarmVerdict.Counting;
        }

        /// <summary>Seconds since the last take ended (or the collector signed in); null when the clock is not running.</summary>
        public double? IdleSeconds(double now) => idleSince is double since ? now - since : (double?)null;
    }
}
