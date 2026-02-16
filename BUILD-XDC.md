# Building Nethermind-XDC (.NET 9 Stable)

This branch (`build/xdc-net9-stable`) contains a stable build of Nethermind with full XDPoS support that compiles with .NET 9 SDK.

## Why This Commit?

**Commit:** `959ce1837d` (Nov 19, 2025 - "XDC README.md #9710")

The main `feature/xdc-network` branch merged upstream Nethermind's .NET 10 migration (commit `ccb19b41f8`), which requires:
- .NET 10 SDK (preview/unreleased)
- `Nethermind.MclBindings` 1.0.5 (net10.0 only)
- `Nethermind.Numerics.Int256` 1.4.0 (net10.0 only)

This commit is the **last stable version before .NET 10 migration** with:
- ✅ .NET 9.0 support
- ✅ Full XDPoS v1 + v2 implementation (`Nethermind.Xdc` module)
- ✅ Compatible dependencies (MclBindings 1.0.3, Int256 1.3.6)
- ✅ Production-ready XDC mainnet support

## Prerequisites

### Install .NET 9 SDK

```bash
cd /tmp
wget https://dot.net/v1/dotnet-install.sh
chmod +x dotnet-install.sh
./dotnet-install.sh --channel 9.0 --install-dir /usr/local/dotnet
export PATH="/usr/local/dotnet:$PATH"
```

Verify installation:
```bash
dotnet --list-sdks
# Should show: 9.0.311 [/usr/local/dotnet/sdk]
```

## Build Instructions

### 1. Clone and checkout this branch

```bash
git clone git@github.com:AnilChinchawale/nethermind.git
cd nethermind
git checkout build/xdc-net9-stable
```

### 2. Build Nethermind Runner

```bash
export PATH="/usr/local/dotnet:$PATH"
cd src/Nethermind/Nethermind.Runner
dotnet build -c Release
```

Build output: `src/Nethermind/artifacts/bin/Nethermind.Runner/release/`

**Expected result:**
- ✅ Build succeeded
- ✅ 0 Warning(s), 0 Error(s)
- ✅ Time: ~30 seconds
- ✅ Binary: `nethermind.dll`
- ✅ XDC module: `Nethermind.Xdc.dll`

## Running XDC Mainnet

### Configuration Files

**Chainspec:** `chainspec/xdc.json`
- Chain ID: 50
- XDPoS v1 + v2 parameters
- Genesis block: `0x4a9d748bd78a8d0385b67788c2435dcdb914f98a96250b68863a1f8b7642d6b1`

**Config:** `configs/xdc.json`
- FastSync + SnapSync enabled
- Pivot block: 80,370,000

### Quick Start

```bash
cd src/Nethermind/artifacts/bin/Nethermind.Runner/release

dotnet nethermind.dll \
  --config xdc \
  --data-dir /path/to/datadir \
  --JsonRpc.Enabled true \
  --JsonRpc.Host 0.0.0.0 \
  --JsonRpc.Port 8548 \
  --Network.DiscoveryPort 30305 \
  --Network.P2PPort 30305
```

### Adding Bootnodes

Create `static-nodes.json` in your data directory:

```json
[
  "enode://PUBKEY@IP:30303",
  "enode://PUBKEY@IP:30303"
]
```

Or use `--Network.StaticPeers` flag:
```bash
--Network.StaticPeers "enode://...,enode://..."
```

### Test Script

A convenience script is provided:

```bash
chmod +x test-xdc-mainnet.sh
./test-xdc-mainnet.sh
```

## XDPoS Implementation

The `Nethermind.Xdc` module includes:

**Core Components:**
- `XdcBlockProducer.cs` - Block production with XDPoS
- `XdcBlockValidator.cs` - XDPoS-specific validation
- `XdcHeaderValidator.cs` - Header validation rules
- `XdcSealEngine.cs` - Seal verification
- `XdcChainSpecEngineParameters.cs` - Chainspec parameters

**Consensus:**
- `Vote/` - Voting mechanism (67+ files)
- `Timeout/` - Timeout handling
- `Propose/` - Block proposal
- `MasterNode/` - Masternode management

**Features:**
- ✅ XDPoS v1 (pre-block 80,370,000)
- ✅ XDPoS v2 (post-block 80,370,000)
- ✅ 108 masternodes
- ✅ 2-second block time
- ✅ 900-block epochs
- ✅ Certificate-based finality (v2)

## Troubleshooting

### Build Errors

**Error:** `Package Nethermind.MclBindings 1.0.5 is not compatible with net9.0`

This means you're on a commit after the .NET 10 migration. Checkout this branch:
```bash
git checkout build/xdc-net9-stable
```

**Error:** `The current .NET SDK does not support targeting .NET 9.0`

Install .NET 9 SDK (see Prerequisites above).

### Runtime Errors

**Warning:** `Could not communicate with any nodes (bootnodes, trusted nodes, persisted nodes)`

The chainspec doesn't include default bootnodes. Add static peers manually (see "Adding Bootnodes" above).

**Error:** Port conflicts

Adjust ports if default ports are in use:
- RPC: `--JsonRpc.Port 8548`
- P2P: `--Network.P2PPort 30305`
- Discovery: `--Network.DiscoveryPort 30305`

## Version Information

- **Nethermind:** v1.36.0-unstable+959ce183
- **XDC Chainspec:** v2.6.8 compatible
- **.NET:** 9.0.311
- **Language:** C# 13.0
- **Commit:** 959ce1837d
- **Date:** November 19, 2025

## Production Deployment

For production use:
1. Use systemd service or PM2 for process management
2. Configure UFW/firewall (allow ports 30305, 8548)
3. Set up monitoring (Prometheus/Grafana compatible)
4. Regular backups of data directory
5. Use SSD/NVMe storage for best performance

## References

- **XDC Network:** https://xdc.org
- **Nethermind Docs:** https://docs.nethermind.io
- **XDPoS Specification:** (in chainspec/xdc.json)
- **GitHub Repository:** https://github.com/AnilChinchawale/nethermind

## License

LGPL-3.0 (same as upstream Nethermind)

---

**Built by:** Anil Chinchawale (@AnilChinchawale)  
**Date:** February 16, 2026  
**Status:** Production-ready for XDC mainnet
