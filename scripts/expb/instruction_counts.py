#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

"""Per-block instruction counts of an EXPB run in the instructions mode, and their comparison with master.

A client run with NETHERMIND_COUNT_INSTRUCTIONS=1 logs one `EXPB-COUNT block=...` line per processed block: the
user-space instructions and cycles its processing thread retired, the bytes it allocated, and the split into
transaction execution, roots and commit.

  parse LOG CSV                      keep the block lines of a run log as a CSV
  report --pr FILE [--master FILE]   print the markdown section for the PR comment or the step summary

report reads either a run log (or its EXPB-COUNT lines) or a CSV written by parse.
"""

from __future__ import annotations

import argparse
import csv
import math
import re
import statistics
import sys
from pathlib import Path

ANSI = re.compile(r"\x1b\[[0-?]*[ -/]*[@-~]")
BLOCK_LINE = re.compile(r"EXPB-COUNT block=(\d+)(.*)$")
FIELD = re.compile(r"(\w+)=(\S+)")

COLUMNS = [
    "block", "txs", "gas", "instr", "cycles", "alloc",
    "exec", "roots", "commit", "exec_cycles", "roots_cycles", "commit_cycles",
    "gc", "mux", "jit",
]
INTEGER_FIELDS = ("txs", "gas", "instr", "cycles", "alloc", "exec", "roots", "commit", "mux", "jit")

# (label, column): the instruction rows of the table, the whole block first.
PHASES = [
    ("Instructions, whole block", "instr"),
    ("transaction execution", "exec"),
    ("receipts and state root", "roots"),
    ("commit", "commit"),
]

# The same split in cycles; the phases come from the `cyc=exec/roots/commit` field.
CYCLE_PHASES = [
    ("Cycles, whole block", "cycles"),
    ("transaction execution", "exec_cycles"),
    ("receipts and state root", "roots_cycles"),
    ("commit", "commit_cycles"),
]

TOP_BLOCKS = 5

# What two runs of one build showed on the EXPB runner; the note quotes it so a reader can tell a change from noise.
NOISE_NOTE = (
    "Counted on the block-processing thread in a deterministic single-threaded mode. Two runs of one build agree "
    "within 0.01% on the whole-block count and within 0.2% on 9 blocks in 10; cycles differ by up to 2%."
)


def parse_log(text: str) -> list[dict]:
    """The block lines of a run log, one row per block number (a block processed twice keeps its last line)."""
    rows: dict[int, dict] = {}
    for raw in text.splitlines():
        match = BLOCK_LINE.search(ANSI.sub("", raw))
        if not match:
            continue
        fields = dict(FIELD.findall(match.group(2)))
        if "instr" not in fields:
            continue
        row: dict = {"block": int(match.group(1))}
        for key in INTEGER_FIELDS:
            value = fields.get(key)
            row[key] = int(value) if value is not None and value.lstrip("-").isdigit() else None
        cycles = fields.get("cyc", "").split("/")
        for key, value in zip(("exec_cycles", "roots_cycles", "commit_cycles"), cycles if len(cycles) == 3 else []):
            row[key] = int(value) if value.isdigit() else None
        row["gc"] = fields.get("gc", "")
        rows[row["block"]] = row
    return [rows[number] for number in sorted(rows)]


def write_csv(rows: list[dict], path: Path) -> None:
    with path.open("w", newline="", encoding="utf-8") as handle:
        writer = csv.DictWriter(handle, fieldnames=COLUMNS)
        writer.writeheader()
        for row in rows:
            writer.writerow({key: "" if row.get(key) is None else row[key] for key in COLUMNS})


def read_csv(path: Path) -> list[dict]:
    rows = []
    with path.open(newline="", encoding="utf-8") as handle:
        for record in csv.DictReader(handle):
            row: dict = {"block": int(record["block"]), "gc": record.get("gc", "")}
            for key in COLUMNS:
                if key in ("block", "gc"):
                    continue
                value = record.get(key, "")
                row[key] = int(value) if value not in ("", None) else None
            rows.append(row)
    return rows


def load(path: Path) -> list[dict]:
    """The rows of a CSV written by parse, or of a run log."""
    text = path.read_text(encoding="utf-8", errors="replace")
    return read_csv(path) if text.startswith("block,") else parse_log(text)


