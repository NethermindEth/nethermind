# XDC Network State Transition Logic for Nethermind Implementation

**Document Purpose**: This document analyzes XDPoS block reward and state transition logic from geth-xdc to guide Nethermind XDC port implementation.

**Critical Context**: Nethermind XDC currently syncs headers and processes blocks 0-15 successfully, but block 16 fails with state root mismatch. This indicates we're missing XDPoS-specific state modifications during block processing.

---

## Executive Summary

**Key Finding**: XDPoS does NOT apply block rewards on every block. Rewards are only applied at **checkpoint/epoch switch blocks** (every 900 blocks for V1, epoch boundaries for V2).

This is fundamentally different from Ethereum's model where rewards apply to every block. **This is likely why block 16 works initially** - blocks 1-15 don't trigger any reward state changes, but block **900** (the first checkpoint) will.

---

## Block Reward Constants

### Mainnet Configuration
```go
// From params/config.go - XDCMainnetChainConfig
Reward:              5000,              // Base reward in XDC tokens
RewardCheckpoint:    900,               // Apply rewards every 900 blocks
Epoch:               900,               // Epoch length
Gap:                 450,               // Gap before epoch switch
FoudationWalletAddr: common.HexToAddress("xdc92a289fe95a85c53b8d0d113cbaef0c1ec98ac65"),
```

**Note**: The field is intentionally misspelled as `FoudationWalletAddr` in the XDC codebase.

### Reward Distribution Formula
```go
// From common/constants.go
RewardMasterPercent      = 90   // 90% to masternodes
RewardVoterPercent       = 0    // 0% to voters (disabled)
RewardFoundationPercent  = 10   // 10% to foundation wallet
```

### Actual Reward Amounts (Mainnet)

**Per Checkpoint Block (every 900 blocks)**:
- **Total Reward**: 5000 XDC
- **Masternode Share**: 4500 XDC (90%)
- **Foundation Share**: 500 XDC (10%)

**In Wei**:
- Total: `5000 * 10^18 = 5,000,000,000,000,000,000,000 wei`
- Masternode: `4500 * 10^18 = 4,500,000,000,000,000,000,000 wei`
- Foundation: `500 * 10^18 = 500,000,000,000,000,000,000 wei`

**Distribution**: The 4500 XDC masternode reward is split among all active masternodes (18 on mainnet pre-V2, 108 post-V2).

### Other Network Configurations

**Testnet**:
```go
Reward:              5000,
FoudationWalletAddr: common.HexToAddress("xdc746249c61f5832c5eed53172776b460491bdcd5c"),
```

**Devnet**:
```go
Reward:              10,   // Only 10 XDC for testing
FoudationWalletAddr: common.HexToAddress("0xde5b54e8e7b585153add32f472e8d545e5d42a82"),
```

---

## State Transition Logic

### V1 Engine (Pre-V2 Switch Block)

#### Location: `consensus/XDPoS/engines/engine_v1/engine.go`

```go
// Finalize - Line ~890
func (x *XDPoS_v1) Finalize(chain consensus.ChainReader, header *types.Header, 
    state *state.StateDB, parentState *state.StateDB, txs []*types.Transaction, 
    uncles []*types.Header, receipts []*types.Receipt) (*types.Block, error) {
    
    // Set block reward ONLY at checkpoint blocks
    number := header.Number.Uint64()
    rCheckpoint := chain.Config().XDPoS.RewardCheckpoint  // = 900
    
    // HookReward is ONLY called every 900 blocks
    if x.HookReward != nil && number%rCheckpoint == 0 {
        rewards, err := x.HookReward(chain, state, parentState, header)
        if err != nil {
            return nil, err
        }
        // Rewards are saved to file if configured
    }
    
    // State root is computed AFTER rewards
    header.Root = state.IntermediateRoot(chain.Config().IsEIP158(header.Number))
    header.UncleHash = types.CalcUncleHash(nil)
    
    return types.NewBlock(header, txs, nil, receipts, trie.NewStackTrie(nil)), nil
}
```

