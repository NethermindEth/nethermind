-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import SequentialBlockPostTransactionFinalizationExtractor.Generated.SequentialBlockPostTransactionFinalization
import SequentialBlockPostTransactionFinalizationExtractor.Specification.SequentialBlockPostTransactionFinalization
import SequentialBlockTransactionFoldExtractor.Generated.SequentialBlockTransactionFold

namespace SequentialBlockPostTransactionFinalizationExtractor.Refinement.SequentialBlockPostTransactionFinalization

namespace Generated
export SequentialBlockPostTransactionFinalizationExtractor.Generated.SequentialBlockPostTransactionFinalization
  (FailureReason OutcomeKind Receipt TerminalResultWitness Header Block ReleaseSpec WorldState Tracer StandardHandler
    CompletedTransactionFold HookObservations HookNormalReturns FinalizationInput FinalizationEvent FinalizationResult
    FinalizationOutcome allHookNormalReturns run completed receiptCount logCount)
end Generated

namespace Spec
export SequentialBlockPostTransactionFinalizationExtractor.Specification.SequentialBlockPostTransactionFinalization
  (FailureReason Receipt TerminalResultWitness Header Block ReleaseSpec WorldState Tracer StandardHandler
    CompletedTransactionFold HookObservations HookNormalReturns Input Event HeaderEffect Observation
    allHookNormalReturns expectedEvents expectedHeaderEffects applyHeaderEffect applyHeaderEffects normalTail
    unsupportedTail eventOrder receiptCount logCount)
end Spec

namespace Fold
abbrev FoldResult :=
  SequentialBlockTransactionFoldExtractor.Generated.SequentialBlockTransactionFold.FoldResult

abbrev FoldReceipt :=
  ReceiptTerminalFoldExtractor.Generated.ReceiptTerminalFoldKernel.Receipt

abbrev FoldTransactionResult :=
  ReceiptTerminalFoldExtractor.Generated.ReceiptTerminalFoldKernel.TransactionResult

def hasPostTransactionCommit (result : FoldResult) : Bool :=
  SequentialBlockTransactionFoldExtractor.Generated.SequentialBlockTransactionFold.hasPostTransactionCommit result

def projectReceipt (receipt : FoldReceipt) : Generated.Receipt :=
  { id := receipt.index, logCount := receipt.logs.length }

def projectReceipts (receipts : List FoldReceipt) : List Generated.Receipt :=
  receipts.map projectReceipt

def projectReceiptLogs (receipts : List FoldReceipt) : List Nat :=
  receipts.map (fun receipt => receipt.logs.length)

def projectInputReceiptLogs (receipts : List Generated.Receipt) : List Nat :=
  receipts.map (fun receipt => receipt.logCount)

def projectTerminalResult : FoldTransactionResult → Generated.TerminalResultWitness
| .ok => .ok
| .evmException exceptionType substateError =>
    .evmException exceptionType.id (substateError.map (fun error => error.id))

def projectTerminalResults (results : List FoldTransactionResult) : List Generated.TerminalResultWitness :=
  results.map projectTerminalResult

/- The upstream sequential fold's completed path admits only `.ok` terminal observations.
   The projection still retains the `evmException` constructor for conditional/non-completed
   fold witnesses; this bridge does not infer success from receipt counts or result equality. -/
def upstreamCompletedTerminalResultsOk : List FoldTransactionResult → Prop
| [] => True
| .ok :: rest => upstreamCompletedTerminalResultsOk rest
| .evmException _ _ :: _ => False

def logCount (result : FoldResult) : Nat :=
  result.state.receipts.foldl (fun total receipt => total + receipt.logs.length) 0
end Fold

