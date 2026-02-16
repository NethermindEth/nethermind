#!/bin/bash
# Nethermind-XDC Mainnet Test Script
# 
# This script launches Nethermind with XDPoS support on XDC mainnet.
# Ports are configured to avoid conflicts with existing geth/erigon nodes.
#
# Default ports:
#   RPC:       8548 (geth uses 8989, erigon uses 8547)
#   P2P:       30305 (geth uses 30303, erigon uses 30304)
#   Discovery: 30305
#
# Usage:
#   ./test-xdc-mainnet.sh
#
# To add static peers, create a static-nodes.json file in DATA_DIR:
#   echo '["enode://PUBKEY@IP:30303"]' > $DATA_DIR/static-nodes.json

set -euo pipefail

NETHERMIND_DIR="$(dirname "$0")/src/Nethermind/artifacts/bin/Nethermind.Runner/release"
DATA_DIR="${XDC_DATA_DIR:-/root/.nethermind-xdc-test}"

# Ensure .NET 9 is in PATH
export PATH="/usr/local/dotnet:$PATH"

# Verify .NET SDK
if ! command -v dotnet &> /dev/null; then
    echo "Error: dotnet not found. Install .NET 9 SDK first."
    echo "See BUILD-XDC.md for instructions."
    exit 1
fi

# Create data directory
mkdir -p "$DATA_DIR"

# Change to Nethermind directory
cd "$NETHERMIND_DIR"

echo "==================================================================="
echo "Starting Nethermind-XDC on mainnet"
echo "==================================================================="
echo "Data directory: $DATA_DIR"
echo "RPC endpoint:   http://0.0.0.0:8548"
echo "P2P port:       30305"
echo "Chain:          XDC Mainnet (ID: 50)"
echo "==================================================================="
echo ""

# Launch Nethermind
dotnet nethermind.dll \
  --config xdc \
  --data-dir "$DATA_DIR" \
  --JsonRpc.Enabled true \
  --JsonRpc.Host 0.0.0.0 \
  --JsonRpc.Port 8548 \
  --Network.DiscoveryPort 30305 \
  --Network.P2PPort 30305 \
  --log Info
