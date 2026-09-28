#!/usr/bin/env python3
"""Runs one scenario end to end: render args, start the enclave, wait, collect, tear down.

    runner/run_scenario.py --ceiling 235800 --attacker-role keccak-wide --k-retry 4

Raw output lands under results/<scenario-id>/ with enough provenance to reproduce the run.
No analysis happens here: this task collects, a later one interprets.
"""
from __future__ import annotations

import argparse
import json
import os
import re
import shutil
import subprocess
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
from datetime import datetime, timezone

HERE = os.path.dirname(os.path.abspath(__file__))
DEVNET_ROOT = os.path.dirname(HERE)
REPO_ROOT = os.path.dirname(os.path.dirname(DEVNET_ROOT))
TRAFFIC_SERVICE = "frame-tx-traffic"

CAMPAIGN_CEILINGS = [100000, 235800, 250000, 300000, 400000, 500000]
K_RETRIES = [1, 2, 4, 8]
ATTACKER_ROLES = ["keccak-wide", "signature-stuffed", "soispoke-groth16"]


def run(cmd: list[str], **kwargs) -> subprocess.CompletedProcess:
    printable = " ".join(cmd)
    print("+ {0}".format(printable), file=sys.stderr, flush=True)
    return subprocess.run(cmd, **kwargs)


def capture(cmd: list[str], check: bool = True) -> str:
    result = run(cmd, check=False, capture_output=True, text=True)
    if check and result.returncode != 0:
        raise RuntimeError(
            "command failed ({0}): {1}\n{2}".format(result.returncode, " ".join(cmd), result.stderr)
        )
    return result.stdout


def scenario_id(args) -> str:
    if args.scenario_id:
        return args.scenario_id
    return "c{0}-{1}-k{2}-a{3}".format(
        args.ceiling, args.attacker_role, args.k_retry, int(args.attacker_rate)
    )


GROTH16_STAGE = "groth16"
GROTH16_FILES = ("verifier.hex", "calldata-invalid.hex", "calldata-valid.hex", "gas.txt")


def stage_groth16_artifacts(path: str) -> str:
    """Returns the package-relative path Kurtosis should upload for the Groth16 sweeps.

    `plan.upload_files` only reads paths inside the package, so a sweep tree elsewhere (for
    example an unpacked frame-verify-gas release) is copied into the package's gitignored
    `groth16/` first. Only the files the generator reads are copied, which keeps the package
    upload small."""
    source = os.path.abspath(os.path.expanduser(path))
    if not os.path.isdir(source):
        raise SystemExit("--groth16-artifacts {0} is not a directory".format(path))
    if os.path.commonpath([source, DEVNET_ROOT]) == DEVNET_ROOT:
        return os.path.relpath(source, DEVNET_ROOT)

    sweeps = sorted(d for d in os.listdir(source)
                    if d.startswith("sweep-") and os.path.isdir(os.path.join(source, d)))
    if not sweeps:
        raise SystemExit("--groth16-artifacts {0} holds no sweep-* directories".format(path))

    stage = os.path.join(DEVNET_ROOT, GROTH16_STAGE)
    shutil.rmtree(stage, ignore_errors=True)
    for sweep in sweeps:
        os.makedirs(os.path.join(stage, sweep))
        for name in GROTH16_FILES:
            if os.path.exists(os.path.join(source, sweep, name)):
                shutil.copy2(os.path.join(source, sweep, name), os.path.join(stage, sweep, name))
    print("staged {0} Groth16 sweep(s) from {1} into {2}".format(len(sweeps), source, stage),
          file=sys.stderr)
    return GROTH16_STAGE


