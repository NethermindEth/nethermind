#!/usr/bin/env python3
"""Run an EXPB image campaign sequentially and retain a self-contained record.

The EXPB executor owns the client, payload server, and snapshot overlay lifecycle.  This
driver deliberately invokes one executor process at a time, records its combined output, and
does not start the next sample until the executor's cleanup can be verified.
"""

from __future__ import annotations

import argparse
import datetime as dt
import json
import math
import os
from pathlib import Path
import re
import signal
import subprocess
import sys
import threading
import time
from typing import Any, Iterable


ANSI_RE = re.compile(r"\x1b\[[0-?]*[ -/]*[@-~]")
CLIENT_METRIC_RE = re.compile(
    r"\[payload-server\]\s+client_metric\s+block_number=(\d+)\s+processing_ms=([0-9]+(?:\.[0-9]+)?)"
)
K6_ROW_RE = re.compile(
    r"^\s*\|\s*([0-9]+)\s*\|\s*[0-9]+\s*\|\s*([0-9]+(?:\.[0-9]+)?)\s*\|\s*$",
    re.MULTILINE,
)
WARMUP_OK_RE = re.compile(
    r"\[payload-server\]\s+warmup\s+block=(\d+)\s+ok\b", re.IGNORECASE
)
WARMUP_FAILED_RE = re.compile(
    r"\[payload-server\]\s+warmup\s+block=(\d+)\s+FAILED\b", re.IGNORECASE
)
SEVERE_RE = re.compile(r"(?:Unhandled|Fatal|ERROR)", re.IGNORECASE)
INVALID_BLOCK_RE = re.compile(r"invalid[\s_-]*blocks?", re.IGNORECASE)
EXCEPTION_RE = re.compile(r"Exception")
KEY_RE = re.compile(r"^[A-Za-z_][A-Za-z0-9_]*$")


def utc_now() -> str:
    return dt.datetime.now(dt.timezone.utc).isoformat().replace("+00:00", "Z")


def parse_flags(value: str) -> list[str]:
    """Convert the workflow's comma/newline flag format to ``--name=value`` arguments."""
    entries = [entry.strip() for entry in re.split(r"[\r\n,]", value) if entry.strip()]
    flags: list[str] = []
    pending: str | None = None
    for entry in entries:
        if entry.startswith("--"):
            if pending is not None:
                flags.append(pending)
            parts = entry.split(None, 1)
            if len(parts) == 2 and "=" not in parts[0]:
                flags.append(f"{parts[0]}={parts[1].strip()}")
                pending = None
            else:
                pending = entry
        elif pending is not None:
            flags.append(f"{pending}={entry}")
            pending = None
        else:
            flags.append(entry)
    if pending is not None:
        flags.append(pending)
    return flags


def parse_client_env(value: str) -> dict[str, str]:
    result: dict[str, str] = {}
    for entry in re.split(r"[\r\n,]", value):
        entry = entry.strip()
        if not entry or "=" not in entry:
            continue
        key, item = entry.split("=", 1)
        if KEY_RE.fullmatch(key):
            result[key] = item
    return result


def percentile(values: list[float], percentage: int) -> float:
    ordered = sorted(values)
    index = max(0, math.ceil(percentage * len(ordered) / 100) - 1)
    return ordered[index]


def metrics_for_values(values: list[float], prefix: str = "") -> dict[str, str]:
    if not values:
        return {}
    ordered = sorted(values)
    middle = len(ordered) // 2
    median = ordered[middle] if len(ordered) % 2 else (ordered[middle - 1] + ordered[middle]) / 2
    return {
        f"{prefix}COUNT": str(len(values)),
        f"{prefix}AVG": f"{sum(values) / len(values):.2f}",
        f"{prefix}AVG_EXACT": f"{sum(values) / len(values):.12g}",
        f"{prefix}MEDIAN": f"{median:.2f}",
        f"{prefix}P90": f"{percentile(values, 90):g}",
        f"{prefix}P95": f"{percentile(values, 95):g}",
        f"{prefix}P99": f"{percentile(values, 99):g}",
        f"{prefix}MIN": f"{ordered[0]:g}",
        f"{prefix}MAX": f"{ordered[-1]:g}",
    }


def coefficient_of_variation(values: Iterable[float]) -> str:
    numbers = [float(value) for value in values]
    if len(numbers) < 2:
        return "unavailable"
    mean = sum(numbers) / len(numbers)
    if mean == 0:
        return "unavailable"
    variance = sum((number - mean) ** 2 for number in numbers) / (len(numbers) - 1)
    return f"{math.sqrt(variance) / abs(mean) * 100:.2f}%"


def parse_output(log: str) -> tuple[dict[str, str], list[str], list[str], list[dict[str, Any]]]:
    """Return metrics, exception lines, invalid-block lines, and block-level SSE records."""
    clean = ANSI_RE.sub("", log)
    exceptions = [line for line in clean.splitlines() if EXCEPTION_RE.search(line)]
    invalid_blocks = [line for line in clean.splitlines() if INVALID_BLOCK_RE.search(line)]
    client_metrics = [
        {"block_number": int(match.group(1)), "processing_ms": float(match.group(2))}
        for match in CLIENT_METRIC_RE.finditer(clean)
    ]
    client_values = [item["processing_ms"] for item in client_metrics]
    if client_values:
        metrics = metrics_for_values(client_values)
        metrics["SOURCE"] = "SSE"
        k6_values = [float(match.group(2)) for match in K6_ROW_RE.finditer(clean)]
        metrics.update(metrics_for_values(k6_values, "TTFB_"))
    else:
        k6_values = [float(match.group(2)) for match in K6_ROW_RE.finditer(clean)]
        metrics = metrics_for_values(k6_values)
        metrics["SOURCE"] = "TTFB"
    return metrics, exceptions, invalid_blocks, client_metrics