def mapEvent : Generated.FinalizationEvent → Spec.Event
| .commitNoRoots ordinal => .commitNoRoots ordinal
| .blobGasAssigned => .blobGasAssigned
| .receiptTaskInitializedNull => .receiptTaskInitializedNull
| .bloomsCalculated => .bloomsCalculated
| .receiptsRootAssigned => .receiptsRootAssigned
| .rewardsApplied => .rewardsApplied
| .withdrawalsApplied => .withdrawalsApplied
| .executionRequestsProcessed => .executionRequestsProcessed
| .endBlockTrace accumulate => .endBlockTrace accumulate
| .commitRoots => .commitRoots
| .accountChangesCaptured => .accountChangesCaptured
| .stateRootComputed => .stateRootComputed
| .balFinalized => .balFinalized
| .headerHashAssigned => .headerHashAssigned
| .returnedReceipts => .returnedReceipts

def mapEvents (events : List Generated.FinalizationEvent) : List Spec.Event := events.map mapEvent

def mapBlock (block : Generated.Block) : Spec.Block :=
  { id := block.id }

def mapHeader (header : Generated.Header) : Spec.Header :=
  { id := header.id
    blobGasUsed := header.blobGasUsed
    bloom := header.bloom
    receiptsRoot := header.receiptsRoot
    stateRoot := header.stateRoot
    hash := header.hash }

def mapReceipts (receipts : List Generated.Receipt) : List Spec.Receipt :=
  receipts.map (fun receipt => { id := receipt.id, logCount := receipt.logCount })

def mapReleaseSpec (spec : Generated.ReleaseSpec) : Spec.ReleaseSpec :=
  { id := spec.id, eip4844Enabled := spec.eip4844Enabled }

def mapWorld (world : Generated.WorldState) : Spec.WorldState :=
  { id := world.id }

def mapTracer (tracer : Generated.Tracer) : Spec.Tracer :=
  { id := tracer.id }

def mapStandardHandler (handler : Generated.StandardHandler) : Spec.StandardHandler :=
  { id := handler.id }

def mapTerminalResult (result : Generated.TerminalResultWitness) : Spec.TerminalResultWitness :=
  match result with
  | .ok => .ok
  | .evmException exceptionType substateError => .evmException exceptionType substateError

def mapCompletedFold (fold : Generated.CompletedTransactionFold) : Spec.CompletedTransactionFold :=
  { completed := fold.completed
    receiptCount := fold.receiptCount
    logCount := fold.logCount
    terminalReceiptProjection := mapReceipts fold.terminalReceiptProjection
    terminalLogProjection := fold.terminalLogProjection
    terminalResults := fold.terminalResults.map mapTerminalResult
    completedIndices := fold.completedIndices }

/- A reference fold is built from the actual settled-fold result, rather than copied from the
   generated finalization input.  The bridge below proves that each projection agrees with the
   generated witness; this keeps the reference result independent of `Generated.run`. -/
def actualCompletedFold (fold : Fold.FoldResult) : Generated.CompletedTransactionFold :=
  { completed :=
      match fold.outcome with
      | .completed => true
      | .invalidPrefix => false
      | .unsupported => false
    receiptCount := (Fold.projectReceipts fold.state.receipts).length
    logCount :=
      (Fold.projectReceipts fold.state.receipts).foldl
        (fun total receipt => total + receipt.logCount) 0
    terminalReceiptProjection := Fold.projectReceipts fold.state.receipts
    terminalLogProjection := Fold.projectReceiptLogs fold.state.receipts
    terminalResults := Fold.projectTerminalResults fold.terminalResults
    completedIndices := fold.completedIndices }

def mapHooks (hooks : Generated.HookObservations) : Spec.HookObservations :=
  { blobGas := hooks.blobGas
    bloom := hooks.bloom
    receiptsRoot := hooks.receiptsRoot
    rewards := hooks.rewards
    withdrawals := hooks.withdrawals
    executionRequests := hooks.executionRequests
    endBlockTrace := hooks.endBlockTrace
    accountChanges := hooks.accountChanges
    stateRoot := hooks.stateRoot
    balFinalized := hooks.balFinalized
    headerHash := hooks.headerHash }

def mapHookNormalReturns (returns : Generated.HookNormalReturns) : Spec.HookNormalReturns :=
  { rewards := returns.rewards
    withdrawals := returns.withdrawals
    executionRequests := returns.executionRequests
    calculateBlooms := returns.calculateBlooms
    calculateReceiptsRoot := returns.calculateReceiptsRoot
    endBlockTrace := returns.endBlockTrace
    accountChanges := returns.accountChanges
    stateRoot := returns.stateRoot
    balFinalization := returns.balFinalization
    headerHash := returns.headerHash }

