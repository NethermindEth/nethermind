#!/usr/bin/env python3
"""Validate one public EXPB arm and summarize its latency/resource artifacts."""

from __future__ import annotations

import argparse
import csv
import datetime as datetime_module
import json
import re
import statistics
import sys
from bisect import bisect_right
from pathlib import Path
from typing import Any, Sequence

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
from expb_account_cv import ARMS, HarnessError  # noqa: E402


ANSI = re.compile(r"\x1b\[[0-?]*[ -/]*[@-~]")
TABLE_ROW = re.compile(r"^\s*\|\s*(\d+)\s*\|\s*[^|]+\|\s*([0-9]+(?:\.[0-9]+)?)\s*\|\s*$")
SSE_ROW = re.compile(
    r"\[payload-server\]\s+client_metric\s+block_number=(\d+)\s+processing_ms=([0-9]+(?:\.[0-9]+)?)"
)
PROCESSED_ROW = re.compile(
    r"^(\d{2} \w{3} \d{2}:\d{2}:\d{2}).*?\|\s*Processed\s+(?:block\s+)?(\d+)\s*\|\s*([\d,.]+)\s*ms",
    re.IGNORECASE,
)
RECEIVED_ROW = re.compile(r"^(\d{2} \w{3} \d{2}:\d{2}:\d{2}).*?Received New Block:\s+(\d+)", re.IGNORECASE)


def _time(value: str, year: int) -> float:
    parsed = datetime_module.datetime.strptime(f"{year} {value}", "%Y %d %b %H:%M:%S")
    return parsed.replace(tzinfo=datetime_module.timezone.utc).timestamp()


def _samples(path: Path) -> dict[str, Any]:
    if not path.is_file():
        raise HarnessError(f"resource samples are missing: {path}")
    with path.open(newline="", encoding="utf-8") as handle:
        rows = list(csv.DictReader(handle))
    if not rows:
        raise HarnessError(f"resource samples are empty: {path}")
    container_ids = {row.get("container_id", "") for row in rows}
    if len(container_ids) != 1 or "" in container_ids:
        raise HarnessError(f"resource samples do not identify one client container: {path}")
    numeric = []
    for row in rows:
        numeric.append({key: float(row[key]) for key in ("epoch", "cpu_usec", "memory_current", "memory_anon", "memory_file")})
    first = numeric[0]
    last = numeric[-1]
    if last["cpu_usec"] < first["cpu_usec"]:
        raise HarnessError(f"client CPU counter went backwards: {path}")
    return {
        "sample_count": len(numeric),
        "lifecycle_cpu_seconds": round((last["cpu_usec"] - first["cpu_usec"]) / 1_000_000, 6),
        "lifecycle_memory_current_avg_bytes": round(statistics.mean(row["memory_current"] for row in numeric)),
        "lifecycle_memory_current_peak_bytes": round(max(row["memory_current"] for row in numeric)),
        "lifecycle_memory_anon_avg_bytes": round(statistics.mean(row["memory_anon"] for row in numeric)),
        "lifecycle_memory_anon_peak_bytes": round(max(row["memory_anon"] for row in numeric)),
        "lifecycle_memory_file_avg_bytes": round(statistics.mean(row["memory_file"] for row in numeric)),
        "lifecycle_memory_file_peak_bytes": round(max(row["memory_file"] for row in numeric)),
        "samples": numeric,
    }


def _measured_resources(samples: dict[str, Any], start: float | None, end: float | None) -> dict[str, Any]:
    rows = samples.pop("samples")
    if start is None or end is None or end < start:
        raise HarnessError("measured resource window is unavailable")
    times = [row["epoch"] for row in rows]
    selected = [row for row in rows if start <= row["epoch"] <= end]
    if len(selected) < 2:
        raise HarnessError("resource samples do not bracket the measured payload window")
    result: dict[str, Any] = {
        "measured_resource_samples": len(selected),
        "measured_memory_current_avg_bytes": round(statistics.mean(row["memory_current"] for row in selected)),
        "measured_memory_current_peak_bytes": round(max(row["memory_current"] for row in selected)),
        "measured_memory_anon_avg_bytes": round(statistics.mean(row["memory_anon"] for row in selected)),
        "measured_memory_anon_peak_bytes": round(max(row["memory_anon"] for row in selected)),
        "measured_memory_file_avg_bytes": round(statistics.mean(row["memory_file"] for row in selected)),
        "measured_memory_file_peak_bytes": round(max(row["memory_file"] for row in selected)),
    }
    if times[0] <= start <= times[-1] and times[0] <= end <= times[-1]:
        cpu = [row["cpu_usec"] for row in rows]

        def at(value: float) -> float:
            index = bisect_right(times, value)
            if index == 0:
                return cpu[0]
            if index == len(times):
                return cpu[-1]
            fraction = (value - times[index - 1]) / (times[index] - times[index - 1])
            return cpu[index - 1] + fraction * (cpu[index] - cpu[index - 1])

        result["measured_cpu_seconds_approx"] = round((at(end) - at(start)) / 1_000_000, 6)
    else:
        raise HarnessError("resource samples do not bracket the measured CPU window")
    return result


