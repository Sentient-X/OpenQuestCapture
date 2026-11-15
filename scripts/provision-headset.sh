#!/usr/bin/env bash
# Prepare a Quest for OpenQuestCapture. Run it after EVERY install or update, not only a fresh
# install: an update can add a permission only adb can grant (DUMP, v60, for the controllers'
# charge), and a headset that only ever took updates never got it. Re-running is safe.
#
# Android hides ro.serialno from apps, so the headset cannot read its own serial; the
# Factory needs it to commission a headset pod. This writes headset_serial (read over
# adb) into the app's pod_config.json, keeps Local only off, grants the permissions the
# app needs and verifies each grant, relaunches the app only when pod_config.json changed
# and the app is not recording or sealing, and turns on Wi-Fi ADB.
#
# Usage: scripts/provision-headset.sh [--keep-awake] [--no-wifi] [adb-serial]
#   (connect the headset by USB first)
#   --keep-awake  keep the screen and headset awake off-head (stay-on, no screen timeout, the
#                 proximity sensor held closed). Resets on reboot: re-run after a reboot.
#   --no-wifi     leave adb on USB (skip `adb tcpip 5555`).
#
# Exit status: 0 when everything held; 1 when the headset was not usable or a permission is
# not granted after `pm grant` (the missing ones are listed).
set -euo pipefail

pkg=com.samusynth.OpenQuestCapture
files=/sdcard/Android/data/$pkg/files
config=$files/pod_config.json
# The rig's per-episode directories (RigCoordinator.StagingDirectoryName).
staging=$files/rig_staging

keep_awake=0
wifi=1
device=
while [ $# -gt 0 ]; do
  case "$1" in
    --keep-awake) keep_awake=1 ;;
    --no-wifi) wifi=0 ;;
    -h|--help) sed -n '2,20p' "$0"; exit 0 ;;
    -*) echo "unknown option: $1" >&2; exit 2 ;;
    *) device=$1 ;;
  esac
  shift
done