def mapFailure : Generated.FailureReason → Spec.FailureReason
| .nonStandardPath => .nonStandardPath
| .balEnabled => .balEnabled
| .backgroundReceipts => .backgroundReceipts
| .nonNormalReturn => .nonNormalReturn
| .incompleteTransactionFold => .incompleteTransactionFold

structure IdentityAdapter (generated : Generated.FinalizationInput) (spec : Spec.Input) : Prop where
  blockIdentity : spec.block = mapBlock generated.block
  headerIdentity : spec.header = mapHeader generated.header
  receiptsIdentity : spec.receipts = mapReceipts generated.receipts
  releaseSpecIdentity : spec.spec = mapReleaseSpec generated.spec
  worldIdentity : spec.world = mapWorld generated.world
  tracerIdentity : spec.tracer = mapTracer generated.tracer
  standardHandlerIdentity : spec.standardHandler = mapStandardHandler generated.standardHandler

/- The bridge carries extensional projections only. It never identifies a complete fold result
   with a finalization result, and every list projection is elementwise rather than cardinality-only. -/
structure CompletedFoldBridge (fold : Fold.FoldResult)
    (input : Generated.FinalizationInput) : Prop where
  completed : fold.outcome = .completed
  committed : fold.committed = true
  receiptProjection : Fold.projectReceipts fold.state.receipts = input.receipts
  receiptLogProjection : Fold.projectReceiptLogs fold.state.receipts = input.fold.terminalLogProjection
  terminalResultProjection : Fold.projectTerminalResults fold.terminalResults = input.fold.terminalResults
  completedIndexProjection : fold.completedIndices = input.fold.completedIndices
  terminalReceiptProjection : input.fold.terminalReceiptProjection = input.receipts
  terminalLogWitness : input.fold.terminalLogProjection = Fold.projectInputReceiptLogs input.receipts
  postTransactionCommit : Fold.hasPostTransactionCommit fold = true
  generatedWitnessCompleted : input.fold.completed = true
  generatedReceiptProjection : input.fold.receiptCount = input.receipts.length
  generatedLogProjection : input.fold.logCount =
    input.receipts.foldl (fun total receipt => total + receipt.logCount) 0
  upstreamOkRestriction : fold.outcome = .completed → Fold.upstreamCompletedTerminalResultsOk fold.terminalResults

def mapInput (input : Generated.FinalizationInput) : Spec.Input :=
  { block := mapBlock input.block
    header := mapHeader input.header
    receipts := mapReceipts input.receipts
    spec := mapReleaseSpec input.spec
    world := mapWorld input.world
    tracer := mapTracer input.tracer
    standardHandler := mapStandardHandler input.standardHandler
    fold := mapCompletedFold input.fold
    hooks := mapHooks input.hooks
    hookNormalReturns := mapHookNormalReturns input.hookNormalReturns
    standardExactBase := input.standardExactBase
    balEnabled := input.balEnabled
    normalReturn := input.normalReturn
    transactionsExecutedNormalReturn := input.transactionsExecutedNormalReturn
    postTransactionCommitNormalReturn := input.postTransactionCommitNormalReturn
    backgroundReceipts := input.backgroundReceipts
    mainProcessingThread := input.mainProcessingThread
    shouldComputeStateRoot := input.shouldComputeStateRoot }

/- The source entry owns the target-model input and the effect list admitted for opaque external
   helpers.  `noAdditionalModeledEffects` is an explicit, source-attached equality: it is consumed
   when the independent reference result is related to the generated observation. -/
structure SourceEntryAdapter (input : Generated.FinalizationInput) where
  spec : Spec.Input
  identity : IdentityAdapter input spec
  modeledEffects : List Spec.HeaderEffect
  noAdditionalModeledEffects :
    modeledEffects = Spec.expectedHeaderEffects (mapInput input)

