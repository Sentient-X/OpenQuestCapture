// Host check of Assets/RealityLog/Scripts/Runtime/Rig/EpisodeLength.cs: libsx_rig's
// episode_length block becomes one warning a minute before the take's ceiling (the Factory's,
// 30 min by default; the words name the take's own) and one
// stop at it, each once per episode and only for the episode the app is recording; and the
// warning cue is its own sound (RigCues.Line). Run by test_binding.sh; not part of the Unity project.
using System;
using Newtonsoft.Json.Linq;
using RealityLog.Rig;

static class EpisodeLengthHarness
{
    static int failures;
    static void Expect(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) failures++; }

    static JObject Block(string episode, string phase, bool recording = true, int minutes = 30) =>
        JObject.Parse("{\"episode_id\":\"" + episode + "\",\"recording\":" + (recording ? "true" : "false")
            + ",\"elapsed_s\":" + (minutes - 1) * 60 + ",\"limit_s\":" + minutes * 60 + ",\"phase\":\"" + phase + "\",\"notice\":"
            + (phase == "near_limit" ? "{\"code\":\"recording_limit_near\",\"who\":\"collector\",\"args\":{\"minutes\":\"" + minutes + "\",\"left\":\"1\"}}"
               : phase == "reached" ? "{\"code\":\"recording_limit_reached\",\"who\":\"collector\",\"args\":{\"minutes\":\"" + minutes + "\"}}" : "null") + "}");

    static int Main()
    {
        RealityLog.Rig.OperatorNotices.Load(System.IO.File.ReadAllText(Environment.GetEnvironmentVariable("SX_OPERATOR_NOTICES")!));
        var watch = new EpisodeLength();
        Expect(watch.Observe(null, "a") == null, "no block (an older library) asks nothing");
        Expect(watch.Observe(Block("a", "within"), "a") == null, "within the limit asks nothing");
        Expect(watch.Observe(Block("a", "near_limit"), null) == null, "nothing recorded in the app: nothing asked");
        Expect(watch.Observe(Block("other", "near_limit"), "a") == null, "another episode's block asks nothing");
        Expect(watch.Observe(Block("a", "near_limit"), "a") == EpisodeLengthAction.Warn, "a minute before the limit: the warning");
        Expect(watch.Observe(Block("a", "near_limit"), "a") == null, "and only once (every status repeats it)");
        Expect(watch.Observe(Block("a", "reached"), "a") == EpisodeLengthAction.Stop, "at the limit: the stop");
        Expect(watch.Observe(Block("a", "reached"), "a") == null, "once: a stop in progress is never asked again");
        Expect(watch.Observe(Block("a", "reached", recording: false), "a") == null, "the stopped take's block (idle) asks nothing");
        var stalled = new EpisodeLength();
        Expect(stalled.Observe(Block("b", "reached"), "b") == EpisodeLengthAction.Stop, "a poll that skipped the warning still stops");
        Expect(stalled.Observe(Block("b", "near_limit"), "b") == null, "and never warns after it");
        Expect(watch.Observe(Block("c", "near_limit"), "c") == EpisodeLengthAction.Warn, "the next take warns for itself");

        var nearNotice = EpisodeLength.Notice(Block("a", "near_limit"), recording: true);
        Expect(nearNotice?.Code == "recording_limit_near", "the live warning is read from the block");
        var near = OperatorNotices.Render(nearNotice!);
        Expect(near.Headline == "This take stops in 1 min, at the 30-minute limit" && near.Who == "collector",
            "the warning's words: " + near.Headline);
        var reachedNotice = EpisodeLength.Notice(Block("a", "reached", recording: false), recording: false);
        Expect(reachedNotice?.Code == "recording_limit_reached", "the stopped take's notice is read while idle");
        var reached = OperatorNotices.Render(reachedNotice!);
        Expect(reached.Headline + "; " + reached.Action.ToLowerInvariant()
            == "This take reached the 30-minute limit and was stopped; start a new one to continue.",
            "the stop's words: " + reached.Headline + "; " + reached.Action);
        var tuned = OperatorNotices.Render(EpisodeLength.Notice(Block("d", "near_limit", minutes: 10), recording: true)!);
        Expect(tuned.Headline == "This take stops in 1 min, at the 10-minute limit",
            "a ceiling the Factory set is named as the rig says it: " + tuned.Headline);
        Expect(reached.StillFine == "It was kept and is sealed as usual.", "it says the take is kept");
        Expect(EpisodeLength.Notice(Block("a", "reached", recording: false), recording: true) == null,
            "a stopped take's notice is not shown as the live one");
        Expect(EpisodeLength.Notice(Block("a", "within"), recording: true) == null, "within the limit: no notice");

        Expect(RigCues.Line(RigCues.Cue.Warning, "episode a a minute from its length limit")
            == "[RealityLog] cue: Warning (episode a a minute from its length limit)", "the warning cue logs as itself");
        Expect((int)RigCues.Cue.Warning == 5, "the warning cue has its own clip slot");

        Console.WriteLine(failures == 0 ? "episode length: all passed" : $"episode length: {failures} FAILED");
        return failures == 0 ? 0 : 1;
    }
}
