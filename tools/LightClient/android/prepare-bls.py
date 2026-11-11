#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

"""Build the pinned BLS binding with Android native-library resolution."""

import argparse
import hashlib
import importlib.util
import json
from pathlib import Path
import shutil


DIRECTORY = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location("native_build", DIRECTORY / "build-native.py")
native_build = importlib.util.module_from_spec(spec)
spec.loader.exec_module(native_build)
COMMIT = "989fb662c22c59de49e11c5e0f019ac0ae80d9b2"
VERSION = "0.3.17.15"
ANCHOR = """    private static nint LoadLibrary(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
"""
ANDROID_LOADER = """        if (OperatingSystem.IsAndroid())
            return NativeLibrary.Load($"lib{libraryName}.so", assembly, searchPath);

"""


def adapt_resolver(content):
    if content.count(ANCHOR) != 1 or ANDROID_LOADER in content:
        raise RuntimeError("The pinned BLS resolver has changed; review the Android adaptation")
    return content.replace(ANCHOR, ANCHOR + ANDROID_LOADER)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--work-dir", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--dotnet", default="dotnet")
    args = parser.parse_args()
    work = args.work_dir.resolve()
    output = args.output.resolve()
    work.mkdir(parents=True, exist_ok=True)
    output.mkdir(parents=True, exist_ok=True)
    source = native_build.checkout(work, "blst-bindings", {
        "repository": "https://github.com/NethermindEth/blst-bindings.git", "commit": COMMIT,
    })
    project = work / "android-bls-binding"
    project.mkdir(exist_ok=True)
    files = []
    for path in sorted((source / "src/Nethermind.Crypto.Bls").glob("*.cs")):
        content = path.read_text(encoding="utf-8")
        original_hash = hashlib.sha256(content.encode()).hexdigest()
        if path.name == "Bls.cs":
            content = adapt_resolver(content)
        (project / path.name).write_text(content, encoding="utf-8", newline="\n")
        files.append({"file": path.name, "upstream_sha256": original_hash,
                      "sha256": hashlib.sha256(content.encode()).hexdigest()})
    project_file = project / "Nethermind.Crypto.Bls.csproj"
    project_file.write_text(f"""<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <LangVersion>14.0</LangVersion>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <Version>{VERSION}</Version>
    <AssemblyVersion>{VERSION}</AssemblyVersion>
    <InformationalVersion>{VERSION}+android-loader.{COMMIT}</InformationalVersion>
    <Company>Nethermind</Company>
    <Copyright>Demerzel Solutions Limited</Copyright>
    <Description>C# bindings for the Supranational Blst library</Description>
  </PropertyGroup>
  <ItemGroup>
    <AssemblyAttribute Include="System.Reflection.AssemblyMetadataAttribute">
      <_Parameter1>RepositoryUrl</_Parameter1>
      <_Parameter2>https://github.com/nethermindeth/blst-bindings</_Parameter2>
    </AssemblyAttribute>
  </ItemGroup>
</Project>
""", encoding="utf-8")
    native_build.run(args.dotnet, "build", project_file, "-c", "release",
                     "-p:SaveDiskSpace=true", "-warnaserror", "--output", output)
    licenses = output / "licenses"
    licenses.mkdir(exist_ok=True)
    shutil.copy2(source / "LICENSE", licenses / "blst-bindings.txt")
    shutil.copy2(source / "THIRD-PARTY-NOTICES", licenses / "blst-bindings-notices.txt")
    assembly = output / "Nethermind.Crypto.Bls.dll"
    manifest = {"upstream_commit": COMMIT, "assembly_version": VERSION,
                "change": "Android native-library resolver only; cryptography and interop signatures unchanged",
                "sha256": hashlib.sha256(assembly.read_bytes()).hexdigest(), "sources": files}
    (output / "bls-binding-build.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
