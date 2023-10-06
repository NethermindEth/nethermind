#!/bin/bash
# SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

set -e

output_path=$GITHUB_WORKSPACE/$PUB_DIR

cd $GITHUB_WORKSPACE/nethermind/src/Nethermind/Nethermind.Runner

echo "Building Nethermind"

for rid in "linux-x64" "linux-arm64" "win-x64" "osx-x64" "osx-arm64"
do
  echo "  Publishing for $rid"

  dotnet publish -c release -r $rid -o $output_path/$rid --sc true \
    -p:BuildTimestamp=$2 \
    -p:Commit=$1 \
    -p:DebugType=none \
    -p:Deterministic=true \
    -p:IncludeAllContentForSelfExtract=true \
    -p:PublishSingleFile=true

  cp -r configs $output_path/$rid
  mkdir $output_path/$rid/keystore

  # A temporary symlink for Linux to support existing scripts if any
  # To be removed after a few months
  [[ $rid == linux* ]] && ln -s -r $output_path/$rid/nethermind $output_path/$rid/Nethermind.Runner
done

cd ..
mkdir $output_path/ref
cp **/obj/release/**/refint/*.dll $output_path/ref

echo "Build completed"