if [ -z "$device" ]; then
  mapfile -t devices < <(adb devices | awk 'NR > 1 && $2 == "device" { print $1 }')
  # One entry per headset: the same Quest on USB and on Wi-Fi ADB has one ro.serialno.
  quests=()
  seen=" "
  for d in "${devices[@]}"; do
    [ "$(adb -s "$d" shell getprop ro.product.manufacturer | tr -d '\r')" = "Oculus" ] || continue
    s=$(adb -s "$d" shell getprop ro.serialno | tr -d '\r')
    case "$seen" in *" $s "*) continue ;; esac
    seen="$seen$s "
    quests+=("$d")
  done
  if [ ${#quests[@]} -ne 1 ]; then
    echo "found ${#quests[@]} Quest(s) on adb; pass the adb serial: $0 <adb-serial>" >&2
    exit 1
  fi
  device=${quests[0]}
fi
q() { adb -s "$device" "$@"; }

if ! q shell pm path "$pkg" >/dev/null 2>&1; then
  echo "$pkg is not installed on $device; install the APK first" >&2
  exit 1
fi

serial=$(q shell getprop ro.serialno | tr -d '\r')
if [ -z "$serial" ]; then
  echo "could not read ro.serialno over adb" >&2
  exit 1
fi
echo "headset serial: $serial"

launch() { q shell monkey -p "$pkg" -c android.intent.category.LAUNCHER 1 >/dev/null 2>&1; }

# The app creates files/ on first launch; launch it once if the directory is missing.
if ! q shell "[ -d $files ]"; then
  launch
  for _ in $(seq 1 15); do
    q shell "[ -d $files ]" && break
    sleep 1
  done
fi

# Merge into the existing config (if any): set headset_serial, keep Local only off unless
# it is already set. The first line python prints says whether that changed anything (by
# value, so key order and whitespace do not count); an unchanged file is not rewritten.
current=$(q shell "cat $config 2>/dev/null" | tr -d '\r' || true)
merged=$(CURRENT="$current" SERIAL="$serial" python3 - <<'EOF'
import json, os
raw = os.environ["CURRENT"].strip()
try:
    before = json.loads(raw) if raw else None
except json.JSONDecodeError:
    before = None
config = dict(before) if isinstance(before, dict) else {}
config["headset_serial"] = os.environ["SERIAL"]
config.setdefault("local_only", False)
# The seal's operator_id is the signed-in collector, never this file: drop a stale one.
config.pop("operator_id", None)
print("changed" if config != before else "unchanged")
print(json.dumps(config, indent=2))
EOF
)
config_state=${merged%%$'\n'*}
merged=${merged#*$'\n'}
if [ "$config_state" = changed ]; then
  # Written in place with a shell redirect so an existing file keeps the app as its owner.
  printf '%s\n' "$merged" | q shell "cat > $config"
  echo "pod_config.json updated:"
else
  echo "pod_config.json already current:"
fi
printf '%s\n' "$merged"

# pm grant takes effect in the running app; no restart is needed for any of these.
# DUMP: the controllers' charge is readable only in OVRRemoteService's dumpsys report, which the
# service writes only for a DUMP holder (every app API answers nothing on Horizon OS 207, and the
# OS's own READ_CONTROLLER_STATUS is signature-only). DUMP is a development permission: the
# manifest declares it and only this grant gives it; without it the Pod card reads the
# controllers' charge as not available.
perms=(android.permission.CAMERA android.permission.BLUETOOTH_SCAN android.permission.BLUETOOTH_CONNECT
  horizonos.permission.HEADSET_CAMERA horizonos.permission.USB_CAMERA android.permission.DUMP)
for perm in "${perms[@]}"; do
  q shell pm grant "$pkg" "$perm" >/dev/null 2>&1 || true
done
granted=$(q shell dumpsys package "$pkg" | tr -d '\r')
missing=()
for perm in "${perms[@]}"; do
  grep -qF "$perm: granted=true" <<<"$granted" || missing+=("$perm")
done
if [ ${#missing[@]} -eq 0 ]; then
  echo "permissions granted: ${#perms[@]}/${#perms[@]}"
fi

# What the app is doing now, from its own log (this process only): the last recording marker
# RecordingManager and HttpServerController write. Prints recording, idle or unknown.
recording_state() {
  local pid=$1 lines
  lines=$(q shell logcat -d --pid="$pid" 2>/dev/null | tr -d '\r' \
    | grep -E 'RealityLog\] (RecordingManager: (Starting recording session|Stopping recording session|Recording stopped)|HEARTBEAT: .*Recording: (True|False))' \
    || true)
  case "$(tail -n 1 <<<"$lines")" in
    *"Recording: False"* | *"Recording stopped"*) echo idle ;;
    *"Recording: True"* | *"Starting recording session"* | *"Stopping recording session"*) echo recording ;;
    *) echo unknown ;;
  esac
}

# Episodes the rig has stopped but not settled: a stop.json and no seal.json saying sealed or
# refused (seal.rs requeued() puts exactly these back on the queue at launch). A force-stop
# would drop that seal mid-way and redo it from the start.
unsealed() {
  q shell "for d in $staging/*/; do [ -f \"\$d\"stop.json ] || continue; [ -f \"\$d\"purge.json ] && continue; \
grep -qE '\"state\" *: *\"(sealed|refused)\"' \"\$d\"seal.json 2>/dev/null || echo \"\$d\"; done" 2>/dev/null \
    | tr -d '\r' || true
}

pid=$(q shell pidof "$pkg" 2>/dev/null | tr -d '\r' | awk '{ print $1 }' || true)
if [ -z "$pid" ]; then
  launch
  echo "launched $pkg (it was not running)"
elif [ "$config_state" = unchanged ]; then
  echo "not relaunched: pod_config.json is unchanged and grants need no restart"
else
  state=$(recording_state "$pid")
  pending=$(unsealed)
  if [ "$state" != idle ]; then
    echo "NOT relaunched: the app's recording state is $state (from logcat); the new pod_config.json" \
      "is read on its next launch: re-run this script once it is idle" >&2
  elif [ -n "$pending" ]; then
    echo "NOT relaunched: the rig has episodes still sealing; the new pod_config.json is read on its" \
      "next launch: re-run once they settle:" >&2
    sed 's/^/  /' <<<"$pending" >&2
  else
    q shell am force-stop "$pkg"
    launch
    echo "relaunched $pkg (pod_config.json changed)"
  fi
fi

if [ "$keep_awake" = 1 ]; then
  q shell svc power stayon true
  q shell settings put system screen_off_timeout 2147483647
  q shell am broadcast -a com.oculus.vrpowermanager.automation_disable >/dev/null
  q shell am broadcast -a com.oculus.vrpowermanager.prox_close >/dev/null
  echo "keep-awake on (stay-on, no screen timeout, proximity held closed); a reboot resets it"
fi

if [ "$wifi" = 1 ]; then
  ip=$(q shell ip -4 addr show wlan0 | grep -oE 'inet [0-9.]+' | cut -d' ' -f2 || true)
  q tcpip 5555 >/dev/null
  if [ -n "$ip" ]; then
    sleep 3
    adb connect "$ip:5555" || true
    echo "Wi-Fi ADB: $ip:5555 (you can unplug USB)"
  else
    echo "Wi-Fi ADB enabled, but the headset has no wlan0 address"
  fi
fi

if [ ${#missing[@]} -gt 0 ]; then
  echo "FAILED: not granted after pm grant: ${missing[*]}" >&2
  echo "  (is this APK's manifest declaring them? check: adb shell dumpsys package $pkg)" >&2
  exit 1
fi
echo "Done. If this headset is not yet a pod: menu → Pod → Scan QR (or Set up manually)."
