#!/bin/bash
# Ethereum.Blockchain.Pyspec.Test (Release) for one revision. Usage: pyspec.sh <rev> <tag>
export DOTNET_ROOT=/root/.dotnet PATH=$PATH:/root/.dotnet DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 MSBUILDDISABLENODEREUSE=1
R=/root/fusion-b3/repair; L=$R/logs; cd $R/nethermind; rev=$1; tag=$2
git checkout -q --detach $rev && echo "== $(date -u +%T) pyspec $tag at $(git log --oneline -1)"
p=Ethereum.Blockchain.Pyspec.Test
nice -n 10 dotnet build src/Nethermind/$p/$p.csproj -c Release -nr:false -p:UseSharedCompilation=false > $L/py-build-$tag.log 2>&1 || { echo "BUILD FAIL"; exit 1; }
( cd src/Nethermind/artifacts/bin/$p/release && nice -n 10 timeout 14400 dotnet $p.dll > $L/py-$tag.log 2>&1; echo "pyspec $tag exit=$? $(grep -E '^\s*(total|failed|succeeded|skipped):' $L/py-$tag.log | tr -s ' ' | tr '\n' ' ')" )
grep -E '^\s*failed ' $L/py-$tag.log | sed 's/ \[.*//' | sort > $L/py-$tag.failed; wc -l < $L/py-$tag.failed
echo "== $(date -u +%T) done $tag; df $(df -h / | tail -1 | awk '{print $4}')"