def render_args_file(args, sid: str, destination: str) -> str:
    """Appends the scenario block to the shared topology.

    Written as text rather than through a YAML library so the runner has no dependency beyond
    the standard library, and so the rendered file stays readable for a reviewer comparing it
    against scenarios/base.yaml."""
    with open(args.base_args) as handle:
        base = handle.read().rstrip("\n")

    if re.search(r"^frame_tx:", base, flags=re.MULTILINE):
        raise SystemExit(
            "{0} already carries a frame_tx block; pass it with --args-file to kurtosis "
            "directly, or point --base-args at a topology-only file".format(args.base_args)
        )

    block = [
        "",
        "# Rendered by runner/run_scenario.py; edit scenarios/base.yaml, not this file.",
        "frame_tx:",
        "  scenario_id: {0}".format(sid),
        "  max_verify_gas: {0}".format(args.ceiling),
        "  k_retry: {0}".format(args.k_retry),
        "  attacker_role: {0}".format(args.attacker_role),
        "  attacker_rate: {0}".format(args.attacker_rate),
        "  baseline_rate: {0}".format(args.baseline_rate),
        "  warmup_seconds: {0}".format(args.warmup),
        "  duration_seconds: {0}".format(args.duration),
        "  privacy_inclusion: {0}".format("true" if args.privacy_inclusion else "false"),
        "  split_traffic: {0}".format("true" if args.split_traffic else "false"),
    ]
    if args.el_param:
        block.append("  extra_el_params:")
        for param in args.el_param:
            block.append("    - {0}".format(param))
    if args.groth16_artifacts:
        block.append("  groth16_artifacts_path: {0}".format(args.groth16_artifacts))
    if args.nethermind_image:
        block.append("  nethermind_image: {0}".format(args.nethermind_image))
    if args.ethrex_image:
        block.append("  ethrex_image: {0}".format(args.ethrex_image))

    rendered = base + "\n" + "\n".join(block) + "\n"
    with open(destination, "w") as handle:
        handle.write(rendered)
    return destination


def enclave_exists(name: str) -> bool:
    listing = capture(["kurtosis", "enclave", "ls"], check=False)
    return any(line.split()[1:2] == [name] for line in listing.splitlines() if line.strip())


def port_of(enclave: str, service: str, port_id: str) -> str | None:
    result = run(
        ["kurtosis", "port", "print", enclave, service, port_id],
        check=False, capture_output=True, text=True,
    )
    if result.returncode != 0:
        return None
    url = result.stdout.strip()
    return url or None


def wait_for_completion(enclave: str, timeout: float, poll: float = 10.0) -> tuple[bool, str]:
    """Waits for the generator's completion marker in its own log stream."""
    deadline = time.monotonic() + timeout
    logs = ""
    while time.monotonic() < deadline:
        logs = capture(["kurtosis", "service", "logs", "--all", enclave, TRAFFIC_SERVICE], check=False)
        if "case=scenario_complete" in logs:
            return True, logs
        if "case=scenario_aborted" in logs:
            return False, logs
        time.sleep(poll)
    return False, logs


def snapshot_metrics(enclave: str, out_dir: str, start: float, end: float, step: str) -> dict:
    """Pulls the configured PromQL range queries out of the enclave's Prometheus."""
    summary = {"collected": False, "reason": "", "queries": {}}
    base_url = None
    for port_id in ("http", "prometheus", "metrics"):
        base_url = port_of(enclave, "prometheus", port_id)
        if base_url:
            break
    if not base_url:
        summary["reason"] = "prometheus port not published; add 'prometheus' to additional_services"
        return summary
    if not base_url.startswith("http"):
        base_url = "http://" + base_url

    with open(os.path.join(HERE, "metrics_queries.json")) as handle:
        queries = json.load(handle)["queries"]

    os.makedirs(out_dir, exist_ok=True)
    for name, expression in queries.items():
        url = "{0}/api/v1/query_range?query={1}&start={2}&end={3}&step={4}".format(
            base_url, urllib.parse.quote(expression), int(start), int(end), step
        )
        try:
            with urllib.request.urlopen(url, timeout=60) as response:
                payload = json.loads(response.read())
        except (urllib.error.URLError, OSError, ValueError) as error:
            summary["queries"][name] = "error: {0}".format(error)
            continue
        path = os.path.join(out_dir, "{0}.json".format(name))
        with open(path, "w") as handle:
            json.dump(payload, handle)
        series = len(payload.get("data", {}).get("result", []))
        summary["queries"][name] = "{0} series".format(series)
    summary["collected"] = True
    summary["prometheus_url"] = base_url
    return summary


