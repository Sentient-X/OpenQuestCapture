#!/usr/bin/env bash
# Builds the stub for the host and runs BindingHarness.cs against it with Unity's Mono, then
# once without the library to check the typed library-missing failure.
#   SX_RIG_INCLUDE  directory holding sx_rig.h;  UNITY_EDITOR_DIR  default 6000.3.6f1 under ~/Unity/Hub
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo="$(cd "$here/../.." && pwd)"
editor="${UNITY_EDITOR_DIR:-$HOME/Unity/Hub/Editor/6000.3.6f1/Editor}"
mono="$editor/Data/MonoBleedingEdge/bin/mono"
csc="$editor/Data/MonoBleedingEdge/lib/mono/4.5/csc.exe"
"$here/build.sh" >/dev/null
out="$here/build/harness"
mkdir -p "$out"
"$mono" "$csc" -nologo -nullable:enable -langversion:9 -define:SX_RIG_HOST_TEST \
    -out:"$out/harness.exe" "$here/BindingHarness.cs" "$repo/Assets/RealityLog/Scripts/Runtime/Rig/SxRig.cs"
LD_LIBRARY_PATH="$here/build/host" "$mono" "$out/harness.exe"
missing="$("$mono" "$out/harness.exe" 2>&1 || true)"
if [[ "$missing" == *SxRigLibraryMissingException*library_missing* ]]; then
    echo "PASS library missing -> SxRigLibraryMissingException"
else
    echo "FAIL library missing was not typed" >&2
    exit 1
fi

# RigStatusJson against Newtonsoft.Json from the Unity project's package cache.
newtonsoft="$(ls "$repo"/Library/PackageCache/com.unity.nuget.newtonsoft-json@*/Runtime/Newtonsoft.Json.dll | head -n 1)"
facades="$editor/Data/MonoBleedingEdge/lib/mono/4.5/Facades"
"$mono" "$csc" -nologo -nullable:enable -langversion:9 -r:"$newtonsoft" -r:"$facades/netstandard.dll" \
    -out:"$out/status-harness.exe" "$here/RigStatusJsonHarness.cs" "$repo/Assets/RealityLog/Scripts/Runtime/Rig/RigStatusJson.cs"
cp "$newtonsoft" "$out/"
"$mono" "$out/status-harness.exe"
