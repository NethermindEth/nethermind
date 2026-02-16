#!/bin/bash
# Helper script to get enode addresses from geth-xdc nodes

echo "=== XDC Geth Node Enode Discovery ==="
echo ""

GETH_NODES=(
  "95.217.56.168:8545"  # Local server
  "65.21.27.213:8545"   # Prod server
)

for node in "${GETH_NODES[@]}"; do
  echo "Querying $node..."
  
  enode=$(curl -s -X POST -H "Content-Type: application/json" \
    --connect-timeout 5 \
    --data '{"jsonrpc":"2.0","method":"admin_nodeInfo","params":[],"id":1}' \
    http://$node 2>/dev/null | jq -r '.result.enode' 2>/dev/null)
  
  if [ $? -eq 0 ] && [ "$enode" != "null" ] && [ -n "$enode" ]; then
    echo "  ✓ $enode"
  else
    echo "  ✗ Failed to get enode (node may not have admin API enabled)"
    echo "    Try checking if the node is running and admin API is enabled"
  fi
  echo ""
done

echo "=== Instructions ==="
echo "1. If enodes were found, copy them to create static-nodes.json"
echo "2. Create: nethermind_db/xdc/static-nodes.json"
echo "3. Format: [\"enode://...\", \"enode://...\"]"
echo ""
echo "Or add them to the launch script:"
echo "  ./run-xdc-debug.sh --Network.StaticPeers \"enode://ID@IP:PORT,enode://ID@IP:PORT\""
