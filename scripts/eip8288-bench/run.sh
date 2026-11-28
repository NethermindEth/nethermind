#!/usr/bin/env bash
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only
#
# Runs the EIP-8288 mempool benchmark from a Nethermind checkout on one Linux host with docker; see tools/Eip8288Bench/README.md.
#
#   run.sh <out dir>
#
# Environment: MEMPOOL_RUNS [3] runs per arm and profile, ARMS [Mempool,None] Blocks.DependencyProofAggregation values compared,
# PROFILES [full-node,attester] EIP-7870 node profiles, CPU_SCALE and MEMORY_SCALE [fit] the share of each profile's CPU and
# memory a node gets (`fit` sizes both profiles to what this host holds), plus the variables mempool.sh reads.
set -euo pipefail

out=$(realpath -m "$1")
root=$(git -C "$(dirname "$0")" rev-parse --show-toplevel)
ARMS=${ARMS:-Mempool,None} PROFILES=${PROFILES:-full-node,attester} NODES=${NODES:-4} LOAD_CPUS=${LOAD_CPUS:-1}
export NODES LOAD_CPUS
mkdir -p "$out"
arch=$(uname -m); case $arch in x86_64) rid=linux-x64 ;; aarch64) rid=linux-arm64 ;; *) echo "unsupported $arch"; exit 1 ;; esac

BENCH_NETHERMIND_COMMIT=$(git -C "$root" rev-parse HEAD)
BENCH_LEAN_PACKAGE=$(sed -nE 's/.*Include="Nethermind.Crypto.Lean" Version="([^"]+)".*/\1/p' "$root/Directory.Packages.props")
export BENCH_NETHERMIND_COMMIT BENCH_LEAN_PACKAGE BENCH_BIN=$out/bin POOL=$out/signatures.bin
{
  echo "commit $BENCH_NETHERMIND_COMMIT"; echo "lean package $BENCH_LEAN_PACKAGE"; echo "machine ${BENCH_MACHINE_TYPE:-unknown}"
  nproc; free -g; lscpu
} > "$out/host.txt"

# EIP-7870 hardware: name, threads, memory (GiB), upload (Mbit/s). Both download at 50 Mbit/s, which egress-only shaping
# leaves unmodelled: every packet is shaped once, at its sender's upload.
declare -A threads=([attester]=16 [full-node]=8) memory=([attester]=64 [full-node]=32) upload=([attester]=25 [full-node]=15)
declare -A label=([attester]="Attester" [full-node]="Full Node")
host_cpus=$(nproc); host_mib=$(( $(awk '/MemAvailable/ { print $2 }' /proc/meminfo) / 1024 ))
# `fit` gives every profile the same share of its EIP-7870 CPU, and of its memory, the largest the host holds for NODES
# nodes of the biggest profile, keeping a quarter of the memory for the host and the load generator. The two shares differ
# on a host whose memory is scarcer than its CPU, but the profiles keep their ratio to each other.
[[ ${CPU_SCALE:-fit} == fit ]] && CPU_SCALE=$(awk -v c="$host_cpus" -v l="$LOAD_CPUS" -v n="$NODES" 'BEGIN { printf "%.4f", (c - l) / (n * 16) }')
[[ ${MEMORY_SCALE:-fit} == fit ]] && MEMORY_SCALE=$(awk -v m="$host_mib" -v n="$NODES" 'BEGIN { printf "%.4f", m * 0.75 / (n * 64 * 1024) }')
SCALE="CPU x $CPU_SCALE, memory x $MEMORY_SCALE"
echo "scale $SCALE" >> "$out/host.txt"

mkdir -p "$BENCH_BIN"
docker run --rm --user "$(id -u):$(id -g)" -e HOME=/tmp -e DOTNET_CLI_HOME=/tmp -v "$root:/src" -v "$BENCH_BIN:/bin-out" -w /src \
  mcr.microsoft.com/dotnet/sdk:11.0 \
  dotnet publish tools/Eip8288Bench/Eip8288Bench.csproj -c Release -r "$rid" --self-contained false -o /bin-out
docker build -t nethermind:eip8288-bench-head --build-arg COMMIT_HASH="$BENCH_NETHERMIND_COMMIT" "$root"

IFS=, read -ra arms <<< "$ARMS"; IFS=, read -ra profiles <<< "$PROFILES"
for profile in "${profiles[@]}"; do
  NODE_CPUS=$(awk -v t="${threads[$profile]}" -v s="$CPU_SCALE" 'BEGIN { printf "%.2f", t * s }')
  NODE_MEMORY=$(awk -v g="${memory[$profile]}" -v s="$MEMORY_SCALE" 'BEGIN { printf "%dm", g * 1024 * s }')
  export PROFILE="${label[$profile]}" NODE_CPUS NODE_MEMORY UP_MBIT=${upload[$profile]}
  # Arms alternate run by run, so drift on the host does not land on one arm only.
  for ((run = 1; run <= ${MEMPOOL_RUNS:-3}; run++)); do
    for arm in "${arms[@]}"; do
      AGGREGATION=$arm "$root/scripts/eip8288-bench/mempool.sh" "$out/mempool/$profile/$arm/run$run" nethermind:eip8288-bench-head "$profile-$arm-run$run"
    done
  done
  for arm in "${arms[@]}"; do
    dirs=$(find "$out/mempool/$profile/$arm" -mindepth 1 -maxdepth 1 -type d -name 'run*' | sort | sed "s|^$out/mempool|/mempool|" | paste -sd, -)
    docker run --rm --network none -v "$BENCH_BIN:/bench:ro" -v "$out/mempool:/mempool" mcr.microsoft.com/dotnet/runtime:11.0 \
      dotnet /bench/Eip8288Bench.dll aggregate --dirs="$dirs" --label="${label[$profile]} (EIP-7870, $SCALE), $arm" --out="/mempool/$profile/$arm"
  done
done

{
  echo "# EIP-8288 mempool benchmark"; echo
  echo "Nethermind \`$BENCH_NETHERMIND_COMMIT\`, Nethermind.Crypto.Lean $BENCH_LEAN_PACKAGE, machine ${BENCH_MACHINE_TYPE:-unknown}, $host_cpus vCPUs, EIP-7870 profiles at $SCALE."
  for f in "$out"/mempool/*/*/aggregate.md "$out"/mempool/*/*/run*/summary.md; do
    if [[ -f $f ]]; then echo; cat "$f"; fi
  done
} > "$out/summary.md"
cat "$out/summary.md"
