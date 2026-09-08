#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

"""Sample a benchmarked node container's resource use for the duration of a load cell.

Reads the container's cgroup v2 files from the host, so nothing is required inside the image
(reth, geth and Nethermind images differ in what shell and tooling they carry). Emits counters
and derived rates only — no request or response data can pass through here.

The headline number is CPU seconds per request: latency says how long a call took, this says how
much machine it cost, which is what distinguishes "slower because it does more work" from
"slower because it waits". Pressure stalls (PSI) separate a third case: waiting on IO or memory
rather than running at all.
"""

from __future__ import annotations

import argparse
import json
import signal
import subprocess
import sys
import time
from pathlib import Path
from typing import Sequence

CGROUP_ROOTS = ("/sys/fs/cgroup/system.slice/docker-{cid}.scope",
                "/sys/fs/cgroup/docker/{cid}",
                "/sys/fs/cgroup/kubepods/docker-{cid}.scope")


class ResourceSampleError(Exception):
    """Raised when the container's cgroup cannot be located or read."""


def _container_id(name: str) -> str:
    try:
        out = subprocess.run(["docker", "inspect", "--format", "{{.Id}}", name],
                             capture_output=True, text=True, timeout=30)
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
    """Parse 'key value' lines (cpu.stat, memory.stat)."""
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
    """'some avg10=.. total=N' -> N microseconds stalled."""
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


def sample(container: str, out_path: str, interval: float, should_stop=None) -> None:
    """Sample until should_stop() is true; by default until SIGTERM/SIGINT.

    The predicate is injectable so the loop and its arithmetic can be exercised without
    delivering a signal — Windows cannot deliver a catchable SIGTERM, and this runs on Linux.
    """
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
    psi_start = {name: _pressure_total(cgroup / f"{name}.pressure")
                 for name in ("cpu", "io", "memory")}

    memory_samples: list[int] = []
    peak_cores = 0.0
    last_t, last_cpu = started, cpu_start
    while not should_stop():
        time.sleep(interval)
        now = time.monotonic()
        current = _read_int(cgroup / "memory.current")
        if current is not None:
            memory_samples.append(current)
        cpu_now = cpu_usec()
        span = now - last_t
        if span > 0:
            peak_cores = max(peak_cores, (cpu_now - last_cpu) / 1e6 / span)
        last_t, last_cpu = now, cpu_now

    wall = time.monotonic() - started
    cpu_seconds = (cpu_usec() - cpu_start) / 1e6
    io_end = _io_totals(cgroup / "io.stat")
    throttle = _read_kv(cgroup / "cpu.stat")

    def psi_delta(name: str) -> int | None:
        end = _pressure_total(cgroup / f"{name}.pressure")
        start = psi_start.get(name)
        return None if end is None or start is None else end - start

    # Every figure here is a delta or a sample taken inside the window. cgroup lifetime values
    # (memory.peak, the raw throttled_usec) would fold node startup and DB warmup into what is
    # reported as a per-cell number, so the peak is the sampled maximum and throttling is a delta.
    summary = {
        "wall_seconds": round(wall, 3),
        "samples": len(memory_samples),
        "cpu_seconds": round(cpu_seconds, 3),
        "cpu_avg_cores": round(cpu_seconds / wall, 3) if wall > 0 else 0.0,
        "cpu_peak_cores": round(peak_cores, 3),
        "cpu_throttled_usec": throttle.get("throttled_usec", 0) - throttled_start,
        "memory_avg_bytes": int(sum(memory_samples) / len(memory_samples)) if memory_samples else 0,
        "memory_peak_bytes": max(memory_samples) if memory_samples else 0,
        "io_read_bytes": io_end[0] - io_start[0],
        "io_write_bytes": io_end[1] - io_start[1],
        "stall_cpu_usec": psi_delta("cpu"),
        "stall_io_usec": psi_delta("io"),
        "stall_memory_usec": psi_delta("memory"),
        # The delivered count is not known until the cell reports it; `normalize` fills it in.
        "requests": 0,
    }
    _write(out_path, _with_rates(summary))


def _with_rates(summary: dict) -> dict:
    """Add per-request costs when a request count is known; drop stale ones when it is not."""
    summary.pop("cpu_ms_per_request", None)
    summary.pop("io_read_bytes_per_request", None)
    requests = summary.get("requests", 0)
    if isinstance(requests, int) and requests > 0:
        summary["cpu_ms_per_request"] = round(summary["cpu_seconds"] * 1000 / requests, 4)
        summary["io_read_bytes_per_request"] = round(summary["io_read_bytes"] / requests, 1)
    return summary


def _write(out_path: str, summary: dict) -> None:
    target = Path(out_path)
    target.parent.mkdir(parents=True, exist_ok=True)
    with target.open("w", encoding="utf-8") as handle:
        json.dump(summary, handle, sort_keys=True, separators=(",", ":"))
    print(f"resources: {summary['cpu_avg_cores']} avg cores, "
          f"{summary['memory_peak_bytes'] / 1e9:.2f} GB peak, "
          f"{summary.get('cpu_ms_per_request', 'n/a')} CPU-ms/request", flush=True)


def normalize(out_path: str, requests: int) -> None:
    """Restate an existing sample against the request count the load actually delivered.

    The sampler cannot know it: deriving requests from rate x duration assumes an integer-second
    duration and that k6 dropped no iterations. Both are false in general, so the count comes from
    the benchmark's own `http_reqs` after the cell.
    """
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

    normalize_parser = subparsers.add_parser(
        "normalize", help="restate a sample against the delivered request count")
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
        return 0  # never fail a benchmark because sampling could not start
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
