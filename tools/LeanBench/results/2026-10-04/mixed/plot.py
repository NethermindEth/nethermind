#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only
"""Plot captured offline S1+G1 proof sizes and worker timings; does not run proving."""
import argparse
import csv
import json
from pathlib import Path

import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--out", type=Path, default=Path(__file__).with_name("proof-bytes.png"))
parser.add_argument("--timing-out", type=Path, default=Path(__file__).with_name("worker-timing.png"))
args = parser.parse_args()
rows = json.loads(Path(__file__).with_name("proof-geometry.json").read_text())
sizes = {row["object"]: row["proofBytes"] for row in rows}
values = [sizes["sphincs-parent"], sizes["generic-parent"],
          sizes["sphincs-parent"] + sizes["generic-parent"], sizes["mixed-root"]]
labels = ["SPHINCS parent", "Generic STARK parent", "Separate parents: sum", "Unified mixed root"]
fig, ax = plt.subplots(figsize=(6.4, 4.2), dpi=180)
ax.barh(labels, [value / 1024 for value in values], color=["#6f879e", "#6f879e", "#a7b3bf", "#16796e"])
ax.invert_yaxis()
ax.set_xlim(0, max(values) / 1024 * 1.28)
for index, value in enumerate(values):
    ax.text(value / 1024 + 8, index, f"{value / 1024:,.1f} KiB", va="center", fontsize=10)
ax.set_xlabel("Serialized proof envelope size (KiB)")
ax.set_title("Offline S1 + G1 proof sizes", loc="left", fontsize=15, pad=27)
ax.text(0, 1.04, "Linux x86_64 · ABI 5 · one worker · one capture", transform=ax.transAxes, fontsize=9)
ax.xaxis.grid(True, alpha=.18)
ax.set_axisbelow(True)
for side in ("top", "right", "left"):
    ax.spines[side].set_visible(False)
ax.tick_params(axis="y", length=0)
fig.text(.04, .02, "Proof preparation excluded from merge timing. This is not a throughput test.", fontsize=8)
fig.tight_layout(rect=(0, .05, 1, 1))
fig.savefig(args.out, metadata={"Software": "Matplotlib"})
plt.close(fig)

root = Path(__file__).parent
captures = [root, root / "two-workers"]
provenance = [json.loads((capture / "provenance.json").read_text()) for capture in captures]
measurements = []
for capture, evidence in zip(captures, provenance):
    with (capture / "mixed-merge.csv").open(newline="") as stream:
        row, = list(csv.DictReader(stream))
    assert row["mode"] == "mixed-merge" and row["sphincs_claims"] == row["generic_claims"] == "1"
    assert int(row["proof_bytes"]) == sizes["mixed-root"]
    assert float(row["proving_ms"]) == evidence["mergeProcess"]["provingMilliseconds"]
    measurements.append(evidence["mergeProcess"])
for artifact in ("input.bin", "sphincs-parent.bin", "generic-parent.bin", "mixed-root.bin"):
    assert provenance[0]["publicArtifactSha256"][artifact] == provenance[1]["publicArtifactSha256"][artifact]
assert provenance[0]["resourceProbeSha256"] == provenance[1]["resourceProbeSha256"]
assert [evidence["workerCount"] for evidence in provenance] == [1, 2]

fig, (timing, memory) = plt.subplots(2, 1, figsize=(6.4, 5.6), dpi=180,
                                    gridspec_kw={"height_ratios": [1.7, 1]})
positions = [0, 1]
wall = [row["wallSeconds"] for row in measurements]
proving = [row["provingMilliseconds"] / 1000 for row in measurements]
timing.barh([pos - .16 for pos in positions], wall, height=.28, color="#6f879e", label="Process wall")
timing.barh([pos + .16 for pos in positions], proving, height=.28, color="#16796e", label="Proving call")
for pos, wall_value, proving_value in zip(positions, wall, proving):
    timing.text(wall_value + .35, pos - .16, f"{wall_value:.2f} s", va="center", fontsize=9)
    timing.text(proving_value + .35, pos + .16, f"{proving_value:.2f} s", va="center", fontsize=9)
timing.set_yticks(positions, ["1 worker", "2 workers"])
timing.invert_yaxis()
timing.set_xlim(0, max(wall) * 1.24)
timing.set_xlabel("Elapsed seconds")
timing.axvline(12, linestyle=":", color="#b06030", linewidth=1.3)
timing.text(12.35, .48, "12 s slot reference", color="#9b5026", fontsize=8)
timing.legend(loc="lower right", fontsize=8, frameon=False)
timing.set_title("Offline S1 + G1: worker comparison", loc="left", fontsize=15, pad=28)
timing.text(0, 1.10, "Same input and executable hashes · one capture per setting", transform=timing.transAxes, fontsize=9)
rss = [row["maximumResidentKiB"] / 1024 ** 2 for row in measurements]
memory.barh(positions, rss, height=.44, color=["#6f879e", "#16796e"])
memory.set_yticks(positions, ["1 worker", "2 workers"])
memory.invert_yaxis()
memory.set_xlim(0, max(rss) * 1.28)
for pos, value in zip(positions, rss):
    memory.text(value + .04, pos, f"{value:.3f} GiB", va="center", fontsize=9)
memory.set_xlabel("Whole-process peak RSS (GiB)")
for axis in (timing, memory):
    axis.xaxis.grid(True, alpha=.18)
    axis.set_axisbelow(True)
    for side in ("top", "right", "left"):
        axis.spines[side].set_visible(False)
    axis.tick_params(axis="y", length=0)
fig.text(.04, .02, "Offline only. Preparation excluded. No throughput or slot-time guarantee.", fontsize=8)
fig.tight_layout(rect=(0, .05, 1, 1))
fig.savefig(args.timing_out, metadata={"Software": "Matplotlib"})
plt.close(fig)
