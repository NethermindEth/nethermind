# Ordinary stateful admission-prefix extractor

This package source-admits the standard-mainnet ordinary route from `Process` through the six-parameter `TransactionProcessor.Execute`, then models the bounded stateful prefix under an explicit successful-`ValidateStatic` adapter premise and before `PrepareSimpleTransferFastPath`:

```text
ValidateStatic success
  -> CalculateEffectiveGasPrice
  -> UpdateMetrics
  -> RecoverSenderIfNeeded
  -> ValidateSender
  -> BuyGas
  -> IncrementNonce
```

The source admission checks `Process → ExecuteCore → Execute(tx,tracer,opts) → RecoverSenderBeforeIntrinsicGas → CalculateIntrinsicGas → Execute(..., intrinsicGas)`, including EIP-2780's pre-intrinsic sender replacement. `Generated/OrdinaryStatefulAdmissionPrefix.lean` is generated from a fail-closed Roslyn AST-subset lowering. It has no theorem declarations. Its IR carries complete normalized source-expression trees with grammar-assigned identity and declaration/result-category tags, source-node bindings, complete token fingerprints, widths, formula operations, branch guards and terminals, visible effects, adapter premises, and route edges. Those tags are not Roslyn `ISymbol` or `ITypeSymbol` results: `sourceGrammarSemanticsCoherent` is a mandatory external, error-free compiler-resolution audit over the reviewed closure, and the package does not establish it. The emitter lowers the admitted grammar trees into the live Lean guards and formula bodies; it structurally re-parses each binding, rejects an unfamiliar node, category, identity tag, branch conjunct, or statement, and emits adapter projection trees alongside the live coherence conjunction. This is a manually specified lowering grammar for this pinned C# subset, not a general C# extractor or a proof of CLR semantics. `Reference/OrdinaryStatefulAdmissionPrefixReference.lean` has its own transition decomposition, state/observation records, and terminal view, while deliberately sharing the finite input and observable carrier at the explicit refinement boundary. `Refinement/OrdinaryStatefulAdmissionPrefix.lean` composes simulations for the actual transition segments rather than accepting a one-line definitional wrapper.

The observable output contains the continuation/return/throw result, updated transaction sender, relevant world fields, metric-update and log-call deltas, reset request, prices, premium, reserved payment, blob fee, delete-caller flag, and entered stages. It intentionally retains mutations made before a rejection or recovery throw. The metric list represents `UpdateBlockGasPrice` calls rather than process-global metric storage; the log list represents source logging/tracing calls rather than a logger backend, including `SENDER_ACCOUNT_DOES_NOT_EXIST`. A requested `WorldState.Reset(resetBlockChanges: false)` is represented only as a journal action, and only when the combined `ValidateSender || BuyGas || IncrementNonce` gate returned with `Restore` set. `MaxFeePerBlobGas` and `BlobVersionedHashes` retain their nullable source shape; blob transactions with a missing fee cap are explicitly outside the production predicate and use an opaque model terminal only at the source dereference point in `BuyGas`, after the preceding prefix effects and fee checks.

The finite-width boundary uses modulo `2^64` for unchecked `ulong` arithmetic and checked `UInt256` add/multiply records carrying both the modulo-`2^256` out value and overflow flag. Thus an overflowing source `out senderReservedGasPayment` remains observable with its low 256-bit value. It does not silently use natural-number successor or unbounded balance arithmetic.

## Source boundary

The extractor rejects source drift in these files before it writes an artifact:

| Role | Source |
| --- | --- |
| Prefix, helpers, and standard blob-fee adapter | `src/Nethermind/Nethermind.Evm/TransactionProcessing/TransactionProcessor.cs` |
| Options | `src/Nethermind/Nethermind.Evm/TransactionProcessing/ExecutionOptions.cs` |
| Price, free-transaction, and blob-count helpers | `src/Nethermind/Nethermind.Core/TransactionExtensions.cs` |
| Transaction input projection | `src/Nethermind/Nethermind.Core/Transaction.cs` |
| Transaction-type byte domain and predicates | `src/Nethermind/Nethermind.Core/TxType.cs`, `TxTypeExtensions.cs` |
| Blob-gas projection | `src/Nethermind/Nethermind.Evm/BlobGasCalculator.cs` |
| Blob-gas constant | `src/Nethermind/Nethermind.Core/Eip4844Constants.cs` |
| Ordinary/system routing predicate | `src/Nethermind/Nethermind.Evm/TransactionProcessing/SystemTransactionRoutingKernel.cs` |
| Standard registration | `src/Nethermind/Nethermind.Init/Modules/BlockProcessingModule.cs` |
| Standard world-state path | `src/Nethermind/Nethermind.State/WorldState.cs`, `StateProvider.cs` |
| Excluded processor variants | `SystemTransactionProcessor.cs`, `XdcTransactionProcessor.cs`, `TaikoTransactionProcessor.cs` |

