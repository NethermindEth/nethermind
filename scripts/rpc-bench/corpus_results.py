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
from account_index_sweep import ContractError as AccountIndexContractError, validate_sanitized_preparation

# metric name -> aggregate fields copied into the sanitized summary
METRIC_FIELDS: dict[str, tuple[str, ...]] = {
    "http_req_duration": ("avg", "med", "p(90)", "p(95)", "p(99)", "max"),
    "http_reqs": ("count", "rate"),
    "http_req_failed": ("rate",),
    "checks": ("passes", "fails"),
    "dropped_iterations": ("count",),
}
# Filenames stage will publish; everything else in the output tree is left behind.
BLOCK_HASH_PATTERN = re.compile(r"0x[0-9a-f]{64}")
STATUS_PATTERN = re.compile(r"(ok|transport_failure|invalid_response|rpc_error)(:-?\d+)?")

STAGED_FILENAMES = ("summary.json", "parity.json", "jsonbench-summary.md", "summaries.manifest",
                    "timings.csv", "parity-diffs.json", "timings.meta.json",
                    "resources.json", "diagnostic.json", "warmup-diagnostic.json",
                    "node-health.json", "account-index-prepare.json")


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


def _validate_parity_diffs(path: Path) -> None:
    """Exact schema for the divergence characterisation — numbers only, never response words.

    This file is the one artifact derived from response bytes, so it is validated strictly: any
    field outside this schema, or any string that could carry a response word, fails staging.
    """
    with path.open("r", encoding="utf-8") as source:
        data = json.load(source)
    if not isinstance(data, dict) or set(data) != {
            "baseline_client", "candidate_client", "total_divergences", "recorded", "diffs"}:
        raise CorpusResultsError(f"{path.name} does not match the diffs schema")
    for field in ("baseline_client", "candidate_client"):
        if not isinstance(data[field], str) or not (0 < len(data[field]) <= 128):
            raise CorpusResultsError(f"{path.name}: {field} is not a short string")
    for field in ("total_divergences", "recorded"):
        value = data[field]
        if isinstance(value, bool) or not isinstance(value, int) or value < 0:
            raise CorpusResultsError(f"{path.name}: {field} is not a non-negative integer")
    diffs = data["diffs"]
    if not isinstance(diffs, list) or len(diffs) != data["recorded"]:
        raise CorpusResultsError(f"{path.name}: diffs length does not match 'recorded'")
    allowed = {"index", "baseline_bytes", "candidate_bytes",
               "baseline_all_zero", "candidate_all_zero",
               "differing_words", "total_differing_words"}
    for entry in diffs:
        if not isinstance(entry, dict) or not set(entry) <= allowed or "index" not in entry:
            raise CorpusResultsError(f"{path.name}: divergence entry has unexpected fields")
        for key in ("index", "baseline_bytes", "candidate_bytes", "total_differing_words"):
            if key in entry and (isinstance(entry[key], bool) or not isinstance(entry[key], int)):
                raise CorpusResultsError(f"{path.name}: {key} is not an integer")
        for key in ("baseline_all_zero", "candidate_all_zero"):
            if key in entry and not isinstance(entry[key], bool):
                raise CorpusResultsError(f"{path.name}: {key} is not a boolean")
        for word in entry.get("differing_words", []):
            if not isinstance(word, dict) or not set(word) <= {"word", "direction"}:
                raise CorpusResultsError(f"{path.name}: a differing word carries unexpected fields")
            if isinstance(word.get("word"), bool) or not isinstance(word.get("word"), int):
                raise CorpusResultsError(f"{path.name}: word position is not an integer")
            # No magnitude of any kind: with a zero operand a magnitude is the other operand.
            if "direction" in word and word["direction"] not in ("higher", "lower"):
                raise CorpusResultsError(f"{path.name}: direction is not 'higher' or 'lower'")


