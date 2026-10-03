#!/bin/bash
set -euo pipefail
if [[ "${1:-}" != all ]]; then
  exec /work/entrypoint-upstream.sh "$@"
fi
/work/entrypoint-upstream.sh el
jq '.config.eip8141PrototypeTime = 0 | .config.eip8288PrototypeTime = 0 |
    .alloc["0x2b5ad5c4795c026514f8317c7a215e218dccd6cf"] = {"balance":"100000000000000000000"}' \
  /data/metadata/genesis.json > /data/metadata/genesis-lean.json
mv /data/metadata/genesis-lean.json /data/metadata/genesis.json
/work/entrypoint-upstream.sh cl
