#!/usr/bin/env bash
# Runs scripts/install_fleet.sh against a fake `adb` and a fake `aapt2` on PATH (no device is
# touched) and checks: up to date -> skipped; running without --stop -> skipped, not installed;
# --stop -> force-stop, install, versionCode verified, DUMP granted; unreachable; a signer
# mismatch is reported and never uninstalled; --dry-run installs nothing; a stdin list with
# comments; exit 1 when one target fails. Usage: scripts/tests/test-install-fleet.sh
set -euo pipefail

here=$(cd "$(dirname "$0")" && pwd)
script=$here/../install_fleet.sh
scratch_root=${TMPDIR:-$HOME/p3/tmp}
mkdir -p "$scratch_root"
work=$(mktemp -d "$scratch_root/install-fleet-test.XXXXXX")
trap 'rm -rf "$work"' EXIT
pkg=com.samusynth.OpenQuestCapture

mkdir -p "$work/bin" "$work/sdk/build-tools/35.0.0"
cat >"$work/bin/adb" <<'SHIM'
#!/usr/bin/env bash
# Fake adb: one directory per target under $FAKE/<target>/ with serial, code (installed
# versionCode), running, unreachable, incompatible. Every call is appended to $FAKE/calls.
set -euo pipefail
echo "$*" >>"$FAKE/calls"
pkg=com.samusynth.OpenQuestCapture
t=
if [ "${1:-}" = -s ]; then t=$2; shift 2; fi
cmd=${1:-}; shift || true
[ "$cmd" = connect ] && t=$1
d=$FAKE/$t
case "$cmd" in
  connect) if [ -f "$d/unreachable" ]; then echo "failed to connect to $t"; exit 1; fi; echo "connected to $t" ;;
  wait-for-device) if [ -f "$d/unreachable" ]; then echo "error: device '$t' not found" >&2; exit 1; fi ;;
  install)
    if [ -f "$d/incompatible" ]; then
      echo "Performing Streamed Install"
      echo "adb: failed to install $2: Failure [INSTALL_FAILED_UPDATE_INCOMPATIBLE: Existing package $pkg signatures do not match newer version; ignoring!]"
      exit 1
    fi
    [ -f "$d/running" ] && { echo "fake adb: install while $pkg runs on $t" >&2; exit 1; }
    echo "$APK_CODE" >"$d/code"; echo "Performing Streamed Install"; echo "Success" ;;
  uninstall) echo "fake adb: uninstall is never allowed" >&2; exit 1 ;;
  shell)
    case "$*" in
      "getprop ro.serialno") cat "$d/serial" ;;
      "dumpsys package $pkg")
        echo "Packages:"; echo "  Package [$pkg]"
        [ -f "$d/code" ] && echo "    versionCode=$(cat "$d/code") minSdk=29 targetSdk=34" || true ;;
      "pidof $pkg") [ -f "$d/running" ] && echo 4242 || exit 1 ;;
      "am force-stop $pkg") rm -f "$d/running" ;;
      "pm grant $pkg android.permission.DUMP") echo DUMP >>"$d/granted" ;;
      *) echo "fake adb: unhandled shell $*" >&2; exit 1 ;;
    esac ;;
  *) echo "fake adb: unhandled $cmd" >&2; exit 1 ;;
esac
SHIM
cat >"$work/sdk/build-tools/35.0.0/aapt2" <<'SHIM'
#!/usr/bin/env bash
# Fake aapt2: `dump badging <apk>` answers from APK_CODE / APK_NAME.
set -euo pipefail
echo "$*" >>"$FAKE/calls"
[ "$1 $2" = "dump badging" ] || { echo "fake aapt2: unhandled $*" >&2; exit 1; }
echo "package: name='com.samusynth.OpenQuestCapture' versionCode='$APK_CODE' versionName='$APK_NAME' platformBuildVersionName='14' platformBuildVersionCode='34' compileSdkVersion='34' compileSdkVersionCodename='14'"
echo "sdkVersion:'29'"
echo "targetSdkVersion:'34'"
SHIM
chmod +x "$work/bin/adb" "$work/sdk/build-tools/35.0.0/aapt2"
apk=$work/OpenQuestCapture-0.85-85.apk
echo fake-apk >"$apk"
export APK_CODE=85 APK_NAME=0.85 ANDROID_HOME=$work/sdk