def parse_k6_rows(log: str) -> list[dict[str, Any]]:
    """Return the delivered payload rows from the printed K6 metrics table."""
    clean = ANSI_RE.sub("", log)
    return [
        {"payload_index": int(match.group(1)), "ttfb_ms": float(match.group(2))}
        for match in K6_ROW_RE.finditer(clean)
    ]


def parse_warmup_records(log: str) -> tuple[list[int], list[int]]:
    """Return concrete successful and failed payload-server warmup block indexes."""
    clean = ANSI_RE.sub("", log)
    successful = [int(match.group(1)) for match in WARMUP_OK_RE.finditer(clean)]
    failed = [int(match.group(1)) for match in WARMUP_FAILED_RE.finditer(clean)]
    return successful, failed


def parse_severe_lines(log: str) -> list[str]:
    """Return severe runtime signal lines for artifact review and warnings."""
    clean = ANSI_RE.sub("", log)
    return [line for line in clean.splitlines() if SEVERE_RE.search(line)]


def load_image_plan(raw: str) -> list[dict[str, str]]:
    try:
        plan = json.loads(raw)
    except json.JSONDecodeError as error:
        raise ValueError(f"images JSON is invalid: {error}") from error
    if not isinstance(plan, list) or not plan:
        raise ValueError("images JSON must be a non-empty array")
    result: list[dict[str, str]] = []
    ids: set[str] = set()
    for item in plan:
        if not isinstance(item, dict) or not isinstance(item.get("image"), str):
            raise ValueError("each image entry must contain an image string")
        sample_id = str(item.get("id", "")).strip()
        if not sample_id or not re.fullmatch(r"[A-Za-z0-9_.-]+", sample_id):
            raise ValueError(f"invalid or missing image id: {sample_id!r}")
        if sample_id in ids:
            raise ValueError(f"duplicate image id: {sample_id}")
        ids.add(sample_id)
        result.append(
            {
                "id": sample_id,
                "image": item["image"],
                "tag": str(item.get("tag", item["image"])),
                "date": str(item.get("date", "n/a")),
            }
        )
    return result


def _configured_work_path(config_path: Path, data_dir: Path, yq_bin: str) -> Path:
    """Read the rendered config's ``paths.work`` with the same YAML parser as rendering."""
    result = subprocess.run(
        [yq_bin, "-o=json", "[ ((.paths // {}) | has(\"work\")), (.paths.work // null) ]", str(config_path)],
        check=False,
        capture_output=True,
        text=True,
    )
    if result.returncode != 0:
        raise ValueError(f"cannot parse rendered config with yq: {result.stderr.strip()}")
    try:
        parsed = json.loads(result.stdout)
    except json.JSONDecodeError as error:
        raise ValueError(f"yq returned invalid JSON for paths.work: {error}") from error
    if not isinstance(parsed, list) or len(parsed) != 2 or not isinstance(parsed[0], bool):
        raise ValueError("yq returned an invalid paths.work shape")
    if not parsed[0]:
        return data_dir.resolve(strict=False) / "work"
    if not isinstance(parsed[1], str) or not parsed[1].strip():
        raise ValueError("rendered config contains an empty paths.work")
    value = parsed[1].strip()
    value = value.replace("/mnt/sda/expb-data", str(data_dir))
    value = value.replace("/mnt/sda/nethermind-flat-snapshot", str(data_dir / "nethermind-flat-snapshot"))
    value = value.replace("/mnt/sda/nethermind-flat-25490000", str(data_dir / "nethermind-flat-25490000"))
    try:
        path = Path(value).expanduser()
        if not path.is_absolute():
            path = data_dir / path
        return path.resolve(strict=False)
    except OSError as error:
        raise ValueError(f"cannot resolve paths.work '{value}': {error}") from error


def snapshot_scratch_paths(config_path: Path, data_dir: Path, yq_bin: str = "yq") -> list[Path]:
    """Return the owned overlay scratch leaves configured for an EXPB scenario."""
    root = _configured_work_path(config_path, data_dir, yq_bin)
    return [root / leaf for leaf in ("upper", "work", "merged")]


def verify_snapshot_scratch(config_path: Path, data_dir: Path, yq_bin: str = "yq") -> tuple[bool, list[str]]:
    """Fail closed when an overlay scratch leaf contains data or cannot be inspected."""
    problems: list[str] = []
    try:
        data_root = data_dir.resolve(strict=False)
    except OSError as error:
        return False, [f"cannot resolve EXPB data directory: {error}"]
    try:
        paths = snapshot_scratch_paths(config_path, data_root, yq_bin)
    except (OSError, ValueError) as error:
        return False, [f"cannot resolve snapshot scratch paths: {error}"]
    for path in paths:
        try:
            resolved = path.resolve(strict=False)
            if data_root != resolved and data_root not in resolved.parents:
                problems.append(f"refusing snapshot scratch path outside {data_root}: {path}")
                continue
            if path.is_symlink():
                problems.append(f"snapshot scratch path is a symlink: {path}")
                continue
            if not path.exists():
                continue
            if not path.is_dir():
                problems.append(f"snapshot scratch path is not a directory: {path}")
                continue
            first_entry = next(path.iterdir(), None)
        except OSError as error:
            problems.append(f"cannot inspect snapshot scratch path {path}: {error}")
            continue
        if first_entry is not None:
            problems.append(f"snapshot scratch path is not empty: {path} (contains {first_entry.name})")
    return not problems, problems