def collect(enclave: str, sid: str, results_root: str, logs: str, started: float,
            finished: float, args, metrics_step: str) -> str:
    out_dir = os.path.join(results_root, sid)
    os.makedirs(out_dir, exist_ok=True)

    with open(os.path.join(out_dir, "traffic.log"), "w") as handle:
        handle.write(logs)
    # `kurtosis service logs` prefixes every line with "[<service>] ", so the RESULT rows are
    # not at the start of the line as they are on the container's own stdout.
    result_lines = [_strip_service_prefix(line) for line in logs.splitlines()
                    if "RESULT case=" in line]
    with open(os.path.join(out_dir, "{0}.result".format(sid)), "w") as handle:
        handle.write("\n".join(result_lines) + ("\n" if result_lines else ""))

    for service in ("prometheus", "grafana", "dora"):
        url = port_of(enclave, service, "http")
        if url:
            with open(os.path.join(out_dir, "{0}.url".format(service)), "w") as handle:
                handle.write(url + "\n")

    _copy_generator_results(out_dir)

    metrics = snapshot_metrics(
        enclave, os.path.join(out_dir, "metrics"), started, finished, metrics_step
    )

    health_lines = []
    el_images = {}
    for service_name in _el_service_names(enclave):
        service_logs = capture(["kurtosis", "service", "logs", "--all", enclave, service_name], check=False)
        with open(os.path.join(out_dir, "{0}.log".format(service_name)), "w") as handle:
            handle.write(service_logs)
        counts = {key: len(re.findall(pattern, service_logs)) for key, pattern in LOG_ALARMS.items()}
        health_lines.append("RESULT case=client_log_health scenario={0} node={1} lines={2} {3}".format(
            sid, service_name, service_logs.count("\n"),
            " ".join("{0}={1}".format(k, v) for k, v in counts.items())))
        image = _container_image(service_name)
        labels = _image_labels(image) if image else {}
        label_ceiling = labels.get("org.nethermind.frame_tx.max_verify_gas", "")
        el_images[service_name] = {"image": image, "labels": labels}
        health_lines.append(
            "RESULT case=image_ceiling scenario={0} node={1} image={2} label_max_verify_gas={3} "
            "revision={4} matches={5}".format(
                sid, service_name, image or "unknown", label_ceiling or "none",
                labels.get("org.opencontainers.image.revision", "unknown"),
                "yes" if label_ceiling == str(args.ceiling) else "no"))
    health_lines.append(_chain_agreement(enclave, sid, _el_service_names(enclave)))
    result_lines.extend(health_lines)
    with open(os.path.join(out_dir, "{0}.result".format(sid)), "a") as handle:
        handle.write("\n".join(health_lines) + ("\n" if health_lines else ""))

    manifest_path = os.path.join(DEVNET_ROOT, "images", "build-manifest.json")
    images = []
    if os.path.exists(manifest_path):
        with open(manifest_path) as handle:
            try:
                images = json.load(handle)
            except ValueError:
                images = []

    metadata = {
        "scenario_id": sid,
        "started_at": datetime.fromtimestamp(started, timezone.utc).isoformat(),
        "finished_at": datetime.fromtimestamp(finished, timezone.utc).isoformat(),
        "enclave": enclave,
        "parameters": {
            "max_verify_gas": args.ceiling,
            "k_retry": args.k_retry,
            "attacker_role": args.attacker_role,
            "attacker_rate": args.attacker_rate,
            "baseline_rate": args.baseline_rate,
            "warmup_seconds": args.warmup,
            "duration_seconds": args.duration,
            "privacy_inclusion": args.privacy_inclusion,
            "groth16_artifacts": getattr(args, "groth16_artifacts_source", args.groth16_artifacts),
            "nethermind_image": args.nethermind_image,
            "ethrex_image": args.ethrex_image,
        },
        "topology_file": os.path.abspath(args.base_args),
        "checkout_commit": _git_commit(REPO_ROOT),
        "el_images": el_images,
        "images": [i for i in images if i.get("max_verify_gas") in (None, str(args.ceiling))],
        "metrics": metrics,
        "result_lines": len(result_lines),
    }
    with open(os.path.join(out_dir, "run.json"), "w") as handle:
        json.dump(metadata, handle, indent=2, sort_keys=True)
        handle.write("\n")
    return out_dir