**Critical Observations**:
1. **Rewards NOT on every block**: Only when `number % 900 == 0`
2. **State modifications**: Occur through `HookReward` callback
3. **No direct balance changes in Finalize**: All reward logic is external
4. **State root timing**: Computed AFTER HookReward modifies state

### V2 Engine (Post-V2 Switch Block)

#### Location: `consensus/XDPoS/engines/engine_v2/engine.go`

```go
// Finalize - Line ~343
func (x *XDPoS_v2) Finalize(chain consensus.ChainReader, header *types.Header, 
    state *state.StateDB, parentState *state.StateDB, txs []*types.Transaction, 
    uncles []*types.Header, receipts []*types.Receipt) (*types.Block, error) {
    
    // Check if this is an epoch switch block
    isEpochSwitch, _, err := x.IsEpochSwitch(header)
    if err != nil {
        return nil, err
    }
    
    // HookReward is ONLY called at epoch switch blocks
    if x.HookReward != nil && isEpochSwitch {
        rewards, err := x.HookReward(chain, state, parentState, header)
        if err != nil {
            return nil, err
        }
    }
    
    // State root computed AFTER rewards
    header.Root = state.IntermediateRoot(chain.Config().IsEIP158(header.Number))
    header.UncleHash = types.CalcUncleHash(nil)
    
    return types.NewBlock(header, txs, nil, receipts, trie.NewStackTrie(nil)), nil
}
```

**IsEpochSwitch Logic**:
```go
// V1: epoch_switch.go
func (x *XDPoS_v1) IsEpochSwitch(header *types.Header) (bool, uint64, error) {
    epochNumber := header.Number.Uint64() / x.config.Epoch  // Epoch = 900
    blockNumInEpoch := header.Number.Uint64() % x.config.Epoch
    return blockNumInEpoch == 0, epochNumber, nil  // Every 900 blocks
}

// V2: Similar but may have different epoch lengths
```

---

## V1 vs V2 Differences

### Consensus Version Detection

```go
// From consensus/XDPoS/XDPoS.go - Line ~50
func (c *XDPoSConfig) BlockConsensusVersion(num *big.Int, extraByte []byte, 
    extraCheck bool) string {
    if c.V2 != nil && c.V2.SwitchBlock != nil && num.Cmp(c.V2.SwitchBlock) > 0 {
        return ConsensusEngineVersion2
    }
    return ConsensusEngineVersion1
}
```

### State Transition Differences

| Aspect | V1 | V2 |
|--------|----|----|
| **Reward Timing** | Every 900 blocks (fixed) | At epoch switch blocks |
| **Epoch Length** | 900 blocks (constant) | Configurable per round |
| **Max Masternodes** | 18 (early), 40 (later) | 108 (configurable) |
| **Masternode Selection** | Checkpoint block extra data | Epoch switch block validators field |
| **Penalties** | Applied to checkpoint header | Applied to epoch switch header |
| **Extra Data Format** | Vanity + Signers + Seal | Round + QuorumCert + encoded fields |

### V2 Activation Block

**From config.go**:
```go
// Mainnet
V2: &V2{
    SwitchEpoch:   common.MaintnetConstant.TIPV2SwitchBlock.Uint64() / 900,
    SwitchBlock:   common.MaintnetConstant.TIPV2SwitchBlock,
    CurrentConfig: MainnetV2Configs[0],
}

// Note: Actual block number depends on network-specific constants
// Typically in the millions for mainnet
```

The actual switch block numbers are defined in network-specific constant files. For mainnet, this is typically a coordinated hard fork block.

---

## Reward Implementation Hook

### Where Rewards Are Calculated

The `HookReward` callback is **NOT** implemented in the consensus engine files. It's injected from outside:

**From state_processor.go** (core/state_processor.go):
```go
func (p *StateProcessor) Process(block *types.Block, statedb *state.StateDB, ...) {
    // ... transaction processing ...
    
    // Finalize calls engine.Finalize which internally calls HookReward
    p.engine.Finalize(p.bc, header, statedb, parentState, 
        block.Transactions(), block.Uncles(), receipts)
}
```

