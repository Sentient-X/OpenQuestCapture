#!/usr/bin/env bash
# Focused host-only review checks; no headset or Factory calls.
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo="$(cd "$here/../.." && pwd)"
editor="${UNITY_EDITOR_DIR:-$HOME/Unity/Hub/Editor/6000.3.6f1/Editor}"
mono="$editor/Data/MonoBleedingEdge/bin/mono"
csc="$editor/Data/MonoBleedingEdge/lib/mono/4.5/csc.exe"
newtonsoft="${SX_NEWTONSOFT_DLL:-}"
if [[ -z "$newtonsoft" ]]; then
    newtonsoft="$(ls "$repo"/Library/PackageCache/com.unity.nuget.newtonsoft-json@*/Runtime/Newtonsoft.Json.dll | head -n 1)"
fi
out="$here/build/review-harness"
mkdir -p "$out"
export SX_OPERATOR_NOTICES="$repo/Assets/RealityLog/Resources/sx_operator_notices.json"
"$mono" "$csc" -nologo -nullable:enable -langversion:9 -r:"$newtonsoft" \
    -r:"$editor/Data/MonoBleedingEdge/lib/mono/4.5/Facades/netstandard.dll" \
    -out:"$out/review-harness.exe" "$here/PodReviewHarness.cs" \
    "$repo/Assets/RealityLog/Scripts/Runtime/Rig/PodScreen.cs" "$repo/Assets/RealityLog/Scripts/Runtime/Rig/GripperSwitch.cs" "$repo/Assets/RealityLog/Scripts/Runtime/Rig/EpisodeLength.cs" \
    "$repo/Assets/RealityLog/Scripts/Runtime/Rig/PodStatusBoard.cs" \
    "$repo/Assets/RealityLog/Scripts/Runtime/Rig/RigStatusJson.cs" \
    "$repo/Assets/RealityLog/Scripts/Runtime/Rig/OperatorNotices.cs"
cp "$newtonsoft" "$out/"
"$mono" "$out/review-harness.exe"
