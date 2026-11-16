#!/usr/bin/env bash
# The on-device gate every OQC APK passes before anyone is told it is ready (sx#4210).
#
# Builds 74 and 75 passed every host harness and failed the first real recording on pod5 (75: both
# eyes NoSuchMethodError at stop, no MP4 finalized). This runs the APK on a headset over adb, with
# no wearer and no tracking gate:
#   1. refuses while the app records or a seal is pending (never interrupts collection)
#   2. installs the APK (install -r only, never uninstall) unless --no-install, and checks its version
#   3. QCL_TEST_APK set: QuestCameraLib's TwoCameraRecordingTest under am instrument
#   4. relaunches the app with the camera self-test asked for (files/camera_self_test.request, a
#      file only adb or the app can write): both head cameras record 15 s through OQC's own C# start
#      and stop; each eye must leave a finished MP4, timestamp rows == encoded frames, no capture_error
#   5. reads logcat: no crash or fatal signal, no ClassNotFound/NoSuchMethod/NoSuchField/
#      UnsatisfiedLink, no Unity exception outside device_smoke_known_exceptions.txt, the rig and
#      gripper status lines, the self-test's cue line
# Exit 0 only when every check passed; the verdict and logs land in <out>.
#
#   scripts/device_smoke.sh [--no-install] <adb-serial> <apk> [out-dir]
#   PKG (default com.samusynth.OpenQuestCapture)  SELF_TEST_SECONDS (default 15)
set -euo pipefail

install=1
if [[ "${1:-}" == "--no-install" ]]; then install=0; shift; fi
serial="${1:?usage: device_smoke.sh [--no-install] <adb-serial> <apk> [out-dir]}"
apk="${2:?usage: device_smoke.sh [--no-install] <adb-serial> <apk> [out-dir]}"
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
pkg="${PKG:-com.samusynth.OpenQuestCapture}"
seconds="${SELF_TEST_SECONDS:-15}"
files="/sdcard/Android/data/$pkg/files"
aapt2="$(ls -d "${ANDROID_HOME:-$HOME/Android/Sdk}"/build-tools/*/aapt2 | sort -V | tail -n 1)"
badging="$("$aapt2" dump badging "$apk")"
want_code="$(sed -n "s/.*versionCode='\([^']*\)'.*/\1/p" <<<"${badging%%$'\n'*}")"
out="${3:-$root/Builds/device-gate-$want_code-${serial//[:.]/_}-$(date -u +%Y%m%dT%H%M%SZ)}"
mkdir -p "$out"
a() { adb -s "$serial" "$@"; }
fails=()
fail() { fails+=("$1"); echo "FAIL: $1"; }
ok() { echo "ok: $1"; }

# A network serial survives another session restarting the adb server only if it is reconnected.
[[ "$serial" != *:* ]] || adb connect "$serial" >/dev/null 2>&1 || true
a get-state >/dev/null || { echo "adb cannot reach $serial" >&2; exit 2; }

# 1. Never during collection: the last heartbeat must say not recording, and no episode may sit
#    stopped without a sealed or refused seal.
heartbeat="$(a logcat -d -s Unity | grep 'HEARTBEAT:' | tail -n 1 || true)"
if grep -q 'Recording: True' <<<"$heartbeat"; then
    echo "REFUSED: $pkg is recording ($heartbeat); run the gate when it is idle" >&2; exit 3
fi
# A seal is running only for a stop the rig will seal: a stop.json whose outcome is not "failed", no
# purge.json, and no seal.json saying sealed or refused. A failed stop never gets a seal (seal.rs
# sealable()); libsx_rig keeps an unreported one on purpose (custody.rs), so it is not pending.
# pod5 11-16: 20261106_013831 and 20261111_160917 (camera dropout, never reported) blocked the
# older rule, which provision-headset.sh still uses.
pending="$(a shell "for d in $files/rig_staging/*/; do [ -f \"\$d\"stop.json ] || continue; [ -f \"\$d\"purge.json ] && continue; \
grep -qE '\"outcome\" *: *\"failed\"' \"\$d\"stop.json && continue; \
grep -qE '\"state\" *: *\"(sealed|refused)\"' \"\$d\"seal.json 2>/dev/null || echo \"\$d\"; done" 2>/dev/null | tr -d '\r' || true)"
if [[ -n "$pending" ]]; then
    echo "REFUSED: a seal is pending ($pending); run the gate when it is idle" >&2; exit 3
fi

# 2. Install and check the version the device now has.
if [[ "$install" -eq 1 ]]; then
    a install -r "$apk" | tee "$out/install.txt"
fi
have_code="$(a shell dumpsys package "$pkg" | sed -n 's/.*versionCode=\([0-9]*\).*/\1/p' | head -n 1)"
[[ "$have_code" == "$want_code" ]] && ok "installed versionCode $have_code" \
    || fail "device has versionCode '$have_code', the APK is $want_code"

a shell input keyevent KEYCODE_WAKEUP
a logcat -c; a logcat -b crash -c

