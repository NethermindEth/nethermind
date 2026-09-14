#!/usr/bin/env python3
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
import threading
ANSI = re.compile(r"\x1b\[[0-?]*[ -/]*[@-~]")
SSE = re.compile(r"\[payload-server\]\s+client_metric\s+block_number=(\d+)\s+processing_ms=(\d+(?:\.\d+)?)")
K6 = re.compile(r"^\s*\|\s*(\d+)\s*\|\s*[^|]+\|\s*(\d+(?:\.\d+)?)\s*\|\s*$", re.M)
WARM_OK = re.compile(r"\[payload-server\]\s+warmup\s+block=(\d+)\s+ok\b", re.I)
WARM_BAD = re.compile(r"\[payload-server\]\s+warmup\s+block=(\d+)\s+FAILED\b", re.I)
SEVERE = re.compile(r"\b(?:Unhandled|Fatal|ERROR)\b", re.I)
LIMIT = 4096
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
parse_pairs = lambda value: dict(item.strip().split("=", 1) for item in re.split(r"[\r\n,]", value) if "=" in item)
def render(base: dict, image: dict, run: int) -> tuple[dict, str]:
    config = json.loads(json.dumps(base))
    for old, new in (("<<DELAY>>", get("DELAY_SECONDS", "0")), ("<<AMOUNT>>", get("AMOUNT")), ("/mnt/sda/expb-data", get("EXPB_DATA_DIR")), ("/mnt/sda/nethermind-flat-snapshot", get("FLAT_SNAPSHOT_DIR")), ("/mnt/sda/nethermind-flat-25490000", get("FLAT_SNAPSHOT_BLOCK_DIR"))):
        config = json.loads(json.dumps(config).replace(old, new))
    scenarios = config.get("scenarios")
    if not isinstance(scenarios, dict) or not isinstance(scenarios.get("nethermind"), dict): raise ValueError("config has no scenarios.nethermind mapping")
    name = f"nethermind-{image['id']}-run{run}"
    config["scenarios"] = {name: (scenario := scenarios.pop("nethermind"))}
    scenario.update({"image": image["image"], **({"amount": int(get("AMOUNT"))} if get("AMOUNT") else {})})
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
    for label, args in (("containers", ["ps", "-aq"]), ("networks", ["network", "ls", "-q"])):
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
def force(process) -> None:
    if process is current: os.killpg(process.pid, signal.SIGKILL)
def stop(_signum: int, _frame: object) -> None:
    global cancelled, watchdog
    cancelled = True
    process = current
    if process is None or process.poll() is not None: return
    os.killpg(process.pid, signal.SIGTERM)
    watchdog = threading.Timer(int(get("CLEANUP_GRACE_SECONDS", "90")), force, (process,))
    watchdog.daemon = True
    watchdog.start()
def collect_metrics(text: str) -> tuple[dict, list[str], list[str], list[str]]:
    clean = ANSI.sub("", text)
    sse = [(int(a), float(b)) for a, b in SSE.findall(clean)]
    rows = [(int(a), float(b)) for a, b in K6.findall(clean)]
    values, source = sse or rows, "SSE" if sse else "TTFB"
    exceptions, invalid, severe = ([x for x in clean.splitlines() if "Exception" in x], [x for x in clean.splitlines() if re.search(r"invalid[\s_-]*blocks?", x, re.I)], [x for x in clean.splitlines() if SEVERE.search(x)])
    if not values: return {"source": "none", "count": 0, "avg": None, "ids": [], "payload_indices": [x[0] for x in rows], "delivered": len(rows), "sse_count": len(sse)}, exceptions, invalid, severe
    return {"source": source, "count": len(values), "avg": sum(x[1] for x in values) / len(values), "ids": [x[0] for x in values], "payload_indices": [x[0] for x in rows], "delivered": len(rows), "sse_count": len(sse)}, exceptions, invalid, severe
