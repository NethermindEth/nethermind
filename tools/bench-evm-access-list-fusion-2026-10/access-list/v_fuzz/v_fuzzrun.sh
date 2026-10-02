#!/bin/bash
# usage: fuzzrun.sh <worktree> <outname> <N>
export DOTNET_ROOT=/root/.dotnet PATH=$PATH:/root/.dotnet DOTNET_CLI_TELEMETRY_OPTOUT=1
W=$1; O=/root/al2929v/fz-out/$2; mkdir -p $O
cd /root/al2929v/$W/src/Nethermind/artifacts/bin/Nethermind.Evm.Test/release
ALFUZZ_N=$3 ALFUZZ_OUT=$O timeout 3600 nice -n 10 ./Nethermind.Evm.Test --filter "FullyQualifiedName~AlDiffFuzz" > $O/run.log 2>&1
echo "rc=$? $(date -u +%FT%TZ)" >> $O/run.log
