#nullable enable

namespace RealityLog.Rig
{
    /// <summary>Where the break alarm stands after one look; it climbs as the idle time grows.</summary>
    public enum BreakAlarmVerdict
    {
        /// <summary>A take is recording or stopping, or it is outside the shifts: silent, no clock running.</summary>
        Off,
        /// <summary>Nothing records, for less than <see cref="BreakAlarmPolicy.GraceSeconds"/>: the clock runs, silent.</summary>
        Counting,
        /// <summary>From <see cref="BreakAlarmPolicy.GraceSeconds"/>: "Pod N, start now", quietly, every few seconds.</summary>
        Soft,
        /// <summary>From <see cref="BreakAlarmPolicy.LoudAfterSeconds"/>: the phrase at full level, faster, the volume held.</summary>
        Loud,
        /// <summary>From <see cref="BreakAlarmPolicy.SirenAfterSeconds"/>: the phrase back to back, the volume held.</summary>
        Siren,
    }

    /// <summary>
    /// The floor's rule (owner, 2026-11-24): any running headset with no take for more than
    /// <see cref="GraceSeconds"/> since the last one ended (good or failed) sounds, loud enough to hear
    /// from the corner of the room, and climbs at 45 s, 60 s and 90 s (what it sounds like is
    /// <see cref="BreakAlarm"/>'s: since 2026-11-25 the spoken "Pod N, start now", not beeps and a
    /// siren). Nothing but a take silences it: not signing out, not taking the
    /// headset off (the presence sensor is held closed on the floor's keep-awake pods anyway), not a
    /// rig refusing Start (a refusal is still time without recording; builds 90–96 stayed silent for
    /// all three, and Pod 1 on 96 sat 89 s idle without a sound). A Start the rig refuses never left
    /// idle, so it does not reset the clock. With no take since the app started, the clock runs from
    /// the start. No shift runs from <see cref="QuietFromHour"/>:00 to <see cref="QuietUntilHour"/>:00
    /// headset local time (owner, 2026-11-25: shifts 07–13, 13–19, 19–01), so it stays silent then and
    /// its clock starts again at 07:00. Pure, no UnityEngine (tools/sx-rig-stub/BreakAlarmHarness.cs). Main thread only.
    /// </summary>
    public sealed class BreakAlarmPolicy
    {
        public const double GraceSeconds = 45.0;
        public const double LoudAfterSeconds = 60.0;
        public const double SirenAfterSeconds = 90.0;
        /// <summary>The first local hour with no shift (the 19–01 shift's end).</summary>
        public const int QuietFromHour = 1;
        /// <summary>The first local hour of the morning shift.</summary>
        public const int QuietUntilHour = 7;

        /// <summary>Whether <paramref name="localHour"/> (0–23, the headset's clock) falls outside every shift.</summary>
        public static bool Quiet(int localHour) => localHour >= QuietFromHour && localHour < QuietUntilHour;

        private double? idleSince;

        /// <summary>
        /// One look. <paramref name="now"/> is a monotonic clock in seconds; <paramref name="collecting"/>
        /// a take is recording or stopping; <paramref name="quiet"/> it is outside the shifts (<see cref="Quiet"/>).
        /// </summary>
        public BreakAlarmVerdict Observe(double now, bool collecting, bool quiet = false)
        {
            if (collecting || quiet)
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

    /// <summary>
    /// The hardware fault alarm (owner, 2026-11-26): while a rig device stands failed or missing (a
    /// camera, a gripper sensor recording on its wrist tags, the headset's own link to it), a low
    /// tone, clearly unlike the break alarm's high pips and siren, beeps at a middling rate and slows
    /// down to once every <see cref="SlowestEverySeconds"/>: someone must deal with it, but the shift
    /// goes on, so it nags rather than shouts. The break alarm takes precedence while it sounds;
    /// this one fills the rest, a take recording included. Pure, no UnityEngine.
    /// </summary>
    public sealed class HardwareAlarmPolicy
    {
        public const double FirstEverySeconds = 1.0;
        public const double SlowestEverySeconds = 5.0;
        /// <summary>Each beep's wait grows by this factor until <see cref="SlowestEverySeconds"/>.</summary>
        public const double SlowingFactor = 1.3;

        private double? nextAt;
        private double every = FirstEverySeconds;

        /// <summary>
        /// One look at <paramref name="now"/> (monotonic seconds): whether to beep now. A fault that
        /// clears and comes back starts again at the middling rate.
        /// </summary>
        public bool Beep(double now, bool faulted)
        {
            if (!faulted)
            {
                nextAt = null;
                every = FirstEverySeconds;
                return false;
            }
            if (nextAt is double at && now < at) return false;
            nextAt = now + every;
            every = System.Math.Min(SlowestEverySeconds, every * SlowingFactor);
            return true;
        }

        /// <summary>The wait before the next beep after the one just played.</summary>
        public double CurrentEverySeconds => every;
    }

    /// <summary>
    /// How loud any alarm holds the headset (owner, 2026-11-26): 70 % of the media stream's maximum
    /// from its first sound, 50 % once it has rung for <see cref="QuieterAfterSeconds"/> without a
    /// break. Pure, no UnityEngine.
    /// </summary>
    public sealed class AlarmVolumePolicy
    {
        public const double StartFraction = 0.7;
        public const double LaterFraction = 0.5;
        public const double QuieterAfterSeconds = 300.0;

        private double? ringingSince;

        /// <summary>The share to hold now, or null when no alarm sounds (the collector's volume is put back).</summary>
        public double? Fraction(double now, bool sounding)
        {
            if (!sounding)
            {
                ringingSince = null;
                return null;
            }
            ringingSince ??= now;
            return now - ringingSince.Value >= QuieterAfterSeconds ? LaterFraction : StartFraction;
        }
    }
}
