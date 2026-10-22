#!/usr/bin/env python3
import argparse
import base64
import gzip
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import tempfile
import subprocess
import zipfile

REPOSITORY = "eez-association/eez-rollup0"
PROFILES = {"rollup0": REPOSITORY, "rollupx": "eez-association/eez-rollupX"}
REVIEWED_STREAMING_REVISION = "0b31df7edbe3e062fffdd18ab31d5911da3b51f3"
REVIEWED_STREAMING_REPLAYS = (REVIEWED_STREAMING_REVISION, "21e0805628f4bd80bcf41c9d2b8f58c179984072", "32549bff918f5d9ac14ed8a78eace22e5c114d4c", "30e6a1fc319d1bf02a4251dbbee240cefcf9310c")
BINARIES = ("eez-composer", "eez-follower", "eez-proof-signer", "eez-genesis-state-root", "eez-genesis-block-hash")


def api(path, repository=REPOSITORY):
    if repository not in PROFILES.values():
        raise ValueError("unsupported upstream repository")
    value = subprocess.check_output(["gh", "api", "repos/" + repository + "/" + path], timeout=60)
    if len(value) > 8 * 1024 * 1024:
        raise ValueError("upstream metadata exceeds the limit")
    return json.loads(value)


def sha(value):
    if not isinstance(value, str) or not re.fullmatch(r"[0-9a-f]{40}", value):
        raise ValueError("invalid source revision")
    return value


def resolve(args):
    profile = getattr(args, "profile", "rollup0")
    repository = PROFILES.get(profile)
    if repository is None:
        raise ValueError("unsupported upstream profile")

    def metadata(path):
        return api(path) if repository == REPOSITORY else api(path, repository)

    head = sha(metadata("commits/main")["sha"])
    source = sha(args.revision) if args.revision else head
    reviewed_replay = getattr(args, "streaming", False) and repository == REPOSITORY and source in REVIEWED_STREAMING_REPLAYS
    if source != head and not reviewed_replay:
        comparison = metadata("compare/" + source + "..." + head)
        if comparison["merge_base_commit"]["sha"] != source:
            raise ValueError("replay revision must belong to public upstream main history")
    streaming = None
    if getattr(args, "streaming", False):
        if repository != REPOSITORY:
            raise ValueError("live streaming requires the Rollup0 source")
        streaming = streaming_source(source)
        origin = "reviewed-pr206-replay" if reviewed_replay else "public-main"
        if streaming is None:
            if args.revision:
                raise ValueError("selected replay revision has no streaming composer")
            pull = metadata("pulls/206")
            if pull["merged"] or pull["head"]["sha"] != REVIEWED_STREAMING_REVISION or pull["head"]["repo"]["full_name"] != REPOSITORY:
                raise ValueError("streaming composer changed; review the published source before running it")
            source = REVIEWED_STREAMING_REVISION
            streaming = streaming_source(source)
            origin = "reviewed-pr206-before-merge"
        if streaming is None:
            raise ValueError("selected source has no streaming composer")
        streaming["source"] = origin
    entries = metadata("git/trees/" + source)["tree"]
    core = [entry for entry in entries if entry["path"] == "eez-core-protocol" and entry["mode"] == "160000"]
    if len(core) != 1:
        raise ValueError("missing unique Core gitlink")
    manifest = {"repository": repository, "profile": profile, "revision": source,
        "core_revision": sha(core[0]["sha"]), "main_at_resolution": head}
    if streaming is not None:
        manifest["streaming"] = streaming
    args.output.write_text(json.dumps(manifest, indent=2) + "\n")
    with Path(os.environ["GITHUB_OUTPUT"]).open("a") as output:
        output.write("revision=" + source + "\n")


def source_blob(entry):
    blob = sha(entry["sha"])
    content = api("git/blobs/" + blob)
    if content["sha"] != blob or content["encoding"] != "base64" or not 0 < content["size"] <= 1024 * 1024:
        raise ValueError("invalid bounded source file")
    data = base64.b64decode("".join(content["content"].split()), validate=True)
    actual = hashlib.sha1(b"blob " + str(len(data)).encode() + b"\0" + data).hexdigest()
    if len(data) != content["size"] or actual != blob:
        raise ValueError("source differs from selected git blob")
    return data.decode("utf-8")


