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
include="${SX_RIG_INCLUDE:?set SX_RIG_INCLUDE to the directory holding sx_rig.h}"
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

# The operator's words: every harness below that draws a screen reads the shipped table.
export SX_OPERATOR_NOTICES="$repo/Assets/RealityLog/Resources/sx_operator_notices.json"
notices="$repo/Assets/RealityLog/Scripts/Runtime/Rig/OperatorNotices.cs"

# RigStatusJson against Newtonsoft.Json from the Unity project's package cache.
newtonsoft="$(ls "$repo"/Library/PackageCache/com.unity.nuget.newtonsoft-json@*/Runtime/Newtonsoft.Json.dll | head -n 1)"
facades="$editor/Data/MonoBleedingEdge/lib/mono/4.5/Facades"
"$mono" "$csc" -nologo -nullable:enable -langversion:9 -r:"$newtonsoft" -r:"$facades/netstandard.dll" \
    -out:"$out/status-harness.exe" "$here/RigStatusJsonHarness.cs" "$repo/Assets/RealityLog/Scripts/Runtime/Rig/RigStatusJson.cs"
cp "$newtonsoft" "$out/"
"$mono" "$out/status-harness.exe"

# Status parsing is one bounded worker; UI/phone readers never parse on the capture thread.
"$mono" "$csc" -nologo -nullable:enable -langversion:9 -r:"$newtonsoft" -r:"$facades/netstandard.dll" \
    -out:"$out/status-snapshot-harness.exe" "$here/StatusSnapshotHarness.cs" "$repo/Assets/RealityLog/Scripts/Runtime/Rig/RigStatusJson.cs"
"$mono" "$out/status-snapshot-harness.exe"

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

# GripperSwitch: the right gripper's start/stop holds, once each, from libsx_rig's counters.
"$mono" "$csc" -nologo -nullable:enable -langversion:9 -r:"$newtonsoft" -r:"$facades/netstandard.dll" \
    -out:"$out/gripper-switch-harness.exe" "$here/GripperSwitchHarness.cs" "$notices" \
    "$repo/Assets/RealityLog/Scripts/Runtime/Rig/GripperSwitch.cs"
"$mono" "$out/gripper-switch-harness.exe"

# EpisodeLength: the take's 15 min ceiling warns once a minute before and stops once at it,
# and the warning cue is its own sound (RigCues against Unity's managed engine modules).
unity_managed="$editor/Data/Managed/UnityEngine"
"$mono" "$csc" -nologo -nullable:enable -langversion:9 -r:"$newtonsoft" -r:"$facades/netstandard.dll" \
    -r:"$unity_managed/UnityEngine.CoreModule.dll" -r:"$unity_managed/UnityEngine.AudioModule.dll" \
    -out:"$out/episode-length-harness.exe" "$here/EpisodeLengthHarness.cs" "$notices" "$repo/Assets/RealityLog/Scripts/Runtime/Rig/EpisodeLength.cs" \
    "$repo/Assets/RealityLog/Scripts/Runtime/Rig/RigCues.cs" "$repo/Assets/RealityLog/Scripts/Runtime/Common/Constants.cs"
MONO_PATH="$unity_managed" "$mono" "$out/episode-length-harness.exe"

# MenuButtons: ☰ starts or stops, a busy press is ignored, a dropped one buzzes; Y never records.
"$mono" "$csc" -nologo -nullable:enable -langversion:9 \
    -out:"$out/menu-button-harness.exe" "$here/MenuButtonHarness.cs" \
    "$repo/Assets/RealityLog/Scripts/Runtime/Rig/MenuButtons.cs"
"$mono" "$out/menu-button-harness.exe"

# CaptureFailureWatch: a head camera failing mid-episode is reported once, as itself.
"$mono" "$csc" -nologo -nullable:enable -langversion:9 \
    -out:"$out/capture-failure-harness.exe" "$here/CaptureFailureHarness.cs" \
    "$repo/Assets/RealityLog/Scripts/Runtime/Camera/CaptureFailureWatch.cs"
"$mono" "$out/capture-failure-harness.exe"