def observeGenerated (input : Generated.FinalizationInput)
    (result : Generated.FinalizationResult) : Spec.Observation :=
  { completed := true
    failure := none
    block := mapBlock result.block
    headerBefore := mapHeader input.header
    headerAfter := mapHeader result.header
    receipts := mapReceipts result.receipts
    spec := mapReleaseSpec result.spec
    world := mapWorld result.world
    tracer := mapTracer result.tracer
    standardHandler := mapStandardHandler result.standardHandler
    foldWitness := mapCompletedFold result.foldWitness
    hooksWitness := mapHooks result.hooksWitness
    hookNormalReturnsWitness := mapHookNormalReturns result.hookNormalReturnsWitness
    transactionsExecutedNormalReturnWitness := result.transactionsExecutedNormalReturnWitness
    postTransactionCommitNormalReturnWitness := result.postTransactionCommitNormalReturnWitness
    postTransactionCommitWitness := result.postTransactionCommitWitness
    events := mapEvents result.events
    effects := Spec.expectedHeaderEffects (mapInput input) }

def observeUnsupported (input : Generated.FinalizationInput) (reason : Generated.FailureReason) : Spec.Observation :=
  { completed := false
    failure := some (mapFailure reason)
    block := mapBlock input.block
    headerBefore := mapHeader input.header
    headerAfter := mapHeader input.header
    receipts := mapReceipts input.receipts
    spec := mapReleaseSpec input.spec
    world := mapWorld input.world
    tracer := mapTracer input.tracer
    standardHandler := mapStandardHandler input.standardHandler
    foldWitness := mapCompletedFold input.fold
    hooksWitness := mapHooks input.hooks
    hookNormalReturnsWitness := mapHookNormalReturns input.hookNormalReturns
    transactionsExecutedNormalReturnWitness := input.transactionsExecutedNormalReturn
    postTransactionCommitNormalReturnWitness := input.postTransactionCommitNormalReturn
    postTransactionCommitWitness := false
    events := []
    effects := [] }

def observeOutcome (input : Generated.FinalizationInput) : Spec.Observation :=
  match Generated.run input with
  | .unsupported reason => observeUnsupported input reason
  | .completed result => observeGenerated input result

def refinementRelation (input : Generated.FinalizationInput)
    (generated : Generated.FinalizationOutcome) (observation : Spec.Observation) : Prop :=
  match generated with
  | .unsupported reason => Spec.unsupportedTail (mapInput input) (mapFailure reason) observation
  | .completed _ => Spec.normalTail (mapInput input) observation

def normalBridgeObligation (input : Generated.FinalizationInput) : Prop :=
  input.standardExactBase = true ∧
  input.balEnabled = false ∧
  input.normalReturn = true ∧
  input.transactionsExecutedNormalReturn = true ∧
  input.postTransactionCommitNormalReturn = true ∧
  input.backgroundReceipts = false ∧
  input.fold.completed = true ∧
  Generated.allHookNormalReturns input.hookNormalReturns = true

def sourceAttachedReferenceResult
    (input : Generated.FinalizationInput)
    (fold : Fold.FoldResult)
    (adapter : SourceEntryAdapter input) : Spec.Observation :=
  { completed := true
    failure := none
    block := adapter.spec.block
    headerBefore := adapter.spec.header
    headerAfter :=
      Spec.applyHeaderEffects adapter.spec.header adapter.modeledEffects
    receipts := mapReceipts (Fold.projectReceipts fold.state.receipts)
    spec := adapter.spec.spec
    world := adapter.spec.world
    tracer := adapter.spec.tracer
    standardHandler := adapter.spec.standardHandler
    foldWitness := mapCompletedFold (actualCompletedFold fold)
    hooksWitness := mapHooks input.hooks
    hookNormalReturnsWitness := mapHookNormalReturns input.hookNormalReturns
    transactionsExecutedNormalReturnWitness := input.transactionsExecutedNormalReturn
    postTransactionCommitNormalReturnWitness := input.postTransactionCommitNormalReturn
    postTransactionCommitWitness := Fold.hasPostTransactionCommit fold
    events := Spec.expectedEvents (mapInput input)
    effects := adapter.modeledEffects }

