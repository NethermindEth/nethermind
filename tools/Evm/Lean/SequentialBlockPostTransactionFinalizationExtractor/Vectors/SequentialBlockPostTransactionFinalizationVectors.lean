-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import SequentialBlockPostTransactionFinalizationExtractor.Generated.SequentialBlockPostTransactionFinalization
import SequentialBlockPostTransactionFinalizationExtractor.Refinement.SequentialBlockPostTransactionFinalization

namespace SequentialBlockPostTransactionFinalizationExtractor.Vectors.SequentialBlockPostTransactionFinalizationVectors

namespace G
export SequentialBlockPostTransactionFinalizationExtractor.Generated.SequentialBlockPostTransactionFinalization
  (Receipt Header Block ReleaseSpec WorldState Tracer StandardHandler CompletedTransactionFold
    HookObservations HookNormalReturns FinalizationInput FinalizationEvent FinalizationResult FinalizationOutcome run
    completed receiptCount logCount)
end G

def baseHooks : G.HookObservations :=
  { blobGas := 484
    bloom := 1001
    receiptsRoot := 1002
    rewards := 1003
    withdrawals := 1004
    executionRequests := 1005
    endBlockTrace := 1006
    accountChanges := 1007
    stateRoot := 1008
    balFinalized := 1009
    headerHash := 1010 }

def normalHookReturns : G.HookNormalReturns :=
  { rewards := true
    withdrawals := true
    executionRequests := true
    calculateBlooms := true
    calculateReceiptsRoot := true
    endBlockTrace := true
    accountChanges := true
    stateRoot := true
    balFinalization := true
    headerHash := true }

def hookNormalReturnCoverage : List Bool :=
  [normalHookReturns.rewards, normalHookReturns.withdrawals,
   normalHookReturns.executionRequests, normalHookReturns.calculateBlooms,
   normalHookReturns.calculateReceiptsRoot, normalHookReturns.endBlockTrace,
   normalHookReturns.accountChanges, normalHookReturns.stateRoot,
   normalHookReturns.balFinalization, normalHookReturns.headerHash]

theorem hook_normal_return_observations : hookNormalReturnCoverage = List.replicate 10 true := by
  decide

def receipts (count logsPerReceipt : Nat) : List G.Receipt :=
  List.replicate count { id := 1, logCount := logsPerReceipt }

def totalLogs (items : List G.Receipt) : Nat :=
  items.foldl (fun total receipt => total + receipt.logCount) 0

def backgroundBySourceThreshold (items : List G.Receipt) : Bool :=
  if items.length >= 16 then true else if totalLogs items >= 64 then true else false

def foldFor (items : List G.Receipt) (logs : Nat) : G.CompletedTransactionFold :=
  { completed := true
    receiptCount := items.length
    logCount := logs
    terminalReceiptProjection := items
    terminalLogProjection := items.map (fun receipt => receipt.logCount)
    terminalResults := List.replicate items.length .ok
    completedIndices := List.range items.length }

def baseInput : G.FinalizationInput :=
  { block := { id := 7 }
    header :=
      { id := 8
        blobGasUsed := none
        bloom := some 77
        receiptsRoot := none
        stateRoot := none
        hash := none }
    receipts := []
    spec := { id := 9, eip4844Enabled := false }
    world := { id := 10 }
    tracer := { id := 11 }
    standardHandler := { id := 12 }
    fold := foldFor [] 0
    hooks := baseHooks
    hookNormalReturns := normalHookReturns
    standardExactBase := true
    balEnabled := false
    normalReturn := true
    transactionsExecutedNormalReturn := true
    postTransactionCommitNormalReturn := true
    backgroundReceipts := false
    mainProcessingThread := true
    shouldComputeStateRoot := true }

def withReceipts (items : List G.Receipt) (logs : Nat) : G.FinalizationInput :=
  { baseInput with receipts := items, fold := foldFor items logs }

def emptyBlock : G.FinalizationInput := withReceipts [] 0

def nonemptyBlock : G.FinalizationInput := withReceipts (receipts 1 1) 1

def receipts15 : List G.Receipt := receipts 15 0

def receipts16 : List G.Receipt := receipts 16 0

def vector15 : G.FinalizationInput := withReceipts receipts15 0

def vector16 : G.FinalizationInput :=
  { baseInput with
    receipts := receipts16
    fold := foldFor receipts16 0
    backgroundReceipts := backgroundBySourceThreshold receipts16 }

def receipts63 : List G.Receipt :=
  List.replicate 14 { id := 1, logCount := 4 } ++ [{ id := 2, logCount := 7 }]

def receipts64 : List G.Receipt :=
  List.replicate 14 { id := 1, logCount := 4 } ++ [{ id := 2, logCount := 8 }]

def vector63Logs : G.FinalizationInput := withReceipts receipts63 63

