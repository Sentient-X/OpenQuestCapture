#!/usr/bin/env bash
# Linux/macOS equivalent of rebuild_kotlin_library.ps1: build QuestCameraLib's release AAR,
# run its JVM unit tests, and copy the AAR into Unity's Android plugins.
# Needs a JDK (17 or 21) and an Android SDK (ANDROID_HOME, or QuestCameraLib/local.properties).
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
lib="$root/QuestCameraLib"
aar="$lib/app/build/outputs/aar/app-release.aar"
plugin="$root/Assets/Plugins/Android/questcameralib.aar"

if [[ ! -f "$lib/settings.gradle.kts" ]]; then
    echo "QuestCameraLib is not checked out; run: git submodule update --init QuestCameraLib" >&2
    exit 1
fi
if [[ ! -f "$lib/local.properties" ]]; then
    sdk="${ANDROID_HOME:-${ANDROID_SDK_ROOT:-$HOME/Android/Sdk}}"
    if [[ ! -d "$sdk" ]]; then
        echo "No Android SDK: set ANDROID_HOME or write QuestCameraLib/local.properties" >&2
        exit 1
    fi
    echo "sdk.dir=$sdk" > "$lib/local.properties"
fi

echo "[1/3] Building QuestCameraLib (assembleRelease + unit tests)"
(cd "$lib" && bash ./gradlew --console=plain :app:assembleRelease :app:testReleaseUnitTest)

echo "[2/3] Built $aar"
[[ -f "$aar" ]] || { echo "AAR not found at $aar" >&2; exit 1; }

echo "[3/3] Copying to $plugin"
cp "$aar" "$plugin"
echo "Done. Unity reimports the AAR on next open or batch build."