def _copy_generator_results(out_dir: str) -> None:
    """Lifts the generator's own files out of its container.

    The per-submission JSONL and the run metadata are written inside the traffic service, not
    to a Kurtosis files artifact, so the log stream alone does not carry them. Copied before
    teardown; a missing container is not fatal, the RESULT rows are already collected."""
    listing = capture(["docker", "ps", "-a", "--format", "{{.Names}}"], check=False)
    container = next((n for n in listing.splitlines() if n.startswith(TRAFFIC_SERVICE + "--")), None)
    if container is None:
        return
    staging = os.path.join(out_dir, "_generator")
    result = run(["docker", "cp", "{0}:/results/.".format(container), staging],
                 check=False, capture_output=True, text=True)
    if result.returncode != 0 or not os.path.isdir(staging):
        return
    for name in os.listdir(staging):
        source = os.path.join(staging, name)
        target = os.path.join(out_dir, name)
        # The RESULT file collected from the log stream is the authoritative one: it is the
        # same rows, and it exists even when the container is already gone.
        if os.path.isfile(source) and not os.path.exists(target):
            shutil.move(source, target)
    shutil.rmtree(staging, ignore_errors=True)


def _strip_service_prefix(line: str) -> str:
    index = line.find("RESULT case=")
    return line[index:] if index >= 0 else line


# Log lines a healthy run must not contain, per execution client log.
LOG_ALARMS = {
    "exception": r"Exception",
    "invalid_block": r"[Ii]nvalid [Bb]lock",
    "unhandled": r"Unhandled",
    "fatal": r"\bFATAL\b|\bFatal\b",
}


def _container_image(service_name: str) -> str:
    """The image the service's container actually ran, so a stale or stock tag is visible."""
    listing = capture(["docker", "ps", "-a", "--format", "{{.Names}}\t{{.Image}}"], check=False)
    for line in listing.splitlines():
        name, _, image = line.partition("\t")
        if name.startswith(service_name + "--"):
            return image
    return ""


def _image_labels(image: str) -> dict:
    raw = capture(["docker", "image", "inspect", "--format", "{{json .Config.Labels}}", image], check=False)
    try:
        return json.loads(raw) or {}
    except ValueError:
        return {}


