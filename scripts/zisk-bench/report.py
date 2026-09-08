#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

"""Render the guest cost table, and the comparison against master when a baseline exists.

Every figure here is exact: ziskemu is deterministic, so an unchanged build reproduces its step count
to the digit. There is therefore no noise threshold to apply and no run-to-run spread to report — a
non-zero delta means the guest really does execute differently.
"""

import argparse
import json
import pathlib
import sys

MARKER = "<!-- zisk-guest-benchmark-report -->"
# Deltas are exact, so this exists to keep the summary readable rather than to filter noise: a
# rounding-level change is reported as a number but not called out as a regression. Testing it per
# block also covers the totals row, whose delta is a cost-weighted mean and so never the larger one.
NOTABLE = 0.05


def load(path: pathlib.Path) -> tuple[dict[str, dict], str]:
    """Rows keyed by block file name, and the commit they were measured at if the file records one.

    A staged baseline is `{"commit": ..., "rows": [...]}`; a freshly measured file is the bare array
    `parse-stats.py` appends to.
    """
    document = json.loads(path.read_text(encoding="utf-8"))
    rows = document["rows"] if isinstance(document, dict) else document
    commit = document.get("commit", "") if isinstance(document, dict) else ""
    return {row["input"]: row for row in rows}, commit


def pct(current: int, before: int) -> float:
    return 0.0 if before == 0 else (current - before) / before * 100.0


def sign(value: float) -> str:
    return f"{value:+.3f}%"


def block(name: str) -> str:
    """`25532382.ssz` reads as a block number, which is what a reviewer recognises."""
    return name.removesuffix(".ssz")


def render(
    current: dict[str, dict],
    baseline: dict[str, dict] | None,
    commit: str,
    baseline_commit: str = "",
    base_commit: str = "",
) -> tuple[str, bool]:
    lines = [MARKER, "## Stateless guest cost", ""]
    regressed = False

    if baseline is None:
        lines += [
            "No baseline was restored, so this run only records where the guest stands. Pull requests "
            "compare against the baseline stored by the most recent `master` run that completed one.",
            "",
            "| block | steps | prover cost |",
            "|---|---:|---:|",
        ]
        for name, row in sorted(current.items()):
            lines.append(f"| {block(name)} | {row['steps']:,} | {row['total']:,} |")
    else:
        missing = sorted(set(current) - set(baseline))
        removed = sorted(set(baseline) - set(current))
        lines += [
            "| block | steps | Δ steps | prover cost | Δ cost |",
            "|---|---:|---:|---:|---:|",
        ]
        for name, row in sorted(current.items()):
            before = baseline.get(name)
            if before is None:
                lines.append(f"| {block(name)} | {row['steps']:,} | new | {row['total']:,} | new |")
                continue

            step_delta = pct(row["steps"], before["steps"])
            cost_delta = pct(row["total"], before["total"])
            regressed = regressed or cost_delta > NOTABLE
            lines.append(
                f"| {block(name)} | {row['steps']:,} | {sign(step_delta)} "
                f"| {row['total']:,} | {sign(cost_delta)} |"
            )

        shared = [(row, baseline[name]) for name, row in current.items() if name in baseline]
        if shared:
            steps_now = sum(row["steps"] for row, _ in shared)
            steps_was = sum(before["steps"] for _, before in shared)
            cost_now = sum(row["total"] for row, _ in shared)
            cost_was = sum(before["total"] for _, before in shared)
            lines.append(
                f"| **all {len(shared)} blocks** | **{steps_now:,}** | **{sign(pct(steps_now, steps_was))}** "
                f"| **{cost_now:,}** | **{sign(pct(cost_now, cost_was))}** |"
            )

            # The buckets are the point of tracking cost as well as steps: steps track MAIN and
            # OPCODES closely and say nothing at all about PRECOMPILES or MEMORY.
            lines += ["", "<details><summary>Cost by bucket, summed over all blocks</summary>", ""]
            lines += ["| bucket | before | after | Δ |", "|---|---:|---:|---:|"]
            for bucket in ("main", "opcodes", "precompiles", "memory"):
                was = sum(before[bucket] for _, before in shared)
                now = sum(row[bucket] for row, _ in shared)
                lines.append(f"| {bucket.upper()} | {was:,} | {now:,} | {sign(pct(now, was))} |")
            lines += ["", "</details>"]

        if missing:
            lines += ["", f"Blocks absent from the baseline: {', '.join(block(n) for n in missing)}."]
        if removed:
            lines += [
                "",
                "Blocks the baseline measured but this run did not: "
                f"{', '.join(block(n) for n in removed)}. They are in neither the table nor the totals.",
            ]

    lines += [
        "",
        f"`{commit[:12]}` · {len(current)} pinned blocks · ziskemu is deterministic, so these figures "
        "are exact and any non-zero delta is real.",
    ]

    if baseline is not None:
        # A delta is only interpretable once the report names what it is a delta against:
        # `restore-keys` will hand a pull request an older master's cache when its own base never
        # stored one, and that difference is invisible in the numbers.
        against = f"`{baseline_commit[:12]}`" if baseline_commit else "an unrecorded master commit"
        lines += ["", f"Compared against {against}."]
        if base_commit and baseline_commit and baseline_commit != base_commit:
            lines[-1] += (
                f" That is not this pull request's base (`{base_commit[:12]}`), so part of any delta "
                "may belong to master commits in between."
            )

    if regressed:
        lines.insert(2, "⚠️ **Prover cost is up against the baseline.**")

    return "\n".join(lines) + "\n", regressed


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--current", required=True, type=pathlib.Path)
    parser.add_argument("--baseline", type=pathlib.Path)
    parser.add_argument("--commit", default="", help="The commit being measured.")
    parser.add_argument(
        "--base-commit",
        default="",
        help="The pull request's base commit, to flag a baseline restored from some other master commit.",
    )
    parser.add_argument("--summary", type=pathlib.Path, help="Appended to, typically GITHUB_STEP_SUMMARY.")
    parser.add_argument("--github-output", type=pathlib.Path, help="Where to write the report output.")
    args = parser.parse_args()

    # The table uses Δ, and a Windows console defaults to a codepage that cannot encode it. Files are
    # always written UTF-8; this only keeps the echo to stdout from throwing.
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")

    current, _ = load(args.current)
    if not current:
        raise SystemExit("no measurements to report")

    baseline, baseline_commit = (
        load(args.baseline) if args.baseline and args.baseline.exists() else (None, "")
    )
    report, regressed = render(current, baseline, args.commit, baseline_commit, args.base_commit)

    print(report)
    if args.summary:
        with args.summary.open("a", encoding="utf-8") as handle:
            handle.write(report)
    if args.github_output:
        with args.github_output.open("a", encoding="utf-8") as handle:
            handle.write(f"regressed={str(regressed).lower()}\n")
            handle.write(f"report<<ZISK_REPORT_EOF\n{report}\nZISK_REPORT_EOF\n")
    return 0


if __name__ == "__main__":
    sys.exit(main())
