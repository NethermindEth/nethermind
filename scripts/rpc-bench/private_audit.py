# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only
"""Opt-in private evidence for the existing corpus sweep; no new workload or cleanup."""
import argparse
import hashlib
import json
import math
import os
from pathlib import Path
import re
import stat
import subprocess
import sys
import time

COMPONENTS = Path(__file__).with_name("private_audit")
sys.path.insert(0, str(COMPONENTS))
import archive_private as archive_tool
from log_quality import ANSI_CSI, process_log_ok

MAX_FILE_BYTES = 256 * 1024 * 1024
NAME = re.compile(r"[A-Za-z0-9_.-]{1,200}")
TOOL_ERRORS = re.compile(r"(?im)^\s*(?:ERRO\[|time=\S+\s+level=(?:error|fatal)\b)|K6 command execution completed with errors")


def locations():
    run, attempt = os.environ["GITHUB_RUN_ID"], os.environ["GITHUB_RUN_ATTEMPT"]
    if not all(re.fullmatch(r"[1-9][0-9]{0,19}", value) for value in (run, attempt)):
        raise ValueError("RUN_IDENTITY")
    temporary = Path(os.environ["RPC_PRIVATE_STORAGE_ROOT"]).absolute()
    if temporary.name != "rpc-private-" + run + "-" + attempt or temporary.resolve(strict=True) != temporary:
        raise ValueError("STORAGE_ROOT")
    archive_tool.private_directory(temporary)
    # Keep the reviewed archive helper's exact ownership namespace unchanged.
    return temporary / ("spin-diagnostic-" + run + "-" + attempt), temporary / ("rpc-private-public-" + run + "-" + attempt)


def write(path, value):
    with path.open("x", encoding="utf-8") as stream:
        json.dump(value, stream, indent=2)
        stream.write("\n")


def context():
    root, public = locations()
    archive_tool.private_directory(root)
    archive_tool.private_directory(root / "outputs")
    preparation = json.loads((root / "preparation.json").read_text())
    if preparation["outputs"] != str(root / "outputs") or preparation["purpose"] != "RPC_PRIVATE_AUDIT":
        raise ValueError("OWNER")
    if directory_identity(root.parent) != preparation["storage_identity"] or any(
            directory_identity(Path(path)) != preparation["root_identities"][name]
            for name, path in preparation["roots"].items()):
        raise ValueError("PREPARED_ROOT_CHANGED")
    return root, public, preparation


def directory_identity(path):
    info = path.lstat()
    if path.resolve(strict=True) != path or not stat.S_ISDIR(info.st_mode):
        raise ValueError("DIRECTORY_ALIAS")
    return [info.st_dev, info.st_ino, stat.S_IFMT(info.st_mode), info.st_uid]


def prepare(arms, warm_count, main_count):
    root, _ = locations()
    if arms != 4 or min(warm_count, main_count) <= 0:
        raise ValueError("UNSUPPORTED_CELL")
    cert = subprocess.run(["/usr/bin/openssl", "x509", "-in", str(COMPONENTS / "recipient.crt"), "-outform", "DER"],
                          check=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=20)
    if hashlib.sha256(cert.stdout).hexdigest() != archive_tool.CERTIFICATE_SHA256:
        raise ValueError("CERTIFICATE_PIN")
    root.mkdir(mode=0o700)
    archive_tool.private_directory(root)
    (root / "outputs").mkdir(mode=0o700)
    roots = {name: str(Path(os.environ[name]).resolve(strict=True)) for name in ("OUT_DIR", "SCRATCH_ROOT", "STATE_ROOT")}
    storage, shared = storage_context()
    if Path(roots["SCRATCH_ROOT"]) != storage / "scratch" or any(not Path(roots[name]).is_relative_to(storage) for name in ("OUT_DIR", "STATE_ROOT")):
        raise ValueError("STORAGE_ANCHOR")
    roots["SHARED_SCRATCH_ROOT"] = str(shared)
    write(root / "preparation.json", dict(status="PREPARED_NOT_MEASURED", purpose="RPC_PRIVATE_AUDIT",
          outputs=str(root / "outputs"), roots=roots, storage_identity=directory_identity(root.parent),
          root_identities={name: directory_identity(Path(path)) for name, path in roots.items()}, expected_arms=arms,
          expected_counts={"warm": warm_count, "main": main_count}))


def arm_directory(root, label):
    if not NAME.fullmatch(label) or label in (".", ".."):
        raise ValueError("ARM_NAME")
    directory = root / "outputs" / label
    if not directory.exists():
        directory.mkdir(mode=0o700)
    archive_tool.private_directory(directory)
    return directory


