#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only
"""Summarizes matrix results (JSONL from netbench.py) as Markdown tables, one per topology and workload.

Usage: report.py results.jsonl [--suffix ""] > report.md
"""
import argparse
import json
import re

BODY = {"k1s": 270_265, "k1l": 1_044_745, "k2": 300_008, "k3": 300_265, "k3b": 300_265}


def load(path, suffix):
    runs = {}
    with open(path) as f:
        for line in f:
            entry = json.loads(line)
            label = entry["label"]
            if suffix and not label.endswith(suffix):
                continue
            if not suffix and re.search(r"-l[0-9.]+$", label) is None:
                continue
            runs[label] = entry  # the latest run of a label wins
    return runs


def fmt(value, digits=0):
    return "—" if value is None else f"{value:,.{digits}f}"


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("results")
    parser.add_argument("--suffix", default="")
    args = parser.parse_args()
    runs = load(args.results, args.suffix)
    keys = {}
    for label, entry in runs.items():
        topology, workload, mode, rtt, loss = re.match(r"(\w+)-(\w+)-(\w+)-r([0-9.]+)-l([0-9.]+)", label).groups()
        keys.setdefault((topology, workload), {})[(float(rtt), float(loss), mode)] = entry
    for (topology, workload), cells in sorted(keys.items()):
        print(f"\n### {topology} / {workload}\n")
        print("| RTT ms | loss % | mode | p50 ms | p95 ms | p99 ms | wire B/obj (x body/receiver) | CPU ms/obj (net) | alloc MB/obj | peak RSS MB | via broadcast | missing |")
        print("|---|---|---|---|---|---|---|---|---|---|---|---|")
        for (rtt, loss, mode), e in sorted(cells.items(), key=lambda kv: (kv[0][0], kv[0][1], ["rlpx", "ethp2p", "bcast"].index(kv[0][2]))):
            lat = e.get("latency_ms") or {}
            objects = e["objects"]
            receivers = e["nodes"] - 1
            wire = e.get("wire_bytes")
            per_object = wire / objects if wire else None
            ratio = per_object / (BODY[workload] * receivers) if per_object else None
            transfers = e.get("transfers", {})
            via = sum(v for k, v in transfers.items() if k.endswith("broadcast"))
            total = sum(transfers.values())
            broadcast_share = f"{via / total:.0%}" if mode == "bcast" and total else ""
            print(f"| {rtt:g} | {loss:g} | {mode} | {fmt(lat.get('p50'))} | {fmt(lat.get('p95'))} | {fmt(lat.get('p99'))} | "
                  f"{fmt(per_object)} ({fmt(ratio, 2)}) | {fmt(e.get('cpu_ms_per_object'))} ({fmt(e.get('cpu_net_ms_per_object'))}) | "
                  f"{fmt(e.get('alloc_mb_per_object'), 1)} | {fmt(e.get('peak_rss_mb_max'))} | {broadcast_share} | "
                  f"{e.get('missing', '?')}{' ' + e['error'] if e.get('error') else ''} |")


if __name__ == "__main__":
    main()
