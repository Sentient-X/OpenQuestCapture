#!/usr/bin/env bash
# Generates the embodiment meshes the on-headset seal checks into
# Assets/StreamingAssets/sx-embodiments/ (gitignored): the sx quest-rig example writes each
# asset by logical path plus manifest.json [{logical_path, sha256, byte_size}], taking the list
# from the seal itself. Unity ships StreamingAssets uncompressed in the APK's assets/, where
# EmbodimentAssets (QuestCameraLib) copies them into filesDir on first launch.
# QuestBuild.Build refuses to build without the manifest. Run before every APK build.
#   SX_QUEST_RIG  sx checkout's experience/data-factory/quest-rig (required)
#   OFFLINE=1     pass --offline to cargo
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
rig="${SX_QUEST_RIG:?set SX_QUEST_RIG to experience/data-factory/quest-rig in an sx checkout}"
out="$root/Assets/StreamingAssets/sx-embodiments"
# Build products stay out of the sx checkout.
target="$(mktemp -d "${TMPDIR:-/tmp}/sx-embodiment-assets.XXXXXX")"
trap 'rm -rf "$target"' EXIT
offline=()
[[ "${OFFLINE:-0}" == "1" ]] && offline=(--offline)
staging="$target/out"
mkdir -p "$staging"
(cd "$rig" && CARGO_TARGET_DIR="$target/cargo" cargo run "${offline[@]}" --locked --quiet --example embodiment_assets -- "$staging")
rm -rf "$out"
mkdir -p "$(dirname "$out")"
mv "$staging" "$out"
echo "embodiment assets -> $out ($(du -sh "$out" | cut -f1))"
