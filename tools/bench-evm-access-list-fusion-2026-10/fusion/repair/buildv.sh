#!/bin/bash
# Builds the benchmark from the fusion head plus a patch into bin-<name>. Usage: buildv.sh <name> <patch|none> [<sha>]
set -euo pipefail
. /root/fusion-bench/env.sh
B=/root/fusion-bench; R=$B/nethermind; name=$1; patch=$2; sha=${3:-e4d189016857c611186f7435e32aa52ddd23fe54}
cd $R
F=src/Nethermind/Nethermind.Evm.Benchmark/OpcodeChainBenchmarks.cs
git stash list | head -2
# keep the bench-file tweak, reset everything else
cp $F /tmp/obench.cs
git checkout -q -f --detach $sha
cp /tmp/obench.cs $F
[ "$patch" != none ] && git apply $patch
[ "${REFRESH:-0}" = 1 ] && python3 /root/fusion-bench/repair/refreshpatch.py $F
echo "== $(date -u +%T) build $name $(git rev-parse --short=10 HEAD) $(git status --short | paste -sd' ')"
nice -n 10 dotnet build src/Nethermind/Nethermind.Evm.Benchmark/Nethermind.Evm.Benchmark.csproj -c Release -nr:false -p:UseSharedCompilation=false > $B/repair/build-$name.log 2>&1 || { grep -E "error" $B/repair/build-$name.log | sort -u | head; exit 1; }
tail -3 $B/repair/build-$name.log
rm -rf $B/bin-$name; cp -a src/Nethermind/artifacts/bin/Nethermind.Evm.Benchmark/release $B/bin-$name
echo "$name $(git rev-parse HEAD) + $patch $( [ $patch != none ] && sha256sum $patch | cut -c1-12)" > $B/bin-$name/BUILT_FROM
git diff --stat
# restore tree to the fusion head with only the bench tweak
git checkout -q -- src/Nethermind/Nethermind.Evm
cp /tmp/obench.cs $F
echo "== $(date -u +%T) done $name: $(cat $B/bin-$name/BUILT_FROM)"
