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
ENODE=$(docker logs daisugi-nethermind 2>&1 | grep -aoE 'enode://[0-9a-f]+@' | head -1)$(ip daisugi-nethermind):30303
PEER_ID=$(curl -sf http://127.0.0.1:25052/eth/v1/node/identity | jq -r .data.peer_id)
export BEACON_MULTIADDR=/ip4/$(ip daisugi-beacon)/tcp/9000/p2p/$PEER_ID

mkdir -p "$GENESIS_DIR/sync-$PAIR"/{nethermind,beacon}
echo "[\"$ENODE\"]" > "$GENESIS_DIR/sync-$PAIR/nethermind/static-nodes.json"
echo "EL peer $ENODE"
echo "CL peer $BEACON_MULTIADDR"
docker compose --profile sync up -d "nethermind-$PAIR" "beacon-$PAIR"
