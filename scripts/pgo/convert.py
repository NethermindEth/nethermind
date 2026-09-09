#!/usr/bin/env python3
"""Merge independently validated Fusaka and eth_call profiles from one collection image."""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import subprocess


def sha256(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def check_profile(path, prefix, mode="instrumentation"):
    methods = json.loads(path.read_text())["Methods"]
    application = [m for m in methods if prefix in m["Method"]]
    counts = {"methods": len(application), "counters": 0, "block_counts": 0, "type_sites": 0, "call_edges": 0}
    for method in application:
        counts["call_edges"] += sum(edge["Weight"] > 0 for edge in method.get("CallWeights", []))
        for entry in method.get("InstrumentationData", []):
            kind = entry["InstrumentationKind"]
            if ("Edge" in kind or "BasicBlock" in kind) and entry.get("Data", 0) > 0:
                counts["counters"] += 1
                if "BasicBlock" in kind:
                    counts["block_counts"] += 1
            data = entry.get("Data", [])
            handles = data if isinstance(data, list) else [data]
            if kind == "HandleHistogramTypes" and any(isinstance(handle, str) and handle.startswith("[") for handle in handles):
                counts["type_sites"] += 1
    required = "call_edges" if mode == "sampling" else "type_sites"
    if (not counts["methods"] or not counts["counters"] or not counts[required]
            or (mode == "sampling" and not counts["block_counts"])):
        raise ValueError(f"Missing application methods, executed counters or {required}: {path}: {counts}")
    return counts


def convert(args):
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    collections = [json.loads((path / "collection.json").read_text()) for path in args.collections]
    expected = {(workload, mode) for workload in ("fusaka", "ethcall") for mode in ("instrumentation", "sampling")}
    if len(collections) != 4 or {(item["workload"], item["mode"]) for item in collections} != expected:
        raise ValueError("Require instrumentation and sampling captures for both Fusaka and eth_call")
    for key in ("image_id", "source_sha", "source_diff_sha256", "runtime"):
        if len({item[key] for item in collections}) != 1:
            raise ValueError(f"Cannot merge collections with different {key}")
    for workload, keys in (("fusaka", ("config_sha256", "amount", "payloads_sha256", "fcus_sha256")),
                           ("ethcall", ("corpus_sha256", "passes", "db_source"))):
        pair = [item for item in collections if item["workload"] == workload]
        for key in keys:
            if len({item[key] for item in pair}) != 1:
                raise ValueError(f"Cannot merge {workload} captures with different {key}")
    image = collections[0]["image_id"]
    references = output / "references"
    references.mkdir()
    container = subprocess.check_output(["docker", "create", image], text=True).strip()
    try:
        for source, dest in (("/nethermind", "app"), ("/usr/share/dotnet/shared", "framework")):
            subprocess.run(["docker", "cp", f"{container}:{source}", str(references / dest)], check=True)
    finally:
        subprocess.run(["docker", "rm", "-v", container], check=True)
    # Plugin folders repeat application assemblies; passing both copies confuses resolution.
    unique = output / "assemblies"
    unique.mkdir()
    for path in references.rglob("*.dll"):
        destination = unique / path.name
        if destination.exists():
            if sha256(destination) != sha256(path):
                raise ValueError(f"Different assembly binaries share the name {path.name}")
        else:
            shutil.copyfile(path, destination)
    ref_args = ["--reference", str(unique / "*.dll")]
    tool = ["dotnet", str(args.tool.resolve())]

    def invoke(name, *command):
        with (output / f"{name}.log").open("w") as log:
            subprocess.run(tool + list(command), check=True, stdout=log, stderr=subprocess.STDOUT)

    inputs = []
    callchain = {}
    for path, metadata in zip(args.collections, collections):
        trace = path / "traces" / metadata["trace"]
        if sha256(trace) != metadata["trace_sha256"]:
            raise ValueError(f"Trace checksum mismatch: {trace}")
        name = metadata["workload"] + "-" + metadata["mode"]
        mibc = output / f"{name}.mibc"
        sampling = ["--spgo"] if metadata["mode"] == "sampling" else []
        invoke(name, "create-mibc", *sampling, "--trace", str(trace.resolve()), "--output", str(mibc), *ref_args)
        dump = output / f"{name}.json"
        invoke(f"{name}-dump", "dump", "--input", str(mibc), "--output", str(dump))
        metadata["coverage"] = check_profile(dump, "[Nethermind.", metadata["mode"])
        if sampling:
            for caller, (callees, weights) in json.loads(Path(str(mibc) + ".callchain.json").read_text()).items():
                edges = callchain.setdefault(caller, {})
                for callee, weight in zip(callees, weights, strict=True):
                    edges[callee] = edges.get(callee, 0) + weight
        metadata["mibc_sha256"] = sha256(mibc)
        inputs += ["--input", str(mibc)]
    merged = output / "nethermind.mibc"
    invoke("merge", "merge", *inputs, "--output", str(merged))
    dump = output / "nethermind.json"
    invoke("dump", "dump", "--input", str(merged), "--output", str(dump))
    check_profile(dump, "[Nethermind.", "sampling")
    chain = output / "nethermind.callchain.json"
    if not callchain:
        raise ValueError("No unambiguous CallFrequency edges were generated")
    chain.write_text(json.dumps({caller: [list(edges), list(edges.values())] for caller, edges in callchain.items()}))
    manifest = {"collections": collections, "profile_sha256": sha256(merged),
                "callchain_sha256": sha256(chain),
                "coverage": check_profile(dump, "[Nethermind."),
                "tool_sha256": sha256(args.tool),
                "runtime_source": "79d0c463f1b55624c874a11585f7e47731e8d675"}
    (output / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n")
    print(json.dumps(manifest, indent=2))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--tool", type=Path, required=True, help="Pinned dotnet-pgo.dll from build-tool.sh")
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("collections", nargs=4, type=Path)
    convert(parser.parse_args())
