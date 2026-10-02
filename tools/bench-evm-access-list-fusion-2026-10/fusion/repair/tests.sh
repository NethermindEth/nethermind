#!/bin/bash
# Repair verification on build3, in /root/fusion-b3/repair/nethermind at the repair head:
# full Nethermind.Evm.Test (Release), fusion/harness/cancellation tests (Debug), dotnet format whitespace on the changed file.
export DOTNET_ROOT=/root/.dotnet PATH=$PATH:/root/.dotnet DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 MSBUILDDISABLENODEREUSE=1
R=/root/fusion-b3/repair; L=$R/logs; cd $R/nethermind
git fetch -q /root/fusion-bench/repair/fix.bundle perf/evm-compare-branch-fusion:repair 2>/dev/null || true
git checkout -q repair && git log --oneline -1
P=src/Nethermind/Nethermind.Evm.Test/Nethermind.Evm.Test.csproj
FLT='FullyQualifiedName~HostCompareBranchFusionTests|FullyQualifiedName~HostMemoryFastPathTests|FullyQualifiedName~Cancellation'
sum() { grep -E '^\s*(total|failed|succeeded|skipped):' $1 | tr -s ' ' | tr '\n' ' '; }
nice -n 10 dotnet build $P -c Release -nr:false -p:UseSharedCompilation=false > $L/build-rel.log 2>&1 || { echo "REL BUILD FAIL"; grep -E " error " $L/build-rel.log | sort -u | head; exit 1; }
( cd src/Nethermind/artifacts/bin/Nethermind.Evm.Test/release && nice -n 10 timeout 3600 dotnet Nethermind.Evm.Test.dll > $L/full-rel.log 2>&1; echo "full Release exit=$? $(sum $L/full-rel.log)" )
nice -n 10 dotnet build $P -c Debug -nr:false -p:UseSharedCompilation=false > $L/build-dbg.log 2>&1 || { echo "DBG BUILD FAIL"; exit 1; }
( cd src/Nethermind/artifacts/bin/Nethermind.Evm.Test/debug && nice -n 10 timeout 3600 dotnet Nethermind.Evm.Test.dll --filter "$FLT" > $L/fusion-dbg.log 2>&1; echo "fusion Debug exit=$? $(sum $L/fusion-dbg.log)" )
nice -n 10 dotnet format whitespace src/Nethermind --folder --verify-no-changes --include src/Nethermind/Nethermind.Evm/VirtualMachine.HostHandlers.std.cs > $L/format.log 2>&1; echo "format exit=$? $(tail -2 $L/format.log | tr '\n' ' ')"
echo TESTS_DONE
