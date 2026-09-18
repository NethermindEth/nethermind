# Scope and claim boundary

## Accepted source path

The admitted source closure is pinned in `SOURCE_PINS.json`:

1. `src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.BlockValidationTransactionsExecutor.cs`
2. `src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.cs`
3. `src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.std.cs`
4. `src/Nethermind/Nethermind.Consensus/Processing/IBlockProcessor.cs`
5. `src/Nethermind/Nethermind.Consensus/Processing/ProcessingOptions.cs`

Twelve support inputs complete the selected partial declarations and referenced internal types,
select standard execution flags, and bind the MSBuild EvmWord alias; all are pin-checked, and
all four production-assembly compilations are diagnosed in full. The auxiliary source closure is
also pinned in the same document: the transaction adapter,
exact-base `BlockReceiptsTracer`, `BlockAccessListManager` and interface, the BAL parallel
decorator, the standard block-processing DI module, and `ITransactionProcessorAdapter`.

The first file supplies the transaction loop and the virtual helper. The second supplies the
caller boundary and normal-return signal. The partial, interface, and flag files close the source
identity set without admitting their unrelated behavior. The auxiliary files close the tracer,
adapter, BAL guard/derivation, decorator fallback, exact-base declaration, and DI registration
relations. The claim is still limited to a direct inner executor invocation with `balEnabled =
false`; runtime DI activation and dispatch, including the absence of a plugin replacement, are
explicit uninterpreted route premises.

## Source-bound relations

The extractor requires the following canonical Roslyn nodes:

- metrics setup before the loop;
- `NoValidation`-controlled gas-limit validation;
- the gas-limit throw as a direct body of that guard, with a reachable typed operation;
- one `ProcessTransaction` call per source-order loop iteration;
- the false `TransactionResult` invalid-transaction throw before the processed event;
- the false-result throw as a direct `!result` guard body whose guard dominates the processed event;
- the ordinary transaction-processor adapter invocation;
- the transaction-processed event's receipt-index observation;
- the `ProcessBlock` transaction call;
- the nearest `CommitState(spec)` before that call and nearest one after it; later commits remain
  outside this slice.
- the unconditional evaluation of the typed `TransactionsExecuted?.Invoke()` statement between
  normal executor return and the first post-fold commit, with the optional invocation bound
  separately on the non-null branch.
- the normal ProcessBlock CFG route: `StartNewBlockTrace` dominates the pre-fold commit, the
  pre-fold commit dominates the fold, the event evaluation postdominates the fold, and the
  post-fold commit postdominates the evaluation.
- `StartNewBlockTrace` reset, receipt append/index, `StartNewTxTrace`/`EndTxTrace`, and the
  adapter's `StartNewTxTrace -> Execute -> EndTxTrace` order;
- `BlockAccessListManager.Enabled` derivation, BAL-disabled decorator fallback, and base-before-
  decorator DI registration order.

Every admitted node carries its source path, canonical token syntax, syntax digest, symbol
identity, operation kind/type, candidate/error status, data-flow result, source position, and CFG
block. Auxiliary anchors additionally carry their containing method and reachable-block bit;
assignments carry an exact ordered RHS inventory of calls, receiver symbols/types, field/property
references, and parameter owners/ordinals. The current-index target is a property and its postfix
increment is admitted as Roslyn `Increment`.
local-function/lambda owners are rejected. Lifecycle relations use CFG dominance and normal-path
postdominance. Direct gas-limit and invalid-result throws, and the BAL-disabled inner call, must
each be the sole expected statement in the relevant guard true arm; bypass branches are rejected.
Member and anchor identities are rejected if a candidate or error symbol is present. The serialized
IR and manifest are strict and fail closed on unknown fields or identity mutations.

## Lean model

`Generated/SequentialBlockTransactionFold.lean` models only:

- the direct-inner route under `balEnabled = false`;
- `parallel = false` at both fold input and receipt-terminal state;
- `shouldValidate` mirrors the source `NoValidation` flag for the gas-limit guard;
- standard, non-system entries;
- already-settled `FinalizationObservation` values;
- `OnlyOkTerminal` `.ok` `TransactionResult` values from the normal-return adapter path; a truthy
  `EvmException`/revert result is not admitted, and false-result terminal observations are rejected
  as malformed input because the source can end the trace before throw;
- one-item receipt/gas-history extensions with receipt index equal to the transaction's
  pre-`EndTxTrace` current index;
- `StartNewBlockTrace` index zero, per-transaction trace-start/trace-end projection, and the
  next-current-index increment after `EndTxTrace`;
- the source-ordered pre-transaction commit marker and post-fold commit marker;
- source-order state, receipt-result, and completed-index accumulation;
- contiguous transaction indices from the source-order loop (mismatches are unsupported);
- invalid transaction and block-gas-limit prefix stops;
- explicit unsupported outcomes for parallel, system, non-standard, and state-chain-mismatch
  entries;
- the post-fold commit event on successful exhaustion.

The state fields are imported from `ReceiptTerminalFoldExtractor.Generated.ReceiptTerminalFoldKernel`.
No terminal observation is re-executed by this fold.

The `FreshSequentialTracer` adapter must establish current index zero, empty receipt/gas
histories, zero cumulative receipt gas, sequential mode, and identify the terminal state's
`headerGasUsed` with production `block.Header.GasUsed` before applying the source gas-limit guard.
The primary theorem takes and consumes `ReceiptTerminalChain` only. It is a conditional theorem
about the generated fold and the independent specification, not production execution.
`OpenSourceCompositionObligation` is explicitly unproved: an independently justified execution
relation must establish fresh entry, exact runtime route and production observations. No caller
can close the model theorem's source gap by filling arbitrary proposition or result-equality fields.
Generated event-tail shape remains a completed-result conclusion.

An invalid-prefix result is a logical prefix projection. Production disposal/rollback after an
invalid transaction exception remains an external obligation. In particular, the source's false
`TransactionResult` path can run `EndTxTrace` before throwing; this package does not model that
poststate as a successful settled observation. Malformed `.ok` observations are rejected with
unchanged histories.

The caller's `TransactionsExecuted` signal is source-audited between the normal fold return and
the post-transaction commit. Its subscriber behavior and cancellation/background effects are not
part of the Lean state model.

## Explicit exclusions

This package makes no claim about transaction execution, EVM semantics, gas production, trie/root/
RLP/hash computation, rewards, withdrawals, execution requests, parallel workers, system
transactions, persistence, background tasks, runtime standard-mainnet DI activation, CLR/JIT
execution, or state commit durability/rollback. The source registration and guard relations are
evidence only; production source composition remains explicitly unproved. The caller's pre-commit call is
represented by the initial event marker; the post-fold commit is emitted only after normal
exhaustion.
