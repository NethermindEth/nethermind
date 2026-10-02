#!/bin/bash
export DOTNET_ROOT=/root/.dotnet PATH=$PATH:/root/.dotnet DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
set -e
cd /root/al2929 && git fetch -q /root/al2929.bundle perf/evm-access-list-2929:refs/heads/al-tmp3 && git merge -q --ff-only al-tmp3 && git log --oneline -1
rm -rf /root/al2929-ref && git clone -q /root/al2929 /root/al2929-ref && cd /root/al2929-ref && git checkout -q perf/evm-access-list-2929
sed -i 's/            if (_lastWarmAddress is not null \&\& _lastWarmAddress.Equals(address)) return false;/            if (ReferenceEquals(address, _lastWarmAddress) \&\& address is not null) return false;/' src/Nethermind/Nethermind.Evm/StackAccessTracker.cs
git diff --stat
cd /root
for v in br ref; do R=/root/al2929; [ $v = ref ] && R=/root/al2929-ref; rm -rf cp-$v; mkdir cp-$v; cp chainprobe/Program.cs chainprobe/chainprobe.csproj cp-$v/; (cd cp-$v && nice -n 10 dotnet build chainprobe.csproj -c Release -p:NmRoot=$R -o out > build.log 2>&1; echo "$v build exit=$?"); done
grep -c "ReferenceEquals" /root/al2929-ref/src/Nethermind/Nethermind.Evm/StackAccessTracker.cs
