#!/usr/bin/env bash
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only
#
# Scenario B: builder-side aggregation of N mode-0 wrapper signatures into one dependency proof, in a container per profile;
# see tools/Eip8288Bench/README.md. Usage:
#
#   builder.sh <out dir> <profile> [docker resource flags...]
#
# e.g. `builder.sh out builder` for the whole host, `builder.sh out average --cpuset-cpus=0-7 --memory=16g`.
# Settings from the environment: COUNTS [20,100,200,300,500], WARMUPS [1], REPEATS [5], SLOT_MS [12000], RESERVE_MS [4000],
# BENCH_BIN (published Eip8288Bench directory), POOL (signature pool, shared across profiles).
set -euo pipefail

out=$(realpath -m "$1"); profile=$2; shift 2
: "${BENCH_BIN:?BENCH_BIN must point at the published Eip8288Bench}"
POOL=$(realpath -m "${POOL:-$out/signatures.bin}")
mkdir -p "$out/$profile" "$(dirname "$POOL")"

status=0
docker run --rm --name "eip8288-bench-builder-$profile" "$@" -v "$BENCH_BIN:/bench:ro" -v "$out/$profile:/out" \
  -v "$(dirname "$POOL"):/pool" -e BENCH_NETHERMIND_COMMIT -e BENCH_LEAN_PACKAGE -e BENCH_MACHINE_TYPE \
  mcr.microsoft.com/dotnet/runtime:10.0 dotnet /bench/Eip8288Bench.dll builder --out=/out --profile="$profile" \
  --pool="/pool/$(basename "$POOL")" --counts="${COUNTS:-20,100,200,300,500}" --warmups="${WARMUPS:-1}" \
  --repeats="${REPEATS:-5}" --slot-ms="${SLOT_MS:-12000}" --reserve-ms="${RESERVE_MS:-4000}" 2>&1 | tee "$out/$profile/builder.log" \
  || status=$?
# 137 is the kernel's OOM kill under the profile's memory limit; the sets that completed are already written.
(( status == 0 )) || echo "builder profile $profile exited with $status" | tee -a "$out/$profile/builder.log"
[[ ! -f $out/$profile/builder.md ]] || cat "$out/$profile/builder.md"
exit "$status"