def source_identity(source, allowed_root):
    source, allowed_root = Path(source).absolute(), Path(allowed_root).resolve(strict=True)
    if not source.exists():
        return None
    if not source.is_relative_to(allowed_root) or source.resolve(strict=True) != source:
        raise ValueError("SOURCE_ALIAS")
    return archive_tool.identity(source.lstat())


def copy_file(source, destination, allowed_root, required=True, previous=None):
    source = Path(source).absolute()
    current = source_identity(source, allowed_root)
    if current is None:
        if required:
            raise ValueError("SOURCE_MISSING")
        return None
    before = source.lstat()
    if not stat.S_ISREG(before.st_mode) or before.st_nlink != 1 or not 0 < before.st_size <= MAX_FILE_BYTES:
        raise ValueError("SOURCE_TYPE_OR_SIZE")
    if previous is not None and archive_tool.identity(before) == previous:
        raise ValueError("STALE_SOURCE")
    flags = os.O_RDONLY | getattr(os, "O_NOFOLLOW", 0) | getattr(os, "O_BINARY", 0)
    digest = hashlib.sha256()
    with os.fdopen(os.open(source, flags), "rb") as incoming, destination.open("xb") as outgoing:
        if archive_tool.identity(os.fstat(incoming.fileno())) != archive_tool.identity(before):
            raise ValueError("SOURCE_CHANGED")
        for block in iter(lambda: incoming.read(1024 * 1024), b""):
            outgoing.write(block)
            digest.update(block)
        if archive_tool.identity(os.fstat(incoming.fileno())) != archive_tool.identity(before):
            raise ValueError("SOURCE_CHANGED")
    if archive_tool.identity(source.lstat()) != archive_tool.identity(before):
        raise ValueError("SOURCE_CHANGED")
    return {"sha256": digest.hexdigest(), "bytes": before.st_size, "source_identity": list(current)}


def log_ok(path, shutdown=False):
    if not 0 < path.stat().st_size <= MAX_FILE_BYTES:
        return False
    value = ANSI_CSI.sub("", path.read_text(encoding="utf-8", errors="strict"))
    return process_log_ok(value) and not TOOL_ERRORS.search(value) and (not shutdown or "Nethermind is shut down" in value)


def counters(path):
    value = json.loads(path.read_text())
    if not isinstance(value, dict) or not isinstance(value.get("metrics"), dict):
        raise ValueError("COUNTER_SCHEMA")
    metrics = value["metrics"]
    def metric(name, field):
        entry = metrics[name]
        if not isinstance(entry, dict):
            raise ValueError("COUNTER_SCHEMA")
        values = entry.get("values", entry)
        if not isinstance(values, dict):
            raise ValueError("COUNTER_SCHEMA")
        result = values.get(field, values.get("value") if field == "rate" else None)
        if isinstance(result, bool) or not isinstance(result, (int, float)) or not math.isfinite(result):
            raise ValueError("COUNTER_MISSING_OR_NONFINITE")
        return result
    return dict(requests=metric("http_reqs", "count"), failed_rate=metric("http_req_failed", "rate"),
                dropped=metric("dropped_iterations", "count") if "dropped_iterations" in metrics else 0,
                check_fails=metric("checks", "fails"), check_passes=metric("checks", "passes"))


def valid_summary(path, expected):
    value = counters(path)
    # Constant-arrival duration has one endpoint ambiguity; this is not a request-limited executor.
    return (value["requests"] in (expected, expected + 1) and value["failed_rate"] == 0
            and value["dropped"] == 0 and value["check_fails"] == 0
            and value["check_passes"] == 2 * value["requests"])


def valid_resources(path, expected):
    value = json.loads(path.read_text())
    for name in ("wall_seconds", "samples", "cpu_seconds", "cpu_throttled_usec", "io_read_bytes", "io_write_bytes"):
        number = value[name]
        if isinstance(number, bool) or not isinstance(number, (int, float)) or not math.isfinite(number) or number < 0:
            return False
    return value["requests"] == expected and value["wall_seconds"] > 0 and value["samples"] >= 2


OWNER_LABEL = "codex.rpc.private-run"


def run_identity():
    return os.environ["GITHUB_RUN_ID"] + "-" + os.environ["GITHUB_RUN_ATTEMPT"]


