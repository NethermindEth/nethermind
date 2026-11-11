#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

"""Build the pinned native light-client dependencies for Android arm64 on Windows."""

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shlex
import shutil
import subprocess
import xml.etree.ElementTree as ET


SCRIPT_DIRECTORY = Path(__file__).resolve().parent
REPOSITORY = SCRIPT_DIRECTORY.parents[2]
NDK_REVISION = "30.0.16248370"
API_LEVEL = 26


def run(*arguments, cwd=None, capture=False):
    return subprocess.run(
        [str(argument) for argument in arguments], cwd=cwd, check=True,
        text=True, encoding="utf-8", stdout=subprocess.PIPE if capture else None,
    ).stdout


def checkout(work, name, source):
    directory = work / (name + "-" + source["commit"][:12])
    if not directory.exists():
        run("git", "init", "--quiet", directory)
        run("git", "remote", "add", "origin", source["repository"], cwd=directory)
        run("git", "fetch", "--quiet", "--depth=1", "origin", source["commit"], cwd=directory)
        run("git", "checkout", "--quiet", "--detach", "FETCH_HEAD", cwd=directory)
    revision = run("git", "rev-parse", "HEAD", cwd=directory, capture=True).strip()
    if revision != source["commit"]:
        raise RuntimeError(f"Unexpected source revision in {directory}: {revision}")
    run("git", "diff", "--exit-code", "HEAD", cwd=directory)
    return directory


