// Host checks for the real review model used by the Pod card. No Factory/device calls.
#nullable enable
using System;
using Newtonsoft.Json.Linq;
using RealityLog.Rig;

static class PodReviewHarness
{
    static int failures;
    static readonly DateTime Now = new(2026, 11, 12, 10, 0, 0, DateTimeKind.Utc);
    static void Expect(bool condition, string message)
    {
        Console.WriteLine((condition ? "PASS " : "FAIL ") + message);
        if (!condition) failures++;
    }
    static JObject Item(string session = "cs_old", string episode = "ep_old", string integrity = "passed") => new()
    {
        ["session_id"] = session, ["episode_id"] = episode, ["source_episode_id"] = "20261107_150000",
        ["episode_index"] = 3, ["scope"] = "episode", ["integrity"] = integrity,
        ["episode_started_at"] = "2026-11-07T07:00:00Z", ["prompts"] = new JArray(),
    };
    static JObject Status(params JObject[] items) => new()
    {
        ["expected"] = true, ["open"] = true, ["enrolled"] = true,
        ["floor"] = new JObject
        {
            // The paired phone's person, whom the rig answers reviews as (floor.collector).
            ["collector"] = new JObject { ["id"] = "collector-1", ["name"] = "Collector" },
            ["review_queue"] = new JArray(items),
        },
        ["factory"] = new JObject { ["collection"] = new JObject { ["id"] = "cs_current" } },
    };
    static PodScreen Screen(JObject? status) => PodScreenBoard.Build(status, null, false, new StreamLiveness(), Now);
    static bool Refuses(Action action)
    {
        try { action(); return false; }
        catch (InvalidOperationException) { return true; }
        catch (ArgumentException) { return true; }
    }
    static int Main()
    {
        var old = Item();
        var active = Item("cs_current", "ep_failed", "failed");
        var status = Status(old, active);
        var before = status.ToString();
        var screen = Screen(status);
        Expect(screen.Reviews.Count == 2 && screen.ReviewUnavailable == "", "active and draining collections are both available");
        Expect(status.ToString() == before, "shared parsed status remains unmodified");
        var item = screen.Reviews[0];
        Expect(item.EpisodeId == "ep_old" && item.SourceEpisodeId == "20261107_150000" && item.EpisodeIndex == 3
            && item.StartedAt == "2026-11-07T07:00:00Z", "review identifies exact episode and recorded time");
        var id = Guid.Parse("a1121907-11bc-4c9e-b4dc-4ac2bcd25fe1");
        var command = JObject.Parse(item.Command(true, id, "collector-1"));
        Expect((string?)command["session_id"] == "cs_old" && (string?)command["episode_id"] == "ep_old"
            && (string?)command["command_id"] == id.ToString() && (string?)command["command"] == "review_success"
            && command["answers"] is JArray { Count: 0 } && command["expected_operator"] == null
            && command.Count == 5, "explicit success preserves old collection and journal ID, names no operator (the rig answers as floor.collector); no invented answers");
        var failedCommand = JObject.Parse(screen.Reviews[1].Command(false, Guid.NewGuid(), "collector-1"));
        Expect((string?)failedCommand["command"] == "review_failure" && screen.Reviews[1].Integrity == "failed"
            && command["integrity"] == null, "human outcome does not overwrite capture integrity");
        Expect(Refuses(() => item.Command(true, Guid.Empty, "collector-1")), "empty idempotency key refused");
        Expect(Refuses(() => item.Command(true, id, "")), "no paired phone's collector: refused");
        Expect(screen.ReviewCollectorSubject == "collector-1", "the review is chosen as floor.collector.id");

        var required = Item();
        required["prompts"] = new JArray(new JObject { ["id"] = "safe", ["label"] = "Object placed safely?", ["required"] = true, ["kind"] = "boolean" });
        var held = Screen(Status(required)).Reviews[0];
        Expect(!held.CanSubmit && held.BlockedReason.Contains("Object placed safely?") && Refuses(() => held.Command(true, id, "collector-1")), "required unanswered prompts fail closed with an actionable message");
        ((JObject)required["prompts"]![0]!)["required"] = false;
        Expect(Screen(Status(required)).Reviews[0].CanSubmit, "optional untouched answer remains absent");
        required.Remove("prompts");
        Expect(!Screen(Status(required)).Reviews[0].CanSubmit, "missing prompt policy cannot be treated as empty");
        required["prompts"] = new JArray(new JObject { ["required"] = "false" });
        Expect(!Screen(Status(required)).Reviews[0].CanSubmit, "malformed required flag fails closed");
        required["prompts"] = new JArray(); required["review_error"] = "unknown prompt kind";
        Expect(!Screen(Status(required)).Reviews[0].CanSubmit, "native review policy error disables submission");
        var batch = Item(); batch["scope"] = "batch"; batch["episode_id"] = null;
        Expect(!Screen(Status(batch)).Reviews[0].CanSubmit && Screen(Status(batch)).Reviews[0].BlockedReason.Contains("SX platform"), "batch questions are explicit work, never automatic answers");
        var malformed = Item(); malformed["episode_id"] = null;
        Expect(!Screen(Status(malformed)).Reviews[0].CanSubmit, "missing episode identity refuses review");

        var unattributed = Status(Item()); unattributed["floor"]!["collector"] = null;
        Expect(Screen(unattributed).ReviewUnavailable.Contains("Pair your phone") && Screen(unattributed).ReviewCollectorSubject == "",
            "an unattributed pod has nobody to answer its reviews");
        var retired = Status(Item()); ((JObject)retired["floor"]!).Remove("collector");
        retired["floor"]!["operator"] = new JObject { ["signed_in"] = true, ["subject"] = "collector-1" };
        Expect(Screen(retired).ReviewCollectorSubject == "", "the retired floor.operator.subject is never read");
        // No Local only mode (2026-11-22): a status an earlier build wrote with localOnly reviews as any enrolled pod.
        var legacy = Status(Item()); legacy["localOnly"] = true;
        Expect(Screen(legacy).ReviewUnavailable == Screen(Status(Item())).ReviewUnavailable && Screen(legacy).Reviews.Count == Screen(Status(Item())).Reviews.Count,
            "a stale localOnly in the status never hides the Factory review queue");
        var recording = Status(Item()); recording["episodeActive"] = true;
        Expect(Screen(recording).ReviewUnavailable.Contains("Stop recording"), "reviews wait until recording stops");
        var unavailable = Status(); ((JObject)unavailable["floor"]!).Remove("review_queue");
        Expect(!Screen(unavailable).ReviewQueueAvailable && Screen(unavailable).ReviewUnavailable.Length > 0, "missing queue is unavailable, not an empty success state");
        Expect(Screen(Status()).ReviewQueueAvailable && Screen(Status()).Reviews.Count == 0, "known empty review queue is represented separately");
        Expect(Screen(null).ReviewUnavailable.Length > 0, "loading status is represented");

        var submission = new PodReviewSubmission(item, false, id, "collector-1");
        var bytes = submission.CommandJson;
        submission.ApplyReply("broken json");
        submission.ApplyReply("{\"answer\":{\"command_id\":{},\"state\":[]}}");
        submission.Observe(Guid.NewGuid().ToString(), "completed", null);
        Expect(submission.State == "uncertain" && submission.CommandJson == bytes && !submission.Success && submission.CollectorSubject == "collector-1", "ambiguous/mismatched response keeps exact collector, choice and payload for retry");
        submission.ApplyReply(new JObject { ["answer"] = new JObject { ["command_id"] = id.ToString(), ["state"] = "pending" } }.ToString());
        Expect(submission.State == "pending" && submission.Message.Contains("waiting for Factory"), "durably pending review is not claimed accepted or delivered");
        var pending = Item(); pending["submission"] = new JObject { ["command_id"] = id.ToString(), ["state"] = "pending" };
        Expect(!Screen(Status(pending)).Reviews[0].CanSubmit, "pending journal submission cannot be duplicated");
        submission.Observe(id.ToString(), "completed", null);
        submission.Observe(id.ToString(), "pending", null);
        Expect(submission.State == "completed" && submission.Message.Contains("still follow"), "stale status cannot undo acceptance, and acceptance does not claim delivery");
        var refusal = new PodReviewSubmission(item, true, Guid.NewGuid(), "collector-1");
        refusal.Observe(refusal.CommandId, "failed", "collector changed");
        Expect(refusal.State == "failed" && refusal.Message.Contains("collector changed"), "definite refusal remains visible and needs another deliberate choice");
        var precondition = new PodReviewSubmission(item, true, Guid.NewGuid(), "collector-1");
        precondition.RefusedBeforeSend("A recording is active");
        Expect(precondition.State == "failed" && precondition.Message.Contains("A recording is active"), "pre-send refusal displays its reason without claiming a queued review");
        var lost = new PodReviewSubmission(item, true, Guid.NewGuid(), "collector-1");
        var retryBytes = lost.CommandJson;
        lost.Unconfirmed("Connection lost");
        Expect(lost.State == "uncertain" && lost.CommandJson == retryBytes && lost.Message.Contains("Connection lost"), "uncertain failure displays its reason and preserves replay identity");
        lost.RefusedBeforeSend("A recording started before this retry", priorAttemptUnconfirmed: true);
        Expect(lost.State == "uncertain" && lost.CommandJson == retryBytes, "a blocked retry cannot erase an earlier uncertain submission or unlock the opposite outcome");
        Console.WriteLine(failures == 0 ? "ALL PASS" : failures + " FAILED");
        return failures == 0 ? 0 : 1;
    }
}
