#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only
"""Bounded EIP-8437 transport matrix; synthetic proofs, real ETH recovery and network bindings."""
import argparse
import json
import os
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))


def scenarios(proof_bytes, proof_sizes):
    for binding in ("rlpx", "ethp2p"):
        for transactions in (1, 16, 64):
            for rate in (1, 8, 32, 64):
                yield f"load-{binding}-tx{transactions}-r{rate}", dict(binding=binding, proof_bytes=proof_sizes.get(str(transactions), proof_bytes),
                    transactions_per_object=transactions, objects=6 * rate, burst=rate, interval_ms=1000)
        for size in (65536, 1048576, 8388608):
            yield f"size-{binding}-{size}", dict(binding=binding, proof_bytes=size, objects=6,
                burst=1, interval_ms=1000)
        for topology, rate, rtt, loss in (("pair", 10, 100, 0), ("pair", 100, 50, 1), ("chain4", 100, 50, 0)):
            yield f"link-{binding}-{topology}-m{rate}-d{rtt}-l{loss}", dict(binding=binding, proof_bytes=proof_bytes,
                objects=24, burst=4, interval_ms=1000, topology=topology, rate_mbit=rate, rtt_ms=rtt, loss=loss)
    for loss in (0, 1):
        yield f"broadcast-ethp2p-l{loss}", dict(binding="ethp2p", kind=2, broadcast=True,
            proof_bytes=proof_bytes, objects=3, warmup=1, interval_ms=12000, burst=1, loss=loss)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--test-dll", required=True)
    parser.add_argument("--dotnet", default="dotnet")
    parser.add_argument("--results", required=True)
    parser.add_argument("--out", default="runs")
    parser.add_argument("--repetitions", type=int, default=2)
    parser.add_argument("--proof-bytes", type=int, default=300000)
    parser.add_argument("--proof-sizes", help="JSON mapping of transaction counts to measured native proof sizes")
    parser.add_argument("--match", default="")
    parser.add_argument("--plan", action="store_true")
    args = parser.parse_args()
    proof_sizes = json.loads(open(args.proof_sizes).read()) if args.proof_sizes else {}
    done = set()
    if os.path.exists(args.results):
        with open(args.results) as stream:
            for line in stream:
                row = json.loads(line)
                done.add(row["label"])
    failures = 0
    for repetition in range(1, args.repetitions + 1):
        for name, scenario in scenarios(args.proof_bytes, proof_sizes):
            label = f"{name}-rep{repetition}"
            if args.match not in label or label in done:
                continue
            config = dict(topology="pair", kind=1, warmup=2, rtt_ms=50, loss=0, rate_mbit=100, timeout=90)
            config.update(scenario)
            command = [sys.executable, os.path.join(HERE, "netbench.py"), "run", "--netns", "--test-dll", args.test_dll,
                "--dotnet", args.dotnet, "--out", args.out, "--results", args.results, "--label", label]
            for key, value in config.items():
                command.append("--" + key.replace("_", "-"))
                if value is not True:
                    command.append(str(value))
            print(label, flush=True)
            if args.plan:
                print(" ".join(command))
                continue
            result = subprocess.run(command, capture_output=True, text=True)
            if result.returncode:
                failures += 1
                print(result.stderr[-3000:], flush=True)
                continue
            row = json.loads(result.stdout.strip().splitlines()[-1])
            failures += bool(row.get("error") or row.get("missing"))
            print(json.dumps({key: row.get(key) for key in ("latency_ms", "goodput_tx_per_s_per_receiver", "missing", "error")}), flush=True)
    return int(failures != 0)


if __name__ == "__main__":
    sys.exit(main())
