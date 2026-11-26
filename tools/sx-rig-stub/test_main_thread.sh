#!/usr/bin/env bash
# No sx_rig call on Unity's main thread (OQC build 107): the binding's guard, the rig lane and
# the teardown hand-off against the host STUB libsx_rig.so, and the app's call-site wiring.
#   SX_RIG_INCLUDE  directory holding sx_rig.h;  UNITY_EDITOR_DIR  default 6000.3.6f1 under ~/Unity/Hub
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo="$(cd "$here/../.." && pwd)"
editor="${UNITY_EDITOR_DIR:-$HOME/Unity/Hub/Editor/6000.3.6f1/Editor}"
mono="$editor/Data/MonoBleedingEdge/bin/mono"
csc="$editor/Data/MonoBleedingEdge/lib/mono/4.5/csc.exe"
include="${SX_RIG_INCLUDE:?set SX_RIG_INCLUDE to the directory holding sx_rig.h}"
out="$here/build/main-thread"
mkdir -p "$out" "$here/build/host"
cc -shared -fPIC -O2 -Wall -Wextra -Werror -I"$include" -o "$here/build/host/libsx_rig.so" "$here/sx_rig_stub.c"
rig="$repo/Assets/RealityLog/Scripts/Runtime/Rig"
"$mono" "$csc" -nologo -nullable:enable -langversion:9 -define:SX_RIG_HOST_TEST \
    -out:"$out/harness.exe" "$here/RigMainThreadHarness.cs" "$rig/SxRig.cs" "$rig/RigLane.cs" "$rig/RigUsbRefresh.cs"
LD_LIBRARY_PATH="$here/build/host" "$mono" "$out/harness.exe" "$repo"