The hook is likely set up in the XDC-specific blockchain initialization code, probably interfacing with:
1. **Masternode Voting Smart Contract**: `xdc0000000000000000000000000000000000000088`
2. **Block Signers Contract**: `xdc0000000000000000000000000000000000000089`

### Expected Reward Logic (Based on Config)

```go
// Pseudocode for HookReward implementation
func CalculateRewards(state *state.StateDB, header *types.Header) error {
    totalReward := 5000 * 1e18  // 5000 XDC in wei
    
    // 1. Foundation reward
    foundationReward := totalReward * 10 / 100  // 10%
    state.AddBalance(foundationAddr, foundationReward)
    
    // 2. Masternode rewards
    masternodeReward := totalReward * 90 / 100  // 90%
    masternodes := GetMasternodesFromCheckpoint(header)
    rewardPerMN := masternodeReward / len(masternodes)
    
    for _, mn := range masternodes {
        state.AddBalance(mn, rewardPerMN)
    }
    
    return nil
}
```

---

## Penalty Logic

### V1 Penalties (engine_v1/engine.go)

Penalties are applied at checkpoint blocks and stored in the `header.Penalties` field:

```go
// Prepare method - Line ~750
if number >= x.config.Epoch && number%x.config.Epoch == 0 {
    if x.HookPenalty != nil || x.HookPenaltyTIPSigning != nil {
        var penMasternodes []common.Address
        var err error
        
        if chain.Config().IsTIPSigning(header.Number) {
            penMasternodes, err = x.HookPenaltyTIPSigning(chain, header, masternodes)
        } else {
            penMasternodes, err = x.HookPenalty(chain, number)
        }
        
        // Store penalties in header
        header.Penalties = common.ExtractAddressToBytes(penMasternodes)
        
        // Remove penalized nodes from masternode list
        masternodes = common.RemoveItemFromArray(masternodes, penMasternodes)
    }
}
```

**Important**: Penalties affect **future epoch masternode selection**, not state balances directly. The penalty is informational and affects validator eligibility.

### V2 Penalties (engine_v2/engine.go)

Similar mechanism but integrated with epoch switch logic:

```go
// calcMasternodes - Line ~1025
func (x *XDPoS_v2) calcMasternodes(chain consensus.ChainReader, 
    blockNum *big.Int, parentHash common.Hash, round types.Round) ([]common.Address, []common.Address, error) {
    
    penalties, err := x.HookPenalty(chain, blockNum, parentHash, candidates)
    if err != nil {
        return nil, nil, err
    }
    
    masternodes := common.RemoveItemFromArray(candidates, penalties)
    return masternodes, penalties, nil
}
```

**Penalty Storage**: V2 stores penalties in `header.Penalties` byte array at epoch switch blocks.

---

## Critical Differences from Ethereum

### 1. **No Per-Block Rewards**
- **Ethereum**: Rewards on every block
- **XDPoS**: Rewards only every 900 blocks (V1) or at epoch boundaries (V2)

### 2. **State Modifications Only at Checkpoints**
- **Ethereum**: State changes on every block (rewards, uncle rewards, etc.)
- **XDPoS**: State changes only at checkpoints for rewards

### 3. **Foundation Wallet**
- **Ethereum**: No fixed foundation address
- **XDPoS**: 10% of all rewards go to hardcoded foundation wallet

### 4. **No Uncle Rewards**
- **Ethereum**: Uncle blocks receive reduced rewards
- **XDPoS**: No uncles permitted, `UncleHash` always equals `CalcUncleHash(nil)`

---

## Nethermind Implementation Guide

### Files to Modify

1. **Nethermind.Consensus.XDPoS/XDPoSBlockProducer.cs** (if exists)
   - Implement checkpoint detection logic
   - Call reward distribution at checkpoints

2. **Nethermind.Consensus.XDPoS/Finalization/XDPoSRewardCalculator.cs** (create)
   - Implement reward calculation logic
   - Foundation wallet: `xdc92a289fe95a85c53b8d0d113cbaef0c1ec98ac65` (mainnet)
   - Distribution: 90% masternodes, 10% foundation

