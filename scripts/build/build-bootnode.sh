#!/bin/bash
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

set -euo pipefail

build_config=${BUILD_CONFIG:-release}
repo_root=$(git rev-parse --show-toplevel)
output_path=${PUB_DIR:-$repo_root/bootnode-pub}
commit_hash=${1:-${COMMIT_HASH:-}}
project=tools/Bootnode/Nethermind.Bootnode/Nethermind.Bootnode.csproj

cd "$repo_root"

echo "Building Nethermind.Bootnode"

mkdir -p "$output_path"
dotnet restore "$project" -p:SaveDiskSpace=true

for rid in "linux-arm64" "linux-x64" "osx-arm64" "osx-x64" "win-x64"; do
  echo "  Publishing for $rid"

  rid_output=$output_path/$rid
  dotnet publish "$project" \
    -c "$build_config" \
    -r "$rid" \
    -o "$rid_output" \
    --no-restore \
    --self-contained true \
    -p:SaveDiskSpace=true \
    -p:DebugType=embedded \
    -p:IncludeAllContentForSelfExtract=true \
    -p:PublishSingleFile=true \
    ${commit_hash:+-p:SourceRevisionId=$commit_hash}

  cp LICENSE-LGPL LICENSE-GPL "$rid_output"
  cp tools/Bootnode/README.md "$rid_output"
  cp -R tools/Bootnode/observability "$rid_output"

  if [[ "$rid" == linux-* || "$rid" == osx-* ]]; then
    ln -sf Nethermind.Bootnode "$rid_output/bootnode"
  fi
done

echo "Build completed"
