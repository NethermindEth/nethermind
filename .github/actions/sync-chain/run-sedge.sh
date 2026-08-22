#!/usr/bin/env bash
set -euo pipefail

case "${SYNC_MODE,,}" in
  halfpath) flatdb_enabled=false ;;
  flat)     flatdb_enabled=true ;;
  *)
    echo "Unsupported sync mode: ${SYNC_MODE}"
    exit 1
    ;;
esac

./build/sedge deps install

GENERIC_METRICS_FLAGS=(
  --el-extra-flag "Metrics.NodeName=${NODE_NAME}"
  --el-extra-flag Metrics.Enabled=true
  --el-extra-flag "Init.LogRules=Synchronization.Peers.SyncPeersReport:Debug"
)
OP_METRICS_FLAGS=(
  --el-op-extra-flag "Metrics.NodeName=${NODE_NAME}"
  --el-op-extra-flag Metrics.Enabled=true
  --el-op-extra-flag "Init.LogRules=Synchronization.Peers.SyncPeersReport:Debug"
)
L2_METRICS_FLAGS=(
  --el-l2-extra-flag "Metrics.NodeName=${NODE_NAME}"
  --el-l2-extra-flag Metrics.Enabled=true
  --el-l2-extra-flag "Init.LogRules=Synchronization.Peers.SyncPeersReport:Debug"
)

extra_param=()

if [[ "$NETWORK" == op-* || "$NETWORK" == world-* ]]; then
  if [[ "$NETWORK" == *mainnet* ]]; then
    consensus_url="$MAINNET_CONSENSUS_URL"
    execution_url="$MAINNET_EXECUTION_URL"
  elif [[ "$NETWORK" == *sepolia* ]]; then
    consensus_url="$SEPOLIA_CONSENSUS_URL"
    execution_url="$SEPOLIA_EXECUTION_URL"
  else
    echo "Unknown network: ${NETWORK}"
    exit 1
  fi

  [[ "$NETWORK" == world-* ]] && extra_param+=(--chain worldchain)

  stripped_network="${NETWORK#op-}"
  stripped_network="${stripped_network#world-}"
  echo "network=${NETWORK} resolved to L1 network=${stripped_network}"

  mkdir -p execution-data-op/logs/configs
  mv ../tests/predefined_configs/customNLog.config execution-data-op/logs/configs/customNLog.config

  if [ -n "$NETHERMIND_ARGS" ]; then
    for f in $NETHERMIND_ARGS; do extra_param+=(--el-op-extra-flag "$f"); done
  fi

  ./build/sedge generate \
    --logging none \
    -p "$GITHUB_WORKSPACE/sedge" \
    op-full-node \
    --op-execution "opnethermind:${DOCKER_IMAGE}" \
    --op-image op-node:us-docker.pkg.dev/oplabs-tools-artifacts/images/op-node:latest \
    --map-all \
    --network "$stripped_network" \
    --consensus-url "$consensus_url" \
    --execution-api-url "$execution_url" \
    --el-op-extra-flag "FlatDb.Enabled=${flatdb_enabled}" \
    --el-op-extra-flag Sync.NonValidatorNode=true \
    --el-op-extra-flag Sync.DownloadBodiesInFastSync=false \
    --el-op-extra-flag Sync.DownloadReceiptsInFastSync=false \
    --el-op-extra-flag loggerConfigSource=/nethermind/data/logs/configs/customNLog.config \
    --el-op-extra-flag Sync.VerifyTrieOnStateSyncFinished=true \
    "${OP_METRICS_FLAGS[@]}" \
    "${extra_param[@]}"

elif [[ "$NETWORK" == taiko-* ]]; then
  if [[ "$NETWORK" == *alethia* ]]; then
    consensus_url="$MAINNET_CONSENSUS_URL"
    execution_url="$MAINNET_EXECUTION_URL"
    stripped_network=mainnet
  elif [[ "$NETWORK" == *hoodi* ]]; then
    consensus_url="$HOODI_CONSENSUS_URL"
    execution_url="$HOODI_EXECUTION_URL"
    stripped_network=hoodi
  else
    echo "Unknown network: ${NETWORK}"
    exit 1
  fi

  mkdir -p execution-data-taiko/logs/configs
  mv ../tests/predefined_configs/customNLog.config execution-data-taiko/logs/configs/customNLog.config

  if [ -n "$NETHERMIND_ARGS" ]; then
    for f in $NETHERMIND_ARGS; do extra_param+=(--el-l2-extra-flag "$f"); done
  fi

  ./build/sedge generate \
    --logging none \
    -p "$GITHUB_WORKSPACE/sedge" \
    taiko-full-node \
    --l2-execution "taiko-nethermind:${DOCKER_IMAGE}" \
    --taiko-image taiko:us-docker.pkg.dev/evmchain/images/taiko-client:latest \
    --map-all \
    --network "$stripped_network" \
    --consensus-url "$consensus_url" \
    --execution-api-url "$execution_url" \
    --el-l2-extra-flag "FlatDb.Enabled=${flatdb_enabled}" \
    --el-l2-extra-flag Sync.NonValidatorNode=true \
    --el-l2-extra-flag Sync.DownloadBodiesInFastSync=false \
    --el-l2-extra-flag Sync.DownloadReceiptsInFastSync=false \
    --el-l2-extra-flag loggerConfigSource=/nethermind/data/logs/configs/customNLog.config \
    --el-l2-extra-flag Sync.VerifyTrieOnStateSyncFinished=true \
    "${L2_METRICS_FLAGS[@]}" \
    "${extra_param[@]}"

else
  mkdir -p execution-data/logs/configs
  mv ../tests/predefined_configs/customNLog.config execution-data/logs/configs/customNLog.config

  if [ -n "$NETHERMIND_ARGS" ]; then
    for f in $NETHERMIND_ARGS; do extra_param+=(--el-extra-flag "$f"); done
  fi

  ./build/sedge generate \
    --logging none \
    -p "$GITHUB_WORKSPACE/sedge" \
    full-node \
    -c "${CL_CLIENT}:${CL_IMAGE}" \
    -e "nethermind:${DOCKER_IMAGE}" \
    --map-all \
    --no-mev-boost \
    --no-validator \
    --network "$NETWORK" \
    --el-extra-flag "FlatDb.Enabled=${flatdb_enabled}" \
    --el-extra-flag Sync.NonValidatorNode=true \
    --el-extra-flag Sync.DownloadBodiesInFastSync=false \
    --el-extra-flag Sync.DownloadReceiptsInFastSync=false \
    --el-extra-flag 'JsonRpc.EnabledModules=[Eth,Subscribe,Trace,TxPool,Web3,Personal,Proof,Net,Parity,Health,Rpc,Debug]' \
    --el-extra-flag Sync.VerifyTrieOnStateSyncFinished=true \
    --el-extra-flag loggerConfigSource=/nethermind/data/logs/configs/customNLog.config \
    --el-extra-flag Sync.SnapSync=true \
    "${GENERIC_METRICS_FLAGS[@]}" \
    "${extra_param[@]}" \
    "--checkpoint-sync-url=${CHECKPOINT_URL}"
fi

./build/sedge run -p "$GITHUB_WORKSPACE/sedge"
