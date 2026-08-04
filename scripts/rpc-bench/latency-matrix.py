#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only
#
# Aggregate per-run jsonbench-summary.md from single-client runs into one latency
# comparison matrix (counterpart to deep-check-compare.py's response-parity check).

import re
import sys

# "| latency p50 (ms) | 115.3 |" and "| requests | 6000 |" / "| checks passed | 98.0% |"
_LAT = re.compile(r"\|\s*latency\s+(\w+)\s*\(ms\)\s*\|\s*([0-9.]+)\s*\|")
_KV = re.compile(r"\|\s*([\w ]+?)\s*\|\s*([0-9.]+%?)\s*\|")
COLS = ["p50", "p90", "p95", "p99"]


def parse(path):
    row = {}
    with open(path, encoding="utf-8", errors="replace") as f:
        for line in f:
            m = _LAT.search(line)
            if m:
                row[m.group(1)] = float(m.group(2))
                continue
            m = _KV.search(line)
            if m:
                key = m.group(1).strip().lower()
                if key == "requests":
                    row["reqs"] = m.group(2)
                elif key == "checks passed":
                    row["checks"] = m.group(2)
    return row


def main(argv):
    if len(argv) < 2:
        print("usage: latency-matrix.py <label>=<jsonbench-summary.md> ...", file=sys.stderr)
        return 2

    rows = []
    for arg in argv[1:]:
        if "=" not in arg:
            print(f"bad arg (expected label=path): {arg}", file=sys.stderr)
            return 2
        label, path = arg.split("=", 1)
        try:
            rows.append((label, parse(path)))
        except FileNotFoundError:
            rows.append((label, {"_missing": True}))

    label_w = max((len(l) for l, _ in rows), default=5)
    header = f"| {'run'.ljust(label_w)} | " + " | ".join(f"{c} (ms)".rjust(9) for c in COLS) + " |  checks | reqs |"
    sep = f"|{'-' * (label_w + 2)}|" + "|".join("-" * 12 for _ in COLS) + "|--------:|-----:|"
    print(header)
    print(sep)
    best = {c: None for c in COLS}
    for _, r in rows:
        for c in COLS:
            v = r.get(c)
            if isinstance(v, float) and (best[c] is None or v < best[c]):
                best[c] = v
    for label, r in rows:
        if r.get("_missing"):
            print(f"| {label.ljust(label_w)} | " + " | ".join("  MISSING".rjust(9) for _ in COLS) + " |       - |    - |")
            continue
        cells = []
        for c in COLS:
            v = r.get(c)
            s = f"{v:.2f}" if isinstance(v, float) else "-"
            # mark the best (lowest) value per column with a *
            if isinstance(v, float) and best[c] is not None and abs(v - best[c]) < 1e-9:
                s += "*"
            cells.append(s.rjust(9))
        print(f"| {label.ljust(label_w)} | " + " | ".join(cells) + f" | {str(r.get('checks','-')).rjust(7)} | {str(r.get('reqs','-')).rjust(4)} |")
    print("\n* = lowest (best) in column")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
