#!/usr/bin/env python3
"""Check ZisK public outputs and reject profiles that increase emulator weighted cost."""
import argparse
import csv
import json
from pathlib import Path


def read_result(directory, expected):
    output = (directory / "output.bin").read_bytes()
    # The pinned ZisK emulator exposes 32 u64 public-output slots.
    if not expected or len(expected) > 256 or output != expected.ljust(256, b"\0"):
        raise ValueError(f"Public output mismatch: {directory}")
    metrics = {}
    with (directory / "stats.csv").open(newline="") as stream:
        for row in csv.reader(stream):
            if row and row[0] == "STEPS":
                metrics["steps"] = int(row[1])
            elif row[:2] == ["COST", "TOTAL"]:
                metrics["weighted_cost"] = int(row[2])
    if set(metrics) != {"steps", "weighted_cost"} or any(value <= 0 for value in metrics.values()):
        raise ValueError(f"Missing positive emulator metrics: {directory}")
    return metrics


def compare(baseline, candidates, expected, max_regression_percent=0):
    reference = read_result(baseline, expected)
    results = []
    for candidate in candidates:
        metrics = read_result(candidate, expected)
        change = {key + "_change_percent": 100 * (value / reference[key] - 1)
                  for key, value in metrics.items()}
        results.append({"directory": str(candidate), **metrics, **change,
                        "accepted": change["weighted_cost_change_percent"] <= max_regression_percent})
    return {"baseline": {"directory": str(baseline), **reference}, "candidates": results}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--baseline", type=Path, required=True)
    parser.add_argument("--expected-output", required=True, help="Expected public-output hex, including the success flag")
    parser.add_argument("--max-regression-percent", type=float, default=0)
    parser.add_argument("candidates", type=Path, nargs="+")
    args = parser.parse_args()
    report = compare(args.baseline, args.candidates, bytes.fromhex(args.expected_output), args.max_regression_percent)
    print(json.dumps(report, indent=2))
    raise SystemExit(0 if all(result["accepted"] for result in report["candidates"]) else 1)
