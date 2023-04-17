#!/bin/bash
# SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

set -e

CLI_PATH=$GITHUB_WORKSPACE/nethermind/src/Nethermind/Nethermind.Cli
OUTPUT_PATH=$GITHUB_WORKSPACE/$PUB_DIR

cd $CLI_PATH

echo "Building Nethermind CLI v$1+${2:0:8}"

for rid in "linux-x64" "linux-arm64" "win-x64" "osx-x64" "osx-arm64"
do
  echo "  Publishing for $rid"

  dotnet publish -c release -r $rid -o $OUTPUT_PATH/$rid --sc true -p:Commit=$2 -p:BuildTimestamp=$3 -p:PublishSingleFile=true -p:IncludeAllContentForSelfExtract=true

  rm -rf $OUTPUT_PATH/$rid/*.pdb
done

echo "Build completed"
