#!/usr/bin/env bash
# Runs scripts/provision-headset.sh against a fake `adb` on PATH (no device is touched) and
# checks: unchanged config -> no relaunch; recording or sealing -> no force-stop; changed and
# idle -> relaunch; a permission pm grant cannot give -> non-zero naming it; --keep-awake and
# --no-wifi. Usage: scripts/tests/test-provision-headset.sh
set -euo pipefail

here=$(cd "$(dirname "$0")" && pwd)
script=$here/../provision-headset.sh
scratch_root=${TMPDIR:-$HOME/p3/tmp}
mkdir -p "$scratch_root"
work=$(mktemp -d "$scratch_root/provision-test.XXXXXX")
trap 'rm -rf "$work"' EXIT
pkg=com.samusynth.OpenQuestCapture

mkdir -p "$work/bin"
cat >"$work/bin/adb" <<'SHIM'
#!/usr/bin/env bash
# Fake adb: state under $FAKE (sdcard/, logcat, ungrantable, running), every call in $FAKE/calls.
set -euo pipefail
echo "$*" >>"$FAKE/calls"
[ "${1:-}" = -s ] && shift 2
cmd=${1:-}; shift || true
case "$cmd" in
  devices) printf 'List of devices attached\nFAKE1\tdevice\n'; exit 0 ;;
  tcpip|connect) exit 0 ;;
  shell) ;;
  *) echo "fake adb: unhandled $cmd" >&2; exit 1 ;;
esac
line="$*"
pkg=com.samusynth.OpenQuestCapture
case "$line" in
  "getprop ro.serialno") echo 2G0YC5ZG8K0123 ;;
  "getprop ro.product.manufacturer") echo Oculus ;;
  "pm path $pkg") echo "package:/data/app/$pkg/base.apk" ;;
  "pm grant $pkg "*)
    perm=${line##* }
    if grep -qxF "$perm" "$FAKE/ungrantable" 2>/dev/null; then
      echo "Exception: not a changeable permission type" >&2; exit 255
    fi
    echo "$perm" >>"$FAKE/granted" ;;
  "dumpsys package $pkg")
    echo "Packages:"; echo "  Package [$pkg]"
    sort -u "$FAKE/granted" 2>/dev/null | sed 's/$/: granted=true/; s/^/      /' || true ;;
  "pidof $pkg") [ -f "$FAKE/running" ] && echo 4242 || exit 1 ;;
  "logcat -d --pid=4242") cat "$FAKE/logcat" 2>/dev/null || true ;;
  "am force-stop $pkg") rm -f "$FAKE/running" ;;
  "monkey -p $pkg "*) touch "$FAKE/running"; mkdir -p "$FAKE/sdcard/Android/data/$pkg/files" ;;
  "ip -4 addr show wlan0") echo "    inet 192.168.1.50/24 brd 192.168.1.255 scope global wlan0" ;;
  "svc "*|"settings "*|"am broadcast "*) ;;
  *) # a file operation: run it against $FAKE/sdcard
    bash -c "${line//\/sdcard/$FAKE/sdcard}" ;;
esac
SHIM
chmod +x "$work/bin/adb"
# The script sleeps 3 s after tcpip; the fake makes that instant.
printf '#!/bin/sh\nexit 0\n' >"$work/bin/sleep"; chmod +x "$work/bin/sleep"

fail=0
n=0
files_of() { echo "$1/sdcard/Android/data/$pkg/files"; }
# fresh <name>: a headset with the app running and an existing config.
fresh() {
  FAKE=$work/$1; export FAKE
  mkdir -p "$(files_of "$FAKE")"
  touch "$FAKE/running"
  : >"$FAKE/calls"
}
run() { # run <args...>: sets out, rc
  set +e
  out=$(PATH="$work/bin:$PATH" bash "$script" "$@" 2>&1); rc=$?
  set -e
}
check() { # check <name> <condition...>
  local name=$1; shift
  n=$((n + 1))
  if "$@"; then echo "ok   $n $name"; else echo "FAIL $n $name"; echo "$out" | sed 's/^/     | /'; fail=1; fi
}
called() { grep -qF -- "$1" "$FAKE/calls"; }
not_called() { ! called "$1"; }
current_config() {
  printf '{\n  "headset_serial": "2G0YC5ZG8K0123",\n  "local_only": false\n}\n' >"$(files_of "$FAKE")/pod_config.json"
}
idle_log() { echo "11-15 10:00:00.000 4242 4300 I Unity   : [RealityLog] HEARTBEAT: Server alive on port 8080 | IPs: 192.168.1.50 | Battery: 80% | Recording: False" >"$FAKE/logcat"; }

