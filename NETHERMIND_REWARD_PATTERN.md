# Nethermind Block Reward Implementation Pattern

## Research Summary
This document describes how to implement block rewards in Nethermind for XDPoS consensus, based on patterns from AuRa, Ethash, and Clique implementations.

---

## 1. Core Interface: `IRewardCalculator`

**Location**: `src/Nethermind/Nethermind.Consensus/Rewards/IRewardCalculator.cs`

```csharp
public interface IRewardCalculator
{
    BlockReward[] CalculateRewards(Block block);
}
```

**Simple and elegant**: Takes a `Block`, returns an array of `BlockReward` structs.

---

## 2. The `BlockReward` Struct

**Location**: `src/Nethermind/Nethermind.Consensus/Rewards/BlockReward.cs`

```csharp
public class BlockReward
{
    public BlockReward(Address address, in UInt256 value, BlockRewardType rewardType = BlockRewardType.Block)
    {
        Address = address;
        Value = value;
        RewardType = rewardType;
    }

    public Address Address { get; }
    public UInt256 Value { get; }
    public BlockRewardType RewardType { get; }
}
```

**BlockRewardType enum** (for tracing/logging):
- `Block` (0) - Standard block reward
- `Uncle` (1) - Uncle block reward (Ethash)
- `EmptyStep` (2) - AuRa empty step reward
- `External` (3) - From smart contract

---

## 3. How Rewards Are Applied to State

**Location**: `src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.cs:223`

```csharp
private void ApplyMinerRewards(Block block, IBlockTracer tracer, IReleaseSpec spec)
{
    if (_logger.IsTrace) _logger.Trace("Applying miner rewards:");
    BlockReward[] rewards = rewardCalculator.CalculateRewards(block);
    
    for (int i = 0; i < rewards.Length; i++)
    {
        BlockReward reward = rewards[i];

        using ITxTracer txTracer = tracer.IsTracingRewards
            ? tracer.StartNewTxTrace(null)
            : NullTxTracer.Instance;

        ApplyMinerReward(block, reward, spec);

        if (tracer.IsTracingRewards)
        {
            tracer.EndTxTrace();
            tracer.ReportReward(reward.Address, reward.RewardType.ToLowerString(), reward.Value);
            if (txTracer.IsTracingState)
            {
                _stateProvider.Commit(spec, txTracer);
            }
        }
    }
}

private void ApplyMinerReward(Block block, BlockReward reward, IReleaseSpec spec)
{
    if (_logger.IsTrace) 
        _logger.Trace($"  {(BigInteger)reward.Value / (BigInteger)Unit.Ether:N3}{Unit.EthSymbol} for account at {reward.Address}");

    _stateProvider.AddToBalanceAndCreateIfNotExists(reward.Address, reward.Value, spec);
}
```

**Key takeaway**: You return multiple `BlockReward` entries, and each one is added to the state balance. Super flexible!

---

## 4. Implementation Examples

### A. NoBlockRewards (Clique / Merge)

**Location**: `src/Nethermind/Nethermind.Consensus/Rewards/NoBlockRewards.cs`

```csharp
public class NoBlockRewards : IRewardCalculator, IRewardCalculatorSource
{
    private NoBlockRewards() { }

    public static NoBlockRewards Instance { get; } = new();

    private static readonly BlockReward[] _noRewards = [];

    public BlockReward[] CalculateRewards(Block block) => _noRewards;

    public IRewardCalculator Get(ITransactionProcessor processor) => Instance;
}
```

**Pattern**: Singleton, returns empty array. Use for chains with no block rewards (PoA, PoS).

---

### B. StaticRewardCalculator (Simple Fixed Rewards)

**Location**: `src/Nethermind/Nethermind.Consensus.AuRa/Rewards/StaticRewardCalculator.cs`

