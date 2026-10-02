#!/bin/bash
# build3: Release build of the touched test projects, then run them. Logs in /root/al2929-logs.
export DOTNET_ROOT=/root/.dotnet PATH=$PATH:/root/.dotnet DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
L=/root/al2929-logs; mkdir -p $L
cd /root/al2929/src/Nethermind
tag=${1:-run}
for p in Nethermind.Core.Test Nethermind.Evm.Test Nethermind.State.Test Nethermind.Evm.Benchmark; do
  nice -n 10 dotnet build $p/$p.csproj -c Release > $L/$tag-build-$p.log 2>&1
  echo "build $p exit=$? $(grep -E 'error|Warn' $L/$tag-build-$p.log | grep -cE ' error ')" 
done
for p in ${TESTS:-Nethermind.Core.Test Nethermind.Evm.Test Nethermind.State.Test}; do
  exe=$(ls $p/bin/Release/*/$p 2>/dev/null | head -1); [ -z "$exe" ] && exe=$(ls artifacts/bin/$p/release*/$p 2>/dev/null | head -1)
  echo "exe $p: $exe"
done
