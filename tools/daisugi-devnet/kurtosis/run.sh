#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")"
ENCLAVE=${1:-daisugi}
PACKAGE=${PACKAGE:-github.com/ethpandaops/ethereum-package@5795b77e65235ad223d4d3b413b2739190bf60cc}
NETHERMIND_IMAGE=${NETHERMIND_IMAGE:-daisugi/nethermind:af50ed1917}
LIGHTHOUSE_IMAGE=${LIGHTHOUSE_IMAGE:-daisugi/lighthouse:eip8288-lean}
PQ_KEY=${PQ_KEY:-bcdf20249abf0ed6d944c0288fad489e33f66b3960d9e6229c1cd214ed3bbe31}
G=$PWD/run/$ENCLAVE
ARGS=$PWD/run/$ENCLAVE.network_params.yaml

# Image tags are mutable: run only the binaries the release pins.
file_sha256() {
  local id; id=$(docker create "$1")
  docker cp "$id:$2" - | tar -xO | sha256sum | cut -d' ' -f1
  docker rm "$id" >/dev/null
}
[[ $(file_sha256 "$NETHERMIND_IMAGE" /nethermind/libnethermind_lean.so) == "${NETHERMIND_LEAN_SHA256:-aad07d8fa0f106457ef1719eadd1685a98e39fe6df3ab27308022e14637b8f00}" ]] \
  || { echo "FAIL: $NETHERMIND_IMAGE does not carry the pinned libnethermind_lean.so"; exit 1; }
docker build -q -f genesis.Dockerfile -t daisugi-kurtosis/genesis-generator:287f596b .
mkdir -p run
sed -e "s|el_image: .*|el_image: $NETHERMIND_IMAGE|" -e "s|\([cv]c_image:\) .*|\1 $LIGHTHOUSE_IMAGE|" -e "s|^\( *\)count: .*|\1count: ${NODES:-1}|" network_params.yaml > "$ARGS"
kurtosis enclave rm -f "$ENCLAVE" >/dev/null 2>&1 || true
trap 'kurtosis enclave rm -f "$ENCLAVE"' EXIT
kurtosis run --enclave "$ENCLAVE" "$PACKAGE" --args-file "$ARGS"
if [[ ${KEEP:-0} == 1 ]]; then trap - EXIT; fi

rm -rf "$G"
kurtosis files download "$ENCLAVE" el_cl_genesis_data "$G" >/dev/null
UUID=$(kurtosis enclave inspect --full-uuids "$ENCLAVE" | sed -n 's/^UUID: *//p')
[[ -n $UUID ]] || { echo "FAIL: no UUID for enclave $ENCLAVE"; exit 1; }
port() { kurtosis port print "$ENCLAVE" "$1" "$2" | sed 's|.*:||'; }
container() {
  docker ps --filter "label=com.kurtosistech.enclave-id=$UUID" --filter "label=com.kurtosistech.id=$1" --format '{{.Names}}'
}
EL_RPC_PORT=$(port el-1-nethermind-lighthouse rpc)
CL_HTTP_PORT=$(port cl-1-lighthouse-nethermind http)
EL_CONTAINER=$(container el-1-nethermind-lighthouse)
BN_CONTAINER=$(container cl-1-lighthouse-nethermind)
VC_CONTAINER=$(container vc-1-nethermind-lighthouse)
for v in EL_RPC_PORT CL_HTTP_PORT EL_CONTAINER BN_CONTAINER VC_CONTAINER; do
  [[ ${!v} =~ ^[^[:space:]]+$ ]] || { echo "FAIL: $v='${!v}' in enclave $ENCLAVE"; exit 1; }
  echo "$v=${!v}"
done
export KURTOSIS_ENCLAVE=$ENCLAVE EL_RPC_PORT CL_HTTP_PORT EL_CONTAINER BN_CONTAINER VC_CONTAINER
for _ in $(seq 60); do
  curl -sf "http://127.0.0.1:$CL_HTTP_PORT/eth/v1/beacon/genesis" >/dev/null && break
  sleep 2
done
curl -sf "http://127.0.0.1:$CL_HTTP_PORT/eth/v1/beacon/genesis" >/dev/null || { echo "FAIL: beacon API not up"; exit 1; }
jq -n --argjson ts "$(jq -r .timestamp "$G/genesis.json")" \
  --argjson t8288 "$(jq -r .config.eip8288PrototypeTime "$G/genesis.json")" \
  --argjson epoch "$(sed -n 's/^DAISUGI_FORK_EPOCH: *//p' "$G/config.yaml")" \
  --arg hash "$(curl -sf "http://127.0.0.1:$CL_HTTP_PORT/eth/v2/debug/beacon/states/genesis" | jq -r .data.latest_execution_payload_header.block_hash)" \
  '{genesisTimestamp: $ts, eip8288PrototypeTime: $t8288, daisugiForkEpoch: $epoch, elGenesisHash: $hash}' > "$G/params.json"
cat "$G/params.json"

../check.sh "$G"

for i in $(seq "${PQ_SERIES:-3}"); do
  for scheme in ${PQ_SCHEMES:-sphincs}; do
    echo "== pq tx $i $scheme"
    PQ_SCHEME=$scheme PQ_KEY=$PQ_KEY CHAIN_ID=$(jq -r .config.chainId "$G/genesis.json") PORT=$EL_RPC_PORT OUT="$G/pq-$i-$scheme" ../pq.sh
  done
done