def vector64Logs : G.FinalizationInput :=
  { baseInput with
    receipts := receipts64
    fold := foldFor receipts64 64
    backgroundReceipts := backgroundBySourceThreshold receipts64 }

def thresholdBoundaryChecks : List Bool :=
  [decide (backgroundBySourceThreshold receipts15 = false),
   decide (backgroundBySourceThreshold receipts16 = true),
   decide (backgroundBySourceThreshold receipts63 = false),
   decide (backgroundBySourceThreshold receipts64 = true)]

theorem threshold_boundaries : thresholdBoundaryChecks = [true, true, true, true] := by
  decide

def eip4844Off : G.FinalizationInput := vector15

def eip4844On : G.FinalizationInput :=
  { vector15 with spec := { vector15.spec with eip4844Enabled := true } }

def mainThreadOn : G.FinalizationInput :=
  { vector15 with mainProcessingThread := true }

def mainThreadOff : G.FinalizationInput :=
  { vector15 with mainProcessingThread := false }

def stateRootOn : G.FinalizationInput :=
  { vector15 with shouldComputeStateRoot := true }

def stateRootOff : G.FinalizationInput :=
  { vector15 with shouldComputeStateRoot := false }

def hookThrowScope : G.FinalizationInput :=
  { vector15 with
    normalReturn := false
    hookNormalReturns := { normalHookReturns with rewards := false } }

def callbackThrowScope : G.FinalizationInput :=
  { vector15 with transactionsExecutedNormalReturn := false }

def postTransactionCommitThrowScope : G.FinalizationInput :=
  { vector15 with postTransactionCommitNormalReturn := false }

def normal15 : G.FinalizationOutcome := G.run vector15

def backgroundAt16 : G.FinalizationOutcome := G.run vector16

def normal63Logs : G.FinalizationOutcome := G.run vector63Logs

def backgroundAt64Logs : G.FinalizationOutcome := G.run vector64Logs

def normalEip4844On : G.FinalizationOutcome := G.run eip4844On

def normalEip4844Off : G.FinalizationOutcome := G.run eip4844Off

def normalMainThreadOff : G.FinalizationOutcome := G.run mainThreadOff

def normalStateRootOff : G.FinalizationOutcome := G.run stateRootOff

def unsupportedHookThrow : G.FinalizationOutcome := G.run hookThrowScope

def sameCountDifferentReceipts : List G.Receipt := [{ id := 99, logCount := 0 }]

def sameCountDifferentWitness : G.CompletedTransactionFold := foldFor [{ id := 1, logCount := 0 }] 0

def sameCountDifferentReceiptMutation : Bool :=
  decide (sameCountDifferentReceipts.length = sameCountDifferentWitness.terminalReceiptProjection.length ∧
    sameCountDifferentReceipts ≠ sameCountDifferentWitness.terminalReceiptProjection)

theorem same_count_different_receipts_is_not_extensional : sameCountDifferentReceiptMutation = true := by
  decide

def emptyBloomResult : Option Nat :=
  match G.run emptyBlock with
  | .unsupported _ => none
  | .completed result => result.header.bloom

def nonemptyBloomResult : Option Nat :=
  match G.run nonemptyBlock with
  | .unsupported _ => none
  | .completed result => result.header.bloom

theorem synchronous_bloom_header_guard :
    emptyBloomResult = some 77 ∧ nonemptyBloomResult = some 77 := by
  decide

def boundaryCoverage : List Bool :=
  [G.completed emptyBlock, G.completed nonemptyBlock, G.completed vector15,
   G.completed vector16, G.completed vector63Logs, G.completed vector64Logs,
   G.completed eip4844On, G.completed eip4844Off, G.completed mainThreadOn,
    G.completed mainThreadOff, G.completed stateRootOn, G.completed stateRootOff,
    !G.completed hookThrowScope, !G.completed callbackThrowScope,
    !G.completed postTransactionCommitThrowScope]

def eventSequence (result : G.FinalizationResult) : List G.FinalizationEvent := result.events

def expectedNormalTail : List G.FinalizationEvent :=
  [.commitNoRoots 0, .receiptTaskInitializedNull, .bloomsCalculated,
   .receiptsRootAssigned, .rewardsApplied, .withdrawalsApplied,
   .commitNoRoots 1, .executionRequestsProcessed, .endBlockTrace true,
   .commitRoots, .accountChangesCaptured, .stateRootComputed, .balFinalized,
   .headerHashAssigned, .returnedReceipts]

def eventsOf (outcome : G.FinalizationOutcome) : List G.FinalizationEvent :=
  match outcome with
  | .unsupported _ => []
  | .completed result => result.events

def normal15Events : List G.FinalizationEvent := eventsOf normal15

theorem normal_event_tail : normal15Events = expectedNormalTail := by
  decide