def verify_cleanup(data_dir: Path, docker_bin: str = "docker") -> tuple[bool, list[str]]:
    """Verify that EXPB left no owned container/network or overlay below its data directory."""
    problems: list[str] = []
    docker_filters = ("label=expb", "name=expb", "name=rpcbench-", "name=nethermind-rpcbench", "name=ethcallchaos-bench", "name=jsonbench-")
    seen: set[str] = set()
    for filter_value in docker_filters:
        try:
            result = subprocess.run(
                [docker_bin, "ps", "-aq", "--filter", filter_value],
                check=False,
                capture_output=True,
                text=True,
            )
        except OSError as error:
            problems.append(f"docker container check failed for {filter_value}: {error}")
            continue
        if result.returncode != 0:
            problems.append(f"docker container check failed for {filter_value}: {result.stderr.strip()}")
            continue
        seen.update(line.strip() for line in result.stdout.splitlines() if line.strip())
    if seen:
        problems.append(f"benchmark containers remain: {', '.join(sorted(seen))}")

    network_ids: set[str] = set()
    for filter_value in ("label=expb", "name=expb", "name=rpcbench-", "name=nethermind-rpcbench", "name=jsonbench-"):
        try:
            result = subprocess.run(
                [docker_bin, "network", "ls", "-q", "--filter", filter_value],
                check=False,
                capture_output=True,
                text=True,
            )
        except OSError as error:
            problems.append(f"docker network check failed for {filter_value}: {error}")
            continue
        if result.returncode != 0:
            problems.append(f"docker network check failed for {filter_value}: {result.stderr.strip()}")
            continue
        network_ids.update(line.strip() for line in result.stdout.splitlines() if line.strip())
    if network_ids:
        problems.append(f"benchmark networks remain: {', '.join(sorted(network_ids))}")

    try:
        root = data_dir.resolve(strict=False)
    except OSError as error:
        problems.append(f"cannot resolve EXPB data directory: {error}")
        return False, problems
    mounts_path = Path("/proc/self/mounts")
    if not mounts_path.exists():
        problems.append("cannot inspect /proc/self/mounts")
    else:
        try:
            mount_lines = mounts_path.read_text(errors="replace").splitlines()
        except OSError as error:
            problems.append(f"cannot inspect /proc/self/mounts: {error}")
            mount_lines = []
        for line in mount_lines:
            fields = line.split()
            if len(fields) < 3 or fields[2] != "overlay":
                continue
            mountpoint = fields[1].replace(r"\040", " ").replace(r"\011", "\t").replace(r"\134", "\\")
            try:
                mounted = Path(mountpoint).resolve()
            except OSError:
                problems.append(f"cannot resolve overlay mount {mountpoint}")
                continue
            if mounted == root or root in mounted.parents:
                problems.append(f"overlay mount remains below {root}: {mounted}")
    return not problems, problems


