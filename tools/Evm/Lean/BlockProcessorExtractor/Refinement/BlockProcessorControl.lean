-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import BlockProcessorExtractor.Generated.BlockProcessorControl
import Eip803x.BranchReference

namespace BlockProcessorExtractor.Refinement

open Eip803x.BlockReference

/-!
The generated phase program is interpreted using the accepted reference's
explicit external contracts. These theorems connect the extracted syntax
projection to that reference. They are not a C# execution, scope rollback,
virtual dispatch, background scheduling, or database correctness theorem.
-/

def phaseToReference : Generated.Phase → Phase
  | .daoTransition => .daoTransition
  | .beaconRootSystemCall => .beaconRootSystemCall
  | .historicalBlockhashStateChange => .historicalBlockhashStateChange
  | .userTransactionFold => .userTransactionFold
  | .blobGasReceiptRootAndBloom => .blobGasReceiptRootAndBloom
  | .rewards => .rewards
  | .withdrawals => .withdrawals
  | .executionRequestsAndSystemCalls => .executionRequestsAndSystemCalls
  | .storageAndStateRoots => .storageAndStateRoots
  | .blockAccessList => .blockAccessList
  | .processedHeaderValidation => .processedHeaderValidation

def receiptEventToReference : Generated.ReceiptEvent → BlockControlEvent
  | .receiptBloomsComputed => .receiptBloomsComputed
  | .receiptsRootComputed => .receiptsRootComputed
  | .receiptsRootInstalled => .receiptsRootInstalled

def finalizationToReference : Generated.FinalizationStep → BranchReference.FinalizationStep
  | .totalDifficulty => .totalDifficulty
  | .mainChainUpdate => .mainChainUpdate
  | .markProcessed => .markProcessed

def phaseMeaning (spec : BlockSpec) (input : BlockInput) (phase : Phase) :
    BlockWorld → Except Failure BlockWorld :=
  match phase with
  | .daoTransition => checkedStateEffect input phase spec.applyDao
  | .beaconRootSystemCall => beaconRootAndBlockSetup spec input
  | .historicalBlockhashStateChange => checkedStateEffect input phase
      (fun state => spec.commitPreSystemState (spec.applyBlockhashState state))
  | .userTransactionFold => userTransactionFold spec input
  | .blobGasReceiptRootAndBloom => blobAndReceiptObservations spec input
  | .rewards => checkedStateEffect input phase spec.applyRewards
  | .withdrawals => withdrawals spec input
  | .executionRequestsAndSystemCalls => executionRequests spec input
  | .storageAndStateRoots => storageAndRoots spec input
  | .blockAccessList => blockAccessList spec input
  | .processedHeaderValidation => processedHeader spec input
  | _ => fun _ => .error (.phaseRejected phase)

def extractedActions (spec : BlockSpec) (input : BlockInput) :
    List (Phase × (BlockWorld → Except Failure BlockWorld)) :=
  Generated.processPhases.map fun sourcePhase =>
    let phase := phaseToReference sourcePhase
    (phase, phaseAction input phase (phaseMeaning spec input phase))

def runExtracted (spec : BlockSpec) (input : BlockInput) (world : BlockWorld) : BlockRun :=
  let raw := finish (runPhases (extractedActions spec input)
    (.running { world := world, trace := [] }))
  let restored := restoreProcessOneFailure world raw
  { restored with finallyAwait := observeBackgroundAwait input raw }

theorem extracted_phase_order : Generated.processPhases.map phaseToReference = Phase.processOne := by
  rfl

theorem extracted_actions_refine_reference (spec : BlockSpec) (input : BlockInput) :
    extractedActions spec input = processOneActions spec input := by
  rfl

theorem extracted_program_refines_reference (spec : BlockSpec) (input : BlockInput)
    (world : BlockWorld) :
    runExtracted spec input world = runProcessOneFromWorld spec input world := by
  rfl

theorem accepted_extracted_program_has_source_phase_order
    (spec : BlockSpec) (input : BlockInput) (world : BlockWorld)
    (h : (runExtracted spec input world).outcome = .accepted) :
    (runExtracted spec input world).trace = Generated.processPhases.map phaseToReference := by
  exact processOne_accepted_has_process_trace spec input world h

/-- Rollback here is the reference scope contract, not a derived Dispose implementation. -/
theorem rejected_extracted_program_restores_reference_scope
    (spec : BlockSpec) (input : BlockInput) (world : BlockWorld)
    (h : ∃ failure, (runExtracted spec input world).outcome = .rejected failure) :
    (runExtracted spec input world).world = world := by
  exact processOne_stopped_preserves_scope spec input world
    (runExtracted spec input world) rfl h

theorem source_receipt_events_match_reference_steps :
    Generated.synchronousReceiptEvents.map receiptEventToReference = receiptControlEventsFor
      [ .computedReceiptBlooms, .computedReceiptsRoot, .installedReceiptsRoot ] := by
  rfl

theorem source_commit_follows_processing_inclusion_and_prewarm :
    Generated.perBlockEvents =
      ["processOne", "inclusionList", "waitPrewarm"] ++ ["commitTree", "reset"] := by
  rfl

