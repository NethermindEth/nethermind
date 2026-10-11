#!/bin/bash
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only
# The EIP-8437 RLPx-vs-ethp2p measurement campaign: variants, the full matrix, targeted extras and a second repetition
# with the mode order reversed. Every phase resumes from its results file. Needs root (network namespaces), tc, ethtool,
# libmsquic and the release build of Nethermind.Network.Test.
#   OUT=/root/lean-bench DOTNET=dotnet scripts/lean-net-bench/campaign.sh [phase ...]   (phases: variants rep2 extras rep3)
set -u
HERE=$(cd "$(dirname "$0")" && pwd)
ROOT=$(cd "$HERE/../.." && pwd)
OUT=${OUT:-$PWD}
DOTNET=${DOTNET:-dotnet}
MAIN=$ROOT/src/Nethermind/artifacts/bin/Nethermind.Network.Test/release/Nethermind.Network.Test.dll
V=$OUT/variants
SEQ=$V/seqfix/Nethermind.Network.Test.dll
matrix() { python3 "$HERE/matrix.py" --dotnet "$DOTNET" "$@"; }
phases=${*:-variants rep2 extras rep3}
for phase in $phases; do
  echo "== $phase $(date -u +%FT%TZ) load $(cut -d' ' -f1-3 /proc/loadavg)"
  case $phase in
    variants)
      python3 "$HERE/variants.py" --dotnet "$DOTNET" --out "$V" seqfix seqfix+inflight16 seqfix+window4m ;;
    rep2)
      matrix --test-dll "$SEQ" --results "$OUT/results-rep2.jsonl" --out "$OUT/runs-rep2" --suffix=-rep2 ;;
    extras)
      E="--results $OUT/results-extras.jsonl --out $OUT/runs-extras"
      # The pull request as it is (request IDs can reach the link out of order), redialing dropped peers.
      matrix --test-dll "$MAIN" $E --suffix=-pr --topologies chain4 --rtts 50,100 --losses 1
      matrix --test-dll "$SEQ" $E --suffix=-v20 --extra "--verify-ms 20" --topologies chain4,cube8 --rtts 50 --losses 0 --workloads k1s,k2,k3
      matrix --test-dll "$V/seqfix+inflight16/Nethermind.Network.Test.dll" $E --suffix=-if16 --topologies chain4,cube8 --rtts 20,50,100 --losses 0,1 \
        --workloads k2,k3,k3b --modes bcast
      matrix --test-dll "$V/seqfix+window4m/Nethermind.Network.Test.dll" $E --suffix=-w4m --topologies chain4 --rtts 50,100 --losses 0,1 \
        --workloads k1l,k3 --modes ethp2p,bcast
      matrix --test-dll "$SEQ" $E --suffix=-ssai0 --extra "--sysctl net.ipv4.tcp_slow_start_after_idle=0" --topologies chain4 --rtts 50,100 \
        --losses 0,1 --workloads k1s,k1l,k3 --modes rlpx
      matrix --test-dll "$SEQ" $E --suffix=-b1 --topologies pair,chain4 --rtts 0,50 --losses 0 --workloads k3b1 ;;
    rep3)
      matrix --test-dll "$SEQ" --results "$OUT/results-rep3.jsonl" --out "$OUT/runs-rep3" --suffix=-rep3 --topologies chain4,cube8 \
        --rtts 0,50,100 --modes bcast,ethp2p,rlpx ;;
  esac
done
python3 "$HERE/netbench.py" teardown
echo "== done $(date -u +%FT%TZ)"
