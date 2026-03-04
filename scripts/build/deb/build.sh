#!/bin/bash
# SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

set -e

export SOURCE_DATE_EPOCH=$(git log -1 --pretty=%ct)

build_output=.artifacts
package_output=${1:-.}

mkdir -p $build_output

docker build . -t nethermind-build -f scripts/build/Dockerfile \
  --build-arg COMMIT_HASH=$(git rev-parse HEAD) \
  --build-arg SOURCE_DATE_EPOCH=$SOURCE_DATE_EPOCH

docker run --rm --mount type=bind,source=$build_output,target=/output nethermind-build

docker build . -t nethermind-deb -f scripts/build/deb/Dockerfile \
  --build-arg ARTIFACTS=$build_output \
  --build-arg SOURCE_DATE_EPOCH=$SOURCE_DATE_EPOCH

docker run --rm --mount "type=bind,source=$package_output,target=/output" nethermind-deb

echo "Copied nethermind.deb to $package_output"

rm -rf $build_output
