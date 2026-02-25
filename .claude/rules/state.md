---
paths:
  - "src/Nethermind/Nethermind.State/**/*.cs"
  - "src/Nethermind/Nethermind.State.Flat/**/*.cs"
---

# Nethermind.State

World state management, accounts, contract storage, and Merkle Patricia trie access.

Key classes:
- `WorldState` — mutable per-block state (accounts + storage)
- `StateReader` — read-only access to committed state
- `IWorldStateManager` — owns and manages the trie store; factory for world state instances

## IWorldState lifecycle (per block)

`IWorldState` is **scoped** in DI — one instance per block processing scope. Do not share it across blocks.

```csharp
// Correct usage inside BlockProcessor.ProcessOne():
worldState.BeginSetStateRoot(blockHeader);  // initialise for this block

// ... apply transactions ...

worldState.MergeToMain(blockHeader.StateRoot!); // commit to main trie
// or
worldState.CommitTree(blockNumber);             // commit and advance
```

Never call `CommitTree` without first applying all changes for that block. Never read `StateRoot` until after `CommitTree`.

## IStateReader — read-only access

`IStateReader` reads **committed state** only — it does not require a scope and is safe to use concurrently. Use it in:
- RPC handlers (`eth_getBalance`, `eth_getCode`, …)
- Trace/call simulations
- Prewarming reads

Do not use `IWorldState` for read-only queries — it holds dirty (uncommitted) state.

## IWorldStateManager

`IWorldStateManager` owns the underlying `ITrieStore` and creates scoped `IWorldState` instances. It is a singleton in DI. Use it to:
- Create a new block-level world state scope: `worldStateManager.GlobalWorldState` (the main mutable instance)
- Access snap sync server: `worldStateManager.SnapServer`

Don't instantiate `WorldState` or `TrieStore` directly — resolve `IWorldStateManager` and use its APIs.

## Read-only tx processing environments

For `eth_call`, `debug_traceCall`, and similar, use:

```csharp
// Get a read-only processing environment
IReadOnlyTxProcessingScope scope = readOnlyTxProcessorSource.Build(stateRoot);
ITransactionProcessor txProcessor = scope.TransactionProcessor;
// ... execute call on scope.WorldState ...
```

- `IReadOnlyTxProcessorSource` is a singleton; `Build(stateRoot)` is cheap — call it per request.
- `IReadOnlyTxProcessingScope` is `IDisposable` — always use `using`.
- `IShareableTxProcessorSource` is a thread-local variant for concurrent trace requests.

## Subdirectories

- `Healing/` — state healing for snap sync gaps
- `OverridableEnv/` — world state with call-level overrides (`eth_call` `stateOverrides`)
- `Proofs/` — Merkle proof generation (`eth_getProof`)
- `Repositories/` — chain-level state repositories (chain level info, etc.)
- `Snap/` — snap sync client-side state download
- `SnapServer/` — snap sync server-side state serving