def streaming_source(revision):
    tree = api("git/trees/" + sha(revision) + "?recursive=1")
    if tree.get("truncated"):
        raise ValueError("upstream source tree is incomplete")
    files = {entry["path"]: entry for entry in tree["tree"] if entry["type"] == "blob"}
    client_path = "crates/eez-prover-client/src/lib.rs"
    execution_path = "crates/eez-evm/src/lib.rs"
    if client_path not in files:
        return None
    client = source_blob(files[client_path])
    if "StreamingProverClient" not in client or ".prove_stream(" not in client:
        return None
    execution = source_blob(files[execution_path])
    factories = re.findall(r"type BlockExecutorFactory\s*=\s*(\w+)\s*<", execution)
    if len(factories) != 1 or factories[0] not in ("EthBlockExecutorFactory", "EezBlockExecutorFactory"):
        raise ValueError("upstream execution factory changed; review consensus-call behavior")
    return {"wire": "prove.v2", "profile": "rollup0-conditional",
        "skip_consensus_layer_calls": factories[0] == "EezBlockExecutorFactory",
        "execution_factory": factories[0],
        "source_blobs": {path: sha(files[path]["sha"]) for path in (client_path, execution_path)}}


def runtime(args):
    manifest = json.loads(args.manifest.read_text())
    revision = sha(manifest["revision"])
    if manifest["repository"] == PROFILES["rollupx"] and manifest.get("profile") == "rollupx":
        args.output.mkdir(parents=True, exist_ok=True)
        if not native_runtime(args, manifest):
            manifest["runtime"] = {"source": "build-required"}
            args.manifest.write_text(json.dumps(manifest, indent=2) + "\n")
        return
    if manifest["repository"] != REPOSITORY:
        raise ValueError("unexpected upstream repository")
    runs = api("actions/runs?event=push&branch=main&head_sha=" + revision + "&status=success&per_page=100")["workflow_runs"]
    artifact = None
    for run in runs:
        if run["head_sha"] != revision or run["event"] != "push" or run["head_branch"] != "main" or run["conclusion"] != "success" or run["repository"]["full_name"] != REPOSITORY:
            raise ValueError("invalid upstream run provenance")
        candidates = api("actions/runs/" + str(run["id"]) + "/artifacts?per_page=100")["artifacts"]
        for candidate in candidates:
            if candidate["name"] == "e2e-build" and not candidate["expired"]:
                if candidate["workflow_run"]["head_sha"] != revision or candidate["workflow_run"]["id"] != run["id"]:
                    raise ValueError("artifact source differs from selected revision")
                artifact = candidate
                break
        if artifact:
            break
    args.output.mkdir(parents=True, exist_ok=True)
    if artifact is None:
        manifest["runtime"] = {"source": "build-required"}
        args.manifest.write_text(json.dumps(manifest, indent=2) + "\n")
        return
    expected = artifact.get("digest", "")
    if not re.fullmatch(r"sha256:[0-9a-f]{64}", expected) or artifact["size_in_bytes"] > 1024 * 1024 * 1024:
        raise ValueError("artifact has no valid bounded digest")
    archive = args.output / "runtime.zip"
    command = ["gh", "api", "repos/" + REPOSITORY + "/actions/artifacts/" + str(artifact["id"]) + "/zip"]
    with archive.open("wb") as stream:
        download = subprocess.run(command, stdout=stream, stderr=subprocess.PIPE, check=False, timeout=600)
    if download.returncode != 0:
        unavailable = re.search(rb"HTTP\s+(401|403|404|410)", download.stderr)
        if unavailable is None:
            raise subprocess.CalledProcessError(download.returncode, command)
        archive.unlink()
        manifest["runtime"] = {"source": "build-required", "artifact_id": artifact["id"], "artifact_unavailable": int(unavailable[1])}
        args.manifest.write_text(json.dumps(manifest, indent=2) + "\n")
        return
    if archive.stat().st_size > 1024 * 1024 * 1024:
        raise ValueError("downloaded runtime exceeds limit")
    with archive.open("rb") as stream:
        digest = "sha256:" + hashlib.file_digest(stream, "sha256").hexdigest()
    if digest != expected:
        raise ValueError("upstream runtime digest mismatch")
    hashes = {}
    with zipfile.ZipFile(archive) as bundle:
        for name in BINARIES:
            candidates = [entry for entry in bundle.infolist() if entry.filename == name]
            if len(candidates) != 1 or not 0 < candidates[0].file_size <= 256 * 1024 * 1024:
                raise ValueError("missing, duplicated or oversized runtime binary: " + name)
            with bundle.open(candidates[0]) as source, (args.output / name).open("wb") as target:
                while chunk := source.read(1024 * 1024):
                    target.write(chunk)
            (args.output / name).chmod(0o555)
            with (args.output / name).open("rb") as stream:
                hashes[name] = hashlib.file_digest(stream, "sha256").hexdigest()
    archive.unlink()
    manifest["runtime"] = {"source": "artifact", "artifact_id": artifact["id"], "run_id": artifact["workflow_run"]["id"], "digest": digest, "binaries": hashes}
    args.manifest.write_text(json.dumps(manifest, indent=2) + "\n")


