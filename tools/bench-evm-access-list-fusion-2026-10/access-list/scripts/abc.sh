#!/bin/bash
# ABCCBA of the chain harness: base (06bd744 + bench commit), br (branch head), ref (branch head, memo by reference only).
# One pinned core, bench lock; a run starts at 1-min load < 2.5 and is repeated (max 4) if the load passed 3.5 during it.
export DOTNET_ROOT=/root/.dotnet PATH=$PATH:/root/.dotnet
C=StorageRead,StorageReadCycle,StorageReadCold,BalanceRead,BalanceReadCycle,BalanceColdPair,BalanceColdSingle,ExtCodeSizeRead,CallEmpty,StaticIdentity,CallReturn,CallRevert
cd /root; out=/root/al2929-logs/abc.txt; : > $out
load() { cut -d' ' -f1 /proc/loadavg; }
for v in base br ref ref br base; do
  for attempt in 1 2 3 4; do
    while awk -v l=$(load) 'BEGIN{exit !(l>=2.5)}'; do sleep 20; done
    tmp=$(mktemp); flag=$(mktemp); : > $flag
    ( while sleep 3; do awk -v l=$(load) 'BEGIN{exit !(l>3.5)}' && echo hi >> $flag; done ) & mon=$!
    flock /root/bench.lock taskset -c 8 cp-$v/out/chainprobe $C 7 > $tmp 2>&1
    kill $mon 2>/dev/null; wait $mon 2>/dev/null
    if [ -s $flag ]; then echo "== $v attempt $attempt OVERLAPPED, discarded" >> $out; rm -f $tmp $flag; continue; fi
    echo "== $v attempt $attempt clean $(date -u +%H:%M)" >> $out; cat $tmp >> $out; rm -f $tmp $flag; break
  done
done
echo ABC_DONE >> $out
