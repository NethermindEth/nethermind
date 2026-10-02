#!/bin/bash
export DOTNET_ROOT=/root/.dotnet PATH=$PATH:/root/.dotnet DOTNET_CLI_TELEMETRY_OPTOUT=1
for w in fz-base fz-head; do
  cp /root/al2929v/fuzz/AlDiffFuzz.cs /root/al2929v/$w/src/Nethermind/Nethermind.Evm.Test/AlDiffFuzz.cs
  (cd /root/al2929v/$w/src/Nethermind && nice -n 10 dotnet build -c Release Nethermind.Evm.Test/Nethermind.Evm.Test.csproj -nologo -v q 2>&1 | grep -E " error |Error\(s\)" | head -5)
done
rm -rf /root/al2929v/fz-out/base /root/al2929v/fz-out/head /root/al2929v/fz-out/base2 /root/al2929v/fz-out/head2
/root/al2929v/fuzzrun.sh fz-base base 4000 & /root/al2929v/fuzzrun.sh fz-head head 4000 & wait
ALFUZZ_SEED=1000003 /root/al2929v/fuzzrun.sh fz-base base2 4000 & ALFUZZ_SEED=1000003 /root/al2929v/fuzzrun.sh fz-head head2 4000 & wait
cd /root/al2929v/fz-out
for p in base:head base2:head2; do a=${p%%:*}; b=${p##*:}
  for f in $a/alfuzz-*.txt; do n=$(basename $f); cmp -s $f $b/$n && echo "IDENTICAL $a/$n vs $b" || echo "DIFF $a/$n vs $b"; tail -1 $f; done
done
echo FUZZ2_DONE