def native_runtime(args, manifest):
    repository, revision = manifest["repository"], sha(manifest["revision"])
    runs = api("actions/runs?event=push&branch=main&head_sha=" + revision + "&status=success&per_page=100", repository)["workflow_runs"]
    image_repository = "ghcr.io/" + repository.lower() + "-ci-runtime"
    for run in runs:
        if (run["head_sha"], run["event"], run["head_branch"], run["conclusion"], run["repository"]["full_name"]) != (revision, "push", "main", "success", repository):
            raise ValueError("invalid upstream runtime provenance")
        if run["path"] != ".github/workflows/ci.yml":
            continue
        attempt = int(run["run_attempt"])
        if attempt <= 0 or not isinstance(run["id"], int) or run["id"] <= 0:
            raise ValueError("invalid upstream runtime run identity")
        for attempt in range(attempt, 0, -1):
            tag = image_repository + ":sha-" + revision + "-" + str(run["id"]) + "-" + str(attempt)
            pull = subprocess.run(["docker", "pull", "--platform", "linux/amd64", tag], capture_output=True, text=True, timeout=600, check=False)
            if pull.returncode == 0:
                break
        else:
            print("Upstream runtime payload unavailable; build the selected source")
            continue
        digests = json.loads(subprocess.check_output(["docker", "image", "inspect", "--format", "{{json .RepoDigests}}", tag], text=True, timeout=30))
        references = set(value for value in digests if re.fullmatch(re.escape(image_repository) + r"@sha256:[0-9a-f]{64}", value))
        if len(references) != 1:
            raise ValueError("missing or ambiguous immutable upstream runtime")
        reference = references.pop()
        with tempfile.TemporaryDirectory(prefix="native-runtime-", dir=args.output.parent) as directory:
            temporary = Path(directory)
            payload = temporary / "payload"
            environment = {**os.environ, "GITHUB_SHA": revision, "GITHUB_REPOSITORY": repository,
                "GITHUB_ENV": str(temporary / "environment"), "GITHUB_PATH": str(temporary / "path"),
                "EEZ_EXPECT_PUBLISHED": "true", "RUNNER_TEMP": str(temporary)}
            subprocess.run(["bash", str(args.checkout / "scripts/ci/ci-payload.sh"), "fetch", "runtime", reference, str(payload)],
                env=environment, check=True, timeout=600)
            subprocess.run(["bash", str(args.checkout / "scripts/ci/prepare-runtime-artifacts.sh"), str(payload)],
                env=environment, check=True, timeout=600)
            paths = (temporary / "path").read_text().splitlines()
            if len(paths) != 1:
                raise ValueError("upstream runtime export path changed")
            binaries = Path(paths[0])
            for name in BINARIES + ("e2e-causal-verify", "verify_blob_evidence"):
                shutil.copy2(binaries / name, args.output / name)
            for name in ("rustc-version.txt", "cargo-version.txt"):
                shutil.copy2(binaries.parent / name, args.output / name)
            record_build(args)
            recorded = json.loads(args.manifest.read_text())
            recorded["runtime"].update(source="upstream-payload", run_id=run["id"], run_attempt=attempt, reference=reference)
            args.manifest.write_text(json.dumps(recorded, indent=2) + "\n")
        return True
    return False


