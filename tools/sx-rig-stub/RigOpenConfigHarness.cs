// Host check of RigOpenConfig against the rig's shipped key list
// (Assets/RealityLog/Resources/sx_rig_config_keys.json, libsx_rig's own rig.rs KNOWN):
// the app's policy names only keys the rig knows; app-only and misspelled pod_config.json keys
// never reach sx_rig_open and are reported; the site always goes (no Local only mode), and a
// local_only an earlier build provisioned is tolerated and never sent.
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
        var local = RigOpenConfig.Build(pod, rigKeys, "/s", "/c", "/p/pod_config.json", "/m", "100.64.0.9", rig);
        var keys = local.Properties().Select(p => p.Name).ToArray();
        Expect(keys.All(rigKeys.Contains), "every key sent is in the rig's list: " + string.Join(",", keys));
        Expect(local["operator_id"] == null, "operator_id never reaches sx_rig_open");
        Expect(!RigOpenConfig.AppKeys.Contains("operator_id") && RigOpenConfig.UnknownPodConfigKeys(JObject.Parse("{\"operator_id\":\"bench-operator\"}"), rigKeys).Length == 0,
            "operator_id is retired: never read, and a stale one still opens");
        // local_only is retired: the rig no longer takes it, a pod_config.json an earlier build
        // provisioned with it still opens, and it never reaches sx_rig_open, true or false.
        Expect(!rigKeys.Contains("local_only"), "the rig's key list has no local_only");
        Expect(!RigOpenConfig.AppKeys.Contains("local_only") && RigOpenConfig.RetiredKeys.Contains("local_only")
            && RigOpenConfig.UnknownPodConfigKeys(JObject.Parse("{\"local_only\":false}"), rigKeys).Length == 0,
            "local_only is retired: never read, and a stale one still opens");
        Expect(local["local_only"] == null, "local_only never reaches sx_rig_open");
        Expect((string?)local["headset_serial"] == "3487C10H9W0C59" && (int)local["max_episode_seconds"]! == 300 && local["pod_name"] == null,
            "rig settings are kept, a null one is not passed");
        Expect((string?)local["rig"]!["rig_id"] == "yubi-06" && (string?)local["embodiment_assets_dir"] == "/m"
            && (string?)local["rig_config_path"] == "/p/pod_config.json" && (string?)local["tailscale_ip"] == "100.64.0.9",
            "rig, meshes, rig_config_path and the tailnet address are passed");
        Expect((string?)local["factory_url"] == "https://app.sentientx.io/d/factory/" && (string?)local["auth_url"] == "https://app.sentientx.io/d/auth/"
            && (string?)local["rig_type_id"] == "rig_yubi", "the site pod_config.json names is always passed, even beside a stale local_only: true");

        // Wrist colour 001: never sent, even when the library offers it (auto white balance and exposure); pod_config.json cannot turn it on.
        var with001 = new System.Collections.Generic.HashSet<string>(rigKeys) { RigOpenConfig.UmiWristColour001Key };
        var quest001 = RigOpenConfig.Build(new JObject(), with001, "/s", "/c", "/p", null, null, null);
        Expect(quest001[RigOpenConfig.UmiWristColour001Key] == null, "an ordinary open leaves wrist colour 001 off: the cameras keep auto white balance and exposure");
        Expect(RigOpenConfig.UnknownPodConfigKeys(new JObject { [RigOpenConfig.UmiWristColour001Key] = true }, with001)
            .Contains(RigOpenConfig.UmiWristColour001Key), "pod_config.json cannot turn wrist colour 001 on");
        var without001 = new System.Collections.Generic.HashSet<string>(rigKeys);
        without001.Remove(RigOpenConfig.UmiWristColour001Key);
        Expect(RigOpenConfig.Build(new JObject(), without001, "/s", "/c", "/p", null, null, null)[RigOpenConfig.UmiWristColour001Key] == null
            && RigOpenConfig.PolicyKeysUnknownToRig(without001).Length == RigOpenConfig.PolicyKeysUnknownToRig(rigKeys).Length,
            "an older library without wrist colour 001 still opens, without the key");

        // quest_apk_build_id goes only to a rig whose list has it.
        var withId = new System.Collections.Generic.HashSet<string>(rigKeys) { "quest_apk_build_id" };
        var newer = RigOpenConfig.Build(pod, withId, "/s", "/c", "/p", null, null, null, "sha256:abc");
        var older = RigOpenConfig.Build(pod, new System.Collections.Generic.HashSet<string>(rigKeys) { }, "/s", "/c", "/p", null, null, null, "sha256:abc");
        Expect((string?)newer["quest_apk_build_id"] == "sha256:abc", "a rig that takes quest_apk_build_id gets the APK's identity");
        Expect(rigKeys.Contains("quest_apk_build_id") || older["quest_apk_build_id"] == null, "a rig whose list lacks it never gets it");

        // quest_app_version goes only to a rig whose list has it: the app's versionName and versionCode.
        var withVersion = new System.Collections.Generic.HashSet<string>(rigKeys) { "quest_app_version" };
        var version = RigOpenConfig.QuestAppVersion("1.5.0", 58);
        var named = RigOpenConfig.Build(pod, withVersion, "/s", "/c", "/p", null, null, null, null, version);
        Expect((string?)named["quest_app_version"]!["version_name"] == "1.5.0" && (long)named["quest_app_version"]!["version_code"]! == 58
            && ((JObject)named["quest_app_version"]!).Count == 2, "a rig that takes quest_app_version gets the app's version");
        var withoutVersion = new System.Collections.Generic.HashSet<string>(rigKeys);
        withoutVersion.Remove("quest_app_version");
        Expect(RigOpenConfig.Build(pod, withoutVersion, "/s", "/c", "/p", null, null, null, null, version)["quest_app_version"] == null,
            "a rig whose list lacks quest_app_version never gets it");

        // Even a rig list that still named local_only (an older libsx_rig) is never sent it.
        var withLocal = new System.Collections.Generic.HashSet<string>(rigKeys) { "local_only" };
        var older2 = RigOpenConfig.Build(pod, withLocal, "/s", "/c", "/p", null, null, null);
        Expect(older2["local_only"] == null && (string?)older2["credentials_dir"] == "/c", "local_only is never sent, whatever the rig's list");

        // A key the rig list lacks is never sent: Build throws instead.
        var narrow = new System.Collections.Generic.HashSet<string>(rigKeys);
        narrow.Remove("max_episode_seconds");
        var threw = false;
        try { RigOpenConfig.Build(pod, narrow, "/s", "/c", "/p", null, null, null); }
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
