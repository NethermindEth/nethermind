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
import errno
import json
import math
import os
import re
import shutil
import signal
import subprocess
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
BLOCK_HASH_PATTERN = re.compile(r"0x[0-9a-f]{64}")
STATUS_PATTERN = re.compile(r"(ok|transport_failure|invalid_response|rpc_error)(:-?\d+)?")

STAGED_FILENAMES = ("summary.json", "parity.json", "jsonbench-summary.md", "summaries.manifest",
                    "timings.csv", "parity-diffs.json", "timings.meta.json",
                    "resources.json", "perf-stat.json")

# The profiler command is intentionally fixed in the shell scripts. Keep its event names, required
# subset, units, and public artifact schema in one place so raw perf output cannot expand the
# aggregate-only corpus artifact surface.
PERF_COUNTERS: tuple[tuple[str, str, bool], ...] = (
    ("task-clock", "milliseconds", True),
    ("cycles", "count", True),
    ("ref-cycles", "count", False),
    ("instructions", "count", True),
    ("cache-references", "count", False),
    ("cache-misses", "count", False),
    ("LLC-loads", "count", False),
    ("LLC-load-misses", "count", False),
    ("dTLB-loads", "count", False),
    ("dTLB-load-misses", "count", False),
    ("minor-faults", "count", False),
    ("page-faults", "count", False),
    ("context-switches", "count", False),
    ("cpu-migrations", "count", False),
)
PERF_COUNTER_NAMES = tuple(name for name, _, _ in PERF_COUNTERS)
PERF_REQUIRED_COUNTERS = tuple(name for name, _, required in PERF_COUNTERS if required)
PERF_COUNTER_UNITS = {name: unit for name, unit, _ in PERF_COUNTERS}
PERF_OPTIONAL_COUNTERS = frozenset(name for name, _, required in PERF_COUNTERS if not required)
PERF_COUNTER_FIELDS = {
    "status", "unit", "raw_count", "per_request", "time_enabled_ns", "time_running_ns", "scale",
}
PERF_RAW_RECORD_FIELDS = {
    "counter-value", "unit", "event", "event-runtime", "pcnt-running",
    # perf emits derived metric fields for some default aliases. They are parsed only as a
    # recognized envelope and never copied to the sanitized output.
    "metric-value", "metric-unit", "metric-threshold", "variance",
}
PERF_COUNTER_VALUE_UNAVAILABLE = {"<not supported>", "<not counted>"}
PERF_NUMERIC_STRING_PATTERN = re.compile(
    r"[+]?(?:[0-9]+(?:\.[0-9]*)?|\.[0-9]+)(?:[eE][+-]?[0-9]+)?")


class CorpusResultsError(Exception):
    """Raised with a content-free message when a result cannot be sanitized or staged."""


def _number(value: Any, label: str) -> int | float:
    if isinstance(value, bool) or not isinstance(value, (int, float)) \
            or not math.isfinite(float(value)) or value < 0:
        raise CorpusResultsError(f"{label} is not a finite non-negative number")
    return value


def _perf_error() -> CorpusResultsError:
    """Keep raw perf data, including diagnostic text, out of workflow logs."""
    return CorpusResultsError("perf stat data is invalid")


def _perf_number(value: Any) -> float:
    """Accept only finite, locale-independent non-negative perf number encodings."""
    if isinstance(value, bool):
        raise _perf_error()
    if isinstance(value, (int, float)):
        number = float(value)
    elif isinstance(value, str) and PERF_NUMERIC_STRING_PATTERN.fullmatch(value):
        try:
            number = float(value)
        except ValueError:
            raise _perf_error() from None
    else:
        raise _perf_error()
    if not math.isfinite(number) or number < 0:
        raise _perf_error()
    return number