def record_build(args):
    manifest = json.loads(args.manifest.read_text())
    for path, expected in ((args.checkout, manifest["revision"]), (args.checkout / "eez-core-protocol", manifest["core_revision"])):
        actual = subprocess.check_output(["git", "-C", str(path), "rev-parse", "HEAD"], text=True, timeout=30).strip()
        if actual != sha(expected):
            raise ValueError("runtime build source differs from selected revision")
    hashes = {}
    names = BINARIES + (("e2e-causal-verify", "verify_blob_evidence") if manifest.get("profile") == "rollupx" else ())
    for name in names:
        path = args.output / name
        if not path.is_file() or not os.access(path, os.X_OK) or not 0 < path.stat().st_size <= 1024 * 1024 * 1024:
            raise ValueError("missing or invalid built runtime: " + name)
        with path.open("rb") as stream:
            hashes[name] = hashlib.file_digest(stream, "sha256").hexdigest()
    versions = {}
    for tool in ("rustc", "cargo"):
        if manifest.get("profile") == "rollupx":
            path = args.output / (tool + "-version.txt")
            if not 0 < path.stat().st_size <= 1024:
                raise ValueError("invalid build tool version")
            versions[tool] = path.read_text().splitlines()[0].strip()
            if not versions[tool].startswith(tool + " ") or "\n" in versions[tool]:
                raise ValueError("invalid build tool version")
        else:
            versions[tool] = subprocess.check_output([tool, "--version"], text=True, timeout=30).strip()
    manifest["runtime"] = {"source": "source-build", "binaries": hashes, **versions}
    args.manifest.write_text(json.dumps(manifest, indent=2) + "\n")


def validate_vectors(document):
    cases = document.get("cases", [])
    if document.get("format") != "eez-prove-v2-transcript-1" or not 0 < len(cases) <= 256:
        raise ValueError("unsupported or empty vector suite")
    names = set()
    allowed = {"open", "send", "receive", "half_close", "rpc_error", "eof"}
    for case in cases:
        name = case.get("name")
        steps = case.get("steps", [])
        actions = {step.get("action") for step in steps}
        if not isinstance(name, str) or not name or name in names or not 4 <= len(steps) <= 4096 or not {"open", "send", "receive", "eof"} <= actions:
            raise ValueError("incomplete vector case")
        names.add(name)
        for step in steps:
            if step.get("action") not in allowed or not isinstance(step.get("stream"), str) or not step["stream"]:
                raise ValueError("unsupported vector step")
            if step["action"] in ("send", "receive"):
                bytes.fromhex(step["protobuf_hex"])


