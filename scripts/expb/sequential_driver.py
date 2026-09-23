#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

"""Run one sequential, auditable EXPB campaign from workflow environment variables."""
from __future__ import annotations
import datetime as dt
import json
import math
import os
from pathlib import Path
import platform
import re
import signal
import socket
import subprocess
import sys
import tempfile
import threading
ANSI = re.compile(r"\x1b\[[0-?]*[ -/]*[@-~]")
SSE = re.compile(r"\[payload-server\]\s+client_metric\s+block_number=(\d+)\s+processing_ms=(\d+(?:\.\d+)?)")
K6 = re.compile(r"^\s*\|\s*(\d+)\s*\|\s*(\d+)\s*\|\s*(\d+(?:\.\d+)?)\s*\|\s*$", re.M)
WARM_OK = re.compile(r"\[payload-server\]\s+warmup\s+block=(\d+)\s+ok\b", re.I)
WARM_BAD = re.compile(r"\[payload-server\]\s+warmup\s+block=(\d+)\s+FAILED\b", re.I)
SEVERE = re.compile(r"\b(?:Unhandled|Fatal|ERROR)\b", re.I)
LIMIT = 4096
MAX_DIAGNOSTIC_LINES = 20
CLIENTS = ("nethermind", "reth", "geth")
current = None
cancelled = False
watchdog: threading.Timer | None = None
def get(name: str, default: str = "") -> str: return os.environ.get(name, default)
def now() -> str: return dt.datetime.now(dt.timezone.utc).isoformat().replace("+00:00", "Z")
def parse_flags(value: str) -> list[str]:
    result = []
    for item in (x.strip() for x in re.split(r"[\r\n,]", value) if x.strip()):
        if not item.startswith("--") and result and result[-1].startswith("--"): result[-1] += f"={item}"
        else:
            parts = item.split(None, 1)
            result.append(item if len(parts) == 1 or "=" in parts[0] else f"{parts[0]}={parts[1]}")
    return result

def parse_pairs(value: str) -> dict[str, str]:
    result = {}
    for raw_item in re.split(r"[\r\n,]", value):
        item = raw_item.strip()
        if not item:
            continue
        key, separator, item_value = item.partition("=")
        if not separator or not re.fullmatch(r"[A-Za-z_][A-Za-z0-9_]*", key):
            raise ValueError(f"environment entry must use a valid KEY=VALUE pair: {item!r}")
        result[key] = item_value
    return result

def parse_amount(value: str) -> int | None:
    if not value:
        return None
    if not re.fullmatch(r"[1-9][0-9]*", value):
        raise ValueError(f"AMOUNT must be a positive integer, got {value!r}")
    return int(value)

def render(base: dict, image: dict, run: int) -> tuple[dict, str]:
    config = json.loads(json.dumps(base))
    amount = parse_amount(get("AMOUNT"))
    for old, new in (("<<DELAY>>", get("DELAY_SECONDS", "0")), ("<<AMOUNT>>", get("AMOUNT")), ("/mnt/sda/expb-data", get("EXPB_DATA_DIR")), ("/mnt/sda/nethermind-flat-snapshot", get("FLAT_SNAPSHOT_DIR")), ("/mnt/sda/nethermind-flat-25490000", get("FLAT_SNAPSHOT_BLOCK_DIR"))):
        config = json.loads(json.dumps(config).replace(old, new))
    scenarios = config.get("scenarios")
    if not isinstance(scenarios, dict) or not isinstance(scenarios.get("nethermind"), dict): raise ValueError("config has no scenarios.nethermind mapping")
    client = get("CLIENT", "nethermind")
    if client not in CLIENTS: raise ValueError(f"CLIENT must be one of {', '.join(CLIENTS)}, got {client!r}")
    name = f"{client}-{image['id']}-run{run}"
    config["scenarios"] = {name: (scenario := scenarios.pop("nethermind"))}
    scenario.update({"client": client, "image": image["image"], **({"amount": amount} if amount is not None else {})})
    if client != "nethermind":
        # Reference clients reuse the Nethermind scenario's payloads but never its flags, env or
        # volumes; they read their own snapshot through the overlay backend, and opening a large
        # reference snapshot can take several minutes on the benchmark runners.
        if get("MEASUREMENT_MODE", "standard") == "compute-warm" or get("TRACE_BLOCKS") or parse_pairs(get("CLIENT_ENV")):
            raise ValueError(f"compute-warm, TRACE_BLOCKS and CLIENT_ENV are Nethermind-only and cannot be used with client={client}")
        if not get("CLIENT_SNAPSHOT_DIR") or not get("SNAPSHOT_MOUNT_PATH"): raise ValueError(f"client={client} needs CLIENT_SNAPSHOT_DIR and SNAPSHOT_MOUNT_PATH")
        scenario.update({"snapshot_source": get("CLIENT_SNAPSHOT_DIR"), "snapshot_backend": "overlay", "extra_flags": [], "extra_env": {}, "extra_commands": [], "extra_volumes": {}, "snapshot_mount_path": get("SNAPSHOT_MOUNT_PATH"), "startup_wait": 600})
    extra = parse_flags(get("ADDITIONAL_EXTRA_FLAGS"))
    if get("MEASUREMENT_MODE", "standard") == "compute-warm":
        if any("JsonRpc.GasCap" in item for item in extra): raise ValueError("compute-warm conflicts with GasCap override")
        if "EXPB_EVM_WARMUP" in parse_pairs(get("EXPB_ENV_PASSTHROUGH")): raise ValueError("compute-warm conflicts with EVM warmup override")
        extra.append("--JsonRpc.GasCap=1000000000000")
    scenario.setdefault("extra_flags", []).extend(extra)
    scenario.setdefault("extra_env", {}).update(parse_pairs(get("CLIENT_ENV")))
    if get("TRACE_BLOCKS"): scenario["extra_env"]["NETHERMIND_PROFILE_BLOCKS"] = get("TRACE_BLOCKS")
    return config, name
