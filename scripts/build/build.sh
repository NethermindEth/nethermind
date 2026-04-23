#!/bin/bash
# SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

set -e

build_config=release
output_path=/nethermind/output

cd src/Nethermind/Nethermind.Runner

echo "Building Nethermind"

# Pass PublishReadyToRun so the SDK fetches R2R platform packs (crossgen2,
# runtime packs) for every RID listed in the csproj's <RuntimeIdentifiers>.
# Staying in --locked-mode preserves reproducibility — platform packs are
# determined by the pinned SDK image, not the lock file.
dotnet restore --locked-mode -p:PublishReadyToRun=true

for rid in "linux-arm64" "linux-x64" "osx-arm64" "osx-x64" "win-x64"; do
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
