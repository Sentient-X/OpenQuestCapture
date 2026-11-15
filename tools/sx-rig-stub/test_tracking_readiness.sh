#!/usr/bin/env bash
# Compile the actual monitor with the host SDK seam and exercise admission failures.
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo="$(cd "$here/../.." && pwd)"
editor="${UNITY_EDITOR_DIR:-$HOME/Unity/Hub/Editor/6000.3.6f1/Editor}"
mono="$editor/Data/MonoBleedingEdge/bin/mono"
dotnet="$editor/Data/NetCoreRuntime/dotnet"
csc="$editor/Data/DotNetSdkRoslyn/csc.dll"
references="$editor/Data/MonoBleedingEdge/lib/mono/4.5"
out="$here/build/tracking-readiness"
mkdir -p "$out"
monitorSource="${TRACKING_MONITOR_SOURCE:-$repo/Assets/RealityLog/Scripts/Runtime/HeadTrackingMonitor.cs}"
"$dotnet" "$csc" -nologo -nullable:enable -langversion:9 -noconfig -nostdlib \
    -r:"$references/mscorlib.dll" -r:"$references/System.dll" -r:"$references/System.Core.dll" \
    -out:"$out/harness.exe" "$here/HeadTrackingMonitorHarness.cs" "$monitorSource" \
    "$repo/Assets/RealityLog/Scripts/Runtime/BodyCadence.cs"
"$mono" "$out/harness.exe"
