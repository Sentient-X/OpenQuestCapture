#!/usr/bin/env bash
# Copies the rig's three shipped artifacts into this project, together, so they always match:
#   libsx_rig.so              -> Assets/Plugins/Android/libs/arm64-v8a/libsx_rig.so
#   sx_rig_config_keys.json   -> Assets/RealityLog/Resources/sx_rig_config_keys.json
#   sx_operator_notices.json  -> Assets/RealityLog/Resources/sx_operator_notices.json
# The notices file is the operator's words keyed by the codes libsx_rig names (OperatorNotices.cs);
# the phone's catalogue equals it, so both screens say the same.
# The keys file is libsx_rig's own open-config key list (generated from rig.rs KNOWN); the app
# validates pod_config.json and builds the sx_rig_open config against it, and the host harness
# (tools/sx-rig-stub/test_binding.sh) fails when the app's policy names a key it does not list.
#   SX_QUEST_RIG   the sx checkout's experience/data-factory/quest-rig (required)
#   RIG_SO         the built library (default ~/p3/.build/quest-rig/aarch64-linux-android/release/libsx_rig.so)
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
rig="${SX_QUEST_RIG:?set SX_QUEST_RIG to experience/data-factory/quest-rig in an sx checkout}"
so="${RIG_SO:-$HOME/p3/.build/quest-rig/aarch64-linux-android/release/libsx_rig.so}"
keys="$rig/include/sx_rig_config_keys.json"
notices="$rig/include/sx_operator_notices.json"
[[ -f "$so" ]] || { echo "no library at $so" >&2; exit 1; }
[[ -f "$keys" ]] || { echo "no key list at $keys" >&2; exit 1; }
[[ -f "$notices" ]] || { echo "no notice table at $notices" >&2; exit 1; }
cp "$so" "$root/Assets/Plugins/Android/libs/arm64-v8a/libsx_rig.so"
cp "$keys" "$root/Assets/RealityLog/Resources/sx_rig_config_keys.json"
cp "$notices" "$root/Assets/RealityLog/Resources/sx_operator_notices.json"
sha256sum "$root/Assets/Plugins/Android/libs/arm64-v8a/libsx_rig.so" "$root/Assets/RealityLog/Resources/sx_rig_config_keys.json" \
    "$root/Assets/RealityLog/Resources/sx_operator_notices.json"
