#!/bin/bash
# gate-1 bench setup on build4: fresh GitHub clone, bundle, base (06bd744 + bench commit, uncommitted) and head worktrees, chainprobe builds.
export DOTNET_ROOT=/root/.dotnet PATH=$PATH:/root/.dotnet DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
set -e
G=/root/g1; mkdir -p $G/logs; cd $G
[ -d src ] || git clone -q --filter=blob:none --no-checkout https://github.com/NethermindEth/nethermind.git src
cd src
git fetch -q /root/al2929.bundle perf/evm-access-list-2929:refs/heads/perf/evm-access-list-2929
git rev-parse perf/evm-access-list-2929
git worktree add -q --detach $G/head 01b51de744
git worktree add -q --detach $G/base 06bd7443f0
cd $G/base && git cherry-pick -n 2ad586e375 && git status --short
cd $G
for v in base head; do rm -rf cp-$v; mkdir cp-$v; cp /root/Program.cs /root/chainprobe.csproj cp-$v/
  (cd cp-$v && nice -n 10 dotnet build chainprobe.csproj -c Release -p:NmRoot=$G/$v -o out > build.log 2>&1; echo "$v build exit=$?"; tail -3 build.log); done
git -C $G/head log --oneline -1; git -C $G/base log --oneline -1
