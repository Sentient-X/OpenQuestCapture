// Host check of RigStartGate (Assets/RealityLog/Scripts/Runtime/Rig/RigStartGate.cs): the
// device check runs whenever the preconditions hold, and only a ready report lets a start arm.
// Run by test_binding.sh; not part of the Unity project.
using System;
using Newtonsoft.Json.Linq;
using RealityLog.Rig;

static class RigStartGateHarness
{
    static int failures;
    static void Expect(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) failures++; }

    static RigStartFacts Ready() => new RigStartFacts
    {
        EpisodeBusy = false, LibraryMissing = false, Opened = true, AwaitingPermission = new string[0],
        ApkBuildIdKnown = true,
    };

    static RigStartFacts With(Action<RigStartFacts[]> change)
    {
        var facts = new[] { Ready() };
        change(facts);
        return facts[0];
    }

    static int Main()
    {
        int runs = 0; string? recorded = null;
        Func<string> passing = () => { runs++; return "{\"ready\":true,\"problems\":[]}"; };
        Func<string> failing = () => { runs++; return "{\"ready\":false,\"problems\":[\"right_camera: no device on usb-port:1-1.2\"]}"; };

        var r = RigStartGate.Evaluate(Ready(), passing, j => recorded = j);
        Expect(r == null && runs == 1 && recorded!.Contains("\"ready\":true"), "ready facts run the check once and pass");

        runs = 0; recorded = null;
        r = RigStartGate.Evaluate(Ready(), failing, j => recorded = j);
        Expect(r?.Code == "rig_check_failed" && runs == 1 && r.Message.Contains("1-1.2") && r.DetailJson.Contains("\"problems\""),
            "a failing report refuses rig_check_failed with its problems");
        Expect(recorded != null && recorded.Contains("\"ready\":false"), "the failing report is recorded (rig.lastCheck)");

        runs = 0;
        r = RigStartGate.Evaluate(Ready(), () => { runs++; return "{\"problems\":[]}"; }, _ => { });
        Expect(r?.Code == "rig_check_failed" && runs == 1, "a report without ready:true refuses");

        runs = 0; recorded = null;
        r = RigStartGate.Evaluate(Ready(),
            () => { runs++; throw new SxRigException("sx_rig_check", SxRigError.Device, "{\"code\":\"device\",\"detail\":\"x\"}"); },
            j => recorded = j);
        Expect(r?.Code == "rig_check_failed" && runs == 1 && recorded!.Contains("\"error\""), "a throwing check refuses and is recorded");

        // Preconditions refuse before the check, each with its code; none of them passes.
        var cases = new (string code, RigStartFacts facts)[]
        {
            ("rig_start_failed", With(f => f[0].EpisodeBusy = true)),
            ("rig_library_missing", With(f => f[0].LibraryMissing = true)),
            ("rig_open_failed", With(f => f[0].Opened = false)),
            ("rig_usb_permission", With(f => f[0].AwaitingPermission = new[] { "/dev/bus/usb/001/008" })),
            ("rig_identity_pending", With(f => f[0].ApkBuildIdKnown = false)),
        };
        foreach (var (code, facts) in cases)
        {
            runs = 0;
            r = RigStartGate.Evaluate(facts, passing, _ => { });
            Expect(r?.Code == code && runs == 0, $"{code} refuses before the check");
        }

        // sx_rig_start_armed's own check refusal maps to rig_check_failed.
        var armed = RigStartGate.FromError(new SxRigException("sx_rig_start_armed", SxRigError.Device,
            "{\"code\":\"device\",\"detail\":\"check failed\",\"refusal\":{\"kind\":\"check_failed\",\"problems\":[\"left_encoder: no usb-serial:58:E6\"]}}"),
            "rig_start_failed");
        Expect(armed.Code == "rig_check_failed" && armed.Message.Contains("left_encoder"), "start_armed check_failed -> rig_check_failed");
        var storage = RigStartGate.FromError(new SxRigException("sx_rig_start_armed", SxRigError.Storage,
            "{\"code\":\"storage\",\"refusal\":{\"kind\":\"storage_low\"}}"), "rig_start_failed");
        Expect(storage.Code == "rig_storage_low", "storage -> rig_storage_low");
        var other = RigStartGate.FromError(new SxRigException("sx_rig_start_armed", SxRigError.Internal, "{\"code\":\"internal\"}"), "rig_start_failed");
        Expect(other.Code == "rig_start_failed", "other errors keep the caller's code");
        var slow = RigStartGate.FromError(new SxRigException("sx_rig_start_armed", SxRigError.Device,
            "{\"code\":\"device\",\"detail\":\"the Factory did not answer\",\"refusal\":{\"kind\":\"factory_unanswered\"}}"), "rig_start_failed");
        Expect(slow.Code == "factory_unanswered" && slow.Message.Contains("start again"), "a Factory that did not answer the start in time -> factory_unanswered");

        // The rig's tracking law (sx#4210): judged before the check and before OQC's session.
        runs = 0;
        var notInHand = "{\"kind\":\"controllers_not_in_hand\",\"detail\":\"left controller is 1.9 m from the headset\"," +
            "\"notice\":{\"code\":\"controllers_not_in_hand\",\"args\":{}}}";
        r = RigStartGate.Evaluate(With(f => f[0].TrackingRefusalJson = notInHand), passing, _ => { });
        Expect(r?.Code == "controllers_not_in_hand" && runs == 0 && r.Message.Contains("1.9 m") && r.DetailJson.Contains("\"notice\""),
            "a tracking refusal refuses before the device check, coded by its kind, with the rig's notice");
        foreach (var kind in RigStartGate.TrackingKinds)
        {
            Expect(RigStartGate.Tracking("{\"kind\":\"" + kind + "\"}")?.Code == kind, "tracking kind " + kind + " is its own refusal code");
        }
        Expect(RigStartGate.Tracking("{\"kind\":\"storage_low\"}") == null && RigStartGate.Tracking(null) == null
            && RigStartGate.Tracking("not json") == null, "anything but a tracking kind is not a tracking refusal");
        runs = 0;
        r = RigStartGate.Evaluate(With(f => f[0].TrackingRefusalJson = null), passing, _ => { });
        Expect(r == null && runs == 1, "no tracking refusal (PICO, an older library): the check decides");
        var armedTracking = RigStartGate.FromError(new SxRigException("sx_rig_start_armed", SxRigError.State,
            "{\"code\":\"state\",\"detail\":\"the right controller is on the left\",\"refusal\":{\"kind\":\"controllers_wrong_side\"}," +
            "\"notice\":{\"code\":\"controllers_wrong_side\",\"args\":{}}}"), "rig_start_failed");
        Expect(armedTracking.Code == "controllers_wrong_side" && armedTracking.DetailJson.Contains("\"notice\""),
            "a start the library refused on tracking -> its kind, its notice kept");

        // No collector is required: with no phone paired the pod records unattributed.
        runs = 0;
        r = RigStartGate.Evaluate(Ready(), passing, _ => { });
        Expect(r == null && runs == 1, "an unattributed start (no phone paired) arms on a passing check");
        var body = RigStartGate.EpisodeBody("20261021_120000", "sha256:ab", "/data/20261021_120000", "1.0", null, null);
        Expect(body["operator_id"] == null && (string?)body["episode_id"] == "20261021_120000"
            && (string?)body["quest_apk_build_id"] == "sha256:ab",
            "the start body names no operator; the rig seals the shift's collector: " + body.ToString(Newtonsoft.Json.Formatting.None));
        var assigned = RigStartGate.EpisodeBody("20261021_120000", "sha256:ab", "/data/20261021_120000", "1.0", "ta_1", "practice");
        Expect((string?)assigned["task_assignment_id"] == "ta_1" && (string?)assigned["mode"] == "practice" && assigned["operator_id"] == null,
            "a task assignment and mode ride the body, still with no operator");

        Console.WriteLine(failures == 0 ? "ALL PASS" : failures + " FAILED");
        return failures;
    }
}
