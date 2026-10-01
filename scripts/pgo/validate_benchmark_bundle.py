#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

"""Validate strict Fusaka training inputs (Python 3.11+).

The caller must obtain the latest run and its complete, unfiltered artifact list
from the authenticated GitHub API. Merge all artifact pages before validation.
Keep the original artifact archives under .artifact-archives/<artifact-id>.zip.
"""
import argparse
from datetime import datetime
import hashlib
import json
from pathlib import Path, PurePosixPath
import re
import zipfile

from validate_collection import validate as validate_replay


def digest(path):
    with Path(path).open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def validate_run(run, source_sha):
    if not re.fullmatch(r"[0-9a-f]{40}", source_sha):
        raise ValueError("source revision must be a full commit SHA")
    if (run.get("status") != "completed" or run.get("conclusion") != "success"
            or run.get("head_sha") != source_sha
            or run.get("path") != ".github/workflows/collect-pgo-profile.yml"
            or run.get("event") != "workflow_dispatch"
            or run.get("head_repository", {}).get("full_name") != "NethermindEth/nethermind"
            or run.get("repository", {}).get("full_name") != "NethermindEth/nethermind"
            or run.get("repository", {}).get("id") != 101194285
            or run.get("head_repository", {}).get("id") != 101194285
            or not isinstance(run.get("id"), int) or run["id"] < 1
            or not isinstance(run.get("run_attempt"), int) or run["run_attempt"] < 1):
        raise ValueError("training run identity or completion does not match")
    return f"{run['id']}-{run['run_attempt']}"


def verify_artifacts(root, run, suffix):
    root = Path(root).resolve(strict=True)
    metadata = json.loads((root / "artifact-api.json").read_text(encoding="utf-8"))
    if type(metadata.get("total_count")) is not int or metadata["total_count"] != len(metadata["artifacts"]):
        raise ValueError("artifact API inventory must be complete and unfiltered")
    names = (f"pgo-collection-{suffix}", f"pgo-app-references-{suffix}",
             "nethermind-pgo-profile", "nethermind-pgo-raw-data")
    identities = {}
    for name in names:
        candidates = [item for item in metadata["artifacts"] if item["name"] == name]
        if len(candidates) != 1:
            raise ValueError("required artifact identity is missing or ambiguous")
        artifact = candidates[0]
        identity = artifact["workflow_run"]
        if (identity["id"] != run["id"] or identity["head_sha"] != run["head_sha"]
                or identity["repository_id"] != run["repository"]["id"]
                or identity["head_repository_id"] != run["head_repository"]["id"]
                or artifact["expired"]
                or datetime.fromisoformat(artifact["created_at"]) < datetime.fromisoformat(run["run_started_at"])
                or datetime.fromisoformat(artifact["created_at"]) > datetime.fromisoformat(run["updated_at"])
                or type(artifact["id"]) is not int or artifact["id"] < 1
                or re.fullmatch(r"sha256:[0-9a-f]{64}", artifact["digest"]) is None):
            raise ValueError("artifact is not from the declared run attempt")
        archive = root / ".artifact-archives" / f"{artifact['id']}.zip"
        if digest(archive) != artifact["digest"].removeprefix("sha256:"):
            raise ValueError("artifact archive does not match the GitHub digest")
        checked = set()
        seen = set()
        with zipfile.ZipFile(archive) as bundle:
            for item in bundle.infolist():
                name_in_archive = item.orig_filename
                relative = PurePosixPath(name_in_archive)
                if (relative.is_absolute() or ".." in relative.parts or "\\" in name_in_archive
                        or "\0" in name_in_archive or name_in_archive in seen):
                    raise ValueError("artifact archive contains an unsafe or duplicate path")
                seen.add(name_in_archive)
                if name_in_archive.endswith("/"):
                    continue
                path = (root / name / relative).resolve()
                if not path.is_relative_to(root / name) or not path.is_file():
                    raise ValueError("artifact file is missing or escapes its directory")
                with bundle.open(item) as stream:
                    expected = hashlib.file_digest(stream, "sha256").hexdigest()
                if digest(path) != expected:
                    raise ValueError("downloaded file does not match the authenticated artifact")
                checked.add(name_in_archive)
        actual = {path.relative_to(root / name).as_posix() for path in (root / name).rglob("*") if path.is_file()}
        if not checked or actual != checked:
            raise ValueError("extracted artifact inventory does not match its archive")
        identities[name] = {"id": artifact["id"], "digest": artifact["digest"]}
    return identities


