#!/usr/bin/env bash
# Run the EvmPooledMemory microbenchmark on several branches and print the results side by side.
# The benchmark uses only the public + InternalsVisibleTo EvmPooledMemory API, so the same source
# runs unchanged on master and on the memory branches; results are directly comparable.
#
# Usage: scripts/bench-evm-memory.sh [branch ...]
#   defaults to: master perf/evm-clear-on-grow
# Run from a clean working tree. The benchmark file is taken from the branch that owns it.
set -euo pipefail

BENCH_FILE="src/Nethermind/Nethermind.Evm.Benchmark/EvmMemoryBenchmarks.cs"
BENCH_OWNER="${BENCH_OWNER:-perf/evm-clear-on-grow}"      # branch that has the benchmark committed
FILTER='*EvmMemoryBenchmarks*'                            # job/toolchain are set in-code (in-process)
PROJ="src/Nethermind/Nethermind.Evm.Benchmark/Nethermind.Evm.Benchmark.csproj"
BRANCHES=("$@"); [ ${#BRANCHES[@]} -eq 0 ] && BRANCHES=(master perf/evm-clear-on-grow)

start_branch="$(git rev-parse --abbrev-ref HEAD)"
outdir="$(mktemp -d)"
cleanup() { git checkout -q "$start_branch" 2>/dev/null || true; git checkout -q -- "$BENCH_FILE" 2>/dev/null || true; }
trap cleanup EXIT

for br in "${BRANCHES[@]}"; do
  echo "=== $br ==="
  git checkout -q "$br"
  # Ensure the benchmark source is present even on branches that don't own it.
  git checkout -q "$BENCH_OWNER" -- "$BENCH_FILE"
  dotnet run -c release --project "$PROJ" -- --filter "$FILTER" 2>&1 \
    | tee "$outdir/${br//\//_}.txt" | grep -E '^\|' || true
  git checkout -q -- "$BENCH_FILE" 2>/dev/null || git clean -fq -- "$BENCH_FILE" 2>/dev/null || true
done

echo
echo "Full result tables saved under: $outdir"
