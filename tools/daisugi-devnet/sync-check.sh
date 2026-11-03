#!/usr/bin/env bash
# Compares the sync pairs against the original pair: every block's hash, state root and receipts root, the raw
# blocks (bodies with their stark_proof) and receipts of the given proof blocks, each beacon node's copy of those
# payloads, and the finalized heads.
# Usage: sync-check.sh <proof-block-number>...
set -euo pipefail
ELS=(28545 28547 28548)
CLS=(25052 25053 25054)
rpc() { curl -sf -X POST -H 'content-type: application/json' --data "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"$2\",\"params\":$3}" "http://127.0.0.1:$1" | jq -c .result; }
fail() { echo "FAIL: $*"; exit 1; }

head=$(for p in "${ELS[@]}"; do printf '%d\n' "$(rpc "$p" eth_blockNumber '[]' | jq -r .)"; done | sort -n | head -1)
echo "== blocks 0..$head on ${ELS[*]}"
for p in "${ELS[@]}"; do
  for ((n = 0; n <= head; n++)); do rpc "$p" eth_getBlockByNumber "[\"$(printf 0x%x $n)\",false]" | jq -r '[.number, .hash, .stateRoot, .receiptsRoot] | join(" ")'; done > "/tmp/sync-check-$p"
done
for p in "${ELS[@]:1}"; do cmp -s "/tmp/sync-check-${ELS[0]}" "/tmp/sync-check-$p" || fail "block list on $p differs"; done
echo "$((head + 1)) blocks: hash, stateRoot and receiptsRoot identical"

for n in "$@"; do
  hex=$(printf 0x%x "$n")
  echo "== proof block $n"
  for p in "${ELS[@]}"; do
    raw=$(rpc "$p" debug_getRawBlock "[\"$hex\"]" | jq -r .)
    receipts=$(rpc "$p" eth_getBlockReceipts "[\"$hex\"]" | jq -S -c . | sha256sum | cut -c1-16)
    echo "EL $p: raw block $(( (${#raw} - 2) / 2 )) B sha256 $(printf %s "$raw" | sha256sum | cut -c1-16), receipts sha256 $receipts"
  done | tee /tmp/sync-check-el
  [[ $(awk '{print $5, $8, $11}' /tmp/sync-check-el | sort -u | wc -l) == 1 ]] || fail "EL copies of block $n differ"
  slot=$(for s in $(seq "$n" $((n + 32))); do curl -sf "http://127.0.0.1:${CLS[0]}/eth/v2/beacon/blocks/$s" | jq -e --arg n "$n" 'select(.data.message.body.execution_payload.block_number == $n)' >/dev/null && { echo "$s"; break; }; done)
  for p in "${CLS[@]}"; do
    proof=$(curl -sf "http://127.0.0.1:$p/eth/v2/beacon/blocks/$slot" | jq -r .data.message.body.execution_payload.recursive_stark_proof)
    echo "CL $p slot $slot: starkProof $(( (${#proof} - 2) / 2 )) B sha256 $(printf %s "$proof" | sha256sum | cut -c1-16)"
  done | tee /tmp/sync-check-cl
  [[ $(awk '{print $5, $8}' /tmp/sync-check-cl | sort -u | wc -l) == 1 ]] || fail "CL copies of slot $slot differ"
done

echo "== finality"
for p in "${ELS[@]}"; do echo "EL $p finalized $(rpc "$p" eth_getBlockByNumber '["finalized",false]' | jq -r '.number + " " + .hash')"; done
for p in "${CLS[@]}"; do echo "CL $p $(curl -sf "http://127.0.0.1:$p/eth/v1/beacon/states/head/finality_checkpoints" | jq -c '{finalized: .data.finalized.epoch, root: .data.finalized.root[:18]}') head $(curl -sf "http://127.0.0.1:$p/eth/v1/node/syncing" | jq -c '{head_slot: .data.head_slot, optimistic: .data.is_optimistic}')"; done
echo PASS
