#!/usr/bin/env bash
# Starts an empty Nethermind + Lighthouse pair that joins a run.sh enclave (KEEP=1) late, only through P2P, and
# range-syncs from genesis, as `../sync.sh <dir> ... genesis` does for the Compose devnet. Its host ports are those of
# ../sync.sh, so ../sync-check.sh compares it with node 1. (The checkpoint pair needs the Compose beacon's hostname.)
# Usage: kurtosis/sync.sh <enclave>
set -euo pipefail
cd "$(dirname "$0")"
ENCLAVE=${1:?enclave}
G=$PWD/run/$ENCLAVE
[[ -f $G/genesis.json ]] || { echo "no run.sh output for enclave $ENCLAVE in $G" >&2; exit 1; }
UUID=$(kurtosis enclave inspect --full-uuids "$ENCLAVE" | sed -n 's/^UUID: *//p')
# With one participant ethereum-package starts the beacon node with --target-peers=0, which refuses the late pair.
kurtosis port print "$ENCLAVE" cl-2-lighthouse-nethermind http >/dev/null 2>&1 || { echo "needs an enclave with NODES >= 2" >&2; exit 1; }
container() {
  docker ps --filter "label=com.kurtosistech.enclave-id=$UUID" --filter "label=com.kurtosistech.id=$1" --format '{{.Names}}'
}
# ../docker-compose.yml mounts <genesis-dir>/data/metadata and <genesis-dir>/data/jwt.
mkdir -p "$G/data"
ln -sfn .. "$G/data/metadata"
[[ -f $G/data/jwt/jwtsecret ]] || { mkdir -p "$G/data/jwt"; openssl rand -hex 32 > "$G/data/jwt/jwtsecret"; }
export KURTOSIS_ENCLAVE=$ENCLAVE SYNC_NETWORK=kt-$ENCLAVE EL_CONTAINER=$(container el-1-nethermind-lighthouse) BN_CONTAINER=$(container cl-1-lighthouse-nethermind)
export CL_HTTP_PORT=$(kurtosis port print "$ENCLAVE" cl-1-lighthouse-nethermind http | sed 's|.*:||')
export COMPOSE_FILE=docker-compose.yml:lighthouse-distroless.override.yml:kurtosis/sync.override.yml
../sync.sh "$G" "${NETHERMIND_IMAGE:?}" "${LIGHTHOUSE_IMAGE:?}" genesis
