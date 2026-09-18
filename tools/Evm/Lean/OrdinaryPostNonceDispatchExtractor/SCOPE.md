# Scope and non-claim

## Included transition

The model starts on the successful continuation after the ordinary
standard-mainnet six-parameter `Execute` has evaluated `IncrementNonce`. It
retains this exact order:

1. `PrepareSimpleTransferFastPath`, including null initialization of both
   preloaded outputs, `tx.To`, the candidate short-circuit, cached code lookup,
   and delegation-first `CodeInfo.IsEmpty` selection;
2. the source `Restore` and effective `Commit` formulas;
3. the guarded `WorldState.Commit` request, selecting the tracing tracer and
   `commitRoots: false`, when `commitBeforeExecution` is true;
4. `CalculateAvailableGas` and its failure policy; and
5. the simple-transfer or EVM typed handoff. These are terminal observations
   of the typed call boundary; neither handoff is composed with downstream
   execution semantics here.

The only terminal outcomes are `EscapedLookup`, `EscapedPrecommit`,
`GasRejected`, `SimpleHandoff`, and `EvmHandoff`. Both handoff forms retain
`tx`, `header`, `spec`, `tracer`, `opts`, `restore`, `commit`,
`deleteCallerAccount`, `intrinsic`, `gasAvailable`, `opcodePrice`, `premium`,
`reserved`, and `blobBaseFee`. The preload is one algebraic value,
`none | loaded { codeHandle, delegation, codeIsEmpty }`.

## Source boundary

The extractor source-binds the ordinary call path and route closure needed to
justify the boundary: `Process`, `ExecuteCore`, the three-parameter `Execute`,
`RecoverSenderBeforeIntrinsicGas`, the six-parameter overload,
`ExecutionOptions`, the system-route predicate, standard-mainnet DI, Ethereum
processor inheritance, code repository interface and implementations,
`CodeInfo.IsEmpty`, `IWorldState.Commit` and its normal `WorldState` route,
`EthereumGasPolicy`, `IntrinsicGas`, available-gas initialization, and the
transaction `To`/`AuthorizationList` projections.

The previous ordinary-stateful prefix is an explicit source-order/erasure
dependency. Its semantic bytes are not reused. The transaction lifecycle
artifact is likewise used only for lifecycle erasure vocabulary.

## Excluded behavior

The package does not model static admission, sender recovery, execution, target
code loading after an EIP-8037 delegation, VM/frame behavior, settlement,
receipts, block accounting, state-root persistence, system/XDC/Taiko/parallel
routes, or BAL orchestration. It does not claim that Roslyn's admitted source
slice proves CLR/JIT behavior, DI construction, database behavior, exception
mechanics, or the internals of the effectful lookup/commit/gas-policy calls.

No `staticAccepted_prefixBridge_implies_gasSuccess` claim is emitted: this
bounded package starts after the already-successful admission prefix and treats
gas initialization as an explicit effect observation. A future EVM handoff
bridge is kept as a separate obligation in the refinement module.
