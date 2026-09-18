# Source audit

## Admitted route

The route is accepted only if `BlockProcessingModule.Load` contains the exact builder-chain registrations for `EthereumTransactionProcessor` as `ITransactionProcessor`, `BlobBaseFeeCalculator` as `ITransactionProcessor.IBlobBaseFeeCalculator`, and `WorldState` as `IWorldState`; the concrete processor closes `EthereumTransactionProcessorBase`; and the base closes `TransactionProcessorBase<EthereumGasPolicy>`. The extractor binds containing member, receiver, top-level builder chain, statement ordinal, and control-flow path rather than accepting source-wide same-named calls. It also binds `ExecuteCore`'s direct use of `SystemTransactionRoutingKernel.UseSystemProcessor(tx.IsSystem(), opts)`.

`UseSystemProcessor` is required to retain its exact `isSystemTransaction || options == ExecutionOptions.SkipValidation` predicate. The ordinary refinement corollary consequently requires a non-free, non-system transaction, a raw option value below `2^31` (the default signed-`Int32` enum backing), and options other than exactly `SkipValidation` or `BuildUp`: raw `SkipValidation` routes to the system processor and raw `BuildUp` takes `Process`'s pre-prefix snapshot action. `Warmup | SkipValidation` is distinct and remains in the bounded helper model. The extractor separately binds source markers for the system `BuyGas` override, XDC effective-price override, and Taiko processor to make exclusions explicit rather than silently following polymorphism.

Before the bounded six-parameter prefix, the extractor follows and pins:

1. `Process(transaction, tracer, options) → ExecuteCore(transaction, tracer, options)`;
2. ordinary `ExecuteCore → Execute(tx, tracer, opts)` after the system-route guard;
3. three-parameter `Execute → RecoverSenderBeforeIntrinsicGas → CalculateIntrinsicGas →` six-parameter `Execute`;
4. the EIP-2780 guard and `tx.SenderAddress = Ecdsa.RecoverAddress(...)` before intrinsic-gas calculation.

The ECDSA result and account-existence read are external observations, but `preIntrinsicEip2780Coherent` connects both the post-EIP-2780 sender and its account-existence fact to the bounded Lean model. The later recovery either retains that supplied fact or explicitly switches to the independently observed recovered-sender account fact.

## Prefix ordering

The extractor finds the particular `Execute` overload and requires this ordering by syntax span:

1. `ValidateStatic`
2. `CalculateEffectiveGasPrice`
3. `UpdateMetrics`
4. `RecoverSenderIfNeeded`
5. `ValidateSender`
6. `BuyGas`
7. `IncrementNonce`
8. `PrepareSimpleTransferFastPath` boundary

It additionally requires the original combined short-circuit syntax for sender validation, gas purchase, and nonce increment; no reset before the fast-path boundary; and `WorldState.Reset(resetBlockChanges: false)` inside that exact failed gate.

## Bound helpers

The source binding checks the current canonical bodies for:

- EIP-1559 effective price with `UInt256.AddOverflow` and `UInt256.Min`;
- metrics equality (`Commit` or `None`), not a flag containment check;
- the `ValidateStatic` null-supplied-sender rejection needed by the production corollary (all other static checks remain outside scope); success is a named adapter premise, including a minimum gas-limit observation of 21,000, not a reachability proof;
- absent/nonexistent supplied-sender recovery, EIP-2780's message-call exception, the `SENDER_ACCOUNT_DOES_NOT_EXIST` trace, zero-balance/zero-nonce account creation, adapter-exception ordering, and terminal throw;
- invalid-contract-sender validation;
- `ShouldValidateGas`, its short-circuited premium call, fee reservation, balance cap/value/blob overflow paths, wrapped `senderReservedGasPayment` out assignments, warmup debit, and normal debit;
- the standard blob-fee adapter's zero `totalBlobBaseFee` assignment only when `TryCalculateFeePerBlobGas` fails, followed by delegated `TryCalculateBlobBaseFee` whose false-result out value remains an oracle observation;
- source's `validate || nonce < ulong.MaxValue ? nonce + 1 : 0` nonce behavior and `WorldState.SetNonce → StateProvider.SetNonce` absent-account throw-before-write behavior.

The executable model treats the admitted `UInt256.AddOverflow` and `UInt256.MultiplyOverflow` `out` values as modulo-`2^256` low results even when their Boolean reports overflow. That is an explicit arithmetic-adapter premise for the pinned `Nethermind.Numerics.Int256` 1.8.0 package; the Roslyn source binding proves call/order/operand shape, not that external binary implementation.

## Reviewed closure and input projections

`Admission/ProductionClosure.txt` is checked before Roslyn lowering and before artifact output. It pins all fifteen source-file bytes and every modeled source member by path, owner, signature, and complete Roslyn-token SHA-256. Consequently, a source edit cannot be accepted merely by regenerating the manifest: it first needs a reviewed closure update. The manifest is evidence of that accepted closure, not authority to refresh it.

The closure includes the input-projection dependencies omitted by an opcode-only view of this prefix:

- `Transaction` properties for sender, message-call, EIP-1559, and blob support;
- nullable `MaxFeePerBlobGas` and `BlobVersionedHashes`, plus `ulong` gas-limit/nonce fields;
- byte-backed `TxType` and `TxTypeExtensions` support predicates;
- `Transaction.GetBlobCount`, all three `BlobGasCalculator.CalculateBlobGas` overloads, and `Eip4844Constants.GasPerBlob`.

The pure input adapter supplies a pre-prefix snapshot of transaction fields, not a free blob-gas value. The generated model follows the admitted `BlobVersionedHashes?.Length ?? 0`, `int → ulong`, and unchecked `blobCount * Eip4844Constants.GasPerBlob` path before the checked UInt256 multiplication. Outer-null blob hashes map to count zero; a blob transaction with a null max fee is excluded by the production predicate rather than given an invented C# result. The source binds the projection path and width, while blob-list traversal, transaction object behavior, header/spec reads, world/account facts, and fee-calculator internals remain explicit adapter obligations.

The semantic IR is not a descriptive sidecar. It contains typed widths, formula operations, branch guards and terminals, effects, route bindings, and adapter premises. Each executable operation, guard, and adapter binding carries a complete normalized Roslyn syntax tree with grammar-assigned identity and declaration/result-category tags, plus full token fingerprint, containing member, receiver, statement ordinal, and control-flow path. Those tags are not compiler-resolved `ISymbol` or `ITypeSymbol` data. `sourceGrammarSemanticsCoherent` records the blocking external obligation that an error-free compiler-resolution audit over the reviewed closure agrees with every admitted tag; the extractor does not establish that obligation. The emitter structurally re-lowers those trees into the live Lean bodies and guards; a changed operator, invocation, grammar category, conjunct, or added `UpdateMetrics` statement either changes the emitted Lean or is rejected. It uses the typed adapter list to construct the bounded-transcription coherence conjunction, and fails closed on an exact field-complete mapping. Unknown formula, width, branch guard, terminal, effect, source owner, adapter field, receiver, or invocation order fails emission.

The `CreateAccount` zero-field transition is restricted to the admitted standard `IWorldState → WorldState → StateProvider` chain, whose source is pinned and whose `StateProvider.CreateAccount` creates `Account.TotallyEmpty` for zero balance/nonce. It is not asserted for `BlockAccessListBasedWorldState` or any alternate world-state mode.
