// Host check of RigOpenConfig: only libsx_rig's keys reach sx_rig_open; pod_config.json's
// app-only keys (operator_id, local_only, anything else) never do; Local only drops the site.
// Run by test_binding.sh; not part of the Unity project.
using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using RealityLog.Rig;

static class RigOpenConfigHarness
{
    static int failures;
    static void Expect(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) failures++; }

    static int Main()
    {
        var pod = JObject.Parse("{\"headset_serial\":\"3487C10H9W0C59\",\"operator_id\":\"bench-operator\",\"local_only\":true," +
            "\"factory_url\":\"https://app.sentientx.io/d/factory/\",\"auth_url\":\"https://app.sentientx.io/d/auth/\"," +
            "\"rig_type_id\":\"rig_yubi\",\"max_episode_seconds\":300,\"provisioned_by\":\"bench\",\"pod_name\":null}");
        var rig = JObject.Parse("{\"profile\":\"bench\",\"binding\":\"identity\",\"rig_id\":\"bench-quest3s\"}");

        var local = RigOpenConfig.Build(pod, "/s", "/c", "/p/pod_config.json", "/m", "100.64.0.9", rig, localOnly: true);
        var keys = local.Properties().Select(p => p.Name).ToArray();
        Expect(keys.All(k => RigOpenConfig.RigKeys.Contains(k)), "every key is the rig's: " + string.Join(",", keys));
        Expect(local["operator_id"] == null && local["local_only"] == null && local["provisioned_by"] == null,
            "operator_id, local_only and other app keys never reach sx_rig_open");
        Expect(local["factory_url"] == null && local["auth_url"] == null && local["rig_type_id"] == null, "Local only drops the site");
        Expect((string?)local["headset_serial"] == "3487C10H9W0C59" && (int)local["max_episode_seconds"]! == 300, "rig settings are kept");
        Expect(local["pod_name"] == null, "a null setting is not passed");
        Expect((string?)local["rig"]!["rig_id"] == "bench-quest3s" && (string?)local["embodiment_assets_dir"] == "/m"
            && (string?)local["pod_config_path"] == "/p/pod_config.json" && (string?)local["tailscale_ip"] == "100.64.0.9",
            "rig, meshes, pod_config_path and the tailnet address are passed");

        var site = RigOpenConfig.Build(pod, "/s", "/c", "/p", null, null, null, localOnly: false);
        Expect((string?)site["factory_url"] == "https://app.sentientx.io/d/factory/" && (string?)site["rig_type_id"] == "rig_yubi",
            "with Local only off the site is passed");
        Expect(site["rig"] == null && site["embodiment_assets_dir"] == null && site["operator_id"] == null,
            "no rig_config.json, no meshes: those keys are absent; app keys still absent");

        Console.WriteLine(failures == 0 ? "ALL PASS" : failures + " FAILED");
        return failures;
    }
}
