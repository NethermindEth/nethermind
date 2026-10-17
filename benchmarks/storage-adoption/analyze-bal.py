import json
from pathlib import Path
import statistics
import sys

root = Path(sys.argv[1])
arms = {name: [] for name in ("baseline", "control", "candidate")}
for folder in sorted((root / "bal").iterdir()):
    name = folder.name.split("-", 1)[1]
    rows = json.loads((folder / "results.json").read_text())
    cases = {}
    for row in rows:
        key = f'{row["count"]}/{row["shape"]}/{row["cache"]}'
        times = [sample["nanoseconds"] for sample in row["samples"]]
        cases[key] = {
            "nanoseconds": statistics.mean(times),
            "managedBytes": statistics.mean(sample["managedBytes"] for sample in row["samples"]),
            "samples": times,
        }
    arms[name].append(cases)
assert [len(arms[name]) for name in ("baseline", "control", "candidate")] == [4, 2, 2]
summary = []
for key in arms["baseline"][0]:
    assert all(key in arm for group in arms.values() for arm in group)
    means = {name: statistics.mean(arm[key]["nanoseconds"] for arm in group) for name, group in arms.items()}
    baseline = [arm[key]["nanoseconds"] for arm in arms["baseline"]]
    controls = [arm[key]["nanoseconds"] for arm in arms["control"]]
    candidates = [arm[key]["nanoseconds"] for arm in arms["candidate"]]
    summary.append({
        "case": key,
        "meansNanoseconds": means,
        "versusMasterPercent": 100 * (means["candidate"] / means["baseline"] - 1),
        "versusControlPercent": 100 * (means["candidate"] / means["control"] - 1),
        "baselineArmSpanPercent": 100 * (max(baseline) / min(baseline) - 1),
        "baselineAdjacentAAControlsPercent": [100 * (baseline[1] / baseline[0] - 1), 100 * (baseline[3] / baseline[2] - 1)],
        "controlArmSpanPercent": 100 * (max(controls) / min(controls) - 1),
        "candidateArmSpanPercent": 100 * (max(candidates) / min(candidates) - 1),
        "allThreadManagedBytes": {name: statistics.mean(arm[key]["managedBytes"] for arm in group) for name, group in arms.items()},
        "arms": {name: [arm[key] for arm in group] for name, group in arms.items()},
    })
(root / "bal-summary.json").write_text(json.dumps(summary, indent=2) + "\n")
print("case | master us | control us | candidate us | vs master | vs control | master span")
for row in summary:
    means = row["meansNanoseconds"]
    print(f'{row["case"]} | {means["baseline"]/1000:.2f} | {means["control"]/1000:.2f} | {means["candidate"]/1000:.2f} | {row["versusMasterPercent"]:+.2f}% | {row["versusControlPercent"]:+.2f}% | {row["baselineArmSpanPercent"]:.2f}%')