def storage_context(require_owner=True):
    root = locations()[0].parent
    shared = Path(os.environ["RPC_PRIVATE_SHARED_SCRATCH_ROOT"]).absolute()
    if root.parent != shared or shared.resolve(strict=True) != shared:
        raise ValueError("STORAGE_PARENT")
    if require_owner:
        owner = json.loads((root / "owner.json").read_text())
        if (owner["storage"] != directory_identity(root) or owner["shared"] != directory_identity(shared)
                or owner["scratch"] != directory_identity(root / "scratch")):
            raise ValueError("STORAGE_OWNER_CHANGED")
    return root, shared


def mount_records(root):
    records = {}
    source = Path("/proc/self/mountinfo").read_text()
    if not source.strip():
        raise ValueError("MOUNT_LOOKUP_EMPTY")
    for line in source.splitlines():
        fields = line.split()
        if len(fields) < 10 or "-" not in fields or not fields[0].isdigit():
            raise ValueError("MOUNT_LOOKUP_INVALID")
        target = Path(re.sub(r"\\([0-7]{3})", lambda match: chr(int(match[1], 8)), fields[4]))
        if target == root or target.is_relative_to(root):
            if str(target) in records:
                raise ValueError("STACKED_MOUNT")
            records[str(target)] = line
    return records


def cpu_apply_attempt():
    root, _ = storage_context()
    state = owned_path(root / "scratch/cpu-state", root / "scratch", exists=False)
    marker = dict(run=run_identity(), storage_identity=directory_identity(root), state=str(state))
    with (root / "cpu-apply-attempt.json").open("x", encoding="utf-8") as stream:
        json.dump(marker, stream)
        stream.write("\n")
        stream.flush()
        os.fsync(stream.fileno())
    return str(state)


def cpu_restore_state():
    root, _ = storage_context()
    path = root / "cpu-apply-attempt.json"
    if path.is_symlink():
        raise ValueError("CPU_ATTEMPT_ALIAS")
    if not path.exists():
        return ""
    info = path.lstat()
    if not stat.S_ISREG(info.st_mode) or info.st_nlink != 1 or not 0 < info.st_size <= 4096:
        raise ValueError("CPU_ATTEMPT_FILE")
    marker = json.loads(path.read_text(encoding="utf-8"))
    state = root / "scratch/cpu-state"
    if (marker["run"] != run_identity() or marker["storage_identity"] != directory_identity(root)
            or marker["state"] != str(state) or state.resolve() != state):
        raise ValueError("CPU_ATTEMPT_OWNER")
    return str(state)


def preflight():
    root, shared = storage_context(require_owner=False)
    names = command_output(["docker", "ps", "-a", "--format", "{{.Names}}"])
    if any(name.startswith(("rpcbench-", "nethermind-rpcbench", "ethcallchaos-bench", "jsonbench-", "expb"))
           for name in names.splitlines()) or command_output(["docker", "ps", "-aq", "--filter", "label=expb"]) or mount_records(shared):
        raise ValueError("FOREIGN_BENCHMARK_RESOURCES")
    states = [shared / "cpu-state/cpu-sysfs.orig", *shared.glob("rpc-private-*/scratch/cpu-state/cpu-sysfs.orig")]
    if any(path.is_symlink() or (path.exists() and path.stat().st_size > 0) for path in states):
        raise ValueError("UNRESTORED_CPU_STATE")
    write(root / "owner.json", dict(storage=directory_identity(root), shared=directory_identity(shared),
                                    scratch=directory_identity(root / "scratch")))


def owned_path(path, parent, exists=True):
    path = Path(path).absolute()
    if path == parent or not path.is_relative_to(parent) or path.resolve(strict=exists) != path:
        raise ValueError("OWNED_PATH_REQUIRED")
    return path


def name_available(name):
    if not NAME.fullmatch(name) or command_output(["docker", "ps", "-aq", "--filter", "name=^/" + name + "$"]):
        raise ValueError("CONTAINER_NAME_IN_USE")


def register_container(directory, stem, name, image, scratch=None):
    root, _ = storage_context()
    directory = owned_path(directory, root)
    if not directory.is_dir() or stem not in ("node", "tool", "version"):
        raise ValueError("OWNER_DIRECTORY")
    name_available(name)
    image_id = command_output(["docker", "image", "inspect", "--format", "{{.Id}}", image])
    if not re.fullmatch(r"sha256:[0-9a-f]{64}", image_id):
        raise ValueError("OWNED_IMAGE_ID")
    value = dict(directory_identity=directory_identity(directory), name=name, image=image, image_id=image_id, run=run_identity())
    if scratch is not None:
        scratch = owned_path(scratch, root / "scratch", exists=False)
        if scratch.exists() or mount_records(scratch):
            raise ValueError("NODE_SCRATCH_IN_USE")
        value["scratch"] = str(scratch)
    write(directory / (stem + "-owner.json"), value)
    return directory / (stem + ".cid")