```csharp
public class StaticRewardCalculator : IRewardCalculator
{
    private readonly IList<BlockRewardInfo> _blockRewards;

    public StaticRewardCalculator(IDictionary<long, UInt256>? blockRewards)
    {
        _blockRewards = CreateBlockRewards(blockRewards);
    }

    public BlockReward[] CalculateRewards(Block block)
    {
        _blockRewards.TryGetForActivation(block.Number, out var blockReward);
        return new[] { new BlockReward(block.Beneficiary, blockReward.Reward) };
    }

    private static IList<BlockRewardInfo> CreateBlockRewards(IDictionary<long, UInt256>? blockRewards)
    {
        List<BlockRewardInfo> blockRewardInfos = new();
        if (blockRewards?.Count > 0)
        {
            if (blockRewards.First().Key > 0)
            {
                blockRewardInfos.Add(new BlockRewardInfo(0, 0));
            }
            foreach (var threshold in blockRewards)
            {
                blockRewardInfos.Add(new BlockRewardInfo(threshold.Key, threshold.Value));
            }
        }
        else
        {
            blockRewardInfos.Add(new BlockRewardInfo(0, 0));
        }
        return blockRewardInfos;
    }

    private class BlockRewardInfo : IActivatedAt
    {
        public long BlockNumber { get; }
        public UInt256 Reward { get; }

        public BlockRewardInfo(long blockNumber, in UInt256 reward)
        {
            BlockNumber = blockNumber;
            Reward = reward;
        }
        long IActivatedAt<long>.Activation => BlockNumber;
    }
}
```

**Pattern**: Supports reward transitions at different block numbers. Single beneficiary.

---

### C. AuRaRewardCalculator (Contract-Based or Fallback)

**Location**: `src/Nethermind/Nethermind.Consensus.AuRa/Rewards/AuRaRewardCalculator.cs`

```csharp
public class AuRaRewardCalculator : IRewardCalculator
{
    private readonly StaticRewardCalculator _blockRewardCalculator;
    private readonly IList<IRewardContract> _contracts;

    public AuRaRewardCalculator(
        AuRaChainSpecEngineParameters auRaParameters, 
        IAbiEncoder abiEncoder, 
        ITransactionProcessor transactionProcessor)
    {
        // ... contract setup ...
        _blockRewardCalculator = new StaticRewardCalculator(auRaParameters.BlockReward);
    }

    public BlockReward[] CalculateRewards(Block block)
    {
        if (block.IsGenesis)
            return [];

        return _contracts.TryGetForBlock(block.Number, out var contract)
            ? CalculateRewardsWithContract(block, contract)
            : _blockRewardCalculator.CalculateRewards(block);
    }

    private static BlockReward[] CalculateRewardsWithContract(Block block, IRewardContract contract)
    {
        // Call smart contract to get beneficiaries and amounts
        var (beneficiaries, kinds) = GetBeneficiaries();
        var (addresses, rewards) = contract.Reward(block.Header, beneficiaries, kinds);

        var blockRewards = new BlockReward[addresses.Length];
        for (int index = 0; index < addresses.Length; index++)
        {
            blockRewards[index] = new BlockReward(addresses[index], rewards[index], BlockRewardType.External);
        }

        return blockRewards;
    }
}
```

**Pattern**: Can use smart contracts for dynamic rewards OR fallback to static rewards. XDC doesn't need this complexity.

---

## 5. The `IRewardCalculatorSource` Pattern

**Location**: `src/Nethermind/Nethermind.Consensus/Rewards/IRewardCalculatorSource.cs`

```csharp
public interface IRewardCalculatorSource
{
    IRewardCalculator Get(ITransactionProcessor processor);
}
```

**Why it exists**: Some reward calculators need access to a transaction processor (e.g., AuRa calling smart contracts). The `Get()` method is called during block processor creation with the appropriate scoped `ITransactionProcessor`.

**For simple rewards** (like XDC): Implement both `IRewardCalculator` and `IRewardCalculatorSource`, and just return `this` from `Get()`.

---

## 6. DI Registration Pattern

### AuRa Example

**Location**: `src/Nethermind/Nethermind.Consensus.AuRa/AuRaPlugin.cs:129`

```csharp
.AddSingleton<IRewardCalculatorSource, AuRaRewardCalculator.AuRaRewardCalculatorSource>()
```

**AuRa uses a nested class as the source**:

```csharp
public class AuRaRewardCalculatorSource : IRewardCalculatorSource
{
    private readonly AuRaChainSpecEngineParameters _auRaParameters;
    private readonly IAbiEncoder _abiEncoder;

    public AuRaRewardCalculatorSource(
        AuRaChainSpecEngineParameters auRaParameters, 
        IAbiEncoder abiEncoder)
    {
        _auRaParameters = auRaParameters;
        _abiEncoder = abiEncoder;
    }

    public IRewardCalculator Get(ITransactionProcessor processor) 
        => new AuRaRewardCalculator(_auRaParameters, _abiEncoder, processor);
}
```

