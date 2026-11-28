#!/usr/bin/env bash
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only
#
# EIP-8288 builder aggregation benchmark: folds N leanSPHINCS claim witnesses into one 0x01 dependency proof with the
# production schedule (wide) and the former one (narrow), each BenchmarkDotNet case in a process
# of its own so each peak RSS is its own.
#
# Usage: scripts/eip8288-bench/builder.sh [--profile whole|local-block-builder] [--claims "16 64 256 500"] [--out DIR]
#
# Profiles, by EIP-7870 name:
#   whole                the whole machine
#   local-block-builder  EIP-7870 "Local Block Builder": 16 vCPU (8 cores / 16 threads) and 64 GiB, through a systemd scope
#
# Linux only (linux-x64, linux-arm64): the native Lean backend and its signing tool ship for those alone.
set -euo pipefail

profile=whole
claims="16 64 256 500"
out=eip8288-builder-results
while [[ $# -gt 0 ]]; do
  case "$1" in
    --profile) profile="$2"; shift 2 ;;
    --claims) claims="$2"; shift 2 ;;
    --out) out="$2"; shift 2 ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
out="$(mkdir -p "$out" && cd "$out" && pwd)"

case "$profile" in
  whole) scope=() ;;
  local-block-builder)
    cpus=16
    if (( $(nproc) < cpus )); then
      echo "local-block-builder needs $cpus vCPUs; this machine has $(nproc)" >&2
      exit 1
    fi
    scope=(systemd-run --quiet --scope -p "AllowedCPUs=0-$((cpus - 1))" -p MemoryMax=64G -p MemorySwapMax=0) ;;
  *) echo "unknown profile: $profile" >&2; exit 2 ;;
esac

# No build servers outlive the run: they would inherit, and hold, a lock the caller runs this under.
export MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_USE_MSBUILD_SERVER=0 UseSharedCompilation=false
dotnet build "$root/src/Nethermind/Nethermind.Benchmark.Runner" -c release -p:NethermindCryptoLeanTestAssets=true -v q -nologo
runner="$root/src/Nethermind/artifacts/bin/Nethermind.Benchmark.Runner/release/Nethermind.Benchmark.Runner"

export EIP8288_BENCH_RESULTS="$out" EIP8288_BENCH_PROFILE="$profile" EIP8288_BENCH_VECTORS="$(dirname "$runner")/lean-vectors"
# One build serves every case; BenchmarkDotNet still runs each (schedule, N) case in a process of its own.
EIP8288_BENCH_CLAIMS="${claims// /,}" "${scope[@]}" "$runner" --quick --artifacts "$out/bdn" \
  --filter "*DependencyProofAggregationBenchmarks*"

python3 - "$out/builder.jsonl" "$profile" <<'EOF'
import json, sys
rows = [json.loads(l) for l in open(sys.argv[1]) if json.loads(l)["profile"] == sys.argv[2]]
print(f"\nEIP-8288 builder aggregation, profile {sys.argv[2]} ({rows[0]['processors'] if rows else '?'} vCPU), slot 12 s, upload 50 Mbps")
print(f"{'N':>4} {'schedule':>8} {'prove ms':>9} {'cores':>6} {'peak RSS MiB':>12} {'calls':>5} "
      f"{'0x01 B':>8} {'0x00 B':>9} {'0x01 ms':>8} {'0x00 ms':>8} {'verify ms':>9}")
for r in sorted(rows, key=lambda r: (r["claims"], r["schedule"])):
    print(f"{r['claims']:>4} {r['schedule']:>8} {r['proveMs']:>9.0f} {r['cpuCores']:>6.1f} {r['peakRssMiB']:>12.0f} "
          f"{r['nativeCallsPerProof']:>5.0f} {r['aggregatedProofBytes']:>8} {r['rawProofBytes']:>9} "
          f"{r['aggregatedBroadcastMs']:>8.1f} {r['rawBroadcastMs']:>8.1f} {r['verifyMs']:>9.1f}")
EOF
