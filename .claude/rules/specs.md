---
paths:
  - "src/Nethermind/Nethermind.Specs/**/*.cs"
---

# Nethermind.Specs

Network specifications, hard fork rules, and chain configuration.

Key classes:
- `ReleaseSpec` — mutable struct holding all EIP/fork feature flags for a single release
- `ISpecProvider` — returns the `IReleaseSpec` applicable at a given block/timestamp
- `MainnetSpecProvider`, `SepoliaSpecProvider`, `GnosisSpecProvider` — network-specific providers

## Adding a new EIP or hard fork flag

1. Add a `bool` property to `IReleaseSpec` (interface in `Nethermind.Core`).
2. Add the implementation in `ReleaseSpec` with the appropriate default (`false` for new features).
3. Enable the flag in the right network provider at the correct fork block/timestamp.
4. Add to `TestSpecProvider` or `SingleReleaseSpecProvider` only if tests need explicit control.

```csharp
// In IReleaseSpec (Nethermind.Core)
bool IsEip1234Enabled { get; }

// In ReleaseSpec
public bool IsEip1234Enabled { get; set; }

// In MainnetSpecProvider (enabled at Cancun)
cancun.IsEip1234Enabled = true;
```

## Primary API — GetSpec(BlockHeader)

Use `ISpecProvider.GetSpec(BlockHeader header)` in block processing and transaction validation. This overload is correct when both block number and timestamp are available:

```csharp
// Correct — uses both block number and timestamp
IReleaseSpec spec = specProvider.GetSpec(blockHeader);

// Avoid — block-number-only overload misses timestamp-based forks (e.g. Shanghai)
IReleaseSpec spec = specProvider.GetSpec(blockNumber);
```

Never hard-code fork checks in transaction or block processing code — always read from `IReleaseSpec`.

## Test spec providers

| Provider | When to use |
|----------|------------|
| `SingleReleaseSpecProvider` | Single-fork test; all blocks use one `ReleaseSpec` |
| `TestSpecProvider` | Fine-grained control; set spec per block number |
| `MainnetSpecProvider.Instance` | Full mainnet fork history; needed for fork-transition tests |

```csharp
// Most unit tests — use a single spec
ISpecProvider spec = new SingleReleaseSpecProvider(London.Instance, 1 /* chainId */);

// For fork-transition tests
ISpecProvider spec = new TestSpecProvider(Cancun.Instance)
{
    SpecToReturn = myCustomSpec
};
```

Don't create a full `MainnetSpecProvider` for a single-fork test — it adds ~50 hard fork transitions that add no value and obscure intent.

## ChainSpec-based providers

Network configuration is loaded from `chainspec/*.json` files (in `Nethermind.Specs/ChainSpecStyle/`). The `ChainSpecBasedSpecProvider` reads fork block numbers from the chain spec. When adding a new network:
1. Add a `chainspec/<network>.json` with fork block numbers.
2. Create a `<Network>SpecProvider.cs` that wraps `ChainSpecBasedSpecProvider`.
3. Do not hard-code fork block numbers in C# — they belong in the chain spec JSON.
