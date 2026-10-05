# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only
"""One prospective lifecycle diagnostic using the reviewed private RPC entrypoints."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import signal
import subprocess
import sys
import urllib.request
import pidfd_compat

HISTORICAL_HARNESS_HEAD = "4cf2f0aeb610227c4884da2c7f19c795cf2eaf8e"
BASELINE = "nethermindeth/nethermind@sha256:a6e80b5688b383d2b60bc5c45ba2b4b200f80d3624ad9b7eddd662496bb8909b"
JB_REF = "de1bcfadea47258ccacae2f420141032a82a9ded"


class Capability:
    def __init__(self, repository, environment):
        self.repository = repository
        self.environment = dict(environment)
        self.scripts = repository / "scripts/rpc-bench"
        self.outcomes = {}
        self.preflight = False
        self.prepared = False
        self.ownership_unknown = False

    def hold(self):
        self.ownership_unknown = True
        marker = Path(self.environment["RPC_PRIVATE_STORAGE_ROOT"]) / "native-ownership-hold.json"
        if not marker.exists():
            with marker.open("x") as stream:
                json.dump({"schema": 1, "ownership": "UNKNOWN_HOLD_NO_CLEANUP_OR_NEXT_DISPATCH"}, stream)

    def run(self, name, arguments, timeout=120, changes=None):
        if self.ownership_unknown:
            raise ValueError("OWNERSHIP_HOLD")
        env = self.environment | (changes or {})
        storage = Path(env["RPC_PRIVATE_STORAGE_ROOT"])
        if self.prepared and name != "finalize":
            storage = storage / ("spin-diagnostic-" + env["GITHUB_RUN_ID"] + "-" + env["GITHUB_RUN_ATTEMPT"])
        with (storage / ("capability-" + name + ".log")).open("xb") as log:
            descriptor = None
            try:
                try:
                    child = subprocess.Popen(arguments, cwd=self.repository, env=env, stdout=log,
                                             stderr=subprocess.STDOUT, start_new_session=True)
                except (OSError, InterruptedError, KeyboardInterrupt):
                    self.hold()
                    raise
                try:
                    descriptor = pidfd_compat.pidfd_open(child.pid)
                    pidfd_compat.pidfd_send_signal(descriptor, 0)
                    code = child.wait(timeout=timeout)
                except (OSError, subprocess.TimeoutExpired, InterruptedError, KeyboardInterrupt):
                    # Let the reviewed command close its own helpers; never kill its wrapper.
                    try:
                        completed = child.wait(timeout=20)
                        if completed != 0 and not (name == "finalize" and completed == 1):
                            self.hold()
                    except (subprocess.TimeoutExpired, InterruptedError, KeyboardInterrupt):
                        self.hold()
                    code = 124
                else:
                    if code != 0 and not (name == "finalize" and code == 1):
                        self.hold()
            finally:
                if descriptor is not None:
                    os.close(descriptor)
        self.outcomes[name] = code
        if code:
            raise ValueError(name)

    def audit(self, name, *arguments, **options):
        self.run(name, [sys.executable, str(self.scripts / "private_audit.py"), *arguments], **options)

    def verify_preload(self):
        storage = Path(self.environment["RPC_PRIVATE_STORAGE_ROOT"])
        root = storage / ("spin-diagnostic-" + self.environment["GITHUB_RUN_ID"] + "-" + self.environment["GITHUB_RUN_ATTEMPT"])
        expected = json.loads(Path(self.environment["RPC_NATIVE_EXPECTED_PINS_FILE"]).read_text())
        cid = (root / "capability-node-cid.log").read_text().strip()
        if not re.fullmatch("[0-9a-f]{64}", cid):
            raise ValueError("EXACT_CID_REQUIRED")
        self.run("container-pin", ["docker", "inspect", "--type", "container", "--format", "{{json .}}", cid])
        container = json.loads((root / "capability-container-pin.log").read_text())
        if (container["Id"] != cid or container["Config"]["Image"] != BASELINE
                or container["State"]["Running"] is not True or container["RestartCount"] != 0):
            raise ValueError("ACTUAL_CONTAINER_PIN")
        self.run("image-pin", ["docker", "image", "inspect", "--format", "{{json .}}", container["Image"]])
        image = json.loads((root / "capability-image-pin.log").read_text())
        if (BASELINE not in image["RepoDigests"] or image["Id"] != container["Image"]
                or image["Config"]["Labels"]["org.opencontainers.image.revision"] != expected["baseline_source"]
                or image["Os"] != "linux" or image["Architecture"] != "amd64"):
            raise ValueError("ACTUAL_IMAGE_PIN")
        process = Path("/proc") / str(container["State"]["Pid"])
        before = (process / "stat").read_text().rsplit(") ", 1)[1].split()[19]
        runtime_paths = {line.split(None, 5)[5] for line in (process / "maps").read_text().splitlines()
                         if len(line.split(None, 5)) == 6 and "libcoreclr.so" in line}
        runtime = "/usr/share/dotnet/shared/Microsoft.NETCore.App/10.0.12/libcoreclr.so"
        if runtime_paths != {runtime}:
            raise ValueError("ACTUAL_MAPPED_RUNTIME_PIN")
        inventory = {}
        for relative in ("/nethermind/nethermind.dll", "/nethermind/Nethermind.Evm.dll", "/nethermind/Nethermind.JsonRpc.dll",
                         "/nethermind/Nethermind.Serialization.Json.dll", "/nethermind/nethermind.runtimeconfig.json", runtime):
            path = process / "root" / relative.lstrip("/")
            inventory[relative] = {"sha256": digest_file(path), "bytes": path.stat().st_size}
        request = urllib.request.Request(self.environment["RPC_URL"], data=json.dumps({
            "jsonrpc": "2.0", "id": 1, "method": "eth_getBlockByNumber", "params": ["latest", False]}).encode(),
            headers={"Content-Type": "application/json"})
        with urllib.request.urlopen(request, timeout=20) as response:
            head = json.loads(response.read(1024 * 1024))["result"]
        fingerprint = Path(self.environment["STATE_DIR"]) / "db-baseline.txt"
        if (int(head["number"], 16) != 25490000 or head["hash"] != expected["snapshot_head_hash"]
                or digest_file(fingerprint) != expected["snapshot_fingerprint_sha256"]
                or digest_file(Path(self.environment["JB_ETH_CALL_CORPUS_FILE"])) != expected["corpus_sha256"]
                or (process / "stat").read_text().rsplit(") ", 1)[1].split()[19] != before):
            raise ValueError("PRELOAD_PROVENANCE_CHANGED")
        with (root / "native-preload-pin.json").open("x") as stream:
            json.dump({"schema": 1, "container_id": cid, "image_ref": BASELINE, "revision": expected["baseline_source"],
                       "actual_mapped_runtime": runtime, "first_binary_inventory": inventory,
                       "corpus_sha256": expected["corpus_sha256"], "head_hash": head["hash"],
                       "fingerprint_sha256": expected["snapshot_fingerprint_sha256"],
                       "resources": {"cpuset": container["HostConfig"]["CpusetCpus"], "memory": container["HostConfig"]["Memory"]}}, stream)

    def restore(self):
        self.audit("restore-state", "cpu-restore-state")
        marker = Path(self.environment["RPC_PRIVATE_STORAGE_ROOT"]) / "cpu-apply-attempt.json"
        if not marker.exists():
            self.outcomes["cpu-restore"] = 0
            return
        state = json.loads(marker.read_text())["state"]
        journal = Path(state) / "cpu-sysfs.orig"
        originals = journal.read_text().splitlines(keepends=True) if journal.exists() else []
        self.run("cpu-restore", ["bash", str(self.scripts / "cpu-stabilize.sh"), "restore"],
                 changes={"STATE_DIR": state})
        if journal.exists() and journal.stat().st_size:
            raise ValueError("CPU_RESTORE_INCOMPLETE")
        for line in originals:
            if line.endswith("\n"):
                path, value = line.rstrip("\n").split("\t", 1)
                if Path(path).read_text().strip() != value:
                    raise ValueError("CPU_ORIGINAL_NOT_RESTORED")

    def verify_cpu_cap(self):
        journal = Path(self.environment["SCRATCH_ROOT"]) / "cpu-state/cpu-sysfs.orig"
        originals = journal.read_text().splitlines()
        governors, caps = 0, 0
        for line in originals:
            raw_path, _ = line.split("\t", 1)
            path = Path(raw_path)
            actual = path.read_text().strip()
            if path.name == "scaling_governor":
                governors += 1
                if actual != "performance":
                    raise ValueError("CPU_GOVERNOR_NOT_APPLIED")
            elif path.name == "scaling_max_freq":
                caps += 1
                if actual != "3800000":
                    raise ValueError("CPU_CAP_NOT_APPLIED")
            elif path.name in ("no_turbo", "boost") and actual != ("1" if path.name == "no_turbo" else "0"):
                raise ValueError("CPU_TURBO_NOT_APPLIED")
        if not governors or caps != governors:
            raise ValueError("CPU_POLICY_CAPABILITY_MISSING")

    def execute(self):
        failure = None
        try:
            self.audit("preflight", "preflight")
            self.preflight = True
            self.audit("prepare", "prepare", "--arms", "4", "--warm-count", "6000", "--main-count", "1000")
            self.prepared = True
            if self.environment.get("RPC_NATIVE_INTEGRATION_FREEZE_FILE"):
                private_root = Path(self.environment["RPC_PRIVATE_STORAGE_ROOT"]) / (
                    "spin-diagnostic-" + self.environment["GITHUB_RUN_ID"] + "-" + self.environment["GITHUB_RUN_ATTEMPT"])
                for name, source in (("prospective-expected-pins.json", self.environment["RPC_NATIVE_EXPECTED_PINS_FILE"]),
                                     ("integration-freeze.json", self.environment["RPC_NATIVE_INTEGRATION_FREEZE_FILE"])):
                    with (private_root / name).open("xb") as target:
                        target.write(Path(source).read_bytes())
            self.audit("cpu-attempt", "cpu-apply-attempt")
            self.run("cpu-apply", ["bash", str(self.scripts / "cpu-stabilize.sh"), "apply"],
                     changes={"STATE_DIR": str(Path(self.environment["SCRATCH_ROOT"]) / "cpu-state")})
            self.verify_cpu_cap()
            self.run("start", ["bash", str(self.scripts / "start-node.sh")], timeout=480)
            self.audit("node-cid", "node-cid", "--state", self.environment["STATE_DIR"])
            self.verify_preload()
            for phase, seconds, seed in (("warm", "60s", "1001"), ("main", "10s", "1")):
                directory = (Path(self.environment["SCRATCH_ROOT"]) / "warmup-cell/default/nativecap" if phase == "warm"
                             else Path(self.environment["OUT_DIR"]) / "nativecap/main")
                directory.mkdir(parents=True, mode=0o700)
                self.audit(phase, "cell", timeout=600, changes={
                    "RPC_PRIVATE_AUDIT_PHASE": phase, "OUT_DIR": str(directory), "JB_DURATION": seconds,
                    "JB_EXACT_REQUEST_CAP": "6000" if phase == "warm" else "1000",
                    "JB_SEED": seed, "JB_CONTAINER_NAME": "jsonbench-nativecap-" + phase + "-" + self.environment["GITHUB_RUN_ID"],
                    "RESOURCE_SAMPLER_CONTAINER": self.environment["CONTAINER_NAME"] if phase == "main" else "",
                    "RESOURCE_SAMPLER_OUT": str(directory / "resources.json") if phase == "main" else ""})
            self.run("stop", ["bash", str(self.scripts / "stop-node.sh")], timeout=240)
            self.audit("node", "node", "--label", "nativecap", "--source-dir", self.environment["STATE_DIR"])
        except (OSError, ValueError, subprocess.SubprocessError, InterruptedError, KeyboardInterrupt) as error:
            if isinstance(error, (InterruptedError, KeyboardInterrupt)):
                self.hold()
            failure = type(error).__name__
        finally:
            if self.preflight and not self.ownership_unknown:
                for name, operation in (("teardown", lambda: self.audit("teardown", "teardown")),
                                        ("cpu-restore", self.restore)):
                    try:
                        if not self.ownership_unknown:
                            operation()
                    except (OSError, ValueError, subprocess.SubprocessError, InterruptedError, KeyboardInterrupt) as error:
                        if isinstance(error, (InterruptedError, KeyboardInterrupt)):
                            self.hold()
                        self.outcomes[name] = 1
                        failure = failure or name
            if self.prepared and not self.ownership_unknown:
                # Four-arm quality must fail for this deliberately partial run.
                try:
                    self.audit("finalize", "finalize", timeout=300, changes={
                        "RPC_PRIVATE_SWEEP_OUTCOME": "failure",
                        "RPC_PRIVATE_TEARDOWN_OUTCOME": "success" if self.outcomes.get("teardown") == 0 else "failure",
                        "RPC_PRIVATE_CPU_RESTORE_OUTCOME": "success" if self.outcomes.get("cpu-restore") == 0 else "failure"})
                    failure = failure or "UNEXPECTED_FULL_AUDIT_PASS"
                except (OSError, ValueError, subprocess.SubprocessError, InterruptedError, KeyboardInterrupt) as error:
                    if isinstance(error, (InterruptedError, KeyboardInterrupt)):
                        self.hold()
                    pass
        root = Path(self.environment["RPC_PRIVATE_STORAGE_ROOT"])
        audit_status = root / ("spin-diagnostic-" + self.environment["GITHUB_RUN_ID"] + "-" + self.environment["GITHUB_RUN_ATTEMPT"]) / "audit-status.json"
        public = root / ("rpc-private-public-" + self.environment["GITHUB_RUN_ID"] + "-" + self.environment["GITHUB_RUN_ATTEMPT"])
        archive = public / "encrypted"
        archive_ready = False
        try:
            manifest = json.loads((archive / "archive.json").read_text())
            cipher = archive / "raw.tar.gz.cms"
            digest = hashlib.sha256()
            with cipher.open("rb") as stream:
                for chunk in iter(lambda: stream.read(1024 * 1024), b""):
                    digest.update(chunk)
            archive_ready = (manifest["status"] == "ENCRYPTED_NOT_DECRYPTION_VERIFIED"
                and manifest["ciphertext"]["file"] == cipher.name and cipher.stat().st_size > 0
                and cipher.stat().st_size == manifest["ciphertext"]["bytes"]
                and digest.hexdigest() == manifest["ciphertext"]["sha256"])
        except (OSError, ValueError, KeyError, TypeError):
            pass
        expected_failure = audit_status.exists() and json.loads(audit_status.read_text())["quality"] == "FAIL"
        passed = not self.ownership_unknown and failure is None and self.outcomes.get("finalize") == 1 and archive_ready and expected_failure
        result = {"schema": 1, "scope": "NATIVE_LIFECYCLE_ONLY", "capability": "PASS" if passed else "FAIL",
                  "full_abba_quality": "FAIL", "timing_result": False, "pooling_eligibility": "UNKNOWN",
                  "outcomes": self.outcomes, "raw_sources_retained": True, "archive_ready": archive_ready,
                  "ownership": "UNKNOWN_HOLD_NO_CLEANUP_OR_NEXT_DISPATCH" if self.ownership_unknown else "COMMANDS_CLOSED"}
        with (root / "native-capability.json").open("x") as stream:
            json.dump(result, stream, indent=2)
            stream.write("\n")
        print(json.dumps(result))
        return 0 if passed else 1


def checked_environment(repository):
    if sys.platform != "linux":
        raise ValueError("LINUX_REQUIRED")
    pidfd_compat.preflight()
    head = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=repository, text=True).strip()
    dirty = subprocess.check_output(["git", "status", "--porcelain", "--untracked-files=no"], cwd=repository, text=True)
    harness_pin = json.loads(Path(__file__).with_name("harness-pin.json").read_text())
    reviewed_head = harness_pin.get("head")
    if (not isinstance(reviewed_head, str) or not re.fullmatch("[0-9a-f]{40}", reviewed_head)
            or reviewed_head == HISTORICAL_HARNESS_HEAD or head != reviewed_head or dirty):
        raise ValueError("REVIEWED_HARNESS_REQUIRED")
    manifest = json.loads(Path(__file__).with_name("source-pins.json").read_text())
    for relative, digest in manifest.items():
        if hashlib.sha256((repository / relative).read_bytes()).hexdigest() != digest:
            raise ValueError("HARNESS_BYTES_CHANGED")
    env = dict(os.environ)
    if env.get("CPU_SYSFS", "/sys/devices/system/cpu") != "/sys/devices/system/cpu":
        raise ValueError("ACTUAL_CPU_SYSFS_REQUIRED")
    for name in ("RPC_PRIVATE_STORAGE_ROOT", "RPC_PRIVATE_SHARED_SCRATCH_ROOT", "SCRATCH_ROOT", "STATE_ROOT",
                 "OUT_DIR", "DB_SOURCE", "GITHUB_RUN_ID", "GITHUB_RUN_ATTEMPT", "RUNNER_TEMP", "JB_ETH_CALL_CORPUS_FILE", "RPC_NATIVE_EXPECTED_PINS_FILE"):
        if not env.get(name):
            raise ValueError("REQUIRED_ENVIRONMENT")
    for name in ("GITHUB_RUN_ID", "GITHUB_RUN_ATTEMPT"):
        if not re.fullmatch(r"[1-9][0-9]{0,19}", env[name]):
            raise ValueError("RUN_IDENTITY")
    if Path(env["DB_SOURCE"]).resolve(strict=True) != Path("/mnt/sda/nethermind-flat-25490000"):
        raise ValueError("FROZEN_SNAPSHOT_REQUIRED")
    corpus = Path(env["JB_ETH_CALL_CORPUS_FILE"])
    if corpus.name != "eth-call-corpus-20260805T104605Z-497-safe.jsonl.gz" or not corpus.is_file():
        raise ValueError("FROZEN_CORPUS_REQUIRED")
    expected = json.loads(Path(env["RPC_NATIVE_EXPECTED_PINS_FILE"]).read_text())
    if (expected.get("schema") != 1 or expected.get("baseline_image") != BASELINE
            or expected.get("baseline_source") != "9bb024e1e84d370a3a27fad38c3b19717e6f67ea"
            or expected.get("runtime_version") != "10.0.12"
            or not all(isinstance(expected.get(key), str) and re.fullmatch("[0-9a-f]{64}", expected[key])
                       for key in ("corpus_sha256", "snapshot_fingerprint_sha256"))
            or not isinstance(expected.get("snapshot_head_hash"), str) or not re.fullmatch("0x[0-9a-f]{64}", expected["snapshot_head_hash"])):
        raise ValueError("EXPECTED_PROVENANCE_PINS_REQUIRED")
    if digest_file(corpus) != expected["corpus_sha256"]:
        raise ValueError("EXPECTED_CORPUS_DIFFERS")
    for key in list(env):
        if key.startswith(("JB_", "RESOURCE_SAMPLER_", "REFERENCE_")) and key != "JB_ETH_CALL_CORPUS_FILE":
            env.pop(key)
    env["STATE_ROOT"] = str(Path(env["RPC_PRIVATE_STORAGE_ROOT"]) / "state/sweep")
    Path(env["STATE_ROOT"]).mkdir(mode=0o700)
    env.update(RPC_PRIVATE_AUDIT="true", LABEL="nativecap", CLIENT="nethermind", CLIENT_TYPE="nethermind", INSTANCE="primary",
               NODE_IMAGE=BASELINE, DB_ISOLATION="overlay", SNAPSHOT_BLOCK="25490000", RPC_PORT="8545",
               RPC_URL="http://127.0.0.1:8545", HEALTH_TIMEOUT="240", STOP_GRACE="60", RPC_GAS_CAP="1000000000000",
               DOTTRACE="false", PERF="false", DOTNET_TRACE="false", DOTNET_DUMP="false", PROFILE_AFTER_WARMUP="false",
               CPU_MAX_FREQ_KHZ="3800000", JB_REF=JB_REF, JB_MODE="benchmark", JB_RPS="100", JB_HTML_REPORT="false",
               JB_DEEP_CHECK="false", JB_ETH_CALL_CORPUS="true", JB_REUSE_PREPARED="false", JB_MAX_FAIL_RATE_PCT="0",
               CORPUS_METHOD="eth_call", CORPUS_TRACER="", CORPUS_TRACE_TYPES="", JB_BENCHMARK_CONFIG="", JB_EXTRA_ARGS="",
               ADDITIONAL_FLAGS="", NODE_ENV_VARS="", LAYOUT_FLAGS="--FlatDb.Enabled=true", CPU_SYSFS="/sys/devices/system/cpu",
               ARM_SCRATCH_DIR="", DATA_DIR_TARGET="/execution-data", NETWORK="mainnet",
               JSONRPC_MODULES="Eth,Subscribe,Trace,TxPool,Web3,Proof,Net,Parity,Health,Rpc,Debug",
               JB_REPO="https://github.com/NethermindEth/json-bench.git", JB_CONCURRENCY="5", JB_TIMEOUT="30", JB_VUS="100", RPC_JB_PATCH_PROTOCOL="RPC_EXACT_COUNT_V1",
               JB_COMPARE_CONFIG="config/compare/defaults.yaml", JB_FAIL_ON_DIFF="false", JB_VALIDATE_SCHEMA="false",
               REFERENCE_RPC_URL="", REFERENCE_LABEL="reference", REFERENCE_CLIENT_TYPE="reference")
    env["STATE_DIR"] = str(Path(env["STATE_ROOT"]) / "nativecap")
    env["CONTAINER_NAME"] = "rpcbench-sweep-nativecap-" + env["GITHUB_RUN_ID"]
    env["NODE_ENV_FILE"] = str(Path(env["STATE_DIR"]) / "node.env")
    env["LOG_OUT"] = str(Path(env["STATE_DIR"]) / "node.log")
    env["DIAG_DIR"] = str(Path(env["SCRATCH_ROOT"]) / "diag")
    return env


def digest_file(path):
    digest = hashlib.sha256()
    with path.open("rb") as source:
        for chunk in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repository", type=Path, required=True)
    args = parser.parse_args()
    os.umask(0o077)
    signal.signal(signal.SIGTERM, lambda *_: (_ for _ in ()).throw(InterruptedError()))
    signal.signal(signal.SIGINT, lambda *_: (_ for _ in ()).throw(InterruptedError()))
    try:
        return Capability(args.repository.resolve(strict=True), checked_environment(args.repository)).execute()
    except (OSError, ValueError, subprocess.SubprocessError, InterruptedError):
        print("NATIVE_CAPABILITY_FAILED", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
