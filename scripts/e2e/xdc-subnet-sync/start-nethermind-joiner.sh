#!/usr/bin/env bash
set -euo pipefail

GENERATED_DIR="${GENERATED_DIR:-/Users/carmen/Documents/Work/xdc-subnet-test/generated}"
NETWORK_NAME="${NETWORK_NAME:-docker_net}"
CONTAINER_NAME="${CONTAINER_NAME:-nm-xdc-subnet-joiner}"
SUBNET1_CONTAINER_NAME="${SUBNET1_CONTAINER_NAME:-generated-subnet1-1}"
NM_IMAGE="${NM_IMAGE:-nethermind-xdc-local:dev-subnet2}"
HOST_RPC_PORT="${HOST_RPC_PORT:-18548}"
HOST_P2P_PORT="${HOST_P2P_PORT:-30333}"
SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
LOCAL_CFG="$SCRIPT_DIR/config/xdc-subnet-local.json"
CHAINSPEC_OUT="$SCRIPT_DIR/config/chainspec-xdc-subnet-local.json"
GEN_SCRIPT="$SCRIPT_DIR/generate-nm-subnet-chainspec.sh"

# Optional deterministic mode:
# STATIC_PEER_ENODE="enode://...@192.168.25.11:20303" ONLY_STATIC_PEERS=true bash start-nethermind-joiner.sh
STATIC_PEER_ENODE="${STATIC_PEER_ENODE:-}"
ONLY_STATIC_PEERS="${ONLY_STATIC_PEERS:-false}"

# Opinionated defaults for local subnet sync test.
ONLY_STATIC_PEERS="${ONLY_STATIC_PEERS:-true}"

if [[ ! -f "$GENERATED_DIR/genesis.json" ]]; then
  echo "Missing genesis: $GENERATED_DIR/genesis.json"
  exit 1
fi
if [[ ! -f "$GENERATED_DIR/subnet1.env" ]]; then
  echo "Missing env: $GENERATED_DIR/subnet1.env"
  exit 1
fi
if [[ ! -f "$LOCAL_CFG" ]]; then
  echo "Missing local config: $LOCAL_CFG"
  exit 1
fi
if [[ ! -x "$GEN_SCRIPT" ]]; then
  echo "Missing generator script: $GEN_SCRIPT"
  exit 1
fi

# Keep chainspec in sync with latest generated genesis
GENERATED_DIR="$GENERATED_DIR" OUT="$CHAINSPEC_OUT" "$GEN_SCRIPT"

BOOTNODES_LINE=$(grep '^BOOTNODES=' "$GENERATED_DIR/subnet1.env" || true)
if [[ -z "$BOOTNODES_LINE" ]]; then
  echo "BOOTNODES not found in $GENERATED_DIR/subnet1.env"
  exit 1
fi
BOOTNODES="${BOOTNODES_LINE#BOOTNODES=}"

JOINER_IP="${JOINER_IP:-192.168.25.2}"

# Auto-detect subnet1 enode if not explicitly provided.
if [[ -z "$STATIC_PEER_ENODE" ]] && docker ps --format '{{.Names}}' | grep -qx "$SUBNET1_CONTAINER_NAME"; then
  SUBNET1_IP=$(docker inspect -f '{{range .NetworkSettings.Networks}}{{.IPAddress}}{{end}}' "$SUBNET1_CONTAINER_NAME" || true)
  SUBNET1_NODE_ID=$(docker exec "$SUBNET1_CONTAINER_NAME" sh -lc \
    "bootnode -nodekey /work/xdcchain/XDC/nodekey -writeaddress 2>/dev/null" || true)

  if [[ -n "$SUBNET1_IP" ]] && [[ -n "$SUBNET1_NODE_ID" ]]; then
    STATIC_PEER_ENODE="enode://$SUBNET1_NODE_ID@$SUBNET1_IP:20303"
  fi
fi

echo "Using bootnodes: $BOOTNODES"
if [[ -n "$STATIC_PEER_ENODE" ]]; then
  echo "Using static peer mode with: $STATIC_PEER_ENODE"
fi
echo "Using joiner IP: $JOINER_IP"

if docker ps -a --format '{{.Names}}' | grep -qx "$CONTAINER_NAME"; then
  docker rm -f "$CONTAINER_NAME" >/dev/null
fi

mkdir -p "$GENERATED_DIR/nm-joiner-db"

CMD=(
  --config /nethermind/custom/xdc-subnet-local.json
  --Network.Bootnodes "$BOOTNODES"
  --JsonRpc.Enabled true
  --JsonRpc.Host 0.0.0.0
  --JsonRpc.Port 8545
  --JsonRpc.EnabledModules Eth,Net,Web3,Subscribe,Xdc,Rpc
  --Network.DiscoveryPort 30303
  --Network.P2PPort 30303
  --Sync.FastSync false
  --Sync.SnapSync false
  --Merge.Enabled false
  --Network.EnableEnrDiscovery false
  --Discovery.DiscoveryVersion V4
  --Network.LocalIp "$JOINER_IP"
  --Network.ExternalIp "$JOINER_IP"
)

if [[ -n "$STATIC_PEER_ENODE" ]]; then
  CMD+=(--Network.StaticPeers "$STATIC_PEER_ENODE")
  CMD+=(--Network.OnlyStaticPeers "$ONLY_STATIC_PEERS")
fi

docker run -d \
  --name "$CONTAINER_NAME" \
  --network "$NETWORK_NAME" \
  -p "$HOST_RPC_PORT:8545" \
  -p "$HOST_P2P_PORT:30303/tcp" \
  -p "$HOST_P2P_PORT:30303/udp" \
  -v "$CHAINSPEC_OUT:/nethermind/custom/chainspec-xdc-subnet-local.json:ro" \
  -v "$LOCAL_CFG:/nethermind/custom/xdc-subnet-local.json:ro" \
  -v "$GENERATED_DIR/nm-joiner-db:/nethermind/nethermind_db" \
  "$NM_IMAGE" \
  "${CMD[@]}"

echo "Started $CONTAINER_NAME"
echo "RPC: http://127.0.0.1:$HOST_RPC_PORT"
echo "P2P: $HOST_P2P_PORT"
echo "Tail logs: docker logs -f $CONTAINER_NAME"
