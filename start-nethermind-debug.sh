#!/bin/bash
# Start Nethermind-XDC with DEBUG logging for detailed diagnostics
#
# This script enables debug-level logs to troubleshoot peer connectivity
# and sync issues.

set -euo pipefail

NETHERMIND_DIR="$(dirname "$0")/src/Nethermind/artifacts/bin/Nethermind.Runner/release"
DATA_DIR="${XDC_DATA_DIR:-/root/.nethermind-xdc-test}"
LOG_FILE="${DATA_DIR}/debug-$(date +%Y%m%d-%H%M%S).log"

# Static peers (your 3 geth servers)
STATIC_PEERS="enode://362c05688c81b60f20b7b11c4c44a991136b00863947b1fb2d1b8cf1c015b60e0b55eed9889ecc252e637827a646163bb76c2c65813bedfedb65080b31b35a01@95.217.56.168:30303,enode://d0b7e994ff858dab18f464cb13ef96a2bbc8ab4478b82f89a2adda05fa2a44d8296f1d7e104bc8a7e062c7765435a3c8a0299289c3d065b1ba2652dcb0723835@65.21.27.213:30303,enode://f3d4d5da1cf3df1116d8344d4cc698a1a7256118633c52f3e053ea8323595a062601494971f3a4b02d4fdcbd156488bc2ebd32dc26a77a2aa9f79c5a3a5c2edc@175.110.113.12:30303"

# Ensure .NET 9 is in PATH
export PATH="/usr/local/dotnet:$PATH"

# Create data directory
mkdir -p "$DATA_DIR"

# Change to Nethermind directory
cd "$NETHERMIND_DIR"

echo "==================================================================="
echo "Starting Nethermind-XDC with DEBUG logging"
echo "==================================================================="
echo "Data directory: $DATA_DIR"
echo "Log file:       $LOG_FILE"
echo "RPC endpoint:   http://0.0.0.0:8548"
echo "P2P port:       30305"
echo "Chain:          XDC Mainnet (ID: 50)"
echo ""
echo "Static peers (3 geth nodes):"
echo "  - 95.217.56.168:30303 (Server 168)"
echo "  - 65.21.27.213:30303  (Server 213)"
echo "  - 175.110.113.12:30303 (Server 112)"
echo "==================================================================="
echo ""
echo "Debug logging enabled - watching for:"
echo "  - P2P connection attempts"
echo "  - Protocol handshakes"
echo "  - Peer discovery"
echo "  - Sync mode selection"
echo "  - Block requests/responses"
echo "==================================================================="
echo ""

# Launch Nethermind with DEBUG logging
dotnet nethermind.dll \
  --config xdc \
  --data-dir "$DATA_DIR" \
  --JsonRpc.Enabled true \
  --JsonRpc.Host 0.0.0.0 \
  --JsonRpc.Port 8548 \
  --Network.DiscoveryPort 30305 \
  --Network.P2PPort 30305 \
  --Network.StaticPeers "$STATIC_PEERS" \
  --log Debug 2>&1 | tee "$LOG_FILE"