class Campaign:
    def __init__(self, args: argparse.Namespace) -> None:
        self.args = args
        self.output_dir = Path(args.output_dir).resolve()
        self.output_dir.mkdir(parents=True, exist_ok=True)
        self.current_process: subprocess.Popen[str] | None = None
        self._termination_timer: threading.Timer | None = None
        self.cancel_requested = False
        self.aborted = False
        self.failures = 0
        self.samples: list[dict[str, Any]] = []
        self.preflight: dict[str, Any] | None = None
        self.flags = parse_flags(args.additional_extra_flags or "")
        self.client_env = parse_client_env(args.client_env or "")
        if args.measurement_mode == "compute-warm":
            self._add_compute_warm_flags()

    def _add_compute_warm_flags(self) -> None:
        if any("GasCap" in flag for flag in self.flags):
            raise ValueError("measurement_mode=compute-warm cannot be combined with a GasCap override")
        if any("EVM_WARMUP" in key for key in parse_client_env(self.args.expb_env or "")):
            raise ValueError("measurement_mode=compute-warm cannot be combined with EXPB_EVM_WARMUP override")
        self.args.expb_env = f"{self.args.expb_env}\nEXPB_EVM_WARMUP=1".strip()
        self.flags.append("--JsonRpc.GasCap=1000000000000")

    def _force_termination(self, process: subprocess.Popen[str]) -> None:
        if process is not self.current_process or process.poll() is not None:
            return
        print("Cleanup grace period elapsed; forcing EXPB shutdown.", flush=True)
        try:
            os.killpg(process.pid, signal.SIGKILL)
        except (AttributeError, ProcessLookupError, OSError):
            try:
                process.kill()
            except (ProcessLookupError, OSError):
                pass

    def terminate(self, _signum: int, _frame: Any) -> None:
        """Forward cancellation and let the normal reader drain EXPB's output."""
        process = self.current_process
        if process is None or process.poll() is not None:
            self.cancel_requested = True
            self.aborted = True
            return
        if self.cancel_requested:
            return
        self.cancel_requested = True
        self.aborted = True
        print(f"Termination signal received; waiting {self.args.cleanup_grace_seconds}s for EXPB cleanup.", flush=True)
        try:
            os.killpg(process.pid, signal.SIGTERM)
        except (AttributeError, ProcessLookupError, OSError):
            try:
                process.terminate()
            except (ProcessLookupError, OSError):
                return
        self._termination_timer = threading.Timer(
            self.args.cleanup_grace_seconds,
            self._force_termination,
            args=(process,),
        )
        self._termination_timer.daemon = True
        self._termination_timer.start()

    def render_config(self, image: dict[str, str], run: int, destination: Path) -> str:
        source = Path(self.args.config_template).read_text()
        scenario = f"nethermind-multi-{image['id']}-run{run}"
        # The template's image field is usually `nethermindeth/nethermind:<<DOCKER_TAG>>`.
        # Replace the placeholder with a harmless value first, then set the complete reference
        # through yq. This also handles a different repository and immutable @sha256 references.
        rendered = source.replace("<<DOCKER_TAG>>", "benchmark")
        rendered = rendered.replace("<<DELAY>>", self.args.delay_seconds)
        amount = self.args.amount
        if not amount and "<<AMOUNT>>" in rendered:
            amount_value = self._resolve_amount_with_yq(Path(self.args.config_template), "nethermind")
            if amount_value is None:
                raise ValueError("config contains <<AMOUNT>> but no scenario amount is configured")
            amount = str(amount_value)
        if amount:
            rendered = rendered.replace("<<AMOUNT>>", amount)
        rendered = rendered.replace("/mnt/sda/expb-data", self.args.expb_data_dir)
        rendered = rendered.replace("/mnt/sda/nethermind-flat-snapshot", self.args.flat_snapshot_dir)
        rendered = rendered.replace("/mnt/sda/nethermind-flat-25490000", self.args.flat_snapshot_block_dir)
        rendered = re.sub(r"^(\s*)nethermind:", rf"\g<1>{scenario}:", rendered, flags=re.MULTILINE)
        destination.write_text(rendered)

        yq = self.args.yq_bin
        image_env = os.environ.copy()
        image_env.update({"SK": scenario, "IMAGE": image["image"]})
        subprocess.run(
            [yq, "-i", ".scenarios.[strenv(SK)].image = strenv(IMAGE)", str(destination)],
            check=True,
            env=image_env,
        )
        if self.flags or self.args.trace_blocks or self.client_env:
            for flag in self.flags:
                env = os.environ.copy()
                env.update({"SK": scenario, "FLAG": flag})
                subprocess.run(
                    [yq, "-i", ".scenarios.[strenv(SK)].extra_flags += [strenv(FLAG)]", str(destination)],
                    check=True,
                    env=env,
                )
            if self.args.trace_blocks:
                env = os.environ.copy()
                env.update({"SK": scenario, "V": self.args.trace_blocks})
                subprocess.run(
                    [yq, "-i", ".scenarios.[strenv(SK)].extra_env.NETHERMIND_PROFILE_BLOCKS = strenv(V)", str(destination)],
                    check=True,
                    env=env,
                )
            for key, value in self.client_env.items():
                env = os.environ.copy()
                env.update({"SK": scenario, "KEY": key, "VALUE": value})
                subprocess.run(
                    [yq, "-i", ".scenarios.[strenv(SK)].extra_env[strenv(KEY)] = strenv(VALUE)", str(destination)],
                    check=True,
                    env=env,
                )
        return scenario

    def _resolve_amount_with_yq(self, config_path: Path, scenario_key: str) -> int | None:
        env = os.environ.copy()
        env["SK"] = scenario_key
        result = subprocess.run(
            [self.args.yq_bin, "-r", ".scenarios.[strenv(SK)].amount // null", str(config_path)],
            check=False,
            capture_output=True,
            text=True,
            env=env,
        )
        if result.returncode != 0:
            raise ValueError(f"cannot resolve scenario amount with yq: {result.stderr.strip()}")
        value = result.stdout.strip()
        if not value or value == "null":
            return None
        try:
            amount = int(value)
        except ValueError as error:
            raise ValueError(f"scenario amount is not an integer: {value!r}") from error
        if amount < 1:
            raise ValueError(f"scenario amount must be positive: {amount}")
        return amount

    def resolve_expected_amount(self, config_path: Path, scenario: str) -> int | None:
        """Resolve the configured payload count when the CLI did not override it."""
        if self.args.amount:
            value = self.args.amount
        else:
            amount = self._resolve_amount_with_yq(config_path, scenario)
            if amount is None:
                return None
            return amount
        try:
            amount = int(value)
        except (TypeError, ValueError) as error:
            raise ValueError(f"scenario amount is not an integer: {value!r}") from error
        if amount < 1:
            raise ValueError(f"scenario amount must be positive: {amount}")
        return amount

    def run_sample(self, image: dict[str, str], run: int) -> dict[str, Any]:
        sample_id = f"{image['id']}-run{run}"
        sample_dir = self.output_dir / sample_id
        sample_dir.mkdir(parents=True, exist_ok=True)
        started = utc_now()
        started_epoch = time.time()
        log_stamp = started.replace("-", "").replace(":", "")
        raw_path = sample_dir / f"combined-{log_stamp}.log"
        config_path = sample_dir / "config.yaml"
        metadata: dict[str, Any] = {
            "sample_id": sample_id,
            "image_id": image["id"],
            "image": image["image"],
            "tag": image["tag"],
            "date": image["date"],
            "run": run,
            "started_at": started,
            "config": str(config_path),
            "log": str(raw_path),
            "measurement_mode": self.args.measurement_mode,
            "expb_source": os.environ.get("EXPB_SOURCE", "unknown"),
            "expb_env": self.args.expb_env or "",
            "expb_env_values": parse_client_env(self.args.expb_env or ""),
            "extra_flags": self.flags,
            "client_env": self.client_env,
            "profiling": {
                "dottrace_requested": bool(self.args.dottrace),
                "dottrace_mode": self.args.dottrace_mode if self.args.dottrace else None,
                "dotnet_trace_requested": bool(self.args.dottrace),
                "perf_requested": bool(self.args.perf),
                "status": "requested" if self.args.dottrace or self.args.perf else "disabled",
            },
        }
        try:
            scenario = self.render_config(image, run, config_path)
            metadata["scenario"] = scenario
            expected_amount = self.resolve_expected_amount(config_path, scenario)
            metadata["expected_amount"] = expected_amount
        except (OSError, ValueError, subprocess.CalledProcessError) as error:
            metadata.update(
                {
                    "finished_at": utc_now(),
                    "exit_code": None,
                    "execution_success": False,
                    "cleanup_verified": False,
                    "cleanup_problems": [f"config render failed: {error}"],
                    "metrics_source": "none",
                    "metrics_count": 0,
                    "exception_found": False,
                    "invalid_block_found": False,
                    "failure_reasons": [f"config render failed: {error}"],
                    "severe_found": False,
                    "status": "failed",
                    "error": str(error),
                    "phase": "render",
                }
            )
            raw_path.write_text(f"driver error rendering config: {error}\n", encoding="utf-8")
            (sample_dir / "clean.log").write_text(raw_path.read_text(encoding="utf-8"), encoding="utf-8")
            (sample_dir / "exceptions.log").write_text("", encoding="utf-8")
            (sample_dir / "invalid-blocks.log").write_text("", encoding="utf-8")
            (sample_dir / "severe-lines.log").write_text("", encoding="utf-8")
            (sample_dir / "metrics.env").write_text(
                f"IMAGE_ID={image['id']}\nIMAGE={image['image']}\nRUN={run}\nERROR=config_render\nEXECUTION_SUCCESS=false\nSTATUS=failed\n",
                encoding="utf-8",
            )
            (sample_dir / "metadata.json").write_text(json.dumps(metadata, indent=2) + "\n", encoding="utf-8")
            self.failures += 1
            self.aborted = True
            return metadata
        if self.cancel_requested:
            metadata.update(
                {
                    "finished_at": utc_now(),
                    "exit_code": None,
                    "execution_success": False,
                    "cleanup_verified": True,
                    "cleanup_problems": [],
                    "snapshot_scratch_verified": True,
                    "snapshot_scratch_problems": [],
                    "metrics_source": "none",
                    "metrics_count": 0,
                    "delivered_count": 0,
                    "delivered_payload_indices": [],
                    "sse_client_metrics": [],
                    "sse_block_ids": [],
                    "exception_found": False,
                    "invalid_block_found": False,
                    "cancel_requested": True,
                    "failure_reasons": ["campaign cancellation requested before execution"],
                    "severe_found": False,
                    "status": "failed",
                    "phase": "before_execution",
                }
            )
            raw_path.write_text("driver: cancellation requested before starting expb\n", encoding="utf-8")
            (sample_dir / "clean.log").write_text(raw_path.read_text(encoding="utf-8"), encoding="utf-8")
            for name in ("exceptions.log", "invalid-blocks.log", "severe-lines.log"):
                (sample_dir / name).write_text("", encoding="utf-8")
            (sample_dir / "metrics.env").write_text(
                f"IMAGE_ID={image['id']}\nIMAGE={image['image']}\nRUN={run}\nEXECUTION_SUCCESS=false\nSTATUS=failed\nCANCEL_REQUESTED=true\n",
                encoding="utf-8",
            )
            (sample_dir / "metadata.json").write_text(json.dumps(metadata, indent=2) + "\n", encoding="utf-8")
            self.failures += 1
            print(f"Sample {sample_id} failed: {metadata['failure_reasons'][0]}", file=sys.stderr, flush=True)
            return metadata
        command = [
            self.args.expb_bin,
            "execute-scenarios",
            "--config-file",
            str(config_path),
            "--per-payload-metrics",
            "--per-payload-metrics-logs",
            "--print-logs",
        ]
        if self.args.dottrace:
            command.extend(["--dottrace", "--dottrace-mode", self.args.dottrace_mode or "sampling", "--dotnet-trace"])
        if self.args.perf:
            command.append("--perf")
        metadata["command"] = command
        child_env = os.environ.copy()
        for key, value in parse_client_env(self.args.expb_env or "").items():
            child_env[key] = value

        exit_code = 125
        log_parts: list[str] = []
        try:
            with raw_path.open("w", encoding="utf-8") as log_file:
                self.current_process = subprocess.Popen(
                    command,
                    cwd=self.args.expb_data_dir,
                    env=child_env,
                    stdout=subprocess.PIPE,
                    stderr=subprocess.STDOUT,
                    text=True,
                    bufsize=1,
                    start_new_session=True,
                )
                assert self.current_process.stdout is not None
                for line in self.current_process.stdout:
                    log_file.write(line)
                    log_file.flush()
                    log_parts.append(line)
                    print(line, end="", flush=True)
                exit_code = self.current_process.wait()
        except OSError as error:
            log_parts.append(f"driver error starting expb: {error}\n")
            raw_path.write_text("".join(log_parts), encoding="utf-8")
        finally:
            if self._termination_timer is not None:
                self._termination_timer.cancel()
                self._termination_timer = None
            self.current_process = None

        log = "".join(log_parts)
        clean_log = ANSI_RE.sub("", log)
        metrics, exceptions, invalid_blocks, client_metrics = parse_output(log)
        k6_rows = parse_k6_rows(log)
        warmup_success_blocks, warmup_failed_blocks = parse_warmup_records(log)
        severe_lines = parse_severe_lines(log)
        cleanup_ok, cleanup_problems = verify_cleanup(Path(self.args.expb_data_dir), self.args.docker_bin)
        scratch_ok, scratch_problems = verify_snapshot_scratch(
            config_path, Path(self.args.expb_data_dir), self.args.yq_bin
        )
        cleanup_problems = list(cleanup_problems)
        cleanup_problems.extend(scratch_problems)
        cleanup_ok = cleanup_ok and scratch_ok
        finished = utc_now()
        normal_shutdown = "Nethermind is shut down" in clean_log
        cleanup_completed = "Cleanup completed" in clean_log
        metadata.update(
            {
                "finished_at": finished,
                "exit_code": exit_code,
                "execution_success": exit_code == 0,
                "cleanup_verified": cleanup_ok,
                "cleanup_problems": cleanup_problems,
                "snapshot_scratch_verified": scratch_ok,
                "snapshot_scratch_problems": scratch_problems,
                "normal_shutdown": normal_shutdown,
                "cleanup_completed_marker": cleanup_completed,
                "metrics_source": metrics.get("SOURCE", "none"),
                "metrics_count": int(metrics.get("COUNT", "0")),
                "delivered_count": len(k6_rows),
                "delivered_payload_indices": [row["payload_index"] for row in k6_rows],
                "sse_client_metrics": client_metrics,
                "sse_block_ids": [item["block_number"] for item in client_metrics],
                "exception_found": bool(exceptions),
                "invalid_block_found": bool(invalid_blocks),
                "severe_found": bool(severe_lines),
                "severe_lines": severe_lines,
                "cancel_requested": self.cancel_requested,
            }
        )
        if self.args.measurement_mode == "compute-warm":
            delivered_payload_indices = set(metadata["delivered_payload_indices"])
            successful_warmup_indices = set(warmup_success_blocks)
            if warmup_failed_blocks:
                warmup_status = "failed"
            elif delivered_payload_indices and delivered_payload_indices <= successful_warmup_indices:
                warmup_status = "ok"
            else:
                warmup_status = "missing"
            metadata.update(
                {
                    "warmup_status": warmup_status,
                    "warmup_success_count": len(warmup_success_blocks),
                    "warmup_failed_count": len(warmup_failed_blocks),
                    "warmup_success_payload_indices": warmup_success_blocks,
                    "warmup_failed_payload_indices": warmup_failed_blocks,
                }
            )
        metadata_path = sample_dir / "metadata.json"
        metadata_path.write_text(json.dumps(metadata, indent=2) + "\n", encoding="utf-8")
        (sample_dir / "clean.log").write_text(ANSI_RE.sub("", log), encoding="utf-8")
        (sample_dir / "exceptions.log").write_text("\n".join(exceptions) + ("\n" if exceptions else ""), encoding="utf-8")
        (sample_dir / "invalid-blocks.log").write_text("\n".join(invalid_blocks) + ("\n" if invalid_blocks else ""), encoding="utf-8")
        (sample_dir / "severe-lines.log").write_text("\n".join(severe_lines) + ("\n" if severe_lines else ""), encoding="utf-8")
        expected_amount = metadata.get("expected_amount")
        delivered_count = metadata.get("delivered_count", 0)
        valid_delivery = isinstance(expected_amount, int) and delivered_count == expected_amount
        metadata["delivery_count_valid"] = valid_delivery
        lines = [f"{key}={value}" for key, value in metrics.items()]
        lines.extend(
            [
                f"IMAGE_ID={image['id']}",
                f"IMAGE={image['image']}",
                f"TAG={image['tag']}",
                f"RUN={run}",
                f"EXIT_CODE={exit_code}",
                f"EXECUTION_SUCCESS={'true' if exit_code == 0 else 'false'}",
                f"EXPECTED_AMOUNT={metadata.get('expected_amount', 'unavailable')}",
                f"DELIVERED_COUNT={metadata.get('delivered_count', 0)}",
                f"DELIVERY_COUNT_VALID={'true' if valid_delivery else 'false'}",
                f"CLEANUP_VERIFIED={'true' if cleanup_ok else 'false'}",
                f"NORMAL_SHUTDOWN={'true' if normal_shutdown else 'false'}",
                f"CLEANUP_COMPLETED_MARKER={'true' if cleanup_completed else 'false'}",
                f"EXCEPTION_FOUND={'true' if exceptions else 'false'}",
                f"INVALID_BLOCK_FOUND={'true' if invalid_blocks else 'false'}",
            ]
        )
        if self.args.measurement_mode == "compute-warm":
            lines.append(f"WARMUP_STATUS={metadata['warmup_status']}")
        profiling_root = Path(self.args.expb_data_dir) / "outputs"
        profiling_files: list[str] = []
        if self.args.dottrace or self.args.perf:
            try:
                if profiling_root.exists():
                    profiling_files = [
                        str(path.relative_to(profiling_root))
                        for path in profiling_root.rglob("*")
                        if path.is_file() and path.stat().st_mtime >= started_epoch
                    ]
            except OSError:
                profiling_files = []
            metadata["profiling"]["files_seen"] = profiling_files
            metadata["profiling"]["status"] = "available" if profiling_files else "missing"
        (sample_dir / "metrics.env").write_text("\n".join(lines) + "\n", encoding="utf-8")
        metadata["metrics"] = metrics
        valid_metrics = bool(metrics.get("AVG"))
        warmup_ok = self.args.measurement_mode != "compute-warm" or metadata["warmup_status"] == "ok"
        failure_reasons: list[str] = []
        if exit_code != 0:
            failure_reasons.append(f"execution exited with code {exit_code}")
        if not valid_delivery:
            if isinstance(expected_amount, int):
                failure_reasons.append(f"delivery count expected {expected_amount}, got {delivered_count}")
            else:
                failure_reasons.append("delivery count expectation is unavailable")
        if not valid_metrics:
            failure_reasons.append("processing metrics are missing")
        if not normal_shutdown:
            failure_reasons.append("normal shutdown marker is missing")
        if not cleanup_completed:
            failure_reasons.append("cleanup completed marker is missing")
        if exceptions:
            failure_reasons.append(f"{len(exceptions)} exception line(s) detected")
        if invalid_blocks:
            failure_reasons.append(f"{len(invalid_blocks)} invalid block line(s) detected")
        if not cleanup_ok:
            failure_reasons.append("cleanup verification failed")
        if not warmup_ok:
            failure_reasons.append(f"compute-warm warmup is {metadata['warmup_status']}")
        if self.cancel_requested:
            failure_reasons.append("campaign cancellation requested")
        metadata["failure_reasons"] = failure_reasons
        metadata["status"] = "success" if not failure_reasons else "failed"
        metadata_path.write_text(json.dumps(metadata, indent=2) + "\n", encoding="utf-8")
        if metadata["status"] != "success":
            self.failures += 1
        if not cleanup_ok:
            self.aborted = True
        if severe_lines:
            print(f"::warning::Severe runtime signal(s) found for {sample_id}; see severe-lines.log.", flush=True)
            for line in severe_lines[:20]:
                print(line, flush=True)
        if failure_reasons:
            print(f"Sample {sample_id} failed: {'; '.join(failure_reasons)}", file=sys.stderr, flush=True)
            if exceptions:
                print("Exception lines:", file=sys.stderr, flush=True)
                for line in exceptions[:20]:
                    print(line, file=sys.stderr, flush=True)
            if invalid_blocks:
                print("Invalid block lines:", file=sys.stderr, flush=True)
                for line in invalid_blocks[:20]:
                    print(line, file=sys.stderr, flush=True)
        return metadata

    def write_summary(self) -> None:
        by_image: dict[str, list[dict[str, Any]]] = {}
        for sample in self.samples:
            by_image.setdefault(sample["image_id"], []).append(sample)
        summary: list[str] = [
            "## EXPB Multi-Image Campaign",
            "",
            f"Images: {len(by_image)} | Runs per image: {self.args.run_count} | Measurement mode: `{self.args.measurement_mode}`",
            "",
            "| # | Image | Run | Date | Status | Source | Count | AVG (ms) | CV across runs |",
            "|---:|---|---:|---|---|---|---:|---:|---:|",
        ]
        row = 0
        for image in load_image_plan(self.args.images_json):
            runs = by_image.get(image["id"], [])
            successful = [sample for sample in runs if sample.get("status") == "success"]
            sources = {sample.get("metrics_source", "none") for sample in successful}
            metric_runs = [
                sample
                for sample in successful
                if sample.get("metrics", {}).get("AVG") and sample.get("metrics", {}).get("COUNT")
            ]
            counts = {sample["metrics"]["COUNT"] for sample in metric_runs}
            sse_runs = [sample for sample in metric_runs if sample.get("metrics", {}).get("SOURCE") == "SSE"]
            sse_block_sequences = [
                tuple(sample["sse_block_ids"])
                for sample in sse_runs
                if isinstance(sample.get("sse_block_ids"), list)
            ]
            if len(sources) > 1:
                cv = "mixed sources"
            elif len(counts) > 1:
                cv = "mismatched counts"
            elif len(sse_runs) > 1 and len(sse_block_sequences) == len(sse_runs) and len(set(sse_block_sequences)) > 1:
                cv = "mismatched block ids"
            else:
                avgs = [
                    float(sample["metrics"].get("AVG_EXACT", sample["metrics"].get("AVG")))
                    for sample in metric_runs
                ]
                cv = coefficient_of_variation(avgs)
            for sample in runs:
                row += 1
                metrics = sample.get("metrics", {})
                status = sample.get("status", "failed")
                if sample.get("cleanup_problems"):
                    status = "failed (cleanup)"
                summary.append(
                    f"| {row} | `{image['tag']}` ({image['id']}) | {sample['run']} | {image['date']} | {status} | {metrics.get('SOURCE', 'n/a')} | {metrics.get('COUNT', 'n/a')} | {metrics.get('AVG', 'n/a')} | {cv} |"
                )
            if not runs:
                row += 1
                summary.append(f"| {row} | `{image['tag']}` ({image['id']}) | - | {image['date']} | missing | n/a | n/a | n/a | unavailable |")
        summary.extend(
            [
                "",
                "CV is sample standard deviation divided by mean and is reported only when at least two successful runs for an image have AVG metrics.",
                "",
                "The combined logs and rendered configs are in the campaign artifact. A failed cleanup aborts the remaining campaign.",
            ]
        )
        (self.output_dir / "summary.md").write_text("\n".join(summary) + "\n", encoding="utf-8")
        started_at = next(
            (sample.get("started_at") for sample in self.samples if sample.get("started_at")),
            utc_now(),
        )
        (self.output_dir / "campaign.json").write_text(
            json.dumps(
                {
                    "started_at": started_at,
                    "finished_at": utc_now(),
                    "run_count": self.args.run_count,
                    "measurement_mode": self.args.measurement_mode,
                    "aborted": self.aborted,
                    "failure_count": self.failures,
                    "preflight": self.preflight,
                    "samples": self.samples,
                },
                indent=2,
            )
            + "\n",
            encoding="utf-8",
        )

    def run(self) -> int:
        signal.signal(signal.SIGTERM, self.terminate)
        signal.signal(signal.SIGINT, self.terminate)
        images = load_image_plan(self.args.images_json)
        preflight_cleanup, preflight_cleanup_problems = verify_cleanup(
            Path(self.args.expb_data_dir), self.args.docker_bin
        )
        preflight_scratch, preflight_scratch_problems = verify_snapshot_scratch(
            Path(self.args.config_template), Path(self.args.expb_data_dir), self.args.yq_bin
        )
        self.preflight = {
            "cleanup_verified": preflight_cleanup,
            "cleanup_problems": preflight_cleanup_problems,
            "snapshot_scratch_verified": preflight_scratch,
            "snapshot_scratch_problems": preflight_scratch_problems,
            "checked_at": utc_now(),
        }
        self.write_manifest()
        if not preflight_cleanup or not preflight_scratch:
            self.aborted = True
            self.failures += 1
            print("Initial cleanup verification failed; refusing to start the campaign.", file=sys.stderr, flush=True)
            self.write_summary()
            self.write_manifest()
            return 1

        for image in images:
            for run in range(1, self.args.run_count + 1):
                if self.aborted or self.cancel_requested:
                    self.aborted = True
                    if self.failures == 0:
                        self.failures += 1
                    print("Campaign cancellation or cleanup failure occurred before the next sample.", file=sys.stderr, flush=True)
                    self.write_summary()
                    self.write_manifest()
                    return 1
                print(f"::group::EXPB sample {image['id']} run {run}/{self.args.run_count}", flush=True)
                sample = self.run_sample(image, run)
                self.samples.append(sample)
                self.write_manifest()
                print(f"Sample status: {sample['status']}", flush=True)
                print("::endgroup::", flush=True)
                if self.aborted:
                    print("Cleanup could not be verified; aborting campaign before the next sample.", file=sys.stderr, flush=True)
                    self.write_summary()
                    return 1
        self.write_summary()
        return 1 if self.failures else 0

    def write_manifest(self) -> None:
        (self.output_dir / "campaign.json").write_text(
            json.dumps(
                {
                    "run_count": self.args.run_count,
                    "measurement_mode": self.args.measurement_mode,
                    "aborted": self.aborted,
                    "failure_count": self.failures,
                    "preflight": self.preflight,
                    "samples": self.samples,
                },
                indent=2,
            )
            + "\n",
            encoding="utf-8",
        )


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--images-json", default=os.environ.get("EXPB_IMAGES_JSON", ""))
    parser.add_argument("--config-template", default=os.environ.get("EXPB_CONFIG_TEMPLATE", ""))
    parser.add_argument("--output-dir", default=os.environ.get("EXPB_CAMPAIGN_DIR", ""))
    parser.add_argument("--expb-data-dir", default=os.environ.get("EXPB_DATA_DIR", ""))
    parser.add_argument("--flat-snapshot-dir", default=os.environ.get("FLAT_SNAPSHOT_DIR", ""))
    parser.add_argument("--flat-snapshot-block-dir", default=os.environ.get("FLAT_SNAPSHOT_BLOCK_DIR", ""))
    parser.add_argument("--delay-seconds", default=os.environ.get("DELAY_SECONDS", "0"))
    parser.add_argument("--amount", default=os.environ.get("AMOUNT", ""))
    parser.add_argument("--additional-extra-flags", default=os.environ.get("ADDITIONAL_EXTRA_FLAGS", ""))
    parser.add_argument("--client-env", default=os.environ.get("CLIENT_ENV", ""))
    parser.add_argument("--trace-blocks", default=os.environ.get("TRACE_BLOCKS", ""))
    parser.add_argument("--expb-env", default=os.environ.get("EXPB_ENV_PASSTHROUGH", ""))
    parser.add_argument("--expb-bin", default=os.environ.get("EXPB_BIN", "expb"))
    parser.add_argument("--yq-bin", default=os.environ.get("YQ_BIN", "yq"))
    parser.add_argument("--docker-bin", default=os.environ.get("DOCKER_BIN", "docker"))
    parser.add_argument("--run-count", type=int, default=int(os.environ.get("RUN_COUNT", "1")))
    parser.add_argument("--cleanup-grace-seconds", type=int, default=int(os.environ.get("CLEANUP_GRACE_SECONDS", "90")))
    parser.add_argument("--dottrace", action="store_true", default=os.environ.get("DOTTRACE", "false") == "true")
    parser.add_argument("--dottrace-mode", default=os.environ.get("DOTTRACE_MODE", "sampling"))
    parser.add_argument("--perf", action="store_true", default=os.environ.get("PERF", "false") == "true")
    parser.add_argument("--measurement-mode", choices=("standard", "compute-warm"), default=os.environ.get("MEASUREMENT_MODE", "standard"))
    return parser


def main() -> int:
    args = build_parser().parse_args()
    required = {
        "images-json": args.images_json,
        "config-template": args.config_template,
        "output-dir": args.output_dir,
        "expb-data-dir": args.expb_data_dir,
    }
    missing = [name for name, value in required.items() if not value]
    if args.run_count < 1:
        missing.append("run-count must be positive")
    if missing:
        print(f"Missing driver arguments: {', '.join(missing)}", file=sys.stderr)
        return 2
    try:
        return Campaign(args).run()
    except (OSError, ValueError, subprocess.CalledProcessError) as error:
        print(f"EXPB campaign setup failed: {error}", file=sys.stderr)
        output_dir = Path(args.output_dir) if args.output_dir else None
        if output_dir is not None:
            output_dir.mkdir(parents=True, exist_ok=True)
            (output_dir / "driver-error.txt").write_text(str(error) + "\n", encoding="utf-8")
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
