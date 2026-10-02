#!/bin/bash
# Gate-1 ABBA BAAB of all OpcodeChainBenchmarks chains, base vs head, pinned to core 12 (SMT sibling 27 left idle), under the bench lock.
# A run is discarded and repeated (up to 4 attempts) when a foreign dotnet process or 1-min load > 3.5 was seen while it ran.
export DOTNET_ROOT=/root/.dotnet PATH=$PATH:/root/.dotnet DOTNET_CLI_TELEMETRY_OPTOUT=1
G=/root/g1; L=$G/logs; CORE=12
C=$(grep -o '\[Params("BalanceColdExistingPair"[^]]*' $G/base/src/Nethermind/Nethermind.Evm.Benchmark/OpcodeChainBenchmarks.cs | sed 's/\[Params(//; s/[" )]//g')
echo "chains: $C" > $L/chains.txt
load() { cut -d' ' -f1 /proc/loadavg; }
foreign() { pgrep -a dotnet | grep -v "/root/g1/cp-" ; }
n=0
for v in base head head base head base base head; do n=$((n+1))
  for attempt in 1 2 3 4; do
    out=$L/run$n-$v-a$attempt.txt; flag=$L/run$n-$v-a$attempt.overlap
    : > $flag
    (
      flock 9
      while awk -v l=$(load) 'BEGIN{exit !(l>=2.5)}' || [ -n "$(foreign)" ]; do sleep 20; done
      ( while sleep 3; do l=$(load); f=$(foreign | head -1); { awk -v l=$l 'BEGIN{exit !(l>3.5)}' || [ -n "$f" ]; } && echo "$(date -u +%T) load=$l $f" >> $flag; done ) & mon=$!
      echo "start $(date -u +%FT%TZ) load=$(load)" > $out
      taskset -c $CORE nice -n 0 $G/cp-$v/out/chainprobe $C 7 >> $out 2>&1
      echo "end $(date -u +%FT%TZ) exit=$? sibling27: $(ps -eLo psr,pid,comm | awk '$1==27' | wc -l) threads" >> $out
      kill $mon 2>/dev/null; wait $mon 2>/dev/null
    ) 9>/root/bench.lock
    if [ -s $flag ]; then echo "run $n $v attempt $attempt OVERLAPPED ($(wc -l < $flag) samples)" >> $L/progress.txt; continue; fi
    echo "run $n $v attempt $attempt clean $out" >> $L/progress.txt; rm -f $flag; break
  done
done
echo DONE >> $L/progress.txt
