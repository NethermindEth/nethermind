#!/bin/bash
# Bisect benchmark script - tests key commits from the combined branch
set -e

SSH_CMD="/c/Windows/System32/OpenSSH/ssh.exe -i C:\Users\kamil\.ssh\id_rsa ubuntu@51.68.103.177"
RESULTS_FILE="/tmp/bisect-results.txt"

# Key checkpoints to test (oldest to newest)
declare -A COMMITS
COMMITS[1_master]="origin/master"
COMMITS[2_eth_transfers_identity]="e7f9d992b8"
COMMITS[3_profiling_added]="46d1002825"
COMMITS[4_rlp_parallel_defer_canonical]="75eb200461"
COMMITS[5_commit_fast]="0e2835ba1a"
COMMITS[6_sload_inline]="7b6a33e489"
COMMITS[7_deferred_storage_HEAD]="56ae37d0cc"

echo "=== BISECT BENCHMARK ===" > "$RESULTS_FILE"
echo "Started: $(date)" >> "$RESULTS_FILE"

for key in $(echo "${!COMMITS[@]}" | tr ' ' '\n' | sort); do
    commit="${COMMITS[$key]}"
    echo ""
    echo "============================================"
    echo "Testing: $key ($commit)"
    echo "============================================"

    # Checkout and build
    $SSH_CMD "sudo bash -c 'cd /home/ubuntu/nethermind && git fetch origin && git checkout $commit --force && git log --oneline -1'"
    $SSH_CMD "sudo bash -c 'cd /home/ubuntu/nethermind && docker build -t block-stm . && docker tag block-stm nethermindeth/nethermind:block-stm'"

    # Run benchmark
    $SSH_CMD "sudo bash -c 'cd /mnt/sda/expb-data && expb execute-scenarios --config-file nethermind-only.yaml --per-payload-metrics --print-logs'" 2>&1 | tail -5

    # Get result
    result=$($SSH_CMD "python3 -c \"
import json, glob
d = sorted(glob.glob('/mnt/sda/expb-data/outputs/expb-executor-nethermind-performance-2sec-*'), reverse=True)[0]
with open(d + '/k6-summary.json') as f:
    data = json.load(f)
it = data['metrics']['iteration_duration']
print('med=%.0f p95=%.0f' % (it['med'], it['p(95)']))
\"")

    echo "$key ($commit): $result" | tee -a "$RESULTS_FILE"
done

echo "" >> "$RESULTS_FILE"
echo "Finished: $(date)" >> "$RESULTS_FILE"
echo ""
echo "=== ALL RESULTS ==="
cat "$RESULTS_FILE"
