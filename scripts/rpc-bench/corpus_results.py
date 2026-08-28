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
import json
import math
import random
import re
import shutil
import statistics
import sys
from pathlib import Path
from typing import Any, Sequence

from corpus_parity import PARITY_COUNTER_FIELDS, PARITY_LABEL_FIELDS

# Identity of the staged tree this module writes and reads. Bump it whenever a staged file's schema changes
# (METRIC_FIELDS, STAGED_FILENAMES, the validators below): the workflow mirrors it, and a cached master baseline
# staged at another schema is then dropped with a warning instead of raising here, mid-render.
BASELINE_SCHEMA = 1

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
}
_LABEL = r"[A-Za-z0-9._-]+"
MANIFEST_LINE_PATTERN = re.compile(
    rf"(?P<prefix>iso\|{_LABEL}\|{_LABEL}\|{_LABEL}|mix\|{_LABEL}\|{_LABEL})=(?P<path>.+jsonbench-summary\.md)$")
COMMENT_METRICS = (("avg", "avg"), ("med", "median"), ("p(90)", "p90"), ("p(95)", "p95"), ("p(99)", "p99"), ("max", "max"))
NOISE_FLOOR_PCT = 2.5
# With a cached master baseline the arms are not co-run: master was measured in another job on another day, so
# day-to-day drift (page cache, snapshot copy, kernel, ambient thermals) is inside the delta and no A/A control in
# the run measures it. NOISE_FLOOR_PCT was calibrated from in-run repeats, so it does not apply; widen it until
# consecutive master baselines have been used to measure the cross-job spread, and say so under the table.
CACHED_NOISE_FLOOR_PCT = 5.0
REQUEST_MISMATCH_PCT = 1.0
RECORD_SHIFT_PCT = 5.0
BOOTSTRAP_ROUNDS = 2000


class CorpusResultsError(Exception):
    """Content-free failure of sanitizing or staging."""


def _number(value: Any, label: str) -> int | float:
    if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(float(value)) or value < 0:
        raise CorpusResultsError(f"{label} is not a finite non-negative number")
    return value


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
        data = json.load(source)
    if not isinstance(data, dict) or not set(data) <= RESOURCE_FIELDS:
        raise CorpusResultsError(f"{path.name} does not match the resource schema")
    for key, value in data.items():
        if value is not None and (isinstance(value, bool) or not isinstance(value, (int, float))):
            raise CorpusResultsError(f"{path.name}: {key} is not numeric")


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
        except (OSError, json.JSONDecodeError, UnicodeDecodeError) as error:
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


def _render_cells(lines: list[str], slot: str, cell: dict, no_repeat_floor: float = NOISE_FLOOR_PCT) -> None:
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
        floor = no_repeat_floor if spread is None else max(2 * spread, 1.0)  # an n=2 range is a ~1 sigma estimate
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
                         f"| {_arrow(p50, no_repeat_floor)} {p50:+.1f}% | {value(base_classes, 'p(99)'):.2f} | {value(cand_classes, 'p(99)'):.2f} "
                         f"| {_arrow(p99, no_repeat_floor)} {p99:+.1f}% |")
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
        levels = sorted({m["concurrency"] for metas in meta.values() for m in metas})
        where = f"concurrency {levels[0]}" if len(levels) == 1 \
            else "mixed concurrency " + "/".join(str(level) for level in levels) + ", not comparable"
        lines.append(f"Closed-loop throughput ({where}): master {base:.1f} req/s, "
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


def comment(stage_root: str, baseline_label: str, candidate_label: str, cached_baseline: bool = False) -> str:
    """Render the PR comment from the STAGED tree, which is what enforces the aggregate-only boundary.

    `cached_baseline` says master's numbers came from the cached master baseline instead of an arm run in this
    job, which widens the noise floor the arrows use wherever no A/A repeat measured one.
    """
    data = _collect(Path(stage_root), baseline_label, candidate_label)
    if not any(c["cells"] or c["timings"] for c in data.values()):
        return "No corpus cells were produced, so there is nothing to compare."
    floor = CACHED_NOISE_FLOOR_PCT if cached_baseline else NOISE_FLOOR_PCT
    lines: list[str] = ["### `eth_call` corpus — PR vs master", ""]
    for name in sorted(data):
        corpus = data[name]
        lines += [f"**`{name}`**", ""]
        for slot in sorted(corpus["cells"], key=_slot_order):
            _render_cells(lines, slot, corpus["cells"][slot], floor)
        _render_timings(lines, corpus)
        _render_parity(lines, corpus["parity"])
        lines.append("")
    lines.append("<sub>Fixed corpus, seeded request sequence, arms interleaved. A/A spread is master against its own repeat: "
                 f"a delta within twice it (min 1%; ~{floor:g}% when no repeat ran) is noise. A PR that changes "
                 "results is a correctness regression regardless of latency.</sub>")
    if cached_baseline:
        lines.append("")
        lines.append(f"<sub>⚠️ master was **not co-run** here: its numbers come from the cached master baseline, so this run "
                     f"carries no A/A control and the drift between the two jobs is measured nowhere. The no-repeat floor above "
                     f"is therefore {CACHED_NOISE_FLOOR_PCT:g}%, not the in-run {NOISE_FLOOR_PCT:g}%; for a delta near it, re-run with "
                     f"`baseline_image=<master image>` (both arms in one job) and `rounds=2`.</sub>")
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
    comment_parser.add_argument("--cached-baseline", action="store_true",
                                help="master's numbers came from the cached baseline, not from an arm run here")
    arguments = parser.parse_args(argv)
    try:
        if arguments.command == "sanitize":
            sanitize(arguments.raw, arguments.out)
        elif arguments.command == "stage":
            stage(arguments.output_root, arguments.stage_root)
        else:
            print(comment(arguments.stage_root, arguments.baseline, arguments.candidate, arguments.cached_baseline))
    except CorpusResultsError as error:
        print(f"error: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