def vectors(args):
    names = ("rollup0-transcripts.json", "rollup0-limits-transcripts.json")
    prefix = "crates/eez-control-rpc/conformance/"
    if getattr(args, "profile", "rollup0") != "rollup0":
        raise ValueError("this vector loader requires the Rollup0 profile")
    args.directory.mkdir(parents=True, exist_ok=False)
    resolve(args)
    manifest = json.loads(args.output.read_text())
    tree = api("git/trees/" + manifest["revision"] + "?recursive=1")
    if tree.get("truncated"):
        raise ValueError("upstream vector tree is incomplete")
    available = {entry["path"]: entry["sha"] for entry in tree["tree"] if entry["type"] == "blob"}
    found = [name for name in names if prefix + name in available]
    snapshot = Path(__file__).resolve().parents[3] / "src/Nethermind/Nethermind.Eez.Test/Fixtures/streaming-conformance"
    if len(found) == len(names):
        source = {"source": "public-main", "revision": manifest["revision"], "core_revision": manifest["core_revision"], "files": {}}
    elif found or api("pulls/205")["merged"]:
        raise ValueError("selected upstream revision is missing the vector suite")
    else:
        source = json.loads((snapshot / "provenance.json").read_text())
        source["source"] = "reviewed-pr-snapshot-before-merge"
        print("PR205 vectors are not on public main yet; running the explicit reviewed snapshot " + sha(source["revision"]))
    for name in names:
        if found:
            blob = sha(available[prefix + name])
            content = api("git/blobs/" + blob)
            if content["sha"] != blob or content["encoding"] != "base64" or not 0 < content["size"] <= 8 * 1024 * 1024:
                raise ValueError("invalid bounded vector file")
            data = base64.b64decode("".join(content["content"].split()), validate=True)
            if len(data) != content["size"]:
                raise ValueError("vector size differs from selected source")
        else:
            data = (snapshot / name).read_bytes()
        if not 0 < len(data) <= 8 * 1024 * 1024:
            raise ValueError("vector data exceeds its limit")
        digest = hashlib.sha256(data).hexdigest()
        if not found and digest != source["files"][name]:
            raise ValueError("reviewed vector snapshot digest mismatch")
        document = json.loads(data)
        validate_vectors(document)
        (args.directory / name).write_bytes(data)
        source["files"][name] = digest
    manifest["vectors"] = source
    args.output.write_text(json.dumps(manifest, indent=2) + "\n")



def rollupx_blob(entry):
    blob = sha(entry["sha"])
    content = api("git/blobs/" + blob, PROFILES["rollupx"])
    if content["sha"] != blob or content["encoding"] != "base64" or not 0 < content["size"] <= 8 * 1024 * 1024:
        raise ValueError("invalid bounded RollupX data file")
    data = base64.b64decode("".join(content["content"].split()), validate=True)
    actual = hashlib.sha1(b"blob " + str(len(data)).encode() + b"\0" + data).hexdigest()
    if len(data) != content["size"] or actual != blob:
        raise ValueError("RollupX data differs from the selected git blob")
    return data


