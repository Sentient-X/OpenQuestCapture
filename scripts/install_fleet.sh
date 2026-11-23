#!/usr/bin/env bash
# Put one OpenQuestCapture APK on many Quest headsets in one go. Until now every install was one
# headset by hand (adb install -r, then provision-headset.sh); the Factory lists 18 active headset
# pods on five builds. Pods provisioned without --no-wifi keep `adb tcpip 5555` on until reboot, so
# they are reachable over the lab Wi-Fi without a cable.
#
# Usage: scripts/install_fleet.sh [--jobs N] [--stop] [--keep-awake] [--dry-run] [--discover CIDR] <apk> [<target>...]
#   target        an adb serial, or ip[:5555] (connected first). With no targets and no --discover,
#                 targets are read from stdin, one per line (# comments allowed).
#   --discover C  try `adb connect ip:5555` across the CIDR (e.g. 192.168.29.0/24) and use every
#                 headset that answers as a target
#   --jobs N      headsets handled at once (default 4)
#   --stop        force-stop a running app before installing. Without it a running app is skipped:
#                 it may be recording. An app this script stopped is launched again after the
#                 install (the pods seal and upload only while the app runs; on 2026-11-24 the
#                 whole floor was draining its backlog at night), and the result says so.
#   --keep-awake  after the install, run provision-headset.sh --keep-awake --no-wifi on the target
#   --dry-run     print the plan per target and install nothing
# A running app is never killed without --stop and a package is never uninstalled: a signer
# mismatch (INSTALL_FAILED_UPDATE_INCOMPATIBLE) is reported for a person to uninstall by hand.
# Logs: ~/p3/tmp/install-fleet/<timestamp>/<target>.log. Exit 1 when any target failed or was
# unreachable.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
pkg=com.samusynth.OpenQuestCapture
jobs=4 stop=0 keep_awake=0 dry_run=0 discover=
targets=()
apk=
while [ $# -gt 0 ]; do
  case "$1" in
    --jobs) jobs=${2:?--jobs needs a number}; shift ;;
    --stop) stop=1 ;;
    --keep-awake) keep_awake=1 ;;
    --dry-run) dry_run=1 ;;
    --discover) discover=${2:?--discover needs a CIDR, e.g. 192.168.29.0/24}; shift ;;
    -h|--help) sed -n '2,21p' "$0"; exit 0 ;;
    -*) echo "unknown option: $1" >&2; exit 2 ;;
    *) if [ -z "$apk" ]; then apk=$1; else targets+=("$1"); fi ;;
  esac
  shift
done
[ -n "$apk" ] || { sed -n '7,7p' "$0" >&2; exit 2; }
[ -f "$apk" ] || { echo "no such APK: $apk" >&2; exit 2; }

# The APK's version, read once from its badging (archive_release.sh reads it the same way).
aapt2=$(ls -d "${ANDROID_HOME:-$HOME/Android/Sdk}"/build-tools/*/aapt2 2>/dev/null | sort -V | tail -n 1 || true)
[ -n "$aapt2" ] || aapt2=$(command -v aapt2 || command -v aapt || true)
if [ -z "$aapt2" ]; then
  echo "no aapt2 or aapt: set ANDROID_HOME to an SDK with build-tools (or install ~/Android/Sdk/build-tools)" >&2
  exit 2
fi
badging=$("$aapt2" dump badging "$apk")
badging=${badging%%$'\n'*}
want=$(sed -n "s/.*versionCode='\([^']*\)'.*/\1/p" <<<"$badging")
want_name=$(sed -n "s/.*versionName='\([^']*\)'.*/\1/p" <<<"$badging")
[ -n "$want" ] || { echo "cannot read versionCode from $apk" >&2; exit 2; }
grep -q "package: name='$pkg'" <<<"$badging" || { echo "$apk is not $pkg: $badging" >&2; exit 2; }

runs=${INSTALL_FLEET_RUNS:-$HOME/p3/tmp/install-fleet}
mkdir -p "$runs"
run=$(mktemp -d "$runs/$(date -u +%Y%m%dT%H%M%SZ).XXXX")
echo "APK: $apk (versionName $want_name, versionCode $want); run directory: $run"

# ip or ip:port is a network target to connect first; anything else is a USB serial.
normalize() { if [[ $1 =~ ^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$ ]]; then echo "$1:5555"; else echo "$1"; fi; }
answers() { case "$1" in "connected to"*|"already connected"*) return 0 ;; *) return 1 ;; esac; }

if [ -n "$discover" ]; then
  [[ $discover =~ ^([0-9]+)\.([0-9]+)\.([0-9]+)\.([0-9]+)/([0-9]+)$ ]] || { echo "--discover wants a CIDR like 192.168.29.0/24" >&2; exit 2; }
  prefix=${BASH_REMATCH[5]}
  [ "$prefix" -ge 16 ] && [ "$prefix" -le 30 ] || { echo "--discover: prefix /$prefix is outside /16../30" >&2; exit 2; }
  net=$(( (BASH_REMATCH[1] << 24 | BASH_REMATCH[2] << 16 | BASH_REMATCH[3] << 8 | BASH_REMATCH[4]) & ~((1 << (32 - prefix)) - 1) ))
  hosts=$(( (1 << (32 - prefix)) - 2 ))
  echo "discovering: $discover ($hosts addresses, adb connect ip:5555, 3 s each, 32 at a time)"
  : >"$run/discovered"
  live=0
  for ((i = 1; i <= hosts; i++)); do
    n=$((net + i))
    ip="$((n >> 24 & 255)).$((n >> 16 & 255)).$((n >> 8 & 255)).$((n & 255))"
    ( out=$(timeout 3 adb connect "$ip:5555" 2>&1 || true); answers "$out" && echo "$ip:5555" >>"$run/discovered"; ) &
    live=$((live + 1))
    if [ "$live" -ge 32 ]; then wait -n || true; live=$((live - 1)); fi
  done
  wait
  mapfile -t found < <(sort -t. -k4,4n "$run/discovered")
  for t in "${found[@]}"; do
    serial=$(timeout 10 adb -s "$t" shell getprop ro.serialno 2>/dev/null | tr -d '\r' || true)
    echo "  found $t  serial ${serial:-?}"
  done
  targets+=("${found[@]}")
