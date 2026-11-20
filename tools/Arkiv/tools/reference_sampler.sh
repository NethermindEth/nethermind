#!/usr/bin/env bash
# reference_sampler.sh OUT_DIR [INTERVAL_SECONDS]
#
# The reference node answers state (balances, nonces, entities, queries) at its current head only. This pins that
# head — block X — and records, at X explicitly, the state of the accounts and entities recent blocks touched. Every
# sample is a ground-truth vector for X; once Nethermind's archive has passed X, tools/differential.py replays them
# against it. A sample whose head moved mid-way is dropped (the reference refuses a block that is not its head).
set -uo pipefail
R=${RABDI_RPC:-https://rpc.rabdi.db-chain.testnet.arkiv.network}
out=$1; every=${2:-20}
mkdir -p "$out"
rpc() { curl -sf -m 30 -X POST -H 'content-type: application/json' --data "$1" "$R"; }
while true; do
  head=$(rpc '{"jsonrpc":"2.0","id":1,"method":"eth_blockNumber","params":[]}' | jq -r .result) || { sleep "$every"; continue; }
  X=$((head))
  # Accounts and entities the last few blocks touched, plus the protocol accounts.
  blocks=$(rpc "$(python3 -I -c "import json,sys; x=int(sys.argv[1]); print(json.dumps([{'jsonrpc':'2.0','id':i,'method':'eth_getBlockByNumber','params':[hex(x-i),True]} for i in range(8)]))" "$X")") || { sleep "$every"; continue; }
  addrs=$(echo "$blocks" | jq -r '[.[].result.transactions[]?|.from,.to] + ["0x000000000000000000000000000000000000dead","0x4400000000000000000000000000000000000044"] | unique | .[]' | head -20)
  hashes=$(echo "$blocks" | jq -r '[.[].result.transactions[]?|select(.to=="0x4400000000000000000000000000000000000044" and (.input|startswith("0x49650044")))|.hash] | .[:6][]')
  receipts=$(python3 -I -c "import json,sys; print(json.dumps([{'jsonrpc':'2.0','id':i,'method':'eth_getTransactionReceipt','params':[h]} for i,h in enumerate(sys.argv[1:])]))" $hashes)
  keys=""; [ -n "$hashes" ] && keys=$(rpc "$receipts" | jq -r '[.[].result.logs[]?|select(.address=="0x4400000000000000000000000000000000000044")|.topics[1]]|unique|.[:10][]')
  hx=$(printf '0x%x' "$X")
  batch=$(python3 -I -c "
import json,sys
x,hx=int(sys.argv[1]),sys.argv[2]; addrs=sys.argv[3].split(); keys=sys.argv[4].split()
calls=[]
for a in addrs:
  calls.append(('eth_getBalance',[a,hx])); calls.append(('eth_getTransactionCount',[a,hx]))
for k in keys:
  calls.append(('arkiv_getEntity',[k,x]))
calls.append(('arkiv_getEntityCount',[{'block':x}]))
calls.append(('arkiv_query',['*',{'atBlock':hx,'select':{'count':True}}]))
calls.append(('eth_getBlockByNumber',[hx,False]))
print(json.dumps([{'jsonrpc':'2.0','id':i,'method':m,'params':p} for i,(m,p) in enumerate(calls[:50])]))
" "$X" "$hx" "$(echo $addrs)" "$(echo $keys)")
  if resp=$(rpc "$batch"); then
    if ! echo "$resp" | jq -e 'map(select(.error and (.error.message|test("chain head only|not retained|ahead")))) | length > 0' >/dev/null; then
      jq -nc --argjson req "$batch" --argjson res "$resp" --argjson x "$X" '{block:$x, at:now, calls:[range(0;$req|length) as $i | {method:$req[$i].method, params:$req[$i].params, response:($res[]|select(.id==$i))}]}' >> "$out/samples.jsonl"
    fi
  fi
  sleep "$every"
done