# HeadVideoGapWatch: a head camera stalling mid-episode is reported once, as itself (OQC#44).
"$mono" "$csc" -nologo -nullable:enable -langversion:9 \
    -out:"$out/head-video-gap-harness.exe" "$here/HeadVideoGapHarness.cs" \
    "$repo/Assets/RealityLog/Scripts/Runtime/Camera/HeadVideoGapWatch.cs" \
    "$repo/Assets/RealityLog/Scripts/Runtime/Camera/CaptureFailureWatch.cs"
"$mono" "$out/head-video-gap-harness.exe"

# PodStatusBoard: the recordings menu's Pod status block and the last start's refusal.
"$mono" "$csc" -nologo -nullable:enable -langversion:9 -r:"$newtonsoft" -r:"$facades/netstandard.dll" \
    -out:"$out/pod-status-harness.exe" "$here/PodStatusBoardHarness.cs" "$notices" \
    "$repo/Assets/RealityLog/Scripts/Runtime/Rig/PodStatusBoard.cs" "$repo/Assets/RealityLog/Scripts/Runtime/Rig/RigStatusJson.cs" \
    "$repo/Assets/RealityLog/Scripts/Runtime/Rig/PodScreen.cs" "$repo/Assets/RealityLog/Scripts/Runtime/Rig/GripperSwitch.cs" "$repo/Assets/RealityLog/Scripts/Runtime/Rig/EpisodeLength.cs"
"$mono" "$out/pod-status-harness.exe"

# PodScreenBoard: the Pod card's status screen (blocker, devices, collection, episodes).
"$mono" "$csc" -nologo -nullable:enable -langversion:9 -r:"$newtonsoft" -r:"$facades/netstandard.dll" \
    -out:"$out/pod-screen-harness.exe" "$here/PodScreenHarness.cs" "$notices" \
    "$repo/Assets/RealityLog/Scripts/Runtime/Rig/PodScreen.cs" "$repo/Assets/RealityLog/Scripts/Runtime/Rig/GripperSwitch.cs" "$repo/Assets/RealityLog/Scripts/Runtime/Rig/EpisodeLength.cs" \
    "$repo/Assets/RealityLog/Scripts/Runtime/Rig/PodStatusBoard.cs" "$repo/Assets/RealityLog/Scripts/Runtime/Rig/RigStatusJson.cs"
"$mono" "$out/pod-screen-harness.exe"

# The operator's words: the shipped table is libsx_rig's (SX_RIG_INCLUDE), every code renders, no raw text.
"$mono" "$csc" -nologo -nullable:enable -langversion:9 -r:"$newtonsoft" -r:"$facades/netstandard.dll" \
    -out:"$out/notices-harness.exe" "$here/OperatorNoticesHarness.cs" "$notices" \
    "$repo/Assets/RealityLog/Scripts/Runtime/Rig/PodScreen.cs" "$repo/Assets/RealityLog/Scripts/Runtime/Rig/GripperSwitch.cs" "$repo/Assets/RealityLog/Scripts/Runtime/Rig/EpisodeLength.cs" \
    "$repo/Assets/RealityLog/Scripts/Runtime/Rig/PodStatusBoard.cs" "$repo/Assets/RealityLog/Scripts/Runtime/Rig/RigStatusJson.cs"
"$mono" "$out/notices-harness.exe" "$SX_OPERATOR_NOTICES" "$include/sx_operator_notices.json"

# The phone companion: HeadsetStatus from the status screen (the collector or unattributed), the phone link report, the Phone section.
"$mono" "$csc" -nologo -nullable:enable -langversion:9 -r:"$newtonsoft" -r:"$facades/netstandard.dll" \
    -out:"$out/companion-harness.exe" "$here/CompanionHarness.cs" "$notices" \
    "$repo/Assets/RealityLog/Scripts/Runtime/Rig/Companion.cs" \
    "$repo/Assets/RealityLog/Scripts/Runtime/Rig/PodScreen.cs" "$repo/Assets/RealityLog/Scripts/Runtime/Rig/GripperSwitch.cs" "$repo/Assets/RealityLog/Scripts/Runtime/Rig/EpisodeLength.cs" \
    "$repo/Assets/RealityLog/Scripts/Runtime/Rig/PodStatusBoard.cs" "$repo/Assets/RealityLog/Scripts/Runtime/Rig/RigStatusJson.cs"
