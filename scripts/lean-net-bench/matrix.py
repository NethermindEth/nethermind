#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only
"""Runs the EIP-8437 RLPx-vs-ethp2p scenario matrix through netbench.py, appending one JSON line per run.

Runs already present in the results file are skipped, so an interrupted matrix resumes where it stopped.
Usage: matrix.py --test-dll <dll> --dotnet <dotnet> --results results.jsonl [--topologies chain4,cube8,pair] [--match k3]
"""
import argparse
import json
import os
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))

# name: (kind, proof bytes, measured objects, interval ms, burst, origins, broadcast-capable)
WORKLOADS = {
    "k1s": (1, 270_000, 20, 800, 1, "0", False),       # kind-1 wrapper, ~270 KB recursive proof
    "k1l": (1, 1_044_480, 15, 1500, 1, "0", False),    # kind-1 wrapper, ~1 MiB
    "k2": (2, 300_000, 20, 800, 1, "0", True),         # kind-2 block-proof sidecar, ~300 KB
    "k3": (3, 300_000, 20, 800, 1, "0", True),         # kind-3 inclusion-list package, ~300 KB
    "k3b": (3, 300_000, 32, 3000, 16, "all", True),    # 16 packages released at once from every node in turn
}
MODES = ["rlpx", "ethp2p", "bcast"]


def scenarios(topologies, rtts, losses, workloads):
    for topology in topologies:
        for rtt in rtts:
            for loss in losses:
                for workload in workloads:
                    kind, proof, objects, interval, burst, origins, broadcast = WORKLOADS[workload]
                    for mode in MODES:
                        if mode == "bcast" and not broadcast:
                            continue
                        label = f"{topology}-{workload}-{mode}-r{rtt}-l{loss:g}"
                        args = ["--topology", topology, "--binding", "rlpx" if mode == "rlpx" else "ethp2p", "--kind", str(kind),
                                "--proof-bytes", str(proof), "--objects", str(objects), "--interval-ms", str(interval),
                                "--burst", str(burst), "--origins", origins, "--rtt-ms", str(rtt), "--loss", str(loss), "--label", label]
                        if mode == "bcast":
                            args.append("--broadcast")
                        yield label, args


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--test-dll", required=True)
    parser.add_argument("--dotnet", default="dotnet")
    parser.add_argument("--results", required=True)
    parser.add_argument("--out", default="runs")
    parser.add_argument("--topologies", default="chain4,cube8,pair")
    parser.add_argument("--rtts", default="0,20,50,100")
    parser.add_argument("--losses", default="0,1")
    parser.add_argument("--workloads", default=",".join(WORKLOADS))
    parser.add_argument("--match", default="")
    parser.add_argument("--extra", default="", help="extra netbench.py arguments, space separated")
    parser.add_argument("--suffix", default="")
    args = parser.parse_args()

    done = set()
    if os.path.exists(args.results):
        with open(args.results) as f:
            for line in f:
                try:
                    entry = json.loads(line)
                except json.JSONDecodeError:
                    continue
                if not entry.get("error") and entry.get("missing", 1) == 0:
                    done.add(entry["label"])

    plan = list(scenarios(args.topologies.split(","), [float(x) for x in args.rtts.split(",")], [float(x) for x in args.losses.split(",")],
                          args.workloads.split(",")))
    for number, (label, run_args) in enumerate(plan, 1):
        label += args.suffix
        if args.match not in label or label in done:
            continue
        run_args[run_args.index("--label") + 1] = label
        command = [sys.executable, os.path.join(HERE, "netbench.py"), "run", "--netns", "--test-dll", args.test_dll, "--dotnet", args.dotnet,
                   "--out", args.out, "--results", args.results, "--timeout", "150"] + run_args + args.extra.split()
        print(f"[{number}/{len(plan)}] {label}", flush=True)
        result = subprocess.run(command, capture_output=True, text=True)
        if result.returncode != 0:
            print(result.stderr[-2000:], flush=True)
            continue
        summary = json.loads(result.stdout.strip().split("\n")[-1])
        latency = summary.get("latency_ms") or {}
        print(f"    p50={latency.get('p50')} p95={latency.get('p95')} missing={summary.get('missing')} error={summary.get('error')}", flush=True)


if __name__ == "__main__":
    main()
