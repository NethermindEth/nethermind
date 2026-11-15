#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

# Usage: summary.py <results dir> <markdown out> <json out>
# Reads the rows that measure.sh wrote for both arms and renders head against base per guest and
# format. Exits with 1 when a block is missing, a run failed or the arms disagree on an output.
import collections
import glob
import json
import os
import sys

UNITS = {"zisk": "steps", "sp1": "cycles", "openvm": "instret", "airbender": "cycles"}
GUESTS = [guest for guest in os.environ.get("GUESTS", "").split(",") if guest in UNITS]
FORMATS = [fmt for fmt in os.environ.get("FORMATS", "").split(",") if fmt]

rows = collections.defaultdict(dict)
for path in sorted(glob.glob(os.path.join(sys.argv[1], "**", "*.tsv"), recursive=True)):
    with open(path) as file:
        for line in file:
            parts = line.rstrip("\n").split("\t")
            if len(parts) == 8 and parts[2] in ("base", "head"):
                vm, fmt, arm, block, metric, cost, got, ok = parts
                rows[(vm, fmt)][(arm, block)] = {"metric": metric, "cost": cost, "output": got, "ok": ok == "OK"}


def num(value):
    return int(value) if value.isdigit() else None


def change(head, base):
    if head is None or base is None or base == 0:
        return None
    return {"delta": head - base, "percent": round((head - base) * 100 / base, 4)}


def cell(head, base):
    diff = change(head, base)
    return "n/a" if diff is None else f"{diff['delta']:+,} ({diff['percent']:+.3f}%)"


def show(value):
    return "n/a" if value is None else f"{value:,}"


def short(sha):
    return sha[:12] if sha else "?"


report = {
    "base": {"ref": os.environ.get("BASE_REF", ""), "sha": os.environ.get("BASE_SHA", "")},
    "head": {"ref": os.environ.get("HEAD_REF", ""), "sha": os.environ.get("HEAD_SHA", "")},
    "config": {name.lower(): os.environ.get(name, "") for name in ("BFLAT_IMAGE", "BFLAT_RISCV32_IMAGE", "EXTLIB", "CODEGENOPT")},
    "results": [],
}
lines = [
    "## zkVM guest A/B",
    "",
    f"head `{report['head']['ref']}` ({short(report['head']['sha'])}) against base `{report['base']['ref']}` ({short(report['base']['sha'])}). "
    "Negative deltas mean the head does less work.",
]
for name, value in report["config"].items():
    if value:
        lines.append(f"- {name}: `{value}`")

ok = True
verdicts = []
for vm in GUESTS:
    unit = UNITS[vm]
    for fmt in FORMATS:
        data = rows.get((vm, fmt), {})
        blocks = sorted({block for _, block in data}, key=lambda block: (len(block), block))
        has_cost = any(num(entry["cost"]) is not None for entry in data.values())
        totals = {"base": 0, "head": 0}
        costs = {"base": 0, "head": 0}
        complete = bool(blocks)
        cost_complete = bool(blocks) and has_cost
        identical = bool(blocks)
        result = {"guest": vm, "format": fmt, "unit": unit, "blocks": []}
        lines += ["", f"### {vm} {fmt}", ""]
        header = f"| block | base {unit} | head {unit} | delta |"
        rule = "|---|---:|---:|---:|"
        if has_cost:
            header += " base cost | head cost | cost delta |"
            rule += "---:|---:|---:|"
        lines += [header + " outputs |", rule + "---|"]
        for block in blocks:
            entries = {arm: data.get((arm, block)) for arm in ("base", "head")}
            metric = {arm: num(entry["metric"]) if entry else None for arm, entry in entries.items()}
            cost = {arm: num(entry["cost"]) if entry else None for arm, entry in entries.items()}
            same = all(entry and entry["ok"] for entry in entries.values()) and entries["base"]["output"] == entries["head"]["output"]
            for arm in ("base", "head"):
                if metric[arm] is None:
                    complete = False
                else:
                    totals[arm] += metric[arm]
                if cost[arm] is None:
                    cost_complete = False
                else:
                    costs[arm] += cost[arm]
            identical = identical and same
            row = f"| {block.removesuffix('.ssz')} | {show(metric['base'])} | {show(metric['head'])} | {cell(metric['head'], metric['base'])} |"
            if has_cost:
                row += f" {show(cost['base'])} | {show(cost['head'])} | {cell(cost['head'], cost['base'])} |"
            lines.append(row + f" {'identical' if same else 'DIFFERENT OR FAILED'} |")
            result["blocks"].append({
                "block": block,
                "base": metric["base"],
                "head": metric["head"],
                "change": change(metric["head"], metric["base"]),
                "base_cost": cost["base"],
                "head_cost": cost["head"],
                "cost_change": change(cost["head"], cost["base"]),
                "identical": same,
            })
        total = {arm: totals[arm] if complete else None for arm in totals}
        total_cost = {arm: costs[arm] if cost_complete else None for arm in costs}
        row = f"| **total** | {show(total['base'])} | {show(total['head'])} | {cell(total['head'], total['base'])} |"
        if has_cost:
            row += f" {show(total_cost['base'])} | {show(total_cost['head'])} | {cell(total_cost['head'], total_cost['base'])} |"
        lines.append(row + f" {'identical' if identical else 'CHECK'} |")
        result.update({
            "total": {"base": total["base"], "head": total["head"], "change": change(total["head"], total["base"])},
            "total_cost": {"base": total_cost["base"], "head": total_cost["head"], "change": change(total_cost["head"], total_cost["base"])} if has_cost else None,
            "complete": complete,
            "identical": identical,
        })
        report["results"].append(result)
        verdicts.append(f"- {vm} {fmt}: {len(blocks)} blocks, {'all runs OK, outputs identical' if identical and complete else 'a run failed, a block is missing or the outputs differ'}")
        ok = ok and identical and complete

lines += ["", "### Output check", ""] + verdicts
report["ok"] = ok
markdown = "\n".join(lines) + "\n"
print(markdown)
with open(sys.argv[2], "w") as file:
    file.write(markdown)
with open(sys.argv[3], "w") as file:
    json.dump(report, file, indent=2)
    file.write("\n")
sys.exit(0 if ok else 1)
