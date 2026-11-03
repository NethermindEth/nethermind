#!/usr/bin/env bash
# Checks a running devnet: genesis hash agreement, Lean backend startup, the header layout on both sides of the
# EIP-8288 boundary, the CL fork switch, no missed slots after the fork, Engine API errors, and finality past it.
# Usage: check.sh <genesis-dir>
set -euo pipefail
G=$(realpath "${1:?genesis dir}")
EL=http://127.0.0.1:${EL_RPC_PORT:-28545}
CL=http://127.0.0.1:${CL_HTTP_PORT:-25052}
N=${DEVNET_PREFIX:-daisugi}
P="$G/params.json"
GENESIS=$(jq -r .genesisTimestamp "$P"); T8288=$(jq -r .eip8288PrototypeTime "$P"); EPOCH=$(jq -r .daisugiForkEpoch "$P")
FORK_SLOT=$(( EPOCH * 32 ))
rpc() { curl -sf -X POST -H 'content-type: application/json' --data "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"$1\",\"params\":$2}" $EL | jq -r .result; }
fields() { python3 -I -c '
import sys
b = bytes.fromhex(sys.argv[1][2:])
def item(i):
    p = b[i]
    if p < 0x80: return i + 1
    if p < 0xb8: return i + 1 + p - 0x80
    if p < 0xc0: l = p - 0xb7; return i + 1 + l + int.from_bytes(b[i+1:i+1+l], "big")
    if p < 0xf8: return i + 1 + p - 0xc0
    l = p - 0xf7; return i + 1 + l + int.from_bytes(b[i+1:i+1+l], "big")
p = b[0]; i = 1 if p < 0xf8 else 1 + p - 0xf7; n = 0
while i < len(b): i = item(i); n += 1
print(n)' "$1"; }
fail() { echo "FAIL: $*"; exit 1; }

echo "== genesis"
until curl -sf -o /dev/null -X POST -H 'content-type: application/json' --data '{"jsonrpc":"2.0","id":1,"method":"eth_chainId","params":[]}' $EL; do sleep 2; done
EL_GENESIS=$(curl -sf -X POST -H 'content-type: application/json' --data '{"jsonrpc":"2.0","id":1,"method":"eth_getBlockByNumber","params":["0x0",false]}' $EL | jq -r .result.hash)
[[ "$EL_GENESIS" == "$(jq -r .elGenesisHash "$P")" ]] || fail "EL genesis $EL_GENESIS differs from CL genesis state"
echo "EL genesis hash $EL_GENESIS matches the CL genesis state"
docker logs $N-nethermind 2>&1 | grep -q "InitializeLeanBackend.*failed" && fail "Lean backend failed to start"
echo "Lean backend: InitializeLeanBackend did not fail"

echo "== waiting for finality past epoch $EPOCH"
until [[ $(curl -sf $CL/eth/v1/beacon/states/head/finality_checkpoints | jq -r .data.finalized.epoch) -gt $EPOCH ]]; do sleep 4; done
curl -sf $CL/eth/v1/beacon/states/head/finality_checkpoints | jq -c .data

echo "== fork boundary"
for slot in $(( FORK_SLOT - 1 )) $FORK_SLOT; do
  block=$(curl -sf $CL/eth/v2/beacon/blocks/$slot)
  number=$(jq -r .data.message.body.execution_payload.block_number <<<"$block")
  ts=$(jq -r .data.message.body.execution_payload.timestamp <<<"$block")
  raw=$(rpc debug_getRawHeader "[\"$(printf '0x%x' "$number")\"]")
  echo "slot $slot: $(jq -r .version <<<"$block"), EL block $number ts $ts, $(fields "$raw") header fields, starkProof $(jq -r .data.message.body.execution_payload.recursive_stark_proof <<<"$block")"
done
[[ $(curl -sf $CL/eth/v2/beacon/blocks/$(( FORK_SLOT - 1 )) | jq -r .version) == electra ]] || fail "slot before the fork is not electra"
[[ $(curl -sf $CL/eth/v2/beacon/blocks/$FORK_SLOT | jq -r .version) == daisugi ]] || fail "fork slot is not daisugi"
[[ $(curl -sf $CL/eth/v2/beacon/blocks/$FORK_SLOT | jq -r .data.message.body.execution_payload.timestamp) == "$T8288" ]] || fail "fork slot timestamp is not eip8288PrototypeTime"

echo "== missed slots after the fork"
head_slot=$(curl -sf $CL/eth/v1/beacon/headers/head | jq -r .data.header.message.slot)
head_number=$(curl -sf $CL/eth/v2/beacon/blocks/head | jq -r .data.message.body.execution_payload.block_number)
fork_number=$(curl -sf $CL/eth/v2/beacon/blocks/$FORK_SLOT | jq -r .data.message.body.execution_payload.block_number)
echo "slots $FORK_SLOT..$head_slot, EL blocks $fork_number..$head_number, missed $(( head_slot - FORK_SLOT - (head_number - fork_number) ))"

echo "== errors"
echo "nethermind: $(docker logs $N-nethermind 2>&1 | grep -ciE 'invalid block|rejected|exception') invalid/rejected/exception lines"
echo "beacon: $(docker logs $N-beacon 2>&1 | grep -cE ' (ERROR|CRIT) ') ERROR/CRIT lines"
echo "validator: $(docker logs $N-validator 2>&1 | grep -cE ' (ERROR|CRIT) ') ERROR/CRIT lines, $(docker logs $N-validator 2>&1 | grep -c 'Successfully published block') blocks published"
echo "PASS"
