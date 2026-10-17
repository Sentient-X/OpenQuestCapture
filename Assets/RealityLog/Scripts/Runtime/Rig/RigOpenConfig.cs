# nullable enable

using System;
using Newtonsoft.Json.Linq;

namespace RealityLog.Rig
{
    /// <summary>
    /// The sx_rig_open config, built from an explicit allow-list, never passed through.
    /// libsx_rig refuses any key it does not know (SX_RIG_E_ARGUMENT with refusal
    /// <c>{"kind": "unknown_config_keys", "keys": [...]}</c>), and pod_config.json carries
    /// app-only keys (operator_id, local_only, whatever provisioning adds). No UnityEngine
    /// dependency, so it is tested on the host (tools/sx-rig-stub/test_binding.sh).
    /// </summary>
    public static class RigOpenConfig
    {
        /// <summary>Every key sx_rig_open accepts (libsx_rig's own list, rig.rs KNOWN).</summary>
        public static readonly string[] RigKeys =
        {
            "staging_dir", "rig", "min_free_bytes", "max_episode_seconds", "seal",
            "embodiment_assets_dir", "factory_url", "auth_url", "credentials_dir",
            "headset_serial", "tailscale_ip", "pod_name", "pod_config_path", "rig_type_id",
            "upload_limit_mib_s",
        };

        /// <summary>The rig settings pod_config.json may give, copied when present.</summary>
        private static readonly string[] Settings =
            { "min_free_bytes", "max_episode_seconds", "seal", "headset_serial", "pod_name" };

        /// <summary>The site: copied only when Local only is off.</summary>
        private static readonly string[] Site =
            { "factory_url", "auth_url", "rig_type_id", "upload_limit_mib_s" };

        /// <summary>
        /// The config: the app's own paths (staging, credentials, pod_config.json for the
        /// serial, the installed meshes), rig_config.json as <c>rig</c> when present, the
        /// rig settings pod_config.json names, its site only when <paramref name="localOnly"/>
        /// is off, and the tailnet address when pod_config.json names none.
        /// </summary>
        public static JObject Build(JObject podConfig, string stagingDir, string credentialsDir,
            string podConfigPath, string? embodimentAssetsDir, string? tailscaleIp, JToken? rig, bool localOnly)
        {
            var config = new JObject
            {
                ["staging_dir"] = stagingDir,
                ["credentials_dir"] = credentialsDir,
                ["pod_config_path"] = podConfigPath,
            };
            foreach (var key in Settings)
            {
                if (podConfig[key] is JToken value && value.Type != JTokenType.Null) config[key] = value.DeepClone();
            }
            if (!localOnly)
            {
                foreach (var key in Site)
                {
                    if (podConfig[key] is JToken value && value.Type != JTokenType.Null) config[key] = value.DeepClone();
                }
            }
            if (podConfig["tailscale_ip"] is JValue { Type: JTokenType.String } ip)
            {
                config["tailscale_ip"] = ip.DeepClone();
            }
            else if (!string.IsNullOrEmpty(tailscaleIp))
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
            foreach (var property in config.Properties())
            {
                if (Array.IndexOf(RigKeys, property.Name) < 0)
                {
                    throw new InvalidOperationException($"RigOpenConfig built a key libsx_rig does not know: {property.Name}");
                }
            }
            return config;
        }
    }
}
