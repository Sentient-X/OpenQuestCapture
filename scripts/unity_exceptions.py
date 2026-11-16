#!/usr/bin/env python3
"""Unity exceptions in a logcat (-v time) for one pid, as "<Type> at <first frame>", minus a
reviewed baseline (scripts/device_smoke_known_exceptions.txt). Prints each new one with its count;
exit 1 if any. Used by device_smoke.sh (sx#4210).

  unity_exceptions.py <logcat.txt> <pid> [known.txt]
"""
import re, sys
from collections import Counter
from pathlib import Path

log, pid = Path(sys.argv[1]), sys.argv[2]
known_path = Path(sys.argv[3]) if len(sys.argv) > 3 else Path(__file__).with_name("device_smoke_known_exceptions.txt")
known = {l.strip() for l in known_path.read_text().splitlines() if l.strip() and not l.startswith("#")}
lines = [l for l in log.read_text(errors="replace").splitlines() if f"({pid})" in l and "/Unity" in l]
body = lambda l: l.split("): ", 1)[1] if "): " in l else ""
found = Counter()
for i, line in enumerate(lines):
    m = re.match(r"(\w+(?:\.\w+)*Exception)(?::|$)", body(line))
    if not m or not line[19:20] == "E" and " E/Unity" not in line:
        continue
    frame = next((body(n).strip() for n in lines[i + 1:i + 4] if body(n).strip().startswith("at ")), "at ?")
    frame = re.sub(r"\s*\[0x[0-9a-f]+\].*$", "", frame)
    found[f"{m.group(1).split('.')[-1]} {frame}"] += 1
new = {k: n for k, n in found.items() if k not in known}
for k, n in sorted(new.items()):
    print(f"{n}x {k}")
sys.exit(1 if new else 0)