**Why separate source?** Because `AuRaRewardCalculator` needs `ITransactionProcessor` (scoped), but the source is singleton. The source creates the calculator per scope.

---

## 7. XDC-Specific Requirements

From `src/Nethermind/Nethermind.Xdc/Spec/XdcChainSpecEngineParameters.cs`:

```csharp
public Address FoundationWalletAddr { get; set; }  // Foundation/treasury address
public int Reward { get; set; }                     // Block reward in XDC
```

**XDC reward split** (based on XDC Network docs):
- 50% to block proposer (validator)
- 50% to foundation wallet

---

## 8. Recommended Implementation for XDC

### File: `src/Nethermind/Nethermind.Xdc/XdcRewardCalculator.cs`

```csharp
// SPDX-FileCopyrightText: 2026 Anil Chinchawale
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Consensus.Rewards;
using Nethermind.Core;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Xdc.Spec;

namespace Nethermind.Xdc;

/// <summary>
/// XDPoS block reward calculator
/// Splits rewards 50/50 between validator and foundation wallet
/// </summary>
public class XdcRewardCalculator : IRewardCalculator, IRewardCalculatorSource
{
    private readonly XdcChainSpecEngineParameters _parameters;
    private readonly UInt256 _blockReward;
    private readonly UInt256 _validatorShare;
    private readonly UInt256 _foundationShare;

    public XdcRewardCalculator(XdcChainSpecEngineParameters parameters)
    {
        _parameters = parameters ?? throw new ArgumentNullException(nameof(parameters));
        
        // Convert reward from XDC to Wei (1 XDC = 10^18 Wei)
        _blockReward = (UInt256)parameters.Reward * Unit.Ether;
        
        // 50/50 split
        _validatorShare = _blockReward / 2;
        _foundationShare = _blockReward / 2;
    }

    public BlockReward[] CalculateRewards(Block block)
    {
        if (block.IsGenesis)
            return Array.Empty<BlockReward>();

        // If no foundation wallet configured, give everything to validator
        if (_parameters.FoundationWalletAddr is null || _parameters.FoundationWalletAddr == Address.Zero)
        {
            return new[] 
            { 
                new BlockReward(block.Beneficiary, _blockReward, BlockRewardType.Block) 
            };
        }

        // Normal case: split between validator and foundation
        return new[]
        {
            new BlockReward(block.Beneficiary, _validatorShare, BlockRewardType.Block),
            new BlockReward(_parameters.FoundationWalletAddr, _foundationShare, BlockRewardType.External)
        };
    }

    public IRewardCalculator Get(ITransactionProcessor processor) => this;
}
```

---

## 9. DI Registration in XdcModule

### File: `src/Nethermind/Nethermind.Xdc/XdcModule.cs`

Add this registration in the `Load()` method:

```csharp
protected override void Load(ContainerBuilder builder)
{
    base.Load(builder);

    // Get chain spec parameters
    var chainSpec = /* ... get from context ... */;
    XdcChainSpecEngineParameters specParam = chainSpec.EngineChainSpecParametersProvider
        .GetChainSpecParameters<XdcChainSpecEngineParameters>();

    // Register reward calculator as singleton (stateless, can be reused)
    builder.RegisterInstance(new XdcRewardCalculator(specParam))
        .As<IRewardCalculatorSource>()
        .SingleInstance();

    // ... rest of registrations ...
}
```

**Lifetime**: Singleton. The calculator is stateless (just configuration), so one instance for the entire app.

---

## 10. Important Gotchas and Best Practices

### ✅ DO:

1. **Return empty array for genesis blocks**
   ```csharp
   if (block.IsGenesis)
       return Array.Empty<BlockReward>();
   ```

2. **Use `UInt256` for all reward values** (never `int` or `decimal`)
   ```csharp
   UInt256 reward = (UInt256)rewardInXdc * Unit.Ether;
   ```

3. **Handle null/zero foundation address gracefully**
   - For testnets or dev chains, foundation wallet might not be set
   - Give all rewards to validator in that case

