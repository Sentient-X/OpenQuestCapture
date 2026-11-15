// Host check of Assets/RealityLog/Scripts/Runtime/Rig/GripperSwitch.cs: libsx_rig's
// gripper_switch counters become one toggle per new right-gripper hold and one refusal per
// refused hold; the first read, and counters that went back (the rig reopened), are baselines.
// Run by test_binding.sh; not part of the Unity project.
using System;
using Newtonsoft.Json.Linq;
using RealityLog.Rig;

static class GripperSwitchHarness
{
    static int failures;
    static void Expect(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) failures++; }

    static JObject Block(long holds, long refusals, string notice = "null") =>
        JObject.Parse("{\"leader\":\"right\",\"holds\":" + holds + ",\"refusals\":" + refusals + ",\"notice\":" + notice + "}");

    static int Main()
    {
        var sw = new GripperSwitch();
        Expect(sw.Observe(null) == null, "no block (an older library) asks nothing");
        Expect(sw.Observe(Block(3, 1)) == null, "the first read is a baseline: holds counted before the app came are never replayed");
        Expect(sw.Observe(Block(3, 1)) == null, "the same counters again ask nothing (every status repeats them)");
        Expect(sw.Observe(Block(4, 1)) == GripperSwitchPress.Toggle, "a new hold is one toggle");
        Expect(sw.Observe(Block(4, 1)) == null, "and only once");
        Expect(sw.Observe(Block(6, 1)) == GripperSwitchPress.Toggle, "two holds between reads are one toggle, never two");
        Expect(sw.Observe(Block(6, 2)) == GripperSwitchPress.Refused, "a refused hold is a refusal");
        Expect(sw.Observe(Block(0, 0)) == null, "counters that went back (the rig reopened) are a new baseline");
        Expect(sw.Observe(Block(1, 0)) == GripperSwitchPress.Toggle, "then its first hold toggles");
        Expect(sw.Observe(JObject.Parse("{\"holds\":\"2\",\"refusals\":0}")) == null, "a counter that is no integer is not read");
        Expect(sw.Observe(Block(2, 0)) == GripperSwitchPress.Toggle, "and does not move the baseline");

        Expect(GripperSwitch.Notice(Block(0, 0)) == null, "no notice while the sides agree");
        var notice = GripperSwitch.Notice(Block(0, 1, "{\"code\":\"gripper_switch_mismatch\",\"who\":\"technician\",\"args\":{\"side\":\"right\"}}"));
        Expect(notice != null && notice.Code == "gripper_switch_mismatch" && notice.Args["side"] == "right",
            "the mismatch notice is read from the block");

        Expect(GripperSwitch.Act(GripperSwitchPress.Toggle, false) == GripperSwitchAction.StartOrStop,
            "a hold while idle or recording runs the menu button's start or stop");
        Expect(GripperSwitch.Act(GripperSwitchPress.Toggle, true) == GripperSwitchAction.Ignore,
            "a hold while a start is arming is ignored silently, as the menu button is then");
        Expect(GripperSwitch.Act(GripperSwitchPress.Refused, false) == GripperSwitchAction.Refuse,
            "a refused hold pulses long and buzzes");
        Expect(GripperSwitch.Act(GripperSwitchPress.Refused, true) == GripperSwitchAction.Ignore,
            "and is ignored too while a start or stop runs");

        Console.WriteLine(failures == 0 ? "gripper switch: all passed" : $"gripper switch: {failures} FAILED");
        return failures == 0 ? 0 : 1;
    }
}
