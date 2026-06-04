#!/usr/bin/env bash
set -euo pipefail

SEED_RPC="${SEED_RPC:-http://127.0.0.1:8545}"
JOINER_RPC="${JOINER_RPC:-http://127.0.0.1:18548}"

hex_to_dec() {
  local h="$1"
  h="${h#0x}"
  echo $((16#$h))
}

rpc_block() {
  local url="$1"
  curl -s -H 'Content-Type: application/json' \
    --data '{"jsonrpc":"2.0","method":"eth_blockNumber","params":[],"id":1}' \
    "$url" | sed -n 's/.*"result":"\([^"]*\)".*/\1/p'
}

s1=$(rpc_block "$SEED_RPC")
j1=$(rpc_block "$JOINER_RPC")

echo "seed:   $s1 ($(hex_to_dec "$s1"))"
echo "joiner: $j1 ($(hex_to_dec "$j1"))"

echo "Waiting 20s and checking progress..."
sleep 20

s2=$(rpc_block "$SEED_RPC")
j2=$(rpc_block "$JOINER_RPC")

echo "seed:   $s2 ($(hex_to_dec "$s2"))"
echo "joiner: $j2 ($(hex_to_dec "$j2"))"

if [[ "$(hex_to_dec "$j2")" -gt "$(hex_to_dec "$j1")" ]]; then
  echo "Joiner is syncing (block number increased)."
else
  echo "Joiner did not advance in this window. Check logs: docker logs -f nm-xdc-subnet-joiner"
  exit 1
fi
