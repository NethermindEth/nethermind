#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

"""Sanitize k6 summaries and stage aggregate-only results for private corpus runs.

Privacy contract: `sanitize` copies only a fixed set of numeric aggregates out of a raw
k6 summary.json (which can embed request URLs and check names); `stage` copies only
validated aggregate/parity files into the directory that gets uploaded as the artifact.
"""

from __future__ import annotations

import argparse
import csv
import json
import re
import math
import shutil
import sys
from pathlib import Path
from typing import Any, Sequence

from corpus_parity import PARITY_COUNTER_FIELDS, PARITY_LABEL_FIELDS

# metric name -> aggregate fields copied into the sanitized summary
METRIC_FIELDS: dict[str, tuple[str, ...]] = {
    "http_req_duration": ("avg", "med", "p(90)", "p(95)", "p(99)", "max"),
    "http_reqs": ("count", "rate"),
    "http_req_failed": ("rate",),
    "checks": ("passes", "fails"),
    "dropped_iterations": ("count",),
}
# Filenames stage will publish; everything else in the output tree is left behind.
STATUS_PATTERN = re.compile(r"(ok|transport_failure|invalid_response|rpc_error)(:-?\d+)?")

STAGED_FILENAMES = ("summary.json", "parity.json", "jsonbench-summary.md", "summaries.manifest",
                    "timings.csv", "parity-diffs.json")


class CorpusResultsError(Exception):
    """Raised with a content-free message when a result cannot be sanitized or staged."""


def _number(value: Any, label: str) -> int | float:
    if isinstance(value, bool) or not isinstance(value, (int, float)) \
            or not math.isfinite(float(value)) or value < 0:
        raise CorpusResultsError(f"{label} is not a finite non-negative number")
    return value


def _metric_value(metric: Any, field: str, label: str) -> int | float:
    """Read a k6 aggregate that may sit under .values or at the metric's top level."""
    if isinstance(metric, dict):
        values = metric.get("values")
        if isinstance(values, dict) and field in values:
            return _number(values[field], label)
        if field in metric:
            return _number(metric[field], label)
        # k6 sometimes reports rates under "value"
        if field == "rate":
            if isinstance(values, dict) and "value" in values:
                return _number(values["value"], label)
            if "value" in metric:
                return _number(metric["value"], label)
    raise CorpusResultsError(f"missing metric value {label}")


def sanitize_data(raw: Any) -> dict[str, Any]:
    """Return only the fixed aggregate schema from a raw k6 summary document."""
    if not isinstance(raw, dict) or not isinstance(raw.get("metrics"), dict):
        raise CorpusResultsError("summary has no metrics object")
    raw_metrics = raw["metrics"]
    sanitized: dict[str, Any] = {}
    # k6 omits these when the workload triggers no check()/failed request/drop — absence
    # means zero, and must not kill a finished cell.
    optional_metrics = ("dropped_iterations", "checks", "http_req_failed")
    for name, fields in METRIC_FIELDS.items():
        if name in optional_metrics and name not in raw_metrics:
            sanitized[name] = {"values": {field: 0 for field in fields}}
            continue
        values = {field: _metric_value(raw_metrics.get(name), field, f"{name}.{field}") for field in fields}
        sanitized[name] = {"values": values}
    if sanitized["http_reqs"]["values"]["count"] < 1:
        raise CorpusResultsError("summary reports zero requests")
    return {"metrics": sanitized}


def sanitize(raw_path: str, out_path: str) -> None:
    raw_file = Path(raw_path)
    try:
        with raw_file.open("r", encoding="utf-8") as source:
            raw = json.load(source)
    except (OSError, json.JSONDecodeError, UnicodeDecodeError):
        raise CorpusResultsError("raw summary is missing or not valid JSON") from None
    data = sanitize_data(raw)
    target = Path(out_path)
    target.parent.mkdir(parents=True, exist_ok=True)
    with target.open("w", encoding="utf-8") as output:
        json.dump(data, output, sort_keys=True, separators=(",", ":"))
        output.write("\n")


def _validate_summary(path: Path) -> None:
    with path.open("r", encoding="utf-8") as source:
        data = json.load(source)
    if not isinstance(data, dict) or set(data) != {"metrics"}:
        raise CorpusResultsError(f"{path.name} is not a sanitized summary")
    for name, fields in METRIC_FIELDS.items():
        metric = data["metrics"].get(name)
        if not isinstance(metric, dict) or not isinstance(metric.get("values"), dict):
            raise CorpusResultsError(f"{path.name}: metric {name} has an unexpected shape")
        for field in fields:
            _number(metric["values"].get(field), f"{name}.{field}")


