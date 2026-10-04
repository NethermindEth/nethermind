#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only
"""Plot the captured offline S1+G1 proof sizes; does not run proving."""
import argparse
import json
from pathlib import Path

import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--out", type=Path, default=Path(__file__).with_name("proof-bytes.png"))
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
