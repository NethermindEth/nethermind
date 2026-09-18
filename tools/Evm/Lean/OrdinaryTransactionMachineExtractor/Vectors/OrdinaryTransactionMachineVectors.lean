-- SPDX-License-Identifier: LGPL-3.0-only

/-! Named fail-closed mutation vectors for the static ordinary-machine boundary.
These are data consumed by the later Roslyn/Lean gate, not production execution tests. -/

namespace OrdinaryTransactionMachineExtractor.Vectors

structure MutationVector where
  name : String
  target : String
  expected : String
  deriving DecidableEq, Repr

def vectors : List MutationVector := [
  { name := "empty-input"
    target := "entries := []"
    expected := "foldReturn, transactionsExecuted, postTransactionCommit with empty receipt/gas histories" },
  { name := "nonzero-initial-index"
    target := "tracerSeed.currentIndex := 7"
    expected := "freshSequentialTracer currentIndex = 0" },
  { name := "invalid-first"
    target := "result := .evmException"
    expected := "rejected invalidResult/exceptionResult before append" },
  { name := "malformed-first"
    target := "wellFormed := false"
    expected := "rejected malformedSettled with unchanged prefix" },
  { name := "malformed-second-after-valid-prefix"
    target := "second entry wellFormed := false"
    expected := "rejected with first receipt/gas prefix unchanged at second entry" },
  { name := "two-entry-indices"
    target := "receipt.index := [0, 1]"
    expected := "sequentialIndexInvariant and receipt/gas append order" },
  { name := "receipt-index"
    target := "receipt.index := 1 for first entry"
    expected := "rejected malformedSettled" },
  { name := "gas-total"
    target := "gasTotals.executionGas differs from terminal observation"
    expected := "rejected malformedSettled without accumulator mutation" },
  { name := "end-increment"
    target := "remove currentIndex increment"
    expected := "rejected missing index-chain evidence" },
  { name := "start-reset"
    target := "remove currentIndex := 0"
    expected := "rejected missing FreshSequentialTracer invariant" },
  { name := "mark-append"
    target := "remove ReceiptTerminal markAsSuccess append"
    expected := "rejected receipt observation mismatch" },
  { name := "adapter-order"
    target := "EndTxTrace before Execute"
    expected := "rejected CFG source-order/post-dominance" },
  { name := "conditional-start"
    target := "conditional StartNewBlockTrace"
    expected := "rejected ProcessBlock dominance" },
  { name := "conditional-precommit"
    target := "conditional pre-fold CommitState"
    expected := "rejected ProcessBlock dominance" },
  { name := "early-return-before-callback"
    target := "return after fold before TransactionsExecuted"
    expected := "rejected callback post-dominance" },
  { name := "early-return-before-postcommit"
    target := "return after callback before post CommitState"
    expected := "rejected post-commit post-dominance" },
  { name := "gas-throw-bypass-in-true-arm"
    target := "gas guard true arm without direct throw"
    expected := "rejected sole direct invocation" },
  { name := "invalid-result-throw-bypass-in-true-arm"
    target := "invalid-result guard true arm without direct throw"
    expected := "rejected sole direct invocation" },
  { name := "bal-inner-return-bypass-in-true-arm"
    target := "BAL-disabled true arm with extra statement"
    expected := "rejected sole direct return" },
  { name := "bal-enabled-derivation"
    target := "BlockAccessListManager.Enabled derivation changed"
    expected := "rejected BAL-disabled route premise" },
  { name := "di-decorator-route"
    target := "parallel executor decorator dispatch changed"
    expected := "rejected direct-inner route premise" },
  { name := "exact-base-executor"
    target := "executor declaration or virtual dispatch changed"
    expected := "rejected exact-base route premise" },
  { name := "local-owner-relocation"
    target := "move bound selector into local function/lambda"
    expected := "rejected local-function or lambda owner" },
  { name := "compiler-reference-mutation"
    target := "change compiler/reference SHA, MVID or dependency list"
    expected := "rejected compiler/reference identity drift" },
  { name := "compiler-reference-missing"
    target := "remove an inventory binary"
    expected := "rejected missing compiler/reference binary" },
  { name := "compiler-reference-ambiguity"
    target := "duplicate path or assembly identity"
    expected := "rejected duplicate or malformed identity" },
  { name := "receipt-source-pin-drift"
    target := "change a transitive ReceiptTerminal source pin or role"
    expected := "rejected pinned receipt source closure" },
  { name := "receipt-manifest-pin-drift"
    target := "change a ReceiptTerminal source-manifest path or SHA"
    expected := "rejected source-pin/manifest mismatch" },
  { name := "receipt-accounting-artifact-missing"
    target := "remove the delegated accounting IR/refinement/manifest"
    expected := "rejected missing transitive receipt artifact" },
  { name := "receipt-compiler-manifest-ambiguity"
    target := "duplicate or reorder a ReceiptTerminal manifest compiler reference"
    expected := "rejected compiler manifest closure ambiguity" },
  { name := "adapter-early-return"
    target := "return before EndTxTrace"
    expected := "rejected adapter EndTxTrace post-dominance" },
  { name := "normal-return-field-false"
    target := "one transaction/executor/block normal-return flag := false"
    expected := "generated run rejects normalReturn and refinement proof cannot be constructed" },
  { name := "normality-missing-entry"
    target := "one settled entry without a positional EntrySourceNormality proof"
    expected := "normality/list lengths disagree and refinement proof cannot be constructed" },
  { name := "settled-proof-mismatch"
    target := "EntrySourceNormality paired with a non-success or non-ok settled entry"
    expected := "settledReturnObservation cannot be constructed" },
  { name := "adapter-local-owner"
    target := "move Start/Execute/End into a local function or lambda"
    expected := "rejected nested-function source owner" },
  { name := "tracer-reset-index-value"
    target := "StartNewBlockTrace sets currentIndex := 1"
    expected := "rejected fresh sequential index reset" },
  { name := "tracer-reset-current"
    target := "StartNewBlockTrace retains a prior CurrentTx"
    expected := "rejected fresh transaction reset" },
  { name := "tracer-reset-tracer"
    target := "StartNewBlockTrace retains a prior transaction tracer"
    expected := "rejected fresh null-tracer reset" },
  { name := "tracer-reset-receipts"
    target := "replace receipt Clear with another operation"
    expected := "rejected receipt-history reset" },
  { name := "tracer-reset-receipts-conditional"
    target := "conditional receipt Clear"
    expected := "rejected unconditional receipt-history reset" },
  { name := "tracer-reset-block-gas"
    target := "replace block-gas history Clear with another operation"
    expected := "rejected block-gas-history reset" },
  { name := "tracer-reset-block-gas-conditional"
    target := "conditional block-gas history Clear"
    expected := "rejected unconditional block-gas-history reset" },
  { name := "tracer-reset-receipt-gas"
    target := "StartNewBlockTrace sets cumulativeReceiptGas := 1"
    expected := "rejected cumulative receipt-gas reset" },
  { name := "tracer-success-conditional"
    target := "conditional MarkAsSuccess receipt append"
    expected := "rejected unconditional receipt append" },
  { name := "tracer-success-status"
    target := "MarkAsSuccess appends StatusCode.Failure"
    expected := "rejected successful receipt projection" },
  { name := "tracer-success-forward-order"
    target := "current-tracer forwarding before nested-tracer forwarding"
    expected := "rejected receipt callback ordering" },
  { name := "tracer-forward-none"
    target := "nestedTracer := false; currentTxTracerIsTracingReceipt := false"
    expected := "gasMutation, receiptAppend" },
  { name := "tracer-forward-nested-only"
    target := "nestedTracer := true; currentTxTracerIsTracingReceipt := false"
    expected := "gasMutation, receiptAppend, nestedTracerForward" },
  { name := "tracer-forward-current-only"
    target := "nestedTracer := false; currentTxTracerIsTracingReceipt := true"
    expected := "gasMutation, receiptAppend, currentTracerForward" },
  { name := "tracer-forward-both"
    target := "nestedTracer := true; currentTxTracerIsTracingReceipt := true"
    expected := "gasMutation, receiptAppend, nestedTracerForward, currentTracerForward" },
  { name := "tracer-gas-update-conditional"
    target := "conditional UpdateCumulativeGasTracking"
    expected := "rejected cumulative-gas chain" },
  { name := "tracer-receipt-index-value"
    target := "BuildReceipt Index := currentIndex + 1"
    expected := "rejected current-index receipt projection" },
  { name := "tracer-end-order"
    target := "increment currentIndex before wrapped EndTxTrace"
    expected := "rejected callback-before-index ordering" },
  { name := "tracer-end-index-conditional"
    target := "conditional currentIndex increment"
    expected := "rejected unconditional index chaining" },
  { name := "bal-enabled-spec"
    target := "derive BAL capability from a constant"
    expected := "rejected release-spec BAL derivation" },
  { name := "bal-enabled-guard"
    target := "invert BAL-disabled guard"
    expected := "rejected direct-inner guard" },
  { name := "bal-inner-argument"
    target := "replace the direct-inner tracer argument"
    expected := "rejected typed direct-inner handoff" },
  { name := "parallel-selection"
    target := "invert the parallel-selection predicate"
    expected := "rejected excluded parallel route" },
  { name := "di-manager-route"
    target := "change BlockAccessListManager lifetime registration"
    expected := "rejected exact BAL manager registration" },
  { name := "di-order"
    target := "decorate before registering the direct executor"
    expected := "rejected DI registration ordering" },
  { name := "di-validation-module"
    target := "change the StandardBlockValidationModule registration lifetime"
    expected := "rejected standard validation DI route" },
  { name := "fast-path-code-overridable"
    target := "remove or invert !_isCodeOverridable"
    expected := "rejected exact simple-transfer eligibility" },
  { name := "fast-path-authorization-list"
    target := "allow a non-null authorization list"
    expected := "rejected exact simple-transfer eligibility" },
  { name := "fast-path-force-disabled"
    target := "ignore ForceSimpleTransferDisabled"
    expected := "rejected exact simple-transfer eligibility" },
  { name := "fast-path-entry-guard"
    target := "change recipient-null or candidate rejection guard"
    expected := "rejected exact fast-path preparation entry" },
  { name := "fast-path-delegation"
    target := "allow a delegated executable recipient"
    expected := "rejected no-executable-code predicate" },
  { name := "fast-path-no-code-call"
    target := "discard preloaded delegation at no-code call"
    expected := "rejected exact classification handoff" },
  { name := "simple-recipient-dead-check"
    target := "replace dead-account classification before state charge"
    expected := "rejected recipient state-charge input" },
  { name := "exact-commit-adapter"
    target := "Execute adapter delegates to CallAndRestore"
    expected := "rejected exact-Commit adapter chain" },
  { name := "exact-commit-option"
    target := "ITransactionProcessor Execute selects CommitAndRestore"
    expected := "rejected exact ExecutionOptions.Commit" },
  { name := "adapter-execute-tracer"
    target := "ProcessTransaction passes the per-entry tracer instead of the base receipts tracer"
    expected := "rejected exact adapter terminal route" },
  { name := "executor-adapter-tracer"
    target := "direct executor substitutes a fresh receipt tracer at the adapter call"
    expected := "rejected exact direct-executor adapter route" },
  { name := "di-adapter-factory"
    target := "change execute-adapter factory registration"
    expected := "rejected exact adapter DI route" },
  { name := "di-adapter-route"
    target := "bypass TransactionProcessorAdapterFactory"
    expected := "rejected exact adapter DI route" },
  { name := "di-adapter-construction"
    target := "factory constructs BuildUpTransactionProcessorAdapter"
    expected := "rejected exact execute-adapter construction" },
  { name := "block-executor-capture"
    target := "BlockProcessor changes the injected executor field initializer"
    expected := "rejected exact decorated-executor capture" },
  { name := "decorator-inner-parameter"
    target := "parallel decorator requires a concrete inner executor instead of the interface"
    expected := "rejected exact decorator constructor route" },
  { name := "decorator-bal-parameter"
    target := "parallel decorator requires a concrete BAL manager instead of the interface"
    expected := "rejected exact decorator constructor route" },
  { name := "direct-adapter-parameter"
    target := "direct executor requires a concrete adapter instead of ITransactionProcessorAdapter"
    expected := "rejected exact direct-executor constructor route" },
  { name := "execute-processor-parameter"
    target := "execute adapter requires concrete EthereumTransactionProcessor"
    expected := "rejected exact execute-adapter constructor route" },
  { name := "block-context-route"
    target := "ProcessBlock forwards a default block context"
    expected := "rejected exact block-context route" },
  { name := "execute-adapter-context"
    target := "execute adapter forwards only a header"
    expected := "rejected exact block-context forwarding" },
  { name := "direct-executor-context"
    target := "direct executor forwards only a header"
    expected := "rejected exact block-context forwarding" },
  { name := "decorator-context-bal"
    target := "remove BAL-manager block-context installation"
    expected := "rejected decorator context closure" },
  { name := "decorator-context-inner"
    target := "remove inner-executor block-context forwarding"
    expected := "rejected decorator context closure" },
  { name := "decorator-context-order"
    target := "inner context forwarding before BAL manager context"
    expected := "rejected context propagation order" },
  { name := "bal-context-store"
    target := "BAL manager stores a default context"
    expected := "rejected exact context assignment" },
  { name := "nested-forward-guard"
    target := "change nested receipt-forwarding predicate"
    expected := "rejected typed forwarding observation" },
  { name := "current-forward-guard"
    target := "change current receipt-forwarding predicate"
    expected := "rejected typed forwarding observation" },
  { name := "simple-value-order"
    target := "write recipient balance before PayValue"
    expected := "rejected simple-transfer state order" },
  { name := "simple-access-order"
    target := "update header gas before access reporting"
    expected := "rejected simple-transfer tail order" },
  { name := "invalid-result-noop"
    target := "replace invalid-result throw with normal return"
    expected := "rejected invalid-result guard" }
]

end OrdinaryTransactionMachineExtractor.Vectors
