#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

"""Sample a node container's cgroup v2 counters for the duration of a load cell.

Emits counters and derived rates only. The headline is CPU-ms per request: latency says how long a
call took, this says how much machine it cost.
"""

from __future__ import annotations

import argparse
import json
import math
import re
import signal
import subprocess
import sys
import time
from pathlib import Path
from typing import Sequence

CGROUP_ROOTS = ("/sys/fs/cgroup/system.slice/docker-{cid}.scope",
                "/sys/fs/cgroup/docker/{cid}",
                "/sys/fs/cgroup/kubepods/docker-{cid}.scope")
CPU_FREQ_ROOT = Path("/sys/devices/system/cpu")
CPU_FREQ_SOURCES = ("scaling_cur_freq", "cpuinfo_cur_freq")
CPPC_FREQUENCY_SOURCE = "cppc_delivered_perf"
CPU_FREQUENCY_SOURCES = CPU_FREQ_SOURCES + (CPPC_FREQUENCY_SOURCE,)
CPU_FREQUENCY_UNAVAILABLE_FIELD = "cpu_frequency_unavailable_sources"
MAX_CPU_FREQ_KHZ = 10_000_000
_CPPC_COUNTER_PATTERN = re.compile(r"([A-Za-z][A-Za-z0-9_-]*)\s*[:=]\s*(-?[0-9]+)")
_CPPC_COUNTER_NAMES = {"ref": "reference", "del": "delivered",
                       "reference": "reference", "delivered": "delivered"}


class ResourceSampleError(Exception):
    """The container's cgroup cannot be located or read."""


def _container_id(name: str) -> str:
    try:
        out = subprocess.run(["docker", "inspect", "--format", "{{.Id}}", name], capture_output=True, text=True, timeout=30)
    except (OSError, subprocess.SubprocessError) as error:
        raise ResourceSampleError(f"cannot inspect container: {error.__class__.__name__}") from None
    if out.returncode != 0:
        raise ResourceSampleError("container not found")
    return out.stdout.strip()


def _cgroup_dir(container_id: str) -> Path:
    for template in CGROUP_ROOTS:
        candidate = Path(template.format(cid=container_id))
        if (candidate / "cpu.stat").is_file():
            return candidate
    raise ResourceSampleError("no cgroup v2 directory found for the container")


def _read_kv(path: Path) -> dict[str, int]:
    values: dict[str, int] = {}
    try:
        for line in path.read_text().splitlines():
            parts = line.split()
            if len(parts) == 2 and parts[1].lstrip("-").isdigit():
                values[parts[0]] = int(parts[1])
    except OSError:
        pass
    return values


def _read_int(path: Path) -> int | None:
    try:
        text = path.read_text().strip()
    except OSError:
        return None
    return int(text) if text.isdigit() else None


def _pressure_total(path: Path) -> int | None:
    try:
        for line in path.read_text().splitlines():
            if line.startswith("some"):
                for field in line.split():
                    if field.startswith("total="):
                        return int(field.split("=", 1)[1])
    except (OSError, ValueError):
        return None
    return None


def _io_totals(path: Path) -> tuple[int, int]:
    read = write = 0
    try:
        for line in path.read_text().splitlines():
            for field in line.split():
                if field.startswith("rbytes="):
                    read += int(field.split("=", 1)[1])
                elif field.startswith("wbytes="):
                    write += int(field.split("=", 1)[1])
    except (OSError, ValueError):
        pass
    return read, write


def _cpu_frequency_values(source: str) -> list[int]:
    """Read positive, plausible kHz values for one cpufreq source.

    Frequency sysfs files are host-dependent: a source may be absent, unreadable, or exposed
    only for a subset of CPUs.  The sampler treats each readable file independently and never
    turns an unavailable source into a zero-valued measurement.
    """
    values: list[int] = []
    try:
        paths = sorted(CPU_FREQ_ROOT.glob(f"cpu[0-9]*/cpufreq/{source}"))
    except OSError:
        return values
    for path in paths:
        value = _read_int(path)
        if value is not None and 0 < value <= MAX_CPU_FREQ_KHZ:
            values.append(value)
    return values


def _cppc_counters(path: Path) -> tuple[int, int] | None:
    try:
        text = path.read_text()
    except OSError:
        return None
    matches = list(_CPPC_COUNTER_PATTERN.finditer(text))
    if not matches or any(match.group(1) not in _CPPC_COUNTER_NAMES for match in matches):
        return None
    values: dict[str, int] = {}
    for match in matches:
        name, value = _CPPC_COUNTER_NAMES[match.group(1)], int(match.group(2))
        if name in values or value < 0:
            return None
        values[name] = value
    remainder = _CPPC_COUNTER_PATTERN.sub("", text).strip(" \t\r\n,;")
    if remainder or set(values) != {"delivered", "reference"}:
        return None
    return values["delivered"], values["reference"]


def _positive_number(path: Path) -> float | None:
    try:
        value = float(path.read_text().strip())
    except (OSError, ValueError):
        return None
    return value if math.isfinite(value) and value > 0 else None