def rollupx_execution(args):
    args.directory.mkdir(parents=True, exist_ok=False)
    resolve(args)
    manifest = json.loads(args.output.read_text())
    repository = PROFILES["rollupx"]
    if manifest["repository"] != repository:
        raise ValueError("RollupX execution requires the RollupX source profile")
    snapshot = Path(__file__).resolve().parents[3] / "src/Nethermind/Nethermind.Eez.Test/Fixtures/rollupx/recorded-execution"
    reviewed = json.loads((snapshot / "provenance.json").read_text())
    tree = api("git/trees/" + manifest["revision"] + "?recursive=1", repository)
    if tree.get("truncated"):
        raise ValueError("upstream RollupX vector tree is incomplete")
    entries = {entry["path"]: entry for entry in tree["tree"] if entry["type"] == "blob"}
    prefix = "crates/eez-proof-signer/tests/fixtures/"
    vector_prefix = prefix + "streaming-v2/"
    cases = sorted(path[len(vector_prefix):] for path in entries if path.startswith(vector_prefix) and path.endswith(".json") and path != vector_prefix + "manifest.json")
    baseline_names = ("baseline-lookup", "baseline-static")
    if cases:
        if cases != reviewed["cases"]:
            raise ValueError("RollupX case inventory changed; review the execution and streaming coverage mapping")
        source = {"repository": repository, "source": "public-main", "revision": manifest["revision"],
            "core_revision": manifest["core_revision"], "files": {}}
    elif api("pulls/89", repository)["merged"]:
        raise ValueError("selected RollupX source is missing the merged vector suite")
    else:
        if reviewed["repository"] != repository:
            raise ValueError("reviewed RollupX snapshot names another repository")
        source = {key: reviewed[key] for key in ("repository", "revision", "core_revision")}
        sha(source["revision"])
        sha(source["core_revision"])
        source.update(source="reviewed-pr-snapshot-before-merge", files={})
        cases = reviewed["cases"]
        print("PR89 data is not on public main; using the explicit reviewed snapshot " + source["revision"])

    def read(name, upstream_path):
        if source["source"] == "public-main":
            if upstream_path not in entries:
                raise ValueError("selected RollupX source is missing execution input " + upstream_path)
            data = rollupx_blob(entries[upstream_path])
        else:
            with gzip.open(snapshot / (name + ".gz"), "rb") as compressed:
                data = compressed.read(8 * 1024 * 1024 + 1)
            expected = reviewed["files"][name]
            if len(data) != expected["length"] or hashlib.sha256(data).hexdigest() != expected["sha256"]:
                raise ValueError("reviewed RollupX snapshot digest mismatch")
        if not 0 < len(data) <= 8 * 1024 * 1024:
            raise ValueError("RollupX execution input exceeds its limit")
        source["files"][name] = {"length": len(data), "sha256": hashlib.sha256(data).hexdigest()}
        (args.directory / (name + ".gz")).write_bytes(gzip.compress(data, mtime=0))
        return json.loads(data)

    fixtures = set()
    for filename in cases:
        name = filename.removesuffix(".json")
        document = read(filename, vector_prefix + filename)
        fixture = document.get("fixture")
        if document.get("format") != 2 or document.get("case") != name or not isinstance(fixture, str) or not re.fullmatch(r"[a-z0-9][a-z0-9-]{0,127}", fixture):
            raise ValueError("unsupported RollupX execution recording")
        events = document.get("events", [])
        if not 4 <= len(events) <= 4096 or not {"client", "server", "eof"} <= {event.get("direction") for event in events}:
            raise ValueError("incomplete RollupX execution recording")
        if fixture not in fixtures:
            read(fixture + ".genesis.json", prefix + fixture + "/l2-genesis.json")
            read(fixture + ".l1-genesis.json", prefix + fixture + "/l1-genesis.json")
            read(fixture + ".identity.json", prefix + fixture + "/identity.json")
            fixtures.add(fixture)
    source["cases"] = cases
    source["execution_baselines"] = list(baseline_names)
    source["streaming_replay"] = "format2-raw"
    (args.directory / "provenance.json").write_text(json.dumps(source, indent=2) + "\n")
    manifest["execution_vectors"] = source
    args.output.write_text(json.dumps(manifest, indent=2) + "\n")
    print("Selected " + str(len(cases)) + " unchanged streaming cases and 2 execution baselines")


def main():
    parser = argparse.ArgumentParser()
    modes = parser.add_subparsers(dest="mode", required=True)
    resolver = modes.add_parser("resolve")
    resolver.add_argument("--revision", default="")
    resolver.add_argument("--streaming", action="store_true")
    resolver.add_argument("--profile", choices=PROFILES, default="rollup0")
    resolver.add_argument("--output", type=Path, required=True)
    downloader = modes.add_parser("runtime")
    downloader.add_argument("--manifest", type=Path, required=True)
    downloader.add_argument("--output", type=Path, required=True)
    downloader.add_argument("--checkout", type=Path)
    builder = modes.add_parser("record-build")
    builder.add_argument("--manifest", type=Path, required=True)
    builder.add_argument("--output", type=Path, required=True)
    builder.add_argument("--checkout", type=Path, required=True)
    vector = modes.add_parser("vectors")
    vector.add_argument("--revision", default="")
    vector.add_argument("--directory", type=Path, required=True)
    vector.add_argument("--output", type=Path, required=True)
    execution = modes.add_parser("rollupx-execution")
    execution.set_defaults(profile="rollupx")
    execution.add_argument("--revision", default="")
    execution.add_argument("--directory", type=Path, required=True)
    execution.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    {"resolve": resolve, "runtime": runtime, "record-build": record_build, "vectors": vectors, "rollupx-execution": rollupx_execution}[args.mode](args)


if __name__ == "__main__":
    main()
