# Scope and admission boundary

## Target

The eventual target is a refinement of the pinned standard-mainnet synchronous
branch/blockchain processor to typed Lean state. The first source boundary is:

`BlockchainProcessor.Process` → `BranchProcessor.Process` →
`BlockProcessor.ProcessOne` → `BlockValidationTransactionsExecutor` /
`ParallelBlockValidationTransactionsExecutor` →
`TransactionProcessorBase` and `SystemTransactionProcessor`.

This package only audits that architecture. It does not claim that the concrete
runtime and the neutral state records are equivalent. The runtime spec root is
the configured `ChainSpecBasedSpecProvider` path:
`ApiBuilder.LoadChainSpec` → `ChainSpecFileLoader.LoadEmbeddedOrFromFile` →
`AutoDetectingChainSpecLoader` → `ChainSpecLoader` →
`ChainSpecBasedSpecProvider`, with `mainnet.json`, `Chains/foundation.json`, and
`Nethermind.Config.csproj`'s embedded-resource mapping pinned. The loader's
`Nethermind.Config.chainspec.foundation.json` name derivation and `IConfig`
assembly lookup are source checked. The pinned `foundation.json` does not yet
configure the target Amsterdam transition properties; the Amsterdam class is
the target schedule type rather than an activated-mainnet claim.

## What is represented

The scaffold has explicit records for:

- proposed versus processing headers, including processing resets and preserved
  fields;
- logical world state, journal version, account changes, and owned versus
  externally owned scopes;
- execution gas, state gas, cumulative paid gas, and header gas as the maximum
  of the two block dimensions;
- receipts, status, paid/cumulative gas, logs, and blooms;
- ordinary versus system transaction observations;
- BAL preparation/finalization, execution-request observations, and inclusion
  signals;
- invalid-block rejection versus skipped results versus escaping exceptions;
- parallel-attempt, BAL retry, scope disposal/reopen, checkpoint, reset,
  `CommitTree`, total-difficulty, head-update, and mark-processed observations.

The phase plan preserves the 17 labels in the processing manifest. Its hook
catalog identifies the production source symbol, input/output shape, failure
disposition, receipt dependency, scope requirement, and logical-state mutation
for each of the 57 unique hooks. The C# catalog, JSON mirror, and Lean string
mirror must agree exactly; the typed Lean phase membership and hook/phase pairs
are checked separately and all handwritten Lean inputs are byte-pinned. Phase
membership is separate from source-checked partial-order edges, including the
per-transaction receipt lifecycle, synchronous receipt arm, and task-local
background arm.

The inclusion-list result is an `Option Bool` observation: `false` is a
committed signal and is not an invalid-block reason. Scope lifecycle events use
a typed operation and a nonnegative block index. Pre-branch chain outcomes do
not contain a fabricated branch result.

## Admission gates for a future extractor

Before any operational extraction is allowed, all of the following must be
closed by independent source and semantic evidence:

1. the source/configuration pins, exact route anchors, and checked metadata
   mirror must match a reviewed baseline;
2. concrete ordinary/system transaction settlement must refine the receipt and
   two-dimensional gas records;
3. frame execution, world journals, rollback, state creation and account
   changes must refine the logical state;
4. standard system operations and EIP-8037/8038 accounting must be connected
   through the actual transaction adapter;
5. receipt bloom/root, state root, BAL, request, and header computations must
   be connected with their real widths, hashing and trie semantics;
6. the byte-pinned `BlockSystemComposition` and `ParallelBlockReference`
   inputs must be semantically composed; parallel/BAL execution must either be
   proved equivalent or remain an explicitly separate unproved path;
7. scope rollback, `CommitTree`, persistence, crash and restart behavior must
   be covered if durability is in the claim;
8. DI resolution, virtual dispatch and CLR/JIT/compiler assumptions must be
   stated separately from the mathematical proof.

The extractor therefore refuses to emit an IR, Lean kernel, or theorem while
these gates are open. The current `SOURCE_PINS.json` is an audit-scaffold
identity set, not a semantic admission.

## Explicit exclusions

Async queue scheduling, background receipt task interleavings, persistence
durability, trie/RLP/hash implementations, cryptographic/precompile semantics,
parallel/BAL equivalence, and CLR/JIT correctness are intentionally open.
DAO, beacon-root, historical-blockhash, rewards, withdrawals, execution
requests, and system-call bodies are named hooks rather than caller-selected
relations or proved implementations.
