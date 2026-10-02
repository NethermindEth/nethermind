#!/bin/bash
# Repair experiment: subset sweeps of several builds, interleaved, each a fresh process under /root/bench.lock, with the
# gate's sampler (busy.log) running so iters.py can judge clean iterations, and a perf map per process (handler addresses).
# Usage: exp.sh <prefix> <rounds> <build>...   Env: CHAINS (subset, AddMod lead added)
set -uo pipefail
G=/root/fusion-bench/gate1; D=/root/fusion-bench/repair
pre=$1; rounds=$2; shift 2
CHAINS=${CHAINS:-Arithmetic,CallEmpty,CompareBranch,Context,DivIsZero,DivOne,DivZero,JumpAlternating,JumpTaken,JumpUntaken,ModOne,ModZero,Predicate,PrevRandao,ReturnDataSize,SelfBalanceRead,SmallValue,Stack}
mkdir -p $D/maps
taskset -c 0 perl $G/sampler.pl 1 16 $G/logs/busy.log &
sampler=$!
trap 'kill $sampler 2>/dev/null' EXIT
log() { echo "$(date -u +%T) $*" | tee -a $D/exp.log; }
sleep 15
for r in $(seq 1 $rounds); do
  for b in "$@"; do
    tag=$pre-$b-$r
    bash $G/quiet.sh 900 st >> $D/exp.log
    log "lock wait $tag; foreign dotnet: $(pgrep -a dotnet | grep -v fusion-bench | cut -c1-80 | paste -sd'|')"
    flock /root/bench.lock env DOTNET_PerfMapEnabled=3 OPCODE_CHAINS=AddMod,$CHAINS bash $G/sweep.sh $b $tag 1 >> $D/exp.log 2>&1
    m=$(ls -t /tmp/perf-*.map 2>/dev/null | head -1); [ -n "$m" ] && mv $m $D/maps/$tag.map
    log "done $tag; short: $(cd $G && python3 contam.py $tag st | paste -sd' ')"
  done
done
log "EXP DONE $pre"
