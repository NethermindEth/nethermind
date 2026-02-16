# Nethermind-XDC

**Full XDPoS v1 + v2 implementation for XDC Network**

This is a fork of [Nethermind](https://github.com/NethermindEth/nethermind) with complete XDPoS (XinFin Delegated Proof of Stake) consensus support for the [XDC Network](https://xdc.org).

## Quick Start

```bash
# Build
git checkout build/xdc-net9-stable
cd src/Nethermind/Nethermind.Runner
dotnet build -c Release

# Run
./test-xdc-mainnet.sh
```

📖 **Full documentation:** [BUILD-XDC.md](BUILD-XDC.md)

## Features

✅ **XDPoS Consensus**
- XDPoS v1 (pre-block 80,370,000)
- XDPoS v2 with certificate-based finality (post-block 80,370,000)
- 108 masternode support
- 2-second block time
- 900-block epochs

✅ **Network Support**
- XDC Mainnet (Chain ID: 50)
- Apothem Testnet support
- Fast sync + snap sync
- P2P protocol eth/62, eth/63, eth/100

✅ **Production Ready**
- Full state sync from genesis or pivot
- Archive node support
- RPC API compatible with geth
- Prometheus metrics

## Why Nethermind for XDC?

1. **Client Diversity** - XDC Network currently has only geth-based clients. Nethermind brings:
   - Different codebase (C# vs Go)
   - Different execution engine
   - Enhanced security through diversity

2. **Performance** - Nethermind optimizations:
   - Faster sync (snap sync + optimized state download)
   - Lower memory usage
   - Better pruning strategies

3. **Features** - Additional capabilities:
   - Built-in monitoring (Prometheus/Grafana)
   - Advanced RPC modules
   - Better developer tooling

## Architecture

```
Nethermind.Xdc/              # XDPoS implementation
├── XdcSealEngine.cs         # Core consensus engine
├── XdcBlockProducer.cs      # Block production
├── XdcBlockValidator.cs     # Block validation
├── XdcHeaderValidator.cs    # Header validation
├── Vote/                    # Voting mechanism
├── Timeout/                 # Timeout handling
├── Propose/                 # Block proposals
└── MasterNode/              # Masternode management

Chains/xdc.json              # XDC chainspec (genesis + parameters)
```

## Branch Strategy

- **`feature/xdc-network`** - Latest development (requires .NET 10)
- **`build/xdc-net9-stable`** - Stable production build (this branch, .NET 9)

This branch tracks commit `959ce1837d` which is the last stable version before upstream's .NET 10 migration.

## XDC Network Stats

- **Mainnet Launch:** June 2019
- **Current Block:** ~80M+ (as of Feb 2026)
- **Block Time:** 2 seconds
- **Masternodes:** 108 active, 133+ standby
- **Daily Transactions:** ~500K+
- **Networks:** Mainnet (50), Apothem Testnet (51)

## Prerequisites

- **.NET 9 SDK** (see [BUILD-XDC.md](BUILD-XDC.md))
- **Ubuntu 22.04+** or **macOS** (Linux recommended)
- **4+ CPU cores**
- **8+ GB RAM** (16+ GB for archive)
- **500+ GB SSD** for full node (1+ TB for archive)

## Configuration Examples

### Mainnet Full Node

```bash
dotnet nethermind.dll \
  --config xdc \
  --data-dir /data/xdc-mainnet \
  --JsonRpc.Enabled true \
  --JsonRpc.Port 8548
```

### Archive Node

```bash
dotnet nethermind.dll \
  --config xdc \
  --data-dir /data/xdc-archive \
  --Sync.DownloadBodiesInFastSync true \
  --Sync.DownloadReceiptsInFastSync true \
  --Pruning.Mode None \
  --JsonRpc.Port 8548
```

### With Static Peers

```bash
dotnet nethermind.dll \
  --config xdc \
  --Network.StaticPeers "enode://abc@1.2.3.4:30303,enode://def@5.6.7.8:30303"
```

## RPC Endpoints

Standard Ethereum JSON-RPC methods are supported:

```bash
# Get current block
curl -X POST -H "Content-Type: application/json" \
  --data '{"jsonrpc":"2.0","method":"eth_blockNumber","params":[],"id":1}' \
  http://localhost:8548

# Get peer count
curl -X POST -H "Content-Type: application/json" \
  --data '{"jsonrpc":"2.0","method":"net_peerCount","params":[],"id":1}' \
  http://localhost:8548
```

## Monitoring

Nethermind exposes Prometheus metrics by default:

```bash
# Enable metrics
--Metrics.Enabled true
--Metrics.ExposePort 9090

# Grafana dashboard available at:
# https://grafana.com/grafana/dashboards/nethermind
```

## Community & Support

- **XDC Network:** https://xdc.org
- **XDC Forum:** https://xdc.dev
- **Telegram:** https://t.me/xdcdevelopers
- **GitHub Issues:** https://github.com/AnilChinchawale/nethermind/issues

## Comparison: Nethermind vs Geth-XDC

| Feature | Geth-XDC | Nethermind-XDC |
|---------|----------|----------------|
| Language | Go | C# |
| Sync Speed | Fast | Faster (snap sync) |
| Memory Usage | Moderate | Lower |
| RPC Modules | Standard | Enhanced |
| Monitoring | Basic | Prometheus built-in |
| Platform | Linux/macOS | Linux/macOS/Windows |
| Pruning | Basic | Advanced |

## Development

### Building from source

```bash
git clone https://github.com/AnilChinchawale/nethermind.git
cd nethermind
git checkout build/xdc-net9-stable
dotnet build -c Release src/Nethermind/Nethermind.Runner
```

### Running tests

```bash
cd src/Nethermind
dotnet test Nethermind.Xdc.Test
```

### Code structure

- **C# 13** with .NET 9 runtime
- **LGPL-3.0** license (same as upstream)
- **Clean architecture** with dependency injection
- **Extensive test coverage**

## Roadmap

- [x] XDPoS v1 consensus
- [x] XDPoS v2 with finality
- [x] Mainnet sync support
- [x] .NET 9 stable build
- [ ] .NET 10 upgrade (when stable)
- [ ] Enhanced metrics for XDPoS
- [ ] Testnet deployment tools
- [ ] Docker images

## Credits

- **Base Client:** [Nethermind](https://github.com/NethermindEth/nethermind) by Nethermind team
- **XDPoS Implementation:** Anil Chinchawale (@AnilChinchawale)
- **XDC Network:** [XDC Foundation](https://xdc.org)

## License

LGPL-3.0 - Same as upstream Nethermind

See [LICENSE-LGPL](LICENSE-LGPL) for details.

---

**Status:** ✅ Production-ready  
**Last Updated:** February 16, 2026  
**Maintainer:** @AnilChinchawale
