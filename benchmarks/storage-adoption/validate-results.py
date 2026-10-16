import json
import math
import sys
from pathlib import Path
root=Path(sys.argv[1])
kind=sys.argv[2]
expected=({f"State={state}&ScopeSize={scope}" for state in ("active","deleted95","compacted95","deleted-gap") for scope in (1,24,256)} if kind=="history" else {f"Count={count}&HitPercent={hit}" for count in (1,2,16,64) for hit in (0,50,100)})
reports=list((root/"results").glob("*-report-full-compressed.json"))
if len(reports)!=1: raise SystemExit("Expected exactly one report")
rows=json.loads(reports[0].read_text())["Benchmarks"]
if len(rows)!=len(expected) or {row["Parameters"] for row in rows}!=expected: raise SystemExit("Incomplete matrix")
for row in rows:
 stats=row.get("Statistics")
 if not stats or stats["N"]<6 or not math.isfinite(stats["Mean"]) or stats["Mean"]<=0: raise SystemExit("Missing samples")
 samples=[x for x in row["Measurements"] if x["IterationMode"]=="Workload" and x["IterationStage"]=="Actual"]
 if len(samples)!=8: raise SystemExit("Expected eight actual iterations")
print(f"Verified {kind}: {len(rows)} complete cases")
