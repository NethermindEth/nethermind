#!/usr/bin/env bash
# Runs the Nethermind.Network.Benchmark benchmarks of one checkout through Nethermind.Benchmark.Runner.
# Usage: run.sh <checkout> <artifacts directory>, with the BenchmarkDotNet filter patterns in FILTER.
set -euo pipefail

checkout="$1"
artifacts="$2"

# Nethermind.Benchmark.Runner enumerates these directories at startup for the precompile benchmarks,
# so they have to exist even when none of those are selected.
inputs_root="${checkout}/src/Nethermind/artifacts/bin/Nethermind.Benchmark.Runner/release"
for d in blake2f blsg1add blsg1msm blsg1mul blsg2add blsg2msm blsg2mul blsmapfp2tog2 \
         blsmapfptog1 blspairingcheck bnadd bnmul bnpair ec_recover modexp \
         point_evaluation ripemd sha256; do
  mkdir -p "${inputs_root}/${d}/current"
  [[ -f "${inputs_root}/${d}/current/sample.csv" ]] || echo "00" > "${inputs_root}/${d}/current/sample.csv"
done

read -ra patterns <<< "${FILTER}"
cd "${checkout}/src/Nethermind/Nethermind.Benchmark.Runner"
dotnet run -c Release -- --filter "${patterns[@]}" --join --buildTimeout 900 --artifacts "${artifacts}"
