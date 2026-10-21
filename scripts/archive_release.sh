#!/usr/bin/env bash
# Archives a built APK as a release the Quest APK registry can cite, with its build record:
#   ~/p3/quest-apk-releases/<versionName>-<versionCode>/OpenQuestCapture-<versionName>-<versionCode>.apk
#   ~/p3/quest-apk-releases/<versionName>-<versionCode>/build.json
# Unity APKs are not byte-reproducible, so the registry cites this exact file's sha256; the
# record says what it was built from. A release directory is never overwritten.
#   archive_release.sh <apk>      (run from the commit the APK was built from, tree clean of tracked changes)
#   RELEASES  archive root (default ~/p3/quest-apk-releases)
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
apk="${1:?usage: archive_release.sh <apk>}"
releases="${RELEASES:-$HOME/p3/quest-apk-releases}"
aapt2="$(ls -d "${ANDROID_HOME:-$HOME/Android/Sdk}"/build-tools/*/aapt2 | sort -V | tail -n 1)"
badging_all="$("$aapt2" dump badging "$apk")"   # whole output: a head pipe would SIGPIPE under pipefail
badging="${badging_all%%$'\n'*}"
version_name="$(sed -n "s/.*versionName='\([^']*\)'.*/\1/p" <<<"$badging")"
version_code="$(sed -n "s/.*versionCode='\([^']*\)'.*/\1/p" <<<"$badging")"
package="$(sed -n "s/.*package: name='\([^']*\)'.*/\1/p" <<<"$badging")"
[[ -n "$version_name" && -n "$version_code" ]] || { echo "cannot read the APK's version" >&2; exit 1; }
# Tracked changes would make the record lie about what was built (untracked files are not inputs).
if [[ -n "$(git -C "$root" status --porcelain --untracked-files=no -- . ':!Assets/XR/UserSimulationSettings')" ]]; then
    echo "the OQC tree has tracked changes; archive from the commit the APK was built from" >&2
    exit 1
fi
target="$releases/$version_name-$version_code"
if [[ -e "$target" ]]; then
    echo "release $target already exists; it is never overwritten" >&2
    exit 1
fi
mkdir -p "$target"
name="OpenQuestCapture-$version_name-$version_code.apk"
cp "$apk" "$target/$name"
apk_sha="$(sha256sum "$target/$name" | cut -d' ' -f1)"
so="$root/Assets/Plugins/Android/libs/arm64-v8a/libsx_rig.so"
keys="$root/Assets/RealityLog/Resources/sx_rig_config_keys.json"
# The phone companion (sx packages/sx-companion-android, companionAar), committed like libsx_rig.so.
companion="$root/Assets/Plugins/Android/sx-companion-release.aar"
certs="$("$(dirname "$aapt2")/apksigner" verify --print-certs "$target/$name")"
cert="$(sed -n 's/^Signer #1 certificate SHA-256 digest: //p' <<<"$certs")"
unity="$(sed -n 's/^m_EditorVersion: //p' "$root/ProjectSettings/ProjectVersion.txt")"
cat > "$target/build.json" <<JSON
{
  "schema": "oqc.release/v1",
  "package": "$package",
  "version_name": "$version_name",
  "version_code": $version_code,
  "apk": "$name",
  "apk_sha256": "$apk_sha",
  "quest_apk_build_id": "sha256:$apk_sha",
  "signing_cert_sha256": "$cert",
  "oqc_commit": "$(git -C "$root" rev-parse HEAD)",
  "questcameralib_commit": "$(git -C "$root/QuestCameraLib" rev-parse HEAD)",
  "libsx_rig_so_sha256": "$(sha256sum "$so" | cut -d' ' -f1)",
  "sx_rig_config_keys_sha256": "$(sha256sum "$keys" | cut -d' ' -f1)",
  "sx_companion_aar_sha256": "$(sha256sum "$companion" | cut -d' ' -f1)",
  "unity_version": "$unity",
  "archived_at_utc": "$(date -u +%Y-%m-%dT%H:%M:%SZ)"
}
JSON
chmod a-w "$target/$name" "$target/build.json"
echo "$target"
cat "$target/build.json"
