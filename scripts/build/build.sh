#!/bin/bash
# SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

set -e

build_config=release
output_path=/nethermind/output

cd src/Nethermind/Nethermind.Runner

echo "Building Nethermind"

dotnet restore --locked-mode

for rid in "linux-arm64" "linux-x64" "osx-arm64" "osx-x64" "win-x64"; do
  echo "  Publishing for $rid"

  dotnet publish -c $build_config -r $rid -o $output_path/$rid --no-restore --sc \
    -p:BuildTimestamp=$2 \
    -p:Commit=$1 \
    -p:DebugType=embedded \
    -p:IncludeAllContentForSelfExtract=true \
    -p:PublishSingleFile=true

  mkdir $output_path/$rid/keystore

  # A temporary symlink for Linux to support the old executable name
  [[ $rid == linux-x64 ]] && ln -sr $output_path/$rid/nethermind $output_path/$rid/Nethermind.Runner
done

mkdir $output_path/ref
cp ../artifacts/obj/**/$build_config/refint/*.dll $output_path/ref

echo "Build completed"
