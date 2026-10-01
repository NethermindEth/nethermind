#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

"""Build matched Linux x64 runtime images from validated strict training inputs."""
import argparse
import json
from pathlib import Path
import re
import shutil
import struct
import subprocess
import tarfile

from validate_benchmark_bundle import compare_rebuilt, digest, validate_bundle, validate_selection


SDK_BASE = "mcr.microsoft.com/dotnet/sdk:10.0.401-resolute@sha256:e37606d2092c70f211d9e4f477dd7373b4b7d2b049399e4c6ff3a261243011c6"
RUNTIME_BASE = "mcr.microsoft.com/dotnet/aspnet:10.0.12-resolute@sha256:5b7bc1308a48fc0d1f8add112d220279081d1d5eb07783ad86729a7241b4fae2"
PROFILE_PATH = "/nethermind/src/Nethermind/Nethermind.Runner/pgo/nethermind.mibc"
ARMS = ("il-baseline", "no-profile", "one-method-profile")


def run(command, log=None, cwd=None):
    if log is None:
        return subprocess.check_output(command, cwd=cwd, text=True).strip()
    with log.open("wb") as output:
        result = subprocess.run(command, cwd=cwd, stdout=output, stderr=subprocess.STDOUT)
    if result.returncode:
        raise RuntimeError(f"build command failed; inspect local {log.name}")


def write_json(path, value):
    path.write_text(json.dumps(value, indent=2) + "\n", encoding="utf-8")


def native_header(path):
    data = path.read_bytes()
    if data[:2] != b"MZ":
        return {"managed": False, "ready_to_run": False}
    pe = struct.unpack_from("<I", data, 0x3c)[0]
    if data[pe:pe + 4] != b"PE\0\0":
        raise ValueError("invalid PE signature")
    count = struct.unpack_from("<H", data, pe + 6)[0]
    optional = pe + 24
    sections = optional + struct.unpack_from("<H", data, pe + 20)[0]
    directories = optional + {0x10b: 96, 0x20b: 112}[struct.unpack_from("<H", data, optional)[0]]
    def offset(rva):
        for index in range(count):
            size, address, raw_size, raw_offset = struct.unpack_from("<IIII", data, sections + index * 40 + 8)
            if address <= rva < address + max(size, raw_size):
                return raw_offset + rva - address
        raise ValueError("unmapped PE RVA")
    clr = struct.unpack_from("<I", data, directories + 14 * 8)[0]
    if not clr:
        return {"managed": False, "ready_to_run": False}
    native = struct.unpack_from("<I", data, offset(clr) + 64)[0]
    return {"managed": True, "ready_to_run": bool(native and data[offset(native):offset(native) + 4] == b"RTR\0")}


def verify_properties(properties, profiled):
    required = {"PublishReadyToRun": "true", "PublishReadyToRunComposite": "false",
                "PublishReadyToRunUseCrossgen2": "true", "TieredPGO": "true"}
    if any(properties["Properties"].get(key) != value for key, value in required.items()):
        raise ValueError("publish properties do not preserve the matched R2R/dynamic PGO contract")
    inputs = properties["Items"]["PublishReadyToRunPgoFiles"]
    if [item["Identity"] for item in inputs] != ([PROFILE_PATH] if profiled else []):
        raise ValueError("publish profile inputs do not match the experimental arm")
    return {**required, "profile_inputs": len(inputs)}


def extract_assembly(log, output, diagnostic_method):
    listings = []
    compiler_versions = set()
    current = None
    with log.open(encoding="utf-8", errors="replace") as stream:
        for line in stream:
            if "/tools/crossgen2 --targetos:linux" in line:
                compiler_versions.update(re.findall(r"microsoft\.netcore\.app\.crossgen2\.linux-x64/([0-9.]+)/tools/crossgen2", line))
            clean = re.sub(r" \(TaskId:\d+\)\s*$", "", line).strip()
            if "; Assembly listing for method" in clean:
                if current is not None:
                    raise ValueError("compiler assembly listing is incomplete")
                if f":{diagnostic_method}(" in clean:
                    current = [clean[clean.index("; Assembly listing"):]]
            elif current is not None:
                current.append(clean)
                if "; Total bytes of code" in clean:
                    listings.append(current)
                    current = None
    if current is not None or not listings:
        raise ValueError("requested compiler assembly listing is missing or incomplete")
    output.write_text("\n\n".join("\n".join(lines) for lines in listings) + "\n", encoding="utf-8")
    entries = [float(match.group(1)) for lines in listings for line in lines
               if (match := re.search(r"with Synthesized PGO: fgCalledCount is ([0-9.eE+]+)", line))]
    return {"listings": len(listings), "positive_profile_entries": sum(value > 0 for value in entries),
            "compiler_versions": sorted(compiler_versions), "sha256": digest(output)}


