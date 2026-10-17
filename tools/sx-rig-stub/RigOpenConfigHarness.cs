// Host check of RigOpenConfig against the rig's shipped key list
// (Assets/RealityLog/Resources/sx_rig_config_keys.json, libsx_rig's own rig.rs KNOWN):
// the app's policy names only keys the rig knows; app-only and misspelled pod_config.json keys
// never reach sx_rig_open and are reported; Local only drops the site.
// Run by test_binding.sh with the keys file's path; not part of the Unity project.
using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using RealityLog.Rig;

static class RigOpenConfigHarness
{
    static int failures;
    static void Expect(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) failures++; }

    static int Main(string[] args)
    {
        var rigKeys = RigOpenConfig.ParseRigKeys(File.ReadAllText(args[0]));
        Expect(rigKeys.Count > 0, $"the shipped rig key list loads ({rigKeys.Count} keys)");
        var stale = RigOpenConfig.PolicyKeysUnknownToRig(rigKeys);
        Expect(stale.Length == 0, "every key the app's policy sends is in the rig's list" + (stale.Length > 0 ? ": " + string.Join(",", stale) : ""));

        var pod = JObject.Parse("{\"headset_serial\":\"3487C10H9W0C59\",\"operator_id\":\"operator-01\",\"local_only\":true," +
            "\"factory_url\":\"https://app.sentientx.io/d/factory/\",\"auth_url\":\"https://app.sentientx.io/d/auth/\"," +
            "\"rig_type_id\":\"rig_yubi\",\"max_episode_seconds\":300,\"pod_name\":null}");
        Expect(RigOpenConfig.UnknownPodConfigKeys(pod, rigKeys).Length == 0, "a valid pod_config.json has no unknown keys");
        var typo = JObject.Parse("{\"operator_id\":\"x\",\"max_episode_secs\":300,\"staging_dir\":\"/elsewhere\",\"provisioned_by\":\"factory\"}");
        var unknown = RigOpenConfig.UnknownPodConfigKeys(typo, rigKeys);
        Expect(unknown.Contains("max_episode_secs") && unknown.Contains("staging_dir") && unknown.Contains("provisioned_by") && !unknown.Contains("operator_id"),
            "a misspelled key, an app-set key and a foreign key are reported: " + string.Join(",", unknown));

        var rig = JObject.Parse("{\"rig_id\":\"yubi-06\"}");
        var local = RigOpenConfig.Build(pod, rigKeys, "/s", "/c", "/p/pod_config.json", "/m", "100.64.0.9", rig, localOnly: true);
        var keys = local.Properties().Select(p => p.Name).ToArray();
        Expect(keys.All(rigKeys.Contains), "every key sent is in the rig's list: " + string.Join(",", keys));
        Expect(local["operator_id"] == null && local["local_only"] == null, "operator_id and local_only never reach sx_rig_open");
        Expect(local["factory_url"] == null && local["auth_url"] == null && local["rig_type_id"] == null, "Local only drops the site");
        Expect((string?)local["headset_serial"] == "3487C10H9W0C59" && (int)local["max_episode_seconds"]! == 300 && local["pod_name"] == null,
            "rig settings are kept, a null one is not passed");
        Expect((string?)local["rig"]!["rig_id"] == "yubi-06" && (string?)local["embodiment_assets_dir"] == "/m"
            && (string?)local["pod_config_path"] == "/p/pod_config.json" && (string?)local["tailscale_ip"] == "100.64.0.9",
            "rig, meshes, pod_config_path and the tailnet address are passed");
        var site = RigOpenConfig.Build(pod, rigKeys, "/s", "/c", "/p", null, null, null, localOnly: false);
        Expect((string?)site["factory_url"] == "https://app.sentientx.io/d/factory/" && (string?)site["rig_type_id"] == "rig_yubi",
            "with Local only off the site is passed");

        // quest_apk_build_id goes only to a rig whose list has it.
        var withId = new System.Collections.Generic.HashSet<string>(rigKeys) { "quest_apk_build_id" };
        var newer = RigOpenConfig.Build(pod, withId, "/s", "/c", "/p", null, null, null, true, "sha256:abc");
        var older = RigOpenConfig.Build(pod, new System.Collections.Generic.HashSet<string>(rigKeys) { }, "/s", "/c", "/p", null, null, null, true, "sha256:abc");
        Expect((string?)newer["quest_apk_build_id"] == "sha256:abc", "a rig that takes quest_apk_build_id gets the APK's identity");
        Expect(rigKeys.Contains("quest_apk_build_id") || older["quest_apk_build_id"] == null, "a rig whose list lacks it never gets it");

        // A key the rig list lacks is never sent: Build throws instead.
        var narrow = new System.Collections.Generic.HashSet<string>(rigKeys);
        narrow.Remove("max_episode_seconds");
        var threw = false;
        try { RigOpenConfig.Build(pod, narrow, "/s", "/c", "/p", null, null, null, true); }
        catch (InvalidOperationException) { threw = true; }
        Expect(threw, "a key outside the rig's list makes Build throw, not send");
        Expect(RigOpenConfig.ParseRigKeys("{\"keys\":[\"staging_dir\"]}").Contains("staging_dir"), "the {keys: [...]} shape loads too");
        var bad = false;
        try { RigOpenConfig.ParseRigKeys("{\"nope\":1}"); } catch (InvalidOperationException) { bad = true; }
        Expect(bad, "a malformed key list is refused");

        var otherSchema = false;
        try { RigOpenConfig.ParseRigKeys("{\"schema\":\"sx.rig_config_keys/v2\",\"keys\":[\"staging_dir\"]}"); } catch (InvalidOperationException) { otherSchema = true; }
        Expect(otherSchema, "a key list of another schema is refused");

        Console.WriteLine(failures == 0 ? "ALL PASS" : failures + " FAILED");
        return failures;
    }
}
