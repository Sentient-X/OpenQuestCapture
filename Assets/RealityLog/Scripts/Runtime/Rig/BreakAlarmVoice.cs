#nullable enable

using System;

namespace RealityLog.Rig
{
    /// <summary>
    /// What the break alarm says: "Pod N, start now." for the pod number in the headset's pod name,
    /// or "Start now." when the name carries none (owner, 2026-11-25: a beep says nothing; the room
    /// should hear which pod is idle and what to do). The phrases ship recorded in
    /// <c>Resources/sx_break_alarm_voice.bytes</c> (tools/break-alarm-voice/build_voice.py: the Quest
    /// has no speech engine of its own to ask). Pure, no UnityEngine
    /// (tools/sx-rig-stub/BreakAlarmHarness.cs).
    /// </summary>
    public static class BreakAlarmVoice
    {
        /// <summary>The Resources name of the recorded phrases.</summary>
        public const string ResourceName = "sx_break_alarm_voice";

        /// <summary>One phrase: mono samples in -1..1 at <see cref="Rate"/> Hz.</summary>
        public readonly struct Phrase
        {
            public Phrase(float[] samples, int rate, int? pod)
            {
                Samples = samples;
                Rate = rate;
                Pod = pod;
            }

            public float[] Samples { get; }
            public int Rate { get; }
            /// <summary>The pod number the phrase names; null for the one that only says "Start now."</summary>
            public int? Pod { get; }
            public string Words => Pod is int pod ? $"Pod {pod}, start now" : "Start now";
        }

        /// <summary>
        /// The pod number in a pod name: the digits right after the word "pod" ("Pod 11", "pod4-quest",
        /// "pod5-quest-3" is 5, "Pod 6 (Pi)"). Null when the name has no "pod" followed by a number
        /// from 1 to 99.
        /// </summary>
        public static int? PodNumber(string? podName)
        {
            if (string.IsNullOrEmpty(podName)) return null;
            var name = podName!;
            for (var at = name.IndexOf("pod", StringComparison.OrdinalIgnoreCase); at >= 0;
                 at = name.IndexOf("pod", at + 3, StringComparison.OrdinalIgnoreCase))
            {
                var i = at + 3;
                while (i < name.Length && (name[i] == ' ' || name[i] == '-' || name[i] == '_' || name[i] == '#')) i++;
                var start = i;
                var number = 0;
                while (i < name.Length && name[i] >= '0' && name[i] <= '9' && i - start < 4) number = number * 10 + (name[i++] - '0');
                if (i > start && number >= 1 && number <= 99) return number;
            }
            return null;
        }

        /// <summary>
        /// The phrase for <paramref name="podName"/> out of the recorded file: the pod's own, else the
        /// one with no number. Null when the file is not the recorded phrases or holds neither.
        /// Layout, little-endian: "SXBV", u32 version (1), u32 sample rate, u32 count, then per phrase
        /// i32 pod number (0 = no number), u32 first sample, u32 sample count; then 16-bit samples.
        /// </summary>
        public static Phrase? Read(byte[]? file, string? podName)
        {
            const int Header = 16;
            if (file == null || file.Length < Header) return null;
            if (file[0] != 'S' || file[1] != 'X' || file[2] != 'B' || file[3] != 'V') return null;
            if (U32(file, 4) != 1) return null;
            var rate = U32(file, 8);
            var count = U32(file, 12);
            if (rate < 8000 || rate > 48000 || count == 0 || count > 1000) return null;
            var body = Header + (long)count * 12;
            if (body > file.Length) return null;

            var wanted = PodNumber(podName) ?? 0;
            var found = Row(file, (int)count, wanted);
            if (found < 0 && wanted != 0)
            {
                wanted = 0;
                found = Row(file, (int)count, 0);
            }
            if (found < 0) return null;
            long first = U32(file, found + 4);
            long length = U32(file, found + 8);
            if (length == 0 || body + (first + length) * 2 > file.Length) return null;

            var samples = new float[length];
            var at = (int)(body + first * 2);
            for (var i = 0; i < samples.Length; i++, at += 2)
                samples[i] = (short)(file[at] | (file[at + 1] << 8)) / 32768f;
            return new Phrase(samples, (int)rate, wanted == 0 ? (int?)null : wanted);
        }

        /// <summary>Where the row for <paramref name="pod"/> starts in the file; -1 when there is none.</summary>
        private static int Row(byte[] file, int count, int pod)
        {
            for (var k = 0; k < count; k++)
            {
                var row = 16 + k * 12;
                if ((int)U32(file, row) == pod) return row;
            }
            return -1;
        }

        private static uint U32(byte[] b, int at) =>
            (uint)(b[at] | (b[at + 1] << 8) | (b[at + 2] << 16) | (b[at + 3] << 24));
    }
}
