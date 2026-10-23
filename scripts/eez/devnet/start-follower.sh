#!/usr/bin/env sh
set -eu
set -a
# shellcheck source=/dev/null
. /out/deployments.env
set +a
: "${NETHERMIND_L1_RPC:?}"
: "${NETHERMIND_L1_CHAIN_ID:?}"
: "${EEZ_REGISTRY_ADDRESS:?}"
: "${EEZ_REGISTRY_DEPLOY_BLOCK:?}"
: "${EEZ_ROLLUP_ID:?}"
set --
if [ "${EEZ_PROFILE:-rollup0}" = rollupx ]; then
    set -- --Eez.L1BeaconUrl "${NETHERMIND_L1_BEACON:?}"
fi
exec /nethermind/entrypoint.sh \
    --Init.ChainSpecPath /out/l2-genesis.json \
    --Init.BaseDbPath /data \
    --Init.LogDirectory /logs \
    --Eez.Enabled true \
    --Eez.SkipConsensusLayerCalls "${EEZ_SKIP_CONSENSUS_LAYER_CALLS:-true}" \
    --Eez.FollowerEnabled true \
    --Eez.L1RpcUrl "$NETHERMIND_L1_RPC" \
    --Eez.L1ChainId "$NETHERMIND_L1_CHAIN_ID" \
    --Eez.RegistryAddress "$EEZ_REGISTRY_ADDRESS" \
    --Eez.RegistryDeployBlock "$EEZ_REGISTRY_DEPLOY_BLOCK" \
    --Eez.RollupId "$EEZ_ROLLUP_ID" \
    --Eez.L1PollingIntervalMs 500 \
    --Eez.L2BlockTimeSeconds 2 \
    --Eez.L1LogScanBlocks 128 \
    --Sync.FastSync false \
    --Sync.SnapSync false \
    --Init.DiscoveryEnabled false \
    --Pruning.Mode None \
    --Receipt.TxLookupLimit 0 \
    --JsonRpc.Enabled true \
    --JsonRpc.Host 0.0.0.0 \
    --JsonRpc.Port 8545 \
    --JsonRpc.EnabledModules Eth,Net,Web3,Debug,Trace "$@"
