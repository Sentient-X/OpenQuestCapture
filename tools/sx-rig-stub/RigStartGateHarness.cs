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
        ApkBuildIdKnown = true, CollectorReady = true,
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
            ("collector_signed_out", With(f => f[0].CollectorReady = false)),
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

        // The seal's operator_id is the signed-in collector, as sx-pod's (Principal.display_name).
        var signedIn = JObject.Parse("{\"floor\":{\"operator\":{\"signed_in\":true,\"display_name\":\"Asha Rao\"}}}");
        var signedOut = JObject.Parse("{\"floor\":{\"operator\":{\"signed_in\":false,\"user_code\":\"WXYZ-1234\"}}}");
        var collector = RigStartGate.SealOperatorId(true, false, RigStatusJson.CollectorName(signedIn), "someone-else");
        Expect(collector == "Asha Rao", "enrolled, signed in: the collector names the seal, never the body: " + collector);
        var body = RigStartGate.EpisodeBody("20261021_120000", collector!, "sha256:ab", "/data/20261021_120000", "1.0", null, null);
        Expect((string?)body["operator_id"] == "Asha Rao" && (string?)body["episode_id"] == "20261021_120000",
            "the start body carries the collector's id: " + body.ToString(Newtonsoft.Json.Formatting.None));
        Expect(RigStartGate.SealOperatorId(true, false, RigStatusJson.CollectorName(signedOut), "bench-operator") == null,
            "enrolled, nobody signed in: no operator (refused), whatever the body names");
        Expect(RigStartGate.SealOperatorId(true, false, RigStatusJson.CollectorName(null), null) == null, "enrolled, no status: refused");
        var refusal = RigStartGate.SignInToRecord();
        Expect(refusal.Code == "collector_signed_out" && refusal.Message.StartsWith("Sign in to record"), "the refusal says Sign in to record: " + refusal.Message);
        Expect(RigStartGate.SealOperatorId(true, true, null, null) == RigStartGate.LocalOperatorId, "Local only, no body: local-only");
        Expect(RigStartGate.SealOperatorId(false, false, null, null) == RigStartGate.LocalOperatorId, "not enrolled: local-only");
        Expect(RigStartGate.SealOperatorId(true, true, "Asha Rao", " bench ") == "bench", "Local only: the body's operator_id when it names one");

        Console.WriteLine(failures == 0 ? "ALL PASS" : failures + " FAILED");
        return failures;
    }
}
