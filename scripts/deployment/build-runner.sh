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
    -p:Deterministic=true \
    -p:IncludeAllContentForSelfExtract=true \
    -p:ProduceReferenceAssembly=true \
    -p:PublishSingleFile=true

  rm -rf $OUTPUT_PATH/$rid/*.pdb
  rm -rf $OUTPUT_PATH/$rid/Data/*
  rm -rf $OUTPUT_PATH/$rid/Hive
  cp -r configs $OUTPUT_PATH/$rid
  cp Data/static-nodes.json $OUTPUT_PATH/$rid/Data
  mkdir $OUTPUT_PATH/$rid/keystore
done

cd ..
mkdir $OUTPUT_PATH/ref
cp **/obj/release/**/ref/*.dll $OUTPUT_PATH/ref

echo "Build completed"
