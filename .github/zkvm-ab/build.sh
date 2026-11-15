#!/usr/bin/env bash
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

# Builds the guest of <checkout> for every format and copies the binaries to <out>.
# Usage: build.sh <guest> <project> <checkout> <out>
# Environment: FORMATS, BFLAT_IMAGE, BFLAT_RISCV32_IMAGE, BFLAT_FLAGS, EXTLIB_PATH, CANDIDATE_DIR.
set -euo pipefail

guest=$1
dir="$(realpath "$3")/src/Nethermind/$2"
out=$(realpath -m "$4")
args=()

image=$BFLAT_IMAGE
[ "$guest" != airbender ] || image=$BFLAT_RISCV32_IMAGE
[ -z "$image" ] || args+=("BFLAT_IMAGE=$image")
flags=$BFLAT_FLAGS
extlib=$EXTLIB_PATH
unset BFLAT_IMAGE BFLAT_RISCV32_IMAGE BFLAT_FLAGS EXTLIB_PATH

if [ -n "$flags" ]; then
  grep -q '^BFLAT_EXTRA_FLAGS ?=' "$dir/../Nethermind.Stateless.Guest/build.mk" || \
    { echo "::error::$3 has no BFLAT_EXTRA_FLAGS in build.mk, so codegenopt cannot reach its guest" >&2; exit 1; }
  args+=("BFLAT_EXTRA_FLAGS=$flags")
fi

if [ -n "$extlib" ]; then
  candidate=$(realpath "$CANDIDATE_DIR")
  if [ "$guest" = airbender ]; then
    args+=("AIRBENDER_LIB_DIR=$(dirname "$candidate/$extlib")")
  else
    args+=("GUEST_EXTLIB=/candidate/$extlib" "BFLAT_DOCKER_ARGS=--mount type=bind,source=\"$candidate\",target=/candidate,readonly")
  fi
fi

read -ra format_list <<< "${FORMATS//,/ }"
for format in "${format_list[@]}"; do
  make -C "$dir" build GUEST_INPUT="$format" "${args[@]}"
done

mkdir -p "$out"
cp "$dir"/bin/nethermind* "$out/"
sha256sum "$out"/*