def owner_record(directory, stem):
    root, _ = storage_context()
    directory = owned_path(directory, root)
    path = directory / (stem + "-owner.json")
    info = path.lstat()
    if path.resolve(strict=True) != path or not stat.S_ISREG(info.st_mode) or info.st_nlink != 1 or info.st_size > 65536:
        raise ValueError("OWNER_FILE")
    record = json.loads(path.read_text())
    if record["directory_identity"] != directory_identity(directory) or record["run"] != run_identity():
        raise ValueError("RESOURCE_OWNER_CHANGED")
    return record


def owned_cid(directory, stem, allow_absent=False):
    record = owner_record(directory, stem)
    path = Path(directory) / (stem + ".cid")
    if not path.exists():
        raise ValueError("OWNED_CID_MISSING")
    info = path.lstat()
    if path.resolve(strict=True) != path or not stat.S_ISREG(info.st_mode) or info.st_nlink != 1 or info.st_size > 65:
        raise ValueError("OWNED_CID_FILE")
    cid = path.read_text().strip()
    if not re.fullmatch(r"[0-9a-f]{64}", cid):
        raise ValueError("OWNED_CID_INVALID")
    present = command_output(["docker", "ps", "-aq", "--no-trunc", "--filter", "id=" + cid])
    if not present and allow_absent:
        return None
    if present != cid:
        raise ValueError("OWNED_CID_NOT_FOUND")
    value = json.loads(command_output(["docker", "inspect", "--type", "container", "--format", "{{json .}}", cid]))
    if (value["Id"] != cid or value["Name"] != "/" + record["name"] or value["Image"] != record["image_id"] or value["Config"]["Image"] != record["image"]
            or value["Config"].get("Labels", {}).get(OWNER_LABEL) != record["run"]):
        raise ValueError("CONTAINER_OWNER_CHANGED")
    return cid


def stop_owned_tool(directory, stem):
    cid = owned_cid(directory, stem, allow_absent=True)
    if cid:
        command_output(["docker", "stop", "--time", "30", cid], timeout=40)
        command_output(["docker", "rm", cid])


def node_mounted(directory, target):
    record = owner_record(directory, "node")
    scratch = Path(record["scratch"])
    target = owned_path(target, scratch)
    records = mount_records(scratch)
    if set(records) != {str(target)}:
        raise ValueError("OWNED_MOUNT_REQUIRED")
    write(Path(directory) / "node-mount.json", dict(scratch_identity=directory_identity(scratch), target=str(target), record=records[str(target)]))


def node_unmount(directory):
    record = owner_record(directory, "node")
    if owned_cid(directory, "node", allow_absent=True):
        raise ValueError("NODE_STILL_EXISTS")
    name_available(record["name"])
    scratch = Path(record["scratch"])
    pin = json.loads((Path(directory) / "node-mount.json").read_text())
    if directory_identity(scratch) != pin["scratch_identity"]:
        raise ValueError("SCRATCH_OWNER_CHANGED")
    records = mount_records(scratch)
    if not records:
        return
    if records != {pin["target"]: pin["record"]}:
        raise ValueError("MOUNT_OWNER_CHANGED")
    command = ["umount", "--", pin["target"]]
    if os.geteuid() != 0:
        command.insert(0, "sudo")
    command_output(command)
    if mount_records(scratch):
        raise ValueError("MOUNT_REMAINS")


def node_done(directory):
    record = owner_record(directory, "node")
    if owned_cid(directory, "node", allow_absent=True) or mount_records(Path(record["scratch"])):
        raise ValueError("NODE_TEARDOWN_INCOMPLETE")
    write(Path(directory) / "node-done.json", {"complete": True})


