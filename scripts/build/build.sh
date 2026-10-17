#!/bin/bash
# SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

set -euo pipefail

build_config=release
output_path=/nethermind/output

cd src/Nethermind/Nethermind.Runner

echo "Building Nethermind"

# These artifacts are built on their native platforms before entering the package container.
for rid in "linux-arm64" "linux-x64" "osx-arm64" "osx-x64" "win-arm64" "win-x64"; do
  case "$rid" in
    linux-*) library=libnethermind_patricia.so ;;
    osx-*) library=libnethermind_patricia.dylib ;;
    win-*) library=nethermind_patricia.dll ;;
  esac
  test -s "../Nethermind.Trie.Native/rust/target/native/$rid/$library" || {
    echo "Missing native Patricia artifact for $rid; build it with scripts/build/native.sh on the matching platform." >&2
    exit 1
  }
done

dotnet restore --locked-mode

for rid in "linux-arm64" "linux-x64" "osx-arm64" "osx-x64" "win-arm64" "win-x64"; do
  echo "  Publishing for $rid"

  dotnet publish -c $build_config -r $rid -o $output_path/$rid --no-restore --sc \
    -p:DebugType=embedded \
    -p:IncludeAllContentForSelfExtract=true \
    -p:PublishSingleFile=true \
    -p:SourceRevisionId=$1

  mkdir $output_path/$rid/keystore

  # A temporary symlink for Linux to support the old executable name
  [[ "$rid" == linux-* ]] && ln -sr $output_path/$rid/nethermind $output_path/$rid/Nethermind.Runner
done

mkdir $output_path/ref
cp ../artifacts/obj/**/$build_config/refint/*.dll $output_path/ref

echo "Build completed"
