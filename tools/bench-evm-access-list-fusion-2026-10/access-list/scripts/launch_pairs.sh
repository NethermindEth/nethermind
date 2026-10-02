#!/bin/bash
# Launches the two paired comparisons in the background (a file, so no ssh command line names the runner).
nohup env GATE=${GATE:-8} bash -c "bash /root/pairab.sh base br 2 /root/al2929-logs/pair_base_br.txt; bash /root/pairab.sh br ref 2 /root/al2929-logs/pair_br_ref.txt BalanceRead,BalanceReadCycle,BalanceColdPair,BalanceColdSingle,ExtCodeSizeRead,CallEmpty,StaticIdentity,CallRevert" > /root/al2929-logs/pair.err 2>&1 &
echo launched
