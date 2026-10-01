#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

"""Run saved, independently checked call-weight fixtures against an offline converter.

The manifest supplies immutable traces/references, expected named weights and
optional inclusive windows. Fixture preparation and independent stack attribution
must be validated separately; this runner is not a training-acceptance gate.
"""
import argparse
import hashlib
import json
from pathlib import Path
import re
import shutil
import subprocess


def digest(path):
    with Path(path).open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def positive_weights(methods):
    exclusive = {}
    edges = {}
    names = set()
    for method in methods:
        name = method["Method"]
        if name in names:
            raise ValueError("duplicate emitted method")
        names.add(name)
        weight = method.get("ExclusiveWeight", 0)
        if type(weight) is not int or weight < 0:
            raise ValueError("invalid exclusive weight")
        if weight:
            exclusive[name] = weight
        for edge in method.get("CallWeights", []):
            key = name + " -> " + edge["Method"]
            weight = edge["Weight"]
            if key in edges or type(weight) is not int or weight < 0:
                raise ValueError("invalid or duplicate call weight")
            if weight:
                edges[key] = weight
    return exclusive, edges


def run(manifest, tool, output, dotnet="dotnet"):
    manifest = Path(manifest).resolve(strict=True)
    tool = Path(tool).resolve(strict=True)
    output = Path(output).resolve()
    if output.exists():
        raise ValueError("integration output already exists")
    data = json.loads(manifest.read_text(encoding="utf-8"))
    cases = data["cases"]
    if not cases or len({case["name"] for case in cases}) != len(cases):
        raise ValueError("fixture cases must be nonempty and unique")
    inputs = [(manifest, digest(manifest)), (tool, digest(tool))]
    references = []
    for item in data["references"]:
        path = (manifest.parent / item["path"]).resolve(strict=True)
        if path.suffix != ".dll" or digest(path) != item["sha256"]:
            raise ValueError("reference identity does not match")
        references.append(path)
        inputs.append((path, item["sha256"]))
    if not references:
        raise ValueError("matching references are required")
    for case in cases:
        if re.fullmatch(r"[a-z0-9][a-z0-9-]*", case["name"]) is None:
            raise ValueError("unsafe fixture case name")
        path = (manifest.parent / case["trace"]).resolve(strict=True)
        if path.suffix not in (".nettrace", ".etlx") or digest(path) != case["sha256"]:
            raise ValueError("trace identity does not match")
        inputs.append((path, case["sha256"]))
    if any(path == output or path.is_relative_to(output) or output.is_relative_to(path)
           for path, _ in inputs):
        raise ValueError("integration output overlaps its inputs")
    output.mkdir(parents=True)
    reports = []
    for case in cases:
        folder = output / case["name"]
        folder.mkdir()
        source = (manifest.parent / case["trace"]).resolve(strict=True)
        trace = folder / ("fixture" + source.suffix)
        shutil.copyfile(source, trace)
        profile = folder / "profile.mibc"
        command = [dotnet, str(tool), "create-mibc", "--trace", str(trace),
                   "--output", str(profile), "--pid", str(case["pid"]), "--spgo",
                   "--automatic-references", "false"]
        for reference in references:
            command.extend(["--reference", str(reference)])
        if "window" in case:
            before, after = case["window"]
            command.extend(["--exclude-events-before", str(before), "--exclude-events-after", str(after)])
        log = folder / "converter.log"
        with log.open("wb") as stream:
            result = subprocess.run(command, stdout=stream, stderr=subprocess.STDOUT)
        text = log.read_text(encoding="utf-8", errors="replace")
        if case.get("overflow", False):
            if (result.returncode == 0 or profile.exists() or "OverflowException" not in text
                    or "Call weight source:" in text or "Universal CPU sample process:" in text):
                raise ValueError(f"{case['name']}: expected checked call-weight overflow before SPGO without a profile")
        else:
            if result.returncode != 0 or f"Call weight source: {case['source']}" not in text:
                raise ValueError(f"{case['name']}: conversion or selected source mismatch")
            dump = folder / "dump.json"
            with (folder / "dump.log").open("wb") as stream:
                subprocess.run([dotnet, str(tool), "dump", "--input", str(profile), "--output", str(dump)],
                               stdout=stream, stderr=subprocess.STDOUT, check=True)
            exclusive, edges = positive_weights(json.loads(dump.read_text(encoding="utf-8"))["Methods"])
            if exclusive != case["exclusive"] or edges != case["edges"]:
                raise ValueError(f"{case['name']}: named call/exclusive weights differ from the independent fixture")
        if digest(trace) != case["sha256"]:
            raise ValueError("conversion changed the copied input")
        reports.append({"case": case["name"], "exit_code": result.returncode,
                        "trace_sha256": case["sha256"], "validated": True})
        print(f"{case['name']}: validated", flush=True)
    if any(digest(path) != expected for path, expected in inputs):
        raise ValueError("integration changed an original input")
    report = {"fixture_manifest_sha256": digest(manifest), "tool_sha256": digest(tool),
              "cases": reports, "training_or_performance_acceptance": False}
    (output / "verification.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    return report


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", type=Path, required=True)
    parser.add_argument("--tool", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--dotnet", default="dotnet")
    args = parser.parse_args()
    run(args.manifest, args.tool, args.output, args.dotnet)