def measured(rows: list[dict], count: int | None) -> list[dict]:
    """The blocks EXPB measures: the last `count` blocks by number, after the snapshot's warm-up blocks."""
    rows = sorted(rows, key=lambda row: row["block"])
    return rows[-count:] if count and count < len(rows) else rows


def is_valid(row: dict) -> bool:
    """Counts are only representative without collections or counter multiplexing in the window."""
    return row.get("gc") in ("", "0/0/0") and not row.get("mux")


def per_block(rows: list[dict], key: str) -> float | None:
    """The mean over the blocks that have the value; a block whose counter read failed lacks its phase split."""
    values = [row[key] for row in rows if row.get(key) is not None]
    return sum(values) / len(values) if values else None


def change(before: float | None, after: float | None) -> str:
    if before is None or after is None or before == 0:
        return ""
    return f"{(after - before) / before * 100:+.2f}%"


def millions(value: float | None) -> str:
    return "" if value is None else f"{value / 1e6:,.2f}M"


def megabytes(value: float | None) -> str:
    return "" if value is None else f"{value / 1e6:,.2f} MB"


def percentile(values: list[float], share: float) -> float:
    """The nearest-rank percentile: the smallest value that `share` of the values do not exceed."""
    ordered = sorted(values)
    return ordered[max(0, math.ceil(share * len(ordered)) - 1)]


