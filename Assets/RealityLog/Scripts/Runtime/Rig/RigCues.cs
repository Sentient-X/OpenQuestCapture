# nullable enable

using UnityEngine;

namespace RealityLog.Rig
{
    /// <summary>
    /// The rig's sound cues. The operator wears the headset on the forehead and never sees the
    /// display, so start, stop, a sealed episode, a refused one and a collector's sign-in are
    /// told by sound only. Synthesized (no audio assets); each cue is distinct by contour:
    /// start rises, stop falls, sealed is three quick high blips, refused a low buzz, sign-in a
    /// rising two-tone chime. Main thread only.
    /// </summary>
    public static class RigCues
    {
        public enum Cue
        {
            Start,
            Stop,
            Sealed,
            Refused,
            SignedIn,
        }

        private const int Rate = 44100;
        private static AudioSource? source;
        private static readonly AudioClip?[] clips = new AudioClip?[5];

        public static void Play(Cue cue, GameObject host)
        {
            if (source == null)
            {
                source = host.GetComponent<AudioSource>() ?? host.AddComponent<AudioSource>();
                source.spatialBlend = 0f;
                source.playOnAwake = false;
            }
            var clip = clips[(int)cue] ??= Build(cue);
            source.PlayOneShot(clip);
        }

        private static AudioClip Build(Cue cue)
        {
            // (frequency Hz, seconds) segments; 0 Hz is silence.
            (float hz, float s)[] notes = cue switch
            {
                Cue.Start => new[] { (523.25f, 0.12f), (783.99f, 0.18f) },
                Cue.Stop => new[] { (783.99f, 0.12f), (523.25f, 0.18f) },
                Cue.Sealed => new[] { (1318.5f, 0.06f), (0f, 0.05f), (1318.5f, 0.06f), (0f, 0.05f), (1318.5f, 0.06f) },
                Cue.Refused => new[] { (196f, 0.55f) },
                _ => new[] { (880f, 0.15f), (1320f, 0.15f) },
            };
            var total = 0;
            foreach (var (_, s) in notes) total += (int)(s * Rate);
            var samples = new float[total];
            var at = 0;
            foreach (var (hz, s) in notes)
            {
                var n = (int)(s * Rate);
                for (var i = 0; i < n; i++)
                {
                    var envelope = Mathf.Min(1f, Mathf.Min(i, n - i) / (Rate * 0.008f));
                    // The refused buzz is a square wave, so it reads as an error, not a tone.
                    var wave = Mathf.Sin(2f * Mathf.PI * hz * i / Rate);
                    if (cue == Cue.Refused) wave = wave >= 0f ? 0.6f : -0.6f;
                    samples[at + i] = hz <= 0f ? 0f : 0.35f * envelope * wave;
                }
                at += n;
            }
            var clip = AudioClip.Create("Rig" + cue, samples.Length, 1, Rate, false);
            clip.SetData(samples, 0);
            return clip;
        }
    }
}
