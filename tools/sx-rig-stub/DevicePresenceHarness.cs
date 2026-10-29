#nullable enable
// DevicePresence: the device report the rig's presence samples read — Kotlin's battery report
// with worn (only when read), foreground and the input totals added. Run by test_binding.sh.

using System;
using System.Linq;
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

        // The controllers, in sx_rig.h's shape: none until read, then one per side.
        Expect(JObject.Parse(presence.Report(battery)!)["controllers"] == null, "no controllers before a read");
        presence.Controllers(new ControllerCharge(12, null, true, false));
        var controllers = (JArray)JObject.Parse(presence.Report(battery)!)["controllers"]!;
        Expect(controllers.Count == 2
            && (string)controllers[0]["side"]! == "left" && (int)controllers[0]["battery_percent"]! == 12
            && (bool)controllers[0]["connected"]! && controllers[0]["charging"]!.Type == JTokenType.Null,
            "the left controller: its charge, connected, charging unknown");
        Expect((string)controllers[1]["side"]! == "right" && controllers[1]["battery_percent"]!.Type == JTokenType.Null
            && !(bool)controllers[1]["connected"]!, "the right controller: not connected, no charge");
        Expect(controllers.Children<JObject>().All(c => c.Properties().Select(p => p.Name).OrderBy(n => n)
                .SequenceEqual(new[] { "battery_percent", "charging", "connected", "side" })),
            "each controller carries exactly the rig's four keys");
        presence.Controllers(new ControllerCharge(140, null, true, true));
        Expect(((JArray)JObject.Parse(presence.Report(battery)!)["controllers"]!)[0]["battery_percent"]!.Type == JTokenType.Null,
            "a charge past 100 is not sent");
        presence.Controllers(ControllerCharge.Unknown);
        Expect(JObject.Parse(presence.Report(battery)!)["controllers"] == null, "a failed read sends no controllers");
        return failures == 0 ? 0 : 1;
    }
}
