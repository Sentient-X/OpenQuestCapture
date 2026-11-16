# nullable enable

using System;

namespace RealityLog.Camera
{
    /// <summary>One eye's recorder failing mid-episode: which eye, its side, and the recorder's words.</summary>
    public sealed class CaptureFailure
    {
        public string Eye { get; }
        public string Side { get; }
        public string Error { get; }

        public CaptureFailure(string eye, string error)
        {
            Eye = eye;
            Side = CaptureFailureWatch.SideOf(eye);
            Error = error;
        }

        /// <summary>The notice table's code for it (sx packages/sx-pod-runtime notice.rs).</summary>
        public const string NoticeCode = "head_camera_failed";

        /// <summary>One sentence for logs and the session's refusal record.</summary>
        public string Describe() => $"the {Side} head camera ({Eye}) failed while recording: {Error}";
    }

    /// <summary>
    /// Watches both head cameras while an episode records (sx#4210). QuestCameraLib a6e6bd3
    /// reports a recorder thread's first failure at once through <c>getCaptureError()</c> ("" while
    /// healthy); before, the eye simply ended and the failure surfaced only at stop, so an episode
    /// went on recording one eye for minutes (pod5 2026-11-16, session 20261116_125224: the right
    /// eye's encoder failed 5 s into a 40 s episode). The recording manager polls this from a
    /// JVM-attached worker every <see cref="PollIntervalMs"/>, never the main thread, and stops
    /// the episode as failed with the first failure. Reports at most one failure per watch: the
    /// first eye that fails is the cause. Pure: no UnityEngine, tested on the host
    /// (tools/sx-rig-stub/CaptureFailureHarness.cs).
    /// </summary>
    public sealed class CaptureFailureWatch
    {
        /// <summary>About 4 Hz.</summary>
        public const int PollIntervalMs = 250;

        private readonly (string eye, Func<string?> read)[] eyes;
        private bool reported;

        /// <param name="eyes">Each eye's file name and its reader: the recorder's error, or null/"" while healthy.</param>
        public CaptureFailureWatch((string eye, Func<string?> read)[] eyes)
        {
            this.eyes = eyes;
        }

        /// <summary>The first failure, once; null while every eye is healthy and after it was reported.</summary>
        public CaptureFailure? Poll()
        {
            if (reported) return null;
            foreach (var (eye, read) in eyes)
            {
                string? error;
                try
                {
                    error = read();
                }
                catch (Exception)
                {
                    // A reader that cannot ask (an older AAR, a closed instance) is not a failure.
                    continue;
                }
                if (string.IsNullOrWhiteSpace(error)) continue;
                reported = true;
                return new CaptureFailure(eye, error!.Trim());
            }
            return null;
        }

        /// <summary>"left" or "right" from an eye's file name (left_camera.mp4); "left" or "right" only.</summary>
        public static string SideOf(string eye) =>
            eye.IndexOf("right", StringComparison.OrdinalIgnoreCase) >= 0 ? "right" : "left";
    }
}