def publish_script(source_sha, epoch, arm, diagnostic_method):
    # All interpolated values are validated identifiers or fixed arm names.
    profile = f'cp /selected/target.mibc "{PROFILE_PATH}"\n' if arm == "one-method-profile" else ""
    return f'''#!/bin/bash
set -euo pipefail
export CI=true SOURCE_DATE_EPOCH={epoch} DOTNET_TieredPGO=1
unset DOTNET_ReadPGOData DOTNET_WritePGOData DOTNET_PGODataPath DOTNET_EnableEventPipe DOTNET_EventPipeConfig DOTNET_EventPipeOutputPath
cd /nethermind/src/Nethermind/Nethermind.Runner
if [ -f "{PROFILE_PATH}" ]; then mv "{PROFILE_PATH}" /tmp/historical-profile.mibc; fi
{profile}dotnet msbuild -p:Configuration=release -p:RuntimeIdentifier=linux-x64 -p:PublishReadyToRun=true -p:SelfContained=false -getProperty:PublishReadyToRun,PublishReadyToRunComposite,PublishReadyToRunUseCrossgen2,TieredPGO -getItem:PublishReadyToRunPgoFiles > /output/evaluated-properties.json
dotnet publish -c release -r linux-x64 -o /output/app --no-self-contained -p:PublishReadyToRun=true -p:SourceRevisionId={source_sha} '-p:PublishReadyToRunCrossgen2ExtraArgs=--codegenopt:JitDisasm={diagnostic_method} --codegenopt:JitDump={diagnostic_method}' -v:diag > /output/publish.log 2>&1
'''


def image_definition(source_sha):
    return f'''FROM {RUNTIME_BASE}
WORKDIR /nethermind
LABEL org.opencontainers.image.revision={source_sha}
VOLUME /nethermind/keystore /nethermind/logs /nethermind/nethermind_db
EXPOSE 8545 8551 30303
COPY . .
ENTRYPOINT ["./entrypoint.sh"]
'''


def select_profile(bundle, references, reference_manifest, selected, tool_command, selector, method, logs):
    selected.mkdir()
    # TraceEvent can regenerate its ETLX cache beside the input trace.
    conversion_trace = selected / "nethermind-1.nettrace"
    shutil.copyfile(bundle / "nethermind-pgo-raw-data/nethermind-1.nettrace", conversion_trace)
    run([*tool_command, "dump", "--input", str(bundle / "nethermind-pgo-profile/nethermind.mibc"),
         "--output", str(selected / "full.json")], logs / "full-dump.log")
    command = [*tool_command, "create-mibc", "--trace", str(conversion_trace),
               "--automatic-references", "false", "--output", str(selected / "target.mibc"),
               "--include-methods", selector]
    for directory in sorted({Path(item["path"]).parent for item in reference_manifest["files"]}):
        command.extend(["--reference", str(references / directory / "*.dll")])
    run(command, logs / "selection.log")
    run([*tool_command, "dump", "--input", str(selected / "target.mibc"), "--output", str(selected / "target.json")],
        logs / "selected-dump.log")
    return validate_selection(json.loads((selected / "full.json").read_text(encoding="utf-8")),
                              json.loads((selected / "target.json").read_text(encoding="utf-8")), method)


def compare_publications(control, profiled):
    def inventory(root):
        return {path.relative_to(root).as_posix(): digest(path) for path in root.rglob("*") if path.is_file()}
    original = inventory(control)
    trained = inventory(profiled)
    if not original or original.keys() != trained.keys():
        raise ValueError("matched R2R publications have different file inventories")
    changed = [name for name in original if original[name] != trained[name]]
    if any(not name.endswith(".dll") for name in changed):
        raise ValueError("non-DLL application files differ between experimental arms")
    return {"changed_dlls": changed, "matched_inventory": len(original)}


