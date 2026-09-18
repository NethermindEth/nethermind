-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import BlockProcessorExtractor.Refinement.BranchAcceptedIteration

namespace BlockProcessorExtractor.Vectors.BranchAcceptedIterationVectors

open BlockProcessorExtractor.Generated.BranchAcceptedIteration
namespace R
export BlockProcessorExtractor.Refinement.BranchAcceptedIteration
  (SourceWitness NormalAdapter SourceAttachedNormal generated_normal_refines_spec)
end R
namespace P
export SequentialBlockPostTransactionFinalizationExtractor.Generated.ProcessOneValidatedPublication
  (PublicationInput sourceClosureSha256 sourceSites)
end P
namespace PR
export SequentialBlockPostTransactionFinalizationExtractor.Refinement.ProcessOneValidatedPublication
  (NormalReturnAdapter)
end PR

def publication (skip : Bool) : P.PublicationInput :=
  { suggestedBlock := 1, processBlock := { normalReturn := true, processedBlock := 2, receipts := 3 },
    suggestedArtifacts := {
      accountChanges := some 11, executionRequests := some 12,
      generatedBlockAccessList := some 13, encodedBlockAccessList := some 14 },
    processedArtifacts := {
      accountChanges := some 21, executionRequests := some 22,
      generatedBlockAccessList := some 23, encodedBlockAccessList := none },
    exactBase := true, standardSequential := true, balEnabled := false, parallelExecutionEnabled := false,
    noValidation := skip, storeReceipts := true,
    validator := {
      ran := !skip, accepted := true, normalReturn := true,
      proposedGeneratedBlockAccessListAfter := some 13 },
    rejectionCleanupNormalReturn := true, postValidationNormalReturn := true,
    insertDeferredNormalReturn := true, noAdditionalBoundaryEffects := true }

def source : R.SourceWitness :=
  { closureSha256 := sourceClosureSha256, semanticIrSha256 := semanticIrSha256,
    publicationSource := { closureSha256 := P.sourceClosureSha256, sites := P.sourceSites } }

theorem publicationAdapter (skip : Bool) : PR.NormalReturnAdapter source.publicationSource (publication skip) := by
  refine {
    sourceAdapter := {
      closureIdentity := rfl, siteIdentities := rfl, exactBase := rfl, standardSequential := rfl,
      balDisabled := rfl, nonparallel := rfl, processBlockNormalReturn := rfl, noAdditionalEffects := rfl },
    validatorObservation := ?_, validatorNormalReturn := Or.inr rfl,
    acceptedOrSkipped := Or.inr rfl, postValidationNormalReturn := rfl,
    storeNormalReturn := Or.inr rfl }
  cases skip <;> decide

def base : Input :=
  { publication := publication false, route := .normalSequential, ownedScope := true,
    index := 0, prefixCount := 0, suggestedBlocks := [1], processedSlots := [none], originalList := 100,
    header := fun block => if block == 1 then 101 else 202, blockNumber := fun _ => 77,
    scope := 10, reopenedScope := 20, readOnly := false, hasTransactions := fun _ => true,
    preWarmerPresent := true, prewarmTask := some 30, cancellation := some 40,
    prefetch := some 50, inclusionSignal := true, normal := fun _ => true }

inductive Scenario where
  | terminalTrue | terminalFalse | skipped | readOnly | checkpoint64 | ordinary63
  | last64 | noPrewarm | noPrewarmer | noTransactions
  deriving DecidableEq, Repr

def vector : Scenario → Input
  | .terminalTrue => base
  | .terminalFalse => { base with inclusionSignal := false }
  | .skipped => { base with
      publication := publication true, inclusionSignal := false,
      normal := fun site => site != .inclusion }
  | .readOnly => { base with readOnly := true, normal := fun site => site != .blockEvent }
  | .checkpoint64 => { base with
      index := 64, prefixCount := 64,
      suggestedBlocks := List.replicate 66 1,
      processedSlots := List.replicate 64 (some 9) ++ [none, none] }
  | .ordinary63 => { base with
      index := 63, prefixCount := 63,
      suggestedBlocks := List.replicate 66 1,
      processedSlots := List.replicate 63 (some 9) ++ [none, none, none] }
  | .last64 => { base with
      index := 64, prefixCount := 64,
      suggestedBlocks := List.replicate 65 1,
      processedSlots := List.replicate 64 (some 9) ++ [none] }
  | .noPrewarm => { base with prewarmTask := none, cancellation := none }
  | .noPrewarmer => { base with prewarmTask := none, preWarmerPresent := false }
  | .noTransactions => { base with hasTransactions := fun _ => false, normal := fun site => site != .scheduleHashes }

theorem adapter (scenario : Scenario) : R.NormalAdapter source (vector scenario) := by
  cases scenario <;>
    refine {
      closureIdentity := rfl, irIdentity := rfl,
      publicationAdapter := publicationAdapter _, boundary := ?_, normalCalls := ?_ } <;> decide

