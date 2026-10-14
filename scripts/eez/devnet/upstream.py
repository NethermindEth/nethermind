#!/usr/bin/env python3
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import zipfile

REPOSITORY = "eez-association/eez-rollup0"
BINARIES = ("eez-composer", "eez-follower", "eez-proof-signer", "eez-genesis-state-root", "eez-genesis-block-hash")


def api(path):
    value = subprocess.check_output(["gh", "api", "repos/" + REPOSITORY + "/" + path], timeout=60)
    if len(value) > 8 * 1024 * 1024:
        raise ValueError("upstream metadata exceeds the limit")
    return json.loads(value)


def sha(value):
    if not isinstance(value, str) or not re.fullmatch(r"[0-9a-f]{40}", value):
        raise ValueError("invalid source revision")
    return value


def resolve(args):
    head = sha(api("commits/main")["sha"])
    source = sha(args.revision) if args.revision else head
    if source != head:
        comparison = api("compare/" + source + "..." + head)
        if comparison["merge_base_commit"]["sha"] != source:
            raise ValueError("replay revision must belong to public upstream main history")
    entries = api("git/trees/" + source)["tree"]
    core = [entry for entry in entries if entry["path"] == "eez-core-protocol" and entry["mode"] == "160000"]
    if len(core) != 1:
        raise ValueError("missing unique Core gitlink")
    manifest = {"repository": REPOSITORY, "revision": source, "core_revision": sha(core[0]["sha"]), "main_at_resolution": head}
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


def main():
    parser = argparse.ArgumentParser()
    modes = parser.add_subparsers(dest="mode", required=True)
    resolver = modes.add_parser("resolve")
    resolver.add_argument("--revision", default="")
    resolver.add_argument("--output", type=Path, required=True)
    downloader = modes.add_parser("runtime")
    downloader.add_argument("--manifest", type=Path, required=True)
    downloader.add_argument("--output", type=Path, required=True)
    builder = modes.add_parser("record-build")
    builder.add_argument("--manifest", type=Path, required=True)
    builder.add_argument("--output", type=Path, required=True)
    builder.add_argument("--checkout", type=Path, required=True)
    args = parser.parse_args()
    {"resolve": resolve, "runtime": runtime, "record-build": record_build}[args.mode](args)


if __name__ == "__main__":
    main()
