#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

"""Inspect Fusaka replay inputs and Linux tracing prerequisites without replaying blocks."""
import argparse
import datetime as dt
import hashlib
import json
from pathlib import Path
import platform
import shutil
import subprocess

import yaml


def file_identity(path):
    path = Path(path).resolve(strict=True)
    digest = hashlib.sha256()
    lines = 0
    last = b""
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
            lines += chunk.count(b"\n")
            last = chunk[-1:]
    return {"path": str(path), "bytes": path.stat().st_size, "sha256": digest.hexdigest(),
            "physical_line_count": lines + (bool(last) and last != b"\n")}


def first_request(path):
    with Path(path).open("rb") as stream:
        line = next((line for line in stream if line.strip()), None)
        if line is None:
            raise ValueError(f"input file contains no requests: {path}")
        record = json.loads(line)
    if not isinstance(record, dict):
        return {"format_error": "first input record is not an RPC object"}
    params = record.get("params", [])
    body = params[0] if isinstance(params, list) and params and isinstance(params[0], dict) else {}
    return {"method": record.get("method"), "root_keys": sorted(record),
            "header": {key: body[key] for key in ("parentHash", "blockHash", "blockNumber", "timestamp",
                       "gasUsed", "gasLimit", "headBlockHash", "safeBlockHash", "finalizedBlockHash") if key in body}}


def inspect_inputs(config_path):
    config_path = Path(config_path).resolve(strict=True)
    config = yaml.safe_load(config_path.read_text(encoding="utf-8"))
    scenario = config["scenarios"]["nethermind"]
    if scenario.get("client", "nethermind") != "nethermind":
        raise ValueError("expected a Nethermind scenario")
    files = {}
    for key in ("payloads", "fcus"):
        path = Path(scenario[key])
        if not path.is_absolute():
            path = config_path.parent / path
        files[key] = file_identity(path)
    snapshot = Path(scenario["snapshot_source"])
    if not snapshot.is_absolute():
        snapshot = config_path.parent / snapshot
    snapshot = snapshot.resolve(strict=True)
    if not snapshot.is_dir():
        raise ValueError("snapshot_source must resolve to a directory for the Linux Flat inventory")
    return {
        "config": file_identity(config_path), "files": files,
        "first_requests": {key: first_request(value["path"]) for key, value in files.items()},
        "snapshot": {"path": str(snapshot), "backend": scenario.get("snapshot_backend", "overlay"),
                     "entries": sorted(p.name for p in snapshot.iterdir()),
                     "free_bytes": shutil.disk_usage(snapshot).free,
                     "metadata": {name: json.loads((snapshot / name).read_text(encoding="utf-8"))
                                  for name in ("_snapshot_metadata.json", "_snapshot_eth_getBlockByNumber.json",
                                               "_snapshot_web3_clientVersion.json") if (snapshot / name).is_file()}},
        "scenario": {key: scenario.get(key) for key in
                     ("network", "amount", "skip", "warmup", "delay", "extra_flags", "snapshot_mount_path")},
        "resources": config.get("resources"),
        "state_identity_verified": False,
    }


def probe(command):
    try:
        result = subprocess.run(command, capture_output=True, text=True, timeout=30)
        return {"command": command, "exit_code": result.returncode,
                "stdout": result.stdout, "stderr": result.stderr}
    except (OSError, subprocess.TimeoutExpired) as error:
        return {"command": command, "error": str(error)}


def host_capabilities():
    paths = ("/proc/sys/kernel/perf_event_paranoid", "/proc/sys/kernel/kptr_restrict",
             "/sys/kernel/tracing/user_events_data", "/sys/kernel/debug/tracing/user_events_data")
    return {"kernel": platform.release(), "architecture": platform.machine(),
            "paths": {name: {"exists": Path(name).exists(),
                             "value": Path(name).read_text().strip() if name.startswith("/proc/sys/") and Path(name).exists() else None}
                      for name in paths},
            "probes": [probe(command) for command in
                       (["id"], ["perf", "--version"], ["dotnet", "--info"],
                        ["dotnet-trace", "--version"], ["docker", "version"],
                        ["docker", "ps", "--filter", "label=expb", "--format", "{{.ID}} {{.Names}}"])]}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--config", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    report = {"schema_version": 1, "created_at": dt.datetime.now(dt.timezone.utc).isoformat(),
              "host": host_capabilities()}
    try:
        report["inputs"] = inspect_inputs(args.config)
    except (OSError, ValueError, KeyError, TypeError, yaml.YAMLError) as error:
        report["input_error"] = str(error)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    if "input_error" in report:
        raise SystemExit(report["input_error"])


if __name__ == "__main__":
    main()