def verify_references(root, manifest):
    root = Path(root).resolve(strict=True)
    verified = {}
    for item in manifest["files"]:
        relative = PurePosixPath(item["path"])
        if (relative.is_absolute() or ".." in relative.parts
                or "\\" in item["path"] or not relative.parts
                or relative.parts[0] not in ("app", "shared")
                or relative.suffix != ".dll"):
            raise ValueError("reference path is outside the DLL allowlist")
        path = (root / relative).resolve(strict=True)
        if not path.is_relative_to(root) or not path.is_file():
            raise ValueError("reference escapes the bundle")
        if (path.stat().st_size != item["bytes"] or digest(path) != item["sha256"]
                or item["path"] in verified):
            raise ValueError("reference hash, size or uniqueness check failed")
        verified[item["path"]] = item["sha256"]
    actual = {path.relative_to(root).as_posix() for path in root.rglob("*") if path.is_file() and path.suffix.lower() == ".dll"}
    if actual != set(verified) or not any(name.startswith("app/") for name in verified):
        raise ValueError("reference inventory is incomplete")
    return verified


def validate_bundle(root, run, source_sha):
    suffix = validate_run(run, source_sha)
    root = Path(root)
    artifacts = verify_artifacts(root, run, suffix)
    collection = root / f"pgo-collection-{suffix}"
    manifest_path = collection / "manifest.json"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    window = manifest["replay_window"]
    if (PurePosixPath(manifest["config_source"]).name != "github-action-mainnet-fusaka-flat.yaml"
            or PurePosixPath(manifest["snapshot_source"]).name != "nethermind-flat-25490000"
            or "fusaka-payloads" not in PurePosixPath(manifest["payloads"]).parts
            or manifest["amount"] != 1000 or window["amount"] != 1000
            or window["warmup"] != 11 or window["snapshot_number"] != 25490000
            or window["training_first_number"] != 25490001
            or window["training_last_number"] != 25491000):
        raise ValueError("training bundle is not the required Fusaka Flat window")
    if ([header["index"] for header in window["headers"]] != list(range(1011))
            or [header["number"] for header in window["headers"]] != list(range(25489990, 25491001))):
        raise ValueError("training headers do not match the declared block window")
    replay = validate_replay(collection / "expb.log", 1000, 0, manifest_path)
    recorded = json.loads((collection / "execution-validation.json").read_text(encoding="utf-8"))
    if replay["status"] != "valid" or json.loads(json.dumps(replay)) != recorded:
        raise ValueError("independent strict replay validation failed")
    references = root / f"pgo-app-references-{suffix}"
    reference_manifest = json.loads((references / "manifest.json").read_text(encoding="utf-8"))
    if (reference_manifest["image"] != manifest["image"]
            or re.fullmatch(r"nethermindeth/nethermind@sha256:[0-9a-f]{64}", manifest["image"]) is None):
        raise ValueError("collected image identity does not match")
    files = verify_references(references, reference_manifest)
    profile = root / "nethermind-pgo-profile" / "nethermind.mibc"
    trace = root / "nethermind-pgo-raw-data" / "nethermind-1.nettrace"
    if not profile.is_file() or not trace.is_file() or not profile.stat().st_size or not trace.stat().st_size:
        raise ValueError("training profile or raw trace is missing")
    return {"source_sha": source_sha, "run_id": run["id"], "run_attempt": run["run_attempt"],
            "image": manifest["image"], "engine_api_results": replay["engine_api_results"],
            "measured_payloads": 1000, "reference_files": len(files),
            "profile_sha256": digest(profile), "trace_sha256": digest(trace),
            "reference_manifest_sha256": digest(references / "manifest.json"), "artifacts": artifacts}


def compare_rebuilt(references, manifest, rebuilt):
    expected = verify_references(references, manifest)
    expected = {name.removeprefix("app/"): sha for name, sha in expected.items() if name.startswith("app/")}
    rebuilt = Path(rebuilt)
    actual = {path.relative_to(rebuilt).as_posix(): digest(path) for path in rebuilt.rglob("*")
              if path.is_file() and path.suffix.lower() == ".dll"}
    if not actual or actual != expected:
        raise ValueError("rebuilt application DLLs do not match collected inputs")
    return {"matched_application_dlls": len(actual)}


def validate_selection(full, selected, method):
    matches = [item for item in full["Methods"] if item["Method"] == method]
    if len(matches) != 1 or selected["Methods"] != matches:
        raise ValueError("selected profile does not retain exactly the requested full method record")
    counts = [item["Data"] for item in matches[0].get("InstrumentationData", [])
              if item["InstrumentationKind"] in ("EdgeIntCount", "EdgeLongCount", "BasicBlockIntCount", "BasicBlockLongCount")]
    if not counts or not any(value > 0 for value in counts):
        raise ValueError("selected method has no positive instrumentation counts")
    return {"method": method, "instrumentation_records": len(matches[0]["InstrumentationData"])}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, required=True)
    parser.add_argument("--run", type=Path, required=True)
    parser.add_argument("--source-sha", required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    report = validate_bundle(args.root, json.loads(args.run.read_text(encoding="utf-8-sig")), args.source_sha)
    args.output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