def branchEventToPhase : String → Option Phase
  | "commitTree" => some .commitTree
  | _ => none

theorem singleton_reference_commit_has_source_position
    (spec : BranchReference.BranchSpec) (policy : BranchReference.RetryPolicy)
    (index total : Nat) (input : BlockInput) (accumulator : BranchReference.BranchAccumulator)
    (blockRun : BlockRun) (inclusionSignal : Bool)
    (hAttempt : (BranchReference.runAttempt spec policy index
      { input with initialState := accumulator.currentState }
      (initialWorld { input with initialState := accumulator.currentState })).run = some blockRun)
    (hAccepted : blockRun.outcome = .accepted)
    (hInclusion : spec.checkInclusionList index
      { input with initialState := accumulator.currentState } blockRun.world = .completed inclusionSignal)
    (hPrewarm : spec.prewarmSucceeded index
      { input with initialState := accumulator.currentState } blockRun.world = .completed ())
    (hCommit : spec.commitTree index
      { input with initialState := accumulator.currentState } blockRun.world = .completed ()) :
    let run := BranchReference.processBlocks spec policy index total [input] accumulator
    run.trace = accumulator.trace ++ blockRun.trace ++
        Generated.perBlockEvents.filterMap branchEventToPhase ∧
      run.commitAttempts = accumulator.commitAttempts + 1 ∧
      run.commitCompletions = accumulator.commitCompletions + 1 := by
  simp [BranchReference.processBlocks, BranchReference.finishBranch,
    hAttempt, hAccepted, hInclusion, hPrewarm, hCommit,
    Generated.perBlockEvents, branchEventToPhase, List.append_assoc]

/-- Natural-number interpretation of the C# loop requires a valid loop index. -/
theorem source_checkpoint_matches_reference (index total : Nat) (h : index < total) :
    Generated.checkpointIndex index total = BranchReference.isCommitPoint index total := by
  apply Bool.eq_iff_iff.mpr
  simp [Generated.checkpointIndex, BranchReference.isCommitPoint]
  omega

theorem source_checkpoint_boundaries :
    [Generated.checkpointIndex 0 130, Generated.checkpointIndex 63 130,
     Generated.checkpointIndex 64 130, Generated.checkpointIndex 65 130,
     Generated.checkpointIndex 128 130, Generated.checkpointIndex 129 130] =
    [false, false, true, false, true, false] := by
  decide

theorem source_finalization_order :
    Generated.finalizationSteps.map finalizationToReference =
      [.totalDifficulty, .mainChainUpdate, .markProcessed] := by
  rfl

theorem successful_reference_finalization_has_source_order
    (spec : BranchReference.BranchSpec) (run : BranchReference.BranchRun)
    (difficulty head : Nat) (updated marked : Bool)
    (hRun : run.outcome = .accepted)
    (hDifficulty : spec.setTotalDifficulty run.successfulBlocks run.committedStates =
      .completed difficulty)
    (hHead : spec.updateMainChain difficulty run.successfulBlocks run.committedStates =
      .completed (head, updated))
    (hMark : spec.markProcessed run.successfulBlocks = .completed marked) :
    ∃ observation,
      (BranchReference.finalizeAcceptedBranch spec (BranchReference.disposeBranch run)).finalization =
        some observation ∧
      observation.completedSteps = Generated.finalizationSteps.map finalizationToReference ∧
      (BranchReference.finalizeAcceptedBranch spec (BranchReference.disposeBranch run)).scopeEvents =
        run.scopeEvents ++ [.disposed] := by
  have h := BranchReference.successful_finalization_follows_scope_disposal
    spec run difficulty head updated marked hRun hDifficulty hHead hMark
  exact ⟨_, h.2.1, rfl, h.2.2⟩

theorem source_cleanup_boundaries :
    Generated.cleanupEvents =
      ["disposeAccountChanges", "disposeAccountChanges", "disposeScopeForRetry",
       "reopenScopeForRetry", "disposeScopeAtCheckpoint", "disposeScopeFinally"] := by
  rfl

/-- These are source assignment strings; no equivalence of object graphs is inferred. -/
theorem source_artifact_projection :
    Generated.publishedArtifacts =
      [("suggestedBlock.AccountChanges", "processedBlock.AccountChanges"),
       ("suggestedBlock.ExecutionRequests", "processedBlock.ExecutionRequests"),
       ("suggestedBlock.GeneratedBlockAccessList", "processedBlock.GeneratedBlockAccessList"),
       ("suggestedBlock.EncodedBlockAccessList",
        "processedBlock.EncodedBlockAccessList??suggestedBlock.EncodedBlockAccessList")] := by
  rfl

theorem source_copies_request_and_bal_hashes :
    ("dst.RequestsHash", "RequestsHash") ∈ Generated.copiedHeaderFields ∧
    ("dst.BlockAccessListHash", "BlockAccessListHash") ∈ Generated.copiedHeaderFields := by
  decide

end BlockProcessorExtractor.Refinement
