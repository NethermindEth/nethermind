#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

"""Gate a training run using the existing EXPB log parser and explicit delivery/shutdown checks."""
import argparse
import json
from pathlib import Path
import re
import sys

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "expb"))
from sequential_driver import collect_metrics


def validate(log_path, amount, exit_code, manifest=None, delay=None):
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
    results = {}
    if manifest is None:
        reasons.append("replay manifest is required for Engine API validation")
    else:
        declared = json.loads(Path(manifest).read_text(encoding="utf-8"))
        if "startup_warmup_disabled" in declared:
            if declared["startup_warmup_disabled"] is not True:
                reasons.append("collection must disable synthetic startup warmup")
            if "Startup payload pipeline warmup " in Path(log_path).read_text(encoding="utf-8", errors="replace"):
                reasons.append("synthetic startup pipeline warmup ran during collection")
        if "delay_seconds" in declared:
            if (type(declared["delay_seconds"]) is not int or declared["delay_seconds"] not in (0, 1)
                    or type(declared.get("warmup_delay_seconds")) is not int
                    or declared["warmup_delay_seconds"] != 0):
                reasons.append("invalid collection pacing declaration")
        if delay is not None and (type(delay) is not int or delay not in (0, 1)
                                  or declared.get("delay_seconds") != delay):
            reasons.append("collection pacing does not match the requested delay")
        window = declared["replay_window"]
        expected = {(header["index"], kind): (header["index"] < window["warmup"], header["hash"].lower())
                    for header in window["headers"] for kind in ("newPayload", "forkchoiceUpdated")}
        pattern = re.compile(r"EXPB_ENGINE_RESULT idx=(\d+) warmup=([01]) kind=(newPayload|forkchoiceUpdated) "
                             r"status=VALID latest_valid_hash=(0x[0-9a-f]{64})")
        for line in Path(log_path).read_text(encoding="utf-8", errors="replace").splitlines():
            if "EXPB_ENGINE_RESULT" not in line:
                continue
            match = pattern.search(line)
            if match is None:
                reasons.append("malformed Engine API validation evidence")
                continue
            key = (int(match[1]), match[3])
            value = (match[2] == "1", match[4])
            if key in results:
                reasons.append("duplicate Engine API validation evidence")
            results[key] = value
        if window["amount"] != amount or results != expected or len(expected) != 2 * (window["warmup"] + amount):
            reasons.append("Engine API response evidence does not match the declared replay window")
        measured = {header["index"] for header in window["headers"] if header["index"] >= window["warmup"]}
        if set(metrics["payload_indices"]) != measured:
            reasons.append("delivery IDs do not match the declared replay window")
    return {"status": "failed" if reasons else "valid", "reasons": reasons,
            "metrics": metrics, "diagnostics": diagnostics, "engine_api_results": len(results)}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--log", type=Path, required=True)
    parser.add_argument("--amount", type=int, required=True)
    parser.add_argument("--exit-code", type=int, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--manifest", type=Path, required=True)
    parser.add_argument("--delay", type=int, choices=(0, 1))
    args = parser.parse_args()
    report = validate(args.log, args.amount, args.exit_code, args.manifest, args.delay)
    args.output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    if report["status"] != "valid":
        raise SystemExit(json.dumps({"reasons": report["reasons"], "diagnostics": report["diagnostics"]}))


if __name__ == "__main__":
    main()
