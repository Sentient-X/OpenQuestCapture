#!/usr/bin/env bash
# Builds (with --build) and archives an APK as a release the Quest APK registry can cite, with its
# build record:
#   ~/p3/quest-apk-releases/<versionName>-<versionCode>/OpenQuestCapture-<versionName>-<versionCode>.apk
#   ~/p3/quest-apk-releases/<versionName>-<versionCode>/build.json
# Unity APKs are not byte-reproducible, so the registry cites this exact file's sha256; the
# record says what it was built from. A release directory is never overwritten.
#   archive_release.sh <apk> --device <serial>     archive an APK built from this commit (tree clean
#                                                  of tracked changes)
#   archive_release.sh --build --device <serial>   the one release command: embodiment assets, Unity
#                                 player build of the committed tree, the gates, archive.
#                                 libsx_rig.so, questcameralib.aar and sx-companion-release.aar are
#                                 committed inputs: refresh them (copy_rig_artifacts.sh,
#                                 rebuild_kotlin_library.sh) and commit first.
# Gates (sx#4210: builds 74 and 75 passed every host harness and failed the first recording on
# pod5): check_jni_abi.py (the AAR has every JNI method the C# binds) before the build, and
# device_smoke.sh on the headset <serial> (it installs the APK there, never while recording) before
# the archive. A failed gate archives nothing. NO_DEVICE_GATE=1 archives without the device gate,
# and build.json then says "device_gate": null: such a build is not ready for anyone.
#   RELEASES      archive root (default ~/p3/quest-apk-releases)
#   UNITY_EDITOR  the editor binary (default ~/p3/Unity/Hub/Editor/<ProjectVersion>/Editor/Unity)
#   SX_QUEST_RIG  --build: an sx checkout's packages/sx-pod-runtime (its packages/sx-embodiments at its pin)
#   OFFLINE=1     --build: pass --offline to cargo
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
releases="${RELEASES:-$HOME/p3/quest-apk-releases}"
aapt2="$(ls -d "${ANDROID_HOME:-$HOME/Android/Sdk}"/build-tools/*/aapt2 | sort -V | tail -n 1)"
unity="$(sed -n 's/^m_EditorVersion: //p' "$root/ProjectSettings/ProjectVersion.txt")"

# Tracked changes would make the record lie about what was built (untracked files are not inputs).
require_clean() {
    if [[ -n "$(git -C "$root" status --porcelain --untracked-files=no -- . ':!Assets/XR/UserSimulationSettings')" ]]; then
        echo "the OQC tree has tracked changes; archive from the commit the APK was built from" >&2
        git -C "$root" status --short --untracked-files=no -- . ':!Assets/XR/UserSimulationSettings' >&2
        exit 1
    fi
}

mode="${1:?usage: archive_release.sh <apk> | --build  [--device <serial>]}"
shift
device=""
if [[ "${1:-}" == "--device" ]]; then device="${2:?--device needs an adb serial}"; shift 2; fi
if [[ -z "$device" && "${NO_DEVICE_GATE:-0}" != 1 ]]; then
    echo "pass --device <adb-serial>: no APK is archived before it passed scripts/device_smoke.sh on a headset" >&2
    echo "(NO_DEVICE_GATE=1 archives one marked not ready)" >&2
    exit 1
fi
python3 "$root/scripts/check_jni_abi.py" "$root/Assets/Plugins/Android/questcameralib.aar"

