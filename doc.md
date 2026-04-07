# Fix: `debug_executionWitness` fails on Taiko blocks

## Problem

Calling `debug_executionWitness` with any block number on a Taiko L2 Nethermind node fails:

```
error code -32000: insufficient MaxFeePerGas for sender balance
```

Every Taiko block starts with an **anchor transaction** — the first transaction sent from the
"golden touch" address (`0x0000777735367b36bC9B61C50022d9D0700dB4Ec`). This address holds zero ETH
by design. Taiko's consensus rules explicitly skip balance checks for anchor txs, so normal block
processing works fine. But `debug_executionWitness` was hardcoding standard Ethereum components
that don't know about anchor transactions.

## Root cause

`WitnessGeneratingBlockProcessingEnv.CreateExistingBlockWitnessCollector()` manually constructed
two components that should come from the DI container:

1. `TransactionProcessor<EthereumGasPolicy>` — standard Ethereum tx processor that checks sender
   balance in `BuyGas()`. The Taiko plugin registers `TaikoTransactionProcessor` which overrides
   `BuyGas()` to return `Ok` with zero gas cost when `tx.IsAnchorTx` is true.

2. `BlockProcessor.BlockValidationTransactionsExecutor` — standard executor that doesn't set
   `IsAnchorTx`. The Taiko plugin registers `TaikoBlockValidationTransactionExecutor` which checks
   if the sender is GoldenTouch and `i == 0`, then sets `currentTx.IsAnchorTx = true`.

Both problems compound: even if we had the right tx processor, the standard executor would never
flag the transaction as an anchor, so the bypass in `BuyGas()` would never trigger.

## How Taiko hooks in (normal block processing)

The Taiko plugin (`TaikoPlugin.cs`) registers via Autofac DI:

```csharp
// Root-level scoped registration
.AddScoped<ITransactionProcessor, TaikoTransactionProcessor>()

// Via IBlockValidationModule (loaded into processing scopes)
.AddScoped<IBlockProcessor.IBlockTransactionsExecutor, TaikoBlockValidationTransactionExecutor>()
```

`TaikoTransactionProcessor` overrides 5 methods for anchor tx handling:
- `ValidateStatic()` — adds `SkipValidationAndCommit` flag
- `BuyGas()` — returns Ok with zero gas cost (the balance bypass)
- `IncrementNonce()` — creates GoldenTouch account if it doesn't exist
- `PayFees()` — custom base fee distribution (treasury + coinbase split)
- `PayRefund()` — skips gas refund

The `IBlockValidationModule` pattern is how plugins inject per-scope components. Every processing
context in Nethermind loads these modules:
- `MainProcessingContext` — `.AddModule(blockValidationModules)`
- `SimulateReadOnlyBlocksProcessingEnvFactory` — `.AddModule(validationModules)`
- `DebugModuleFactory` — `.AddModule(validationBlockProcessingModules)`

The witness generation factory was the only one that didn't.

## The fix

### `WitnessGeneratingBlockProcessingEnvFactory.cs`

Added `IBlockValidationModule[] validationModules` to the constructor (auto-injected by Autofac).

The scope now:
1. Creates `WitnessGeneratingWorldState` and `WitnessGeneratingHeaderFinder` in the factory
   (previously created inside `CreateExistingBlockWitnessCollector()`)
2. Registers them as scope overrides so DI-resolved components use them
3. Calls `.AddModule(validationModules)` — loads plugin-specific tx executor registrations
4. Overrides `IBlockhashCache` as scoped (so the new `BlockhashCache` uses the witness header finder
   instead of the root scope's singleton)
5. Overrides `IReceiptStorage` with `NullReceiptStorage` (witness generation doesn't need receipts)
6. Registers `WitnessGeneratingBlockProcessingEnv` for DI resolution

### `WitnessGeneratingBlockProcessingEnv.cs`

Simplified from 9 constructor params + manual component wiring to 3 DI-injected dependencies:

```csharp
public class WitnessGeneratingBlockProcessingEnv(
    WitnessGeneratingWorldState witnessWorldState,
    IBlockProcessor blockProcessor,
    ISpecProvider specProvider) : IWitnessGeneratingBlockProcessingEnv
{
    public IExistingBlockWitnessCollector CreateExistingBlockWitnessCollector()
        => new WitnessCollector(witnessWorldState, blockProcessor, specProvider);
}
```

The `IBlockProcessor` resolved by Autofac is a `BlockProcessor` that already contains:
- `TaikoTransactionProcessor` (bypasses `BuyGas` for anchor txs)
- `TaikoBlockValidationTransactionExecutor` (sets `IsAnchorTx = true`)
- `TaikoBlockValidator` (Taiko validation rules)
- All other components (`BeaconBlockRootHandler`, `BlockhashStore`, `WithdrawalProcessor`, etc.)

Removed the hardcoded `CreateTransactionProcessor()` method entirely.

## DI resolution chain (with Taiko active)

```
IBlockProcessor (scoped BlockProcessor)
  ├── IBlockProcessor.IBlockTransactionsExecutor
  │     → TaikoBlockValidationTransactionExecutor  (from .AddModule(validationModules))
  │       └── ITransactionProcessorAdapter → wraps ITransactionProcessor
  │             → TaikoTransactionProcessor  (from root scope, scoped)
  │               ├── IWorldState → WitnessGeneratingWorldState  (from scope override)
  │               ├── IVirtualMachine → EthereumVirtualMachine
  │               │     └── IBlockhashProvider → BlockhashProvider
  │               │           └── IBlockhashCache → BlockhashCache (scoped override)
  │               │                 └── IHeaderFinder → WitnessGeneratingHeaderFinder
  │               └── ICodeInfoRepository → CacheCodeInfoRepository
  ├── IBlockValidator → TaikoBlockValidator  (singleton from root)
  ├── IWorldState → WitnessGeneratingWorldState  (from scope override)
  ├── IReceiptStorage → NullReceiptStorage  (from scope override)
  └── ... (BeaconBlockRootHandler, BlockhashStore, etc. — all scoped, resolved normally)
```

## Execution flow after fix

```
debug_executionWitness(blockNumber=25)
  → DebugRpcModule → BlockchainBridge.GenerateExecutionWitness(parent, block25)
    → factory.CreateScope()  // new scope with witness overrides + validation modules
    → DI resolves WitnessGeneratingBlockProcessingEnv
      → DI resolves IBlockProcessor = BlockProcessor with Taiko components
    → WitnessCollector.GetWitnessForExistingBlock(parent, block25)
      → blockProcessor.ProcessOne(block25, ReadOnlyChain)
        → TaikoBlockValidationTransactionExecutor.ProcessTransaction(block, anchorTx, i=0)
          → sender == GoldenTouch && i == 0 → anchorTx.IsAnchorTx = true
          → TaikoTransactionProcessor.Execute(anchorTx, ...)
            → BuyGas(): IsAnchorTx → return Ok (zero gas cost, no balance check)
            → anchor tx executes successfully
        → remaining txs process normally
      → witnessWorldState.GetWitness(parent) → returns collected witness
```

## Files changed

| File | Change |
|------|--------|
| `Nethermind.Consensus/Stateless/WitnessGeneratingBlockProcessingEnv.cs` | Simplified: 9 params → 3, removed hardcoded component creation |
| `Nethermind.Consensus/Stateless/WitnessGeneratingBlockProcessingEnvFactory.cs` | Added `IBlockValidationModule[]`, moved witness wrappers here, added `.AddModule(validationModules)` |

No test changes needed — existing `DebugRpcModuleTests.ExecutionWitness.cs` tests exercise the same
code path through the standard DI pipeline.
