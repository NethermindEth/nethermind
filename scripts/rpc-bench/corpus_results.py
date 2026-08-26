#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

"""Sanitize k6 summaries, stage aggregate-only results, and render the PR comment for private corpus runs.

Privacy contract: `sanitize` copies a fixed set of numeric aggregates out of a raw k6 summary.json;
`stage` copies only validated aggregate/parity files into the artifact directory; `comment` reads the
staged tree only.
"""

from __future__ import annotations

import argparse
import csv
import errno
import json
import math
import os
import random
import re
import signal
import shutil
import statistics
import subprocess
import sys
from pathlib import Path
from typing import Any, Sequence

from corpus_parity import PARITY_COUNTER_FIELDS, PARITY_LABEL_FIELDS

METRIC_FIELDS: dict[str, tuple[str, ...]] = {
    "http_req_duration": ("avg", "med", "p(90)", "p(95)", "p(99)", "max"),
    "http_reqs": ("count", "rate"),
    "http_req_failed": ("rate",),
    "checks": ("passes", "fails"),
    "dropped_iterations": ("count",),
}
OPTIONAL_METRICS = ("dropped_iterations", "checks", "http_req_failed")
CLASS_FIELDS = METRIC_FIELDS["http_req_duration"] + ("count",)
CLASS_NAME_PATTERN = re.compile(r"class_[0-9]+")
SUBMETRIC_PATTERN = re.compile(r"^(http_req_duration|http_reqs)\{(.*)\}$")
CLASS_TAG_PATTERN = re.compile(r"req_name:['\"]?(class_[0-9]+)['\"]?")
BLOCK_HASH_PATTERN = re.compile(r"0x[0-9a-f]{64}")
STATUS_PATTERN = re.compile(r"(ok|transport_failure|invalid_response|rpc_error)(:-?\d+)?")
REPEAT_PATTERN = re.compile(r"^(.*?)(?:_r[0-9]+)?$")
STAGED_FILENAMES = ("summary.json", "parity.json", "jsonbench-summary.md", "summaries.manifest",
                    "timings.csv", "parity-diffs.json", "timings.meta.json", "resources.json")
RESOURCE_FIELDS = {
    "wall_seconds", "samples", "cpu_seconds", "cpu_avg_cores", "cpu_peak_cores", "cpu_throttled_usec",
    "memory_avg_bytes", "memory_peak_bytes", "io_read_bytes", "io_write_bytes", "stall_cpu_usec",
    "stall_io_usec", "stall_memory_usec", "requests", "cpu_ms_per_request", "io_read_bytes_per_request",
    "cpu_frequency_khz", "estimated_cycles_per_request", "perf_stat",
}
FREQUENCY_SOURCES = ("scaling_cur_freq", "cpuinfo_cur_freq")
FREQUENCY_FIELDS = {"sample_count", "observation_count", "avg_khz", "min_khz", "max_khz"}
PERF_STAT_FIELDS = {
    "task_clock_ms", "cycles", "instructions", "task_clock_ms_per_request", "cycles_per_request",
    "instructions_per_request", "ipc", "effective_ghz",
}
PERF_STAT_EVENTS = ("task-clock", "cycles", "instructions")
PERF_STAT_RAW_FIELDS = {
    "counter-value", "unit", "event", "event-runtime", "pcnt-running", "runtime",
    "metric-value", "metric-unit", "metric-threshold", "variance",
}
PERF_STAT_UNAVAILABLE = {"<not supported>", "<not counted>"}
PERF_NUMBER_PATTERN = re.compile(r"[+]?(?:[0-9]+(?:\.[0-9]*)?|\.[0-9]+)(?:[eE][+-]?[0-9]+)?")
_LABEL = r"[A-Za-z0-9._-]+"
MANIFEST_LINE_PATTERN = re.compile(
    rf"(?P<prefix>iso\|{_LABEL}\|{_LABEL}\|{_LABEL}|mix\|{_LABEL}\|{_LABEL})=(?P<path>.+jsonbench-summary\.md)$")
COMMENT_METRICS = (("avg", "avg"), ("med", "median"), ("p(90)", "p90"), ("p(95)", "p95"), ("p(99)", "p99"), ("max", "max"))
NOISE_FLOOR_PCT = 2.5
REQUEST_MISMATCH_PCT = 1.0
RECORD_SHIFT_PCT = 5.0
BOOTSTRAP_ROUNDS = 2000


class CorpusResultsError(Exception):
    """Content-free failure of sanitizing or staging."""


def _number(value: Any, label: str) -> int | float:
    if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(float(value)) or value < 0:
        raise CorpusResultsError(f"{label} is not a finite non-negative number")
    return value