fail=0
n=0
# fresh <name>: a new fleet; headset <target> <serial> <code> [running|unreachable|incompatible...]
fresh() {
  FAKE=$work/$1; export FAKE
  mkdir -p "$FAKE"; : >"$FAKE/calls"
  export INSTALL_FLEET_RUNS=$FAKE/runs
}
headset() {
  local t=$1 serial=$2 code=$3; shift 3
  mkdir -p "$FAKE/$t"; echo "$serial" >"$FAKE/$t/serial"
  [ "$code" = none ] || echo "$code" >"$FAKE/$t/code"
  for flag in "$@"; do touch "$FAKE/$t/$flag"; done
}
run() { # run <args...>: sets out, rc
  set +e
  out=$(PATH="$work/bin:$PATH" bash "$script" "$@" 2>&1 </dev/null); rc=$?
  set -e
}
check() { # check <name> <condition...>
  local name=$1; shift
  n=$((n + 1))
  if "$@"; then echo "ok   $n $name"; else echo "FAIL $n $name"; echo "$out" | sed 's/^/     | /'; fail=1; fi
}
called() { grep -qF -- "$1" "$FAKE/calls"; }
not_called() { ! called "$1"; }
row() { grep -E "^$1 +$2" <<<"$out" | grep -qF -- "$3"; } # row <target> <serial> <result text>
code_of() { cat "$FAKE/$1/code"; }

# 1. Up to date: nothing installed, nothing stopped, exit 0.
fresh current; headset 3487C10J5K07MS 3487C10J5K07MS 85 running
run "$apk" 3487C10J5K07MS
check "up to date: exit 0" test "$rc" -eq 0
check "up to date: in the table" row 3487C10J5K07MS 3487C10J5K07MS "up to date"
check "up to date: no install" not_called "install -r"
check "up to date: no force-stop" not_called "am force-stop"
check "up to date: badging read once" test "$(grep -c "dump badging" "$FAKE/calls")" -eq 1

# 2. Running without --stop: skipped, never installed (it may be recording); exit 0.
fresh running; headset 340YC10GBR0PFM 340YC10GBR0PFM 76 running
run "$apk" 340YC10GBR0PFM
check "running: exit 0" test "$rc" -eq 0
check "running: skipped, names --stop" row 340YC10GBR0PFM 340YC10GBR0PFM "running, skipped (pass --stop)"
check "running: no install" not_called "install -r"
check "running: no force-stop" not_called "am force-stop"
check "running: still on 76" test "$(code_of 340YC10GBR0PFM)" = 76

# 3. --stop on a running headset: force-stop, install, versionCode verified, DUMP granted.
fresh stop; headset 340YC10GBR0PFM 340YC10GBR0PFM 76 running
run --stop "$apk" 340YC10GBR0PFM
check "--stop: exit 0" test "$rc" -eq 0
check "--stop: force-stop before install" called "shell am force-stop $pkg"
check "--stop: installed with -r" called "install -r $apk"
check "--stop: updated 76 -> 85" grep -qE '^340YC10GBR0PFM +340YC10GBR0PFM +76 +85 +updated' <<<"$out"
check "--stop: device now on 85" test "$(code_of 340YC10GBR0PFM)" = 85
check "--stop: DUMP granted" called "pm grant $pkg android.permission.DUMP"
check "--stop: version re-read after install" test "$(grep -c "dumpsys package" "$FAKE/calls")" -eq 2

