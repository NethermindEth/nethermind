#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

"""Validate semantic dotnet-pgo dump data against explicitly required capabilities."""
import argparse
from collections import Counter
import hashlib
import json
import math
from pathlib import Path


def weight(value):
    if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(value) or value < 0:
        raise ValueError(f"invalid profile weight: {value!r}")
    return value


def method_name(record):
    name = record.get("Method") if isinstance(record, dict) else None
    if not isinstance(name, str) or not name.strip():
        raise ValueError("method record needs a nonempty Method name")
    return name


def validate(data, required=(), required_methods=()):
    required_methods = tuple(required_methods)
    methods = data.get("Methods") if isinstance(data, dict) else None
    if not isinstance(methods, list) or not methods:
        raise ValueError("profile dump contains no method records")
    names = Counter()
    kinds = Counter()
    positive_methods = set()
    calls = exclusive = positive_counts = instrumented_methods = 0
    call_total = exclusive_total = 0
    for method in methods:
        names[method_name(method)] += 1
        edges = method.get("CallWeights", [])
        if not isinstance(edges, list):
            raise ValueError("CallWeights must be an array of method/weight objects")
        for edge in edges:
            method_name(edge)
            value = weight(edge.get("Weight"))
            calls += value > 0
            call_total += value
        value = weight(method.get("ExclusiveWeight", 0))
        exclusive += value > 0
        exclusive_total += value
        schema = method.get("InstrumentationData", [])
        if not isinstance(schema, list):
            raise ValueError("InstrumentationData must be an array")
        instrumented_methods += bool(schema)
        for item in schema:
            if not isinstance(item, dict) or not isinstance(item.get("InstrumentationKind"), str):
                raise ValueError("invalid instrumentation schema record")
            kind = item["InstrumentationKind"]
            kinds[kind] += 1
            offset = item.get("ILOffset")
            if isinstance(offset, bool) or not isinstance(offset, int):
                raise ValueError("instrumentation record needs an integer ILOffset")
            if kind in ("BasicBlockIntCount", "BasicBlockLongCount", "EdgeIntCount", "EdgeLongCount"):
                positive = weight(item.get("Data")) > 0
                positive_counts += positive
                if positive:
                    positive_methods.add(method_name(method))
    capabilities = {"methods": True, "instrumentation": bool(instrumented_methods),
                    "block-counts": bool(positive_counts), "callweights": bool(calls and exclusive)}
    report = {"method_records": len(methods), "instrumented_methods": instrumented_methods,
              "instrumentation_kinds": dict(kinds), "positive_count_records": positive_counts,
              "positive_call_edges": calls, "total_call_weight": call_total,
              "methods_with_exclusive_weight": exclusive, "total_exclusive_weight": exclusive_total,
              "duplicate_display_names": {name: count for name, count in names.items() if count > 1},
              "capabilities": capabilities,
              "required_methods": list(required_methods),
              "identity_note": "Display names do not establish module/MVID identity or compiler consumption."}
    missing = [name for name in required if not capabilities.get(name)]
    if missing:
        raise ValueError(f"missing required profile capabilities: {', '.join(missing)}; report: {json.dumps(report)}")
    missing_methods = set(required_methods) - positive_methods
    if missing_methods:
        raise ValueError(f"required methods have no positive block/edge counts: {', '.join(sorted(missing_methods))}")
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dump", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--require-method", action="append", default=[],
                        help="Exact dump display name requiring positive block/edge counts; identity is validated separately.")
    parser.add_argument("--require", action="append", default=[],
                        choices=("methods", "instrumentation", "block-counts", "callweights"))
    args = parser.parse_args()
    raw = args.dump.read_bytes()
    try:
        report = validate(json.loads(raw), args.require, args.require_method)
        report["status"] = "valid"
    except (ValueError, TypeError) as error:
        report = {"status": "failed", "reason": str(error)}
    report.update({"dump_sha256": hashlib.sha256(raw).hexdigest(), "required": args.require})
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    if report["status"] != "valid":
        raise SystemExit(report["reason"])


if __name__ == "__main__":
    main()