`Admission/ProductionClosure.txt` is the reviewed admission boundary: it pins all fifteen source-file SHA-256 values and every modeled member's path/owner/signature-qualified complete Roslyn token fingerprint. The extractor compares this closure before writing any artifact, so regeneration cannot bless either an unmodeled edit or a changed modeled method. Updating the closure is an intentional source-re-admission step; its own SHA-256 is embedded in both IR and manifest. The manifest then records the accepted identities, semantic IR hash, and generated Lean hash. `--check` regenerates in a unique `D:\tmp` directory and byte-compares all checked-in artifacts.

## Exact claim

For a source tree accepted by this extractor, `sourceGrammarSemanticsCoherent`'s external compiler-resolution-audit premise, and `ordinaryStandardInput`'s finite-width and adapter-coherence predicate, the generated transition has the same complete observation as the handwritten reference for the bounded prefix above. This is a conditional Lean transcription/refinement claim, not production C# semantic refinement. It includes terminal returns, recovery throws with their prior sender/log/state effects, short-circuit ordering, wrapped `UInt256` out assignments, warmup's best-effort debit, nonce wrap, the source-derived `blobCount * GasPerBlob` `ulong` product, the terminal absent-account `SetNonce` throw, and the outer restore request. The blob adapter model distinguishes its two false paths: fee-per-blob failure sets the output total to zero, while a later total-fee failure retains its typed out-value observation. The bounded transcription predicate requires an explicit static-success adapter premise: it supplies a non-null post-EIP-2780 sender and a gas limit of at least 21,000, but does not prove `ValidateStatic` or establish a live-production reachability witness. It also requires an EIP-2780 pre-intrinsic coherence relation, `isFree = false`, `isSystem = false`, a byte-coherent `TxType`/supports projection, coherent nullable blob fields and hash count, source-width `ulong`/`UInt256` values, standard `WorldState → StateProvider` creation and `SetNonce` behavior, coherent supplied/effective/recovered account facts, coherent header/spec/blob oracles, a representable nonnegative signed-`Int32` option value (`raw < 2^31`), and options other than exactly `SkipValidation` or `BuildUp`. Source routes a transaction with exactly raw `SkipValidation` to `SystemTransactionProcessor`; exactly raw `BuildUp` takes a pre-prefix `WorldState.TakeSnapshot` path which this post-static-prefix observation deliberately excludes. The model retains raw non-standard, malformed, and static-failure inputs for bounded helper coverage only; they are outside the ordinary-route corollary.

The transaction/transaction-type/blob-gas closure is deliberately a projection boundary: it source-binds `Transaction`'s relevant properties, byte-backed `TxType` predicates, `GetBlobCount`, all three `BlobGasCalculator.CalculateBlobGas` overloads, and `Eip4844Constants.GasPerBlob`. The generated formula preserves the source `int → ulong` cast and unchecked `ulong` multiplication before widening to the checked `UInt256` calculation. It does not prove transaction-object, blob-list, or calculator implementation correctness beyond those stated projections.

This is not a proof of production C# semantics, compiler symbol/type resolution, the CLR/JIT, ECDSA, header/spec/object adapters, database/journal reset semantics, tracer/logger storage, blob-fee calculation, `ValidateStatic` internals, DI resolution, system/XDC/Taiko/plugin processors, fast path, code lookup, precommit, `CalculateAvailableGas`, VM execution, settlement, receipts, or block processing. The reviewed source closure plus structural AST lowering is a fail-closed source-admission argument; the external compiler-resolution audit is a blocking composition obligation, not a Roslyn-to-Lean correctness theorem.

## Verification

From this directory:

```powershell
.\Verify.ps1
```

The script builds the generator and mutation tests with warnings as errors and `SaveDiskSpace`, regenerates to a guarded temporary directory, byte-compares artifacts, runs `--check`, runs tests, builds Lean, checks the explicit Lean files with warnings promoted to errors, and rejects Lean placeholder tokens.
