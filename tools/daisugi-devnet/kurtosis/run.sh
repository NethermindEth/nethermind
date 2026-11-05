#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")"
ENCLAVE=${1:-daisugi}
PACKAGE=${PACKAGE:-github.com/ethpandaops/ethereum-package@5795b77e65235ad223d4d3b413b2739190bf60cc}
LIGHTHOUSE_IMAGE=${LIGHTHOUSE_IMAGE:-lighthouse:eip8288-lean-amd64-cmd}
PQ_KEY=${PQ_KEY:-bcdf20249abf0ed6d944c0288fad489e33f66b3960d9e6229c1cd214ed3bbe31}
G=$PWD/run/$ENCLAVE

docker build -q --build-arg LIGHTHOUSE_IMAGE="$LIGHTHOUSE_IMAGE" -f lighthouse.Dockerfile -t daisugi-kurtosis/lighthouse:eip8288-lean-amd64-cmd .
docker build -q -f genesis.Dockerfile -t daisugi-kurtosis/genesis-generator:287f596b .
kurtosis enclave rm -f "$ENCLAVE" >/dev/null 2>&1 || true
trap 'kurtosis enclave rm -f "$ENCLAVE"' EXIT
kurtosis run --enclave "$ENCLAVE" "$PACKAGE" --args-file network_params.yaml
[[ -z ${KEEP:-} ]] || trap - EXIT

rm -rf "$G"; mkdir -p "$(dirname "$G")"
kurtosis files download "$ENCLAVE" el_cl_genesis_data "$G" >/dev/null
port() { kurtosis port print "$ENCLAVE" "$1" "$2" | sed 's|.*:||'; }
export EL_RPC_PORT=$(port el-1-nethermind-lighthouse rpc)
export CL_HTTP_PORT=$(port cl-1-lighthouse-nethermind http)
container() { docker ps --format '{{.Names}}' | grep -m1 "^$1--"; }
export EL_CONTAINER=$(container el-1-nethermind-lighthouse)
export BN_CONTAINER=$(container cl-1-lighthouse-nethermind)
export VC_CONTAINER=$(container vc-1-nethermind-lighthouse)
until curl -sf "http://127.0.0.1:$CL_HTTP_PORT/eth/v1/beacon/genesis" >/dev/null; do sleep 2; done
jq -n --argjson ts "$(jq -r .timestamp "$G/genesis.json")" \
  --argjson t8288 "$(jq -r .config.eip8288PrototypeTime "$G/genesis.json")" \
  --argjson epoch "$(sed -n 's/^DAISUGI_FORK_EPOCH: *//p' "$G/config.yaml")" \
  --arg hash "$(curl -sf "http://127.0.0.1:$CL_HTTP_PORT/eth/v2/debug/beacon/states/genesis" | jq -r .data.latest_execution_payload_header.block_hash)" \
  '{genesisTimestamp: $ts, eip8288PrototypeTime: $t8288, daisugiForkEpoch: $epoch, elGenesisHash: $hash}' > "$G/params.json"
cat "$G/params.json"

../check.sh "$G"

for i in $(seq "${PQ_SERIES:-3}"); do
  echo "== pq tx $i"
  PQ_KEY=$PQ_KEY CHAIN_ID=$(jq -r .config.chainId "$G/genesis.json") PORT=$EL_RPC_PORT OUT="$G/pq-$i" ../pq.sh
done
