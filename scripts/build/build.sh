#!/bin/bash
# SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

set -e

build_config=release
output_path=$GITHUB_WORKSPACE/$PUB_DIR

cd $GITHUB_WORKSPACE/src/Nethermind/Nethermind.Runner

echo "Building Nethermind"

for rid in "linux-x64" "linux-arm64" "win-x64" "osx-x64" "osx-arm64"; do
  echo "  Publishing for $rid"

  dotnet publish -c $build_config -r $rid -o $output_path/$rid --sc true \
    -p:BuildTimestamp=$2 \
    -p:Commit=$1 \
    -p:DebugType=none \
    -p:IncludeAllContentForSelfExtract=true \
    -p:PublishSingleFile=true

  mkdir $output_path/$rid/keystore

  # A temporary symlink for Linux and macOS for the old executable name
  [[ $rid != win* ]] && ln -s -r $output_path/$rid/nethermind $output_path/$rid/Nethermind.Runner
done

mkdir $output_path/ref
cp ../artifacts/obj/**/$build_config/refint/*.dll $output_path/ref

echo "Build completed"