def teardown():
    root, _ = storage_context()
    complete = True
    for path in root.glob("spin-diagnostic-*/outputs/*/*/*-owner.json"):
        stem = path.name.removesuffix("-owner.json")
        if stem not in ("tool", "version"):
            raise ValueError("UNEXPECTED_TOOL_RECORD")
        try:
            stop_owned_tool(path.parent, stem)
        except (OSError, ValueError, KeyError, TypeError, subprocess.SubprocessError):
            complete = False
    for path in (root / "state/sweep").glob("*/node-owner.json"):
        directory = path.parent
        if (directory / "node-done.json").exists():
            continue
        try:
            if (directory / "node.env").exists():
                environment = dict(os.environ, STATE_DIR=str(directory), NODE_ENV_FILE=str(directory / "node.env"),
                                   LOG_OUT=str(directory / "node.log"), RPC_PRIVATE_AUDIT="true")
                result = subprocess.run([str(Path(__file__).with_name("stop-node.sh"))], env=environment,
                                        stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=240, check=False)
                if result.returncode:
                    complete = False
            else:
                if owned_cid(directory, "node", allow_absent=True):
                    raise ValueError("NODE_STATE_MISSING")
                node_unmount(directory)
        except (OSError, ValueError, KeyError, TypeError, subprocess.SubprocessError):
            complete = False
    if mount_records(root):
        complete = False
    return complete


def cleanup_sources(audit_outcome, upload_outcome, teardown_outcome, cpu_outcome):
    if any(outcome != "success" for outcome in (audit_outcome, upload_outcome, teardown_outcome, cpu_outcome)):
        return False
    root, _, preparation = context()
    storage = root.parent
    if storage.name != "rpc-private-" + os.environ["GITHUB_RUN_ID"] + "-" + os.environ["GITHUB_RUN_ATTEMPT"]:
        raise ValueError("STORAGE_NAME")
    if directory_identity(storage) != preparation["storage_identity"]:
        raise ValueError("STORAGE_CHANGED")
    storage_context()
    cpu_state = storage / "scratch/cpu-state/cpu-sysfs.orig"
    if mount_records(storage) or (cpu_state.exists() and cpu_state.stat().st_size > 0):
        raise ValueError("OWNED_RESOURCES_REMAIN")
    import shutil
    shutil.rmtree(storage)
    return True


def command_output(command, timeout=20):
    result = subprocess.run(command, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                            check=True, timeout=timeout)
    if len(result.stdout) > 1024 * 1024:
        raise ValueError("PROVENANCE_OUTPUT_SIZE")
    return result.stdout.decode("utf-8", errors="strict").strip()


def verified_source(source, expected):
    if not re.fullmatch(r"[0-9a-f]{40}", expected):
        raise ValueError("SOURCE_REF")
    actual = command_output(["git", "-C", str(source), "rev-parse", "--verify", "HEAD"])
    if actual != expected:
        raise ValueError("SOURCE_HEAD")
    command_output(["git", "-C", str(source), "diff", "--quiet", "HEAD", "--"])
    return actual


def file_pin(path):
    before = path.lstat()
    if path.resolve(strict=True) != path or not stat.S_ISREG(before.st_mode) or before.st_nlink != 1 or before.st_size <= 0:
        raise ValueError("PIN_SOURCE")
    digest = archive_tool.digest_file(path)
    if archive_tool.identity(before) != archive_tool.identity(path.lstat()):
        raise ValueError("PIN_SOURCE_CHANGED")
    return dict(sha256=digest, bytes=before.st_size)


def tool_version(image_id, directory):
    label_hash = hashlib.sha256(os.environ["LABEL"].encode()).hexdigest()[:12]
    name = "rpcbench-version-" + run_identity() + "-" + label_hash + "-" + os.environ["RPC_PRIVATE_AUDIT_PHASE"]
    cidfile = register_container(directory, "version", name, image_id)
    try:
        command_output(["docker", "create", "--cidfile", str(cidfile), "--name", name,
                        "--label", OWNER_LABEL + "=" + run_identity(), "--network", "none", "--read-only",
                        "--entrypoint", "k6", image_id, "version"])
        container = owned_cid(directory, "version")
        version = command_output(["docker", "start", "--attach", container])
        code = command_output(["docker", "inspect", "--format", "{{.State.ExitCode}}", container])
        if code != "0" or len(version) > 4096 or not re.match(r"^k6 v2\.1\.0(?:\s|$)", version):
            raise ValueError("K6_VERSION")
        return version
    finally:
        stop_owned_tool(directory, "version")