"$mono" "$out/companion-harness.exe"

# RecordingListRefreshGate: the recordings list stays off the main thread's back while recording.
"$mono" "$csc" -nologo -nullable:enable -langversion:9 \
    -out:"$out/list-refresh-harness.exe" "$here/RecordingListRefreshGateHarness.cs" \
    "$repo/Assets/RealityLog/Scripts/Runtime/Common/RecordingListRefreshGate.cs"
"$mono" "$out/list-refresh-harness.exe"

# RigUsbRefresh: slow Android USB reads never hold the sampling thread or close an in-flight fd.
bash "$here/test_usb_refresh.sh"

# HeadTrackingMonitor: reject stale or incompletely tracked sources before capture starts.
bash "$here/test_tracking_readiness.sh"

# AppLogFile: the app's log lines the rig ships, rolled and bounded.
"$mono" "$csc" -nologo -nullable:enable -langversion:9 -r:"$facades/netstandard.dll" \
    -out:"$out/app-log-harness.exe" "$here/AppLogHarness.cs" \
    "$repo/Assets/RealityLog/Scripts/Runtime/Rig/AppLogFile.cs"
"$mono" "$out/app-log-harness.exe"

# DevicePresence: worn, focus and input added to the device report.
"$mono" "$csc" -nologo -nullable:enable -langversion:9 -r:"$newtonsoft" -r:"$facades/netstandard.dll" \
    -out:"$out/device-presence-harness.exe" "$here/DevicePresenceHarness.cs" "$notices" \
    "$repo/Assets/RealityLog/Scripts/Runtime/Rig/DevicePresence.cs" \
    "$repo/Assets/RealityLog/Scripts/Runtime/Rig/Companion.cs" \
    "$repo/Assets/RealityLog/Scripts/Runtime/Rig/PodScreen.cs" "$repo/Assets/RealityLog/Scripts/Runtime/Rig/GripperSwitch.cs" "$repo/Assets/RealityLog/Scripts/Runtime/Rig/EpisodeLength.cs" \
    "$repo/Assets/RealityLog/Scripts/Runtime/Rig/PodStatusBoard.cs" "$repo/Assets/RealityLog/Scripts/Runtime/Rig/RigStatusJson.cs"
"$mono" "$out/device-presence-harness.exe"

# SelfRelaunch: the app relaunches itself for a frozen head pose only when nothing records or seals.
"$mono" "$csc" -nologo -nullable:enable -langversion:9 -r:"$newtonsoft" -r:"$facades/netstandard.dll" \
    -out:"$out/self-relaunch-harness.exe" "$here/SelfRelaunchHarness.cs" \
    "$repo/Assets/RealityLog/Scripts/Runtime/Rig/SelfRelaunch.cs"
"$mono" "$out/self-relaunch-harness.exe"

# BreakAlarm: any running headset with no take for 45 s since the last one ended says "Pod N, start now", louder from 60 s, back to back from 90 s, until a take records.
"$mono" "$csc" -nologo -nullable:enable -langversion:9 \
    -out:"$out/break-alarm-harness.exe" "$here/BreakAlarmHarness.cs" \
    "$repo/Assets/RealityLog/Scripts/Runtime/Rig/BreakAlarmPolicy.cs" \
    "$repo/Assets/RealityLog/Scripts/Runtime/Rig/BreakAlarmVoice.cs"
"$mono" "$out/break-alarm-harness.exe" "$repo/Assets/RealityLog/Resources/sx_break_alarm_voice.bytes"

# OsControllerReport: the controllers' charge from OVRRemoteService's dumpsys text, and its cache.
"$mono" "$csc" -nologo -nullable:enable -langversion:9 -r:"$facades/netstandard.dll" \
    -out:"$out/os-controller-harness.exe" "$here/OsControllerReportHarness.cs" \
    "$repo/Assets/RealityLog/Scripts/Runtime/Rig/OsControllerReport.cs"
"$mono" "$out/os-controller-harness.exe"

# Explicit collector reviews: no invented outcomes/answers and stable replay after uncertainty.
SX_NEWTONSOFT_DLL="$newtonsoft" bash "$here/test_pod_review.sh"