# 3. QuestCameraLib's own camera test, when its test APK is given.
if [[ -n "${QCL_TEST_APK:-}" ]]; then
    a install -r -g "$QCL_TEST_APK" >/dev/null
    a shell am instrument -w -e class com.samusynth.questcamera.io.TwoCameraRecordingTest \
        com.samusynth.questcamera.test/androidx.test.runner.AndroidJUnitRunner | tee "$out/qcl-instrument.txt"
    grep -q '^OK (1 test)' "$out/qcl-instrument.txt" && ok "QuestCameraLib TwoCameraRecordingTest" \
        || fail "QuestCameraLib TwoCameraRecordingTest (see $out/qcl-instrument.txt)"
fi

# 4. The app's camera self-test, from a fresh launch.
a shell "rm -f $files/camera_self_test/report.json; echo '{\"seconds\":$seconds}' > $files/camera_self_test.request"
a shell am force-stop "$pkg"
a logcat -c
a shell monkey -p "$pkg" -c android.intent.category.LAUNCHER 1 >/dev/null 2>&1
deadline=$((SECONDS + seconds + 90))
verdict=""
while [[ $SECONDS -lt $deadline ]]; do
    verdict="$(a logcat -d -s Unity | grep -E 'CAMERA SELF-TEST (PASS|FAIL)' | tail -n 1 || true)"
    [[ -n "$verdict" ]] && break
    sleep 3
done
sleep 2   # the cue line follows the verdict
a logcat -d -v time > "$out/logcat.txt"
a logcat -d -b crash > "$out/crash.txt"
a exec-out cat "$files/camera_self_test/report.json" > "$out/self_test_report.json" 2>/dev/null || true
a exec-out screencap -p > "$out/screen.png" || true
a shell rm -f "$files/camera_self_test.request"   # an APK without the self-test leaves it unread

if [[ "$verdict" == *"CAMERA SELF-TEST PASS"* ]] && grep -q '"verdict": "pass"' "$out/self_test_report.json"; then
    ok "camera self-test: ${verdict#*PASS: }"
elif [[ -n "$verdict" ]]; then
    fail "camera self-test: ${verdict#*FAIL: }"
else
    fail "camera self-test never reported within $((seconds + 90)) s (an APK without it, or the app did not start)"
fi

# 5. Logcat: what an APK must never print at launch or around a recording.
pid="$(a shell pidof "$pkg" | tr -d '\r' || true)"
[[ -n "$pid" ]] && ok "$pkg running (pid $pid)" || fail "$pkg is not running after the self-test"
if grep -q "$pkg" "$out/crash.txt" || grep -E "Fatal signal|FATAL EXCEPTION" "$out/logcat.txt" | grep -q "$pkg\|(${pid:-none})"; then
    fail "a crash (see $out/crash.txt)"
else
    ok "no crash or fatal signal"
fi
linkage="$(grep -E "\(${pid:-none}\)" "$out/logcat.txt" | grep -E 'ClassNotFoundException|NoSuchMethodError|NoSuchFieldError|UnsatisfiedLinkError|NoClassDefFoundError' || true)"
if [[ -n "$linkage" ]]; then
    fail "linkage errors in the app's log:"$'\n'"$(cut -c1-300 <<<"$linkage" | sort -u | head -n 8)"
else
    ok "no ClassNotFound/NoSuchMethod/NoSuchField/UnsatisfiedLink"
fi
# Any Unity exception not in the reviewed baseline (scripts/device_smoke_known_exceptions.txt) fails:
# a new launch exception is a regression until someone reads it and lists it with its reason.
if unknown="$(python3 "$root/scripts/unity_exceptions.py" "$out/logcat.txt" "${pid:-none}")"; then
    ok "no Unity exception outside the reviewed baseline"
else
    fail "new Unity exceptions:"$'\n'"$unknown"
fi
for line in 'RigCoordinator: rig opened' 'RigCoordinator: gripper switch status:' 'cue: Sealed (camera self-test passed)'; do
    grep -qF "$line" "$out/logcat.txt" && ok "logged: $line" || fail "never logged: $line"
done

{
    echo "{"
    echo "  \"schema\": \"oqc.device_gate/v1\","
    echo "  \"serial\": \"$serial\", \"version_code\": \"$want_code\","
    echo "  \"apk_sha256\": \"$(sha256sum "$apk" | cut -d' ' -f1)\","
    echo "  \"passed\": $([[ ${#fails[@]} -eq 0 ]] && echo true || echo false),"
    echo "  \"failures\": $(printf '%s\n' "${fails[@]+"${fails[@]}"}" | python3 -c 'import json,sys; print(json.dumps([l for l in sys.stdin.read().split("\n") if l]))'),"
    echo "  \"finished_utc\": \"$(date -u +%Y-%m-%dT%H:%M:%SZ)\""
    echo "}"
} > "$out/verdict.json"
if [[ ${#fails[@]} -eq 0 ]]; then
    echo "DEVICE GATE PASS: $apk on $serial ($out)"
    exit 0
fi
echo "DEVICE GATE FAIL: ${#fails[@]} check(s) on $serial; not ready ($out)"
exit 1