def _validate_timings_meta(path: Path) -> None:
    """Numbers, a client-agnostic block identity, and outcome counts — nothing else."""
    with path.open("r", encoding="utf-8") as source:
        data = json.load(source)
    required = {"head", "chain_id", "block_hash", "records", "passes", "requests",
                "target_rps", "achieved_rps", "concurrency", "warmup_seconds", "warmup_rps",
                "outcomes"}
    if not isinstance(data, dict) or set(data) != required:
        raise CorpusResultsError(f"{path.name} does not match the timings metadata schema")
    for key in ("head", "chain_id", "records", "passes", "requests", "concurrency", "warmup_seconds"):
        if isinstance(data[key], bool) or not isinstance(data[key], int) or data[key] < 0:
            raise CorpusResultsError(f"{path.name}: {key} is not a non-negative integer")
    for key in ("target_rps", "achieved_rps", "warmup_rps"):
        if isinstance(data[key], bool) or not isinstance(data[key], (int, float)) or data[key] < 0:
            raise CorpusResultsError(f"{path.name}: {key} is not a non-negative number")
    if not isinstance(data["block_hash"], str) or not BLOCK_HASH_PATTERN.fullmatch(data["block_hash"]):
        raise CorpusResultsError(f"{path.name}: block_hash is not a 32-byte hex hash")
    outcomes = data["outcomes"]
    if not isinstance(outcomes, dict):
        raise CorpusResultsError(f"{path.name}: outcomes is not an object")
    for key, value in outcomes.items():
        if not STATUS_PATTERN.fullmatch(key) or isinstance(value, bool) or not isinstance(value, int):
            raise CorpusResultsError(f"{path.name}: unexpected outcome entry")


RESOURCE_FIELDS = {
    "wall_seconds", "samples", "cpu_seconds", "cpu_avg_cores", "cpu_peak_cores",
    "cpu_throttled_usec", "memory_avg_bytes", "memory_peak_bytes", "io_read_bytes",
    "io_write_bytes", "stall_cpu_usec", "stall_io_usec", "stall_memory_usec", "requests",
    "cpu_ms_per_request", "io_read_bytes_per_request", "memory_anon_samples",
    "memory_anon_avg_bytes", "memory_anon_peak_bytes", "memory_file_samples",
    "memory_file_avg_bytes", "memory_file_peak_bytes",
}
MEMORY_BREAKDOWN_FIELDS = {
    "memory_anon_samples", "memory_anon_avg_bytes", "memory_anon_peak_bytes",
    "memory_file_samples", "memory_file_avg_bytes", "memory_file_peak_bytes",
}


def _validate_resources(path: Path) -> None:
    """Resource counters are numbers or null — nothing here may carry request-derived data."""
    with path.open("r", encoding="utf-8") as source:
        data = json.load(source)
    if not isinstance(data, dict) or not set(data) <= RESOURCE_FIELDS:
        raise CorpusResultsError(f"{path.name} does not match the resource schema")
    for key, value in data.items():
        if value is None:
            continue  # PSI is absent on kernels without pressure accounting
        if isinstance(value, bool) or not isinstance(value, (int, float)):
            raise CorpusResultsError(f"{path.name}: {key} is not numeric")
        if key in MEMORY_BREAKDOWN_FIELDS and (not math.isfinite(float(value)) or value < 0):
            raise CorpusResultsError(f"{path.name}: {key} is not a finite non-negative number")


DIAGNOSTIC_FIELDS = {
    "schema_version", "tool_exit_code", "summary_present", "summary_valid", "summary_error",
    "summary_bytes", "summary_request_count", "summary_fail_rate", "requested_duration_seconds",
    "container_present", "container_status", "container_exit_code", "container_oom_killed",
    "container_error", "tool_log_present", "tool_log_lines", "tool_log_k6_errors",
    "tool_log_summary_read_errors", "tool_log_summary_parse_errors", "tool_log_oom_signals",
    "output_files", "output_bytes",
    "resource_sample_present", "resource_sample_valid", "resource_sample_wall_seconds",
    "resource_sample_count", "resource_sample_requests", "resource_sample_normalized",
}
DIAGNOSTIC_SUMMARY_ERRORS = {"none", "missing", "invalid_json", "invalid_schema", "unreadable"}
DIAGNOSTIC_CONTAINER_STATUSES = {"missing", "unknown", "created", "running", "exited", "dead"}


