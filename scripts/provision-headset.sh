#!/usr/bin/env bash
# Prepare a Quest for OpenQuestCapture after a fresh install, before Scan QR / Set up.
#
# Android hides ro.serialno from apps, so the headset cannot read its own serial; the
# Factory needs it to commission a headset pod. This writes headset_serial (read over
# adb) into the app's pod_config.json with the seal's operator_id, keeps Local only off, grants the permissions the
# app needs, relaunches it and turns on Wi-Fi ADB.
#
# Usage: scripts/provision-headset.sh [adb-serial]   (connect the headset by USB first)
set -euo pipefail

pkg=com.samusynth.OpenQuestCapture
config=/sdcard/Android/data/$pkg/files/pod_config.json

if [ $# -ge 1 ]; then
  device=$1
else
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

# The app creates files/ on first launch; launch it once if the directory is missing.
if ! q shell "[ -d /sdcard/Android/data/$pkg/files ]"; then
  q shell monkey -p "$pkg" -c android.intent.category.LAUNCHER 1 >/dev/null 2>&1
  for _ in $(seq 1 15); do
    q shell "[ -d /sdcard/Android/data/$pkg/files ]" && break
    sleep 1
  done
fi

# Merge into the existing config (if any): set headset_serial, keep Local only off unless
# it is already set. Written in place with a shell redirect so an existing file keeps the
# app as its owner.
current=$(q shell "cat $config 2>/dev/null" | tr -d '\r' || true)
merged=$(CURRENT="$current" SERIAL="$serial" python3 - <<'EOF'
import json, os
raw = os.environ["CURRENT"].strip()
try:
    config = json.loads(raw) if raw else {}
except json.JSONDecodeError:
    config = {}
if not isinstance(config, dict):
    config = {}
config["headset_serial"] = os.environ["SERIAL"]
config.setdefault("local_only", False)
# The seal's operator_id: OQC reads it from here, not from the signed-in collector (yet).
config.setdefault("operator_id", "bench-operator")
print(json.dumps(config, indent=2))
EOF
)
printf '%s\n' "$merged" | q shell "cat > $config"
echo "pod_config.json:"
q shell "cat $config"

for perm in android.permission.CAMERA android.permission.BLUETOOTH_SCAN android.permission.BLUETOOTH_CONNECT \
  horizonos.permission.HEADSET_CAMERA horizonos.permission.USB_CAMERA; do
  q shell pm grant "$pkg" "$perm" 2>/dev/null || echo "note: could not grant $perm"
done

q shell am force-stop "$pkg"
q shell monkey -p "$pkg" -c android.intent.category.LAUNCHER 1 >/dev/null 2>&1
echo "relaunched $pkg"

ip=$(q shell ip -4 addr show wlan0 | grep -oE 'inet [0-9.]+' | cut -d' ' -f2 || true)
q tcpip 5555 >/dev/null
if [ -n "$ip" ]; then
  sleep 3
  adb connect "$ip:5555" || true
  echo "Wi-Fi ADB: $ip:5555 (you can unplug USB)"
else
  echo "Wi-Fi ADB enabled, but the headset has no wlan0 address"
fi
echo "Done. Now open the menu → Pod → Scan QR (or Set up manually)."
