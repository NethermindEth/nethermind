#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

"""Inventory app and framework references extracted from the collection image."""
import argparse
import hashlib
import json
from pathlib import Path


def inventory(root, image):
    root = Path(root).resolve(strict=True)
    app = root / "app"
    shared = root / "shared"
    if not (app / "nethermind.runtimeconfig.json").is_file():
        raise ValueError("missing application runtimeconfig")
    frameworks = shared / "Microsoft.NETCore.App"
    if not frameworks.is_dir() or not list(frameworks.glob("*/System.Private.CoreLib.dll")):
        raise ValueError("missing matching framework references")
    files = []
    selected = {}
    references = []
    for directory, role in ((app, "application"), (shared, "framework")):
        for path in sorted(directory.rglob("*.dll")):
            if "\n" in str(path) or "\r" in str(path):
                raise ValueError("reference paths cannot contain newlines")
            digest = hashlib.sha256(path.read_bytes()).hexdigest()
            key = path.name.casefold()
            if key in selected and selected[key] != digest:
                raise ValueError(f"ambiguous reference filename: {path.name}")
            files.append({"path": path.relative_to(root).as_posix(), "role": role,
                          "bytes": path.stat().st_size, "sha256": digest})
            if key not in selected:
                references.append(str(path))
                selected[key] = digest
    if not any(item["role"] == "application" for item in files):
        raise ValueError("missing application references")
    report = {"schema_version": 1, "image": image, "files": files,
              "reference_count": len(references), "trace_identity_verified": False}
    return report, references


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, required=True)
    parser.add_argument("--image", required=True)
    args = parser.parse_args()
    report, references = inventory(args.root, args.image)
    (args.root / "manifest.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    (args.root / "references.txt").write_text("\n".join(references) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
