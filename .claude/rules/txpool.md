---
paths:
  - "src/Nethermind/Nethermind.TxPool/**/*.cs"
---

# Nethermind.TxPool

Transaction pool (mempool) management, validation, sorting, and gossip.

Key classes:
- `TxPool` — main pool implementation
- `ITxPool` — primary interface (submit, remove, query)
- `AcceptTxResult` — result type for transaction acceptance decisions

## AcceptTxResult — use named constants

Transaction submission returns `AcceptTxResult`. Always use the named constants, never compare to raw booleans or integers:

```csharp
AcceptTxResult result = txPool.SubmitTx(tx, TxHandlingOptions.None);

// Correct
if (result != AcceptTxResult.Accepted) { /* handle rejection */ }

// Named rejection constants: .Invalid, .AlreadyKnown, .Overflow,
// .FeeTooLow, .FeeTooLowToCompete, .SenderIsContract, .NonceTooLow,
// .OldNonce, .GasLimitExceeded, .NotSupportedTxType, …
```

## Validation — IIncomingTxFilter

Add new validation rules by implementing `IIncomingTxFilter` and registering it in the filter chain:

```csharp
public class MyFilter : IIncomingTxFilter
{
    public AcceptTxResult Accept(Transaction tx, ref TxFilteringState state, TxHandlingOptions options)
    {
        if (SomeCondition(tx)) return AcceptTxResult.Invalid.WithMessage("reason");
        return AcceptTxResult.Accepted;
    }
}
```

- Never modify pool internal state inside a filter — filters are read-only guards.
- The filter chain is assembled in `TxPoolModule` / `BlockProcessingModule`; register with `AddComposite<IIncomingTxFilter, ...>()`.
- Filters receive a `ref TxFilteringState` for cheap per-call shared context (nonce, sender account).

## Blob transactions (EIP-4844)

Blob transactions are handled separately from regular transactions:

- `BlobTxDistinctSortedPool` — the blob-specific sub-pool; has its own size limits (`MaxBlobTx`)
- `BlobTxStorage` — persists blob data across restarts
- Do not treat blob transactions as standard in filter logic — check `tx.SupportsBlobs` before assuming standard fields like `GasPrice` are set in the usual way

## Gossip — ITxGossipPolicy

Control what gets relayed to peers via `ITxGossipPolicy`:

```csharp
public class MyGossipPolicy : ITxGossipPolicy
{
    public bool ShouldGossipTransaction(Transaction tx) => !tx.IsPrivate;
}
```

Register as a composite: `AddComposite<ITxGossipPolicy, CompositeTxGossipPolicy>()`. Never modify `TxPool` directly to suppress gossip.

## Subdirectories

- `Collections/` — `TxDistinctSortedPool`, `BlobTxDistinctSortedPool`: sorted pools by fee/nonce
- `Comparison/` — `ITransactionComparerProvider`, fee-based comparers
- `Filters/` — `IIncomingTxFilter` implementations (nonce, gas, fee, type checks)
