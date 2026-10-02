#!/bin/bash
export DOTNET_ROOT=/root/.dotnet PATH=$PATH:/root/.dotnet DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
L=/root/al2929-logs; cd /root/al2929/src/Nethermind; git log --oneline -1; git status --short | wc -l
for p in Nethermind.Evm.Test Nethermind.Core.Test; do nice -n 10 dotnet build $p/$p.csproj -c Release > $L/h-build-$p.log 2>&1 || echo "BUILD FAIL $p"; done
p=Nethermind.Evm.Test; nice -n 10 artifacts/bin/$p/release/$p > $L/h-test-$p.log 2>&1; echo "$p exit=$? $(grep -E '^  (total|failed|succeeded|skipped):' $L/h-test-$p.log | tr -s ' ' | tr '\n' ' ')"
p=Nethermind.Core.Test; nice -n 10 artifacts/bin/$p/release/$p --filter "FullyQualifiedName~JournalSet" > $L/h-test-$p.log 2>&1; echo "$p(JournalSet) exit=$? $(grep -E '^  (total|failed|succeeded|skipped):' $L/h-test-$p.log | tr -s ' ' | tr '\n' ' ')"
cd /root/al2929 && nice -n 10 dotnet format whitespace src/Nethermind/ --folder --verify-no-changes > $L/h-format.log 2>&1; echo "format exit=$?"
echo EVMHEAD_DONE