/- A single relation packages the generated outcome, an independently-defined reference
   observation, all actual fold projections, and the source-entry identity.  In particular, the
   result is not a conjunction whose first projection silently discards the adapter evidence. -/
structure SourceAttachedRefinement
    (input : Generated.FinalizationInput)
    (fold : Fold.FoldResult)
    (adapter : SourceEntryAdapter input)
    (_bridge : CompletedFoldBridge fold input) : Prop where
  generatedToReference :
    observeOutcome input = sourceAttachedReferenceResult input fold adapter
  referenceNormalTail :
    Spec.normalTail (mapInput input) (sourceAttachedReferenceResult input fold adapter)
  refinement :
    refinementRelation input (Generated.run input) (sourceAttachedReferenceResult input fold adapter)
  sourceIdentity : IdentityAdapter input adapter.spec
  noAdditionalModeledEffects :
    adapter.modeledEffects = Spec.expectedHeaderEffects (mapInput input)
  actualFoldProjection : actualCompletedFold fold = input.fold
  completedFold : CompletedFoldBridge fold input
  foldUpstreamOkRestriction : Fold.upstreamCompletedTerminalResultsOk fold.terminalResults

theorem actualFoldProjection_matches_input
    (input : Generated.FinalizationInput)
    (fold : Fold.FoldResult)
    (bridge : CompletedFoldBridge fold input) :
    actualCompletedFold fold = input.fold := by
  have hReceiptCount :
      (Fold.projectReceipts fold.state.receipts).length = input.fold.receiptCount := by
    calc
      (Fold.projectReceipts fold.state.receipts).length = input.receipts.length := by
        rw [bridge.receiptProjection]
      _ = input.fold.receiptCount := bridge.generatedReceiptProjection.symm
  have hLogCount :
      (Fold.projectReceipts fold.state.receipts).foldl
          (fun total receipt => total + receipt.logCount) 0 = input.fold.logCount := by
    calc
      (Fold.projectReceipts fold.state.receipts).foldl
          (fun total receipt => total + receipt.logCount) 0 =
          input.receipts.foldl (fun total receipt => total + receipt.logCount) 0 := by
            rw [bridge.receiptProjection]
      _ = input.fold.logCount := bridge.generatedLogProjection.symm
  have hTerminalReceipts :
      Fold.projectReceipts fold.state.receipts = input.fold.terminalReceiptProjection := by
    calc
      Fold.projectReceipts fold.state.receipts = input.receipts := bridge.receiptProjection
      _ = input.fold.terminalReceiptProjection := bridge.terminalReceiptProjection.symm
  have hCompleted :
      (match fold.outcome with
       | .completed => true
       | .invalidPrefix => false
       | .unsupported => false) = input.fold.completed := by
    rw [bridge.completed, bridge.generatedWitnessCompleted]
  have hTerminalLogs := bridge.receiptLogProjection
  have hTerminalResults := bridge.terminalResultProjection
  have hCompletedIndices := bridge.completedIndexProjection
  cases hFoldValue : input.fold with
  | mk completed receiptCount logCount terminalReceiptProjection terminalLogProjection
      terminalResults completedIndices =>
    simp_all [actualCompletedFold]