def snapshot_head(url, expected):
    # Only the already running local snapshot endpoint is admissible.
    if url not in ("http://localhost:8545", "http://127.0.0.1:8545"):
        raise ValueError("RPC_ENDPOINT")
    import urllib.request
    request = urllib.request.Request(url, data=b'{"jsonrpc":"2.0","id":1,"method":"eth_getBlockByNumber","params":["latest",false]}',
                                     headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(request, timeout=15) as response:
        body = response.read(8 * 1024 * 1024 + 1)
    if len(body) > 8 * 1024 * 1024:
        raise ValueError("HEAD_RESPONSE_SIZE")
    value = json.loads(body)
    head = value["result"]
    number, block_hash = head["number"], head["hash"]
    if "error" in value or not re.fullmatch(r"0x[0-9a-fA-F]+", number) or int(number, 16) != expected or not re.fullmatch(r"0x[0-9a-fA-F]{64}", block_hash):
        raise ValueError("SNAPSHOT_HEAD")
    return dict(number=int(number, 16), hash=block_hash.lower())


def runtime_pin(image, source_head, built_current):
    root, _, preparation = context()
    label, phase = os.environ["LABEL"], os.environ["RPC_PRIVATE_AUDIT_PHASE"]
    if phase not in preparation["expected_counts"] or built_current != "true":
        raise ValueError("FRESH_TOOL_BUILD_REQUIRED")
    directory = arm_directory(root, label) / phase
    archive_tool.private_directory(directory)
    source = Path(preparation["roots"]["SCRATCH_ROOT"]) / "jsonbench/src"
    head = verified_source(source, os.environ["JB_REF"])
    if head != source_head:
        raise ValueError("SOURCE_CHANGED_DURING_BUILD")
    tool_id = command_output(["docker", "image", "inspect", "--format", "{{.Id}}", image])
    if not re.fullmatch(r"sha256:[0-9a-f]{64}", tool_id):
        raise ValueError("TOOL_IMAGE_ID")
    node_name = "rpcbench-sweep-" + label + "-" + os.environ["GITHUB_RUN_ID"]
    node_cid = owned_cid(Path(preparation["roots"]["STATE_ROOT"]) / label, "node")
    observed = json.loads(command_output(["docker", "inspect", "--type", "container", "--format", "{{json .}}", node_cid]))
    node_id, node_image, node_ref = observed["Id"], observed["Image"], observed["Config"]["Image"]
    if (observed["Name"] != "/" + node_name or observed["State"]["Running"] is not True
            or not re.fullmatch(r"[0-9a-f]{64}", node_id) or not re.fullmatch(r"sha256:[0-9a-f]{64}", node_image)
            or not re.fullmatch(r"[^\s]+@sha256:[0-9a-f]{64}", node_ref)):
        raise ValueError("NODE_IMAGE_IDENTITY")
    fingerprint = Path(preparation["roots"]["STATE_ROOT"]) / label / "db-baseline.txt"
    result = dict(schema=1, observed_before_load_ns=time.time_ns(),
                  tool=dict(image_id=tool_id, json_bench_commit=head, k6_version=tool_version(tool_id, directory),
                            build_in_current_invocation=True, tracked_source_clean=True),
                  node=dict(container_id=node_id, image_id=node_image, image_ref=node_ref),
                  corpus=file_pin(Path(os.environ["JB_ETH_CALL_CORPUS_FILE"]).absolute()),
                  snapshot=dict(head=snapshot_head(os.environ["RPC_URL"], int(os.environ["SNAPSHOT_BLOCK"])),
                                fingerprint=file_pin(fingerprint)))
    write(directory / "runtime-pin.json", result)
    shared = {name: result[name] for name in ("tool", "corpus", "snapshot")}
    for previous in (root / "outputs").glob("*/*/runtime-pin.json"):
        if previous == directory / "runtime-pin.json":
            continue
        prior = json.loads(previous.read_text())
        completed = json.loads(previous.with_name("runtime-pin-complete.json").read_text())
        if completed["complete"] is not True or any(prior[name] != shared[name] for name in shared):
            raise ValueError("CROSS_CELL_PROVENANCE_MISMATCH")
        if previous.parent.parent.name == label and prior["node"] != result["node"]:
            raise ValueError("NODE_CHANGED_WITHIN_ARM")
        if prior["node"]["image_ref"] == node_ref and prior["node"]["image_id"] != node_image:
            raise ValueError("NODE_IMAGE_CHANGED")
    # The first record remains intact on a mismatch; completion is a separate marker.
    write(directory / "runtime-pin-complete.json", {"complete": True, "image_id": tool_id})
    return tool_id


def cell(child_umask=0o022):
    root, _, preparation = context()
    phase, label = os.environ["RPC_PRIVATE_AUDIT_PHASE"], os.environ["LABEL"]
    if phase not in preparation["expected_counts"]:
        raise ValueError("PHASE")
    directory = arm_directory(root, label) / phase
    directory.mkdir(mode=0o700)
    result = dict(phase=phase, returncode=None, complete=False, files={}, expected_count=preparation["expected_counts"][phase])
    started = time.time_ns()
    try:
        scratch = Path(preparation["roots"]["SCRATCH_ROOT"])
        cell_dir = Path(os.environ["OUT_DIR"])
        cell_root = scratch if phase == "warm" else Path(preparation["roots"]["OUT_DIR"])
        paths = [
            (scratch / "jsonbench/jsonbench-tool.log", "tool.log", scratch, True),
            (scratch / "jsonbench/io/out/summary.json", "summary.raw.json", scratch, True),
            (cell_dir / "summary.json", "summary.json", cell_root, True),
            (cell_dir / "resources.json", "resources.json", cell_root, phase == "main"),
        ]
        previous = {name: source_identity(source, anchor) for source, name, anchor, _ in paths}
        write(directory / "started.json", {"time_ns": started, "previous_sources": previous})
        with (directory / "wrapper.log").open("xb") as stream:
            run = subprocess.run([str(Path(__file__).with_name("run-jsonbench.sh"))],
                                 stdout=stream, stderr=subprocess.STDOUT, check=False, umask=child_umask)
        result["returncode"] = run.returncode
        result["files"]["wrapper.log"] = {"sha256": archive_tool.digest_file(directory / "wrapper.log"),
                                              "bytes": (directory / "wrapper.log").stat().st_size}
        failed = False
        for source, name, anchor, required in paths:
            try:
                record = copy_file(source, directory / name, anchor, required, previous[name])
                if record is not None:
                    result["files"][name] = record
            except (OSError, ValueError):
                failed = True
        raw_counters = counters(directory / "summary.raw.json")
        sanitized_counters = counters(directory / "summary.json")
        result["observed_counters"] = raw_counters
        runtime_complete = json.loads((directory / "runtime-pin-complete.json").read_text(encoding="utf-8"))["complete"] is True
        result["complete"] = runtime_complete and not failed and run.returncode == 0 and all(
            log_ok(directory / name) for name in ("wrapper.log", "tool.log")
        ) and valid_summary(directory / "summary.raw.json", result["expected_count"]) and valid_summary(
            directory / "summary.json", result["expected_count"]) and raw_counters == sanitized_counters and (
            phase != "main" or valid_resources(directory / "resources.json", raw_counters["requests"]))
    except (OSError, ValueError, KeyError, TypeError, subprocess.SubprocessError):
        result["complete"] = False
    write(directory / "audit.json", result)
    return result["complete"]


def node(label, source_dir):
    root, _, preparation = context()
    directory = arm_directory(root, label) / "node"
    directory.mkdir(mode=0o700)
    source_dir = Path(source_dir)
    result = dict(complete=False, files={})
    failed = False
    for name in ("node.log", "node.env", "db-baseline.txt", "db-final.txt"):
        try:
            result["files"][name] = copy_file(source_dir / name, directory / name, preparation["roots"]["STATE_ROOT"])
        except (OSError, ValueError):
            failed = True
    try:
        result["complete"] = not failed and log_ok(directory / "node.log", shutdown=True) and (
            (directory / "db-baseline.txt").read_bytes() == (directory / "db-final.txt").read_bytes())
    except (OSError, ValueError):
        result["complete"] = False
    write(directory / "audit.json", result)
    return result["complete"]


def finalize():
    root, public, preparation = context()
    arms = list((root / "outputs").iterdir())
    sweep_success = all(os.environ.get(name) == "success" for name in (
        "RPC_PRIVATE_SWEEP_OUTCOME", "RPC_PRIVATE_TEARDOWN_OUTCOME", "RPC_PRIVATE_CPU_RESTORE_OUTCOME"))
    good, complete = 0, len(arms) == preparation["expected_arms"] and sweep_success
    for arm in arms:
        try:
            archive_tool.private_directory(arm)
            ok = all(json.loads((arm / phase / "audit.json").read_text())["complete"] is True
                     for phase in ("warm", "main", "node"))
        except (OSError, ValueError, KeyError, TypeError):
            ok = False
        good += int(ok)
        complete = complete and ok
    status = dict(schema=1, quality="PASS" if complete else "FAIL", expected_arms=preparation["expected_arms"],
                  observed_arms=len(arms), valid_arms=good, pooling_eligibility="UNKNOWN",
                  scope="SUPPLEMENTAL_GENERAL_RPC_ONLY", sweep_success=sweep_success,
                  copied_evidence_retained_privately=True, all_required_evidence_copied=complete,
                  source_cleanup_requires="AUDIT_AND_ENCRYPTED_UPLOAD_SUCCESS")
    write(root / "audit-status.json", status)
    original_temporary = os.environ["RUNNER_TEMP"]
    try:
        os.environ["RUNNER_TEMP"] = str(root.parent)
        archive_tool.archive(root, public / "encrypted", COMPONENTS / "recipient.crt")
    finally:
        os.environ["RUNNER_TEMP"] = original_temporary
    write(public / "audit-status.json", status)
    output = os.environ.get("GITHUB_OUTPUT")
    if output:
        with open(output, "a", encoding="utf-8") as stream:
            stream.write("archive_ready=true\n")
    print(json.dumps(status))
    return complete


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    p = commands.add_parser("prepare")
    p.add_argument("--arms", type=int, required=True)
    p.add_argument("--warm-count", type=int, required=True)
    p.add_argument("--main-count", type=int, required=True)
    p = commands.add_parser("verify-source")
    p.add_argument("--source", type=Path, required=True)
    p.add_argument("--ref", required=True)
    p = commands.add_parser("runtime-pin")
    p.add_argument("--image", required=True)
    p.add_argument("--source-head", required=True)
    p.add_argument("--built-current", required=True)
    commands.add_parser("cell")
    p = commands.add_parser("node")
    p.add_argument("--label", required=True)
    p.add_argument("--source-dir", required=True)
    commands.add_parser("finalize")
    p = commands.add_parser("cleanup-sources")
    p.add_argument("--audit-outcome", required=True)
    p.add_argument("--upload-outcome", required=True)
    p.add_argument("--teardown-outcome", required=True)
    p.add_argument("--cpu-outcome", required=True)
    commands.add_parser("preflight")
    commands.add_parser("cpu-apply-attempt")
    commands.add_parser("cpu-restore-state")
    commands.add_parser("teardown")
    p = commands.add_parser("node-begin")
    p.add_argument("--state", type=Path, required=True)
    p.add_argument("--scratch", type=Path, required=True)
    p.add_argument("--name", required=True)
    p.add_argument("--image", required=True)
    for command in ("node-mounted", "node-cid", "node-unmount", "node-done"):
        p = commands.add_parser(command)
        p.add_argument("--state", type=Path, required=True)
        if command == "node-mounted":
            p.add_argument("--mount", type=Path, required=True)
    commands.add_parser("tool-begin").add_argument("--image", required=True)
    args = parser.parse_args()
    os.umask(0o077)
    try:
        if args.command == "cpu-apply-attempt":
            print(cpu_apply_attempt())
            return 0
        if args.command == "cpu-restore-state":
            print(cpu_restore_state())
            return 0
        if args.command == "preflight":
            preflight()
            return 0
        if args.command == "teardown":
            return 0 if teardown() else 1
        if args.command == "node-begin":
            register_container(args.state, "node", args.name, args.image, args.scratch)
            return 0
        if args.command == "node-mounted":
            node_mounted(args.state, args.mount)
            return 0
        if args.command == "node-cid":
            print(owned_cid(args.state, "node"))
            return 0
        if args.command == "node-unmount":
            node_unmount(args.state)
            return 0
        if args.command == "node-done":
            node_done(args.state)
            return 0
        if args.command == "tool-begin":
            root, _, _ = context()
            directory = arm_directory(root, os.environ["LABEL"]) / os.environ["RPC_PRIVATE_AUDIT_PHASE"]
            print(register_container(directory, "tool", os.environ["JB_CONTAINER_NAME"], args.image))
            return 0
        if args.command == "verify-source":
            print(verified_source(args.source, args.ref))
            return 0
        if args.command == "runtime-pin":
            print(runtime_pin(args.image, args.source_head, args.built_current))
            return 0
        if args.command == "prepare":
            prepare(args.arms, args.warm_count, args.main_count)
            return 0
        if args.command == "cleanup-sources":
            return 0 if cleanup_sources(args.audit_outcome, args.upload_outcome, args.teardown_outcome, args.cpu_outcome) else 1
        if args.command == "cell":
            ok = cell()
        elif args.command == "node":
            ok = node(args.label, args.source_dir)
        else:
            ok = finalize()
        if not ok:
            print("RPC_PRIVATE_AUDIT_FAILED", file=sys.stderr)
        return 0 if ok else 1
    except (OSError, ValueError, KeyError, TypeError, archive_tool.ArchiveError, subprocess.SubprocessError):
        print("RPC_PRIVATE_AUDIT_OR_ARCHIVE_FAILED", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
