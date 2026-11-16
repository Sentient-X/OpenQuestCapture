// Host check of Assets/RealityLog/Scripts/Runtime/Rig/MenuButtons.cs: the left controller's ☰
// (Button.Start on the left Touch) runs the recorder's start or stop with no side asked, a press
// while one runs is ignored silently, one dropped for lost input focus buzzes; Y only opens and
// closes the recordings menu, never starts a recording, and a Y within 0.75 s of the last toggle
// is ignored with the refused cue. Run by test_binding.sh; not part of the Unity project.
using System;
using System.Collections.Generic;
using RealityLog.Rig;

static class MenuButtonHarness
{
    static int failures;
    static void Expect(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) failures++; }

    static int Main()
    {
        var logs = new List<string>();
        var buzzes = new List<string>();
        var calls = new List<bool?>();
        Func<bool?, Action<bool, string>?, string?> startOrStop = (start, done) => { calls.Add(start); return null; };
        void Reset() { logs.Clear(); buzzes.Clear(); calls.Clear(); }

        var press = MenuButtons.Start(true, false, false, startOrStop, logs.Add, buzzes.Add);
        Expect(press == MenuPress.StartOrStop && calls.Count == 1 && calls[0] == null,
            "☰ while idle runs StartOrStop(null): the recorder's own state decides start or stop");
        Expect(logs.Count == 1 && logs[0].Contains("menu-button start"), "and logs 'menu-button start'");
        Expect(buzzes.Count == 0, "and does not buzz (the toggle cues its own outcome)");

        Reset();
        MenuButtons.Start(true, false, true, startOrStop, logs.Add, buzzes.Add);
        Expect(calls.Count == 1 && calls[0] == null && logs[0].Contains("menu-button stop"), "☰ while recording logs 'menu-button stop'");

        Reset();
        press = MenuButtons.Start(true, true, false, startOrStop, logs.Add, buzzes.Add);
        Expect(press == MenuPress.Busy && calls.Count == 0, "☰ while a start or stop runs does not toggle");
        Expect(logs.Count == 1 && logs[0].Contains("ignored") && buzzes.Count == 0,
            "and is logged 'ignored', silently (the owner's rule for a gripper hold then)");

        Reset();
        press = MenuButtons.Start(false, false, false, startOrStop, logs.Add, buzzes.Add);
        Expect(press == MenuPress.NoInputFocus && calls.Count == 0, "☰ without input focus is dropped");
        Expect(logs.Count == 1 && logs[0].Contains("dropped") && buzzes.Count == 1, "and is logged and buzzes");

        Reset();
        press = MenuButtons.Start(true, false, false, null, logs.Add, buzzes.Add);
        Expect(press == MenuPress.NoRecorder && buzzes.Count == 1, "☰ with no recorder buzzes");

        Reset();
        var lastToggle = double.NegativeInfinity;
        press = MenuButtons.Y(10.0, ref lastToggle, false, logs.Add, buzzes.Add);
        Expect(press == MenuPress.ToggleMenu && lastToggle == 10.0 && buzzes.Count == 0, "Y opens the menu");
        Expect(calls.Count == 0, "Y never starts a recording");
        press = MenuButtons.Y(10.3, ref lastToggle, true, logs.Add, buzzes.Add);
        Expect(press == MenuPress.Debounced && lastToggle == 10.0, "Y 300 ms later is ignored and the last toggle stays");
        Expect(logs[^1].Contains("300 ms") && logs[^1].Contains("ignored") && buzzes.Count == 1 && buzzes[0].Contains("debounced"),
            "and is logged with its gap and plays the refused cue");
        press = MenuButtons.Y(10.0 + MenuButtons.YDebounceSeconds, ref lastToggle, true, logs.Add, buzzes.Add);
        Expect(press == MenuPress.ToggleMenu && logs[^1].Contains("closes"), "Y at 0.75 s toggles again (closes)");
        Expect(calls.Count == 0, "and no Y press ever reached StartOrStop");

        Console.WriteLine(failures == 0 ? "menu buttons: all passed" : $"menu buttons: {failures} FAILED");
        return failures == 0 ? 0 : 1;
    }
}
