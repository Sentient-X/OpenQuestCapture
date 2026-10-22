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

# RigStartGate (with the binding's types and RigCoordinator's RigRefusal compiled in).
"$mono" "$csc" -nologo -nullable:enable -langversion:9 -define:SX_RIG_HOST_TEST -r:"$newtonsoft" -r:"$facades/netstandard.dll" \
    -out:"$out/gate-harness.exe" "$here/RigStartGateHarness.cs" \
    "$repo/Assets/RealityLog/Scripts/Runtime/Rig/RigStartGate.cs" \
    "$repo/Assets/RealityLog/Scripts/Runtime/Rig/RigStatusJson.cs" \
    "$repo/Assets/RealityLog/Scripts/Runtime/Rig/SxRig.cs" \
    "$repo/Assets/RealityLog/Scripts/Runtime/Rig/RigRefusal.cs"
"$mono" "$out/gate-harness.exe"

# RigOpenConfig: the sx_rig_open allow-list.
"$mono" "$csc" -nologo -nullable:enable -langversion:9 -r:"$newtonsoft" -r:"$facades/netstandard.dll" \
    -out:"$out/open-config-harness.exe" "$here/RigOpenConfigHarness.cs" \
    "$repo/Assets/RealityLog/Scripts/Runtime/Rig/RigOpenConfig.cs"
"$mono" "$out/open-config-harness.exe" "$repo/Assets/RealityLog/Resources/sx_rig_config_keys.json"

# SessionRecovery: closing out sessions a killed process left without a manifest.
"$mono" "$csc" -nologo -nullable:enable -langversion:9 -r:"$facades/netstandard.dll" \
    -out:"$out/recovery-harness.exe" "$here/SessionRecoveryHarness.cs" \
    "$repo/Assets/RealityLog/Scripts/Runtime/SessionRecovery.cs" "$repo/Assets/RealityLog/Scripts/Runtime/StreamManifest.cs"
"$mono" "$out/recovery-harness.exe"

# RecordingDeletePolicy: what a delete may touch.
"$mono" "$csc" -nologo -nullable:enable -langversion:9 -r:"$newtonsoft" -r:"$facades/netstandard.dll" \
    -out:"$out/delete-harness.exe" "$here/DeletePolicyHarness.cs" \
    "$repo/Assets/RealityLog/Scripts/Runtime/Common/RecordingDeletePolicy.cs"
"$mono" "$out/delete-harness.exe"

# PodSetupJson: the console's QR payload and the keyboard inputs.
"$mono" "$csc" -nologo -nullable:enable -langversion:9 -r:"$newtonsoft" -r:"$facades/netstandard.dll" \
    -out:"$out/pod-setup-harness.exe" "$here/PodSetupJsonHarness.cs" \
    "$repo/Assets/RealityLog/Scripts/Runtime/Rig/PodSetupJson.cs" "$repo/Assets/RealityLog/Scripts/Runtime/Rig/RigApiGuard.cs"
"$mono" "$out/pod-setup-harness.exe"

# PodStatusBoard: the recordings menu's Pod status block and the last start's refusal.
"$mono" "$csc" -nologo -nullable:enable -langversion:9 -r:"$newtonsoft" -r:"$facades/netstandard.dll" \
    -out:"$out/pod-status-harness.exe" "$here/PodStatusBoardHarness.cs" \
    "$repo/Assets/RealityLog/Scripts/Runtime/Rig/PodStatusBoard.cs" "$repo/Assets/RealityLog/Scripts/Runtime/Rig/RigStatusJson.cs"
"$mono" "$out/pod-status-harness.exe"

# PodScreenBoard: the Pod card's status screen (blocker, devices, collection, episodes).
"$mono" "$csc" -nologo -nullable:enable -langversion:9 -r:"$newtonsoft" -r:"$facades/netstandard.dll" \
    -out:"$out/pod-screen-harness.exe" "$here/PodScreenHarness.cs" \
    "$repo/Assets/RealityLog/Scripts/Runtime/Rig/PodScreen.cs" \
    "$repo/Assets/RealityLog/Scripts/Runtime/Rig/PodStatusBoard.cs" "$repo/Assets/RealityLog/Scripts/Runtime/Rig/RigStatusJson.cs"
"$mono" "$out/pod-screen-harness.exe"

# The phone companion: HeadsetStatus from the status screen, the sign-in hand-off, the Phone section.
"$mono" "$csc" -nologo -nullable:enable -langversion:9 -r:"$newtonsoft" -r:"$facades/netstandard.dll" \
    -out:"$out/companion-harness.exe" "$here/CompanionHarness.cs" \
    "$repo/Assets/RealityLog/Scripts/Runtime/Rig/Companion.cs" \
    "$repo/Assets/RealityLog/Scripts/Runtime/Rig/PodScreen.cs" \
    "$repo/Assets/RealityLog/Scripts/Runtime/Rig/PodStatusBoard.cs" "$repo/Assets/RealityLog/Scripts/Runtime/Rig/RigStatusJson.cs"
"$mono" "$out/companion-harness.exe"

# RecordingListRefreshGate: the recordings list stays off the main thread's back while recording.
"$mono" "$csc" -nologo -nullable:enable -langversion:9 \
    -out:"$out/list-refresh-harness.exe" "$here/RecordingListRefreshGateHarness.cs" \
    "$repo/Assets/RealityLog/Scripts/Runtime/Common/RecordingListRefreshGate.cs"
"$mono" "$out/list-refresh-harness.exe"

# AppLogFile: the app's log lines the rig ships, rolled and bounded.
"$mono" "$csc" -nologo -nullable:enable -langversion:9 -r:"$facades/netstandard.dll" \
    -out:"$out/app-log-harness.exe" "$here/AppLogHarness.cs" \
    "$repo/Assets/RealityLog/Scripts/Runtime/Rig/AppLogFile.cs"
"$mono" "$out/app-log-harness.exe"

# DevicePresence: worn, focus and input added to the device report.
"$mono" "$csc" -nologo -nullable:enable -langversion:9 -r:"$newtonsoft" -r:"$facades/netstandard.dll" \
    -out:"$out/device-presence-harness.exe" "$here/DevicePresenceHarness.cs" \
    "$repo/Assets/RealityLog/Scripts/Runtime/Rig/DevicePresence.cs"
"$mono" "$out/device-presence-harness.exe"
