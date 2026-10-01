#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

"""Download and validate a strict training bundle through authenticated gh API calls."""
import argparse
import json
from pathlib import Path, PurePosixPath
import re
import shutil
import stat
import subprocess
import zipfile

from validate_benchmark_bundle import digest, validate_bundle, validate_run


REPOSITORY = "repos/NethermindEth/nethermind"


def api(endpoint, destination=None):
    command = ["gh", "api", f"{REPOSITORY}/{endpoint}"]
    if destination is None:
        result = subprocess.run(command, stdout=subprocess.PIPE, stderr=subprocess.PIPE, check=False)
        if result.returncode:
            raise RuntimeError("authenticated GitHub metadata request failed")
        return json.loads(result.stdout)
    with destination.open("xb") as output:
        result = subprocess.run(command, stdout=output, stderr=subprocess.PIPE, check=False)
    if result.returncode:
        raise RuntimeError("authenticated GitHub artifact download failed")


def list_artifacts(run_id, request=api):
    artifacts = []
    count = None
    page = 1
    while True:
        response = request(f"actions/runs/{run_id}/artifacts?per_page=100&page={page}")
        total = response.get("total_count")
        items = response.get("artifacts")
        if type(total) is not int or total < 0 or not isinstance(items, list):
            raise ValueError("invalid artifact API inventory")
        if count is None:
            count = total
        if total != count or len(items) > 100:
            raise ValueError("artifact API inventory changed during pagination")
        artifacts.extend(items)
        if len(artifacts) >= count:
            break
        if not items:
            raise ValueError("artifact API inventory is incomplete")
        page += 1
    if len(artifacts) != count or len({item["id"] for item in artifacts}) != count:
        raise ValueError("artifact API inventory has duplicate or excess entries")
    return {"total_count": count, "artifacts": artifacts}


def extract_archive(archive, destination):
    """Check all members before writing; extraction never follows archive symlinks."""
    destination.mkdir(exist_ok=False)
    root = destination.resolve(strict=True)
    with zipfile.ZipFile(archive) as bundle:
        entries = []
        seen = set()
        for item in bundle.infolist():
            name = item.orig_filename
            relative = PurePosixPath(name)
            mode = item.external_attr >> 16
            if (not name or relative.is_absolute() or ".." in relative.parts
                    or "\\" in name or "\0" in name or ":" in name
                    or stat.S_ISLNK(mode)
                    or stat.S_IFMT(mode) not in (0, stat.S_IFREG, stat.S_IFDIR)):
                raise ValueError("artifact archive contains an unsafe or duplicate path")
            path = (root / relative).resolve()
            if not path.is_relative_to(root) or path == root or path in seen:
                raise ValueError("artifact archive contains an unsafe or duplicate path")
            seen.add(path)
            entries.append((item, path, name.endswith("/")))
        for item, path, directory in entries:
            if directory:
                path.mkdir(parents=True, exist_ok=True)
            else:
                path.parent.mkdir(parents=True, exist_ok=True)
                with bundle.open(item) as source, path.open("xb") as output:
                    shutil.copyfileobj(source, output)


def download_bundle(root, run_id, source_sha, request=api):
    if type(run_id) is not int or run_id < 1:
        raise ValueError("training run ID must be positive")
    run = request(f"actions/runs/{run_id}")
    suffix = validate_run(run, source_sha)
    if run["id"] != run_id:
        raise ValueError("GitHub returned a different training run")
    metadata = list_artifacts(run_id, request)
    names = (f"pgo-collection-{suffix}", f"pgo-app-references-{suffix}",
             "nethermind-pgo-profile", "nethermind-pgo-raw-data")
    selected = []
    for name in names:
        matches = [item for item in metadata["artifacts"] if item["name"] == name]
        if len(matches) != 1:
            raise ValueError("required artifact identity is missing or ambiguous")
        artifact = matches[0]
        if (type(artifact.get("id")) is not int or artifact["id"] < 1
                or re.fullmatch(r"sha256:[0-9a-f]{64}", artifact.get("digest", "")) is None):
            raise ValueError("artifact has no usable authenticated digest or ID")
        selected.append(artifact)
    root.mkdir(parents=True, exist_ok=False)
    (root / "run-api.json").write_text(json.dumps(run, indent=2) + "\n", encoding="utf-8")
    (root / "artifact-api.json").write_text(json.dumps(metadata, indent=2) + "\n", encoding="utf-8")
    archives = root / ".artifact-archives"
    archives.mkdir()
    for artifact in selected:
        archive = archives / f"{artifact['id']}.zip"
        request(f"actions/artifacts/{artifact['id']}/zip", archive)
        if "sha256:" + digest(archive) != artifact["digest"]:
            raise ValueError("artifact archive does not match the GitHub digest")
        extract_archive(archive, root / artifact["name"])
    latest = request(f"actions/runs/{run_id}")
    validate_run(latest, source_sha)
    if any(latest.get(key) != run.get(key) for key in ("id", "run_attempt", "run_started_at", "updated_at")):
        raise ValueError("training run changed during artifact download")
    report = validate_bundle(root, run, source_sha)
    (root / "bundle-validation.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, required=True)
    parser.add_argument("--run-id", type=int, required=True)
    parser.add_argument("--source-sha", required=True)
    args = parser.parse_args()
    report = download_bundle(args.root, args.run_id, args.source_sha)
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()
