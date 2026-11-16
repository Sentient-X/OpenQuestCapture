// Host check of Assets/RealityLog/Scripts/Runtime/Camera/CaptureFailureWatch.cs: while both
// eyes answer "" nothing is reported; the first eye whose recorder reports a failure is the
// cause, reported once with its side; a reader that throws is not a failure. Run by
// test_binding.sh; not part of the Unity project.
using System;
using RealityLog.Camera;

static class CaptureFailureHarness
{
    static int failures;
    static void Expect(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) failures++; }

    static int Main()
    {
        string? left = "", right = "";
        var watch = new CaptureFailureWatch(new (string, Func<string?>)[]
        {
            ("left_camera.mp4", () => left),
            ("right_camera.mp4", () => right),
        });
        Expect(watch.Poll() == null, "both eyes healthy (\"\") report nothing");
        right = null;
        Expect(watch.Poll() == null, "an eye not recording (null) reports nothing");

        // pod5 2026-11-16 (build 74, 20261116_125224): the right eye's encoder, 5 s in.
        right = "Encoder output failed after 151 frames: IllegalStateException: Encoded sample has no source exposure timestamp\n";
        var failure = watch.Poll();
        Expect(failure != null && failure.Eye == "right_camera.mp4" && failure.Side == "right",
            "the right eye's failure is reported with its side");
        Expect(failure != null && failure.Error.StartsWith("Encoder output failed") && !failure.Error.EndsWith("\n"),
            "with the recorder's own words, trimmed");
        Expect(failure != null && failure.Describe().Contains("the right head camera (right_camera.mp4) failed while recording"),
            "and one sentence for the log and rig_refusal.json");
        Expect(CaptureFailure.NoticeCode == "head_camera_failed", "its notice code is the table's head_camera_failed");
        left = "camera device error 4";
        Expect(watch.Poll() == null, "only once per watch: a second eye failing after is not a second cause");

        var both = new CaptureFailureWatch(new (string, Func<string?>)[]
        {
            ("left_camera.mp4", () => throw new InvalidOperationException("no getCaptureError in this AAR")),
            ("right_camera.mp4", () => "   "),
        });
        Expect(both.Poll() == null, "a reader that throws, or answers whitespace, is not a failure");

        Expect(CaptureFailureWatch.SideOf("left_camera.mp4") == "left" && CaptureFailureWatch.SideOf("Right_Camera.mp4") == "right",
            "the side is read from the eye's file name");
        Expect(CaptureFailureWatch.PollIntervalMs == 250, "polled at about 4 Hz");

        Console.WriteLine(failures == 0 ? "capture failure: all passed" : $"capture failure: {failures} FAILED");
        return failures == 0 ? 0 : 1;
    }
}
