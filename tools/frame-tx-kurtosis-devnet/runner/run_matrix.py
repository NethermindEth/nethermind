#!/usr/bin/env python3
"""Runs scenarios over ceiling x attacker role, and checks each one.

    runner/run_matrix.py --list                                   # the plan, nothing runs
    runner/run_matrix.py --ceilings 235800 --roles signature-stuffed
    runner/run_matrix.py --standard --groth16-artifacts ~/frame-verify-gas-v2

Each scenario is followed by runner/check_results.py; the summary records both the scenario's
exit code and whether its checks passed. Scenarios at one ceiling run together, because the
ceiling is the only axis that needs different client images.

Results default to ~/frame-tx-devnet-results: `kurtosis run .` uploads the whole package
folder, so results kept inside it would be re-uploaded by every later scenario.
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

CEILINGS = [100000, 235800, 250000, 300000, 400000, 500000]
ROLES = ["keccak-wide", "signature-stuffed", "soispoke-groth16"]


def scenario_id(ceiling: int, role: str, rate: float) -> str:
    return "c{0}-{1}-a{2:g}".format(ceiling, role, rate)


def run_one(ceiling: int, role: str, args) -> dict:
    sid = scenario_id(ceiling, role, args.attacker_rate)
    cmd = [
        sys.executable, os.path.join(HERE, "run_scenario.py"),
        "--ceiling", str(ceiling),
        "--attacker-role", role,
        "--attacker-rate", str(args.attacker_rate),
        "--baseline-rate", str(args.baseline_rate),
        "--warmup", str(args.warmup),
        "--duration", str(args.duration),
        "--scenario-id", sid,
        "--results-dir", args.results_dir,
    ]
    if args.groth16_artifacts:
        cmd.extend(["--groth16-artifacts", args.groth16_artifacts])

    started = time.time()
    print("\n=== {0} ===".format(sid), flush=True)
    exit_code = subprocess.run(cmd).returncode
    checks = subprocess.run(
        [sys.executable, os.path.join(HERE, "check_results.py"), os.path.join(args.results_dir, sid), role]
    ).returncode
    return {
        "scenario_id": sid,
        "ceiling": ceiling,
        "role": role,
        "attacker_rate": args.attacker_rate,
        "exit_code": exit_code,
        "checks_passed": checks == 0,
        "started_at": datetime.fromtimestamp(started, timezone.utc).isoformat(),
        "seconds": round(time.time() - started, 1),
    }


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--standard", action="store_true", help="every campaign ceiling and role")
    parser.add_argument("--ceilings", type=int, nargs="+", default=None)
    parser.add_argument("--roles", nargs="+", default=None, choices=ROLES)
    parser.add_argument("--attacker-rate", type=float, default=25.0)
    parser.add_argument("--baseline-rate", type=float, default=2.0)
    parser.add_argument("--warmup", type=float, default=30.0)
    parser.add_argument("--duration", type=float, default=240.0)
    parser.add_argument("--groth16-artifacts", default="")
    parser.add_argument("--results-dir", default=os.path.expanduser("~/frame-tx-devnet-results"))
    parser.add_argument("--list", action="store_true", help="print the plan and stop")
    parser.add_argument("--continue-on-error", action="store_true")
    args = parser.parse_args(argv if argv is not None else sys.argv[1:])

    ceilings = args.ceilings or (CEILINGS if args.standard else [235800])
    roles = args.roles or (ROLES if args.standard else ["keccak-wide"])
    if "soispoke-groth16" in roles and not args.groth16_artifacts and not args.list:
        parser.error("soispoke-groth16 needs --groth16-artifacts")

    plan = [(ceiling, role) for ceiling in ceilings for role in roles]
    print("{0} scenario(s) planned".format(len(plan)))
    for ceiling, role in plan:
        print("  {0}".format(scenario_id(ceiling, role, args.attacker_rate)))
    if args.list:
        return 0

    os.makedirs(args.results_dir, exist_ok=True)
    summary = []
    for ceiling, role in plan:
        outcome = run_one(ceiling, role, args)
        summary.append(outcome)
        if (outcome["exit_code"] != 0 or not outcome["checks_passed"]) and not args.continue_on_error:
            break

    failed = [o["scenario_id"] for o in summary if o["exit_code"] != 0 or not o["checks_passed"]]
    path = os.path.join(args.results_dir,
                        "matrix-{0}.json".format(datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ")))
    with open(path, "w") as handle:
        json.dump({"planned": len(plan), "ran": len(summary), "failed": failed, "scenarios": summary},
                  handle, indent=2, sort_keys=True)
        handle.write("\n")
    print("\nmatrix summary: {0}\nfailed: {1}".format(path, failed or "none"))
    return 1 if failed else 0


if __name__ == "__main__":
    raise SystemExit(main())
