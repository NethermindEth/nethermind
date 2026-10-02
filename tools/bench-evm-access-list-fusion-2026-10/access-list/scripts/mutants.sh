#!/bin/bash
# Each mutant must make the new tests fail. Restores the files after each run.
export DOTNET_ROOT=/root/.dotnet PATH=$PATH:/root/.dotnet DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
cd /root/al2929/src/Nethermind
JS=Nethermind.Core/Collections/JournalSet.cs
ST=Nethermind.Evm/StackAccessTracker.cs
run() { # name file sed-expr project filter
  cp $2 /tmp/mut.bak
  sed -i "$3" $2
  if cmp -s $2 /tmp/mut.bak; then echo "$1: SED DID NOT APPLY"; return; fi
  nice -n 10 dotnet build $4/$4.csproj -c Release > /tmp/mut-$1.build 2>&1 || { echo "$1: build failed"; cp /tmp/mut.bak $2; return; }
  out=$(nice -n 10 artifacts/bin/$4/release/$4 --filter "$5" 2>&1 | grep -E "^  (total|failed|succeeded):" | tr -s ' ' | tr '\n' ' ')
  echo "$1: $out"
  cp /tmp/mut.bak $2
}
run M1-noforget-address $ST 's/            _lastWarmAddress = null;/            GC.KeepAlive(_lastWarmAddress);/' Nethermind.Evm.Test "FullyQualifiedName~EthereumGasPolicyTests|FullyQualifiedName~Eip2929Tests"
run M6-restore-off-by-one $JS 's/for (int i = count - 1; i > snapshot; i--)/for (int i = count - 1; i > snapshot + 1; i--)/' Nethermind.Core.Test "FullyQualifiedName~JournalSetTests"
# rebuild clean tree
nice -n 10 dotnet build Nethermind.Core.Test/Nethermind.Core.Test.csproj -c Release > /tmp/mut-final.build 2>&1; nice -n 10 dotnet build Nethermind.Evm.Test/Nethermind.Evm.Test.csproj -c Release >> /tmp/mut-final.build 2>&1; echo "final rebuild exit=$?"; git status --short
