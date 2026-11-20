#!/usr/bin/env bash
# record_corpus.sh NODE_RPC FROM TO OUT.jsonl — record the chain FROM..TO as engine_newPayloadV4 parameters, exported
# from a node that executed and validated it (debug_getRawBlock), so a benchmark replays exactly what the consensus
# client handed that node — no peers, no waiting for blocks. Needs the payloads example (native/arkiv-ffi).
# Batches start at 50 blocks and halve when the node refuses one as too large (-32005, its batch response cap).
set -euo pipefail
rpc=$1; from=$2; to=$3; out=$4
payloads=${PAYLOADS_BIN:-$(dirname "$(realpath "$0")")/../native/arkiv-ffi/target/release/examples/payloads}
raw=$(mktemp); chunk=$(mktemp); trap 'rm -f "$raw" "$chunk"' EXIT
n=$from; size=50
while [ "$n" -le "$to" ]; do
  last=$(( n + size - 1 < to ? n + size - 1 : to ))
  python3 -I -c "import json,sys; a,b=int(sys.argv[1]),int(sys.argv[2]); print(json.dumps([{'jsonrpc':'2.0','id':i,'method':'debug_getRawBlock','params':[hex(i)]} for i in range(a,b+1)]))" "$n" "$last" \
    | curl -sf -m 120 -X POST -H 'content-type: application/json' --data-binary @- "$rpc" > "$chunk"
  if jq -e 'type == "array" and all(.result != null)' "$chunk" >/dev/null; then
    jq -r 'sort_by(.id)[] | .result' "$chunk" >> "$raw"
    n=$(( last + 1 )); [ $size -lt 50 ] && size=$(( size * 2 ))
  elif [ $size -gt 1 ]; then
    size=$(( size / 2 ))
  else
    echo "block $n: $(head -c 300 "$chunk")" >&2; exit 1
  fi
done
"$payloads" --rlp "$raw" > "$out"
echo "recorded $(wc -l < "$out") payloads ($from..$to) to $out"
