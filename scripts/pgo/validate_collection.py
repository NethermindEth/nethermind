#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

"""Gate a training run using the existing EXPB log parser and explicit delivery/shutdown checks."""
import argparse
import json
from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "expb"))
from sequential_driver import collect_metrics


def validate(log_path, amount, exit_code):
    metrics, diagnostics = collect_metrics(log_path)
    reasons = []
    if exit_code:
        reasons.append(f"EXPB exited {exit_code}")
    if metrics["delivered"] != amount or len(set(metrics["payload_indices"])) != amount:
        reasons.append(f"expected {amount} unique payload deliveries, got {metrics['delivered']}")
    for key in ("exception_count", "invalid_count", "severe_count"):
        if metrics[key]:
            reasons.append(f"{key}={metrics[key]}")
    if not metrics["shutdown"]:
        reasons.append("Nethermind shutdown marker is missing")
    if not metrics["cleanup"]:
        reasons.append("EXPB cleanup marker is missing")
    return {"status": "failed" if reasons else "valid", "reasons": reasons,
            "metrics": metrics, "diagnostics": diagnostics}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--log", type=Path, required=True)
    parser.add_argument("--amount", type=int, required=True)
    parser.add_argument("--exit-code", type=int, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    report = validate(args.log, args.amount, args.exit_code)
    args.output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    if report["status"] != "valid":
        raise SystemExit(json.dumps({"reasons": report["reasons"], "diagnostics": report["diagnostics"]}))


if __name__ == "__main__":
    main()
