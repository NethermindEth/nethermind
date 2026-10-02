#!/bin/bash
# usage: build.sh <repo-with-new-JournalSet> <outdir>
set -e
export DOTNET_ROOT=/root/.dotnet PATH=$PATH:/root/.dotnet DOTNET_CLI_TELEMETRY_OPTOUT=1
REPO=$1; OUT=$2
cd /root/al2929v/probe
J=src/Nethermind/Nethermind.Core/Collections/JournalSet.cs
git -C /root/al2929v/src show 06bd7443f0:$J | sed 's/^namespace Nethermind.Core.Collections/using Nethermind.Core;\nnamespace Probe.Old/' > OldJournalSet.cs
sed 's/^namespace Nethermind.Core.Collections/using Nethermind.Core;\nnamespace Probe.New/' $REPO/$J > NewJournalSet.cs
NmBin=/root/al2929v/src/src/Nethermind/artifacts/bin/Nethermind.Core.Test/release
nice -n 10 dotnet build -c Release -p:NmBin=$NmBin -o $OUT -nologo -v q 2>&1 | grep -E "error|Warn|Elapsed" | head -20