def run_sample(base: dict, image: dict, run: int, root: Path) -> dict:
    global current, watchdog
    sample_id = f"{image['id']}-run{run}"
    directory = root / sample_id
    directory.mkdir(parents=True, exist_ok=True)
    started = now()
    log_path = directory / f"combined-{started.replace(':', '').replace('-', '')}.log"
    config_path = directory / "config.json"
    result = {"sample_id": sample_id, "image_id": image["id"], "image": image["image"], "run": run, "started_at": started, "architecture": platform.machine(), "runner_hostname": socket.gethostname(), "log": str(log_path), "config": str(config_path), "expb_source": get("EXPB_SOURCE", "unknown"), "expb_env": get("EXPB_ENV_PASSTHROUGH"), "measurement_mode": get("MEASUREMENT_MODE", "standard"), "status": "failed"}
    config = base
    try:
        config, scenario = render(base, image, run)
        config_path.write_text(json.dumps(config, indent=2) + "\n", encoding="utf-8")
        configured = get("AMOUNT") or config["scenarios"][scenario].get("amount", 0)
        result["expected_amount"] = int(configured)
        command = [get("EXPB_BIN", "expb"), "execute-scenarios", "--config-file", str(config_path), "--per-payload-metrics", "--per-payload-metrics-logs", "--print-logs"]
        if get("DOTTRACE", "false") == "true": command += ["--dottrace", "--dottrace-mode", get("DOTTRACE_MODE", "sampling"), "--dotnet-trace"]
        if get("PERF", "false") == "true": command.append("--perf")
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
    text = ANSI.sub("", log_path.read_text(encoding="utf-8", errors="replace"))
    parsed, exceptions, invalid, severe = collect_metrics(text)
    try:
        verify_clean(config)
        clean_error = ""
    except Exception as error:
        clean_error = str(error)
    warm_ok = {int(x) for x in WARM_OK.findall(text)}
    warm_bad = {int(x) for x in WARM_BAD.findall(text)}
    expected, delivered = result.get("expected_amount"), parsed["delivered"]
    reasons = []
    if code != 0: reasons.append(f"execution exited with code {code}")
    if expected is None or delivered != expected or len(set(parsed["payload_indices"])) != delivered: reasons.append(f"delivery count/IDs expected {expected}, got {delivered}")
    if parsed["source"] == "SSE" and (delivered < 1 or parsed["sse_count"] not in (delivered, delivered - 1) or len(set(parsed["ids"])) != parsed["sse_count"]): reasons.append(f"SSE coverage/IDs are {parsed['sse_count']} for {delivered} delivered")
    if parsed["avg"] is None: reasons.append("processing metrics are missing")
    if "Nethermind is shut down" not in text: reasons.append("normal shutdown marker is missing")
    if "Cleanup completed" not in text: reasons.append("cleanup completed marker is missing")
    for lines, label in ((exceptions, "exception"), (invalid, "invalid block")):
        if lines: reasons.append(f"{len(lines)} {label} line(s) detected")
    if clean_error: reasons.append("cleanup verification failed: " + clean_error)
    if get("MEASUREMENT_MODE", "standard") == "compute-warm" and (warm_bad or set(parsed["payload_indices"]) - warm_ok): reasons.append("compute-warm warmup is missing or failed")
    if cancelled: reasons.append("campaign cancellation requested")
    result.update({"finished_at": now(), "exit_code": code, "metrics": parsed, "sse_block_ids": parsed["ids"] if parsed["source"] == "SSE" else [], "cleanup_verified": not clean_error, "cleanup_problems": [clean_error] if clean_error else [], "exception_count": len(exceptions), "invalid_count": len(invalid), "severe_count": len(severe), "failure_reasons": reasons, "status": "success" if not reasons else "failed"})
    (directory / "metadata.json").write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    print(f"{sample_id}: {result['status']} AVG={parsed['avg']}")
    if severe: print(f"::warning::severe runtime signal in {sample_id}; see combined log")
    for line in (exceptions + invalid + severe)[:20]: print(bounded(line), file=sys.stderr)
    return result
def save_campaign(root: Path, started: str, images: list[dict], run_count: int, samples: list[dict]) -> None:
    (root / "campaign.json").write_text(json.dumps({"started_at": started, "finished_at": now(), "images": images, "run_count": run_count, "architecture": platform.machine(), "runner_hostname": socket.gethostname(), "samples": samples}, indent=2) + "\n", encoding="utf-8")
def image_stats(items: list[dict]) -> tuple[float | None, float | None]:
    signatures = {(x["metrics"]["source"], x["metrics"]["count"], x["metrics"]["delivered"], tuple(x.get("sse_block_ids") or x["metrics"]["ids"])) for x in items}
    if len(signatures) != 1: return None, None
    mean = sum(x["metrics"]["avg"] for x in items) / len(items)
    return mean, math.sqrt(sum((x["metrics"]["avg"] - mean) ** 2 for x in items) / (len(items) - 1)) / mean * 100 if len(items) > 1 and mean else None
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
        verify_clean(base)
    except Exception as error:
        (root / "campaign.json").write_text(json.dumps({"status": "failed", "failure_reasons": [str(error)]}, indent=2) + "\n", encoding="utf-8")
        return 1
    signal.signal(signal.SIGTERM, stop)
    signal.signal(signal.SIGINT, stop)
    samples = []
    for image in images:
        for run in range(1, run_count + 1):
            if cancelled: break
            item = run_sample(base, image, run, root)
            samples.append(item)
            save_campaign(root, started, images, run_count, samples)
            if item["status"] != "success": cancelled = True
        if cancelled: break
    stats = {image_id: image_stats([x for x in samples if x["status"] == "success" and x["image_id"] == image_id]) for image_id in {x["image_id"] for x in samples}}
    lines = ["## EXPB Campaign", "", f"Runner: {socket.gethostname()} ({platform.machine()})", f"Samples: {len(samples)}", "", "| Sample | Status | Source | Count | AVG ms |", "|---|---|---:|---:|---:|"]
    lines += [f"| {x['sample_id']} | {x['status']} | {x['metrics'].get('source', 'n/a')} | {x['metrics'].get('count', 0)} | {x['metrics'].get('avg', 'n/a')} |" for x in samples]
    lines += ["", *[f"Image {image_id}: mean AVG={mean if mean is not None else 'n/a'} ms; CV={f'{cv:.2f}%' if cv is not None else 'unavailable'}" for image_id, (mean, cv) in stats.items()]]
    (root / "summary.md").write_text("\n".join(lines) + "\n", encoding="utf-8")
    save_campaign(root, started, images, run_count, samples)
    return 0 if not cancelled and len(samples) == len(images) * run_count and all(x["status"] == "success" for x in samples) else 1
raise SystemExit(main())