3. **Nethermind.Consensus.XDPoS/XDPoSBlockValidator.cs**
   - Verify state root AFTER applying checkpoint rewards
   - Validate penalty headers at checkpoints

4. **Nethermind.State/StateProvider.cs**
   - Ensure balance updates are applied correctly
   - State root must match after reward distribution

### Implementation Pseudocode

```csharp
// In XDPoSBlockProcessor.cs or similar
public class XDPoSBlockProcessor : BlockProcessor
{
    private readonly Address _foundationWallet = 
        new Address("0x92a289fe95a85c53b8d0d113cbaef0c1ec98ac65");
    private const long CHECKPOINT_INTERVAL = 900;
    private const long REWARD_AMOUNT = 5000; // XDC
    
    protected override void ApplyBlockRewards(Block block, IReleaseSpec spec)
    {
        // XDPoS only applies rewards at checkpoint blocks
        if (block.Number % CHECKPOINT_INTERVAL != 0)
        {
            // No rewards for non-checkpoint blocks
            return;
        }
        
        // Calculate rewards
        UInt256 totalReward = REWARD_AMOUNT * Unit.Ether;
        UInt256 foundationReward = totalReward * 10 / 100; // 10%
        UInt256 masternodeReward = totalReward * 90 / 100; // 90%
        
        // 1. Foundation reward
        _stateProvider.AddToBalance(_foundationWallet, foundationReward, spec);
        
        // 2. Masternode rewards
        Address[] masternodes = GetMasternodesFromCheckpoint(block.Header);
        UInt256 rewardPerMN = masternodeReward / (ulong)masternodes.Length;
        
        foreach (var masternode in masternodes)
        {
            _stateProvider.AddToBalance(masternode, rewardPerMN, spec);
        }
        
        // Important: State root must be recalculated after these changes
    }
    
    private Address[] GetMasternodesFromCheckpoint(BlockHeader header)
    {
        // Extract masternodes from checkpoint block extra data
        // V1: Bytes 32 to (32 + N*20), where N is number of masternodes
        // V2: From header.Validators field
        
        if (IsV2Block(header))
        {
            return ExtractAddressesFromBytes(header.Validators);
        }
        else
        {
            // V1: Extract from Extra field
            int extraVanity = 32;
            int extraSeal = 65;
            byte[] signersData = header.ExtraData[extraVanity..^extraSeal];
            return ExtractAddressesFromBytes(signersData);
        }
    }
    
    private bool IsCheckpointBlock(long blockNumber)
    {
        return blockNumber % CHECKPOINT_INTERVAL == 0;
    }
}
```

### State Root Calculation Order

**Critical**: The state root must be calculated AFTER applying rewards:

```csharp
// Correct order:
1. Process all transactions
2. If checkpoint block:
   a. Apply foundation reward
   b. Apply masternode rewards
   c. Update state
3. Calculate intermediate state root
4. Set header.StateRoot
```

### Testing Strategy

1. **Unit Tests**:
   ```csharp
   [Test]
   public void Should_Not_Apply_Rewards_On_Block_15()
   {
       // Block 15 is NOT a checkpoint
       var block = CreateBlock(15);
       ProcessBlock(block);
       // Verify NO balance changes for foundation or masternodes
   }
   
   [Test]
   public void Should_Apply_Rewards_On_Block_900()
   {
       // Block 900 IS a checkpoint
       var block = CreateBlock(900);
       ProcessBlock(block);
       // Verify foundation received 500 XDC
       // Verify masternodes received 4500 XDC total
   }
   ```

2. **Integration Tests**:
   - Sync blocks 0-899 (no rewards expected)
   - Process block 900 (first checkpoint - verify state root matches)
   - Process block 1800 (second checkpoint)

3. **State Root Validation**:
   - Compare state roots with geth-xdc at blocks: 900, 1800, 2700, etc.
   - Verify intermediate state roots match during sync

---

## Common Pitfalls

