#!/bin/bash
export DOTNET_ROOT=/root/.dotnet PATH=$PATH:/root/.dotnet
f=/root/al2929-logs/jit-br2.txt; rm -f $f
DOTNET_JitStdOutFile=$f DOTNET_JitDisasm='*JournalSet*:Add *JournalSet*:FindSlot' timeout 300 taskset -c 20 /root/cp-br/out/chainprobe StorageReadCycle 1 > /dev/null 2>&1
grep -n "; Assembly listing" $f | cut -c1-170
awk '/; Assembly listing for method.*StorageCell\]:Add\(.*Tier1/{p=1} p&&/(call |Total bytes)/{print} /; Total bytes of code/{p=0}' $f | cut -c1-170