def build(args):
    if re.fullmatch(r"pgo-benchmark-[0-9]+-[0-9]+", args.tag_prefix) is None:
        raise ValueError("image tag prefix must identify the CI run and attempt")
    if re.fullmatch(r"[A-Za-z_][A-Za-z0-9_]*", args.diagnostic_method) is None:
        raise ValueError("diagnostic method must be a simple method name")
    if f".{args.diagnostic_method}(" not in args.method:
        raise ValueError("diagnostic method does not match the selected profile method")
    bundle = args.bundle.resolve(strict=True)
    run_api = json.loads((bundle / "run-api.json").read_text(encoding="utf-8"))
    training = validate_bundle(bundle, run_api, args.source_sha)
    root = args.output.resolve()
    if root.is_relative_to(bundle) or bundle.is_relative_to(root):
        raise ValueError("build output and training provenance must be separate")
    root.mkdir(parents=True, exist_ok=False)
    evidence = root / "evidence"
    evidence.mkdir()
    write_json(evidence / "bundle-validation.json", training)
    source = root / "source"
    source.mkdir()
    archive = root / "source.tar"
    run(["git", "archive", "--format=tar", f"--output={archive}", args.source_sha], cwd=args.repository)
    with tarfile.open(archive) as files:
        files.extractall(source, filter="data")
    first_stage = next(line.strip() for line in (source / "Dockerfile.pgo").read_text(encoding="utf-8").splitlines()
                       if line.lstrip().startswith("FROM "))
    if first_stage != f"FROM --platform=$BUILDPLATFORM {SDK_BASE} AS build":
        raise ValueError("training source does not use the pinned SDK build base")
    epoch = int(run(["git", "show", "-s", "--format=%ct", args.source_sha], cwd=args.repository))
    write_json(evidence / "source.json", {"source_sha": args.source_sha, "source_date_epoch": epoch,
                                          "archive_sha256": digest(archive), "sdk_base": SDK_BASE, "runtime_base": RUNTIME_BASE})
    tool = args.pgo_tool.resolve(strict=True)
    tool_command = ([str(args.pgo_host.resolve(strict=True))] if args.pgo_host else []) + [str(tool)]
    selected = root / "selected"
    references = bundle / f"pgo-app-references-{run_api['id']}-{run_api['run_attempt']}"
    reference_manifest = json.loads((references / "manifest.json").read_text(encoding="utf-8"))
    selection = select_profile(bundle, references, reference_manifest, selected, tool_command, args.selector, args.method, root)
    write_json(evidence / "selection.json", {**selection, "profile_sha256": digest(selected / "target.mibc")})
    sdk_image = f"nethermind-pgo2:{args.tag_prefix}-build"
    run(["docker", "build", "--platform", "linux/amd64", "--target", "build", "-f", "Dockerfile.pgo",
         "--build-arg", "BUILD_CONFIG=release", "--build-arg", "CI=true", "--build-arg", f"COMMIT_HASH={args.source_sha}",
         "--build-arg", f"SOURCE_DATE_EPOCH={epoch}", "--build-arg", "TARGETARCH=amd64", "-t", sdk_image, "."],
        root / "sdk-build.log", cwd=source)
    images = []
    environments = []
    for arm in ARMS:
        arm_root = root / arm
        arm_root.mkdir()
        app = arm_root / "app"
        if arm == "il-baseline":
            app.mkdir()
            container = run(["docker", "create", "--entrypoint", "/bin/true", sdk_image])
            try:
                run(["docker", "cp", f"{container}:/publish/.", str(app)], arm_root / "copy.log")
            finally:
                run(["docker", "rm", "-v", container])
            write_json(evidence / "rebuilt-inputs.json", compare_rebuilt(references, reference_manifest, app))
        else:
            script = arm_root / "publish.sh"
            with script.open("w", encoding="utf-8", newline="\n") as stream:
                stream.write(publish_script(args.source_sha, epoch, arm, args.diagnostic_method))
            run(["docker", "run", "--rm", "--platform", "linux/amd64", "--mount", f"type=bind,src={arm_root},dst=/output",
                 "--mount", f"type=bind,src={selected},dst=/selected,readonly", sdk_image, "bash", "/output/publish.sh"],
                arm_root / "container.log")
            properties = verify_properties(json.loads((arm_root / "evaluated-properties.json").read_text(encoding="utf-8")),
                                           arm == "one-method-profile")
            write_json(evidence / f"{arm}-properties.json", properties)
            asm = extract_assembly(arm_root / "publish.log", evidence / f"{arm}.asm", args.diagnostic_method)
            if (arm == "one-method-profile" and not asm["positive_profile_entries"]
                    or arm == "no-profile" and asm["positive_profile_entries"]):
                raise ValueError("compiler assembly does not show the required profile consumption")
            if asm["compiler_versions"] != ["10.0.12"]:
                raise ValueError("actual Crossgen2 invocation does not use the pinned compiler version")
            write_json(evidence / f"{arm}-compiler.json", asm)
        headers = [{"path": path.relative_to(app).as_posix(), **native_header(path)}
                   for path in sorted(app.rglob("*.dll"))]
        managed = sum(item["managed"] for item in headers)
        r2r = sum(item["ready_to_run"] for item in headers)
        if not managed or (arm == "il-baseline" and r2r) or (arm != "il-baseline" and not r2r):
            raise ValueError("emitted native headers do not match the experimental arm")
        write_json(evidence / f"{arm}-native-headers.json", {"managed": managed, "ready_to_run": r2r, "files": headers})
        if any(path.suffix.lower() in (".jit", ".mibc", ".nettrace", ".etlx") for path in app.rglob("*") if path.is_file()):
            raise ValueError("runtime image unexpectedly contains raw training data or a text profile")
        shutil.copyfile(source / "scripts/entrypoint.sh", app / "entrypoint.sh")
        (app / "entrypoint.sh").chmod(0o755)
        definition = arm_root / "Dockerfile"
        definition.write_text(image_definition(args.source_sha), encoding="utf-8")
        tag = f"nethermindeth/nethermind:{args.tag_prefix}-{arm}"
        run(["docker", "build", "--platform", "linux/amd64", "-f", str(definition), "-t", tag, str(app)], arm_root / "image-build.log")
        info = json.loads(run(["docker", "image", "inspect", tag]))[0]
        environment = info["Config"]["Env"]
        base_environment = json.loads(run(["docker", "image", "inspect", RUNTIME_BASE]))[0]["Config"]["Env"]
        if environment != base_environment:
            raise ValueError("runtime image has environment overrides beyond the pinned production base")
        if info["Config"]["Entrypoint"] != ["./entrypoint.sh"] or info["Architecture"] != "amd64":
            raise ValueError("runtime image startup or architecture does not match")
        environments.append(environment)
        runtime_config = app / "nethermind.runtimeconfig.json"
        config = json.loads(runtime_config.read_text(encoding="utf-8"))
        if config["runtimeOptions"]["configProperties"].get("System.Runtime.TieredPGO") is not True:
            raise ValueError("runtime image does not preserve production dynamic PGO")
        run(["docker", "run", "--rm", "--network", "none", tag, "--help"], arm_root / "help.log")
        images.append({"arm": arm, "tag": tag, "image_id": info["Id"], "managed_dlls": managed,
                       "ready_to_run_dlls": r2r, "runtimeconfig_sha256": digest(runtime_config),
                       "tiered_pgo": True, "entrypoint_sha256": digest(app / "entrypoint.sh")})
    if any(environment != environments[0] for environment in environments):
        raise ValueError("runtime image environments are not matched")
    if len({image["runtimeconfig_sha256"] for image in images}) != 1:
        raise ValueError("runtime configuration differs between experimental arms")
    write_json(evidence / "publication-comparison.json", compare_publications(root / "no-profile/app", root / "one-method-profile/app"))
    if validate_bundle(bundle, run_api, args.source_sha) != training:
        raise ValueError("authenticated training provenance changed during image preparation")
    write_json(evidence / "images.json", {"source_sha": args.source_sha, "images": images, "performance_claim": False})
    if args.publish:
        for image in images:
            run(["docker", "push", image["tag"]], root / f"{image['arm']}-push.log")
            digests = json.loads(run(["docker", "image", "inspect", image["tag"]]))[0]["RepoDigests"]
            matches = [value for value in digests if re.fullmatch(r"nethermindeth/nethermind@sha256:[0-9a-f]{64}", value)]
            if len(matches) != 1:
                raise ValueError("published image has no unambiguous immutable registry identity")
            image["digest"] = matches[0]
        write_json(evidence / "images.json", {"source_sha": args.source_sha, "images": images, "performance_claim": False})
    return images


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repository", type=Path, required=True)
    parser.add_argument("--bundle", type=Path, required=True)
    parser.add_argument("--source-sha", required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--pgo-tool", type=Path, required=True)
    parser.add_argument("--pgo-host", type=Path)
    parser.add_argument("--selector", required=True)
    parser.add_argument("--method", required=True)
    parser.add_argument("--diagnostic-method", required=True)
    parser.add_argument("--tag-prefix", required=True)
    parser.add_argument("--publish", action="store_true")
    args = parser.parse_args()
    print(json.dumps(build(args), indent=2))


if __name__ == "__main__":
    main()
