# Nethermind XDC State Root Mismatch Investigation

## Executive Summary

**Problem:** Nethermind computes state root `0xdfd5b0cc...` at block 1800, but geth expects `0xd3a3ec14...`. State root at block 1799 MATCHES geth. Block 1800 has 0 transactions, only 2 reward account changes (both verified correct).

**Critical Finding:** During genesis TRIE-WRITE logging, address `0x0000...0001` (the EcRecover precompile) was written **TWICE**:
1. First with balance=`1000000000000000000000` (1000 XDC)
2. Then with balance=`0`

## Key Findings

### 1. Address 0x01 in xdc.json Chainspec

```json
"0x0000000000000000000000000000000000000001": {
  "balance": "0x0"
}
```

- Address 0x01 is the **EcRecover precompile** (defined in `PrecompiledAddresses.cs` as `Address.FromNumber(0x01)`)
- It has **zero balance** explicitly set in the chainspec
- Same as geth's mainnet alloc data: `"0000000000000000000000000000000000000001":{"balance":"0x0"}`

### 2. ChainSpecLoader Logic (lines 395-440)

```csharp
// ChainSpecLoader.cs LoadAllocations method
foreach (KeyValuePair<string, AllocationJson> account in chainSpecJson.Accounts)
{
    if (account.Value.BuiltIn is not null && account.Value.Balance is null)
    {
        continue;  // Skip builtins without explicit balance
    }
    
    // Create allocation with explicit balance (or zero if not specified)
    chainSpec.Allocations[address] = new ChainSpecAllocation(
        account.Value.Balance ?? UInt256.Zero,
        ...
    );
}
```

The loader does NOT skip 0x01 because:
- It has NO `BuiltIn` field set (xdc.json doesn't define builtins)
- It has an explicit `balance: "0x0"`

### 3. Genesis Building Process

Both `GenesisBuilder.cs` and `XdcGenesisBuilder.cs` call:
```csharp
stateProvider.CreateAccount(address, allocation.Balance, allocation.Nonce);
```

This creates the account with balance 0 as specified.

### 4. EIP-158 Handling in XDC

From `XdcBlockProcessor.cs` (line 134):
```csharp
// XDC: EIP-158 RE-ENABLED (eip158Block=3 in geth-xdc)
// Geth calls IntermediateRoot(deleteEmptyObjects=true) which deletes empty touched accounts.
```

EIP-158 is enabled at block 3 in XDC, which means empty accounts are deleted when touched.

From `StateProvider.cs` (lines 606-624):
```csharp
// In CommitTree/Commit logic:
if (releaseSpec.IsEip158Enabled && change.Account.IsEmpty && !isGenesis)
{
    SetState(change.Address, null);  // Delete empty account
}
```

**Key:** `!isGenesis` check means empty accounts are NOT deleted during genesis, even with EIP-158 enabled.

### 5. The Mystery: Why is 0x01 Written Twice?

The user observed during genesis logging:
1. First write: balance = 1000000000000000000000 (1000 XDC)
2. Second write: balance = 0

**This suggests something in the genesis processing is creating the precompile with a non-zero balance first, then overwriting it with 0 from the chainspec.**

Possible sources of the first write:
- Precompile initialization code somewhere?
- XDC-specific genesis post-processor?
- Snapshot manager storing genesis snapshot?

### 6. Get vs Nethermind Comparison

| Aspect | Get | Nethermind |
|--------|-----|------------|
| 0x01 in genesis alloc | balance: "0x0" | balance: "0x0" |
| State root block 1799 | Matches | Matches |
| State root block 1800 | 0xd3a3ec14... | 0xdfd5b0cc... |

## Hypothesis

The state root mismatch at block 1800 is likely caused by **different account states in the genesis trie**:

1. **Get** creates 0x01 with balance 0 directly
2. **Nethermind** may be:
   - Creating 0x01 with some non-zero balance (1000 XDC) initially
   - Then overwriting with balance 0 from chainspec
   - This could result in different trie structure or intermediate state

Alternatively, there could be **extra accounts** in Nethermind's genesis trie that geth doesn't have, or vice versa.

## Recommendations

1. **Trace the genesis account creation**: Add logging to `XdcGenesisBuilder.Preallocate()` to see exactly when and how 0x01 is being created with 1000 XDC.

2. **Compare full genesis state**: Dump all account addresses and balances at genesis to compare between geth and Nethermind.

3. **Check for implicit precompile creation**: Search for any code that might create precompiles with default balances during genesis initialization.

4. **Verify at block 1799**: Check if account 0x01 exists in the trie at block 1799 and what its balance is.

## Files to Investigate Further

- `src/Nethermind/Nethermind.Xdc/XdcGenesisBuilder.cs` - Genesis building
- `src/Nethermind/Nethermind.Blockchain/GenesisBuilder.cs` - Base genesis building
- `src/Nethermind/Nethermind.State/StateProvider.cs` - Account creation
- `src/Nethermind/Nethermind.Evm/Precompiles/` - Precompile handling
- `src/Nethermind/Nethermind.Xdc/SnapshotManager.cs` - XDC snapshot storage
