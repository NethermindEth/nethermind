#!/bin/bash
# Usage: disasm.sh <bin-dir-suffix> <chains> <outtag>   (env JITPAT overrides the method filter)
. /root/fusion-bench/env.sh
D=/root/fusion-bench/repair; b=$1; chains=$2; tag=$3
cd /root/fusion-bench/bin-$b
rm -f $D/jit-$tag.txt
OPCODE_CHAINS=$chains DOTNET_JitStdOutFile=$D/jit-$tag.txt DOTNET_JitDisasmSummary=1 \
DOTNET_JitDisasm="${JITPAT:-OpIsZero OpNot OpLt OpEq ExecuteCompareBranch ExecuteFusedBranch OpPop OpDup1 OpPush2 OpJumpI OpJumpDest OpDiv OpReturnDataSize OpPrevRandao}" \
  nice -n 10 taskset -c 20 dotnet Nethermind.Evm.Benchmark.dll --filter "*OpcodeChainBenchmarks*" --inProcess --job Dry \
  --artifacts $D/bdn-$tag > $D/run-$tag.log 2>&1
echo "$tag exit $? $(grep -c "Assembly listing" $D/jit-$tag.txt) listings"