def _validate_diagnostic(path: Path) -> None:
    """Validate the counts-only failure diagnostic; no tool or RPC text may cross staging."""
    with path.open("r", encoding="utf-8") as source:
        data = json.load(source)
    if not isinstance(data, dict) or set(data) != DIAGNOSTIC_FIELDS:
        raise CorpusResultsError(f"{path.name} does not match the diagnostic schema")
    if data["schema_version"] != 1:
        raise CorpusResultsError(f"{path.name}: unsupported schema version")
    for key in ("summary_present", "summary_valid", "container_present", "container_error",
                "container_oom_killed",
                "tool_log_present", "resource_sample_present", "resource_sample_valid",
                "resource_sample_normalized"):
        if not isinstance(data[key], bool):
            raise CorpusResultsError(f"{path.name}: {key} is not a boolean")
    for key in ("summary_bytes", "tool_log_lines", "tool_log_k6_errors",
                "tool_log_summary_read_errors", "tool_log_summary_parse_errors", "tool_log_oom_signals",
                "output_files", "output_bytes", "resource_sample_count"):
        if isinstance(data[key], bool) or not isinstance(data[key], int) or data[key] < 0:
            raise CorpusResultsError(f"{path.name}: {key} is not a non-negative integer")
    for key in ("resource_sample_wall_seconds",):
        _number(data[key], f"{path.name}: {key}")
    for key in ("summary_fail_rate", "requested_duration_seconds"):
        value = data[key]
        if value is not None:
            _number(value, f"{path.name}: {key}")
    if data["summary_fail_rate"] is not None and data["summary_fail_rate"] > 1:
        raise CorpusResultsError(f"{path.name}: summary_fail_rate is greater than one")
    value = data["summary_request_count"]
    if value is not None and (isinstance(value, bool) or not isinstance(value, int) or value < 0):
        raise CorpusResultsError(f"{path.name}: summary_request_count is not a non-negative integer or null")
    for key in ("tool_exit_code", "container_exit_code", "resource_sample_requests"):
        value = data[key]
        if value is not None and (isinstance(value, bool) or not isinstance(value, int) or value < 0):
            raise CorpusResultsError(f"{path.name}: {key} is not a non-negative integer or null")
    if data["summary_error"] not in DIAGNOSTIC_SUMMARY_ERRORS:
        raise CorpusResultsError(f"{path.name}: unknown summary error")
    if data["container_status"] not in DIAGNOSTIC_CONTAINER_STATUSES:
        raise CorpusResultsError(f"{path.name}: unknown container status")


NODE_HEALTH_FIELDS = {
    "exception_count", "gated_exception_count", "invalid_block_count", "clean_shutdown",
    "unhandled_count", "fatal_count", "error_count",
}


def _validate_node_health(path: Path) -> None:
    """Validate fixed log-scan counts without publishing any log lines."""
    with path.open("r", encoding="utf-8") as source:
        data = json.load(source)
    if not isinstance(data, dict) or set(data) != NODE_HEALTH_FIELDS:
        raise CorpusResultsError(f"{path.name} does not match the node health schema")
    if not isinstance(data["clean_shutdown"], bool):
        raise CorpusResultsError(f"{path.name}: clean_shutdown is not a boolean")
    for key in NODE_HEALTH_FIELDS - {"clean_shutdown"}:
        if isinstance(data[key], bool) or not isinstance(data[key], int) or data[key] < 0:
            raise CorpusResultsError(f"{path.name}: {key} is not a non-negative integer")


