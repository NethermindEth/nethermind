#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only
"""Summarizes matrix results (JSONL from netbench.py) as Markdown tables, one per topology and workload.

Each argument is a results file, optionally with the label suffix its runs carry (file:suffix). Runs of the same scenario
from several files are repetitions: a cell shows the median across them and, with more than one, the min-max range.
A last table gives each scenario's p50 ratio of ethp2p retrieval and of broadcast to RLPx.

Usage: report.py results.jsonl [results-rep2.jsonl:-rep2 ...] > report.md
"""
import argparse
import json
import re
import statistics

BODY = {"k1s": 270_265, "k1l": 1_044_745, "k2": 300_008, "k3": 300_265, "k3b": 300_265, "k3b1": 300_265}
MODES = ["rlpx", "ethp2p", "bcast"]
LABEL = re.compile(r"(\w+)-(\w+)-(rlpx|ethp2p|bcast)-r([0-9.]+)-l([0-9.]+)$")


def load(specs):
    cells = {}
    for spec in specs:
        path, _, suffix = spec.partition(":")
        latest = {}
        with open(path) as f:
            for line in f:
                entry = json.loads(line)
                label = entry["label"]
                if suffix:
                    if not label.endswith(suffix):
                        continue
                    label = label[: -len(suffix)]
                if LABEL.match(label):
                    latest[label] = entry  # the latest run of a label in a file wins
        for label, entry in latest.items():
            topology, workload, mode, rtt, loss = LABEL.match(label).groups()
            cells.setdefault((topology, workload), {}).setdefault((float(rtt), float(loss), mode), []).append(entry)
    return cells


def fmt(value, digits=0):
    return "—" if value is None else f"{value:,.{digits}f}"


def spread(values, digits=0):
    values = [v for v in values if v is not None]
    if not values:
        return "—"
    median = statistics.median(values)
    if len(values) == 1 or max(values) == min(values):
        return fmt(median, digits)
    return f"{fmt(median, digits)} ({fmt(min(values), digits)}–{fmt(max(values), digits)})"


def median(values):
    values = [v for v in values if v is not None]
    return statistics.median(values) if values else None


def latency(entry, q):
    # A run that lost objects has no meaningful farthest-node percentile over the objects it did deliver.
    return None if entry.get("missing") or entry.get("error") else (entry.get("latency_ms") or {}).get(q)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("results", nargs="+")
    args = parser.parse_args()
    cells = load(args.results)
    ratios = {}
    for (topology, workload), scenario in sorted(cells.items()):
        print(f"\n### {topology} / {workload}\n")
        print("| RTT ms | loss % | mode | n | p50 ms | p95 ms | p99 ms | wire B/obj (x body/receiver) | CPU ms/obj | alloc MB/obj | peak RSS MB "
              "| via bcast | missing | redials | other cores |")
        print("|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|")
        for (rtt, loss, mode), runs in sorted(scenario.items(), key=lambda kv: (kv[0][0], kv[0][1], MODES.index(kv[0][2]))):
            receivers = runs[0]["nodes"] - 1
            wire = [e["wire_bytes"] / e["objects"] if e.get("wire_bytes") else None for e in runs]
            wire_median = median(wire)
            share = []
            for e in runs:
                transfers = e.get("transfers", {})
                total = sum(transfers.values())
                share.append(100 * sum(v for k, v in transfers.items() if k.endswith("broadcast")) / total if total else None)
            missing = [e.get("missing", 0) + (1000 if e.get("error") else 0) for e in runs]
            other = [(e.get("host") or {}).get("other_cores") for e in runs]
            print(f"| {rtt:g} | {loss:g} | {mode} | {len(runs)} | {spread([latency(e, 'p50') for e in runs])} | "
                  f"{spread([latency(e, 'p95') for e in runs])} | {spread([latency(e, 'p99') for e in runs])} | "
                  f"{fmt(wire_median)} ({fmt(wire_median / (BODY[workload] * receivers) if wire_median else None, 2)}) | "
                  f"{spread([e.get('cpu_ms_per_object') for e in runs])} | {fmt(median([e.get('alloc_mb_per_object') for e in runs]), 1)} | "
                  f"{fmt(median([e.get('peak_rss_mb_max') for e in runs]))} | {spread(share) + '%' if mode == 'bcast' else ''} | "
                  f"{'/'.join(str(m) for m in missing)} | {'/'.join(str(e.get('redials', '?')) for e in runs)} | "
                  f"{spread(other, 2)} |")
            ratios.setdefault((topology, workload), {}).setdefault((rtt, loss), {})[mode] = median([latency(e, "p50") for e in runs])

    columns = sorted({key for scenario in ratios.values() for key in scenario})
    print("\n### p50 relative to RLPx (ethp2p retrieval / broadcast; below 1 is faster, x marks lost objects)\n")
    print("| topology / workload | " + " | ".join(f"{rtt:g} ms {loss:g}%" for rtt, loss in columns) + " |")
    print("|---|" + "---|" * len(columns))
    for (topology, workload), scenario in sorted(ratios.items()):
        row = []
        for key in columns:
            modes = scenario.get(key, {})
            base = modes.get("rlpx")
            parts = []
            for mode in ("ethp2p", "bcast"):
                if mode not in modes:
                    continue
                value = modes[mode]
                parts.append("x" if value is None else ("rlpx x" if base is None else f"{value / base:.2f}"))
            row.append(" / ".join(parts) if parts else "")
        print(f"| {topology} / {workload} | " + " | ".join(row) + " |")


if __name__ == "__main__":
    main()
