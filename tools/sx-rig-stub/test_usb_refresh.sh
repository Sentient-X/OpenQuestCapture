#!/usr/bin/env bash
# Host-only concurrency regression: USB enumeration, native attach/detach and fd release
# run on the USB worker, never on Unity's main thread.
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo="$(cd "$here/../.." && pwd)"
editor="${UNITY_EDITOR_DIR:-$HOME/Unity/Hub/Editor/6000.3.6f1/Editor}"
mono="$editor/Data/MonoBleedingEdge/bin/mono"
csc="$editor/Data/MonoBleedingEdge/lib/mono/4.5/csc.exe"
out="$here/build/usb-refresh"
mkdir -p "$out"
"$mono" "$csc" -nologo -nullable:enable -langversion:9 \
    -out:"$out/harness.exe" "$here/RigUsbRefreshHarness.cs" \
    "$repo/Assets/RealityLog/Scripts/Runtime/Rig/RigUsbRefresh.cs"
"$mono" "$out/harness.exe"
"$mono" "$csc" -nologo -nullable:enable -langversion:9 \
    -out:"$out/attach-harness.exe" "$here/RigUsbAttachHarness.cs" \
    "$repo/Assets/RealityLog/Scripts/Runtime/Rig/RigUsbRefresh.cs"
"$mono" "$out/attach-harness.exe" "$repo/Assets/RealityLog/Scripts/Runtime/Rig/RigCoordinator.cs" \
    "$repo/Assets/RealityLog/Scripts/Runtime/Camera/VideoRecorderSurfaceProvider.cs"
