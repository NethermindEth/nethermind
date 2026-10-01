#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only
"""Compare raw Universal CPU weight with one converter's attribution accounting."""
import argparse
import hashlib
import json
import math
from pathlib import Path
import re


LABELS = {
    "total": "Universal CPU sample weight in window",
    "missing_stacks": "Universal CPU sample weight without stacks",
    "native": "Samples outside managed code",
    "missing_maps": "Samples in managed code that does not have native<->IL mappings",
    "outside_maps": "Samples in managed code with mappings that could not be correlated",
    "unknown_inlinees": "Samples in inlinees that were not present in ETW events",
    "missing_il": "Samples in managed code for which we could not get the IL",
    "outside_graph": "Samples in managed code that could not be attributed to the method's flow graph",
    "attributed": "Samples successfully attributed",
}


def single(log, label):
    values = re.findall(r"^" + re.escape(label) + r": (.+)$", log, re.MULTILINE)
    if len(values) != 1:
        raise ValueError(f"expected exactly one {label!r} diagnostic")
    return values[0].strip()


def nonnegative(value):
    if isinstance(value, bool) or not isinstance(value, int) or not 0 <= value <= 2**63 - 1:
        raise ValueError(f"invalid sample count/weight: {value!r}")
    return value


def validate(raw, log, require_managed=False):
    total = nonnegative(raw["weight"])
    records = nonnegative(raw["records"])
    process_id = nonnegative(raw["processId"])
    if process_id == 0 or int(single(log, "Universal CPU sample process")) != process_id:
        raise ValueError("raw reader and converter process IDs differ")
    for key, label in [("before", "Universal CPU sample window start"), ("after", "Universal CPU sample window end")]:
        value = raw[key]
        if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(value):
            raise ValueError("invalid raw sample window")
        if float(single(log, label)) != value:
            raise ValueError("raw reader and converter sample windows differ")
    if raw["before"] > raw["after"]:
        raise ValueError("reversed raw sample window")
    counts = {key: nonnegative(int(single(log, label))) for key, label in LABELS.items()}
    if counts["total"] != total:
        raise ValueError("raw CPU weight differs from converted CPU weight")
    if sum(value for key, value in counts.items() if key != "total") != total:
        raise ValueError("attribution buckets do not conserve raw CPU weight")
    failures = sum(counts[key] for key in ("missing_stacks", "missing_maps", "outside_maps", "unknown_inlinees", "missing_il", "outside_graph"))
    if require_managed and (counts["attributed"] == 0 or failures != 0):
        raise ValueError("managed attribution is empty or contains unresolved samples")
    return {"raw_records": records, "counts": counts,
            "scope": "Sample accounting only; does not prove capture completeness, method identity, profile smoothing or compiler consumption."}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--raw-report", type=Path, required=True)
    parser.add_argument("--converter-log", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--require-managed", action="store_true")
    args = parser.parse_args()
    raw_bytes = args.raw_report.read_bytes()
    log_bytes = args.converter_log.read_bytes()
    try:
        report = validate(json.loads(raw_bytes), log_bytes.decode("utf-8"), args.require_managed)
        report["status"] = "valid"
    except (ValueError, TypeError, KeyError) as error:
        report = {"status": "failed", "reason": str(error)}
    report.update({"raw_report_sha256": hashlib.sha256(raw_bytes).hexdigest(),
                   "converter_log_sha256": hashlib.sha256(log_bytes).hexdigest()})
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    if report["status"] != "valid":
        raise SystemExit(report["reason"])


if __name__ == "__main__":
    main()