def _validate_account_preparation(path: Path) -> None:
    """Validate the helper's aggregate-only report; raw paths and SST metadata never stage."""
    with path.open("r", encoding="utf-8") as source:
        data = json.load(source)
    try:
        validate_sanitized_preparation(data)
    except AccountIndexContractError as error:
        raise CorpusResultsError(f"{path.name}: invalid Account preparation aggregate") from error


def _safe_summary_state(path: Path) -> tuple[bool, bool, str, int, int | None, float | None]:
    """Inspect a raw summary without returning or logging any request-derived data."""
    try:
        size = path.stat().st_size
    except OSError:
        return False, False, "missing", 0, None, None
    try:
        with path.open("r", encoding="utf-8") as source:
            raw = json.load(source)
    except UnicodeDecodeError:
        return True, False, "invalid_json", size, None, None
    except json.JSONDecodeError:
        return True, False, "invalid_json", size, None, None
    except OSError:
        return True, False, "unreadable", size, None, None
    try:
        sanitized = sanitize_data(raw)
    except CorpusResultsError:
        return True, False, "invalid_schema", size, None, None
    metrics = sanitized["metrics"]
    request_count = metrics["http_reqs"]["values"]["count"]
    fail_rate = metrics["http_req_failed"]["values"]["rate"]
    return True, True, "none", size, int(request_count), float(fail_rate)


def _duration_seconds(value: str | None) -> float | None:
    if not value:
        return None
    match = re.fullmatch(r"\s*([0-9]+(?:\.[0-9]+)?)\s*([smh])\s*", value)
    if not match:
        return None
    amount = float(match.group(1))
    multiplier = {"s": 1.0, "m": 60.0, "h": 3600.0}[match.group(2)]
    return amount * multiplier


