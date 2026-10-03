#!/usr/bin/env python3
"""Export fixed devnet evidence without secrets or complete proof/transaction bytes."""
import argparse
import hashlib
import json
from pathlib import Path
import re


def read_json(path):
    if path.stat().st_size > 20 * 1024 * 1024:
        raise ValueError(f"Evidence file exceeds bound: {path.name}")
    return json.loads(path.read_text())


def digest(path):
    value = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            value.update(chunk)
    return value.hexdigest()


def select(value, fields):
    return {key: value[key] for key in fields if key in value}


def dependency_set(requests):
    return {(item["scheme"], item["dataHash"], item["verificationKey"]) for request in requests for item in request["dependencies"]}


def export(root):
    evidence = {"schemaVersion": 1, "testKind": "functional two-Runner Engine driver", "runs": {}, "manifests": {}}
    for directory in ("driver", "driver-sphincs64-original-deadline", "driver-sphincs64"):
        base = root / directory
        report = base / "report.json"
        if not report.exists():
            continue
        run = {"report": read_json(report), "reportSha256": digest(report), "receipts": {}, "inspections": {}, "artifacts": []}
        for path in sorted(base.iterdir()):
            if re.fullmatch(r"receipts-\d{3}\.json", path.name):
                run["receipts"][path.name] = [[select(receipt, ("transactionHash", "blockHash", "blockNumber", "status", "gasUsed")) for receipt in node] for node in read_json(path)]
            elif re.fullmatch(r"payload-\d{3}-invalid-(proof|commitment)\.json\.inspection\.json", path.name):
                run["inspections"][path.name] = read_json(path)
            elif re.fullmatch(r"(payload-\d{3}(-invalid-(proof|commitment))?|get-payload-\d{3}|forkchoice-\d{3})\.json", path.name):
                record = {"file": path.name, "bytes": path.stat().st_size, "sha256": digest(path)}
                request = read_json(path)
                payload = request.get("params", [{}])[0] if "params" in request else request.get("executionPayload", {})
                if isinstance(payload, dict):
                    record.update(select(payload, ("blockHash", "blockNumber", "recursiveStarkBlockDepsHash")))
                    proof = payload.get("recursiveStarkProof")
                    if isinstance(proof, str) and proof.startswith("0x"):
                        raw = bytes.fromhex(proof[2:])
                        record.update({"proofBytes": len(raw), "proofSha256": hashlib.sha256(raw).hexdigest()})
                run["artifacts"].append(record)
        evidence["runs"][directory] = run
    dependency_sets = {}
    for directory in ("transactions", "sphincs64"):
        path = root / directory / "manifest.json"
        if not path.exists():
            continue
        manifest = read_json(path)
        summary = select(manifest, ("chainId", "sender", "firstNonce", "nextNonce", "genericCompression"))
        summary["sha256"] = digest(path)
        dependencies = dependency_set(manifest["requests"])
        dependency_sets[directory] = dependencies
        summary["distinctDependencies"] = len(dependencies)
        summary["dependencySetSha256"] = hashlib.sha256(json.dumps(sorted(dependencies), separators=(",", ":")).encode()).hexdigest()
        summary["requests"] = []
        for request in manifest["requests"]:
            item = select(request, ("name", "mode", "nonce", "transactionHash", "wrapperBytes", "proofBytes", "expectedReceiptStatus"))
            item["dependencyCount"] = len(dependency_set([request]))
            for field in ("requestFile", "negativeRequestFile"):
                name = request[field]
                if Path(name).name != name or not name.endswith(".json"):
                    raise ValueError("Manifest request path must be a JSON basename")
                file = path.parent / name
                item[field] = {"name": name, "bytes": file.stat().st_size, "sha256": digest(file)}
                wrapper = read_json(file).get("params", [None])[0]
                if isinstance(wrapper, str) and wrapper.startswith("0x"):
                    item[field]["wrapperSha256"] = hashlib.sha256(bytes.fromhex(wrapper[2:])).hexdigest()
            summary["requests"].append(item)
        evidence["manifests"][directory] = summary
    if "transactions" in dependency_sets and "sphincs64" in dependency_sets:
        evidence["fixtureOverlapDependencies"] = len(dependency_sets["transactions"] & dependency_sets["sphincs64"])
    context = root / "sphincs64-test-context.json"
    if context.exists():
        evidence["sphincs64Context"] = read_json(context)
    binary = root / "binary-sha256.txt"
    if binary.exists():
        evidence["binaryHashes"] = {}
        for line in binary.read_text().splitlines():
            sha, name = line.split(maxsplit=1)
            if not re.fullmatch(r"[0-9a-f]{64}", sha):
                raise ValueError("Invalid binary SHA-256")
            evidence["binaryHashes"][Path(name.strip()).name] = sha
    return evidence


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--runtime-root", type=Path, required=True)
    args = parser.parse_args()
    print(json.dumps(export(args.runtime_root), indent=2, sort_keys=True))
