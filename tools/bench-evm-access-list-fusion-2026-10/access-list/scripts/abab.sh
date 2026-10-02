#!/bin/bash
# ABAB of the OpcodeChainBenchmarks chains, base (06bd744 + bench commit) vs branch. One pinned core, under the bench lock.
# A run starts only when the machine is quiet (1-min load < 2.5) and is repeated (up to 4 times) if the load rose above 3.5
# while it ran, i.e. another agent's build or test overlapped it.
export DOTNET_ROOT=/root/.dotnet PATH=$PATH:/root/.dotnet
C=${CHAINS:-StorageRead,StorageReadCycle,StorageReadCold,StorageWrite,BalanceRead,BalanceReadCycle,BalanceColdPair,BalanceColdSingle,ExtCodeSizeRead,CallEmpty,StaticIdentity,CallReturn,CallRevert}
cd /root; out=${OUT:-/root/al2929-logs/abab.txt}; : > $out
load() { cut -d' ' -f1 /proc/loadavg; }
for r in 1 2; do for v in base br; do
  for attempt in 1 2 3 4; do
    while awk -v l=$(load) 'BEGIN{exit !(l>=2.5)}'; do sleep 20; done
    tmp=$(mktemp); flag=$(mktemp); : > $flag
    ( while sleep 3; do awk -v l=$(load) 'BEGIN{exit !(l>3.5)}' && echo hi >> $flag; done ) & mon=$!
    flock /root/bench.lock taskset -c 8 cp-$v/out/chainprobe $C 7 > $tmp 2>&1
    kill $mon 2>/dev/null; wait $mon 2>/dev/null
    if [ -s $flag ]; then echo "== $v run $r attempt $attempt OVERLAPPED (load>3.5 $(wc -l < $flag) samples), discarded" >> $out; rm -f $tmp $flag; continue; fi
    echo "== $v run $r attempt $attempt clean" >> $out; cat $tmp >> $out; rm -f $tmp $flag; break
  done
done; done
echo ABAB_DONE >> $out
