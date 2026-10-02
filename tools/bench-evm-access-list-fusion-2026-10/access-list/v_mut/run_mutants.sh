#!/bin/bash
# Runs each verifier mutant against the repo's tests, then the probe and the fuzz.
export DOTNET_ROOT=/root/.dotnet PATH=$PATH:/root/.dotnet DOTNET_CLI_TELEMETRY_OPTOUT=1
M=/root/al2929v/mut
B=$M/src/Nethermind/artifacts/bin
L=/root/al2929v/logs/mut
mkdir -p $L /root/al2929v/fz-out
EVMF="FullyQualifiedName~EthereumGasPolicyTests|FullyQualifiedName~Eip2929|FullyQualifiedName~Eip2930|FullyQualifiedName~Eip3651|FullyQualifiedName~Eip7702|FullyQualifiedName~Eip1153|FullyQualifiedName~Eip3529"
summ() { grep -E "^ *(total|failed|succeeded|skipped):" $1 | tr -s ' ' | tr '\n' ' '; }
for id in ${@:-$(python3 /root/al2929v/mutate.py list)}; do
  echo "=== $id $(date -u +%FT%TZ)"
  git -C $M checkout -q -- .
  python3 /root/al2929v/mutate.py $id $M || continue
  git -C $M diff --stat | tail -1
  cp /root/al2929v/fuzz/AlDiffFuzz.cs $M/src/Nethermind/Nethermind.Evm.Test/AlDiffFuzz.cs
  cd $M/src/Nethermind
  if ! nice -n 10 dotnet build -c Release Nethermind.Evm.Test/Nethermind.Evm.Test.csproj -nologo -v q > $L/$id.build 2>&1 || ! nice -n 10 dotnet build -c Release Nethermind.Core.Test/Nethermind.Core.Test.csproj -nologo -v q >> $L/$id.build 2>&1; then
    echo "BUILD FAILED"; grep -E " error " $L/$id.build | head -3; continue
  fi
  (cd $B/Nethermind.Core.Test/release && timeout 900 nice -n 10 ./Nethermind.Core.Test --filter "FullyQualifiedName~JournalSet" > $L/$id.core 2>&1; echo "core rc=$? $(summ $L/$id.core)")
  (cd $B/Nethermind.Evm.Test/release && timeout 1200 nice -n 10 ./Nethermind.Evm.Test --filter "$EVMF" > $L/$id.evm 2>&1; echo "evm-targeted rc=$? $(summ $L/$id.evm)")
  grep -E "^ *failed [A-Za-z]" $L/$id.core $L/$id.evm | sed 's/^.*failed /  failed /' | head -6
  if [ "${FULL:-0}" = 1 ] || ! grep -qE "^ *failed: *[1-9]" $L/$id.core $L/$id.evm; then
    (cd $B/Nethermind.Evm.Test/release && timeout 2400 nice -n 10 ./Nethermind.Evm.Test > $L/$id.evmfull 2>&1; echo "evm-full rc=$? $(summ $L/$id.evmfull)")
    grep -E "^ *failed [A-Za-z]" $L/$id.evmfull | head -4
  fi
  # fuzz: production-path differential against the base output
  mkdir -p /root/al2929v/fz-out/$id
  (cd $B/Nethermind.Evm.Test/release && ALFUZZ_N=300 ALFUZZ_OUT=/root/al2929v/fz-out/$id timeout 1200 nice -n 10 ./Nethermind.Evm.Test --filter "FullyQualifiedName~AlDiffFuzz" > $L/$id.fuzz 2>&1; echo "fuzz rc=$?")
  for f in /root/al2929v/fz-out/$id/alfuzz-*.txt; do
    b=/root/al2929v/fz-out/base/$(basename $f)
    d=$(diff <(head -300 $b) <(head -300 $f) | grep -c '^>')
    echo "  fuzz $(basename $f .txt): $d of 300 lines differ from base"
  done
  case $id in M[1-7]_*)
    bash /root/al2929v/probe/build.sh $M /root/al2929v/probe/out-$id > /dev/null 2>&1
    timeout 600 /root/al2929v/probe/out-$id/probe diff 20000 > $L/$id.probe 2>&1; echo "probe rc=$? $(grep -E 'TOTAL_FAILURES' $L/$id.probe)"
    grep -m2 FAIL $L/$id.probe;;
  esac
done
git -C $M checkout -q -- .
echo "MUTANTS_DONE $(date -u +%FT%TZ)"
