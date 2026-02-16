#!/bin/bash
# XDC Mainnet Launch Script for Nethermind

export PATH="/usr/local/dotnet:$PATH"
export DOTNET_ROOT="/usr/local/dotnet"

# XDC Mainnet Configuration
NETWORK_ID=50
CHAIN_ID=50

# Data directory
DATA_DIR="${HOME}/.nethermind/xdc-mainnet"

# Create data directory
mkdir -p "${DATA_DIR}"

cd /root/.openclaw/workspace/nethermind

# Run Nethermind with XDC mainnet settings
dotnet run --project src/Nethermind/Nethermind.Runner \
    -- --config xdc-mainnet \
    --datadir "${DATA_DIR}" \
    --Network.DiscoveryPort 30303 \
    --Network.P2PPort 30303 \
    --JsonRpc.Enabled true \
    --JsonRpc.Port 8545 \
    --JsonRpc.Host 127.0.0.1 \
    --Sync.FastSync true \
    --log DEBUG \
    2>&1 | tee "${DATA_DIR}/nethermind.log"
