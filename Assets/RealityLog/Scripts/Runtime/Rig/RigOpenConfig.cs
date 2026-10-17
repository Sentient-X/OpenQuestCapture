# nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RealityLog.Rig
{
    /// <summary>
    /// The sx_rig_open config and the validation of pod_config.json. The keys sx_rig_open
    /// accepts are not listed here: they are libsx_rig's own (rig.rs KNOWN), shipped beside the
    /// .so as <c>sx_rig_config_keys.json</c> (Assets/RealityLog/Resources, copied by
    /// scripts/copy_rig_artifacts.sh) and passed in as <c>rigKeys</c>. What this file owns is
    /// app policy: which of those keys pod_config.json may set, and the app's own keys. No
    /// UnityEngine dependency, so it is tested on the host (tools/sx-rig-stub/test_binding.sh).
    /// </summary>
    public static class RigOpenConfig
    {
        /// <summary>pod_config.json keys the app itself reads; never sent to the rig.</summary>
        public static readonly string[] AppKeys = { "operator_id", "local_only" };

        /// <summary>Rig settings pod_config.json may give, sent when present.</summary>
        public static readonly string[] Settings =
            { "min_free_bytes", "max_episode_seconds", "seal", "headset_serial", "pod_name", "tailscale_ip" };

        /// <summary>The site pod_config.json may give, sent only when Local only is off.</summary>
        public static readonly string[] Site = { "factory_url", "auth_url", "rig_type_id", "upload_limit_mib_s" };

        /// <summary>Rig keys the app sets itself (pod_config.json may not).</summary>
        public static readonly string[] AppSet =
            { "staging_dir", "credentials_dir", "pod_config_path", "embodiment_assets_dir", "rig" };

        /// <summary>
        /// The rig's key set from <c>sx_rig_config_keys.json</c>: a JSON array of strings, or an
        /// object whose <c>keys</c> is one. Throws <see cref="InvalidOperationException"/> on any
        /// other shape, so a build never runs without the rig's own list.
        /// </summary>
        public static HashSet<string> ParseRigKeys(string json)
        {
            JToken token;
            try
            {
                token = JToken.Parse(json);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException("sx_rig_config_keys.json is not JSON: " + ex.Message);
            }
            var list = token as JArray ?? (token as JObject)?["keys"] as JArray
                ?? throw new InvalidOperationException("sx_rig_config_keys.json is neither an array nor {\"keys\": [...]}");
            var keys = new HashSet<string>();
            foreach (var item in list)
            {
                if (item.Type != JTokenType.String || string.IsNullOrEmpty((string?)item))
                {
                    throw new InvalidOperationException("sx_rig_config_keys.json lists a non-string key");
                }
                keys.Add((string)item!);
            }
            if (keys.Count == 0) throw new InvalidOperationException("sx_rig_config_keys.json lists no keys");
            return keys;
        }

        /// <summary>
        /// The app-policy keys the rig does not know (they would be refused at open): empty when
        /// this build's policy agrees with the shipped rig key set.
        /// </summary>
        public static string[] PolicyKeysUnknownToRig(ISet<string> rigKeys) =>
            Settings.Concat(Site).Concat(AppSet).Where(k => !rigKeys.Contains(k)).ToArray();

        /// <summary>
        /// pod_config.json keys that are neither the app's nor rig keys it may set: a misspelled
        /// <c>max_episode_secs</c>, a key only the app sets (<c>staging_dir</c>), a key the rig
        /// does not know. Never silently ignored: the coordinator refuses to open on them.
        /// </summary>
        public static string[] UnknownPodConfigKeys(JObject podConfig, ISet<string> rigKeys) =>
            podConfig.Properties().Select(p => p.Name)
                .Where(k => !AppKeys.Contains(k)
                    && !((Settings.Contains(k) || Site.Contains(k)) && rigKeys.Contains(k)))
                .ToArray();

        /// <summary>
        /// The config: the app's own paths (staging, credentials, pod_config.json for the
        /// serial, the installed meshes), rig_config.json as <c>rig</c> when present, the rig
        /// settings pod_config.json names, its site only when <paramref name="localOnly"/> is
        /// off, and the tailnet address when pod_config.json names none. Throws
        /// <see cref="InvalidOperationException"/> on any key not in <paramref name="rigKeys"/>,
        /// rather than send it.
        /// </summary>
        public static JObject Build(JObject podConfig, ISet<string> rigKeys, string stagingDir, string credentialsDir,
            string podConfigPath, string? embodimentAssetsDir, string? tailscaleIp, JToken? rig, bool localOnly)
        {
            var config = new JObject
            {
                ["staging_dir"] = stagingDir,
                ["credentials_dir"] = credentialsDir,
                ["pod_config_path"] = podConfigPath,
            };
            foreach (var key in Settings.Concat(localOnly ? Array.Empty<string>() : Site))
            {
                if (podConfig[key] is JToken value && value.Type != JTokenType.Null) config[key] = value.DeepClone();
            }
            if (config["tailscale_ip"] == null && !string.IsNullOrEmpty(tailscaleIp))
            {
                config["tailscale_ip"] = tailscaleIp;
            }
            if (!string.IsNullOrEmpty(embodimentAssetsDir))
            {
                config["embodiment_assets_dir"] = embodimentAssetsDir;
            }
            if (rig != null)
            {
                config["rig"] = rig.DeepClone();
            }
            var unknown = config.Properties().Select(p => p.Name).Where(k => !rigKeys.Contains(k)).ToArray();
            if (unknown.Length > 0)
            {
                throw new InvalidOperationException("the open config would carry keys libsx_rig does not know: " + string.Join(", ", unknown));
            }
            return config;
        }
    }
}