class _CppcFrequencySampler:
    """Convert ACPI CPPC feedback-counter deltas into per-CPU delivered kHz samples."""

    def __init__(self, root: Path):
        self.root = Path(root)
        self._previous: dict[str, tuple[int, int, float, float, float]] = {}

    def sample(self) -> list[float]:
        values: list[float] = []
        current_cpus: set[str] = set()
        current: dict[str, tuple[int, int, float, float, float]] = {}
        try:
            paths = sorted(self.root.glob("cpu[0-9]*/acpi_cppc"))
        except OSError:
            paths = []
        for directory in paths:
            cpu = directory.parent.name
            current_cpus.add(cpu)
            counters = _cppc_counters(directory / "feedback_ctrs")
            metadata = tuple(_positive_number(directory / name)
                             for name in ("reference_perf", "nominal_perf", "nominal_freq"))
            if counters is None or any(value is None for value in metadata):
                continue
            delivered, reference = counters
            reference_perf, nominal_perf, nominal_freq = metadata
            reading = (delivered, reference, reference_perf, nominal_perf, nominal_freq)
            previous = self._previous.get(cpu)
            current[cpu] = reading
            if previous is None or previous[2:] != reading[2:]:
                continue
            delivered_delta = delivered - previous[0]
            reference_delta = reference - previous[1]
            if delivered_delta <= 0 or reference_delta <= 0:
                continue
            frequency = (nominal_freq * reference_perf * delivered_delta
                         / (nominal_perf * reference_delta) * 1000)
            if math.isfinite(frequency) and 0 < frequency <= MAX_CPU_FREQ_KHZ:
                values.append(frequency)
        # A missing or malformed CPU must not retain a stale baseline if it reappears later.
        self._previous = {cpu: reading for cpu, reading in current.items() if cpu in current_cpus}
        return values


def _frequency_observations(cppc_sampler: _CppcFrequencySampler | None = None) -> dict[str, list[float]]:
    observations: dict[str, list[float]] = {source: _cpu_frequency_values(source) for source in CPU_FREQ_SOURCES}
    if cppc_sampler is not None:
        observations[CPPC_FREQUENCY_SOURCE] = cppc_sampler.sample()
    return observations


def _frequency_summary(observations: dict[str, dict[str, int | float]]) -> dict[str, dict[str, int | float]]:
    """Return bounded aggregate values for sources that produced at least one observation."""
    result: dict[str, dict[str, int | float]] = {}
    for source, stats in observations.items():
        count = stats["observation_count"]
        if not isinstance(count, int) or count < 1:
            continue
        result[source] = {
            "sample_count": stats["sample_count"],
            "observation_count": count,
            "avg_khz": round(float(stats["total_khz"]) / count, 3),
            "min_khz": stats["min_khz"],
            "max_khz": stats["max_khz"],
        }
    return result


def sample(container: str, out_path: str, interval: float, should_stop=None) -> None:
    """Sample until should_stop() (default: SIGTERM/SIGINT). Every figure is a delta or a sample inside the window."""
    cgroup = _cgroup_dir(_container_id(container))
    stop = {"now": False}
    if should_stop is None:
        for sig in (signal.SIGTERM, signal.SIGINT):
            signal.signal(sig, lambda *_: stop.__setitem__("now", True))
        should_stop = lambda: stop["now"]  # noqa: E731

    def cpu_usec() -> int:
        return _read_kv(cgroup / "cpu.stat").get("usage_usec", 0)

    started = time.monotonic()
    cpu_start = cpu_usec()
    throttled_start = _read_kv(cgroup / "cpu.stat").get("throttled_usec", 0)
    io_start = _io_totals(cgroup / "io.stat")
    psi_start = {name: _pressure_total(cgroup / f"{name}.pressure") for name in ("cpu", "io", "memory")}
    memory_samples: list[int] = []
    cppc_sampler = _CppcFrequencySampler(CPU_FREQ_ROOT)
    frequency_stats: dict[str, dict[str, int | float]] = {
        source: {
            "sample_count": 0,
            "observation_count": 0,
            "total_khz": 0,
            "min_khz": MAX_CPU_FREQ_KHZ,
            "max_khz": 0,
        }
        for source in CPU_FREQUENCY_SOURCES
    }
    peak_cores = 0.0
    last_t, last_cpu = started, cpu_start
    while not should_stop():
        time.sleep(interval)
        now = time.monotonic()
        current = _read_int(cgroup / "memory.current")
        if current is not None:
            memory_samples.append(current)
        for source, values in _frequency_observations(cppc_sampler).items():
            if not values:
                continue
            stats = frequency_stats[source]
            stats["sample_count"] += 1
            stats["observation_count"] += len(values)
            stats["total_khz"] += sum(values)
            stats["min_khz"] = min(stats["min_khz"], min(values))
            stats["max_khz"] = max(stats["max_khz"], max(values))
        cpu_now = cpu_usec()
        span = now - last_t
        if span > 0:
            peak_cores = max(peak_cores, (cpu_now - last_cpu) / 1e6 / span)
        last_t, last_cpu = now, cpu_now

    wall = time.monotonic() - started
    cpu_seconds = (cpu_usec() - cpu_start) / 1e6
    io_end = _io_totals(cgroup / "io.stat")

    def psi_delta(name: str) -> int | None:
        end = _pressure_total(cgroup / f"{name}.pressure")
        start = psi_start.get(name)
        return None if end is None or start is None else end - start

    summary = {
        "wall_seconds": round(wall, 3),
        "samples": len(memory_samples),
        "cpu_seconds": round(cpu_seconds, 3),
        "cpu_avg_cores": round(cpu_seconds / wall, 3) if wall > 0 else 0.0,
        "cpu_peak_cores": round(peak_cores, 3),
        "cpu_throttled_usec": _read_kv(cgroup / "cpu.stat").get("throttled_usec", 0) - throttled_start,
        "memory_avg_bytes": int(sum(memory_samples) / len(memory_samples)) if memory_samples else 0,
        "memory_peak_bytes": max(memory_samples) if memory_samples else 0,
        "io_read_bytes": io_end[0] - io_start[0],
        "io_write_bytes": io_end[1] - io_start[1],
        "stall_cpu_usec": psi_delta("cpu"),
        "stall_io_usec": psi_delta("io"),
        "stall_memory_usec": psi_delta("memory"),
        "requests": 0,  # filled in by `normalize` once the cell reports its delivered count
    }
    frequency = _frequency_summary(frequency_stats)
    if frequency:
        summary["cpu_frequency_khz"] = frequency
    unavailable = [source for source in CPU_FREQUENCY_SOURCES
                   if frequency_stats[source]["observation_count"] < 1]
    if unavailable:
        summary[CPU_FREQUENCY_UNAVAILABLE_FIELD] = unavailable
    _write(out_path, _with_rates(summary))


