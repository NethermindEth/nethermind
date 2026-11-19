#!/usr/bin/env bash
# run-devnet.sh DATA_DIR — a two-node Arkiv devnet with no consensus client, for exercising block production:
# Nethermind (with the plugin, Testing RPC module on) and the reference arkiv-reth, both on Arkiv's genesis/dev.json
# (chain 1337, the Hardhat test accounts funded) with Rabdi's genesis predeploys added (the Prague system calls need
# them) and Osaka left out (Rabdi runs Prague). Blocks are then built and fed to both with tools/devnet_build.py.
#
# Environment: ARKIV_DEV_GENESIS (arkiv's genesis/dev.json), NETHERMIND (the Runner output directory, built with the Arkiv native library),
# ARKIV_RETH (the arkiv-reth binary). Ports: NM_RPC 38545,
# NM_ENGINE 38551, NM_P2P 31303, RETH_RPC 18645, RETH_ENGINE 18651, RETH_P2P 31304.
set -euo pipefail
data=$(realpath -m "$1"); mkdir -p "$data"/{nm,reth}
: "${ARKIV_DEV_GENESIS:?}" "${NETHERMIND:?}" "${ARKIV_RETH:?}"
NM_RPC=${NM_RPC:-38545}; NM_ENGINE=${NM_ENGINE:-38551}; NM_P2P=${NM_P2P:-31303}
RETH_RPC=${RETH_RPC:-18645}; RETH_ENGINE=${RETH_ENGINE:-18651}; RETH_P2P=${RETH_P2P:-31304}
chains="$NETHERMIND/chainspec"

[ -s "$data/jwt.hex" ] || { head -c 32 /dev/urandom | od -An -tx1 | tr -d ' \n' > "$data/jwt.hex"; chmod 600 "$data/jwt.hex"; }
jq --slurpfile r "$chains/rabdi.json" \
  'del(.config.osakaTime) | .alloc += ($r[0].alloc | with_entries(select(.value.code != null and .value.code != "0x")))' \
  "$ARKIV_DEV_GENESIS" > "$data/genesis.json"
# The genesis file is the chain spec too (Nethermind reads geth-style genesis files, the plugin its Arkiv section).
jq --arg genesis "$data/genesis.json" \
  '.Init.ChainSpecPath = $genesis | del(.Init.GenesisHash) | .Init.BaseDbPath = "nethermind_db/dev" | .Init.LogFileName = "dev.log"
   | .Arkiv.CheckpointInterval = 100 | .Network.StaticPeers = "" | .Network.MaxActivePeers = 8 | .Network.FilterPeersByRecentIp = false
   | .JsonRpc.EnabledModules += ["Testing"]' \
  "$NETHERMIND/configs/rabdi_archive.json" > "$data/nethermind.json"

setsid dotnet "$NETHERMIND/nethermind.dll" -c "$data/nethermind.json" -dd "$data/nm" \
  --JsonRpc.JwtSecretFile "$data/jwt.hex" --JsonRpc.Port "$NM_RPC" --JsonRpc.EnginePort "$NM_ENGINE" \
  --Network.P2PPort "$NM_P2P" --Network.DiscoveryPort "$NM_P2P" > "$data/nm.log" 2>&1 < /dev/null &
setsid env ARKIV_STORE_PATH="$data/reth/golemdb" "$ARKIV_RETH" node --chain "$data/genesis.json" --datadir "$data/reth" \
  --disable-discovery --max-outbound-peers 0 --max-inbound-peers 0 --port "$RETH_P2P" --ipcdisable \
  --http --http.port "$RETH_RPC" --authrpc.port "$RETH_ENGINE" --authrpc.jwtsecret "$data/jwt.hex" > "$data/reth.log" 2>&1 < /dev/null &
echo "logs in $data/{nm,reth}.log; then: JWT=$data/jwt.hex tools/devnet_build.py N"
