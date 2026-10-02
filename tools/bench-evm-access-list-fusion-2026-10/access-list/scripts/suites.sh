#!/bin/bash
export DOTNET_ROOT=/root/.dotnet PATH=$PATH:/root/.dotnet DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
L=/root/al2929-logs; cd /root/al2929/src/Nethermind
for p in Nethermind.Core.Test Nethermind.Evm.Test Nethermind.State.Test Nethermind.Blockchain.Test; do
  nice -n 10 dotnet build $p/$p.csproj -c Release > $L/s-build-$p.log 2>&1 || { echo "BUILD FAIL $p"; continue; }
done
for p in Nethermind.Evm.Test Nethermind.State.Test Nethermind.Core.Test; do
  nice -n 10 artifacts/bin/$p/release/$p > $L/s-test-$p.log 2>&1; echo "$p exit=$? $(grep -E '^  (total|failed|succeeded|skipped):' $L/s-test-$p.log | tr -s ' ' | tr '\n' ' ')"
done
p=Nethermind.Blockchain.Test
nice -n 10 artifacts/bin/$p/release/$p --filter "FullyQualifiedName~Eip7702|FullyQualifiedName~AccessTx|FullyQualifiedName~TransactionProcessor|FullyQualifiedName~Eip2930|FullyQualifiedName~Eip2929" > $L/s-test-$p.log 2>&1; echo "$p(filtered) exit=$? $(grep -E '^  (total|failed|succeeded|skipped):' $L/s-test-$p.log | tr -s ' ' | tr '\n' ' ')"
echo SUITES_DONE
