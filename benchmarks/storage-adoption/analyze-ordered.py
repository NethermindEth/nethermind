import json
import math
from pathlib import Path
import statistics
import sys

root = Path(sys.argv[1])
kind = sys.argv[2]
order = json.loads((root / "measurement-order.json").read_text())[kind]
assert order in (["baseline", "history", "history", "baseline"],
                 ["baseline", "multiget", "multiget", "baseline"],
                 ["baseline", "baseline", "history", "history", "baseline", "baseline"],
                 ["baseline", "baseline", "multiget", "multiget", "baseline", "baseline"],
                 ["baseline", "baseline", "control", "candidate", "candidate", "control", "baseline", "baseline"])
arms = []
cases = None
cpu = set()
for number, source in enumerate(order, 1):
    files = list((root / kind / f"{number}-{source}" / "results").glob("*-report-full-compressed.json"))
    assert len(files) == 1
    report = json.loads(files[0].read_text())
    cpu.add(report["HostEnvironmentInfo"]["ProcessorName"])
    rows = {r["Parameters"]:r for r in report["Benchmarks"]}
    if cases is None:
        cases = set(rows)
    assert set(rows) == cases
    parsed = {}
    for key, row in rows.items():
        stats = row["Statistics"]
        assert stats and stats["N"] >= 6 and math.isfinite(stats["Mean"]) and stats["Mean"] > 0
        actual = [m for m in row["Measurements"] if m["IterationMode"] == "Workload" and m["IterationStage"] == "Actual"]
        assert len(actual) == 8
        parsed[key] = {"ns":stats["Mean"], "bytes":row["Memory"]["BytesAllocatedPerOperation"],
                       "samples":[m["Nanoseconds"]/m["Operations"] for m in actual]}
    arms.append({"source":source,"rows":parsed})
assert len(cpu) == 1
summary = {}
for key in sorted(cases):
    names = set(order)
    times = {name:[a["rows"][key]["ns"] for a in arms if a["source"] == name] for name in names}
    means = {name:statistics.mean(values) for name,values in times.items()}
    allocations = {name:statistics.mean(a["rows"][key]["bytes"] for a in arms if a["source"] == name) for name in names}
    controls = [100*abs(arms[i+1]["rows"][key]["ns"]/arms[i]["rows"][key]["ns"]-1)
                for i in range(len(arms)-1) if order[i] == order[i+1] == "baseline"]
    baseline_spread = 100*(max(times["baseline"])/min(times["baseline"])-1)
    target = "candidate" if "candidate" in names else ("history" if "history" in names else "multiget")
    changes = {}
    for reference in sorted(names - {target}):
        delta = 100*(means[target]/means[reference]-1)
        reference_spread = 100*(max(times[reference])/min(times[reference])-1)
        changes[reference] = {"time_change_pct":delta,
                              "candidate_both_below_reference_all":max(times[target]) < min(times[reference]),
                              "candidate_both_above_reference_all":min(times[target]) > max(times[reference]),
                              "gain_exceeds_observed_control_variation":bool(controls) and delta < -max(baseline_spread,reference_spread,*controls)}
    summary[key] = {"mean_ns":means,"managed_bytes":allocations,"arms_ns":times,"aa_pair_change_pct":controls,
                    "baseline_spread_pct":baseline_spread,"changes":changes}
    print(key, json.dumps(summary[key],sort_keys=True))
(root / f"{kind}-summary.json").write_text(json.dumps({"cpu":next(iter(cpu)),"order":order,"cases":summary,"arms":arms},indent=2)+"\n")
