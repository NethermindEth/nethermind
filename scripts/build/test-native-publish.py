#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

"""Check Runner's transitive publish items without compiling or executing foreign code."""

import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile


ROOT = Path(__file__).resolve().parents[2]
LIBRARIES = {
    "linux-x64": "libnethermind_patricia.so",
    "linux-arm64": "libnethermind_patricia.so",
    "osx-x64": "libnethermind_patricia.dylib",
    "osx-arm64": "libnethermind_patricia.dylib",
    "win-x64": "nethermind_patricia.dll",
    "win-arm64": "nethermind_patricia.dll",
}


def main():
    # Copy the real project graph, not a synthetic direct reference to the native leaf.
    # Isolating it also prevents placeholder foreign artifacts from reaching real packages.
    with tempfile.TemporaryDirectory(prefix="native-publish-", dir=ROOT / ".tmp") as temporary:
        root = Path(temporary)
        tracked = subprocess.check_output(
            ["git", "ls-files", "-z", "*.csproj", "*.props", "*.targets", "global.json", "nuget.config", "*packages.lock.json"],
            cwd=ROOT,
        ).decode().split("\0")
        for name in tracked:
            source = ROOT / name
            if name and source.is_file():
                destination = root / name
                destination.parent.mkdir(parents=True, exist_ok=True)
                shutil.copyfile(source, destination)

        runner = root / "src/Nethermind/Nethermind.Runner/Nethermind.Runner.csproj"
        native = root / "src/Nethermind/Nethermind.Trie.Native"
        environment = os.environ | {"DOTNET_NOLOGO": "1", "DOTNET_CLI_TELEMETRY_OPTOUT": "1"}
        subprocess.run(
            ["dotnet", "restore", str(runner), "--locked-mode", "-p:NuGetAudit=false"],
            cwd=root, env=environment, check=True,
        )
        host = subprocess.check_output(
            ["dotnet", "msbuild", str(native / "Nethermind.Trie.Native.csproj"),
             "-getProperty:NETCoreSdkRuntimeIdentifier", "-nologo"],
            cwd=root, env=environment, text=True,
        ).strip()
        host_asset = native / "rust/target/native" / host / LIBRARIES[host]
        host_asset.parent.mkdir(parents=True)
        host_asset.write_text("placeholder: never compiled or executed\n")

        def collect(rid):
            return subprocess.run(
                ["dotnet", "msbuild", str(runner),
                 "-t:PrepareProjectReferences;GetCopyToPublishDirectoryItems",
                 "-getItem:AllPublishItemsFullPathWithTargetPath",
                 f"-p:RuntimeIdentifier={rid}", "-p:UseAppHost=false",
                 "-p:PublishSingleFile=false", "-p:Configuration=Release", "-nologo"],
                cwd=root, env=environment, text=True, capture_output=True,
            )

        for rid, library in LIBRARIES.items():
            artifact = native / "rust/target/native" / rid / library
            artifact.parent.mkdir(parents=True, exist_ok=True)
            artifact.write_text("placeholder: publish-item selection only\n")
            result = collect(rid)
            assert result.returncode == 0, result.stdout + result.stderr
            items = json.loads(result.stdout)["Items"]["AllPublishItemsFullPathWithTargetPath"]
            selected = {item["Identity"] for item in items if "nethermind_patricia" in item["Identity"]}
            assert selected == {str(artifact)}, (rid, selected)

            artifact.unlink()
            result = collect(rid)
            assert result.returncode != 0, f"Missing {rid} artifact was accepted"
            assert f"Native Patricia library for {rid} is missing" in result.stdout + result.stderr, result.stdout + result.stderr
            host_asset.write_text("placeholder: never compiled or executed\n")
            print(f"PASS {rid}: target selected; missing target rejected", flush=True)


if __name__ == "__main__":
    (ROOT / ".tmp").mkdir(exist_ok=True)
    main()
