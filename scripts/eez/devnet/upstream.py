#!/usr/bin/env python3
import argparse
import base64
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import zipfile

REPOSITORY = "eez-association/eez-rollup0"
PROFILES = {"rollup0": REPOSITORY, "rollupx": "eez-association/eez-rollupX"}
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
    if source != head:
        comparison = metadata("compare/" + source + "..." + head)
        if comparison["merge_base_commit"]["sha"] != source:
            raise ValueError("replay revision must belong to public upstream main history")
    entries = metadata("git/trees/" + source)["tree"]
    core = [entry for entry in entries if entry["path"] == "eez-core-protocol" and entry["mode"] == "160000"]
    if len(core) != 1:
        raise ValueError("missing unique Core gitlink")
    manifest = {"repository": repository, "profile": profile, "revision": source,
        "core_revision": sha(core[0]["sha"]), "main_at_resolution": head}
    args.output.write_text(json.dumps(manifest, indent=2) + "\n")
    with Path(os.environ["GITHUB_OUTPUT"]).open("a") as output:
        output.write("revision=" + source + "\n")


def runtime(args):
    manifest = json.loads(args.manifest.read_text())
    revision = sha(manifest["revision"])
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


def record_build(args):
    manifest = json.loads(args.manifest.read_text())
    for path, expected in ((args.checkout, manifest["revision"]), (args.checkout / "eez-core-protocol", manifest["core_revision"])):
        actual = subprocess.check_output(["git", "-C", str(path), "rev-parse", "HEAD"], text=True, timeout=30).strip()
        if actual != sha(expected):
            raise ValueError("runtime build source differs from selected revision")
    hashes = {}
    for name in BINARIES:
        path = args.output / name
        if not path.is_file() or not os.access(path, os.X_OK) or not 0 < path.stat().st_size <= 1024 * 1024 * 1024:
            raise ValueError("missing or invalid built runtime: " + name)
        with path.open("rb") as stream:
            hashes[name] = hashlib.file_digest(stream, "sha256").hexdigest()
    manifest["runtime"] = {"source": "source-build", "binaries": hashes,
        "rustc": subprocess.check_output(["rustc", "--version"], text=True, timeout=30).strip(),
        "cargo": subprocess.check_output(["cargo", "--version"], text=True, timeout=30).strip()}
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


def main():
    parser = argparse.ArgumentParser()
    modes = parser.add_subparsers(dest="mode", required=True)
    resolver = modes.add_parser("resolve")
    resolver.add_argument("--revision", default="")
    resolver.add_argument("--profile", choices=PROFILES, default="rollup0")
    resolver.add_argument("--output", type=Path, required=True)
    downloader = modes.add_parser("runtime")
    downloader.add_argument("--manifest", type=Path, required=True)
    downloader.add_argument("--output", type=Path, required=True)
    builder = modes.add_parser("record-build")
    builder.add_argument("--manifest", type=Path, required=True)
    builder.add_argument("--output", type=Path, required=True)
    builder.add_argument("--checkout", type=Path, required=True)
    vector = modes.add_parser("vectors")
    vector.add_argument("--revision", default="")
    vector.add_argument("--directory", type=Path, required=True)
    vector.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    {"resolve": resolve, "runtime": runtime, "record-build": record_build, "vectors": vectors}[args.mode](args)


if __name__ == "__main__":
    main()
