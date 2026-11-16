# nullable enable

using System;

namespace RealityLog.Rig
{
    /// <summary>What one press of a left-controller button did.</summary>
    public enum MenuPress
    {
        /// <summary>☰ (the left menu button, three lines): the recorder's start or stop ran.</summary>
        StartOrStop,
        /// <summary>☰ while a start or stop runs: ignored silently, as a gripper hold is then.</summary>
        Busy,
        /// <summary>☰ while the app has no input focus (a system overlay holds it): dropped, buzzed.</summary>
        NoInputFocus,
        /// <summary>☰ with no recorder: buzzed.</summary>
        NoRecorder,
        /// <summary>Y: the recordings menu opened or closed. Y never starts or stops a recording.</summary>
        ToggleMenu,
        /// <summary>Y within <see cref="MenuButtons.YDebounceSeconds"/> of the last toggle: ignored, buzzed.</summary>
        Debounced,
    }

    /// <summary>
    /// The left controller's two buttons, judged the same way wherever they are read (sx#4210).
    /// ☰ (<c>OVRInput.Button.Start</c> on the left Touch) starts or stops a recording; Y
    /// (<c>Button.Four</c>) only opens and closes the recordings menu. Every press logs one line,
    /// and a press that did nothing the collector asked for buzzes (the refused cue), since the
    /// collector wears the headset on the forehead and never sees the display. A ☰ press while a
    /// start or stop runs stays silent: the owner's rule for the gripper's hold, which is that
    /// button pressed. Pure: no UnityEngine, tested on the host (tools/sx-rig-stub).
    /// </summary>
    public static class MenuButtons
    {
        /// <summary>
        /// A Y press within this long of the last toggle is ignored. On pod5 (2026-11-15) one
        /// collector's Y opened the menu and closed it again 200-360 ms later, five times in a
        /// row: the menu builds its rows after opening and the collector pressed again.
        /// </summary>
        public const double YDebounceSeconds = 0.75;

        /// <summary>
        /// One ☰ press. <paramref name="startOrStop"/> is the recorder's toggle
        /// (<see cref="PodControls.StartOrStop"/>, called with no side asked); null when the
        /// recorder is not running. <paramref name="buzz"/> plays the refused cue with its reason.
        /// </summary>
        public static MenuPress Start(bool hasInputFocus, bool startOrStopInProgress, bool recording,
            Func<bool?, Action<bool, string>?, string?>? startOrStop, Action<string> log, Action<string> buzz)
        {
            if (!hasInputFocus)
            {
                log("menu-button press dropped: the app has no input focus (a system overlay holds the controllers)");
                buzz("menu-button press dropped: no input focus");
                return MenuPress.NoInputFocus;
            }
            if (startOrStopInProgress)
            {
                log("menu-button press ignored: a start or stop is in progress");
                return MenuPress.Busy;
            }
            if (startOrStop == null)
            {
                log("menu-button press, but the recorder is not running");
                buzz("menu-button press: recorder not running");
                return MenuPress.NoRecorder;
            }
            log($"menu-button {(recording ? "stop" : "start")} pressed");
            // The toggle logs and cues its own outcome (start rises, stop falls, refused buzzes).
            startOrStop(null, null);
            return MenuPress.StartOrStop;
        }

        /// <summary>
        /// One Y press at <paramref name="now"/> (seconds, the caller's clock). Returns whether the
        /// menu toggles; <paramref name="lastToggleAt"/> moves only when it does.
        /// </summary>
        public static MenuPress Y(double now, ref double lastToggleAt, bool menuOpen, Action<string> log, Action<string> buzz)
        {
            var since = now - lastToggleAt;
            if (since < YDebounceSeconds)
            {
                log($"Y press {since * 1000.0:F0} ms after the last toggle ignored (menu stays {(menuOpen ? "open" : "closed")})");
                buzz("Y press debounced");
                return MenuPress.Debounced;
            }
            lastToggleAt = now;
            log($"Y press: recordings menu {(menuOpen ? "closes" : "opens")}");
            return MenuPress.ToggleMenu;
        }
    }
}
