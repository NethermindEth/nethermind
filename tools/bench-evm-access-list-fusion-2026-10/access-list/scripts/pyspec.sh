#!/bin/bash
# Pyspec state + blockchain tests for the forks whose EIPs touch access lists (Berlin: 2929/2930; Prague: 7702; Osaka; Amsterdam: 8038/7928).
export DOTNET_ROOT=/root/.dotnet PATH=$PATH:/root/.dotnet DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
L=/root/al2929-logs; cd /root/al2929/src/Nethermind; git log --oneline -1
p=Ethereum.Blockchain.Pyspec.Test
nice -n 10 dotnet build $p/$p.csproj -c Release > $L/py-build.log 2>&1 || { echo "BUILD FAIL"; exit 1; }
exe=artifacts/bin/$p/release/$p
for f in BerlinStateTests PragueStateTests OsakaStateTests BerlinBlockchainTests; do
  nice -n 10 $exe --filter "FullyQualifiedName~.$f." > $L/py-$f.log 2>&1
  echo "$f exit=$? $(grep -E '^  (total|failed|succeeded|skipped):' $L/py-$f.log | tr -s ' ' | tr '\n' ' ')"
done
echo PYSPEC_DONE