def diagnose(raw_summary: str, out_path: str, state_path: str, tool_exit_code: int | None,
             output_root: str, resources_path: str = "", requested_duration: str = "",
             tool_log_path: str = "") -> None:
    """Write a fixed, aggregate-only diagnostic for a corpus load container."""
    raw_path = Path(raw_summary)
    present, valid, summary_error, summary_bytes, summary_requests, summary_fail_rate = _safe_summary_state(raw_path)

    container_present = False
    container_status = "missing"
    container_exit_code: int | None = None
    container_oom_killed = False
    container_error = False
    state = Path(state_path)
    try:
        with state.open("r", encoding="utf-8") as source:
            value = json.load(source)
        if isinstance(value, dict):
            container_present = True
            candidate_status = value.get("Status")
            container_status = candidate_status if candidate_status in DIAGNOSTIC_CONTAINER_STATUSES else "unknown"
            candidate_exit = value.get("ExitCode")
            if isinstance(candidate_exit, int) and candidate_exit >= 0:
                container_exit_code = candidate_exit
            container_oom_killed = value.get("OOMKilled") is True
            container_error = bool(value.get("Error"))
    except (OSError, json.JSONDecodeError, UnicodeDecodeError):
        pass

    output_files = 0
    output_bytes = 0
    output = Path(output_root)
    if output.is_dir():
        for path in output.rglob("*"):
            if path.is_file() and not path.is_symlink():
                output_files += 1
                try:
                    output_bytes += path.stat().st_size
                except OSError:
                    pass

    tool_log_present = False
    tool_log_lines = 0
    tool_log_k6_errors = 0
    tool_log_summary_read_errors = 0
    tool_log_summary_parse_errors = 0
    tool_log_oom_signals = 0
    if tool_log_path:
        tool_log = Path(tool_log_path)
        tool_log_present = tool_log.is_file()
        if tool_log_present:
            try:
                with tool_log.open("r", encoding="utf-8", errors="replace") as source:
                    for line in source:
                        lowered = line.lower()
                        tool_log_lines += 1
                        tool_log_k6_errors += "k6 command execution completed with errors" in lowered
                        tool_log_summary_read_errors += "failed to read k6 summary" in lowered
                        tool_log_summary_parse_errors += "failed to unmarshal k6 summary" in lowered
                        tool_log_oom_signals += any(signal in lowered for signal in (
                            "out of memory", "oomkilled", "signal: killed", "exit status 137"))
            except OSError:
                pass

    resource_present = False
    resource_valid = False
    resource_wall = 0.0
    resource_samples = 0
    resource_requests: int | None = None
    resource_normalized = False
    if resources_path:
        resource = Path(resources_path)
        resource_present = resource.is_file()
        if resource_present:
            try:
                with resource.open("r", encoding="utf-8") as source:
                    value = json.load(source)
                _validate_resources(resource)
                resource_valid = True
                resource_wall = float(value.get("wall_seconds", 0.0))
                resource_samples = int(value.get("samples", 0))
                requests = value.get("requests", 0)
                if isinstance(requests, int) and requests > 0:
                    resource_requests = requests
                    resource_normalized = True
            except (OSError, json.JSONDecodeError, UnicodeDecodeError, CorpusResultsError, ValueError, TypeError):
                pass

    data = {
        "schema_version": 1,
        "tool_exit_code": tool_exit_code,
        "summary_present": present,
        "summary_valid": valid,
        "summary_error": summary_error,
        "summary_bytes": summary_bytes,
        "summary_request_count": summary_requests,
        "summary_fail_rate": summary_fail_rate,
        "requested_duration_seconds": _duration_seconds(requested_duration),
        "container_present": container_present,
        "container_status": container_status,
        "container_exit_code": container_exit_code,
        "container_oom_killed": container_oom_killed,
        "container_error": container_error,
        "tool_log_present": tool_log_present,
        "tool_log_lines": tool_log_lines,
        "tool_log_k6_errors": tool_log_k6_errors,
        "tool_log_summary_read_errors": tool_log_summary_read_errors,
        "tool_log_summary_parse_errors": tool_log_summary_parse_errors,
        "tool_log_oom_signals": tool_log_oom_signals,
        "output_files": output_files,
        "output_bytes": output_bytes,
        "resource_sample_present": resource_present,
        "resource_sample_valid": resource_valid,
        "resource_sample_wall_seconds": round(resource_wall, 3),
        "resource_sample_count": resource_samples,
        "resource_sample_requests": resource_requests,
        "resource_sample_normalized": resource_normalized,
    }
    target = Path(out_path)
    target.parent.mkdir(parents=True, exist_ok=True)
    with target.open("w", encoding="utf-8") as destination:
        json.dump(data, destination, sort_keys=True, separators=(",", ":"))
        destination.write("\n")


# Arity matches the one consumer exactly: percat-matrix.py unpacks 4 fields for iso| and
# 3 for mix|, so the label class excludes '|' and each kind states its field count.
_LABEL = r"[A-Za-z0-9._-]+"
MANIFEST_LINE_PATTERN = re.compile(
    rf"(?P<prefix>iso\|{_LABEL}\|{_LABEL}\|{_LABEL}|mix\|{_LABEL}\|{_LABEL})"
    rf"=(?P<path>.+jsonbench-summary\.md)$")


