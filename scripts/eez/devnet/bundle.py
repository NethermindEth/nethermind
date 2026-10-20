#!/usr/bin/env python3
import argparse
import json
import os
from pathlib import Path
import re
import shutil
import subprocess

from embedded import CASES


def libraries(executables, destination):
    destination.mkdir()
    base = {"libc.so.6", "libm.so.6", "libpthread.so.0", "libdl.so.2", "librt.so.1", "libresolv.so.2", "libutil.so.1"}
    for executable in executables:
        result = subprocess.run(["ldd", str(executable)], text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, check=False)
        if result.returncode and "not a dynamic executable" not in result.stdout and "statically linked" not in result.stdout:
            raise ValueError("could not inspect compiled runtime libraries: " + result.stdout)
        if "not found" in result.stdout:
            raise ValueError("compiled executable has unresolved runtime libraries")
        for match in re.finditer(r"=> (/[^\s]+)", result.stdout):
            source = Path(match[1])
            if source.name not in base:
                target = destination / source.name
                if not target.exists():
                    shutil.copy2(source, target)


def tests(directory):
    selected = set(CASES) | {"nethermind_streaming_controls"}
    binaries = {}
    for line in (directory / "test-build.jsonl").read_text().splitlines():
        item = json.loads(line)
        name = item.get("target", {}).get("name")
        if item.get("reason") != "compiler-artifact" or name not in selected or not item.get("executable"):
            continue
        if item["target"].get("kind") != ["test"] or item.get("profile", {}).get("test") is not True:
            raise ValueError("compiled target is not an integration test executable")
        path = Path(item["executable"]).resolve()
        root = Path(os.environ["EEZ_UPSTREAM"]).resolve()
        relative = path.relative_to(root)
        if not path.is_file() or not os.access(path, os.X_OK) or name in binaries:
            raise ValueError("missing or duplicate compiled test executable")
        binaries[name] = str(relative)
    if set(binaries) != selected:
        raise ValueError("compiled upstream test inventory changed")
    (directory / "test-binaries.json").write_text(json.dumps(binaries, indent=2) + "\n")


def pack(directory):
    upstream = Path(os.environ["EEZ_UPSTREAM"]).resolve()
    keep = directory / "executables"
    keep.mkdir()
    if os.environ["EEZ_PROFILE"] == "rollup0":
        binaries = json.loads((directory / "test-binaries.json").read_text())
        for relative in binaries.values():
            destination = keep / relative
            destination.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(upstream / relative, destination)
        for name in ("eez-composer", "eez-follower", "eez-proof-signer"):
            destination = keep / "target/ci-e2e" / name
            shutil.copy2(Path(os.environ["EEZ_RUNTIME_DIR"]) / name, destination)
        runtime = directory / "runtime"
        libraries([*(upstream / name for name in binaries.values()),
                   *(path for path in runtime.iterdir() if path.is_file() and os.access(path, os.X_OK))], runtime / "lib")
    # Keep fixtures and matching source metadata, not the compiler's dependency cache.
    subprocess.run(["tar", "--exclude=./target", "--exclude=./artifacts", "--exclude=./broadcast",
                    "--exclude=./eez-core-protocol/tmp", "-C", str(upstream),
                    "-cf", str(directory / "sources.tar"), "."], check=True)
    subprocess.run(["tar", "-C", str(keep), "-rf", str(directory / "sources.tar"), "."], check=True)
    subprocess.run(["zstd", "-T0", "-3", "--rm", str(directory / "sources.tar")], check=True)
    shutil.rmtree(keep)
    subprocess.run(["tar", "-I", "zstd", "-C", str(directory), "-cf", str(directory / "runtime.tar.zst"), "runtime"], check=True)
    shutil.rmtree(directory / "runtime")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("mode", choices=("tests", "pack"))
    parser.add_argument("--directory", type=Path, required=True)
    args = parser.parse_args()
    {"tests": tests, "pack": pack}[args.mode](args.directory)


if __name__ == "__main__":
    main()