# 4. A network target nobody answers: unreachable, exit 1; a USB serial whose wait times out too.
fresh unreachable; headset 192.168.29.77:5555 ? none unreachable; headset 2G0YC5ZG8K0123 ? none unreachable
run "$apk" 192.168.29.77 2G0YC5ZG8K0123
check "unreachable: exit 1" test "$rc" -eq 1
check "unreachable: ip gets :5555 and connect" called "connect 192.168.29.77:5555"
check "unreachable: ip in the table" row 192.168.29.77:5555 '\?' unreachable
check "unreachable: serial in the table" row 2G0YC5ZG8K0123 '\?' unreachable
check "unreachable: no install" not_called "install -r"

# 5. Signer mismatch: reported for a person, never uninstalled.
fresh signer; headset 2G0YC5ZG8K0123 2G0YC5ZG8K0123 67 incompatible
run "$apk" 2G0YC5ZG8K0123
check "signer: exit 1" test "$rc" -eq 1
check "signer: says a person must uninstall" row 2G0YC5ZG8K0123 2G0YC5ZG8K0123 "failed: signer differs, needs uninstall by a person"
check "signer: never uninstalls" not_called "uninstall"
check "signer: still on 67" test "$(code_of 2G0YC5ZG8K0123)" = 67

# 6. --dry-run: the plan per target, nothing installed or stopped.
fresh dry; headset A1 SER-A1 84; headset B2 SER-B2 85; headset C3 SER-C3 77 running
run --dry-run "$apk" A1 B2 C3
check "dry-run: exit 0" test "$rc" -eq 0
check "dry-run: would install" row A1 SER-A1 "would install 84 -> 85"
check "dry-run: up to date" row B2 SER-B2 "up to date"
check "dry-run: running would skip" row C3 SER-C3 "running, would skip (pass --stop)"
check "dry-run: no install" not_called "install -r"
check "dry-run: no force-stop" not_called "am force-stop"
check "dry-run: A1 still on 84" test "$(code_of A1)" = 84

# 7. Targets on stdin with comments and blanks; a mixed fleet; one failure makes exit 1.
fresh stdin; headset A1 SER-A1 84; headset 192.168.31.20:5555 SER-WIFI 84; headset DEAD ? none unreachable
set +e
out=$(printf '# the lab\nA1   # bench\n\n192.168.31.20\nDEAD\n' | PATH="$work/bin:$PATH" bash "$script" --jobs 2 "$apk" 2>&1); rc=$?
set -e
check "stdin: exit 1 (one unreachable)" test "$rc" -eq 1
check "stdin: A1 updated" grep -qE '^A1 +SER-A1 +84 +85 +updated' <<<"$out"
check "stdin: Wi-Fi target connected and updated" grep -qE '^192.168.31.20:5555 +SER-WIFI +84 +85 +updated' <<<"$out"
check "stdin: DEAD unreachable" row DEAD '\?' unreachable
check "stdin: comment not a target" test "$(grep -c "^the\|^bench" <<<"$out")" -eq 0
check "stdin: one log per target" test -f "$INSTALL_FLEET_RUNS"/*/192.168.31.20:5555.log
check "stdin: run directory printed" grep -q "^logs: $INSTALL_FLEET_RUNS/" <<<"$out"

# 8. No badging tool (an SDK without build-tools; this assumes no aapt2 on PATH): a clear failure
#    before any adb call.
fresh notool; headset A1 SER-A1 84
set +e
out=$(ANDROID_HOME=$work/nowhere PATH="$work/bin:$PATH" bash "$script" "$apk" A1 2>&1 </dev/null); rc=$?
set -e
check "no aapt2: exit 2" test "$rc" -eq 2
check "no aapt2: says so" grep -q "no aapt2 or aapt" <<<"$out"
check "no aapt2: no adb call" test ! -s "$FAKE/calls"

echo
if [ "$fail" -eq 0 ]; then echo "all $n checks passed"; else echo "some of $n checks FAILED"; exit 1; fi