# 1. Config already current: nothing rewritten, no force-stop, grants verified, exit 0.
fresh unchanged; current_config; idle_log
before=$(stat -c %Y "$(files_of "$FAKE")/pod_config.json")
run FAKE1
check "unchanged config: exit 0" test "$rc" -eq 0
check "unchanged config: no force-stop" not_called "am force-stop"
check "unchanged config: no relaunch" not_called "monkey -p"
check "unchanged config: file not rewritten" not_called "cat > "
check "unchanged config: DUMP granted" called "pm grant $pkg android.permission.DUMP"

# 2. Config changed (old operator_id, no serial) while recording: written, no force-stop.
fresh recording
echo '{"operator_id":"stale"}' >"$(files_of "$FAKE")/pod_config.json"
{ idle_log; echo "11-15 10:01:00.000 4242 4242 I Unity   : [RealityLog] RecordingManager: Starting recording session '20261115_100100'"; } >"$FAKE/logcat"
run FAKE1
check "recording: exit 0" test "$rc" -eq 0
check "recording: no force-stop" not_called "am force-stop"
check "recording: config written" grep -q 2G0YC5ZG8K0123 "$(files_of "$FAKE")/pod_config.json"
check "recording: says why" grep -q "NOT relaunched: the app's recording state is recording" <<<"$out"

# 3. Changed, idle by the log, but an episode is still sealing: no force-stop.
fresh sealing
echo '{}' >"$(files_of "$FAKE")/pod_config.json"; idle_log
ep=$(files_of "$FAKE")/rig_staging/20261115_095000
mkdir -p "$ep"; echo '{"outcome":"complete"}' >"$ep/stop.json"; echo '{"state": "pending"}' >"$ep/seal.json"
done_ep=$(files_of "$FAKE")/rig_staging/20261115_090000
mkdir -p "$done_ep"; echo '{}' >"$done_ep/stop.json"; echo '{"state":"sealed"}' >"$done_ep/seal.json"
run FAKE1
check "sealing: no force-stop" not_called "am force-stop"
check "sealing: names the pending episode only" bash -c 'grep -q 20261115_095000 <<<"$1" && ! grep -q 20261115_090000 <<<"$1"' _ "$out"

# 4. Changed, no log marker at all: unknown state is not idle, so no force-stop.
fresh unknown; echo '{}' >"$(files_of "$FAKE")/pod_config.json"; : >"$FAKE/logcat"
run FAKE1
check "unknown state: no force-stop" not_called "am force-stop"

# 5. Changed and idle: force-stop and relaunch.
fresh idle; echo '{}' >"$(files_of "$FAKE")/pod_config.json"
{ echo "I Unity   : [RealityLog] RecordingManager: Starting recording session 'x'"; echo "I Unity   : [RealityLog] RecordingManager: Recording stopped successfully. Files saved to 'x'"; } >"$FAKE/logcat"
run FAKE1
check "changed+idle: exit 0" test "$rc" -eq 0
check "changed+idle: force-stop" called "am force-stop $pkg"
check "changed+idle: relaunch" called "monkey -p $pkg"

# 6. A grant that does not take: non-zero, named; the rest still ran.
fresh missing; current_config; idle_log
echo android.permission.DUMP >"$FAKE/ungrantable"
run FAKE1
check "missing grant: exit non-zero" test "$rc" -ne 0
check "missing grant: names DUMP" grep -q "FAILED: not granted after pm grant: android.permission.DUMP" <<<"$out"
check "missing grant: Wi-Fi still enabled" called "tcpip 5555"

# 7. --keep-awake and --no-wifi.
fresh options; current_config; idle_log
run --keep-awake --no-wifi FAKE1
check "options: exit 0" test "$rc" -eq 0
check "--keep-awake: stayon" called "shell svc power stayon true"
check "--keep-awake: timeout" called "shell settings put system screen_off_timeout 2147483647"
check "--keep-awake: prox_close" called "am broadcast -a com.oculus.vrpowermanager.prox_close"
check "--no-wifi: no tcpip" not_called "tcpip"

# 8. App not running: launched, never force-stopped.
fresh stopped; current_config; rm -f "$FAKE/running"
run FAKE1
check "not running: launched" called "monkey -p $pkg"
check "not running: no force-stop" not_called "am force-stop"

echo
if [ "$fail" -eq 0 ]; then echo "all $n checks passed"; else echo "some of $n checks FAILED"; exit 1; fi
