#!/usr/bin/env bash
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only
#
# Runs the EIP-8288 benchmarks from a Nethermind checkout on one Linux host with docker; see tools/Eip8288Bench/README.md.
#
#   run.sh <out dir> <scenarios>
#
# scenarios: comma-separated `builder`, `mempool`. Environment: MEMPOOL_RUNS [3] runs per mempool arm, AVERAGE_CPUS [8] and AVERAGE_MEMORY [16g] for the
# average-validator profile, BASELINE_REF [feature/eip8288-eth75], the NethermindEth/nethermind branch or
# tag built for the eth/75 arm of the mempool comparison (empty to skip it), plus the variables builder.sh and mempool.sh read.
set -euo pipefail

out=$(realpath -m "$1"); scenarios=$2
root=$(git -C "$(dirname "$0")" rev-parse --show-toplevel)
AVERAGE_CPUS=${AVERAGE_CPUS:-8} AVERAGE_MEMORY=${AVERAGE_MEMORY:-16g}
BASELINE_REF=${BASELINE_REF-feature/eip8288-eth75}
mkdir -p "$out"
arch=$(uname -m); case $arch in x86_64) rid=linux-x64 ;; aarch64) rid=linux-arm64 ;; *) echo "unsupported $arch"; exit 1 ;; esac

BENCH_NETHERMIND_COMMIT=$(git -C "$root" rev-parse HEAD)
BENCH_LEAN_PACKAGE=$(sed -nE 's/.*Include="Nethermind.Crypto.Lean" Version="([^"]+)".*/\1/p' "$root/Directory.Packages.props")
export BENCH_NETHERMIND_COMMIT BENCH_LEAN_PACKAGE BENCH_BIN=$out/bin
{
  echo "commit $BENCH_NETHERMIND_COMMIT"; echo "lean package $BENCH_LEAN_PACKAGE"; echo "machine ${BENCH_MACHINE_TYPE:-unknown}"
  nproc; free -g; lscpu
} > "$out/host.txt"

# Published once in the SDK container, so the host needs only docker.
mkdir -p "$BENCH_BIN"
docker run --rm --user "$(id -u):$(id -g)" -e HOME=/tmp -e DOTNET_CLI_HOME=/tmp -v "$root:/src" -v "$BENCH_BIN:/bin-out" -w /src \
  mcr.microsoft.com/dotnet/sdk:10.0 \
  dotnet publish tools/Eip8288Bench/Eip8288Bench.csproj -c Release -r "$rid" --self-contained false -o /bin-out

if [[ ,$scenarios, == *,builder,* ]]; then
  export POOL=$out/signatures.bin
  "$root/scripts/eip8288-bench/builder.sh" "$out/builder" builder || true
  "$root/scripts/eip8288-bench/builder.sh" "$out/builder" average --cpuset-cpus="0-$(( AVERAGE_CPUS - 1 ))" --memory="$AVERAGE_MEMORY" || true
fi

if [[ ,$scenarios, == *,mempool,* ]]; then
  export POOL=$out/signatures.bin NODE_CPUS=${NODE_CPUS:-$AVERAGE_CPUS} NODE_MEMORY=${NODE_MEMORY:-$AVERAGE_MEMORY}
  docker build -t nethermind:eip8288-bench-head --build-arg COMMIT_HASH="$BENCH_NETHERMIND_COMMIT" "$root"
  arms=(eth76)
  if [[ -n $BASELINE_REF ]]; then
    baseline=$out/baseline-src
    rm -rf "$baseline"
    git -C "$root" worktree prune
    # By branch or tag name only: a commit SHA could name a fork-only commit, which GitHub serves through upstream too.
    upstream=https://github.com/NethermindEth/nethermind.git
    git -C "$root" fetch -q --no-tags "$upstream" "refs/heads/$BASELINE_REF" 2>/dev/null ||
      git -C "$root" fetch -q --no-tags "$upstream" "refs/tags/$BASELINE_REF"
    git -C "$root" worktree add --detach "$baseline" FETCH_HEAD
    git -C "$baseline" rev-parse HEAD > "$out/baseline-commit.txt"
    docker build -t nethermind:eip8288-bench-baseline --build-arg COMMIT_HASH="$(cat "$out/baseline-commit.txt")" "$baseline"
    git -C "$root" worktree remove --force "$baseline"
    arms+=(eth75)
  fi
  # Arms alternate run by run, so drift on the host does not land on one arm only.
  for ((run = 1; run <= ${MEMPOOL_RUNS:-3}; run++)); do
    for arm in "${arms[@]}"; do
      image=nethermind:eip8288-bench-head; [[ $arm == eth75 ]] && image=nethermind:eip8288-bench-baseline
      "$root/scripts/eip8288-bench/mempool.sh" "$out/mempool/$arm/run$run" "$image" "$arm-run$run"
    done
  done
  for arm in "${arms[@]}"; do
    dirs=$(find "$out/mempool/$arm" -mindepth 1 -maxdepth 1 -type d -name 'run*' | sort | sed "s|^$out/mempool|/mempool|" | paste -sd, -)
    docker run --rm --network none -v "$BENCH_BIN:/bench:ro" -v "$out/mempool:/mempool" mcr.microsoft.com/dotnet/runtime:10.0 \
      dotnet /bench/Eip8288Bench.dll aggregate --dirs="$dirs" --label="$arm" --out="/mempool/$arm"
  done
fi

{
  echo "# EIP-8288 benchmarks"; echo
  echo "Nethermind \`$BENCH_NETHERMIND_COMMIT\`, Nethermind.Crypto.Lean $BENCH_LEAN_PACKAGE, machine ${BENCH_MACHINE_TYPE:-unknown}, $(nproc) vCPUs."
  for f in "$out"/builder/*/builder.md "$out"/mempool/*/aggregate.md "$out"/mempool/*/run*/summary.md; do
    if [[ -f $f ]]; then echo; cat "$f"; fi
  done
} > "$out/summary.md"
cat "$out/summary.md"