def _strict_json_object(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    """Reject duplicate JSON keys instead of allowing json.load to overwrite one."""
    result: dict[str, Any] = {}
    for key, value in pairs:
        if key in result:
            raise ValueError("duplicate JSON key")
        result[key] = value
    return result


def _reject_json_constant(_: str) -> Any:
    """Reject non-standard JSON constants such as NaN and Infinity."""
    raise ValueError("non-finite JSON constant")


def _pidfd_error() -> CorpusResultsError:
    """Keep process identity and host capability details out of workflow logs."""
    return CorpusResultsError("identity-safe perf signaling is unavailable")


def _perf_process_identity(pid: int) -> tuple[str, str]:
    """Return the Linux process state and start time after validating the proc-stat shape."""
    if isinstance(pid, bool) or not isinstance(pid, int) or pid < 1:
        raise _pidfd_error()
    try:
        stat = Path(f"/proc/{pid}/stat").read_text(encoding="utf-8")
    except FileNotFoundError:
        raise ProcessLookupError from None
    except OSError:
        raise _pidfd_error() from None
    tail_index = stat.rfind(") ")
    if tail_index < 0:
        raise _pidfd_error()
    fields = stat[tail_index + 2:].split()
    if len(fields) < 20 or not fields[0] or not fields[19].isdigit():
        raise _pidfd_error()
    return fields[0], fields[19]


def _pidfd_supported() -> bool:
    """Whether this Python/Linux host exposes the two pidfd operations needed for safe signaling."""
    return hasattr(os, "pidfd_open") and hasattr(signal, "pidfd_send_signal")


def signal_perf_process(pid: int, expected_start_time: str, signal_name: str) -> str:
    """Signal the captured Linux process through a pidfd, never a reused numeric PID.

    Returns ``sent``, ``zombie``, or ``gone``. A missing/replaced process is deliberately a
    non-error because the caller's original perf process has already stopped.
    """
    if not _pidfd_supported():
        raise _pidfd_error()
    if isinstance(pid, bool) or not isinstance(pid, int) or pid < 1 \
            or not isinstance(expected_start_time, str) or not expected_start_time.isascii() \
            or not expected_start_time.isdigit():
        raise _pidfd_error()
    signals = {"INT": signal.SIGINT, "TERM": signal.SIGTERM, "KILL": signal.SIGKILL}
    signal_number = signals.get(signal_name)
    if signal_number is None:
        raise _pidfd_error()
    try:
        pidfd = os.pidfd_open(pid, 0)
    except ProcessLookupError:
        return "gone"
    except OSError as error:
        if error.errno == errno.ESRCH:
            return "gone"
        raise _pidfd_error() from None
    try:
        try:
            state, start_time = _perf_process_identity(pid)
        except ProcessLookupError:
            return "gone"
        if start_time != expected_start_time:
            return "gone"
        if state == "Z":
            return "zombie"
        try:
            signal.pidfd_send_signal(pidfd, signal_number, None, 0)
        except ProcessLookupError:
            return "gone"
        except OSError as error:
            if error.errno == errno.ESRCH:
                return "gone"
            raise _pidfd_error() from None
        return "sent"
    finally:
        try:
            os.close(pidfd)
        except OSError:
            raise _pidfd_error() from None


def validate_perf_pidfd_support() -> None:
    """Exercise the host pidfd operations using a short-lived child before a benchmark starts."""
    if not _pidfd_supported():
        raise _pidfd_error()
    child: subprocess.Popen[str] | None = None
    try:
        child = subprocess.Popen(["/bin/sleep", "60"], stdout=subprocess.DEVNULL,
                                 stderr=subprocess.DEVNULL)
        _, start_time = _perf_process_identity(child.pid)
        if signal_perf_process(child.pid, start_time, "TERM") != "sent" \
                or child.wait(timeout=5) != -signal.SIGTERM:
            raise _pidfd_error()
    except (OSError, subprocess.SubprocessError):
        raise _pidfd_error() from None
    finally:
        if child is not None and child.poll() is None:
            try:
                child.kill()
            except OSError:
                pass
            try:
                child.wait(timeout=5)
            except subprocess.SubprocessError:
                pass


def _load_perf_records(text: str) -> list[Any]:
    """Decode perf's JSON-record stream without accepting non-JSON text around it."""
    if not text or not text.strip():
        raise _perf_error()
    decoder = json.JSONDecoder(object_pairs_hook=_strict_json_object,
                               parse_constant=_reject_json_constant)
    length = len(text)

    def skip_whitespace(position: int) -> int:
        while position < length and text[position] in " \t\r\n":
            position += 1
        return position

    try:
        position = skip_whitespace(0)
        if position == length:
            raise ValueError("empty")
        if text[position] == "[":
            records, position = decoder.raw_decode(text, position)
            if not isinstance(records, list):
                raise ValueError("invalid array")
            if skip_whitespace(position) != length:
                raise ValueError("trailing data")
            return records

        records = []
        while position < length:
            record, position = decoder.raw_decode(text, position)
            records.append(record)
            position = skip_whitespace(position)
            if position == length:
                break
            if text[position] == ",":
                position = skip_whitespace(position + 1)
                if position == length:
                    raise ValueError("trailing separator")
            elif text[position] != "{":
                raise ValueError("unexpected data")
        return records
    except (json.JSONDecodeError, TypeError, ValueError):
        raise _perf_error() from None


def _perf_timing(record: dict[str, Any]) -> tuple[float | None, float | None, float | None]:
    """Derive enabled/running time and scale from perf's documented runtime percentage."""
    runtime = record.get("event-runtime")
    percent = record.get("pcnt-running")
    if runtime is None or percent is None:
        return None, None, None
    running = _perf_number(runtime)
    running_percent = _perf_number(percent)
    if running_percent > 100:
        raise _perf_error()
    if running_percent == 0:
        return None, None, None
    scale = 100.0 / running_percent
    enabled = running * scale
    if not math.isfinite(enabled):
        raise _perf_error()
    return enabled, running, scale


def _validate_perf_raw_envelope(record: Any, expected_events: tuple[str, ...]) -> tuple[str, dict[str, Any]]:
    """Validate one raw perf record before its fixed fields are normalized."""
    if not isinstance(record, dict) or not {"counter-value", "unit", "event"} <= set(record) \
            or not set(record) <= PERF_RAW_RECORD_FIELDS:
        raise _perf_error()
    event = record["event"]
    if not isinstance(event, str) or event not in expected_events:
        raise _perf_error()
    unit = record["unit"]
    expected_raw_unit = "msec" if event == "task-clock" else ""
    if not isinstance(unit, str) or unit != expected_raw_unit:
        raise _perf_error()
    for field in ("metric-value", "variance"):
        if field in record and record[field] is not None:
            _perf_number(record[field])
    for field in ("metric-unit", "metric-threshold"):
        if field in record and record[field] is not None and (not isinstance(record[field], str)
                                                               or len(record[field]) > 128
                                                               or "\n" in record[field]
                                                               or "\r" in record[field]):
            raise _perf_error()
    # A present timing field is numeric (or explicit null); invalid metadata is not silently
    # discarded as an unavailable counter.
    for field in ("event-runtime", "pcnt-running"):
        if field in record and record[field] is not None:
            _perf_number(record[field])
    return event, record


def _parse_perf_records(raw: str, expected_events: tuple[str, ...]) -> dict[str, dict[str, Any]]:
    """Return safe fixed records, rejecting every omitted, duplicate, or unknown event."""
    parsed: dict[str, dict[str, Any]] = {}
    for candidate in _load_perf_records(raw):
        event, record = _validate_perf_raw_envelope(candidate, expected_events)
        if event in parsed:
            raise _perf_error()
        raw_value = record["counter-value"]
        if isinstance(raw_value, str) and raw_value in PERF_COUNTER_VALUE_UNAVAILABLE:
            if event not in PERF_OPTIONAL_COUNTERS:
                raise _perf_error()
            parsed[event] = {
                "status": "unsupported",
                "unit": PERF_COUNTER_UNITS[event],
                "raw_count": None,
                "per_request": None,
                "time_enabled_ns": None,
                "time_running_ns": None,
                "scale": None,
            }
            continue
        raw_count = _perf_number(raw_value)
        enabled, running, scale = _perf_timing(record)
        parsed[event] = {
            "status": "collected",
            "unit": PERF_COUNTER_UNITS[event],
            "raw_count": raw_count,
            "per_request": None,
            "time_enabled_ns": enabled,
            "time_running_ns": running,
            "scale": scale,
        }
    if set(parsed) != set(expected_events):
        raise _perf_error()
    return parsed


def normalize_perf_data(raw: str, requests: int) -> dict[str, Any]:
    """Normalize a complete fixed perf-stat record stream into the aggregate-only schema."""
    if isinstance(requests, bool) or not isinstance(requests, int) or requests < 1:
        raise _perf_error()
    counters = _parse_perf_records(raw, PERF_COUNTER_NAMES)
    for counter in counters.values():
        if counter["status"] == "collected":
            counter["per_request"] = counter["raw_count"] / requests
    return {"schema_version": 1, "requests": requests,
            "counters": {name: counters[name] for name in PERF_COUNTER_NAMES}}


def normalize_perf(raw_path: str, out_path: str, requests: int) -> None:
    """Read raw perf output and write only its sanitized fixed aggregate summary."""
    try:
        raw = Path(raw_path).read_text(encoding="utf-8")
    except (OSError, UnicodeDecodeError):
        raise _perf_error() from None
    data = normalize_perf_data(raw, requests)
    try:
        with Path(out_path).open("w", encoding="utf-8") as output:
            json.dump(data, output, sort_keys=True, separators=(",", ":"))
            output.write("\n")
    except OSError:
        raise CorpusResultsError("could not write perf stat summary") from None


def validate_perf_preflight(raw_path: str) -> None:
    """Exercise the same strict raw parser for the required perf-stat event subset."""
    try:
        raw = Path(raw_path).read_text(encoding="utf-8")
    except (OSError, UnicodeDecodeError):
        raise _perf_error() from None
    _parse_perf_records(raw, PERF_REQUIRED_COUNTERS)


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
                "target_rps", "achieved_rps", "concurrency", "warmup_seconds", "outcomes"}
    if not isinstance(data, dict) or set(data) != required:
        raise CorpusResultsError(f"{path.name} does not match the timings metadata schema")
    for key in ("head", "chain_id", "records", "passes", "requests", "concurrency", "warmup_seconds"):
        if isinstance(data[key], bool) or not isinstance(data[key], int) or data[key] < 0:
            raise CorpusResultsError(f"{path.name}: {key} is not a non-negative integer")
    for key in ("target_rps", "achieved_rps"):
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
    "cpu_ms_per_request", "io_read_bytes_per_request",
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