def _strict_json_object(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    result: dict[str, Any] = {}
    for key, value in pairs:
        if key in result:
            raise ValueError("duplicate JSON key")
        result[key] = value
    return result


def _reject_json_constant(value: str) -> None:
    raise ValueError(f"invalid JSON constant {value}")


def _pidfd_error() -> CorpusResultsError:
    """Keep process identity and host capability details out of workflow logs."""
    return CorpusResultsError("identity-safe perf signaling is unavailable")


def _perf_process_identity(pid: int) -> tuple[str, str]:
    """Return Linux process state and start time after validating proc-stat shape."""
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
    return hasattr(os, "pidfd_open") and hasattr(signal, "pidfd_send_signal")


def signal_perf_process(pid: int, expected_start_time: str, signal_name: str) -> str:
    """Signal a captured perf process through a pidfd, never a reused numeric PID."""
    if not _pidfd_supported():
        raise _pidfd_error()
    if isinstance(pid, bool) or not isinstance(pid, int) or pid < 1 \
            or not isinstance(expected_start_time, str) or not expected_start_time.isascii() \
            or not expected_start_time.isdigit():
        raise _pidfd_error()
    signal_number = {"INT": signal.SIGINT, "TERM": signal.SIGTERM, "KILL": signal.SIGKILL}.get(signal_name)
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
    """Exercise identity-safe signaling before starting an opted-in cell."""
    if not _pidfd_supported():
        raise _pidfd_error()
    child: subprocess.Popen[str] | None = None
    try:
        child = subprocess.Popen(["/bin/sleep", "60"], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
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


def _perf_error() -> CorpusResultsError:
    return CorpusResultsError("perf stat data is invalid")


def _perf_number(value: Any) -> float:
    if isinstance(value, bool):
        raise _perf_error()
    if isinstance(value, (int, float)):
        number = float(value)
    elif isinstance(value, str) and PERF_NUMBER_PATTERN.fullmatch(value):
        try:
            number = float(value)
        except ValueError:
            raise _perf_error() from None
    else:
        raise _perf_error()
    if not math.isfinite(number) or number < 0:
        raise _perf_error()
    return number


def _perf_event_name(value: Any) -> str:
    if not isinstance(value, str):
        raise _perf_error()
    if value in PERF_STAT_EVENTS:
        return value
    if value.endswith(":u") and value[:-2] in PERF_STAT_EVENTS:
        return value[:-2]
    raise _perf_error()


def _load_perf_records(raw: str) -> list[Any]:
    """Decode perf's JSON record stream or its top-level array form."""
    if not raw or not raw.strip():
        raise _perf_error()
    decoder = json.JSONDecoder(object_pairs_hook=_strict_json_object,
                               parse_constant=_reject_json_constant)
    length = len(raw)

    def skip_whitespace(position: int) -> int:
        while position < length and raw[position] in " \t\r\n":
            position += 1
        return position

    try:
        position = skip_whitespace(0)
        if position == length:
            raise ValueError("empty")
        if raw[position] == "[":
            records, position = decoder.raw_decode(raw, position)
            if not isinstance(records, list) or skip_whitespace(position) != length:
                raise ValueError("invalid array")
            return records

        records: list[Any] = []
        while position < length:
            record, position = decoder.raw_decode(raw, position)
            records.append(record)
            position = skip_whitespace(position)
            if position == length:
                break
            if raw[position] == ",":
                position = skip_whitespace(position + 1)
                if position == length:
                    raise ValueError("trailing separator")
            elif raw[position] != "{":
                raise ValueError("unexpected data")
        return records
    except (json.JSONDecodeError, TypeError, ValueError):
        raise _perf_error() from None


def _parse_perf_data(raw: str) -> dict[str, float]:
    parsed: dict[str, float] = {}
    for record in _load_perf_records(raw):
        if not isinstance(record, dict) or not set(record) <= PERF_STAT_RAW_FIELDS:
            raise _perf_error()
        event = _perf_event_name(record.get("event"))
        if event in parsed or "counter-value" not in record:
            raise _perf_error()
        for field in ("event-runtime", "pcnt-running", "runtime", "metric-value", "variance"):
            if field in record and record[field] is not None:
                value = _perf_number(record[field])
                if field == "pcnt-running" and value > 100:
                    raise _perf_error()
        for field in ("metric-unit", "metric-threshold"):
            value = record.get(field)
            if value is not None and (not isinstance(value, str) or len(value) > 128
                                      or "\n" in value or "\r" in value):
                raise _perf_error()
        value = record["counter-value"]
        if isinstance(value, str) and value in PERF_STAT_UNAVAILABLE:
            raise _perf_error()
        parsed[event] = _perf_number(value)
        unit = record.get("unit", "")
        if event == "task-clock" and unit not in ("msec", "ms"):
            raise _perf_error()
        if event != "task-clock" and unit not in ("", "count"):
            raise _perf_error()
    if set(parsed) != set(PERF_STAT_EVENTS):
        raise _perf_error()
    if parsed["task-clock"] <= 0 or parsed["cycles"] <= 0:
        raise _perf_error()
    return parsed


def normalize_perf_data(raw: str, requests: int) -> dict[str, float | int]:
    """Convert the fixed user-space perf event stream into numeric per-cell aggregates."""
    if isinstance(requests, bool) or not isinstance(requests, int) or requests < 1:
        raise _perf_error()
    counters = _parse_perf_data(raw)
    task_clock = counters["task-clock"]
    cycles = counters["cycles"]
    instructions = counters["instructions"]
    return {
        "task_clock_ms": round(task_clock, 6),
        "cycles": round(cycles, 3),
        "instructions": round(instructions, 3),
        "task_clock_ms_per_request": round(task_clock / requests, 9),
        "cycles_per_request": round(cycles / requests, 3),
        "instructions_per_request": round(instructions / requests, 3),
        "ipc": round(instructions / cycles, 9),
        "effective_ghz": round(cycles / (task_clock * 1_000_000), 9),
    }


def merge_perf(resources_path: str, raw_path: str, requests: int) -> None:
    """Merge fixed perf aggregates into an existing resource summary."""
    try:
        with Path(raw_path).open("r", encoding="utf-8") as source:
            raw = source.read()
        with Path(resources_path).open("r", encoding="utf-8") as source:
            resources = json.load(source, object_pairs_hook=_strict_json_object, parse_constant=_reject_json_constant)
    except (OSError, UnicodeDecodeError, ValueError, json.JSONDecodeError):
        raise _perf_error() from None
    if not isinstance(resources, dict):
        raise _perf_error()
    resources["perf_stat"] = normalize_perf_data(raw, requests)
    try:
        with Path(resources_path).open("w", encoding="utf-8") as output:
            json.dump(resources, output, sort_keys=True, separators=(",", ":"))
            output.write("\n")
    except OSError:
        raise CorpusResultsError("could not write resource summary") from None


def _metric_value(metric: Any, field: str, label: str) -> int | float:
    """A k6 aggregate under .values or at the metric's top level; rates may be reported as 'value'."""
    if isinstance(metric, dict):
        values = metric.get("values")
        if isinstance(values, dict) and field in values:
            return _number(values[field], label)
        if field in metric:
            return _number(metric[field], label)
        if field == "rate":
            if isinstance(values, dict) and "value" in values:
                return _number(values["value"], label)
            if "value" in metric:
                return _number(metric["value"], label)
    raise CorpusResultsError(f"missing metric value {label}")


def _class_submetrics(raw_metrics: dict) -> dict[str, dict]:
    classes: dict[str, dict] = {}
    for key, metric in raw_metrics.items():
        match = SUBMETRIC_PATTERN.match(key)
        tag = CLASS_TAG_PATTERN.search(match.group(2)) if match else None
        if not tag:
            continue
        entry = classes.setdefault(tag.group(1), {})
        if match.group(1) == "http_req_duration":
            for field in METRIC_FIELDS["http_req_duration"]:
                entry[field] = _metric_value(metric, field, f"{key}.{field}")
        else:
            entry["count"] = _metric_value(metric, "count", f"{key}.count")
    complete = {}
    for name, entry in sorted(classes.items(), key=lambda item: int(item[0].split("_")[1])):
        if all(field in entry for field in METRIC_FIELDS["http_req_duration"]):
            entry.setdefault("count", 0)
            complete[name] = {"values": entry}
    return complete


def sanitize_data(raw: Any) -> dict[str, Any]:
    """Return only the fixed aggregate schema (plus per-class sub-metrics) from a raw k6 summary."""
    if not isinstance(raw, dict) or not isinstance(raw.get("metrics"), dict):
        raise CorpusResultsError("summary has no metrics object")
    raw_metrics = raw["metrics"]
    sanitized: dict[str, Any] = {}
    for name, fields in METRIC_FIELDS.items():
        if name in OPTIONAL_METRICS and name not in raw_metrics:
            sanitized[name] = {"values": {field: 0 for field in fields}}
            continue
        sanitized[name] = {"values": {field: _metric_value(raw_metrics.get(name), field, f"{name}.{field}") for field in fields}}
    if sanitized["http_reqs"]["values"]["count"] < 1:
        raise CorpusResultsError("summary reports zero requests")
    classes = _class_submetrics(raw_metrics)
    if classes:
        sanitized["classes"] = classes
    return {"metrics": sanitized}


def sanitize(raw_path: str, out_path: str) -> None:
    try:
        with Path(raw_path).open("r", encoding="utf-8") as source:
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
    if not isinstance(data, dict) or set(data) != {"metrics"} or not set(data["metrics"]) <= set(METRIC_FIELDS) | {"classes"}:
        raise CorpusResultsError(f"{path.name} is not a sanitized summary")
    for name, fields in METRIC_FIELDS.items():
        metric = data["metrics"].get(name)
        if not isinstance(metric, dict) or not isinstance(metric.get("values"), dict):
            raise CorpusResultsError(f"{path.name}: metric {name} has an unexpected shape")
        for field in fields:
            _number(metric["values"].get(field), f"{name}.{field}")
    classes = data["metrics"].get("classes", {})
    if not isinstance(classes, dict):
        raise CorpusResultsError(f"{path.name}: classes is not an object")
    for name, metric in classes.items():
        if not CLASS_NAME_PATTERN.fullmatch(name) or not isinstance(metric, dict) \
                or not isinstance(metric.get("values"), dict) or set(metric["values"]) != set(CLASS_FIELDS):
            raise CorpusResultsError(f"{path.name}: class entry has an unexpected shape")
        for field in CLASS_FIELDS:
            _number(metric["values"][field], f"{name}.{field}")


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
    if not isinstance(divergences, list) or len(divergences) > data["total"]:
        raise CorpusResultsError(f"{path.name}: divergences exceed the record count")
    for entry in divergences:
        if not isinstance(entry, dict) or set(entry) != {"index", "kind"} \
                or isinstance(entry["index"], bool) or not isinstance(entry["index"], int) or entry["index"] < 1 \
                or not isinstance(entry["kind"], str) or not (0 < len(entry["kind"]) <= 64):
            raise CorpusResultsError(f"{path.name}: divergences entries must be index/kind pairs")


def _timings_columns(header: list[str]) -> set[int]:
    if not header or header[0] != "record_index" or len(header) < 2:
        raise CorpusResultsError("timings.csv: unexpected header")
    numeric = {0}
    for position, column in enumerate(header[1:], start=1):
        if column.startswith("pass_") and column.endswith("_ms"):
            numeric.add(position)
        elif not (column.startswith("pass_") and column.endswith("_status")):
            raise CorpusResultsError("timings.csv: unexpected column name")
    return numeric


def _validate_timings(path: Path) -> None:
    with path.open(encoding="utf-8", newline="") as handle:
        reader = csv.reader(handle)
        try:
            header = next(reader)
        except StopIteration:
            raise CorpusResultsError("timings.csv: empty") from None
        numeric = _timings_columns(header)
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
                elif not STATUS_PATTERN.fullmatch(cell):
                    raise CorpusResultsError(f"timings.csv: row {number} holds an unexpected status")


def _validate_parity_diffs(path: Path) -> None:
    with path.open("r", encoding="utf-8") as source:
        data = json.load(source)
    if not isinstance(data, dict) or set(data) != {"baseline_client", "candidate_client", "total_divergences", "recorded", "diffs"}:
        raise CorpusResultsError(f"{path.name} does not match the diffs schema")
    for field in ("baseline_client", "candidate_client"):
        if not isinstance(data[field], str) or not (0 < len(data[field]) <= 128):
            raise CorpusResultsError(f"{path.name}: {field} is not a short string")
    for field in ("total_divergences", "recorded"):
        if isinstance(data[field], bool) or not isinstance(data[field], int) or data[field] < 0:
            raise CorpusResultsError(f"{path.name}: {field} is not a non-negative integer")
    diffs = data["diffs"]
    if not isinstance(diffs, list) or len(diffs) != data["recorded"]:
        raise CorpusResultsError(f"{path.name}: diffs length does not match 'recorded'")
    allowed = {"index", "baseline_bytes", "candidate_bytes", "baseline_all_zero", "candidate_all_zero",
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
            if not isinstance(word, dict) or not set(word) <= {"word", "direction"} \
                    or isinstance(word.get("word"), bool) or not isinstance(word.get("word"), int) \
                    or ("direction" in word and word["direction"] not in ("higher", "lower")):
                raise CorpusResultsError(f"{path.name}: a differing word carries unexpected fields")


def _validate_timings_meta(path: Path) -> None:
    with path.open("r", encoding="utf-8") as source:
        data = json.load(source)
    required = {"head", "chain_id", "block_hash", "records", "passes", "requests", "target_rps", "achieved_rps",
                "concurrency", "warmup_seconds", "warmup_rps", "outcomes"}
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
    if not isinstance(data["outcomes"], dict):
        raise CorpusResultsError(f"{path.name}: outcomes is not an object")
    for key, value in data["outcomes"].items():
        if not STATUS_PATTERN.fullmatch(key) or isinstance(value, bool) or not isinstance(value, int):
            raise CorpusResultsError(f"{path.name}: unexpected outcome entry")


def _validate_resources(path: Path) -> None:
    with path.open("r", encoding="utf-8") as source:
        data = json.load(source, object_pairs_hook=_strict_json_object, parse_constant=_reject_json_constant)
    if not isinstance(data, dict) or not set(data) <= RESOURCE_FIELDS:
        raise CorpusResultsError(f"{path.name} does not match the resource schema")
    for key, value in data.items():
        if key in ("cpu_frequency_khz", "estimated_cycles_per_request", "perf_stat"):
            continue
        if value is not None and (isinstance(value, bool) or not isinstance(value, (int, float))
                                  or not math.isfinite(float(value)) or value < 0):
            raise CorpusResultsError(f"{path.name}: {key} is not numeric")
    frequency = data.get("cpu_frequency_khz")
    if frequency is not None:
        if not isinstance(frequency, dict) or not set(frequency) <= set(FREQUENCY_SOURCES):
            raise CorpusResultsError(f"{path.name}: invalid CPU frequency schema")
        for source, values in frequency.items():
            if not isinstance(values, dict) or set(values) != FREQUENCY_FIELDS:
                raise CorpusResultsError(f"{path.name}: invalid CPU frequency source {source}")
            for key in ("sample_count", "observation_count"):
                value = values[key]
                if isinstance(value, bool) or not isinstance(value, int) or value < 1:
                    raise CorpusResultsError(f"{path.name}: invalid CPU frequency count")
            if values["sample_count"] > values["observation_count"]:
                raise CorpusResultsError(f"{path.name}: CPU frequency sample count exceeds observations")
            numeric = [values[key] for key in ("avg_khz", "min_khz", "max_khz")]
            if any(isinstance(value, bool) or not isinstance(value, (int, float))
                   or not math.isfinite(float(value)) or value <= 0 for value in numeric):
                raise CorpusResultsError(f"{path.name}: invalid CPU frequency value")
            if not values["min_khz"] <= values["avg_khz"] <= values["max_khz"]:
                raise CorpusResultsError(f"{path.name}: CPU frequency range is inconsistent")
    estimates = data.get("estimated_cycles_per_request")
    if estimates is not None:
        if not isinstance(estimates, dict) or set(estimates) - set(FREQUENCY_SOURCES):
            raise CorpusResultsError(f"{path.name}: invalid cycle estimate schema")
        if not isinstance(frequency, dict) or not set(estimates) <= set(frequency):
            raise CorpusResultsError(f"{path.name}: cycle estimate has no frequency source")
        for value in estimates.values():
            if isinstance(value, bool) or not isinstance(value, (int, float)) \
                    or not math.isfinite(float(value)) or value <= 0:
                raise CorpusResultsError(f"{path.name}: invalid cycle estimate")
    perf = data.get("perf_stat")
    if perf is not None:
        if not isinstance(perf, dict) or set(perf) != PERF_STAT_FIELDS:
            raise CorpusResultsError(f"{path.name}: invalid perf stat schema")
        for key, value in perf.items():
            if isinstance(value, bool) or not isinstance(value, (int, float)) \
                    or not math.isfinite(float(value)) or value < 0:
                raise CorpusResultsError(f"{path.name}: invalid perf stat value for {key}")
        if perf["cycles"] <= 0 or perf["task_clock_ms"] <= 0 or perf["ipc"] < 0 or perf["effective_ghz"] <= 0:
            raise CorpusResultsError(f"{path.name}: perf stat has unusable required values")
        requests = data.get("requests")
        if isinstance(requests, int) and not isinstance(requests, bool) and requests > 0:
            for raw, per_request in (("task_clock_ms", "task_clock_ms_per_request"),
                                     ("cycles", "cycles_per_request"),
                                     ("instructions", "instructions_per_request")):
                if not math.isclose(perf[per_request], perf[raw] / requests, rel_tol=1e-6, abs_tol=1e-6):
                    raise CorpusResultsError(f"{path.name}: invalid perf per-request value")


def _stage_manifest(path: Path, source_root: Path, target: Path) -> bool:
    """Rewrite manifest paths relative to the artifact root; a malformed index drops only itself."""
    lines_out = []
    for number, line in enumerate(path.read_text(encoding="utf-8").splitlines(), start=1):
        if not line.strip():
            continue
        match = MANIFEST_LINE_PATTERN.fullmatch(line)
        if not match:
            print(f"::warning::{path.name}: line {number} does not match the manifest shape — index not staged", file=sys.stderr)
            return False
        try:
            relative = Path(match.group("path")).resolve().relative_to(source_root.resolve())
        except ValueError:
            print(f"::warning::{path.name}: line {number} points outside the output root — index not staged", file=sys.stderr)
            return False
        lines_out.append(f"{match.group('prefix')}={relative.as_posix()}")
    target.write_text("\n".join(lines_out) + "\n", encoding="utf-8")
    return True


VALIDATORS = {
    "summary.json": _validate_summary,
    "parity.json": _validate_parity,
    "timings.csv": _validate_timings,
    "parity-diffs.json": _validate_parity_diffs,
    "timings.meta.json": _validate_timings_meta,
    "resources.json": _validate_resources,
}


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
        parts = path.relative_to(source_root).parts
        if parts[0].startswith("warmup") or path.parent.name.startswith("warmup"):
            continue
        target = destination_root / path.relative_to(source_root)
        target.parent.mkdir(parents=True, exist_ok=True)
        try:
            if path.name == "summaries.manifest":
                staged += _stage_manifest(path, source_root, target)
                continue
            if path.name in VALIDATORS:
                VALIDATORS[path.name](path)
        except (OSError, ValueError, json.JSONDecodeError, UnicodeDecodeError) as error:
            raise CorpusResultsError(f"{path.name}: unreadable ({error.__class__.__name__})") from None
        shutil.copyfile(path, target)
        staged += 1
    if staged == 0:
        raise CorpusResultsError("no publishable result files found under the output root")
    print(f"staged {staged} aggregate file(s)")


def _family(label: str) -> str:
    return REPEAT_PATTERN.match(label).group(1)


def _mean(values: list[float]) -> float:
    return sum(values) / len(values)


def _spread_pct(values: list[float]) -> float | None:
    if len(values) < 2 or _mean(values) == 0:
        return None
    return (max(values) - min(values)) / _mean(values) * 100


def _delta_pct(base: float, cand: float) -> float:
    return (cand - base) / base * 100 if base else float("nan")


def _arrow(delta: float, floor: float) -> str:
    if math.isnan(delta):
        return "⚪"
    return "🟢" if delta < -floor else ("🔴" if delta > floor else "⚪")


def _load_timings(path: Path) -> dict[int, list[float]]:
    """record index -> ok latencies across passes."""
    samples: dict[int, list[float]] = {}
    with path.open(encoding="utf-8", newline="") as handle:
        reader = csv.reader(handle)
        header = next(reader)
        for row in reader:
            values = samples.setdefault(int(row[0]), [])
            for position in range(1, len(header) - 1, 2):
                if row[position] and row[position + 1] == "ok":
                    values.append(float(row[position]))
    return samples


def paired_timings(base_paths: list[Path], cand_paths: list[Path]) -> dict[str, Any] | None:
    """Per-record paired comparison: median of each record's ok samples per arm, then the per-record delta.

    A record counts as moved when its delta exceeds RECORD_SHIFT_PCT and twice its own spread across
    the baseline runs, so a few noisy samples per record do not read as a shift.
    """
    base: dict[int, list[float]] = {}
    cand: dict[int, list[float]] = {}
    base_runs: list[dict[int, float]] = []
    for paths, into in ((base_paths, base), (cand_paths, cand)):
        for path in paths:
            samples = _load_timings(path)
            if into is base:
                base_runs.append({r: statistics.median(v) for r, v in samples.items() if v})
            for record, values in samples.items():
                into.setdefault(record, []).extend(values)
    deltas: list[float] = []
    regressed = improved = 0
    for record in sorted(base):
        if not (base[record] and cand.get(record)):
            continue
        delta = _delta_pct(statistics.median(base[record]), statistics.median(cand[record]))
        deltas.append(delta)
        own = [run[record] for run in base_runs if record in run]
        threshold = max(RECORD_SHIFT_PCT, 2 * (_spread_pct(own) or 0.0))
        regressed += delta > threshold
        improved += delta < -threshold
    if not deltas:
        return None
    rng = random.Random(20260825)
    resampled = sorted(statistics.median(rng.choices(deltas, k=len(deltas))) for _ in range(BOOTSTRAP_ROUNDS))
    return {
        "records": len(deltas),
        "median_delta": statistics.median(deltas),
        "mean_delta": _mean(deltas),
        "ci_low": resampled[int(0.025 * BOOTSTRAP_ROUNDS)],
        "ci_high": resampled[int(0.975 * BOOTSTRAP_ROUNDS)],
        "regressed": regressed,
        "improved": improved,
    }


def _read_json(path: Path) -> dict:
    return json.loads(path.read_text(encoding="utf-8"))


def _collect(root: Path, baseline_label: str, candidate_label: str) -> dict:
    """Group staged files by corpus and arm; label repeats (X_r2) and slot repeats (100_r2) pool into one arm."""
    arms = {baseline_label: "master", candidate_label: "PR"}
    data: dict[str, dict] = {}
    for label_dir in sorted(p for p in root.glob("corpus/*/*") if p.is_dir()):
        arm = arms.get(_family(label_dir.name))
        if arm is None:
            continue
        corpus = data.setdefault(label_dir.parent.name, {"cells": {}, "timings": {}, "meta": {}, "parity": []})
        for cell in sorted(p for p in label_dir.iterdir() if p.is_dir() and (p / "summary.json").is_file()):
            slot = _family(cell.name)
            entry = corpus["cells"].setdefault(slot, {}).setdefault(arm, {"summaries": [], "cpu": []})
            entry["summaries"].append(_read_json(cell / "summary.json")["metrics"])
            if (cell / "resources.json").is_file():
                cpu = _read_json(cell / "resources.json").get("cpu_ms_per_request")
                if isinstance(cpu, (int, float)):
                    entry["cpu"].append(cpu)
        if (label_dir / "timings.csv").is_file():
            corpus["timings"].setdefault(arm, []).append(label_dir / "timings.csv")
        if (label_dir / "timings.meta.json").is_file():
            corpus["meta"].setdefault(arm, []).append(_read_json(label_dir / "timings.meta.json"))
        if arm == "PR" and (label_dir / "parity.json").is_file():
            corpus["parity"].append(_read_json(label_dir / "parity.json"))
    return data


def _slot_order(slot: str):
    head = slot.split("_")[0]
    return (int(head) if head.isdigit() else 0, slot)


def _render_cells(lines: list[str], slot: str, cell: dict) -> None:
    base, cand = cell.get("master"), cell.get("PR")
    if not base or not cand:
        lines.append(f"@ `{slot}` rps: missing a client, cannot compare.")
        return
    runs = f"n={len(base['summaries'])}/{len(cand['summaries'])} runs"
    b_requests = int(_mean([m["http_reqs"]["values"]["count"] for m in base["summaries"]]))
    c_requests = int(_mean([m["http_reqs"]["values"]["count"] for m in cand["summaries"]]))
    lines += [f"@ `{slot}` rps · {runs} · {b_requests}/{c_requests} requests/run", "",
              "| metric | master | PR | delta | A/A spread |", "|---|---|---|---|---|"]

    def row(name: str, base_values: list[float], cand_values: list[float], unit: str) -> None:
        spread = _spread_pct(base_values)
        floor = NOISE_FLOOR_PCT if spread is None else max(2 * spread, 1.0)  # an n=2 range is a ~1 sigma estimate
        delta = _delta_pct(_mean(base_values), _mean(cand_values))
        spread_text = f"{spread:.1f}%" if spread is not None else "n/a"
        lines.append(f"| {name} | {_mean(base_values):.2f}{unit} | {_mean(cand_values):.2f}{unit} | {_arrow(delta, floor)} {delta:+.1f}% | {spread_text} |")

    if base["cpu"] and cand["cpu"]:
        row("CPU-ms/request", base["cpu"], cand["cpu"], "")
    for key, name in COMMENT_METRICS:
        row(name, [m["http_req_duration"]["values"][key] for m in base["summaries"]],
            [m["http_req_duration"]["values"][key] for m in cand["summaries"]], " ms")
    b_fail = _mean([m["http_req_failed"]["values"]["rate"] for m in base["summaries"]]) * 100
    c_fail = _mean([m["http_req_failed"]["values"]["rate"] for m in cand["summaries"]]) * 100
    lines += ["", f"Failure rate — master {b_fail:.2f}%, PR {c_fail:.2f}%."]
    dropped = sum(int(m["dropped_iterations"]["values"]["count"]) for m in base["summaries"] + cand["summaries"])
    if dropped or abs(b_requests - c_requests) > REQUEST_MISMATCH_PCT / 100 * max(b_requests, c_requests):
        lines.append(f"⚠️ Unequal load — master {b_requests} vs PR {c_requests} requests/run, {dropped} iteration(s) dropped by k6: "
                     "the arms did not see the same sample, so the deltas above are not like for like.")
    lines.append("")

    base_classes = [m["classes"] for m in base["summaries"] if "classes" in m]
    cand_classes = [m["classes"] for m in cand["summaries"] if "classes" in m]
    if base_classes and cand_classes:
        names = sorted(set(base_classes[0]) & set(cand_classes[0]), key=lambda n: int(n.split("_")[1]))
        lines += ["| selector class | requests | master p50 | PR p50 | Δ p50 | master p99 | PR p99 | Δ p99 |",
                  "|---|---|---|---|---|---|---|---|"]
        for name in names:
            def value(sets, field):
                return _mean([s[name]["values"][field] for s in sets if name in s])
            p50 = _delta_pct(value(base_classes, "med"), value(cand_classes, "med"))
            p99 = _delta_pct(value(base_classes, "p(99)"), value(cand_classes, "p(99)"))
            lines.append(f"| {name} | {int(value(cand_classes, 'count'))} | {value(base_classes, 'med'):.2f} | {value(cand_classes, 'med'):.2f} "
                         f"| {_arrow(p50, NOISE_FLOOR_PCT)} {p50:+.1f}% | {value(base_classes, 'p(99)'):.2f} | {value(cand_classes, 'p(99)'):.2f} "
                         f"| {_arrow(p99, NOISE_FLOOR_PCT)} {p99:+.1f}% |")
        lines.append("")


def _render_timings(lines: list[str], corpus: dict) -> None:
    timings, meta = corpus["timings"], corpus["meta"]
    if "master" not in timings or "PR" not in timings:
        return
    paired = paired_timings(timings["master"], timings["PR"])
    if paired is None:
        return
    lines += [f"Paired per-record replay ({paired['records']} records, medians across passes/runs): "
              f"median delta {_arrow(paired['median_delta'], 1.0)} {paired['median_delta']:+.1f}% "
              f"(95% CI {paired['ci_low']:+.1f}% .. {paired['ci_high']:+.1f}%), mean {paired['mean_delta']:+.1f}%; "
              f"{paired['regressed']} records slower and {paired['improved']} faster beyond {RECORD_SHIFT_PCT:g}% "
              f"and twice their own A/A spread."]
    closed = {arm: [m["achieved_rps"] for m in metas if m.get("target_rps") == 0] for arm, metas in meta.items()}
    if closed.get("master") and closed.get("PR"):
        base, cand = _mean(closed["master"]), _mean(closed["PR"])
        delta = _delta_pct(base, cand)
        lines.append(f"Closed-loop throughput (concurrency {meta['PR'][0]['concurrency']}): master {base:.1f} req/s, "
                     f"PR {cand:.1f} req/s, {_arrow(-delta, 1.0)} {delta:+.1f}%.")
    failed = sum(v for metas in meta.values() for m in metas for k, v in m["outcomes"].items() if k != "ok")
    if failed:
        lines.append(f"⚠️ {failed} replay request(s) did not return a result — the paired figures above are not clean.")
    lines.append("")


def _render_parity(lines: list[str], reports: list[dict]) -> None:
    if not reports:
        lines.append("Response parity: not checked in this run (no baseline responses to compare against).")
    for data in reports:
        agree = data["matched"] + data.get("both_rpc_errors", 0)
        who = data.get("candidate_client", "PR")
        if agree == data["total"]:
            lines.append(f"Response parity ({who}): {agree}/{data['total']} identical to master.")
        else:
            defects = ", ".join(f"{k}={v}" for k, v in sorted(data.items())
                                if isinstance(v, int) and v and k not in ("total", "matched", "both_rpc_errors"))
            lines.append(f"Response parity ({who}): {agree}/{data['total']} **DIVERGES from master** — {defects}")


def comment(stage_root: str, baseline_label: str, candidate_label: str) -> str:
    """Render the PR comment from the STAGED tree, which is what enforces the aggregate-only boundary."""
    data = _collect(Path(stage_root), baseline_label, candidate_label)
    if not any(c["cells"] or c["timings"] for c in data.values()):
        return "No corpus cells were produced, so there is nothing to compare."
    lines: list[str] = ["### `eth_call` corpus — PR vs master", ""]
    for name in sorted(data):
        corpus = data[name]
        lines += [f"**`{name}`**", ""]
        for slot in sorted(corpus["cells"], key=_slot_order):
            _render_cells(lines, slot, corpus["cells"][slot])
        _render_timings(lines, corpus)
        _render_parity(lines, corpus["parity"])
        lines.append("")
    lines.append("<sub>Fixed corpus, seeded request sequence, arms interleaved. A/A spread is master against its own repeat: "
                 f"a delta within twice it (min 1%; ~{NOISE_FLOOR_PCT:g}% when no repeat ran) is noise. A PR that changes "
                 "results is a correctness regression regardless of latency.</sub>")
    return "\n".join(lines)


def main(argv: Sequence[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    subparsers = parser.add_subparsers(dest="command", required=True)
    sanitize_parser = subparsers.add_parser("sanitize", help="write a fixed aggregate-only summary")
    sanitize_parser.add_argument("raw")
    sanitize_parser.add_argument("out")
    stage_parser = subparsers.add_parser("stage", help="stage only validated aggregate files")
    stage_parser.add_argument("output_root")
    stage_parser.add_argument("stage_root")
    comment_parser = subparsers.add_parser("comment", help="render a PR comment from staged results")
    comment_parser.add_argument("stage_root")
    comment_parser.add_argument("--baseline", required=True)
    comment_parser.add_argument("--candidate", required=True)
    perf_parser = subparsers.add_parser("perf-merge", help="merge fixed perf aggregates into resources.json")
    perf_parser.add_argument("resources")
    perf_parser.add_argument("raw")
    perf_parser.add_argument("requests", type=int)
    perf_validate_parser = subparsers.add_parser("perf-validate", help="validate fixed perf aggregates")
    perf_validate_parser.add_argument("raw")
    subparsers.add_parser("perf-pidfd-preflight", help="verify identity-safe perf signaling")
    perf_signal_parser = subparsers.add_parser("perf-pidfd-signal", help="signal captured perf through a pidfd")
    perf_signal_parser.add_argument("pid", type=int)
    perf_signal_parser.add_argument("start_time")
    perf_signal_parser.add_argument("signal", choices=("INT", "TERM", "KILL"))
    arguments = parser.parse_args(argv)
    try:
        if arguments.command == "sanitize":
            sanitize(arguments.raw, arguments.out)
        elif arguments.command == "perf-merge":
            merge_perf(arguments.resources, arguments.raw, arguments.requests)
        elif arguments.command == "perf-validate":
            try:
                raw = Path(arguments.raw).read_text(encoding="utf-8")
            except (OSError, UnicodeDecodeError):
                raise _perf_error() from None
            normalize_perf_data(raw, 1)
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
