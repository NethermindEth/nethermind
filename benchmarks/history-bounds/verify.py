import json
import math
import sys
from pathlib import Path

root = Path(sys.argv[1])
reports = list((root / "results").glob("*-report-full-compressed.json"))
if len(reports) != 1:
    raise SystemExit(f"Expected one JSON report, found {len(reports)} under {root}")
benchmarks = json.loads(reports[0].read_text())["Benchmarks"]
expected = {f"State={state}&ScopeSize={scope}" for state in ("active", "deleted95", "compacted95") for scope in (1, 24, 256)}
actual = {entry["Parameters"] for entry in benchmarks}
if len(benchmarks) != len(expected) or actual != expected:
    raise SystemExit(f"Incomplete benchmark coverage: {actual}")
for entry in benchmarks:
    stats = entry.get("Statistics")
    if not stats or stats["N"] < 6 or not math.isfinite(stats["Mean"]) or stats["Mean"] <= 0:
        raise SystemExit(f"Missing valid measurements for {entry['Parameters']}")
print(f"Verified {len(benchmarks)} complete benchmark cases in {root}")