### ❌ Mistake 1: Applying Rewards on Every Block
```csharp
// WRONG - Don't do this!
protected override void ApplyBlockRewards(Block block, IReleaseSpec spec)
{
    // This will cause state root mismatch on every block!
    ApplyRewardsToFoundation(block);
    ApplyRewardsToMasternodes(block);
}
```

### ✅ Correct: Only at Checkpoints
```csharp
// CORRECT
protected override void ApplyBlockRewards(Block block, IReleaseSpec spec)
{
    if (block.Number % 900 != 0)
        return; // No rewards for non-checkpoint blocks
        
    ApplyRewardsToFoundation(block);
    ApplyRewardsToMasternodes(block);
}
```

### ❌ Mistake 2: Wrong State Root Timing
```csharp
// WRONG - State root calculated too early!
header.StateRoot = CalculateStateRoot();
ApplyBlockRewards(block); // Too late!
```

### ✅ Correct: State Root After Rewards
```csharp
// CORRECT
ProcessTransactions(block);
if (IsCheckpoint(block.Number))
    ApplyBlockRewards(block);
header.StateRoot = CalculateStateRoot(); // After everything
```

### ❌ Mistake 3: Wrong Foundation Address Format
```csharp
// WRONG - Missing 'xdc' to '0x' conversion
Address foundation = new Address("xdc92a289fe95a85c53...");
```

### ✅ Correct: Convert XDC to 0x Format
```csharp
// CORRECT - XDC addresses are Ethereum addresses with different prefix
// xdc → 0x conversion
Address foundation = new Address("0x92a289fe95a85c53b8d0d113cbaef0c1ec98ac65");
```

---

## Verification Checklist

- [ ] Rewards only applied at block numbers divisible by 900
- [ ] Foundation wallet receives exactly 10% (500 XDC per checkpoint)
- [ ] Masternodes receive exactly 90% (4500 XDC split among them)
- [ ] State root calculated AFTER applying rewards
- [ ] No uncle rewards implemented
- [ ] Penalties extracted and applied correctly
- [ ] V1/V2 detection works correctly
- [ ] Extra data parsing handles both V1 and V2 formats
- [ ] Address format conversion (xdc ↔ 0x) handled correctly

---

## Key Code Snippets from geth-xdc

### 1. Finalize V1 (Full Method)
```go
// consensus/XDPoS/engines/engine_v1/engine.go:880-910
func (x *XDPoS_v1) Finalize(chain consensus.ChainReader, header *types.Header, 
    state *state.StateDB, parentState *state.StateDB, txs []*types.Transaction, 
    uncles []*types.Header, receipts []*types.Receipt) (*types.Block, error) {
    
    // set block reward
    number := header.Number.Uint64()
    rCheckpoint := chain.Config().XDPoS.RewardCheckpoint

    if x.HookReward != nil && number%rCheckpoint == 0 {
        rewards, err := x.HookReward(chain, state, parentState, header)
        if err != nil {
            return nil, err
        }
        if len(common.StoreRewardFolder) > 0 {
            data, err := json.Marshal(rewards)
            // ... save rewards to file ...
        }
    }

    // the state remains as is and uncles are dropped
    header.Root = state.IntermediateRoot(chain.Config().IsEIP158(header.Number))
    header.UncleHash = types.CalcUncleHash(nil)

    // Assemble and return the final block for sealing
    return types.NewBlock(header, txs, nil, receipts, trie.NewStackTrie(nil)), nil
}
```

### 2. Finalize V2 (Full Method)
```go
// consensus/XDPoS/engines/engine_v2/engine.go:343-380
func (x *XDPoS_v2) Finalize(chain consensus.ChainReader, header *types.Header, 
    state *state.StateDB, parentState *state.StateDB, txs []*types.Transaction, 
    uncles []*types.Header, receipts []*types.Receipt) (*types.Block, error) {
    
    isEpochSwitch, _, err := x.IsEpochSwitch(header)
    if err != nil {
        log.Error("[Finalize] IsEpochSwitch bug!", "err", err)
        return nil, err
    }
    
    if x.HookReward != nil && isEpochSwitch {
        rewards, err := x.HookReward(chain, state, parentState, header)
        if err != nil {
            return nil, err
        }
        if len(common.StoreRewardFolder) > 0 {
            data, err := json.Marshal(rewards)
            // ... save rewards to file ...
        }
    }

    // the state remains as is and uncles are dropped
    header.Root = state.IntermediateRoot(chain.Config().IsEIP158(header.Number))
    header.UncleHash = types.CalcUncleHash(nil)

    // Assemble and return the final block for sealing
    return types.NewBlock(header, txs, nil, receipts, trie.NewStackTrie(nil)), nil
}
```