def analyze(arm_label: str, log_path: Path, sample_path: Path) -> dict[str, Any]:
    arm = next((item for item in ARMS if item.label == arm_label), None)
    if arm is None:
        raise HarnessError(f"unknown arm {arm_label}")
    clean = ANSI.sub("", log_path.read_text(encoding="utf-8"))
    lines = clean.splitlines()
    year_match = re.search(r"timestamp=(\d{4})-", clean)
    if year_match is None:
        raise HarnessError(f"{arm_label}: EXPB timestamp year is missing")
    year = int(year_match[1])
    payload_rows = [(int(match[1]), float(match[2])) for line in lines if (match := TABLE_ROW.match(line))]
    if len(payload_rows) != 1000 or len({item[0] for item in payload_rows}) != 1000:
        raise HarnessError(f"{arm_label}: expected 1000 unique per-payload rows, got {len(payload_rows)}")
    payload_ids = [item[0] for item in payload_rows]
    if payload_ids != list(range(payload_ids[0], payload_ids[0] + 1000)):
        raise HarnessError(f"{arm_label}: payload ids are not contiguous")

    sse = {int(match[1]): float(match[2]) for match in SSE_ROW.finditer(clean)}
    native: list[dict[str, Any]] = []
    for line in lines:
        match = PROCESSED_ROW.match(line)
        if match:
            native.append({"block": int(match[2]), "ms": float(match[3].replace(",", "")), "epoch": _time(match[1], year)})
    if len(native) < 1000:
        raise HarnessError(f"{arm_label}: expected at least 1000 native Processed rows, got {len(native)}")
    measured_native = native[-1000:]
    blocks = [item["block"] for item in measured_native]
    if blocks != list(range(blocks[0], blocks[0] + 1000)):
        raise HarnessError(f"{arm_label}: native block ids are not contiguous")
    native_by_block = {item["block"]: item for item in measured_native}
    received: dict[int, float] = {}
    for line in lines:
        match = RECEIVED_ROW.match(line)
        if match:
            received[int(match[2])] = _time(match[1], year)
    if measured_native[0]["block"] not in received:
        raise HarnessError(f"{arm_label}: first measured block has no Received New Block timestamp")
    if not 999 <= len(sse) <= 1000:
        raise HarnessError(f"{arm_label}: expected 999 or 1000 SSE metrics, got {len(sse)}")
    if not set(sse).issubset(native_by_block):
        raise HarnessError(f"{arm_label}: SSE contains an unknown block id")
    values: list[float] = []
    fallback = 0
    for item in measured_native:
        value = sse.get(item["block"])
        if value is None:
            value = item["ms"]
            fallback += 1
        elif abs(value - item["ms"]) > 0.11:
            raise HarnessError(f"{arm_label}: SSE/native timing mismatch for block {item['block']}")
        values.append(value)
    native_lines = [line for line in lines if re.match(r"^\d{2} \w{3} \d{2}:\d{2}:\d{2}", line)]
    prohibited = {
        term: sum(term.lower() in line.lower() for line in native_lines)
        for term in ("Exception", "Invalid Block", "Invalid Blocks", "Unhandled", "Fatal", "ERROR")
    }
    prohibited["expb_level_error"] = len(re.findall(r"\blevel=error\b", clean, re.IGNORECASE))
    prohibited["container_error"] = sum(
        bool(re.search(r"\berror=\S+", line, re.IGNORECASE))
        for line in lines
    )
    if any(prohibited.values()):
        raise HarnessError(f"{arm_label}: prohibited runtime log signal: {prohibited}")
    health = {
        **prohibited,
        "clean_shutdown": "Nethermind is shut down" in clean,
        "cleanup_completed": 'event="Cleanup completed"' in clean,
    }
    if not health["clean_shutdown"] or not health["cleanup_completed"]:
        raise HarnessError(f"{arm_label}: normal shutdown markers are missing")

    resource = _samples(sample_path)
    resource_lifecycle = dict(resource)
    resource_lifecycle.pop("samples", None)
    measured_start = received[measured_native[0]["block"]]
    measured = _measured_resources(resource, measured_start, measured_native[-1]["epoch"] + 1)
    return {
        "schema_version": 1,
        "arm": arm.label,
        "mode": arm.mode,
        "threshold": arm.threshold,
        "search_type": arm.search_type,
        "payload_count": len(payload_rows),
        "payload_first": payload_ids[0],
        "payload_last": payload_ids[-1],
        "sse_count": len(sse),
        "native_fallback_count": fallback,
        "block_first": blocks[0],
        "block_last": blocks[-1],
        "mean_ms": statistics.mean(values),
        "median_ms": statistics.median(values),
        "p95_nearest_rank_ms": sorted(values)[max(0, (95 * len(values) + 99) // 100 - 1)],
        "p99_nearest_rank_ms": sorted(values)[max(0, (99 * len(values) + 99) // 100 - 1)],
        "health": health,
        "expb_sampled": resource_lifecycle,
        "expb_measured": measured,
        "blocks": [{"payload": payload_ids[index], "block": item["block"], "processing_ms": values[index]} for index, item in enumerate(measured_native)],
    }


def main(argv: Sequence[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--arm", required=True)
    parser.add_argument("--log", required=True)
    parser.add_argument("--samples", required=True)
    parser.add_argument("--output", required=True)
    arguments = parser.parse_args(argv)
    try:
        value = analyze(arguments.arm, Path(arguments.log), Path(arguments.samples))
        Path(arguments.output).write_text(json.dumps(value, indent=2, sort_keys=True) + "\n", encoding="utf-8")
        print(json.dumps({key: value[key] for key in ("arm", "payload_count", "sse_count", "native_fallback_count", "mean_ms")}, sort_keys=True))
        return 0
    except (OSError, UnicodeError, HarnessError, ValueError) as error:
        print(f"analyze-arm: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