-- Each route constructs the source adapter and invokes the source-attached refinement theorem.
theorem admitted_vectors_execute_theorem (scenario : Scenario) : R.SourceAttachedNormal source (vector scenario) :=
  R.generated_normal_refines_spec source (vector scenario) (adapter scenario)

theorem normal_adapter_is_inhabited : ∃ i, R.NormalAdapter source i :=
  ⟨vector .terminalTrue, adapter .terminalTrue⟩

theorem false_signal_returns_branch : (run (vector .terminalFalse)).outcome = .returnedBranch := by decide
theorem false_signal_is_preserved : (run (vector .terminalFalse)).state.map (·.suggestedSignal) = some (some false) := by decide
theorem skip_validation_sets_processed_signal : (run (vector .skipped)).state.map (·.processedSignal) = some (some true) := by decide
theorem skip_validation_sets_suggested_signal : (run (vector .skipped)).state.map (·.suggestedSignal) = some (some true) := by decide
theorem read_only_commits : (run (vector .readOnly)).state.map (·.commitCompleted) = some true := by decide
theorem read_only_resets : (run (vector .readOnly)).state.map (·.resetCompleted) = some true := by decide
theorem checkpoint_reopens_scope : (run (vector .checkpoint64)).state.map (·.scope) = some 20 := by decide
theorem checkpoint_uses_processed_base : (run (vector .checkpoint64)).state.map (·.nextBase) = some (some 202) := by decide
theorem ordinary_iteration_retains_scope : (run (vector .ordinary63)).state.map (·.scope) = some 10 := by decide
theorem terminal_iteration_does_not_checkpoint : (run (vector .last64)).state.map (·.scope) = some 10 := by decide
theorem checkpoint_continues_branch : (run (vector .checkpoint64)).outcome = .nextIteration := by decide
theorem terminal_iteration_counts_success : (run (vector .last64)).observedPrefix = 65 := by decide
theorem returned_array_contains_processed_block : (run base).returnedSlots = some [some 2] := by decide
theorem terminal_trace_has_exact_order : (run base).events =
  [.acceptedPublication 2 3, .cancelInvoked (some 40), .slotAssigned 0 2,
   .inclusionChecked 2 1, .processedSignalAssigned 2 true, .suggestedSignalAssigned 1 true,
   .clearQueueInvoked 30, .prewarmWaitInvoked (some 30), .preCommitInvoked 101, .commitTreeInvoked 10 77,
   .successfulPrefixAssigned 1, .blockEventEvaluated 2 3,
   .nextBaseAssigned 202, .prefetchCleared, .resetInvoked, .hashScheduleInvoked 1,
   .returnPrepared, .unsubscribeInvoked, .finalDisposeInvoked 10,
   .completionEvaluated 100 1, .returnedArray] := by decide

def failsAt (site : Site) : Input := { base with normal := fun candidate => candidate != site }
theorem failed_commit_does_not_count : (run (failsAt .commitTree)).observedPrefix = 0 := by decide
theorem failed_commit_has_unknown_state : (run (failsAt .commitTree)).state = none := by decide
theorem failed_commit_returns_no_array : (run (failsAt .commitTree)).returnedSlots = none := by decide
theorem slot_assignment_precedes_inclusion : Event.slotAssigned 0 2 ∈ (run (failsAt .inclusion)).events := by decide
theorem failed_inclusion_does_not_count : (run (failsAt .inclusion)).observedPrefix = 0 := by decide
theorem failed_block_event_preserves_prefix : (run (failsAt .blockEvent)).observedPrefix = 1 := by decide
theorem failed_reset_preserves_prefix : (run (failsAt .reset)).observedPrefix = 1 := by decide
theorem failed_disposal_escapes : (run (failsAt .disposeFinally)).outcome = .escaped .disposeFinally := by decide
theorem failed_disposal_has_unknown_state : (run (failsAt .disposeFinally)).state = none := by decide
theorem failed_completion_returns_no_array : (run (failsAt .completionEvent)).returnedSlots = none := by decide
theorem failed_reopen_preserves_prefix : (run { vector .checkpoint64 with normal := fun site => site != .checkpointReopen }).observedPrefix = 65 := by decide
theorem failed_reopen_has_unknown_state : (run { vector .checkpoint64 with normal := fun site => site != .checkpointReopen }).state = none := by decide

theorem retry_is_outside_boundary : (run { base with route := .balRetry }).outcome = .outsideBoundary := by decide
theorem parallel_is_outside_boundary : (run { base with route := .parallel }).outcome = .outsideBoundary := by decide
theorem unowned_scope_is_outside_boundary : (run { base with ownedScope := false }).outcome = .outsideBoundary := by decide
theorem rejected_publication_is_outside_boundary : (run { base with publication := { base.publication with validator :=
    { base.publication.validator with accepted := false } } }).outcome = .outsideBoundary := by decide
theorem inconsistent_prefix_is_outside_boundary : (run { base with prefixCount := 1 }).outcome = .outsideBoundary := by decide

end BlockProcessorExtractor.Vectors.BranchAcceptedIterationVectors
