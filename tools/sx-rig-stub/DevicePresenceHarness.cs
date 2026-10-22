#nullable enable
// DevicePresence: the device report the rig's presence samples read — Kotlin's battery report
// with worn (only when read), foreground and the input totals added. Run by test_binding.sh.

using System;
using Newtonsoft.Json.Linq;
using RealityLog.Rig;

internal static class DevicePresenceHarness
{
    private static int failures;

    private static void Expect(bool condition, string what)
    {
        Console.WriteLine((condition ? "PASS " : "FAIL ") + what);
        if (!condition) failures++;
    }

    private static int Main()
    {
        const string battery = "{\"battery_percent\":64,\"charging\":false,\"network_time_synchronized\":true}";
        var presence = new DevicePresence();
        var first = JObject.Parse(presence.Report(battery)!);
        Expect((int)first["battery_percent"]! == 64 && first["headset_worn"] == null,
            "Kotlin's report is kept, and worn is absent until it is read");
        Expect((bool)first["foreground"]! && (long)first["inputs"]!["key_presses"]! == 0, "focused, nothing pressed");
        presence.Worn(true);
        presence.KeyPressed();
        presence.KeyPressed();
        presence.Focus(false);
        var next = JObject.Parse(presence.Report(battery)!);
        Expect((bool)next["headset_worn"]! && !(bool)next["foreground"]!, "worn and out of focus are told");
        Expect((long)next["inputs"]!["key_presses"]! == 2 && (long)next["inputs"]!["focus_changes"]! == 1,
            "inputs are totals since the app started");
        presence.Focus(true);
        presence.Pause(true);
        Expect(!(bool)JObject.Parse(presence.Report(battery)!)["foreground"]!, "a paused app is not in the foreground");
        presence.Worn(null);
        Expect(JObject.Parse(presence.Report(battery)!)["headset_worn"] == null, "worn that cannot be read is absent, never false");
        Expect(presence.Report("[]") == null, "a report that is not an object is not sent");
        return failures == 0 ? 0 : 1;
    }
}
