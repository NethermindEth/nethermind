# XDC Mainnet Configuration for Nethermind

## Configuration Summary

### ✅ Completed Configuration

1. **Runner Config** (`src/Nethermind/Nethermind.Runner/configs/xdc.json`)
   - ✅ ChainSpec path: `chainspec/xdc.json`
   - ✅ Network ports: P2P 30303, Discovery 30303
   - ✅ JSON-RPC enabled on 0.0.0.0:8545
   - ✅ Full sync mode (FastSync: false, SnapSync: false)
   - ✅ Debug logging enabled

2. **Chainspec** (`src/Nethermind/Chains/xdc.json`)
   - ✅ NetworkId: 50
   - ✅ ChainId: 50
   - ✅ XDPoS engine configured
   - ✅ 108+ XDC mainnet bootnodes configured

3. **Launch Script** (`run-xdc-debug.sh`)
   - ✅ Debug logging enabled
   - ✅ Bootnodes configured
   - ✅ JSON-RPC enabled

## Quick Start

### Launch Nethermind on XDC Mainnet

```bash
cd /root/.openclaw/workspace/nethermind
./run-xdc-debug.sh
```

### Optional: Add Custom Bootnodes

To add additional bootnodes at runtime:

```bash
./run-xdc-debug.sh --Network.Bootnodes "enode://YOUR_ENODE_HERE@IP:PORT"
```

## Static Peers Configuration

### Adding Geth Nodes as Static Peers

To connect to specific geth-xdc nodes (like the ones on 95.217.56.168 and 65.21.27.213), you need their enode addresses.

#### Step 1: Get Enode Addresses from Geth Nodes

On each geth server, run:

```bash
# If geth is running with IPC enabled
geth attach /path/to/geth.ipc --exec "admin.nodeInfo.enode"

# Or via HTTP RPC
curl -X POST -H "Content-Type: application/json" \
  --data '{"jsonrpc":"2.0","method":"admin_nodeInfo","params":[],"id":1}' \
  http://95.217.56.168:8545 | jq -r '.result.enode'
```

#### Step 2: Create Static Nodes Configuration

Create a file at the Nethermind data directory with the enode addresses:

```bash
# For XDC mainnet, create:
# nethermind_db/xdc/static-nodes.json

cat > nethermind_db/xdc/static-nodes.json <<EOF
[
  "enode://ENODE_ID_1@95.217.56.168:30303",
  "enode://ENODE_ID_2@65.21.27.213:30303"
]
EOF
```

Replace `ENODE_ID_1` and `ENODE_ID_2` with the actual enode IDs from Step 1.

#### Alternative: Use Command Line

You can also specify static peers via command line:

```bash
./run-xdc-debug.sh --Network.StaticPeers "enode://ID@95.217.56.168:30303,enode://ID@65.21.27.213:30303"
```

## Network Configuration

### Ports Used

- **P2P Port**: 30303 (TCP/UDP)
- **Discovery Port**: 30303 (UDP)
- **JSON-RPC**: 8545 (HTTP)

### Firewall Rules

```bash
# Allow P2P connections
sudo ufw allow 30303/tcp
sudo ufw allow 30303/udp

# Allow JSON-RPC (only if needed externally)
sudo ufw allow 8545/tcp
```

## Monitoring

### Check Peer Connections

Use JSON-RPC to monitor peer connections:

```bash
# Get peer count
curl -X POST -H "Content-Type: application/json" \
  --data '{"jsonrpc":"2.0","method":"net_peerCount","params":[],"id":1}' \
  http://localhost:8545

# Get peer info
curl -X POST -H "Content-Type: application/json" \
  --data '{"jsonrpc":"2.0","method":"admin_peers","params":[],"id":1}' \
  http://localhost:8545
```

### Check Sync Status

```bash
curl -X POST -H "Content-Type: application/json" \
  --data '{"jsonrpc":"2.0","method":"eth_syncing","params":[],"id":1}' \
  http://localhost:8545
```

## Troubleshooting

### No Peers Connecting

1. Check firewall rules
2. Verify bootnodes are reachable
3. Check logs: `tail -f nethermind_db/xdc/xdc.logs.txt`
4. Try adding static peers manually

### Sync Issues

1. Ensure you're using full sync (FastSync: false)
2. Check if XDC network is healthy
3. Try connecting to known working peers

### eth/100 Protocol

Nethermind should automatically negotiate the correct protocol version with XDC peers. The eth/100 protocol is an extension used by XDC for XDPoS consensus.

## Agent Ports (SkyOne)

The configured agent port for Nethermind XDC: **7072**

```bash
# Proxy to port 7072 will reach Nethermind JSON-RPC on 8545
```

## Additional Resources

- XDC Network: https://xdc.network/
- Nethermind Docs: https://docs.nethermind.io/
- XDC Block Explorer: https://xdc.network/