def render(
    title: str,
    pr: list[dict],
    master: list[dict] | None,
    *,
    labels: tuple[str, str] = ("master", "PR"),
    baseline_missing: bool = False,
    master_sha: str = "",
    base_sha: str = "",
) -> str:
    """The markdown section: the per-block means, then the single blocks when there is something to compare with.

    `baseline_missing` says a comparison was asked for but there was no baseline to compare with.
    """
    before_label, after_label = labels
    lines = [f"#### {title}", ""]
    if not pr:
        lines.append("No instruction counts were produced: the client logged no `EXPB-COUNT` block lines.")
        return "\n".join(lines) + "\n"

    shared = bool(master)
    no_overlap = False
    if shared:
        by_block = {row["block"]: row for row in master}
        paired = [(by_block[row["block"]], row) for row in pr if row["block"] in by_block]
        if paired:
            master = [before for before, _ in paired]
            pr = [after for _, after in paired]
        else:
            # A baseline cached from other blocks, such as an older payload set, has nothing to compare with.
            shared, master, no_overlap = False, None, True

    # A window with a collection or multiplexed counters is left out on both sides, unless every block has one.
    keep = [index for index, row in enumerate(pr) if is_valid(row) and (not shared or is_valid(master[index]))]
    excluded = len(pr) - len(keep)
    if keep and excluded:
        pr = [pr[index] for index in keep]
        master = [master[index] for index in keep] if shared else master

    lines.append(NOISE_NOTE)
    lines.append("")

    if shared:
        lines.append(f"| per block, mean of {len(pr)} blocks | {before_label} | {after_label} | Δ |")
        lines.append("|---|---:|---:|---:|")
    else:
        lines.append(f"| per block, mean of {len(pr)} blocks | {after_label} |")
        lines.append("|---|---:|")

    def row(label: str, before: str, after: str, delta: str) -> None:
        lines.append(f"| {label} | {before} | {after} | {delta} |" if shared else f"| {label} | {after} |")

    for index, (label, key) in enumerate(PHASES):
        name = f"**{label}**" if index == 0 else f"&nbsp;&nbsp;{label}"
        after = per_block(pr, key)
        before = per_block(master, key) if shared else None
        row(name, millions(before), millions(after), change(before, after))

    # Cycles per phase: a phase that runs beside another thread's work can take more cycles for the same instructions.
    for index, (label, key) in enumerate(CYCLE_PHASES):
        name = f"**{label}**" if index == 0 else f"&nbsp;&nbsp;{label}"
        after = per_block(pr, key)
        before = per_block(master, key) if shared else None
        delta = change(before, after)
        row(name, millions(before), millions(after), f"{delta} (noise ±2%)" if delta and index == 0 else delta)
    alloc_after = per_block(pr, "alloc")
    alloc_before = per_block(master, "alloc") if shared else None
    row("Allocated", megabytes(alloc_before), megabytes(alloc_after), change(alloc_before, alloc_after))
    lines.append("")

    if shared:
        deltas = [
            ((after["instr"] - before["instr"]) / before["instr"], before, after)
            for before, after in zip(master, pr)
            if before["instr"] and after["instr"] is not None
        ]
        if deltas:
            magnitudes = [abs(item[0]) * 100 for item in deltas]
            lines.append(
                f"Single blocks: median change {statistics.median(magnitudes):.2f}%, "
                f"9 in 10 within {percentile(magnitudes, 0.9):.2f}%."
            )
            lines.append("")
        largest = sorted(deltas, key=lambda item: abs(item[0]), reverse=True)[:TOP_BLOCKS]
        if largest and largest[0][0] != 0:
            lines.append("<details><summary>Blocks that changed most</summary>")
            lines.append("")
            lines.append(f"| block | txs | gas | {before_label} | {after_label} | Δ |")
            lines.append("|---:|---:|---:|---:|---:|---:|")
            for _, before, after in largest:
                txs = "" if after["txs"] is None else after["txs"]
                gas = "" if after["gas"] is None else f"{after['gas']:,}"
                lines.append(
                    f"| {after['block']} | {txs} | {gas} | {millions(before['instr'])} | "
                    f"{millions(after['instr'])} | {change(before['instr'], after['instr'])} |"
                )
            lines.append("")
            lines.append("</details>")
            lines.append("")

    if excluded and keep:
        lines.append(
            f"⚠️ {excluded} block{'s' if excluded != 1 else ''} ran a garbage collection or had multiplexed counters in either run, "
            "so " + ("they are" if excluded != 1 else "it is") + " left out of the means."
        )
        lines.append("")
    elif excluded:
        lines.append("⚠️ Every block ran a garbage collection or had multiplexed counters, so these counts are not representative.")
        lines.append("")

    if shared and master_sha:
        if base_sha and master_sha != base_sha:
            lines.append(
                f"Baseline: master `{master_sha[:12]}`. The PR's base is `{base_sha[:12]}`, so commits merged in "
                "between count as part of the change."
            )
        else:
            lines.append(f"Baseline: master `{master_sha[:12]}`, the PR's base.")
        lines.append("")
    elif no_overlap:
        lines.append(
            f"The {before_label} counts share no blocks with this run, as after a change of payload set or snapshot, "
            f"so this shows {after_label} alone."
        )
        lines.append("")
    elif baseline_missing and labels == ("master", "PR"):
        lines.append(
            "No master baseline with instruction counts was cached yet, so this shows the PR run only. "
            "The next successful `master` push run creates one."
        )
        lines.append("")
    elif baseline_missing:
        lines.append(f"{before_label} has no counts to compare with, so this shows {after_label} alone.")
        lines.append("")

    return "\n".join(lines) + "\n"


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    commands = parser.add_subparsers(dest="command", required=True)

    parse = commands.add_parser("parse", help="keep a run log's block lines as a CSV")
    parse.add_argument("log", type=Path)
    parse.add_argument("csv", type=Path)

    report = commands.add_parser("report", help="print the markdown section")
    report.add_argument("--pr", type=Path, required=True, help="the run to report")
    report.add_argument("--master", type=Path, help="the run to compare with; a missing file says there is no baseline")
    report.add_argument("--title", default="Instruction counts")
    report.add_argument("--labels", default="master,PR", help="column names of the two runs, BEFORE,AFTER")
    report.add_argument("--measured", type=int, help="compare only the last N blocks, the ones EXPB measures")
    report.add_argument("--master-sha", default="")
    report.add_argument("--base-sha", default="")

    args = parser.parse_args(argv)
    if args.command == "parse":
        rows = parse_log(args.log.read_text(encoding="utf-8", errors="replace"))
        write_csv(rows, args.csv)
        print(f"{len(rows)} blocks counted")
        return 0

    before_label, _, after_label = args.labels.partition(",")
    pr = measured(load(args.pr), args.measured) if args.pr.is_file() else []
    master = measured(load(args.master), args.measured) if args.master and args.master.is_file() else None
    sys.stdout.write(
        render(
            args.title,
            pr,
            master,
            labels=(before_label or "master", after_label or "PR"),
            baseline_missing=args.master is not None and not master,
            master_sha=args.master_sha,
            base_sha=args.base_sha,
        )
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