def _stage_manifest(path: Path, source_root: Path, target: Path) -> bool:
    """Validate each manifest line and rewrite its path relative to the artifact root.

    The absolute paths exist for the runner-side aggregator; in the published artifact they
    only leak runner directory layout, so the staged copy carries artifact-relative paths.
    Unlike the content validators, a malformed INDEX drops only itself (with a warning):
    nothing downstream reads the staged copy, so failing the whole artifact over it would
    discard every validated result from a multi-hour sweep for zero privacy benefit.
    """
    lines_out = []
    for number, line in enumerate(path.read_text(encoding="utf-8").splitlines(), start=1):
        if not line.strip():
            continue
        match = MANIFEST_LINE_PATTERN.fullmatch(line)
        if not match:
            print(f"::warning::{path.name}: line {number} does not match the manifest shape — "
                  f"index not staged", file=sys.stderr)
            return False
        entry = Path(match.group("path"))
        try:
            relative = entry.resolve().relative_to(source_root.resolve())
        except ValueError:
            print(f"::warning::{path.name}: line {number} points outside the output root — "
                  f"index not staged", file=sys.stderr)
            return False
        lines_out.append(f"{match.group('prefix')}={relative.as_posix()}")
    target.write_text("\n".join(lines_out) + "\n", encoding="utf-8")
    return True


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
        # Discarded warm-up output must never publish: comment() keys cells by directory position,
        # so a staged warmup/summary.json would displace a measured cell in the PR comment. The
        # sweep writes warm-ups to scratch, but staging is the boundary, so it enforces this too.
        parts = path.relative_to(source_root).parts
        # Anchored to the two positions warm-up output can occupy (the scratch tree root and the
        # cell-slot under a label) — a corpus LABEL containing "warmup" must not trip this, or a
        # legitimately named scenario would vanish from the artifact without a word.
        if parts[0].startswith("warmup") or path.parent.name.startswith("warmup"):
            continue
        try:
            if path.name == "summary.json":
                _validate_summary(path)
            elif path.name == "parity.json":
                _validate_parity(path)
            elif path.name == "timings.csv":
                _validate_timings(path)
            elif path.name == "parity-diffs.json":
                _validate_parity_diffs(path)
            elif path.name == "timings.meta.json":
                _validate_timings_meta(path)
            elif path.name == "resources.json":
                _validate_resources(path)
            elif path.name == "account-index-prepare.json":
                _validate_account_preparation(path)
            elif path.name in ("diagnostic.json", "warmup-diagnostic.json"):
                _validate_diagnostic(path)
            elif path.name == "node-health.json":
                _validate_node_health(path)
            elif path.name == "summaries.manifest":
                target = destination_root / path.relative_to(source_root)
                target.parent.mkdir(parents=True, exist_ok=True)
                if _stage_manifest(path, source_root, target):
                    staged += 1
                continue
            # jsonbench-summary.md is generated by run-jsonbench.sh strictly downstream of
            # sanitize(), so it is the one file staged without its own validator here.
        except (OSError, json.JSONDecodeError, UnicodeDecodeError) as error:
            raise CorpusResultsError(f"{path.name}: unreadable ({error.__class__.__name__})") from None
        target = destination_root / path.relative_to(source_root)
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(path, target)
        staged += 1
    if staged == 0:
        raise CorpusResultsError("no publishable result files found under the output root")
    print(f"staged {staged} aggregate file(s)")


COMMENT_METRICS = (("avg", "avg"), ("med", "median"), ("p(90)", "p90"),
                   ("p(95)", "p95"), ("p(99)", "p99"), ("max", "max"))


