# Geth-PR5 State Dump

**Generated:** Mon Feb 16 2026 23:03 IST

## Summary

Attempted to sync geth-pr5 (from `/root/workspace/go-ethereum-pr5`, branch `feature/xdpos-consensus`) on XDC mainnet to query historical state at block 1800.

### Status
- **Build:** ✅ Successful (XDC binary built)
- **Genesis Init:** ✅ Successful (hash matches: `0x4a9d748bd78a8d0385b67788c2435dcdb914f98a96250b68863a1f8b7642d6b1`)
- **XDPoS V2 Config:** ✅ Added to genesis (switchBlock: 80370000)
- **Archive Mode:** ✅ Enabled (`--gcmode archive`)
- **P2P Sync:** ❌ Connection issues - peer handshakes failing

### Issue Encountered
The geth-pr5 node successfully discovers peers via UDP but cannot establish TCP connections. The sync briefly worked (synced 64 blocks in Docker network) but then the connection dropped with error:
```
ERROR XDC sync: failed to request bodies err="write tcp 172.20.0.5:30303->172.20.0.3:52488: use of closed network connection"
```

Possible causes:
1. Protocol-level incompatibility between geth-pr5 and xdc-node (XinFinOrg/XDPoSChain:v2.6.8)
2. XDPoS consensus handshake requirements not fully implemented in geth-pr5
3. Network configuration issues between host and Docker network

## Genesis Block (Block 0) State

### Block Info
```json
{
  "hash": "0x4a9d748bd78a8d0385b67788c2435dcdb914f98a96250b68863a1f8b7642d6b1",
  "stateRoot": "0x49be235b0098b048f9805aed38a279d8c189b469ff9ba307b39c7ad3a3bc55ae",
  "number": "0x0",
  "timestamp": "0x5cefae27"
}
```

### Account States at Block 0

| Address | Balance | Nonce | Has Code |
|---------|---------|-------|----------|
| 0x0000000000000000000000000000000000000000 | 0x0 | 0x0 | No |
| 0x0000000000000000000000000000000000000001 | 0x0 | 0x0 | No |
| 0x0000000000000000000000000000000000000088 | 0x18d0bf423c03d8de000000 | 0x0 | Yes (28906 chars) |
| 0x0000000000000000000000000000000000000089 | 0x0 | 0x0 | Yes (1698 chars) |
| 0x0000000000000000000000000000000000000090 | 0x0 | 0x0 | Yes (1646 chars) |
| 0x0000000000000000000000000000000000000099 | 0x0 | 0x0 | Yes (10884 chars) |
| 0x54d4369719bf06b194c32f8be57e2605dd5b59e5 | 0x7912752226cec5131e000000 | 0x0 | No |
| 0x746249c61f5832c5eed53172776b460491bdcd5c | 0x0 | 0x0 | Yes (10884 chars) |
| 0x381047523972c9fdc3aa343e0b96900a8e2fa765 | 0x0 | 0x0 | No |
| 0x92a289fe95a85c53b8d0d113cbaef0c1ec98ac65 | 0x0 | 0x0 | No |

### Balance Conversions (Block 0)
- **0x88 (MasterNode Contract):** 0x18d0bf423c03d8de000000 = 7,500,000,000 XDC (7.5B)
- **0x54d4...59e5:** 0x7912752226cec5131e000000 = 9,750,000,000,000 XDC (9.75T)

## Blocks 1799 and 1800 State

**NOT AVAILABLE** - Unable to sync to block 1800 due to P2P connection issues.

## Recommendations

1. **Investigate geth-pr5 P2P compatibility** - The XDPoS consensus handshake may have compatibility issues with the official xdc-node
2. **Try alternative sync sources** - Use XDC public bootnodes or archive nodes
3. **Check xdc-node logs** - Look for peer rejection/drop messages
4. **Consider using official XDC client** - If geth-pr5 continues to have issues, use XinFinOrg/XDPoSChain with archive mode

## Technical Details

### Build Command
```bash
cd /root/workspace/go-ethereum-pr5 && make XDC
```

### Run Command (Archive Mode)
```bash
/root/workspace/go-ethereum-pr5/build/bin/XDC \
  --datadir /root/geth-pr5-archive \
  --gcmode archive \
  --syncmode full \
  --port 30307 \
  --http --http.addr 0.0.0.0 --http.port 8557 \
  --http.api eth,net,web3,debug,admin,txpool \
  --networkid 50
```

### Genesis Configuration Used
```json
{
  "config": {
    "chainId": 50,
    "XDPoS": {
      "period": 2,
      "epoch": 900,
      "reward": 5000,
      "rewardCheckpoint": 900,
      "gap": 450,
      "foudationWalletAddr": "0x92a289fe95a85c53b8d0d113cbaef0c1ec98ac65",
      "v2": {
        "SwitchBlock": 80370000,
        "MinePeriod": 2,
        "TimeoutPeriod": 30,
        "TimeoutSyncThreshold": 3,
        "CertThreshold": 67
      }
    }
  }
}
```

### Peer Connection Attempt
```bash
enode://a7b52a10f3b6c6bb26c2c2f1da7ff9cf8492e0188d8eeb8e9423d73fa2a18981b6ab134df11f62db3d6039dc3199cad25967dcdf93fed84744e8eaffd0c723db@172.20.0.3:30303
```
