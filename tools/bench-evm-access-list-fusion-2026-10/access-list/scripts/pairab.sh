#!/bin/bash
# pairab.sh A B NPAIRS OUT [CHAINS]: chain harness cp-A and cp-B run at the same time on two physical cores (8 and 10), then
# with the cores swapped, NPAIRS times; each pair under the bench lock, started when the 1-min load is < GATE (default 8). The pair runs
# side by side so another agent's load hits both arms alike; max load seen during each pair is logged next to it.
export DOTNET_ROOT=/root/.dotnet PATH=$PATH:/root/.dotnet
A=$1; B=$2; N=$3; OUT=$4
C=${5:-StorageRead,StorageReadCycle,StorageReadCold,StorageWrite,BalanceRead,BalanceReadCycle,BalanceColdPair,BalanceColdSingle,ExtCodeSizeRead,CallEmpty,StaticIdentity,CallReturn,CallRevert}
cd /root; : > $OUT
load() { cut -d' ' -f1 /proc/loadavg; }
for k in $(seq 1 $N); do for order in 0 1; do
  while awk -v l=$(load) -v g=${GATE:-8} 'BEGIN{exit !(l>=g)}'; do sleep 15; done
  if [ $order = 0 ]; then ca=8; cb=10; else ca=10; cb=8; fi
  mx=$(mktemp); echo 0 > $mx
  ( while sleep 3; do l=$(load); awk -v l=$l -v m=$(cat $mx) 'BEGIN{exit !(l>m)}' && echo $l > $mx; done ) & mon=$!
  flock /root/bench.lock bash -c "taskset -c $ca cp-$A/out/chainprobe $C 7 > /tmp/pa-$A.txt 2>&1 & taskset -c $cb cp-$B/out/chainprobe $C 7 > /tmp/pa-$B.txt 2>&1 & wait"
  kill $mon 2>/dev/null; wait $mon 2>/dev/null
  echo "== pair $k order $order ($A@$ca $B@$cb) maxload $(cat $mx) foreign $(pgrep -c -f 'Nethermind.*Test|dotnet build' )" >> $OUT
  paste -d'|' /tmp/pa-$A.txt /tmp/pa-$B.txt | awk -F'|' -v a=$A -v b=$B '{split($1,x," "); split($2,y," "); printf "%-18s %s %8.3f  %s %8.3f  %s/%s %.3f\n", x[1], a, x[2], b, y[2], b, a, y[2]/x[2]}' >> $OUT
  rm -f $mx
done; done
echo PAIRS_DONE >> $OUT