def _validate_perf_stat(path: Path) -> None:
    """A perf-stat artifact is fixed counters and numeric aggregates only."""
    try:
        with path.open("r", encoding="utf-8") as source:
            data = json.load(source, object_pairs_hook=_strict_json_object,
                             parse_constant=_reject_json_constant)
    except (OSError, UnicodeDecodeError, json.JSONDecodeError, ValueError):
        raise CorpusResultsError(f"{path.name} does not match the perf stat schema") from None
    if not isinstance(data, dict) or set(data) != {"schema_version", "requests", "counters"} \
            or isinstance(data["schema_version"], bool) or not isinstance(data["schema_version"], int) \
            or data["schema_version"] != 1 \
            or isinstance(data["requests"], bool) or not isinstance(data["requests"], int) \
            or data["requests"] < 1 or not isinstance(data["counters"], dict) \
            or set(data["counters"]) != set(PERF_COUNTER_NAMES):
        raise CorpusResultsError(f"{path.name} does not match the perf stat schema")
    requests = data["requests"]
    for name in PERF_COUNTER_NAMES:
        counter = data["counters"][name]
        if not isinstance(counter, dict) or set(counter) != PERF_COUNTER_FIELDS \
                or counter["unit"] != PERF_COUNTER_UNITS[name] \
                or counter["status"] not in {"collected", "unsupported"}:
            raise CorpusResultsError(f"{path.name}: invalid perf counter")
        if counter["status"] == "unsupported":
            if name not in PERF_OPTIONAL_COUNTERS or any(
                    counter[field] is not None for field in PERF_COUNTER_FIELDS - {"status", "unit"}):
                raise CorpusResultsError(f"{path.name}: invalid unsupported perf counter")
            continue
        raw_count = _number(counter["raw_count"], "perf raw_count")
        per_request = _number(counter["per_request"], "perf per_request")
        if not math.isclose(float(per_request), float(raw_count) / requests, rel_tol=1e-12, abs_tol=1e-12):
            raise CorpusResultsError(f"{path.name}: invalid perf per-request value")
        timing = tuple(counter[field] for field in ("time_enabled_ns", "time_running_ns", "scale"))
        if any(value is None for value in timing):
            if not all(value is None for value in timing):
                raise CorpusResultsError(f"{path.name}: incomplete perf timing metadata")
            continue
        enabled, running, scale = (_number(value, "perf timing") for value in timing)
        if scale == 0 or (running == 0 and enabled != 0) \
                or (running > 0 and not math.isclose(float(enabled) / float(running), float(scale),
                                                      rel_tol=1e-12, abs_tol=1e-12)):
            raise CorpusResultsError(f"{path.name}: invalid perf timing metadata")


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
            elif path.name == "perf-stat.json":
                _validate_perf_stat(path)
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

    perf_normalize_parser = subparsers.add_parser(
        "perf-normalize", help="write a fixed aggregate-only perf-stat summary")
    perf_normalize_parser.add_argument("raw")
    perf_normalize_parser.add_argument("out")
    perf_normalize_parser.add_argument("requests", type=int)

    perf_preflight_parser = subparsers.add_parser(
        "perf-preflight", help="validate required perf-stat counters without publishing output")
    perf_preflight_parser.add_argument("raw")

    subparsers.add_parser("perf-pidfd-preflight",
                          help="verify the host supports identity-safe perf signaling")

    perf_signal_parser = subparsers.add_parser(
        "perf-pidfd-signal", help="signal a captured perf process through a pidfd")
    perf_signal_parser.add_argument("pid", type=int)
    perf_signal_parser.add_argument("start_time")
    perf_signal_parser.add_argument("signal", choices=("INT", "TERM", "KILL"))

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
        elif arguments.command == "perf-normalize":
            normalize_perf(arguments.raw, arguments.out, arguments.requests)
        elif arguments.command == "perf-preflight":
            validate_perf_preflight(arguments.raw)
        elif arguments.command == "perf-pidfd-preflight":
            validate_perf_pidfd_support()
        elif arguments.command == "perf-pidfd-signal":
            print(signal_perf_process(arguments.pid, arguments.start_time, arguments.signal))
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
