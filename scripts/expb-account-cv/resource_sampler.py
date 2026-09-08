#!/usr/bin/env python3
"""Sample only the exact EXPB Nethermind containers for the five CV arms.

The helper container has a different name and is never selected.  Samples are
written as raw cgroup counters so lifecycle and measured-window costs can be
computed independently after the public EXPB logs have been validated.
"""

from __future__ import annotations

import argparse
import csv
import signal
import subprocess
import time
from pathlib import Path
from typing import Sequence


CGROUP_TEMPLATES = (
    "/sys/fs/cgroup/system.slice/docker-{cid}.scope",
    "/sys/fs/cgroup/docker/{cid}",
    "/sys/fs/cgroup/kubepods/docker-{cid}.scope",
)


def _container_id(name: str) -> str | None:
    result = subprocess.run(
        ["docker", "inspect", "--format", "{{.Id}}", name],
        check=False,
        capture_output=True,
        text=True,
    )
    if result.returncode != 0:
        return None
    value = result.stdout.strip()
    return value or None


def _cgroup_dir(container_id: str) -> Path | None:
    for template in CGROUP_TEMPLATES:
        candidate = Path(template.format(cid=container_id))
        if (candidate / "cpu.stat").is_file():
            return candidate
    try:
        result = subprocess.run(
            ["find", "/sys/fs/cgroup", "-maxdepth", "5", "-type", "d", "-name", f"*{container_id}*"],
            check=False,
            capture_output=True,
            text=True,
        )
    except OSError:
        return None
    for line in result.stdout.splitlines():
        candidate = Path(line.strip())
        if (candidate / "cpu.stat").is_file():
            return candidate
    return None


def _values(path: Path) -> dict[str, int]:
    try:
        lines = path.read_text(encoding="utf-8").splitlines()
    except OSError:
        return {}
    values: dict[str, int] = {}
    for line in lines:
        parts = line.split()
        if len(parts) == 2 and parts[1].lstrip("-").isdigit():
            values[parts[0]] = int(parts[1])
    return values


def _current(path: Path) -> int:
    try:
        value = path.read_text(encoding="utf-8").strip()
    except OSError:
        return 0
    return int(value) if value.isdigit() else 0


def _sample_row(name: str, arm: str) -> dict[str, int | str | float] | None:
    container_id = _container_id(name)
    if container_id is None:
        return None
    cgroup = _cgroup_dir(container_id)
    if cgroup is None:
        return None
    memory = _values(cgroup / "memory.stat")
    cpu = _values(cgroup / "cpu.stat")
    if not all(key in memory for key in ("anon", "file")) or "usage_usec" not in cpu:
        return None
    current_path = cgroup / "memory.current"
    if not current_path.is_file():
        return None
    return {
        "epoch": time.time(),
        "monotonic": time.monotonic(),
        "arm": arm,
        "container": name,
        "container_id": container_id,
        "memory_current": _current(current_path),
        "memory_anon": memory.get("anon", 0),
        "memory_file": memory.get("file", 0),
        "cpu_usec": cpu.get("usage_usec", 0),
        "nr_throttled": cpu.get("nr_throttled", 0),
        "throttled_usec": cpu.get("throttled_usec", 0),
    }


def sample(arms: Sequence[tuple[str, str]], output_dir: Path, interval: float = 5.0) -> None:
    output_dir.mkdir(parents=True, exist_ok=True)
    fields = [
        "epoch",
        "monotonic",
        "arm",
        "container",
        "container_id",
        "memory_current",
        "memory_anon",
        "memory_file",
        "cpu_usec",
        "nr_throttled",
        "throttled_usec",
    ]
    handles: dict[str, tuple[object, csv.DictWriter]] = {}
    for arm, name in arms:
        handle = (output_dir / f"{arm}.csv").open("w", newline="", encoding="utf-8")
        writer = csv.DictWriter(handle, fieldnames=fields)
        writer.writeheader()
        handles[arm] = (handle, writer)

    stopping = False

    def stop(*_: object) -> None:
        nonlocal stopping
        stopping = True

    signal.signal(signal.SIGTERM, stop)
    signal.signal(signal.SIGINT, stop)
    try:
        while not stopping:
            for arm, name in arms:
                row = _sample_row(name, arm)
                if row is not None:
                    handles[arm][1].writerow(row)
                    handles[arm][0].flush()
            time.sleep(interval)
    finally:
        for handle, _ in handles.values():
            handle.close()


def main(argv: Sequence[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output-dir", required=True)
    parser.add_argument("--interval", type=float, default=5.0)
    parser.add_argument("--arm", action="append", nargs=2, metavar=("LABEL", "CONTAINER"), required=True)
    arguments = parser.parse_args(argv)
    sample([(label, container) for label, container in arguments.arm], Path(arguments.output_dir), arguments.interval)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