def _validate_parity(path: Path) -> None:
    with path.open("r", encoding="utf-8") as source:
        data = json.load(source)
    expected_keys = set(PARITY_COUNTER_FIELDS) | set(PARITY_LABEL_FIELDS) | {"divergences"}
    if not isinstance(data, dict) or set(data) != expected_keys:
        raise CorpusResultsError(f"{path.name} does not match the parity report schema")
    for field in PARITY_COUNTER_FIELDS:
        value = data[field]
        if isinstance(value, bool) or not isinstance(value, int) or value < 0:
            raise CorpusResultsError(f"{path.name}: {field} is not a non-negative integer")
    for field in PARITY_LABEL_FIELDS:
        if not isinstance(data[field], str) or not (0 < len(data[field]) <= 128):
            raise CorpusResultsError(f"{path.name}: {field} is not a short string")
    divergences = data["divergences"]
    # Bound against the report's own record count, not the producer's runtime cap: staging runs in
    # a separate process from the replay, so an env-tuned cap is not visible here and hard-coding
    # the default would reject a report that legitimately enumerated more.
    if not isinstance(divergences, list) or len(divergences) > data["total"]:
        raise CorpusResultsError(
            f"{path.name}: divergences ({len(divergences) if isinstance(divergences, list) else 'n/a'}) "
            f"exceeds the record count ({data['total']})")
    for entry in divergences:
        if not isinstance(entry, dict) or set(entry) != {"index", "kind"} \
                or isinstance(entry["index"], bool) or not isinstance(entry["index"], int) or entry["index"] < 1 \
                or not isinstance(entry["kind"], str) or not (0 < len(entry["kind"]) <= 64):
            raise CorpusResultsError(f"{path.name}: divergences entries must be index/kind pairs")


def _validate_timings(path: Path) -> None:
    """A timing matrix must be record indexes and milliseconds — never call content."""
    with path.open(encoding="utf-8", newline="") as handle:
        reader = csv.reader(handle)
        try:
            header = next(reader)
        except StopIteration:
            raise CorpusResultsError("timings.csv: empty") from None
        if not header or header[0] != "record_index" or len(header) < 2:
            raise CorpusResultsError("timings.csv: unexpected header")
        numeric = {0}
        for position, column in enumerate(header[1:], start=1):
            if column.startswith("pass_") and column.endswith("_ms"):
                numeric.add(position)
            elif not (column.startswith("pass_") and column.endswith("_status")):
                raise CorpusResultsError("timings.csv: unexpected column name")
        for number, row in enumerate(reader, start=2):
            if len(row) != len(header):
                raise CorpusResultsError(f"timings.csv: row {number} has {len(row)} of {len(header)} columns")
            for position, cell in enumerate(row):
                if cell == "":
                    continue
                if position in numeric:
                    try:
                        float(cell)
                    except ValueError:
                        raise CorpusResultsError(f"timings.csv: row {number} holds a non-numeric value") from None
                # Status is a fixed vocabulary plus an optional JSON-RPC integer code — never content.
                elif not STATUS_PATTERN.fullmatch(cell):
                    raise CorpusResultsError(f"timings.csv: row {number} holds an unexpected status")


def stage(output_root: str, stage_root: str) -> None:
    """Copy only validated aggregate files from output_root into a fresh stage_root."""
    source_root = Path(output_root)
    if not source_root.is_dir():
        raise CorpusResultsError("output root does not exist")
    staged = 0
    destination_root = Path(stage_root)
    shutil.rmtree(destination_root, ignore_errors=True)
    for path in sorted(source_root.rglob("*")):
        if not path.is_file() or path.is_symlink() or path.name not in STAGED_FILENAMES:
            continue
        try:
            if path.name == "summary.json":
                _validate_summary(path)
            elif path.name == "parity.json":
                _validate_parity(path)
            elif path.name == "timings.csv":
                _validate_timings(path)
            # markdown/manifest files are generated by our own scripts from sanitized data
        except (OSError, json.JSONDecodeError, UnicodeDecodeError) as error:
            raise CorpusResultsError(f"{path.name}: unreadable ({error.__class__.__name__})") from None
        target = destination_root / path.relative_to(source_root)
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(path, target)
        staged += 1
    if staged == 0:
        raise CorpusResultsError("no publishable result files found under the output root")
    print(f"staged {staged} aggregate file(s)")


def main(argv: Sequence[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    subparsers = parser.add_subparsers(dest="command", required=True)

    sanitize_parser = subparsers.add_parser("sanitize", help="write a fixed aggregate-only summary")
    sanitize_parser.add_argument("raw")
    sanitize_parser.add_argument("out")

    stage_parser = subparsers.add_parser("stage", help="stage only validated aggregate files")
    stage_parser.add_argument("output_root")
    stage_parser.add_argument("stage_root")

    arguments = parser.parse_args(argv)
    try:
        if arguments.command == "sanitize":
            sanitize(arguments.raw, arguments.out)
        else:
            stage(arguments.output_root, arguments.stage_root)
    except CorpusResultsError as error:
        print(f"error: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