def verify_clean(config: dict) -> None:
    root = Path(get("EXPB_DATA_DIR")).resolve()
    docker = get("DOCKER_BIN", "docker")
    subprocess.check_output([docker, "container", "prune", "--force", "--filter", "label=expb"], text=True)
    for label, args in (("containers", ["container", "ps", "-q"]), ("networks", ["network", "ls", "-q"])):
        result = subprocess.check_output([docker, *args, "--filter", "label=expb"], text=True).strip()
        if result: raise RuntimeError(f"benchmark {label} remain: {result}")
    for line in Path("/proc/self/mounts").read_text(errors="replace").splitlines():
        parts = line.split()
        if len(parts) >= 3 and parts[2] == "overlay":
            mount = Path(parts[1].replace(r"\040", " ")).resolve()
            if mount == root or root in mount.parents: raise RuntimeError(f"overlay remains below data directory: {mount}")
    paths = config.get("paths", {})
    work = paths.get("work", "work") if isinstance(paths, dict) else "work"
    if not isinstance(work, str) or not work: raise ValueError("paths.work is invalid")
    scratch = Path(work.replace("/mnt/sda/expb-data", get("EXPB_DATA_DIR"))).expanduser()
    scratch = (scratch if scratch.is_absolute() else root / scratch).resolve()
    if scratch != root and root not in scratch.parents: raise ValueError(f"paths.work escapes data directory: {scratch}")
    for leaf in ("work", "upper", "merged"):
        path = scratch / leaf
        if path.is_symlink(): raise RuntimeError(f"scratch is a symlink: {path}")
        if path.exists() and (not path.is_dir() or next(path.iterdir(), None) is not None): raise RuntimeError(f"scratch is not empty: {path}")
def bounded(line: str) -> str: return line.rstrip("\r\n") if len(line.rstrip("\r\n")) <= LIMIT else line[:LIMIT] + " ... [truncated in console; full line retained in combined log]"

