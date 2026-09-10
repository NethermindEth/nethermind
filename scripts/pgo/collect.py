#!/usr/bin/env python3
"""Collect startup-to-shutdown EventPipe data using the existing benchmark workloads."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import sys

from expb_errors import summarize


def run(*args, **kwargs):
    return subprocess.run(args, check=True, **kwargs)


def sha256(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def validate_expb_log(text):
    if re.search(r"exception|invalid[\s_-]*block|unhandled|fatal", text, re.IGNORECASE):
        raise ValueError("EXPB log contains a runtime failure")
    if "Nethermind is shut down" not in text or "Cleanup completed" not in text:
        raise ValueError("EXPB did not confirm graceful shutdown and cleanup")


def collect(args):
    import yaml

    if args.amount <= 0 or args.passes <= 0:
        raise ValueError("Training amounts and passes must be positive")
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    traces = output / "traces"
    traces.mkdir()
    repo = Path(__file__).resolve().parents[2]
    inspected = json.loads(run("docker", "image", "inspect", args.image,
                              capture_output=True, text=True).stdout)[0]
    image_id = inspected["Id"]
    metadata = {
        "workload": args.workload,
        "mode": args.mode,
        "image_id": image_id,
        "source_sha": run("git", "rev-parse", "HEAD", cwd=repo, capture_output=True, text=True).stdout.strip(),
        "source_diff_sha256": hashlib.sha256(run("git", "diff", "HEAD", "--binary", cwd=repo,
                                                capture_output=True).stdout).hexdigest(),
    }
    labels = inspected["Config"].get("Labels") or {}
    if labels.get("org.nethermind.pgo.collection") != "true":
        raise ValueError("Image must be built with Dockerfile.pgo")
    if labels.get("org.opencontainers.image.revision") != metadata["source_sha"]:
        raise ValueError("Collection image revision must match the current checkout")
    metadata["runtime"] = run("docker", "run", "--rm", "--entrypoint", "dotnet", image_id,
                              "--list-runtimes", capture_output=True, text=True).stdout
    if "Microsoft.NETCore.App 10.0.11 " not in metadata["runtime"]:
        raise ValueError("Update and validate the pinned dotnet-pgo tool for this runtime first")
    # The pinned MethodMemoryMap keys native-to-IL maps by method/re-JIT ID, not tier.
    # Sampling must keep one native body per method; instrumentation needs tiering.
    tiering = "0" if args.mode == "sampling" else "1"
    profiling_env = {"DOTNET_ReadyToRun": "0", "DOTNET_TieredCompilation": tiering, "DOTNET_TieredPGO": tiering}
    if args.workload == "fusaka":
        if args.expb_config is None:
            raise ValueError("--expb-config is required for Fusaka")
        config = yaml.safe_load(args.expb_config.read_text().replace("<<DOCKER_TAG>>", "unused").replace("<<DELAY>>", "0"))
        scenarios = config["scenarios"]
        if len(scenarios) != 1:
            raise ValueError("Expected exactly one Nethermind Fusaka scenario")
        scenario = next(iter(scenarios.values()))
        if scenario.get("client") != "nethermind":
            raise ValueError("Expected a Nethermind scenario")
        scenario["image"] = image_id
        scenario["amount"] = args.amount
        scenario["repeat"] = 1
        scenario.setdefault("extra_env", {}).update(profiling_env)
        scenario.setdefault("extra_volumes", {})["pgo"] = {
            "source": str(traces), "bind": "/nethermind/pgo", "mode": "rw"}
        config["pull_images"] = False
        config.setdefault("paths", {}).update(work=str(output / "work"), outputs=str(output / "expb"))
        rendered = output / "expb.yaml"
        rendered.write_text(yaml.safe_dump(config))
        metadata["config_sha256"] = sha256(args.expb_config)
        metadata["amount"] = args.amount
        for key in ("payloads", "fcus"):
            source = Path(scenario[key])
            if not source.is_absolute():
                source = args.expb_config.resolve().parent / source
            metadata[key + "_sha256"] = sha256(source)
        with (output / "expb.log").open("w") as log:
            try:
                run("expb", "execute-scenarios", "--config-file", str(rendered), "--per-payload-metrics",
                    "--print-logs", cwd=args.expb_config.resolve().parent, stdout=log, stderr=subprocess.STDOUT)
            except subprocess.CalledProcessError:
                log.flush()
                print(summarize((output / "expb.log").read_text(errors="replace")), file=sys.stderr)
                raise
        log_text = (output / "expb.log").read_text(errors="replace")
        validate_expb_log(log_text)
    else:
        if args.corpus is None or args.db_source is None:
            raise ValueError("--corpus and --db-source are required for eth_call")
        metadata["corpus_sha256"] = sha256(args.corpus)
        metadata["db_source"] = str(args.db_source.resolve())
        metadata["passes"] = args.passes
        env = dict(os.environ, CLIENT="nethermind", NODE_IMAGE=image_id, DB_SOURCE=str(args.db_source.resolve()),
                   SCRATCH_ROOT=str(output / "scratch"), STATE_DIR=str(output / "state"),
                   CONTAINER_NAME="pgo-" + hashlib.sha256(str(output).encode()).hexdigest()[:12], INSTANCE="primary",
                   PGO_OUTPUT_DIR=str(traces), LAYOUT_FLAGS="--FlatDb.Enabled=true", STOP_GRACE="180")
        env["NODE_ENV_VARS"] = " ".join(f"{key}={value}" for key, value in profiling_env.items())
        try:
            run("bash", str(repo / "scripts/rpc-bench/start-node.sh"), env=env)
            run("python3", str(repo / "scripts/rpc-bench/corpus_parity.py"), "timings",
                "--corpus", str(args.corpus.resolve()), "--rpc-url", "http://127.0.0.1:8545",
                "--out", str(output / "requests.csv"), "--passes", str(args.passes), "--rps", "100")
            outcomes = json.loads((output / "timings.meta.json").read_text())["outcomes"]
            if not outcomes.get("ok") or any(value for key, value in outcomes.items() if key != "ok"):
                raise ValueError(f"eth_call collection contains failed requests: {outcomes}")
            metadata["outcomes"] = outcomes
        finally:
            if (output / "state/node.env").exists():
                run("bash", str(repo / "scripts/rpc-bench/stop-node.sh"), env=env)
    found = list(traces.glob("*.nettrace"))
    if len(found) != 1 or found[0].stat().st_size == 0:
        raise ValueError("Expected exactly one nonempty trace from this run")
    metadata["trace"] = found[0].name
    metadata["trace_sha256"] = sha256(found[0])
    (output / "collection.json").write_text(json.dumps(metadata, indent=2) + "\n")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("workload", choices=["fusaka", "ethcall"])
    parser.add_argument("--image", required=True, help="Locally present image built with Dockerfile.pgo")
    parser.add_argument("--output", required=True, type=Path, help="New directory on the benchmark disk")
    parser.add_argument("--expb-config", type=Path)
    parser.add_argument("--amount", type=int, default=1000)
    parser.add_argument("--corpus", type=Path)
    parser.add_argument("--db-source", type=Path)
    parser.add_argument("--passes", type=int, default=25)
    parser.add_argument("--mode", choices=["instrumentation", "sampling"], default="instrumentation")
    collect(parser.parse_args())
