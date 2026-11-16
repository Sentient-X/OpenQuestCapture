#!/usr/bin/env python3
"""Every JNI method OQC's C# binds exists in the AAR that ships (sx#4210).

Reads each `new JniSignatures(<class>, new[] { ("name", "descriptor"), ... })` table under
Assets/RealityLog/Scripts and asks `javap -s` for that class inside the AAR's classes.jar. Exit 1
naming every (class, name, descriptor) the AAR lacks.

Build 75 shipped QuestCameraLib a6e6bd3, whose stopRecording compiled to ()Lkotlin/Unit;, while
OQC called ()V: every host harness passed and both eyes threw NoSuchMethodError at the first stop
on pod5. Against build 75's AAR this check prints
  MISSING com.samusynth.questcamera.io.VideoRecorderSurfaceProvider.stopRecording()V

  python3 scripts/check_jni_abi.py [Assets/Plugins/Android/questcameralib.aar]
"""

from __future__ import annotations

import re
import subprocess
import sys
import tempfile
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SCRIPTS = ROOT / "Assets" / "RealityLog" / "Scripts"
TABLE = re.compile(r"new\s+JniSignatures\(\s*([A-Za-z_][\w.]*|\"[^\"]+\")\s*,\s*new\[\]\s*\{(.*?)\}\s*\)", re.S)
ENTRY = re.compile(r"\(\s*\"(\w+)\"\s*,\s*\"([^\"]+)\"\s*\)")
CONST = r"const\s+string\s+{name}\s*=\s*\"([^\"]+)\""


def tables() -> dict[str, set[tuple[str, str]]]:
    found: dict[str, set[tuple[str, str]]] = {}
    for source in sorted(SCRIPTS.rglob("*.cs")):
        text = source.read_text(encoding="utf-8")
        for cls, body in TABLE.findall(text):
            if cls.startswith('"'):
                name = cls.strip('"')
            else:
                const = re.search(CONST.format(name=re.escape(cls.split(".")[-1])), text)
                if const is None:
                    sys.exit(f"{source.relative_to(ROOT)}: JniSignatures class {cls} is not a string constant in that file")
                name = const.group(1)
            entries = set(ENTRY.findall(body))
            if not entries:
                sys.exit(f"{source.relative_to(ROOT)}: empty JniSignatures table for {name}")
            found.setdefault(name, set()).update(entries)
    return found


def descriptors(jar: Path, cls: str) -> set[tuple[str, str]]:
    out = subprocess.run(["javap", "-s", "-p", "-cp", str(jar), cls], capture_output=True, text=True)
    if out.returncode != 0:
        return set()
    have: set[tuple[str, str]] = set()
    lines = out.stdout.splitlines()
    for i, line in enumerate(lines):
        m = re.search(r"\s([\w$]+)\(.*\)", line)
        if m and i + 1 < len(lines) and "descriptor:" in lines[i + 1]:
            have.add((m.group(1), lines[i + 1].split("descriptor:", 1)[1].strip()))
    return have


def main() -> int:
    aar = Path(sys.argv[1]) if len(sys.argv) > 1 else ROOT / "Assets" / "Plugins" / "Android" / "questcameralib.aar"
    wanted = tables()
    if not wanted:
        sys.exit("no JniSignatures tables found")
    missing: list[str] = []
    with tempfile.TemporaryDirectory(dir=ROOT / "Builds" if (ROOT / "Builds").is_dir() else None) as tmp:
        jar = Path(tmp) / "classes.jar"
        with zipfile.ZipFile(aar) as z:
            jar.write_bytes(z.read("classes.jar"))
        for cls, entries in sorted(wanted.items()):
            have = descriptors(jar, cls)
            if not have:
                missing.append(f"MISSING class {cls} (not in {aar.name})")
                continue
            missing += [f"MISSING {cls}.{n}{d}" for n, d in sorted(entries) if (n, d) not in have]
    total = sum(len(e) for e in wanted.values())
    if missing:
        print("\n".join(missing))
        print(f"FAIL: {len(missing)} of {total} JNI methods OQC binds are not in {aar}")
        return 1
    print(f"OK: {total} JNI methods in {len(wanted)} classes match {aar.name}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
