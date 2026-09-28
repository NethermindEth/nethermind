#!/usr/bin/env python3
"""Drives the campaign matrix: ceiling x attacker_role x K_retry, plus the privacy-inclusion
scenarios.

    runner/run_matrix.py --list                        # what would run, and in what order
    runner/run_matrix.py --standard                    # the campaign's full matrix
    runner/run_matrix.py --ceilings 235800 500000 --roles keccak-wide
    runner/run_matrix.py --privacy-only                # one privacy-inclusion run per ceiling

The Cartesian product lives here rather than in the Kurtosis package: a scenario is a set of
parameters, and generating them is a runner's job, not a network definition's.

Scenarios are ordered so every cell at one ceiling runs together, because a ceiling change is
the only axis that costs a client image rebuild.
"""
from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
import time
from datetime import datetime, timezone

HERE = os.path.dirname(os.path.abspath(__file__))
DEVNET_ROOT = os.path.dirname(HERE)

CEILINGS = [100000, 235800, 250000, 300000, 400000, 500000]
ROLES = ["keccak-wide", "signature-stuffed", "soispoke-groth16"]
K_RETRIES = [1, 2, 4, 8]


def scenario_id(ceiling: int, role: str, k_retry: int, rate: float, kind: str) -> str:
    if kind == "privacy":
        return "privacy-c{0}-{1}-k{2}".format(ceiling, role, k_retry)
    return "c{0}-{1}-k{2}-a{3}".format(ceiling, role, k_retry, int(rate))


def build_plan(args) -> list[dict]:
    plan = []
    for ceiling in args.ceilings:
        if not args.privacy_only:
            for role in args.roles:
                for k_retry in args.k_retries:
                    plan.append(
                        {
                            "kind": "matrix",
                            "ceiling": ceiling,
                            "role": role,
                            "k_retry": k_retry,
                            "attacker_rate": args.attacker_rate,
                            "duration": args.duration,
                            "privacy_inclusion": False,
                        }
                    )
        if args.privacy or args.privacy_only:
            # One privacy-inclusion scenario per ceiling, run under the attack shape that
            # exercises the same code path a real privacy transaction takes, so the question
            # it answers is "does a valid privacy transfer still land while the pool is being
            # flooded with proofs that fail".
            plan.append(
                {
                    "kind": "privacy",
                    "ceiling": ceiling,
                    "role": args.privacy_attack_role,
                    "k_retry": args.privacy_k_retry,
                    "attacker_rate": args.attacker_rate,
                    "duration": args.duration,
                    "privacy_inclusion": True,
                }
            )
    return plan


def run_one(cell: dict, args) -> dict:
    sid = scenario_id(cell["ceiling"], cell["role"], cell["k_retry"], cell["attacker_rate"], cell["kind"])
    cmd = [
        sys.executable,
        os.path.join(HERE, "run_scenario.py"),
        "--ceiling", str(cell["ceiling"]),
        "--attacker-role", cell["role"],
        "--k-retry", str(cell["k_retry"]),
        "--attacker-rate", str(cell["attacker_rate"]),
        "--baseline-rate", str(args.baseline_rate),
        "--warmup", str(args.warmup),
        "--duration", str(cell["duration"]),
        "--scenario-id", sid,
        "--results-dir", args.results_dir,
    ]
    if cell["privacy_inclusion"]:
        cmd.append("--privacy-inclusion")
    if args.groth16_artifacts:
        cmd.extend(["--groth16-artifacts", args.groth16_artifacts])
    if args.build_images:
        cmd.append("--build-images")
    if args.dry_run:
        cmd.append("--dry-run")

    started = time.time()
    print("\n=== {0} ===".format(sid), flush=True)
    result = subprocess.run(cmd)
    return {
        "scenario_id": sid,
        **{k: v for k, v in cell.items()},
        "exit_code": result.returncode,
        "started_at": datetime.fromtimestamp(started, timezone.utc).isoformat(),
        "seconds": round(time.time() - started, 1),
    }


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--standard", action="store_true",
                        help="the campaign matrix: every ceiling, role and K_retry")
    parser.add_argument("--ceilings", type=int, nargs="+", default=None)
    parser.add_argument("--roles", nargs="+", default=None, choices=ROLES)
    parser.add_argument("--k-retries", type=int, nargs="+", default=None, choices=K_RETRIES)
    parser.add_argument("--attacker-rate", type=float, default=25.0)
    parser.add_argument("--baseline-rate", type=float, default=2.0)
    parser.add_argument("--warmup", type=float, default=30.0)
    parser.add_argument("--duration", type=float, default=240.0)
    parser.add_argument("--privacy", action="store_true",
                        help="also run one privacy-inclusion scenario per ceiling")
    parser.add_argument("--privacy-only", action="store_true")
    parser.add_argument("--privacy-attack-role", default="soispoke-groth16", choices=ROLES)
    parser.add_argument("--privacy-k-retry", type=int, default=1, choices=K_RETRIES)
    parser.add_argument("--groth16-artifacts", default="")
    parser.add_argument("--results-dir", default=os.path.join(DEVNET_ROOT, "results"))
    parser.add_argument("--build-images", action="store_true")
    parser.add_argument("--list", action="store_true", help="print the plan and stop")
    parser.add_argument("--dry-run", action="store_true")
    parser.add_argument("--continue-on-error", action="store_true")
    args = parser.parse_args(argv if argv is not None else sys.argv[1:])

    if args.standard:
        args.ceilings = args.ceilings or CEILINGS
        args.roles = args.roles or ROLES
        args.k_retries = args.k_retries or K_RETRIES
        args.privacy = True
    args.ceilings = args.ceilings or [235800]
    args.roles = args.roles or ["keccak-wide"]
    args.k_retries = args.k_retries or [1]

    plan = build_plan(args)
    print("{0} scenario(s) planned".format(len(plan)))
    for cell in plan:
        print("  {0}".format(scenario_id(cell["ceiling"], cell["role"], cell["k_retry"],
                                         cell["attacker_rate"], cell["kind"])))
    if args.list:
        return 0

    os.makedirs(args.results_dir, exist_ok=True)
    summary = []
    failures = 0
    for cell in plan:
        outcome = run_one(cell, args)
        summary.append(outcome)
        if outcome["exit_code"] != 0:
            failures += 1
            print("scenario {0} exited {1}".format(outcome["scenario_id"], outcome["exit_code"]),
                  file=sys.stderr)
            if not args.continue_on_error:
                break

    path = os.path.join(
        args.results_dir,
        "matrix-{0}.json".format(datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ")),
    )
    with open(path, "w") as handle:
        json.dump({"planned": len(plan), "ran": len(summary), "failures": failures,
                   "scenarios": summary}, handle, indent=2, sort_keys=True)
        handle.write("\n")
    print("\nmatrix summary: {0}".format(path))
    return 1 if failures else 0


if __name__ == "__main__":
    raise SystemExit(main())