elif [ ${#targets[@]} -eq 0 ]; then
  mapfile -t targets < <(sed 's/#.*//; s/^[[:space:]]*//; s/[[:space:]]*$//' | grep -v '^$')
fi
[ ${#targets[@]} -gt 0 ] || { echo "no targets: pass serials or ip[:5555], a list on stdin, or --discover CIDR" >&2; exit 2; }

# One target, in a background job. Everything adb says goes to the target's log; `say` also prints
# one line to the terminal. The outcome is one tab-separated line in <target>.result.
one() {
  local t=$1 log="$run/$1.log" serial=? before=none after=none pid out
  say() { printf '%s\n' "$*" >>"$log"; printf '[%s] %s\n' "$t" "$*"; }
  finish() { printf '%s\t%s\t%s\t%s\t%s\n' "$t" "$serial" "$before" "$after" "$1" >"$run/$t.result"; say "$1"; }
  a() { adb -s "$t" "$@"; }
  : >"$log"
  if [[ $t == *:* ]]; then
    out=$(timeout 10 adb connect "$t" 2>&1 || true); echo "$out" >>"$log"
    answers "$out" || { finish unreachable; return; }
  fi
  timeout 20 adb -s "$t" wait-for-device >>"$log" 2>&1 || { finish unreachable; return; }
  serial=$(a shell getprop ro.serialno 2>>"$log" | tr -d '\r' || true)
  [ -n "$serial" ] || { finish unreachable; return; }
  before=$(a shell dumpsys package "$pkg" 2>>"$log" | sed -n 's/.*versionCode=\([0-9]*\).*/\1/p' | head -n 1)
  before=${before:-none}
  after=$before
  if [ "$before" = "$want" ]; then finish "up to date"; return; fi
  pid=$(a shell pidof "$pkg" 2>/dev/null | tr -d '\r' || true)
  if [ "$dry_run" = 1 ]; then
    if [ -n "$pid" ] && [ "$stop" = 0 ]; then finish "running, would skip (pass --stop)"
    elif [ -n "$pid" ]; then finish "would stop, install $before -> $want, relaunch"
    else finish "would install $before -> $want"; fi
    return
  fi
  if [ -n "$pid" ]; then
    if [ "$stop" = 0 ]; then finish "running, skipped (pass --stop)"; return; fi
    a shell am force-stop "$pkg" >>"$log" 2>&1
    say "force-stopped $pkg (pid $pid)"
  fi
  say "installing $before -> $want"
  if ! out=$(a install -r "$apk" 2>&1) || ! grep -q Success <<<"$out"; then
    echo "$out" >>"$log"
    case "$out" in
      *INSTALL_FAILED_UPDATE_INCOMPATIBLE*) finish "failed: signer differs, needs uninstall by a person" ;;
      *) finish "failed: install: $(tail -n 1 <<<"$out")" ;;
    esac
    return
  fi
  after=$(a shell dumpsys package "$pkg" 2>>"$log" | sed -n 's/.*versionCode=\([0-9]*\).*/\1/p' | head -n 1)
  after=${after:-none}
  [ "$after" = "$want" ] || { finish "failed: versionCode $after after install, wanted $want"; return; }
  # DUMP is a development permission only adb can grant (the controllers' charge).
  a shell pm grant "$pkg" android.permission.DUMP >>"$log" 2>&1 || say "warning: pm grant DUMP refused (see log)"
  if [ "$keep_awake" = 1 ]; then
    "$root/scripts/provision-headset.sh" --keep-awake --no-wifi "$t" >>"$log" 2>&1 \
      || { finish "failed: provision-headset.sh exit $? (see log)"; return; }
  fi
  if [ -z "$pid" ]; then finish updated; return; fi
  # The app was running before: bring it back, so the pod goes on sealing and uploading.
  a shell monkey -p "$pkg" -c android.intent.category.LAUNCHER 1 >>"$log" 2>&1 || true
  for _ in 1 2 3 4 5 6 7 8 9 10; do
    sleep 1
    newpid=$(a shell pidof "$pkg" 2>/dev/null | tr -d '\r' || true)
    [ -n "$newpid" ] && break
  done
  if [ -n "${newpid:-}" ]; then finish "updated, relaunched (pid $newpid)"; else finish "updated, but the app did not relaunch within 10 s: launch it by hand"; fi
}

live=0
for t in "${targets[@]}"; do
  one "$(normalize "$t")" &
  live=$((live + 1))
  if [ "$live" -ge "$jobs" ]; then wait -n || true; live=$((live - 1)); fi
done
wait

echo
printf '%-22s %-16s %-7s %-7s %s\n' TARGET SERIAL BEFORE AFTER RESULT
bad=0
for t in "${targets[@]}"; do
  t=$(normalize "$t")
  if [ -f "$run/$t.result" ]; then
    IFS=$'\t' read -r _ serial before after result <"$run/$t.result"
  else
    serial=? before=? after=? result="failed: no result (see log)"
  fi
  printf '%-22s %-16s %-7s %-7s %s\n' "$t" "$serial" "$before" "$after" "$result"
  case "$result" in unreachable|failed:*) bad=1 ;; esac
done
echo "logs: $run"
exit "$bad"