def metric_stats(values: list[float]) -> dict:
    if not values:
        return {"count": 0, "avg": None, "median": None, "p90": None, "p95": None, "p99": None, "min": None, "max": None}
    ordered = sorted(values)
    def percentile(rank: int) -> float: return ordered[max(0, math.ceil(rank * len(ordered) / 100) - 1)]
    return {"count": len(values), "avg": sum(values) / len(values), "median": (ordered[(len(ordered) - 1) // 2] + ordered[len(ordered) // 2]) / 2, "p90": percentile(90), "p95": percentile(95), "p99": percentile(99), "min": ordered[0], "max": ordered[-1]}

def force(process) -> None:
    if process is current:
        try:
            os.killpg(process.pid, signal.SIGKILL)
        except ProcessLookupError:
            pass
def stop(_signum: int, _frame: object) -> None:
    global cancelled, watchdog
    cancelled = True
    process = current
    if process is None or process.poll() is not None: return
    try:
        os.killpg(process.pid, signal.SIGTERM)
    except ProcessLookupError:
        return
    watchdog = threading.Timer(int(get("CLEANUP_GRACE_SECONDS", "90")), force, (process,))
    watchdog.daemon = True
    watchdog.start()
def collect_metrics(log_path: Path | str, use_sse: bool = True) -> tuple[dict, dict[str, list[str]]]:
    """Parse a campaign log once while keeping diagnostics bounded in memory.

    With use_sse=False (measurement_source=engine-api) the k6 request table is the only timing
    source, so every client is measured on the same clock.
    """
    sse = []
    rows = []
    diagnostics = {"exceptions": [], "invalid": [], "severe": []}
    diagnostic_counts = {"exceptions": 0, "invalid": 0, "severe": 0}
    warm_ok = set()
    warm_bad = set()
    shutdown = False
    cleanup = False

    with Path(log_path).open("r", encoding="utf-8", errors="replace") as log:
        for raw_line in log:
            line = ANSI.sub("", raw_line).rstrip("\r\n")
            if use_sse and (match := SSE.search(line)):
                sse.append((int(match.group(1)), float(match.group(2))))
            if match := K6.match(line):
                rows.append((int(match.group(1)), int(match.group(2)), float(match.group(3))))
            if match := WARM_OK.search(line):
                warm_ok.add(int(match.group(1)))
            if match := WARM_BAD.search(line):
                warm_bad.add(int(match.group(1)))
            if "Exception" in line:
                diagnostic_counts["exceptions"] += 1
                if len(diagnostics["exceptions"]) < MAX_DIAGNOSTIC_LINES:
                    diagnostics["exceptions"].append(bounded(line))
            if re.search(r"invalid[\s_-]*blocks?", line, re.I):
                diagnostic_counts["invalid"] += 1
                if len(diagnostics["invalid"]) < MAX_DIAGNOSTIC_LINES:
                    diagnostics["invalid"].append(bounded(line))
            if SEVERE.search(line):
                diagnostic_counts["severe"] += 1
                if len(diagnostics["severe"]) < MAX_DIAGNOSTIC_LINES:
                    diagnostics["severe"].append(bounded(line))
            shutdown |= "Nethermind is shut down" in line
            cleanup |= "Cleanup completed" in line

    processing_values = [value for _, value in sse] if sse else [value for _, _, value in rows]
    request_values = [value for _, _, value in rows]
    # k6 rows and feed records share no key, so they pair by position only when every payload has both;
    # with one record missing, a positional pairing shears every value after the gap.
    paired = bool(sse) and len(sse) == len(rows)
    outside_values = [request - processing for (_, _, request), (_, processing) in zip(rows, sse)] if paired else []
    primary = metric_stats(processing_values)
    processing = metric_stats([value for _, value in sse])
    request = metric_stats(request_values)
    outside = metric_stats(outside_values)
    processing_source = "SSE" if sse else "TTFB"
    mgas_s = None
    request_mgas_s = None
    total_gas = sum(gas for _, gas, _ in rows) / 1_000_000
    if paired:
        processing_total = sum(value for _, value in sse)
        if processing_total > 0:
            mgas_s = total_gas / (processing_total / 1_000)
    request_total = sum(request_values)
    if request_total > 0:
        request_mgas_s = total_gas / (request_total / 1_000)
    parsed = {
        "source": processing_source if processing_values else "none",
        "count": len(processing_values),
        "avg": primary["avg"],
        "ids": [block for block, _ in sse] if sse else [index for index, _, _ in rows],
        "payload_indices": [index for index, _, _ in rows],
        "delivered": len(rows),
        "sse_count": len(sse),
        "primary": primary,
        "processing": processing,
        "request": request,
        "outside": outside,
        "mgas_s": mgas_s,
        "request_mgas_s": request_mgas_s,
        "warm_ok": sorted(warm_ok),
        "warm_bad": sorted(warm_bad),
        "shutdown": shutdown,
        "cleanup": cleanup,
        "exception_count": diagnostic_counts["exceptions"],
        "invalid_count": diagnostic_counts["invalid"],
        "severe_count": diagnostic_counts["severe"],
    }
    return parsed, diagnostics
def run_sample(base: dict, image: dict, run: int, root: Path) -> dict:
    global current, watchdog
    sample_id = f"{image['id']}-run{run}"
    directory = root / sample_id
    directory.mkdir(parents=True, exist_ok=True)
    started = now()
    log_path = directory / f"combined-{started.replace(':', '').replace('-', '')}.log"
    config_path = directory / "config.json"
    result = {"sample_id": sample_id, "image_id": image["id"], "image": image["image"], "run": run, "started_at": started, "architecture": platform.machine(), "runner_hostname": socket.gethostname(), "log": str(log_path), "config": str(config_path), "expb_source": get("EXPB_SOURCE", "unknown"), "expb_env": get("EXPB_ENV_PASSTHROUGH"), "measurement_mode": get("MEASUREMENT_MODE", "standard"), "client": get("CLIENT", "nethermind"), "measurement_source": get("MEASUREMENT_SOURCE", "auto"), "status": "failed"}
    config = base
    try:
        config, scenario = render(base, image, run)
        # The artifact is retained and uploaded; only the private runtime copy may contain export credentials.
        artifact_config = dict(config)
        artifact_config.pop("export", None)
        config_path.write_text(json.dumps(artifact_config, indent=2) + "\n", encoding="utf-8")
        runtime_root = get("RUNNER_TEMP") or None
        with tempfile.TemporaryDirectory(prefix=".expb-runtime-", dir=runtime_root) as runtime_directory:
            runtime_config_path = Path(runtime_directory) / "config.json"
            runtime_config_path.write_text(json.dumps(config, indent=2) + "\n", encoding="utf-8")
            configured = parse_amount(get("AMOUNT"))
            if configured is None:
                configured = config["scenarios"][scenario].get("amount", 0)
            result["expected_amount"] = int(configured)
            command = [get("EXPB_BIN", "expb"), "execute-scenarios", "--config-file", str(runtime_config_path), "--per-payload-metrics", "--per-payload-metrics-logs", "--print-logs"]
            if get("DOTTRACE", "false") == "true": command += ["--dottrace", "--dottrace-mode", get("DOTTRACE_MODE", "sampling"), "--dotnet-trace"]
            if get("PERF", "false") == "true": command.append("--perf")
            # Cross-client runs share the k6 request clock; keep the Nethermind-only SSE feed off.
            if get("MEASUREMENT_SOURCE", "auto") == "engine-api": command.append("--no-client-metrics")
            result["command"] = command
            child_env = os.environ.copy()
            child_env.update(parse_pairs(get("EXPB_ENV_PASSTHROUGH")))
            if get("MEASUREMENT_MODE", "standard") == "compute-warm": child_env["EXPB_EVM_WARMUP"] = "1"
            with log_path.open("wb") as output:
                current = subprocess.Popen(command, cwd=get("EXPB_DATA_DIR"), env=child_env, stdout=output, stderr=subprocess.STDOUT, start_new_session=True)
                if cancelled: stop(signal.SIGTERM, None)
                code = current.wait()
    except Exception as error:
        code = 125
        result["error"] = str(error)
        with log_path.open("a", encoding="utf-8") as log:
            log.write(f"driver error: {error}\n")
    finally:
        if watchdog is not None: watchdog.cancel()
        current = None
    parsed, diagnostics = collect_metrics(log_path, use_sse=get("MEASUREMENT_SOURCE", "auto") != "engine-api")
    try:
        verify_clean(config)
        clean_error = ""
    except Exception as error:
        clean_error = str(error)
    expected, delivered = result.get("expected_amount"), parsed["delivered"]
    reasons = []
    if code != 0: reasons.append(f"execution exited with code {code}")
    if expected is None or delivered != expected or len(set(parsed["payload_indices"])) != delivered: reasons.append(f"delivery count/IDs expected {expected}, got {delivered}")
    if parsed["source"] == "SSE" and (delivered < 1 or parsed["sse_count"] not in (delivered, delivered - 1) or len(set(parsed["ids"])) != parsed["sse_count"]): reasons.append(f"SSE coverage/IDs are {parsed['sse_count']} for {delivered} delivered")
    if parsed["avg"] is None: reasons.append("processing metrics are missing")
    # Only Nethermind prints this marker; reference clients rely on expb's cleanup marker below.
    if get("CLIENT", "nethermind") == "nethermind" and not parsed["shutdown"]: reasons.append("normal shutdown marker is missing")
    if not parsed["cleanup"]: reasons.append("cleanup completed marker is missing")
    if parsed["exception_count"]: reasons.append(f"{parsed['exception_count']} exception line(s) detected")
    if parsed["invalid_count"]: reasons.append(f"{parsed['invalid_count']} invalid block line(s) detected")
    if clean_error: reasons.append("cleanup verification failed: " + clean_error)
    if get("MEASUREMENT_MODE", "standard") == "compute-warm" and (parsed["warm_bad"] or set(parsed["payload_indices"]) - set(parsed["warm_ok"])): reasons.append("compute-warm warmup is missing or failed")
    if cancelled: reasons.append("campaign cancellation requested")
    result.update({"finished_at": now(), "exit_code": code, "metrics": parsed, "sse_block_ids": parsed["ids"] if parsed["source"] == "SSE" else [], "cleanup_verified": not clean_error, "cleanup_problems": [clean_error] if clean_error else [], "exception_count": parsed["exception_count"], "invalid_count": parsed["invalid_count"], "severe_count": parsed["severe_count"], "failure_reasons": reasons, "status": "success" if not reasons else "failed"})
    (directory / "metadata.json").write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    print(f"{sample_id}: {result['status']} AVG={parsed['avg']}")
    if parsed["severe_count"]: print(f"::warning::severe runtime signal in {sample_id}; see combined log")
    for line in (diagnostics["exceptions"] + diagnostics["invalid"] + diagnostics["severe"])[:20]: print(line, file=sys.stderr)
    return result
def save_campaign(root: Path, started: str, images: list[dict], run_count: int, samples: list[dict], failure_reasons: list[str] | None = None) -> None:
    (root / "campaign.json").write_text(json.dumps({"started_at": started, "finished_at": now(), "images": images, "run_count": run_count, "architecture": platform.machine(), "runner_hostname": socket.gethostname(), "samples": samples, **({"failure_reasons": failure_reasons} if failure_reasons else {})}, indent=2) + "\n", encoding="utf-8")
def image_stats(items: list[dict]) -> tuple[float | None, float | None]:
    signatures = {sample_signature(x) for x in items}
    if len(signatures) != 1: return None, None
    mean = sum(x["metrics"]["avg"] for x in items) / len(items)
    return mean, math.sqrt(sum((x["metrics"]["avg"] - mean) ** 2 for x in items) / (len(items) - 1)) / mean * 100 if len(items) > 1 and mean else None

def sample_signature(sample: dict) -> tuple:
    metrics = sample["metrics"]
    return (metrics["source"], metrics["count"], metrics["delivered"], tuple(sample.get("sse_block_ids") or metrics["ids"]))

def comparison_delta(baseline: list[dict], candidate: list[dict], run_count: int) -> float | None:
    if len(baseline) != run_count or len(candidate) != run_count:
        return None
    baseline_signatures = {sample_signature(sample) for sample in baseline}
    candidate_signatures = {sample_signature(sample) for sample in candidate}
    if len(baseline_signatures) != 1 or baseline_signatures != candidate_signatures:
        return None
    baseline_mean = image_stats(baseline)[0]
    candidate_mean = image_stats(candidate)[0]
    if baseline_mean is None or candidate_mean is None or baseline_mean == 0:
        return None
    return (candidate_mean - baseline_mean) / baseline_mean * 100

def format_ms(value: float | None) -> str:
    return f"{value:.4f}" if value is not None else "n/a"

def write_summary(root: Path, images: list[dict], run_count: int, samples: list[dict]) -> None:
    successful = {image["id"]: [sample for sample in samples if sample["status"] == "success" and sample["image_id"] == image["id"]] for image in images}
    baseline_id = images[0]["id"]
    stats = {image["id"]: image_stats(successful[image["id"]]) for image in images}
    lines = ["## EXPB Campaign", "", f"Runner: {socket.gethostname()} ({platform.machine()})", f"Client: {get('CLIENT', 'nethermind')} (measurement_source={get('MEASUREMENT_SOURCE', 'auto')})", f"Samples: {len(samples)}", "", "**Images:**", *(f"- `{image['id']}` → `{image['image']}`" for image in images), "", "| Sample | Status | Source | Count | Processing AVG ms | Request AVG ms | Outside AVG ms | MGas/s | Request MGas/s |", "|---|---|---|---:|---:|---:|---:|---:|---:|"]
    rows = []
    for sample in samples:
        metrics = sample["metrics"]
        source = metrics.get("source", "n/a")
        processing_avg = format_ms(metrics.get("avg")) if source == "SSE" else "n/a"
        mgas_s = metrics.get("mgas_s")
        request_mgas_s = metrics.get("request_mgas_s")
        rows.append(f"| {sample['sample_id']} | {sample['status']} | {source} | {metrics.get('count', 0)} | {processing_avg} | {format_ms(metrics.get('request', {}).get('avg'))} | {format_ms(metrics.get('outside', {}).get('avg'))} | {f'{mgas_s:.2f}' if mgas_s is not None else 'n/a'} | {f'{request_mgas_s:.2f}' if request_mgas_s is not None else 'n/a'} |")
    lines += rows
    lines.append("")
    for image in images:
        image_id = image["id"]
        mean, cv = stats[image_id]
        delta = "n/a"
        if image_id != baseline_id:
            delta_value = comparison_delta(successful[baseline_id], successful[image_id], run_count)
            if delta_value is not None:
                delta = f"{delta_value:+.2f}%"
        lines.append(f"Image {image_id}: mean AVG={format_ms(mean)} ms; CV={f'{cv:.2f}%' if cv is not None else 'unavailable'}; delta vs {baseline_id}={delta}")
    (root / "summary.md").write_text("\n".join(lines) + "\n", encoding="utf-8")

def main() -> int:
    global cancelled
    root = Path(get("EXPB_CAMPAIGN_DIR"))
    root.mkdir(parents=True, exist_ok=True)
    started = now()
    try:
        base = json.loads(subprocess.check_output([get("YQ_BIN", "yq"), "-o=json", ".", get("EXPB_CONFIG_TEMPLATE")], text=True))
        raw = json.loads(get("EXPB_IMAGES_JSON"))
        if not isinstance(raw, list) or not raw: raise ValueError("images JSON must be a non-empty array")
        images = [{"id": str(item["id"]), "image": item["image"]} for item in raw]
        if len({x["id"] for x in images}) != len(images) or any(x["id"] in {".", ".."} or not re.fullmatch(r"[A-Za-z0-9_.-]+", x["id"]) for x in images): raise ValueError("image IDs must be unique and shell-safe")
        run_count = int(get("RUN_COUNT", "1"))
        if run_count < 1: raise ValueError("RUN_COUNT must be positive")
        # Dispatch input is the same for every sample, so a malformed value fails the campaign here rather than as a sample.
        render(base, images[0], 1)
        parse_pairs(get("EXPB_ENV_PASSTHROUGH"))
        verify_clean(base)
    except Exception as error:
        (root / "campaign.json").write_text(json.dumps({"status": "failed", "failure_reasons": [str(error)]}, indent=2) + "\n", encoding="utf-8")
        return 1
    signal.signal(signal.SIGTERM, stop)
    signal.signal(signal.SIGINT, stop)
    samples = []
    # A failed sample always abandons the rest of its own image - its remaining runs cannot complete the
    # set the stats need. Whether it abandons the campaign depends on what the campaign is for: see
    # CAMPAIGN_FAIL_FAST in run-expb-reproducible-benchmarks.yml.
    fail_fast = get("CAMPAIGN_FAIL_FAST", "true") != "false"
    for image in images:
        for run in range(1, run_count + 1):
            if cancelled: break
            item = run_sample(base, image, run, root)
            samples.append(item)
            save_campaign(root, started, images, run_count, samples)
            if item["status"] != "success":
                # Cleanup that failed to verify leaves the runner itself suspect, so no later image can be trusted.
                if fail_fast or not item.get("cleanup_verified", True): cancelled = True
                break
        if cancelled: break
    failure_reasons = []
    try:
        write_summary(root, images, run_count, samples)
    except Exception as error:
        failure_reasons.append(f"summary could not be written: {error}")
    try:
        save_campaign(root, started, images, run_count, samples, failure_reasons)
    except Exception as error:
        (root / "campaign.json").write_text(json.dumps({"status": "failed", "failure_reasons": [*failure_reasons, f"campaign record could not be written: {error}"]}, indent=2) + "\n", encoding="utf-8")
        return 1
    return 0 if not cancelled and not failure_reasons and len(samples) == len(images) * run_count and all(x["status"] == "success" for x in samples) else 1
if __name__ == "__main__":
    raise SystemExit(main())
