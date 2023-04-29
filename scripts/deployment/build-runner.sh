#!/bin/bash
# SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

set -e

OUTPUT_PATH=$GITHUB_WORKSPACE/$PUB_DIR
RUNNER_PATH=$GITHUB_WORKSPACE/nethermind/src/Nethermind/Nethermind.Runner

cd $RUNNER_PATH

echo "Building Nethermind v$1+${2:0:8}"

for rid in "linux-x64" "linux-arm64" "win-x64" "osx-x64" "osx-arm64"
do
  echo "  Publishing for $rid"

  dotnet publish -c release -r $rid -o $OUTPUT_PATH/$rid --sc true \
    -p:BuildTimestamp=$3 \
    -p:Commit=$2 \
    -p:DebugType=none \
    -p:Deterministic=true \
    -p:IncludeAllContentForSelfExtract=true \
    -p:PublishSingleFile=true

  cp -r configs $OUTPUT_PATH/$rid
  mkdir $OUTPUT_PATH/$rid/keystore
done

cd ..
mkdir $OUTPUT_PATH/ref
cp **/obj/release/**/refint/*.dll $OUTPUT_PATH/ref

echo "Build completed"
