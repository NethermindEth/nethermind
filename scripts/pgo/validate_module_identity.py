#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

"""Run matching, conflicting and unavailable PDB identity converter regression cases."""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import uuid


def validate(tool, dotnet, trace, references, module_name, pdb_guid, pid, root):
    root = Path(root).resolve()
    if root.exists():
        raise ValueError("regression output directory already exists")
    tool = Path(tool).resolve(strict=True)
    trace = Path(trace).resolve(strict=True)
    references = [Path(path).resolve(strict=True) for path in references]
    matches = [path for path in references if path.name == module_name]
    if len(matches) != 1:
        raise ValueError("regression module must have exactly one explicit reference")
    for path in [tool, trace, *references]:
        if root == path or path.is_relative_to(root):
            raise ValueError("regression output cannot overlap its inputs")
    if trace.stat().st_size > 64 * 1024 * 1024:
        raise ValueError("use a bounded capability fixture, not a full training trace")
    signature = uuid.UUID(pdb_guid).bytes_le
    original_module = matches[0].read_bytes()
    offsets = [offset + 4 for offset in range(len(original_module) - 19)
               if original_module[offset:offset + 20] == b"RSDS" + signature]
    if len(offsets) != 1:
        raise ValueError("fixture must contain one matching PE CodeView record")
    original_trace = trace.read_bytes()
    occurrences = original_trace.count(signature)
    if not occurrences:
        raise ValueError("fixture trace does not contain the declared PDB identity")
    root.mkdir(parents=True)
    wrong_module = bytearray(original_module)
    wrong_module[offsets[0]] ^= 1
    wrong_directory = root / "wrong-reference"
    wrong_directory.mkdir()
    wrong_path = wrong_directory / module_name
    wrong_path.write_bytes(wrong_module)
    results = []
    for name, error in (("matching", None), ("mismatch", "Dll mismatch"),
                        ("unknown", "No verifiable PDB identity")):
        case = root / name
        case.mkdir()
        case_trace = case / "fixture.nettrace"
        case_trace.write_bytes(original_trace.replace(signature, bytes(16)) if name == "unknown" else original_trace)
        output = case / "fixture.mibc"
        command = [str(dotnet), str(tool), "create-mibc", "--trace", str(case_trace),
                   "--output", str(output), "--pid", str(pid)]
        for reference in references:
            command += ["--reference", str(wrong_path if name == "mismatch" and reference == matches[0] else reference)]
        run = subprocess.run(command, capture_output=True, text=True, timeout=120)
        log = run.stdout + run.stderr
        (case / "converter.log").write_text(log, encoding="utf-8")
        passed = (run.returncode == 0 and output.is_file() and output.stat().st_size > 0) if error is None else (
            run.returncode != 0 and error in log and not output.exists())
        results.append({"case": name, "exit_code": run.returncode, "passed": passed})
    report = {"classification": "capability-only regression; never training", "results": results,
              "tool_sha256": hashlib.sha256(tool.read_bytes()).hexdigest(),
              "trace_sha256": hashlib.sha256(original_trace).hexdigest(),
              "module_sha256": hashlib.sha256(original_module).hexdigest(),
              "zeroed_identity_occurrences": occurrences}
    (root / "validation.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    if not all(result["passed"] for result in results):
        raise ValueError("module identity regression failed; inspect validation.json and case logs")
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--tool", type=Path, required=True)
    parser.add_argument("--dotnet", type=Path, required=True)
    parser.add_argument("--trace", type=Path, required=True)
    parser.add_argument("--references", type=Path, required=True, help="File containing one reference path per line")
    parser.add_argument("--module-name", required=True)
    parser.add_argument("--pdb-guid", required=True)
    parser.add_argument("--pid", type=int, required=True)
    parser.add_argument("--root", type=Path, required=True)
    args = parser.parse_args()
    validate(args.tool, args.dotnet, args.trace, args.references.read_text(encoding="utf-8").splitlines(),
             args.module_name, args.pdb_guid, args.pid, args.root)


if __name__ == "__main__":
    main()