def _rpc(url: str, method: str, params: list):
    body = json.dumps({"jsonrpc": "2.0", "id": 1, "method": method, "params": params}).encode()
    request = urllib.request.Request(url, data=body, headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(request, timeout=10) as response:
        return json.loads(response.read()).get("result")


def _chain_agreement(enclave: str, sid: str, services: list[str]) -> str:
    """Compares one block's hash across every execution client.

    Two slots below the lowest head, so a block still propagating does not read as a split. A
    client that diverged on a frame transaction reports a different hash, or none."""
    urls = {}
    for service in services:
        url = port_of(enclave, service, "rpc")
        if url:
            urls[service] = url if url.startswith("http") else "http://" + url
    try:
        heads = {s: int(_rpc(u, "eth_blockNumber", []), 16) for s, u in urls.items()}
        height = min(heads.values()) - 2
        hashes = {s: (_rpc(u, "eth_getBlockByNumber", [hex(height), False]) or {}).get("hash", "none")
                  for s, u in urls.items()}
    except (urllib.error.URLError, OSError, ValueError, TypeError) as error:
        return "RESULT case=chain_agreement scenario={0} agree=unknown reason=\"{1}\"".format(sid, error)
    agree = len(hashes) >= 2 and len(set(hashes.values())) == 1
    return "RESULT case=chain_agreement scenario={0} nodes={1} block={2} agree={3} {4}".format(
        sid, len(hashes), height, "yes" if agree else "no",
        " ".join("hash_{0}={1}".format(s, h) for s, h in sorted(hashes.items())))


def _el_service_names(enclave: str) -> list[str]:
    listing = capture(["kurtosis", "enclave", "inspect", enclave], check=False)
    return sorted({m.group(0) for m in re.finditer(r"\bel-\d+-[a-z0-9-]+\b", listing)})


def _git_commit(path: str) -> str:
    result = run(["git", "-C", path, "rev-parse", "HEAD"], check=False, capture_output=True, text=True)
    return result.stdout.strip() or "unknown"


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--ceiling", type=int, default=235800,
                        help="MAX_VERIFY_GAS; campaign set: {0}".format(CAMPAIGN_CEILINGS))
    parser.add_argument("--k-retry", type=int, default=1, choices=K_RETRIES)
    parser.add_argument("--attacker-role", default="keccak-wide",
                        choices=ATTACKER_ROLES + ["none"])
    parser.add_argument("--attacker-rate", type=float, default=25.0)
    parser.add_argument("--baseline-rate", type=float, default=2.0)
    parser.add_argument("--warmup", type=float, default=30.0)
    parser.add_argument("--duration", type=float, default=240.0)
    parser.add_argument("--privacy-inclusion", action="store_true")
    parser.add_argument("--split-traffic", action="store_true",
                        help="attack the first node, observe honest traffic on the second")
    parser.add_argument("--el-param", action="append", default=[],
                        help="extra Nethermind CLI param, repeatable")
    parser.add_argument("--groth16-artifacts", default="",
                        help="package-relative path to a frame-verify-gas sweep tree")
    parser.add_argument("--nethermind-image", default="",
                        help="override frame-tx-devnet/nethermind:vg<ceiling>, e.g. a stock-ceiling image "
                             "to show the compiled constant is what moved a result")
    parser.add_argument("--ethrex-image", default="",
                        help="override frame-tx-devnet/ethrex:vg<ceiling>")
    parser.add_argument("--scenario-id", default="")
    parser.add_argument("--enclave", default="")
    parser.add_argument("--base-args", default=os.path.join(DEVNET_ROOT, "scenarios", "base.yaml"))
    parser.add_argument("--results-dir", default=os.path.join(DEVNET_ROOT, "results"))
    parser.add_argument("--metrics-step", default="5s")
    parser.add_argument("--completion-slack", type=float, default=900.0,
                        help="extra wait beyond warmup+duration, covering enclave start-up "
                             "and the wait for the Hegota fork")
    parser.add_argument("--keep", action="store_true", help="leave the enclave running")
    parser.add_argument("--build-images", action="store_true",
                        help="build the client images for this ceiling first")
    parser.add_argument("--dry-run", action="store_true")
    args = parser.parse_args(argv if argv is not None else sys.argv[1:])

    if shutil.which("kurtosis") is None and not args.dry_run:
        raise SystemExit("kurtosis is not on PATH: https://docs.kurtosis.com/install")

    if args.groth16_artifacts and not args.dry_run:
        args.groth16_artifacts_source = os.path.abspath(os.path.expanduser(args.groth16_artifacts))
        args.groth16_artifacts = stage_groth16_artifacts(args.groth16_artifacts)

    sid = scenario_id(args)
    enclave = args.enclave or "frame-tx-{0}".format(sid.replace("_", "-"))
    rendered = os.path.join(DEVNET_ROOT, ".rendered-{0}.yaml".format(sid))
    render_args_file(args, sid, rendered)
    print("rendered args file: {0}".format(rendered), file=sys.stderr)

    if args.dry_run:
        with open(rendered) as handle:
            print(handle.read())
        os.remove(rendered)
        return 0

    if args.build_images:
        run([os.path.join(DEVNET_ROOT, "images", "build.sh"), str(args.ceiling)], check=True)

    if enclave_exists(enclave):
        run(["kurtosis", "enclave", "rm", "-f", enclave], check=False)

    # Clear any previous output for this scenario id first: a failed run that left stale
    # files behind would otherwise be graded against the last successful one.
    out_dir = os.path.join(args.results_dir, sid)
    if os.path.isdir(out_dir):
        shutil.rmtree(out_dir)

    started = time.time()
    launch = run(
        ["kurtosis", "run", ".", "--args-file", rendered, "--enclave", enclave],
        cwd=DEVNET_ROOT, check=False,
    )
    if launch.returncode != 0:
        print("kurtosis run failed; leaving the enclave for inspection", file=sys.stderr)
        return launch.returncode

    # Covers the generator's own wait for Hegota to activate (epoch 1) on top of the
    # measured window, plus enclave start-up.
    budget = args.warmup + args.duration + args.completion_slack
    completed, logs = wait_for_completion(enclave, budget)
    finished = time.time()

    out_dir = collect(enclave, sid, args.results_dir, logs, started, finished, args, args.metrics_step)
    print("results: {0}".format(out_dir))

    if not args.keep:
        run(["kurtosis", "enclave", "rm", "-f", enclave], check=False)
    os.remove(rendered)

    if not completed:
        print("scenario did not report completion; see {0}/traffic.log".format(out_dir),
              file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
