#nullable enable

using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RealityLog.Rig
{
    /// <summary>
    /// What the collector does with the headset, for the rig's presence samples (sx-pod's): whether
    /// the headset is on a head (the runtime's user-present / proximity state), whether this app
    /// holds its focus, and the collector's inputs counted since the app started. The main thread
    /// writes it each frame; the device-report thread reads it and adds it to Kotlin's battery and
    /// clock report for <c>sx_rig_report_device</c>. No UnityEngine dependency, so it is tested on
    /// the host (tools/sx-rig-stub/DevicePresenceHarness.cs).
    /// </summary>
    public sealed class DevicePresence
    {
        // -1 unread, 0 off a head, 1 worn.
        private int worn = -1;
        private int focused = 1;
        private int paused;
        private long keyPresses;
        private long focusChanges;

        /// <summary>The runtime's user-present state this frame; null when it could not be read.</summary>
        public void Worn(bool? present) => Volatile.Write(ref worn, present is null ? -1 : present.Value ? 1 : 0);

        /// <summary>A button went down this frame (any controller button).</summary>
        public void KeyPressed() => Interlocked.Increment(ref keyPresses);

        /// <summary>The app gained or lost the headset's focus.</summary>
        public void Focus(bool hasFocus)
        {
            Volatile.Write(ref focused, hasFocus ? 1 : 0);
            Interlocked.Increment(ref focusChanges);
        }

        public void Pause(bool isPaused) => Volatile.Write(ref paused, isPaused ? 1 : 0);

        /// <summary>
        /// The device report: Kotlin's <paramref name="batteryJson"/> with <c>headset_worn</c> (only
        /// when read), <c>foreground</c> and the <c>inputs</c> totals added; null when Kotlin's report
        /// is not an object.
        /// </summary>
        public string? Report(string batteryJson)
        {
            if (JToken.Parse(batteryJson) is not JObject report) return null;
            var present = Volatile.Read(ref worn);
            if (present >= 0) report["headset_worn"] = present == 1;
            report["foreground"] = Volatile.Read(ref focused) == 1 && Volatile.Read(ref paused) == 0;
            report["inputs"] = new JObject
            {
                ["key_presses"] = Interlocked.Read(ref keyPresses),
                ["clicks"] = 0,
                ["scrolls"] = 0,
                ["focus_changes"] = Interlocked.Read(ref focusChanges),
            };
            return report.ToString(Formatting.None);
        }
    }
}
