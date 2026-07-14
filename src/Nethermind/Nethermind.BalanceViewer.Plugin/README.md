# Nethermind.BalanceViewer.Plugin

A self-contained balance-viewer UI served at the `/balances` path of the node's (unauthenticated)
JSON-RPC HTTP endpoint. It shows native, ERC-20, and NFT (ERC-721 / ERC-1155) holdings for pinned
addresses across every reachable Nethermind node on the machine, with fiat valuation via Chainlink
feeds and automatic token/NFT detection.

## Configuration

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| `BalanceViewer.Enabled` | bool | true | Serve the UI and detection endpoints |

The page is only served on unauthenticated JSON-RPC ports (never the Engine API port).

## Token & NFT detection

The plugin discovers the tokens and collections an address holds by scanning the node's own history
in-process for `Transfer` / `TransferSingle` / `TransferBatch` logs involving the address, via
`ILogFinder`. Results are cached per address (see `DetectionCache`) and re-scanned forward as the chain
advances. Detection depth is bounded by the node's retained receipts (`Sync.AncientReceiptsBarrier` /
`History.Pruning`).

### Recommended: enable the log index

For usable detection performance this plugin is **recommended to be run with the node's log index
enabled**:

```
--LogIndex.Enabled true
```

With the log index, historical scans jump directly to the blocks containing an address's logs. Without
it, `ILogFinder` falls back to a linear per-block bloom scan, so a deep scan of a sparse address (one
with little token activity over a long retained window) can take many minutes. The plugin works either
way — the log index only affects scan speed — so it is intentionally **not** forced on from the plugin;
enable it in the node configuration. Building the index is a one-time background task proportional to
the retained-receipt range.

## Building the plugin

```bash
# From the repository root
cd src/Nethermind
dotnet build Nethermind.BalanceViewer.Plugin/Nethermind.BalanceViewer.Plugin.csproj -c Release
```

## Additional resources

- [Nethermind Plugin Documentation](https://docs.nethermind.io/developers/plugins/)
