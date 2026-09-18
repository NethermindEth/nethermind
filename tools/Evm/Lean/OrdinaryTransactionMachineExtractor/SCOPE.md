# Ordinary transaction machine: bounded static slice

Status: static source/compiler closure and theorem-free model draft. The package has not
run dotnet, Lake, Lean, or a production execution in this lane. `Generated/` contains a
deliberately marked draft; the serialized lane must run `--extract` and publish the IR,
Lean, and source manifest atomically before any gate can accept them.

The intended source-attached domain is exactly:

- standard Ethereum processor/base gas policy and Amsterdam chainspec identity;
- `ExecutionOptions.Commit` with raw value `1`, with no `Restore`, `Warmup`, `BuildUp`, or
  `SkipValidation` route;
- an ordinary, non-create message call to a live recipient with no executable code and no
  delegation, with `_isCodeOverridable = false`, a null authorization list, and
  `ForceSimpleTransferDisabled = false`;
- `BlockProcessor.ProcessOne`'s `PrepareForProcessing` route selection followed by
  `BlockProcessor.ProcessBlock` using the sequential direct-inner executor only, with
  `IBlockAccessListManager.Enabled = false` and `parallel = false`;
  the DI-selected `BlockValidationTransactionsExecutor.ProcessTransactions` outer entry is
  the concrete non-virtual method on its object-base executor type;
- the exact `SetBlockExecutionContext` chain through `ProcessBlock`, the BAL decorator and direct
  executor, execute adapter, and processor, plus the exact-Commit chain through the registered
  `CreateExecuteAdapter` factory and `ITransactionProcessorExtensions.Execute`; the corresponding
  primary-constructor parameter types and `BlockProcessor` executor-field capture are pinned;
- `Process -> ExecuteCore -> Execute`, static/stateful
  admission, nonce increment, simple-transfer preparation, optional pre-execution commit,
  available gas, `ExecuteSimpleTransfer`, settlement, exact final `WorldState.Commit`, and
  the base `BlockReceiptsTracer` receipt terminal;
- a normally returning adapter/executor/block callback boundary and a list of settled
  `ReceiptTerminal` observations whose selected fields are well formed and status/result are
  `OnlyOkTerminal`. The refinement theorem takes an explicit `ReceiptTerminalChain`,
  proof-carrying per-entry/block callback normal-return premises, and one-step fieldwise
  state/receipt/gas bridges; it proves the successful adapter-supplied settled-list fold by
  induction. It does not relate those entries to the source block transaction array.

`Extractor.cs` uses CSharp 14 Roslyn syntax trees, symbols, `IOperation`, data-flow, CFG
reachability, dominance, post-dominance, and exact source identities. It rejects unresolved,
ambiguous, local-function/lambda, changed, missing, or out-of-order source/reference members.
The receipt terminal is imported only through the pinned `ReceiptTerminalFoldExtractor`
artifact and compiler inventory; the other settled artifacts are identity-pinned handoffs,
not a second implementation of their semantics.

The generated model has independent types for block input, tracer seed, normal-return premises,
settled terminal entries, both receipt-forwarding guards, event order, and outcomes.
`FreshSequentialTracer` resets index `0`,
receipt/gas histories, cumulative receipt gas, and retains the explicit header-gas relation.
Malformed `.ok` entries or truthy `EvmException`/invalid results reject before mutating the
receipt/gas prefix. A valid prefix is retained. Empty input reaches the callback and post-commit
boundaries with an empty receipt/gas history.

The EVM frame/CREATE branch, invalid-result poststate, rewards, withdrawals, execution requests,
BAL-enabled or parallel paths, roots/trie/RLP/hash computation, persistence/background work,
DI/CLR execution, mapping `Block.Transactions` to settled entries, and numeric UInt256/ulong
equivalence are explicit open obligations. This package does not claim that C# was executed or
that the generated model is production equivalent.
