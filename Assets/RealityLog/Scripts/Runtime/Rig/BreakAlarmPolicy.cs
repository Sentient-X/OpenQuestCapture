#nullable enable

namespace RealityLog.Rig
{
    /// <summary>Where the break alarm stands after one look; it climbs as the idle time grows.</summary>
    public enum BreakAlarmVerdict
    {
        /// <summary>A take is recording or stopping: silent, no clock running.</summary>
        Off,
        /// <summary>Nothing records, for less than <see cref="BreakAlarmPolicy.GraceSeconds"/>: the clock runs, silent.</summary>
        Counting,
        /// <summary>From <see cref="BreakAlarmPolicy.GraceSeconds"/>: a soft beep every few seconds.</summary>
        Soft,
        /// <summary>From <see cref="BreakAlarmPolicy.LoudAfterSeconds"/>: a loud beep, faster, the volume held at its maximum.</summary>
        Loud,
        /// <summary>From <see cref="BreakAlarmPolicy.SirenAfterSeconds"/>: the full siren, looping, the volume held at its maximum.</summary>
        Siren,
    }

    /// <summary>
    /// The floor's rule (owner, 2026-11-24): any running headset with no take for more than
    /// <see cref="GraceSeconds"/> since the last one ended (good or failed) sounds, loud enough to hear
    /// from the corner of the room, and climbs: a soft beep from 45 s, a loud faster beep from 60 s,
    /// the full siren from 90 s. Nothing but a take silences it: not signing out, not taking the
    /// headset off (the presence sensor is held closed on the floor's keep-awake pods anyway), not a
    /// rig refusing Start (a refusal is still time without recording; builds 90–96 stayed silent for
    /// all three, and Pod 1 on 96 sat 89 s idle without a sound). A Start the rig refuses never left
    /// idle, so it does not reset the clock. With no take since the app started, the clock runs from
    /// the start. Pure, no UnityEngine (tools/sx-rig-stub/BreakAlarmHarness.cs). Main thread only.
    /// </summary>
    public sealed class BreakAlarmPolicy
    {
        public const double GraceSeconds = 45.0;
        public const double LoudAfterSeconds = 60.0;
        public const double SirenAfterSeconds = 90.0;

        private double? idleSince;

        /// <summary>
        /// One look. <paramref name="now"/> is a monotonic clock in seconds; <paramref name="collecting"/>
        /// a take is recording or stopping.
        /// </summary>
        public BreakAlarmVerdict Observe(double now, bool collecting)
        {
            if (collecting)
            {
                idleSince = null;
                return BreakAlarmVerdict.Off;
            }
            idleSince ??= now;
            var idle = now - idleSince.Value;
            return idle >= SirenAfterSeconds ? BreakAlarmVerdict.Siren
                : idle >= LoudAfterSeconds ? BreakAlarmVerdict.Loud
                : idle >= GraceSeconds ? BreakAlarmVerdict.Soft
                : BreakAlarmVerdict.Counting;
        }

        /// <summary>Seconds since the last take ended (or the app started); null while a take records.</summary>
        public double? IdleSeconds(double now) => idleSince is double since ? now - since : (double?)null;
    }
}