def blobGasUsed (outcome : G.FinalizationOutcome) : Option Nat :=
  match outcome with
  | .unsupported _ => none
  | .completed result => result.header.blobGasUsed

theorem eip4844_guard_vectors :
    blobGasUsed normalEip4844On = some 484 ∧ blobGasUsed normalEip4844Off = none := by
  decide

def hasAccountChanges (events : List G.FinalizationEvent) : Bool :=
  events.any (fun event =>
    match event with
    | .accountChangesCaptured => true
    | _ => false)

def hasStateRoot (events : List G.FinalizationEvent) : Bool :=
  events.any (fun event =>
    match event with
    | .stateRootComputed => true
    | _ => false)

theorem thread_and_state_root_guard_vectors :
    hasAccountChanges (eventsOf (G.run mainThreadOn)) = true ∧
      hasAccountChanges (eventsOf (G.run mainThreadOff)) = false ∧
      hasStateRoot (eventsOf (G.run stateRootOn)) = true ∧
      hasStateRoot (eventsOf (G.run stateRootOff)) = false := by
  decide

theorem unsupported_guard_vectors :
    backgroundAt16 = .unsupported .backgroundReceipts ∧
      backgroundAt64Logs = .unsupported .backgroundReceipts ∧
      unsupportedHookThrow = .unsupported .nonNormalReturn ∧
      G.run callbackThrowScope = .unsupported .nonNormalReturn ∧
      G.run postTransactionCommitThrowScope = .unsupported .nonNormalReturn := by
  decide

namespace R
export SequentialBlockPostTransactionFinalizationExtractor.Refinement.SequentialBlockPostTransactionFinalization
  (SourceEntryAdapter IdentityAdapter mapInput)
end R

namespace S
export SequentialBlockPostTransactionFinalizationExtractor.Specification.SequentialBlockPostTransactionFinalization
  (HeaderEffect expectedHeaderEffects)
end S

def sourceAdapterEffects : List S.HeaderEffect :=
  S.expectedHeaderEffects (R.mapInput vector15)

def sourceAdapter : R.SourceEntryAdapter vector15 :=
  { spec := R.mapInput vector15
    identity :=
      { blockIdentity := rfl
        headerIdentity := rfl
        receiptsIdentity := rfl
        releaseSpecIdentity := rfl
        worldIdentity := rfl
        tracerIdentity := rfl
        standardHandlerIdentity := rfl }
    modeledEffects := sourceAdapterEffects
    noAdditionalModeledEffects := rfl }

def wrongSourceEffects : List S.HeaderEffect :=
  sourceAdapterEffects ++ [.hash 999]

def wrongSourceEffectsDiffer : Bool :=
  decide (wrongSourceEffects ≠ sourceAdapterEffects)

theorem source_adapter_effects_are_closed :
    sourceAdapter.modeledEffects = S.expectedHeaderEffects (R.mapInput vector15) := by
  exact sourceAdapter.noAdditionalModeledEffects

theorem wrong_source_effects_are_rejected : wrongSourceEffectsDiffer = true := by
  decide

namespace Bridge
open SequentialBlockPostTransactionFinalizationExtractor.Refinement.SequentialBlockPostTransactionFinalization

def suppliedEmptyFold : Fold.FoldResult :=
  { outcome := .completed
    state :=
      { receipts := []
        gasHistory := []
        cumulativeReceiptGas := 0
        headerGasUsed := 0
        parallel := false
        currentIndex := 0 }
    terminalResults := []
    completedIndices := []
    events := [.transactionFoldCompleted, .transactionsExecuted, .postTransactionCommit]
    failure := none
    committed := true }

def emptyAdapter : SourceEntryAdapter emptyBlock :=
  { spec := mapInput emptyBlock
    identity := ⟨rfl, rfl, rfl, rfl, rfl, rfl, rfl⟩
    modeledEffects := Spec.expectedHeaderEffects (mapInput emptyBlock)
    noAdditionalModeledEffects := rfl }

theorem emptyBridge : CompletedFoldBridge suppliedEmptyFold emptyBlock := by
  constructor <;> (first | rfl | decide | (intro _; trivial))

theorem empty_source_attached_refinement :
    SourceAttachedRefinement emptyBlock suppliedEmptyFold emptyAdapter emptyBridge := by
  exact generated_refines_spec emptyBlock emptyAdapter suppliedEmptyFold emptyBridge
    (by unfold normalBridgeObligation; decide)

theorem complete_bridge_executes_the_refinement :
    observeOutcome emptyBlock = sourceAttachedReferenceResult emptyBlock suppliedEmptyFold emptyAdapter :=
  empty_source_attached_refinement.generatedToReference

end Bridge

end SequentialBlockPostTransactionFinalizationExtractor.Vectors.SequentialBlockPostTransactionFinalizationVectors
