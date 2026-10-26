#!/usr/bin/env python3
"""Turn RESULT rows into the cost curve: block-processing delay against the VERIFY gas ceiling.

The workflow uploads `RESULT key=value` rows. That is the raw material, not the deliverable.
This prints the curve those rows describe, and refuses to plot points the harness marked invalid.

    ./curve.py results/*.txt
    ./curve.py --all results/*.txt     # include valid=no rows, marked
"""
import re
import sys
from collections import defaultdict


def rows(paths):
    for path in paths:
        try:
            with open(path) as handle:
                for line in handle:
                    if line.startswith("RESULT case="):
                        yield path.rsplit("/", 1)[-1], line.strip()
        except OSError as err:
            print(f"skipping {path}: {err}", file=sys.stderr)


def field(line, key, cast=str):
    match = re.search(rf"\b{key}=([^\s]+)", line)
    return cast(match.group(1)) if match else None


def main(argv):
    show_invalid = "--all" in argv
    paths = [a for a in argv[1:] if not a.startswith("--")]
    if not paths:
        print(__doc__)
        return 1

    # (rate, ceiling) -> list of (run, delta_us, delta_pct, valid)
    curve = defaultdict(list)
    reject = {}
    rmax = {}

    for run, line in rows(paths):
        case = field(line, "case")
        if case == "flood_delay":
            ceiling, rate = field(line, "ceiling", int), field(line, "offered_rate", int)
            curve[(rate, ceiling)].append(
                (run, field(line, "delta_p50_us", float), field(line, "delta_p50_pct", float),
                 field(line, "valid") != "no", field(line, "achieved_rate", float))
            )
        elif case == "r_max":
            rmax.setdefault((field(line, "ceiling", int)), []).append(
                (run, field(line, "r_max_sustained_tx_per_s", float), field(line, "censored") == "yes"))
        elif case == "frame_reject":
            key = (field(line, "shape"), field(line, "verify_gas", int))
            reject.setdefault(key, []).append(field(line, "submit_p50_us", float))

    if curve:
        print("\n## The curve: block-processing delay vs VERIFY gas ceiling\n")
        for rate in sorted({r for r, _ in curve}):
            print(f"  offered rate = {rate} tx/s")
            print(f"    {'ceiling':>9} {'delta p50 us':>13} {'delta %':>9}  {'achieved':>9}  source")
            for (r, ceiling) in sorted(k for k in curve if k[0] == rate):
                for run, us, pct, valid, achieved in curve[(r, ceiling)]:
                    if not valid and not show_invalid:
                        print(f"    {ceiling:>9} {'DISCARDED':>13} {'':>9}  {'':>9}  {run} (valid=no, baseline drift)")
                        continue
                    flag = "" if valid else "  <-- valid=no"
                    ach = f"{achieved:.0f}" if achieved is not None else "-"
                    print(f"    {ceiling:>9} {us:>13.1f} {pct:>8.1f}% {ach:>10}  {run}{flag}")
            missing = {100_000, 236_285, 300_000, 500_000} - {c for r, c in curve if r == rate}
            if missing:
                print(f"    missing: {', '.join(str(m) for m in sorted(missing))}")
            print()

    if rmax:
        print("## R_max: sustainable rejection rate\n")
        print(f"  {'ceiling':>9} {'tx/s':>8}  note")
        for ceiling in sorted(rmax):
            for run, value, censored in rmax[ceiling]:
                note = "lower bound, ramp did not saturate" if censored else ""
                print(f"  {ceiling:>9} {value:>8.1f}  {run} {note}")
        print()

    if reject:
        print("## t_reject: admission cost per shape\n")
        print(f"  {'shape':<16}{'ceiling':>9}{'p50 us':>10}{'runs':>6}")
        for (shape, ceiling) in sorted(reject, key=lambda k: (k[0], k[1])):
            samples = reject[(shape, ceiling)]
            median = sorted(samples)[len(samples) // 2]
            print(f"  {shape:<16}{ceiling:>9}{median:>10.1f}{len(samples):>6}")
        print()

    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