if [[ "$mode" == "--build" ]]; then
    require_clean
    rig="${SX_QUEST_RIG:?set SX_QUEST_RIG to packages/sx-pod-runtime in an sx checkout}"
    sx="$(git -C "$rig" rev-parse --show-toplevel)"
    # An sx-embodiments checkout off its pin ("+": the main checkout after a pull that moved it)
    # generates meshes the seal does not hold, and the build would ship them. "-" with files is
    # a worktree filled at the pin by sx tools/fill-embodiments.sh.
    pin_state="$(git -C "$sx" submodule status packages/sx-embodiments)"
    case "${pin_state:0:1}" in
        " ") ;;
        -) [[ -n "$(ls -A "$sx/packages/sx-embodiments")" ]] || {
               echo "sx packages/sx-embodiments is empty; run sx tools/fill-embodiments.sh $sx" >&2
               exit 1
           } ;;
        *) echo "sx packages/sx-embodiments is not at its pin ($pin_state)" >&2; exit 1 ;;
    esac
    code="$(sed -n 's/^  AndroidBundleVersionCode: //p' "$root/ProjectSettings/ProjectSettings.asset")"
    name="$(sed -n 's/^  bundleVersion: //p' "$root/ProjectSettings/ProjectSettings.asset")"
    # Fail before a ten-minute build, not after it.
    [[ ! -e "$releases/$name-$code" ]] || {
        echo "release $releases/$name-$code already exists; bump AndroidBundleVersionCode and commit" >&2
        exit 1
    }
    editor="${UNITY_EDITOR:-$HOME/p3/Unity/Hub/Editor/$unity/Editor/Unity}"
    [[ -x "$editor" ]] || { echo "no Unity $unity editor at $editor (set UNITY_EDITOR)" >&2; exit 1; }
    SX_QUEST_RIG="$rig" OFFLINE="${OFFLINE:-0}" "$root/scripts/build_embodiment_assets.sh"
    out="$root/Builds/OpenQuestCapture-$name-$code.apk"
    log="$root/Builds/OpenQuestCapture-$name-$code.unity.log"
    mkdir -p "$root/Builds"
    rm -f "$out"
    # Unity rewrites ProjectSettings.asset (generated icon GUIDs) on every build; put it back so
    # the archive's clean-tree check holds.
    keep="$(mktemp "$root/Builds/ProjectSettings.asset.XXXXXX")"
    cp "$root/ProjectSettings/ProjectSettings.asset" "$keep"
    status=0
    OQC_BUILD_APK_PATH="$out" "$editor" -batchmode -nographics -buildTarget Android \
        -projectPath "$root" -executeMethod QuestBuild.Build -quit -logFile "$log" || status=$?
    cp "$keep" "$root/ProjectSettings/ProjectSettings.asset"
    rm -f "$keep"
    if [[ "$status" -ne 0 ]]; then
        if [[ "$status" -eq 198 ]] || grep -q "No valid Unity Editor license found" "$log"; then
            echo "Unity refused its licence (exit $status). Sign in to Unity Hub on this machine (run" >&2
            echo "'unityhub', sign in, Preferences > Licenses), then rerun. See sx" >&2
            echo ".agents/skills/headset-device/references/build.md." >&2
        else
            grep -E "error CS|Build failed|Build refused" "$log" >&2 || tail -n 40 "$log" >&2
        fi
        exit "$status"
    fi
    # Batchmode can exit 0 with scripts that did not compile (QuestBuild never ran): success is
    # the APK plus Unity's own words, and no compiler error anywhere in the log.
    if grep -q "error CS" "$log" || [[ ! -s "$out" ]] || ! grep -q "^Build succeeded: " "$log"; then
        grep -E "error CS|Build failed|Build refused" "$log" >&2 || tail -n 40 "$log" >&2
        echo "Unity exited 0 without a clean build; see $log" >&2
        exit 1
    fi
    apk="$out"
    rig_commit="$(git -C "$sx" rev-parse HEAD)"
else
    apk="$mode"
    rig_commit=""
fi

gate_json=null
if [[ -n "$device" ]]; then
    gate_dir="$root/Builds/device-gate-$(date -u +%Y%m%dT%H%M%SZ)"
    if ! "$root/scripts/device_smoke.sh" "$device" "$apk" "$gate_dir"; then
        echo "device gate failed on $device: $apk is not archived and not ready ($gate_dir)" >&2
        exit 1
    fi
    gate_json="$(tr -d '\n' < "$gate_dir/verdict.json")"
fi

badging_all="$("$aapt2" dump badging "$apk")"   # whole output: a head pipe would SIGPIPE under pipefail
badging="${badging_all%%$'\n'*}"
version_name="$(sed -n "s/.*versionName='\([^']*\)'.*/\1/p" <<<"$badging")"
version_code="$(sed -n "s/.*versionCode='\([^']*\)'.*/\1/p" <<<"$badging")"
package="$(sed -n "s/.*package: name='\([^']*\)'.*/\1/p" <<<"$badging")"
[[ -n "$version_name" && -n "$version_code" ]] || { echo "cannot read the APK's version" >&2; exit 1; }
require_clean
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
# The submodule commit is the gitlink this OQC commit records (an uninitialised submodule's empty
# directory would answer rev-parse with OQC's own HEAD). The APK ships the committed AAR, which
# may predate it, so its digest is recorded too.
camera_aar="$root/Assets/Plugins/Android/questcameralib.aar"
certs="$("$(dirname "$aapt2")/apksigner" verify --print-certs "$target/$name")"
cert="$(sed -n 's/^Signer #1 certificate SHA-256 digest: //p' <<<"$certs")"
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
  "questcameralib_commit": "$(git -C "$root" rev-parse HEAD:QuestCameraLib)",
  "questcameralib_aar_sha256": "$(sha256sum "$camera_aar" | cut -d' ' -f1)",
  "libsx_rig_so_sha256": "$(sha256sum "$so" | cut -d' ' -f1)",
  "sx_rig_config_keys_sha256": "$(sha256sum "$keys" | cut -d' ' -f1)",
  "sx_companion_aar_sha256": "$(sha256sum "$companion" | cut -d' ' -f1)",
  "embodiment_assets_sx_commit": "$rig_commit",
  "unity_version": "$unity",
  "device_gate": $gate_json,
  "archived_at_utc": "$(date -u +%Y-%m-%dT%H:%M:%SZ)"
}
JSON
chmod a-w "$target/$name" "$target/build.json"
echo "$target"
cat "$target/build.json"
