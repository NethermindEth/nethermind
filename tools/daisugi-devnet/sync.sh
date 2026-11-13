#!/usr/bin/env bash
# Starts an empty Nethermind + Lighthouse pair that joins the running devnet only through P2P.
# Usage: sync.sh <genesis-dir> <nethermind-image> <lighthouse-image> genesis|checkpoint
#   genesis:    Lighthouse range-syncs from genesis over libp2p and feeds its EL each payload over the Engine API.
#   checkpoint: Lighthouse checkpoint-syncs from the original beacon node; its EL gets a head with no history and
#               must fetch headers and bodies over devp2p.
set -euo pipefail
cd "$(dirname "$0")"
export GENESIS_DIR=$(realpath "${1:?genesis dir}") NETHERMIND_IMAGE=${2:?nethermind image} LIGHTHOUSE_IMAGE=${3:?lighthouse image}
PAIR=${4:?genesis or checkpoint}
export DEVNET_USER="$(id -u):$(id -g)"

ip() { docker inspect -f '{{range .NetworkSettings.Networks}}{{.IPAddress}}{{end}}' "$1"; }
# Nethermind logs its enode with an unresolved external IP, so the container address replaces it.
EL_CONTAINER=${EL_CONTAINER:-${DEVNET_PREFIX:-daisugi}-nethermind} BN_CONTAINER=${BN_CONTAINER:-${DEVNET_PREFIX:-daisugi}-beacon}
# A Kurtosis container (<service>--<uuid>) logs to the enclave's aggregator; `docker logs` shows only its first lines.
logs() { if [[ -n ${KURTOSIS_ENCLAVE:-} ]]; then kurtosis service logs -a "$KURTOSIS_ENCLAVE" "${1%%--*}"; else docker logs "$1"; fi 2>&1; }
ENODE=$(logs $EL_CONTAINER | grep -aoE 'enode://[0-9a-f]+@' | head -1)
[[ -n $ENODE ]] || { echo "no enode in the logs of $EL_CONTAINER" >&2; exit 1; }
ENODE=$ENODE$(ip $EL_CONTAINER):30303
PEER_ID=$(curl -sf http://127.0.0.1:${CL_HTTP_PORT:-25052}/eth/v1/node/identity | jq -r .data.peer_id)
export BEACON_MULTIADDR=/ip4/$(ip $BN_CONTAINER)/tcp/9000/p2p/$PEER_ID

mkdir -p "$GENESIS_DIR/sync-$PAIR"/{nethermind,beacon}
echo "[\"$ENODE\"]" > "$GENESIS_DIR/sync-$PAIR/nethermind/static-nodes.json"
echo "EL peer $ENODE"
echo "CL peer $BEACON_MULTIADDR"
docker compose --profile sync up -d "nethermind-$PAIR" "beacon-$PAIR"
