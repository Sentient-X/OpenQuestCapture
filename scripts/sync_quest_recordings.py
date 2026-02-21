#!/usr/bin/env python3
"""Sync OpenQuestCapture recordings from Quest to local storage.

Default behavior:
1) Pull all session folders that are on the headset but not present locally.
2) Delete every session on the headset that is now present locally
   (including sessions pulled in the same run).
"""

from __future__ import annotations

import argparse
import re
import shutil
import subprocess
import sys
from pathlib import Path


SESSION_PATTERN = re.compile(r"^\d{8}_\d{6}$")
DEFAULT_DEVICE_DIR = "/sdcard/Android/data/com.samusynth.OpenQuestCapture/files"


def run_checked(cmd: list[str], *, capture_output: bool = True) -> subprocess.CompletedProcess[str]:
    """Run a command and raise RuntimeError with stderr on failure."""
    result = subprocess.run(cmd, text=True, capture_output=capture_output)
    if result.returncode != 0:
        stderr = (result.stderr or "").strip()
        stdout = (result.stdout or "").strip()
        details = stderr if stderr else stdout
        raise RuntimeError(f"Command failed ({result.returncode}): {' '.join(cmd)}\n{details}")
    return result


def ensure_adb_available() -> None:
    if shutil.which("adb") is None:
        raise RuntimeError("adb was not found in PATH.")


def ensure_device_connected() -> str:
    output = run_checked(["adb", "devices"], capture_output=True).stdout
    connected: list[str] = []
    unauthorized: list[str] = []

    for line in output.splitlines()[1:]:
        line = line.strip()
        if not line:
            continue
        parts = line.split()
        if len(parts) < 2:
            continue
        serial, state = parts[0], parts[1]
        if state == "device":
            connected.append(serial)
        elif state == "unauthorized":
            unauthorized.append(serial)

    if unauthorized and not connected:
        raise RuntimeError(
            "Quest is connected but unauthorized. Put on the headset and allow USB debugging."
        )
    if not connected:
        raise RuntimeError("No ADB device detected.")

    return connected[0]


def list_device_sessions(device_dir: str) -> list[str]:
    output = run_checked(["adb", "shell", "ls", "-1", device_dir], capture_output=True).stdout
    sessions = []
    for raw in output.splitlines():
        name = raw.strip().replace("\r", "")
        if SESSION_PATTERN.fullmatch(name):
            sessions.append(name)
    return sorted(sessions)


def list_local_sessions(local_dir: Path) -> set[str]:
    if not local_dir.exists():
        return set()
    sessions: set[str] = set()
    for entry in local_dir.iterdir():
        if entry.is_dir() and SESSION_PATTERN.fullmatch(entry.name):
            sessions.add(entry.name)
    return sessions


def pull_session(session: str, device_dir: str, local_dir: Path, dry_run: bool) -> bool:
    remote = f"{device_dir}/{session}"
    if dry_run:
        print(f"[DRY-RUN] pull {remote} -> {local_dir}")
        return True

    print(f"Pulling {session}...")
    result = subprocess.run(["adb", "pull", remote, str(local_dir)], text=True)
    if result.returncode != 0:
        print(f"  [FAIL] adb pull failed for {session}", file=sys.stderr)
        return False

    local_session_dir = local_dir / session
    if not local_session_dir.is_dir():
        print(f"  [FAIL] Pull completed but {local_session_dir} was not created.", file=sys.stderr)
        return False

    print("  [OK]")
    return True


def delete_session(session: str, device_dir: str, dry_run: bool) -> bool:
    remote = f"{device_dir}/{session}"
    if dry_run:
        print(f"[DRY-RUN] delete {remote}")
        return True

    result = subprocess.run(["adb", "shell", "rm", "-rf", remote], text=True, capture_output=True)
    if result.returncode != 0:
        stderr = (result.stderr or "").strip()
        print(f"  [FAIL] delete {session}: {stderr}", file=sys.stderr)
        return False
    print(f"Deleted {session}")
    return True


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description=(
            "Fetch new OpenQuestCapture recordings from Quest and prune old recordings "
            "from the device."
        )
    )
    parser.add_argument(
        "--device-dir",
        default=DEFAULT_DEVICE_DIR,
        help=f"Device recordings directory (default: {DEFAULT_DEVICE_DIR})",
    )
    parser.add_argument(
        "--local-dir",
        default="recordings",
        help="Local recordings directory (default: ./recordings)",
    )
    parser.add_argument(
        "--dry-run",
        action="store_true",
        help="Print actions without pulling/deleting.",
    )
    parser.add_argument(
        "--no-delete",
        action="store_true",
        help="Do not delete any recordings from the device.",
    )
    parser.add_argument(
        "--delete-old-only",
        action="store_true",
        help=(
            "Delete only sessions that already existed locally before this run. "
            "By default, newly pulled sessions are also deleted from the device."
        ),
    )
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    local_dir = Path(args.local_dir).expanduser().resolve()
    local_dir.mkdir(parents=True, exist_ok=True)

    try:
        ensure_adb_available()
        serial = ensure_device_connected()
    except RuntimeError as exc:
        print(f"[ERROR] {exc}", file=sys.stderr)
        return 1

    print(f"Using device: {serial}")
    print(f"Local directory: {local_dir}")

    try:
        device_sessions = list_device_sessions(args.device_dir)
    except RuntimeError as exc:
        print(f"[ERROR] {exc}", file=sys.stderr)
        return 1

    if not device_sessions:
        print("No recording sessions found on the device.")
        return 0

    local_before = list_local_sessions(local_dir)
    new_sessions = [s for s in device_sessions if s not in local_before]
    old_sessions = [s for s in device_sessions if s in local_before]

    print(f"Device sessions: {len(device_sessions)}")
    print(f"New sessions to pull: {len(new_sessions)}")
    print(f"Already local sessions: {len(old_sessions)}")

    pulled_ok: list[str] = []
    pull_failed: list[str] = []

    for session in new_sessions:
        if pull_session(session, args.device_dir, local_dir, args.dry_run):
            pulled_ok.append(session)
        else:
            pull_failed.append(session)

    if args.no_delete:
        print("Deletion disabled (--no-delete).")
        delete_candidates: list[str] = []
    elif args.delete_old_only:
        delete_candidates = old_sessions
    else:
        # Delete all sessions that are present locally after this run.
        delete_candidates = list(dict.fromkeys(old_sessions + pulled_ok))

    deleted_ok: list[str] = []
    delete_failed: list[str] = []
    for session in delete_candidates:
        if delete_session(session, args.device_dir, args.dry_run):
            deleted_ok.append(session)
        else:
            delete_failed.append(session)

    print("\nSummary:")
    print(f"  Pulled successfully: {len(pulled_ok)}")
    print(f"  Pull failed: {len(pull_failed)}")
    print(f"  Deleted on device: {len(deleted_ok)}")
    print(f"  Delete failed: {len(delete_failed)}")

    if pull_failed:
        print("  Pull failures:", ", ".join(pull_failed))
    if delete_failed:
        print("  Delete failures:", ", ".join(delete_failed))

    if pull_failed or delete_failed:
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