theorem generated_refines_spec (input : Generated.FinalizationInput)
    (adapter : SourceEntryAdapter input)
    (fold : Fold.FoldResult)
    (bridge : CompletedFoldBridge fold input)
    (hNormal : normalBridgeObligation input) :
    SourceAttachedRefinement input fold adapter bridge := by
  rcases hNormal with ⟨hStandard, hBal, hNormalReturn, hTransactionsExecuted,
    hPostTransactionCommit, hBackground, hFold, hHooks⟩
  let referenceOutput := sourceAttachedReferenceResult input fold adapter
  have hActualFold := actualFoldProjection_matches_input input fold bridge
  have hMappedHooks : Spec.allHookNormalReturns (mapHookNormalReturns input.hookNormalReturns) = true := by
    simpa only [Spec.allHookNormalReturns, mapHookNormalReturns,
      Generated.allHookNormalReturns] using hHooks
  have hReferenceOutput : referenceOutput =
      { completed := true
        failure := none
        block := mapBlock input.block
        headerBefore := mapHeader input.header
        headerAfter := Spec.applyHeaderEffects (mapHeader input.header)
          (Spec.expectedHeaderEffects (mapInput input))
        receipts := mapReceipts input.receipts
        spec := mapReleaseSpec input.spec
        world := mapWorld input.world
        tracer := mapTracer input.tracer
        standardHandler := mapStandardHandler input.standardHandler
        foldWitness := mapCompletedFold input.fold
        hooksWitness := mapHooks input.hooks
        hookNormalReturnsWitness := mapHookNormalReturns input.hookNormalReturns
        transactionsExecutedNormalReturnWitness := input.transactionsExecutedNormalReturn
        postTransactionCommitNormalReturnWitness := input.postTransactionCommitNormalReturn
        postTransactionCommitWitness := true
        events := Spec.expectedEvents (mapInput input)
        effects := Spec.expectedHeaderEffects (mapInput input) } := by
    simp only [referenceOutput, sourceAttachedReferenceResult,
      adapter.identity.blockIdentity, adapter.identity.headerIdentity,
      adapter.identity.releaseSpecIdentity, adapter.identity.worldIdentity,
      adapter.identity.tracerIdentity, adapter.identity.standardHandlerIdentity,
      adapter.noAdditionalModeledEffects, bridge.receiptProjection,
      bridge.postTransactionCommit, hActualFold]
  have hGeneratedObservation :
      observeOutcome input = referenceOutput := by
    rw [hReferenceOutput]
    cases hBlob : input.spec.eip4844Enabled <;>
      cases hThread : input.mainProcessingThread <;>
      cases hStateRoot : input.shouldComputeStateRoot <;>
      simp [observeOutcome,
      observeGenerated, Generated.run, Spec.expectedEvents, Spec.expectedHeaderEffects,
      Spec.applyHeaderEffects, Spec.applyHeaderEffect, mapInput, mapBlock, mapHeader,
      mapReceipts, mapReleaseSpec, mapWorld, mapTracer, mapStandardHandler,
      mapCompletedFold, mapHooks, mapHookNormalReturns, mapEvents, mapEvent,
      hStandard, hBal, hNormalReturn, hTransactionsExecuted, hPostTransactionCommit,
      hBackground, hFold, hHooks, hBlob, hThread, hStateRoot]
  have hReferenceNormalTail :
      Spec.normalTail (mapInput input) referenceOutput := by
    rw [hReferenceOutput]
    simp [Spec.normalTail, mapInput, mapCompletedFold, hStandard, hBal,
      hNormalReturn, hTransactionsExecuted, hPostTransactionCommit, hBackground,
      hFold, hMappedHooks]
  refine
    { generatedToReference := hGeneratedObservation
      referenceNormalTail := hReferenceNormalTail
      refinement := ?_
      sourceIdentity := adapter.identity
      noAdditionalModeledEffects := adapter.noAdditionalModeledEffects
      actualFoldProjection := hActualFold
      completedFold := bridge
      foldUpstreamOkRestriction := bridge.upstreamOkRestriction bridge.completed }
  simpa [refinementRelation, Generated.run, hStandard, hBal, hNormalReturn,
    hTransactionsExecuted, hPostTransactionCommit, hBackground, hFold, hHooks,
    referenceOutput] using hReferenceNormalTail

theorem mapEvent_preserves_end_trace (accumulate : Bool) :
    mapEvent (.endBlockTrace accumulate) = .endBlockTrace accumulate := by
  rfl

theorem bridge_respects_upstream_ok_restriction (input : Generated.FinalizationInput)
    (fold : Fold.FoldResult)
    (bridge : CompletedFoldBridge fold input) :
    Fold.upstreamCompletedTerminalResultsOk fold.terminalResults := by
  exact bridge.upstreamOkRestriction bridge.completed

end SequentialBlockPostTransactionFinalizationExtractor.Refinement.SequentialBlockPostTransactionFinalization