def _with_rates(summary: dict) -> dict:
    summary.pop("cpu_ms_per_request", None)
    summary.pop("io_read_bytes_per_request", None)
    summary.pop("estimated_cycles_per_request", None)
    requests = summary.get("requests", 0)
    if isinstance(requests, int) and requests > 0:
        summary["cpu_ms_per_request"] = round(summary["cpu_seconds"] * 1000 / requests, 4)
        summary["io_read_bytes_per_request"] = round(summary["io_read_bytes"] / requests, 1)
        frequency = summary.get("cpu_frequency_khz")
        if isinstance(frequency, dict):
            estimates: dict[str, float] = {}
            for source, values in frequency.items():
                if not isinstance(values, dict):
                    continue
                average = values.get("avg_khz")
                if isinstance(average, (int, float)) and not isinstance(average, bool) and average > 0:
                    estimates[source] = round(summary["cpu_ms_per_request"] * average, 3)
            if estimates:
                summary["estimated_cycles_per_request"] = estimates
    return summary


def _write(out_path: str, summary: dict) -> None:
    target = Path(out_path)
    target.parent.mkdir(parents=True, exist_ok=True)
    with target.open("w", encoding="utf-8") as handle:
        json.dump(summary, handle, sort_keys=True, separators=(",", ":"))
    print(f"resources: {summary['cpu_avg_cores']} avg cores, {summary['memory_peak_bytes'] / 1e9:.2f} GB peak, "
          f"{summary.get('cpu_ms_per_request', 'n/a')} CPU-ms/request", flush=True)


def normalize(out_path: str, requests: int) -> None:
    """Restate a sample against the request count the load actually delivered."""
    target = Path(out_path)
    try:
        with target.open("r", encoding="utf-8") as handle:
            summary = json.load(handle)
    except (OSError, json.JSONDecodeError) as error:
        raise ResourceSampleError(f"cannot read sample: {error.__class__.__name__}") from None
    if not isinstance(summary, dict) or "cpu_seconds" not in summary:
        raise ResourceSampleError("sample is not a resource summary")
    summary["requests"] = requests
    _write(out_path, _with_rates(summary))


def main(argv: Sequence[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    subparsers = parser.add_subparsers(dest="command", required=True)
    sample_parser = subparsers.add_parser("sample", help="sample until SIGTERM")
    sample_parser.add_argument("--container", required=True)
    sample_parser.add_argument("--out", required=True)
    sample_parser.add_argument("--interval", type=float, default=0.25)
    normalize_parser = subparsers.add_parser("normalize", help="restate a sample against the delivered request count")
    normalize_parser.add_argument("--out", required=True)
    normalize_parser.add_argument("--requests", type=int, required=True)
    arguments = parser.parse_args(argv)
    try:
        if arguments.command == "sample":
            sample(arguments.container, arguments.out, arguments.interval)
        else:
            normalize(arguments.out, arguments.requests)
    except ResourceSampleError as error:
        print(f"resource sampling unavailable: {error}", file=sys.stderr)
    return 0  # sampling must never fail a benchmark


if __name__ == "__main__":
    raise SystemExit(main())
