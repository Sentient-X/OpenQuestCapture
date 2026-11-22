// Host check of Assets/RealityLog/Scripts/Runtime/Camera/HeadVideoGapWatch.cs: two eyes polled at
// ~4 Hz through the recorder's existing counters; an eye that keeps its 30 Hz cadence (with the
// encoder's lag and the poll's phase) reports nothing, a single lost exposure reports nothing (the
// stop's capture report names it), and a stall the seal is certain to refuse is reported once, with
// the eye, the frames lost, the gap, when it began and whether the camera thread or the camera took
// it. Also the capture report's integer reader. Run by test_binding.sh; not part of the Unity project.
using System;
using System.Collections.Generic;
using RealityLog.Camera;

static class HeadVideoGapHarness
{
    static int failures;
    static void Expect(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) failures++; }

    // One eye's recorder, simulated: the camera lattice is 20 ms, the selector keeps the first exposure
    // of each absolute 1/30 s bin, the encoder emits a frame latencyMs after its exposure, and the stall
    // list says which lattice exposures never reach the camera thread.
    sealed class Eye
    {
        public readonly string Name;
        public double LatencyMs;
        public long IntervalMaxNs, CallbackMaxNs;
        readonly List<(double fromMs, double toMs)> stalls = new();
        public Eye(string name, double latencyMs) { Name = name; LatencyMs = latencyMs; }
        public void Stall(double fromMs, double toMs, bool threadBlocked)
        {
            stalls.Add((fromMs, toMs));
            IntervalMaxNs = Math.Max(IntervalMaxNs, (long)((toMs - fromMs) * 1e6));
            if (threadBlocked) CallbackMaxNs = Math.Max(CallbackMaxNs, (long)((toMs - fromMs - 5) * 1e6));
        }
        bool Lost(double exposureMs)
        {
            foreach (var (from, to) in stalls) if (exposureMs > from && exposureMs < to) return true;
            return false;
        }
        // Written frames at elapsed ms: exposures at 20 k ms (k >= 1, the camera is already streaming at
        // start), first fresh one per 1/30 s bin, emitted latencyMs later.
        public HeadVideoSample Sample(double elapsedMs, bool withMaxima = true)
        {
            long written = 0; long lastBin = -1;
            for (double t = 20; t + LatencyMs <= elapsedMs; t += 20)
            {
                if (Lost(t)) continue;
                var bin = (long)Math.Floor(t * 30 / 1000.0);
                if (bin <= lastBin) continue;
                lastBin = bin; written++;
            }
            // The recorder's maxima are running: visible once the stall has happened.
            long interval = 0, callback = 0;
            foreach (var (from, to) in stalls) if (elapsedMs >= to) { interval = Math.Max(interval, (long)((to - from) * 1e6)); }
            if (interval > 0 && CallbackMaxNs > 0) callback = CallbackMaxNs;
            return new HeadVideoSample((long)elapsedMs, written, withMaxima ? interval : 0, withMaxima ? callback : 0);
        }
    }

    static HeadVideoGap? Run(HeadVideoGapWatch watch, double untilMs, Func<double, double> clock)
    {
        for (double now = 0; now <= untilMs; now += 250)
        {
            clock(now);
            if (watch.Poll() is { } gap) return gap;
        }
        return null;
    }

    static int Main()
    {
        // A clean 10-minute take: both eyes keep cadence, the encoders lag 70 and 100 ms, the poll's
        // phase wanders; nothing is reported.
        {
            var left = new Eye("left_camera.mp4", 70); var right = new Eye("right_camera.mp4", 100);
            double now = 0;
            var watch = new HeadVideoGapWatch(new (string, Func<HeadVideoSample?>)[]
            {
                (left.Name, () => left.Sample(now + 3 * Math.Sin(now / 97.0))),
                (right.Name, () => right.Sample(now + 7 * Math.Cos(now / 131.0))),
            });
            var gap = Run(watch, 600_000, t => now = t);
            Expect(gap == null, "a clean 10 min take with 70/100 ms encoder lag and poll jitter reports nothing");
            // The encoder's lag varies by up to two frames: still nothing.
            var wobble = new Eye("left_camera.mp4", 70);
            var w2 = new HeadVideoGapWatch(new (string, Func<HeadVideoSample?>)[] { (wobble.Name, () => { wobble.LatencyMs = 70 + 60 * Math.Abs(Math.Sin(now / 53.0)); return wobble.Sample(now); }) });
            Expect(Run(w2, 300_000, t => now = t) == null, "an encoder whose lag wanders by two frames reports nothing");
        }

        // One lost exposure in a one-slot bin (the 60 ms seal gap, 60% of 2026-11-22's refusals): the
        // count falls one frame behind for good; below this watch's floor, so nothing is reported here
        // (the stop's capture report line names it).
        {
            var left = new Eye("left_camera.mp4", 70); var right = new Eye("right_camera.mp4", 70);
            left.Stall(79, 81, false); // the lone exposure at 80 ms of bin [66.7, 100) never arrives
            double now = 0;
            var watch = new HeadVideoGapWatch(new (string, Func<HeadVideoSample?>)[]
            {
                (left.Name, () => left.Sample(now)), (right.Name, () => right.Sample(now)),
            });
            Expect(Run(watch, 120_000, t => now = t) == null, "a single lost exposure (a 60 ms seal gap) is left to the stop's capture report");
        }

        // A 1.02 s stall of the right eye 133 s in, its camera thread blocked inside the callback
        // (eglSwapBuffers on the encoder): reported once, as the right eye, ~49 frames, ~1.02 s,
        // about 133 s in, blocked.
        {
            var left = new Eye("left_camera.mp4", 70); var right = new Eye("right_camera.mp4", 70);
            right.Stall(133_000, 134_020, threadBlocked: true);
            double now = 0;
            var watch = new HeadVideoGapWatch(new (string, Func<HeadVideoSample?>)[]
            {
                (left.Name, () => left.Sample(now)), (right.Name, () => right.Sample(now)),
            });
            var gap = Run(watch, 300_000, t => now = t);
            Expect(gap != null && gap.Eye == "right_camera.mp4" && gap.Side == "right", "a 1.02 s stall of the right eye is reported as the right eye");
            Expect(gap != null && gap.LostFrames >= 28 && gap.LostFrames <= 32, $"with the selected frames lost ({gap?.LostFrames}, 30 of 1.02 s at 30 Hz)");
            Expect(gap != null && Math.Abs(gap.GapMs - 1020) < 1, $"and the recorder's longest frame interval ({gap?.GapMs:F0} ms)");
            Expect(gap != null && gap.AtSeconds >= 131 && gap.AtSeconds <= 135, $"about when it began ({gap?.AtSeconds:F0} s)");
            Expect(gap != null && gap.CameraThreadBlocked, "and that the eye's camera thread took the gap");
            Expect(gap != null && gap.Describe().Contains("the right head camera (right_camera.mp4) skipped") && gap.Describe().Contains("camera thread was blocked"),
                "in one sentence for the log and rig_refusal.json");
            Expect(gap != null && now - 134_020 <= 1000, $"within a second of the stall's end (reported at {now / 1000:F2} s)");
            left.Stall(200_000, 201_000, false);
            Expect(Run(watch, 260_000, t => now = t) == null, "only once per watch: a later stall of the other eye is not a second cause");
            Expect(HeadVideoGap.NoticeCode == "seal_video_gap", "its notice is the seal's own seal_video_gap");
        }

        // The same stall with the camera delivering nothing (a short callback): "the camera delivered no frames".
        {
            var left = new Eye("left_camera.mp4", 70);
            left.Stall(60_000, 61_000, threadBlocked: false);
            double now = 0;
            var watch = new HeadVideoGapWatch(new (string, Func<HeadVideoSample?>)[] { (left.Name, () => left.Sample(now)) });
            var gap = Run(watch, 120_000, t => now = t);
            Expect(gap != null && !gap.CameraThreadBlocked && gap.Describe().Contains("the camera delivered no frames"),
                "a stall with a short callback is the camera's, not the thread's");
        }

        // The count alone is not enough: an encoder that falls 15 frames behind and catches up (no
        // frame interval to match) is not a stall.
        {
            var left = new Eye("left_camera.mp4", 70);
            double now = 0;
            var watch = new HeadVideoGapWatch(new (string, Func<HeadVideoSample?>)[]
            {
                (left.Name, () => { left.LatencyMs = now >= 50_000 && now < 52_000 ? 570 : 70; return left.Sample(now, withMaxima: false); }),
            });
            Expect(Run(watch, 120_000, t => now = t) == null, "an encoder 15 frames behind for 2 s with no camera-frame interval is not a stall");
        }

        // A reader that throws or answers null (an eye not recording, an older AAR) is skipped.
        {
            var watch = new HeadVideoGapWatch(new (string, Func<HeadVideoSample?>)[]
            {
                ("left_camera.mp4", () => throw new InvalidOperationException("no getCaptureReportJson in this AAR")),
                ("right_camera.mp4", () => null),
            });
            Expect(watch.Poll() == null, "a reader that throws, or answers null, is not a stall");
        }

        // The capture report's integer reader, on the recorder's own compact JSON.
        {
            const string report = "{\"requested_fps_range\":[60,60],\"observed_source_fps\":49.9871,\"selection_mode\":\"absolute_grid\",\"selection_grid_ns\":33333333,\"exposure_missing_frame_count\":0,\"capture_result_missing_frame_count\":2,\"source_sequence_error_count\":0,\"timestamp_regression_count\":0,\"gop_violation_count\":0,\"selected_mean_fps\":29.9998,\"selected_max_gap_ns\":1020031000,\"stage_max_ns\":{\"camera_frame_callback\":1015221000,\"camera_frame_interval\":1020412000,\"update_tex_image\":1811000,\"encoder_render\":4102000,\"encoder_output\":12044000,\"fragment_write\":3122000,\"fragment_fsync\":18450000,\"sidecar_write\":210000,\"sidecar_fsync\":9100000},\"io_max_queued_jobs\":1,\"io_queue_capacity\":16}";
            Expect(CaptureReportJson.Integer(report, "selected_max_gap_ns") == 1_020_031_000L, "selected_max_gap_ns is read");
            Expect(CaptureReportJson.StageMaxNs(report, "camera_frame_interval") == 1_020_412_000L, "camera_frame_interval is read from stage_max_ns");
            Expect(CaptureReportJson.StageMaxNs(report, "camera_frame_callback") == 1_015_221_000L, "camera_frame_callback is read");
            Expect(CaptureReportJson.Integer(report, "selected_mean_fps") == null, "a float is not an integer");
            Expect(CaptureReportJson.Integer(report, "missing") == null && CaptureReportJson.Integer("{}", "x") == null && CaptureReportJson.Integer(null, "x") == null,
                "an absent key, an empty report and no report read as null");
            Expect(CaptureReportJson.StageMaxNs("{ \"camera_frame_interval\" : 42 }", "camera_frame_interval") == 42L, "spaced JSON reads too");
        }

        Console.WriteLine(failures == 0 ? "head video gap: all passed" : $"head video gap: {failures} FAILED");
        return failures == 0 ? 0 : 1;
    }
}