### 3. Masternode Extraction (V1)
```go
// consensus/XDPoS/engines/engine_v1/engine.go:GetMasternodesFromCheckpointHeader
func (x *XDPoS_v1) GetMasternodesFromCheckpointHeader(checkpointHeader *types.Header) []common.Address {
    if checkpointHeader == nil {
        log.Warn("Checkpoint's header is empty", "Header", checkpointHeader)
        return []common.Address{}
    }
    return decodeMasternodesFromHeaderExtra(checkpointHeader)
}

// Helper function
func decodeMasternodesFromHeaderExtra(header *types.Header) []common.Address {
    extraSuffix := len(header.Extra) - utils.ExtraSeal  // 65 bytes seal
    // Masternodes are between vanity (32 bytes) and seal (65 bytes)
    masternodesBytes := header.Extra[utils.ExtraVanity:extraSuffix]
    
    masternodes := make([]common.Address, len(masternodesBytes)/common.AddressLength)
    for i := 0; i < len(masternodes); i++ {
        copy(masternodes[i][:], masternodesBytes[i*common.AddressLength:])
    }
    return masternodes
}
```

### 4. Masternode Extraction (V2)
```go
// consensus/XDPoS/engines/engine_v2/engine.go:GetMasternodesFromEpochSwitchHeader
func (x *XDPoS_v2) GetMasternodesFromEpochSwitchHeader(epochSwitchHeader *types.Header) []common.Address {
    if epochSwitchHeader == nil {
        log.Error("[GetMasternodesFromEpochSwitchHeader] use nil epoch switch block")
        return []common.Address{}
    }
    
    // V2 stores masternodes in Validators field
    masternodes := make([]common.Address, len(epochSwitchHeader.Validators)/common.AddressLength)
    for i := 0; i < len(masternodes); i++ {
        copy(masternodes[i][:], epochSwitchHeader.Validators[i*common.AddressLength:])
    }
    
    return masternodes
}
```

---

## Summary

**Why Block 16 Fails**:
- Block 16 is NOT a checkpoint block (16 % 900 ≠ 0)
- Block 16 should NOT have any reward-related state changes
- The failure is likely elsewhere (transaction processing, gas handling, etc.)

**Why Block 900 Will Be Critical**:
- Block 900 IS a checkpoint block (900 % 900 == 0)
- Must apply foundation reward: 500 XDC
- Must apply masternode rewards: 4500 XDC split among 18 nodes
- State root must be calculated AFTER these balance changes

**Implementation Priority**:
1. ✅ Implement checkpoint detection (block % 900 == 0)
2. ✅ Implement foundation reward (10% to fixed address)
3. ✅ Implement masternode reward distribution (90% split)
4. ✅ Ensure state root calculation happens AFTER rewards
5. ✅ Extract masternodes from checkpoint block extra data
6. ⚠️ Test specifically at blocks: 900, 1800, 2700, etc.

---

## References

- **geth-xdc Repository**: https://github.com/XinFinOrg/XDPoSChain
- **Key Files Analyzed**:
  - `consensus/XDPoS/XDPoS.go` - Main adapter
  - `consensus/XDPoS/engines/engine_v1/engine.go` - V1 consensus
  - `consensus/XDPoS/engines/engine_v2/engine.go` - V2 consensus  
  - `params/config.go` - Network configurations
  - `common/constants.go` - Reward percentages
  - `core/state_processor.go` - Block processing flow

**Document Version**: 1.0
**Last Updated**: 2026-02-16
**Author**: OpenClaw Research Agent