def comment(stage_root: str, baseline_label: str, candidate_label: str) -> str:
    """Render a PR comment from STAGED results only.

    Reads the staged tree rather than the raw output dir on purpose: staging is what enforces the
    aggregate-only boundary, so anything reaching a public PR comment has already passed it.
    """
    root = Path(stage_root)
    # Keyed by rate slot as well: a multi-rate sweep produces sibling rate directories under one
    # label, and keying on (corpus, label) alone silently kept whichever slot sorted last.
    cells: dict[tuple[str, str, str], dict] = {}
    for path in sorted(root.rglob("summary.json")):
        slot = path.parent.name
        label, corpus = path.parent.parent.name, path.parent.parent.parent.name
        cells[(corpus, label, slot)] = json.loads(path.read_text(encoding="utf-8"))["metrics"]
    if not cells:
        return "No corpus cells were produced, so there is nothing to compare."

    def slot_order(slot: str):
        head = slot.split("_")[0]
        return (int(head) if head.isdigit() else 0, slot)

    lines: list[str] = ["### `eth_call` corpus — PR vs master", ""]
    for corpus in sorted({c for c, _, _ in cells}):
        slots = sorted({s for c, _, s in cells if c == corpus}, key=slot_order)
        for slot in slots:
            base = cells.get((corpus, baseline_label, slot))
            cand = cells.get((corpus, candidate_label, slot))
            if not base or not cand:
                lines.append(f"`{corpus}` @ `{slot}`: missing a client, cannot compare.")
                continue
            b_fail = base["http_req_failed"]["values"]["rate"] * 100
            c_fail = cand["http_req_failed"]["values"]["rate"] * 100
            lines += [f"**`{corpus}`** @ `{slot}` rps · "
                      f"{int(cand['http_reqs']['values']['count'])} requests/client", "",
                      "| metric | master | PR | delta |", "|---|---|---|---|"]
            for key, name in COMMENT_METRICS:
                bv = base["http_req_duration"]["values"][key]
                cv = cand["http_req_duration"]["values"][key]
                delta = (cv - bv) / bv * 100 if bv else float("nan")
                arrow = "🟢" if delta < -1 else ("🔴" if delta > 1 else "⚪")
                lines.append(f"| {name} | {bv:.2f} ms | {cv:.2f} ms | {arrow} {delta:+.1f}% |")
            lines += ["", f"Failure rate — master {b_fail:.2f}%, PR {c_fail:.2f}%.", ""]

        report = root / "corpus" / corpus / candidate_label / "parity.json"
        if report.is_file():
            data = json.loads(report.read_text(encoding="utf-8"))
            agree = data["matched"] + data["both_rpc_errors"]
            verdict = "identical to master" if agree == data["total"] else "**DIVERGES from master**"
            lines.append(f"Response parity: {agree}/{data['total']} {verdict}.")
            if agree != data["total"]:
                defects = ", ".join(f"{k}={v}" for k, v in sorted(data.items())
                                    if isinstance(v, int) and v
                                    and k not in ("total", "matched", "both_rpc_errors"))
                lines.append(f"Divergence counts: {defects}")
        lines.append("")
    lines.append("<sub>Fixed corpus and rate; a PR that changes results is a correctness "
                 "regression regardless of latency. Latency deltas under ~2.5% are within "
                 "run-to-run noise.</sub>")
    return "\n".join(lines)


def main(argv: Sequence[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    subparsers = parser.add_subparsers(dest="command", required=True)

    sanitize_parser = subparsers.add_parser("sanitize", help="write a fixed aggregate-only summary")
    sanitize_parser.add_argument("raw")
    sanitize_parser.add_argument("out")

    diagnose_parser = subparsers.add_parser("diagnose", help="write a counts-only corpus run diagnostic")
    diagnose_parser.add_argument("raw_summary")
    diagnose_parser.add_argument("out")
    diagnose_parser.add_argument("--state", required=True, help="docker State JSON on the runner")
    diagnose_parser.add_argument("--tool-exit-code", type=int)
    diagnose_parser.add_argument("--output-root", required=True)
    diagnose_parser.add_argument("--resources", default="")
    diagnose_parser.add_argument("--requested-duration", default="")
    diagnose_parser.add_argument("--tool-log", default="")

    stage_parser = subparsers.add_parser("stage", help="stage only validated aggregate files")
    stage_parser.add_argument("output_root")
    stage_parser.add_argument("stage_root")

    comment_parser = subparsers.add_parser("comment", help="render a PR comment from staged results")
    comment_parser.add_argument("stage_root")
    comment_parser.add_argument("--baseline", required=True)
    comment_parser.add_argument("--candidate", required=True)

    arguments = parser.parse_args(argv)
    try:
        if arguments.command == "sanitize":
            sanitize(arguments.raw, arguments.out)
        elif arguments.command == "diagnose":
            diagnose(arguments.raw_summary, arguments.out, arguments.state,
                     arguments.tool_exit_code, arguments.output_root, arguments.resources,
                     arguments.requested_duration, arguments.tool_log)
        elif arguments.command == "stage":
            stage(arguments.output_root, arguments.stage_root)
        else:
            print(comment(arguments.stage_root, arguments.baseline, arguments.candidate))
    except CorpusResultsError as error:
        print(f"error: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