4. **Use appropriate `BlockRewardType`**:
   - `BlockRewardType.Block` for validator reward
   - `BlockRewardType.External` for foundation/treasury reward (clearer in traces)

5. **Implement both `IRewardCalculator` and `IRewardCalculatorSource`**
   - Simple pattern: return `this` from `Get()`
   - Unless you need transaction processor (XDC doesn't)

### ❌ DON'T:

1. **Don't mutate state in `CalculateRewards()`**
   - Only calculate and return rewards
   - `BlockProcessor` handles the actual state changes

2. **Don't access `IWorldState` directly**
   - No need to check balances or modify state
   - Just return the rewards array

3. **Don't forget to multiply by `Unit.Ether`**
   - Chain spec reward is typically in XDC (human-readable)
   - State balances are in Wei (10^18 Wei = 1 XDC)

4. **Don't make calculator scoped unless necessary**
   - Singleton lifetime is fine for stateless calculators
   - Only use scoped if you need per-request state

### 🔍 Testing Notes:

1. **Check block beneficiary is correct**
   - Should be the validator that produced the block
   - Not necessarily `block.Header.Beneficiary` in all consensus engines
   - For XDC: `block.Beneficiary` should be correct

2. **Verify foundation wallet receives rewards**
   - Use block tracer to see rewards applied
   - Check state after block processing

3. **Test with zero reward configuration**
   - Devnets might have `Reward = 0`
   - Should return empty rewards or zero-value rewards

4. **Test reward transitions**
   - If reward amount changes at specific block numbers
   - Use `IActivatedAt` pattern like `StaticRewardCalculator`

---

## 11. How XdcBlockProcessor Uses the Calculator

**Current code**: `src/Nethermind/Nethermind.Xdc/XdcBlockProcessor.cs`

```csharp
internal class XdcBlockProcessor : BlockProcessor
{
    public XdcBlockProcessor(
        ISpecProvider specProvider, 
        IBlockValidator blockValidator, 
        IRewardCalculator rewardCalculator,  // <-- Injected here
        // ... other params ...
    ) : base(specProvider, blockValidator, rewardCalculator, /* ... */)
    {
    }

    // Inherits ApplyMinerRewards() from BlockProcessor
    // No need to override unless custom logic needed
}
```

**Good news**: XdcBlockProcessor already accepts `IRewardCalculator` and passes it to base class. You just need to register your implementation!

---

## 12. Summary Checklist

- [x] Understand `IRewardCalculator` interface (simple!)
- [x] Know how `BlockProcessor.ApplyMinerRewards()` works
- [x] Study `StaticRewardCalculator` (closest to XDC needs)
- [x] Understand DI registration pattern (singleton source)
- [x] Know how to handle foundation wallet rewards (return multiple `BlockReward` entries)
- [x] Implement `XdcRewardCalculator` with 50/50 split
- [x] Register in `XdcModule.Load()`
- [x] Handle edge cases (genesis, null foundation, zero rewards)

---

## 13. Next Steps for Implementation

1. **Create** `src/Nethermind/Nethermind.Xdc/XdcRewardCalculator.cs` with the code above
2. **Modify** `src/Nethermind/Nethermind.Xdc/XdcModule.cs`:
   - Add XdcChainSpecEngineParameters resolution
   - Register `XdcRewardCalculator` as `IRewardCalculatorSource`
3. **Test** with local devnet:
   - Verify validator balance increases
   - Verify foundation wallet balance increases
   - Check rewards are 50/50 split
4. **Optional**: Add logging in `XdcRewardCalculator.CalculateRewards()` for debugging

---

## 14. References

- Interface: `Nethermind.Consensus/Rewards/IRewardCalculator.cs`
- Application: `Nethermind.Consensus/Processing/BlockProcessor.cs:223`
- Simple example: `Nethermind.Consensus.AuRa/Rewards/StaticRewardCalculator.cs`
- Complex example: `Nethermind.Consensus.AuRa/Rewards/AuRaRewardCalculator.cs`
- DI pattern: `Nethermind.Consensus.AuRa/AuRaPlugin.cs:129`
- Current XDC processor: `Nethermind.Xdc/XdcBlockProcessor.cs`
- Chain spec params: `Nethermind.Xdc/Spec/XdcChainSpecEngineParameters.cs`

---

**End of Research Document**
