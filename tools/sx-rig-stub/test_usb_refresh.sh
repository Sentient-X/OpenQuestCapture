#!/usr/bin/env bash
# Host-only concurrency regression for the main-thread/USB refresh hand-off.
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
    -out:"$out/flow-harness.exe" "$here/RigUsbSnapshotFlowHarness.cs" \
    "$repo/Assets/RealityLog/Scripts/Runtime/Rig/RigUsbRefresh.cs"
"$mono" "$out/flow-harness.exe" "$repo/Assets/RealityLog/Scripts/Runtime/Rig/RigCoordinator.cs"