def verify_library(llvm, path, required):
    elf = run(llvm / "llvm-readelf.exe", "-h", "-l", "-d", path, capture=True)
    if "AArch64" not in elf or "ELF64" not in elf or "little endian" not in elf:
        raise RuntimeError(f"{path.name} is not little-endian Android arm64")
    needed = re.findall(r"Shared library: \[(.+?)\]", elf)
    if not set(needed) <= {"libc.so", "libdl.so", "libm.so"}:
        raise RuntimeError(f"Unexpected native dependencies in {path.name}: {needed}")
    loads = [line for line in elf.splitlines() if line.strip().startswith("LOAD")]
    if not loads or any(int(line.split()[-1], 16) < 16384 for line in loads):
        raise RuntimeError(f"{path.name} is not aligned for 16 KB pages")
    symbols = run(llvm / "llvm-nm.exe", "--dynamic", "--defined-only", path, capture=True)
    exports = {line.split()[-1] for line in symbols.splitlines() if line.split()}
    missing = set(required) - exports
    if missing:
        raise RuntimeError(f"Missing managed imports in {path.name}: {sorted(missing)}")
    return {
        "file": path.name,
        "bytes": path.stat().st_size,
        "sha256": hashlib.sha256(path.read_bytes()).hexdigest(),
        "verified_imports": len(required),
        "needed": needed,
        "load_alignment": 16384,
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--ndk", required=True, type=Path)
    parser.add_argument("--cmake", required=True, type=Path)
    parser.add_argument("--ninja", required=True, type=Path)
    parser.add_argument("--bash", required=True, type=Path, help="Git for Windows bash.exe")
    parser.add_argument("--work-dir", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path, help="Output directory for the three .so files")
    args = parser.parse_args()
    if os.name != "nt":
        parser.error("This build entry point currently supports Windows hosts")
    args.work_dir = args.work_dir.resolve()
    args.output = args.output.resolve()
    args.ndk = args.ndk.resolve()
    if " " in str(args.work_dir):
        parser.error("Use a work directory without spaces for the upstream Autotools build")
    ndk_properties = (args.ndk / "source.properties").read_text(encoding="utf-8")
    if f"Pkg.Revision = {NDK_REVISION}" not in ndk_properties:
        parser.error(f"Use the pinned Android NDK r30 ({NDK_REVISION})")
    lock = json.loads((SCRIPT_DIRECTORY / "native-lock.json").read_text(encoding="utf-8"))
    versions = {item.attrib["Include"]: item.attrib["Version"] for item in
                ET.parse(REPOSITORY / "Directory.Packages.props").iter("PackageVersion")}
    for package, version in lock["central_packages"].items():
        if versions.get(package) != version:
            parser.error(f"Revalidate native sources/imports after changing {package} from {version}")
    args.work_dir.mkdir(parents=True, exist_ok=True)
    args.output.mkdir(parents=True, exist_ok=True)
    llvm = args.ndk / "toolchains/llvm/prebuilt/windows-x86_64/bin"
    sources = {name: checkout(args.work_dir, name, source) for name, source in lock["sources"].items()}

    run(llvm / "clang.exe", f"--target=aarch64-linux-android{API_LEVEL}",
        "-O2", "-fno-builtin", "-fPIC", "-Wall", "-Wextra", "-Werror", "-D__BLST_PORTABLE__",
        "-shared", "-Wl,-Bsymbolic", "-Wl,-z,max-page-size=16384", "-Wl,-soname,libblst.so",
        "-Wl,--no-undefined", sources["blst"] / "src/server.c", sources["blst"] / "build/assembly.S",
        "-o", args.output / "libblst.so")

    secp_build = args.work_dir / "secp-build"
    run(args.cmake, "-S", sources["secp256k1"], "-B", secp_build, "-G", "Ninja",
        f"-DCMAKE_MAKE_PROGRAM={args.ninja.resolve().as_posix()}",
        f"-DCMAKE_TOOLCHAIN_FILE={(args.ndk / 'build/cmake/android.toolchain.cmake').as_posix()}",
        "-DANDROID_ABI=arm64-v8a", f"-DANDROID_PLATFORM=android-{API_LEVEL}",
        "-DCMAKE_BUILD_TYPE=Release", "-DSECP256K1_ENABLE_MODULE_RECOVERY=ON",
        "-DSECP256K1_BUILD_TESTS=OFF", "-DSECP256K1_BUILD_BENCHMARK=OFF",
        "-DSECP256K1_BUILD_EXHAUSTIVE_TESTS=OFF", "-DSECP256K1_BUILD_CTIME_TESTS=OFF",
        "-DCMAKE_COMPILE_WARNING_AS_ERROR=ON")
    run(args.cmake, "--build", secp_build, "--parallel", "4")
    shutil.copy2(secp_build / "lib/libsecp256k1.so", args.output)

    cygpath = args.bash.resolve().parent.parent / "usr/bin/cygpath.exe"

    def posix(path):
        return run(cygpath, "-u", path.resolve(), capture=True).strip()

    # Native NDK make expands SHELL in recipes; a short path avoids splitting "Program Files".
    shell = run(cygpath, "-m", "-s", cygpath.parent / "sh.exe", capture=True).strip()
    if " " in shell:
        parser.error("Git's shell needs a space-free path; install portable Git under the work directory")
    install = args.work_dir / "sodium-install"
    script = args.work_dir / "build-sodium.sh"
    script.write_text(
        "#!/usr/bin/env bash\nset -euo pipefail\n"
        f"cd {shlex.quote(posix(sources['libsodium']))}\n"
        f"export PATH={shlex.quote(posix(llvm))}:{shlex.quote(posix(args.ndk / 'prebuilt/windows-x86_64/bin'))}:$PATH\n"
        f"export CC='clang.exe --target=aarch64-linux-android{API_LEVEL}'\n"
        "export AR=llvm-ar.exe RANLIB=llvm-ranlib.exe STRIP=llvm-strip.exe\n"
        "export CFLAGS='-O2 -fPIC -Werror'\n"
        "export LDFLAGS='-Wl,-z,max-page-size=16384 -Wl,--no-undefined'\n"
        "./configure --disable-soname-versions --disable-pie --host=aarch64-linux-android "
        f"--prefix={shlex.quote(posix(install))}\n"
        f"make -j4 SHELL={shlex.quote(shell)} install\n", encoding="utf-8", newline="\n")
    run(args.bash, script.as_posix())
    shutil.copy2(install / "lib/libsodium.so", args.output)

    libraries = []
    for name, item in lock["sources"].items():
        library = args.output / item["library"]
        run(llvm / "llvm-strip.exe", "--strip-unneeded", library)
        libraries.append(verify_library(llvm, library, item["imports"]))
    licenses = args.output / "licenses"
    licenses.mkdir(exist_ok=True)
    for name in sources:
        license_file = "COPYING" if name == "secp256k1" else "LICENSE"
        shutil.copy2(sources[name] / license_file, licenses / (name + ".txt"))
    manifest = {"ndk": NDK_REVISION, "android_api": API_LEVEL, "abi": "arm64-v8a",
                "source_commits": {name: source["commit"] for name, source in lock["sources"].items()},
                "libraries": libraries}
    (args.output / "native-build.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(manifest, indent=2))


if __name__ == "__main__":
    main()
