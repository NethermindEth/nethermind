#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

"""Prepare an isolated, pinned offline dotnet-pgo source tree; never modify the shipping runtime."""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess


def verified_patches(lock_path):
    lock_path = Path(lock_path).resolve(strict=True)
    lock = json.loads(lock_path.read_text(encoding="utf-8"))
    patches = []
    for entry in lock["patches"]:
        path = (lock_path.parent / entry["file"]).resolve(strict=True)
        if not path.is_relative_to(lock_path.parent):
            raise ValueError("patch must be inside the tool-lock directory")
        if hashlib.sha256(path.read_bytes()).hexdigest() != entry["sha256"]:
            raise ValueError(f"patch checksum mismatch: {entry['file']}")
        patches.append(path)
    return lock, patches


def prepare(lock_path, destination):
    lock, patches = verified_patches(lock_path)
    destination = Path(destination).absolute()
    if destination.exists():
        raise ValueError(f"source destination already exists: {destination}")
    subprocess.run(["git", "clone", "--depth", "1", "--branch", lock["ref"],
                    lock["repository"], str(destination)], check=True)
    actual = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=destination, text=True).strip()
    if actual != lock["commit"]:
        raise ValueError(f"source identity mismatch: expected {lock['commit']}, got {actual}")
    for patch in patches:
        subprocess.run(["git", "apply", "--check", str(patch)], cwd=destination, check=True)
        subprocess.run(["git", "apply", str(patch)], cwd=destination, check=True)
    changed = subprocess.check_output(["git", "diff", "--name-only"], cwd=destination, text=True).splitlines()
    manifest = {"source": lock, "lock_sha256": hashlib.sha256(Path(lock_path).read_bytes()).hexdigest(),
                "changed_files": {name: hashlib.sha256((destination / name).read_bytes()).hexdigest()
                                  for name in changed}}
    (destination.parent / f"{destination.name}-source-manifest.json").write_text(
        json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    return manifest


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--lock", type=Path, default=Path(__file__).with_name("tool-lock.json"))
    parser.add_argument("--destination", type=Path, required=True)
    args = parser.parse_args()
    prepare(args.lock, args.destination)


if __name__ == "__main__":
    main()
