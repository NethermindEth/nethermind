#!/bin/bash
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

current_dir="$(pwd)"
parent_dir=$(dirname "${current_dir}")

# Build target image
docker build --platform linux/amd64 -t bflat-tmp .

if [ "$?" != "0" ]; then
  echo "Docker build failed"
  exit 1
fi

# Run target bflat-tmp image with parameters provided to the current script.
time docker run \
  --platform linux/amd64 \
  --rm \
  --cap-add=SYS_PTRACE \
  --security-opt seccomp=unconfined \
  --security-opt apparmor=unconfined \
  -e DOTNET_TYPELOADER_TRACE_INTERFACE_RESOLUTION=0 \
  #--mount type=bind,source="$parent_dir/artifacts/bin/StatelessExecution/release",target=/nethermind/bin \
  --mount type=bind,source="$current_dir",target=/nethermind/src \
  -w /nethermind/src \
  bflat-tmp "$@"

exit $?
