-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import BlockProcessorExtractor.Refinement.BlockchainPublication
import BlockProcessorExtractor.Vectors.FiniteBranchVectors

namespace BlockProcessorExtractor.Vectors.OuterBlockVectors

namespace F
export BlockProcessorExtractor.Generated.NormalFiniteBranchCompletion (Input Resources PublicationInput run containsFlag suggested)
end F
namespace G
export BlockProcessorExtractor.Generated.BlockchainPublication (Input Event Site run classifyFailure FailureInput)
end G
namespace R
export BlockProcessorExtractor.Refinement.BlockchainPublication
  (SourceWitness NormalAdapter SourceAttachedNormal generated_normal_refines_spec failure_classification_refines_boundary_contract)
end R
export BlockProcessorExtractor.Vectors.FiniteBranchVectors
  (Scenario count options finite finiteSource finiteAdapter emptyFinite)

def source : R.SourceWitness :=
  { closureSha256 := BlockProcessorExtractor.Generated.BlockchainPublication.sourceClosureSha256,
    semanticIrSha256 := BlockProcessorExtractor.Generated.BlockchainPublication.semanticIrSha256,
    finiteSource := finiteSource }

def vector (scenario : Scenario) : G.Input :=
  { finite := finite scenario, suggestedBlock := count scenario,
    preparedBlocks := if scenario == .forceEmptyPreload then [] else F.suggested (finite scenario),
    blocksToProcess := F.suggested (finite scenario), blocksResource := 800, inputsResource := 801,
    difficulty := fun header => some (header + 77), baseBlock := some 900,
    headResult := scenario != .markAfterHeadFalse, warnEnabled := true, normal := fun _ => true }

theorem adapter (scenario : Scenario) : R.NormalAdapter source (vector scenario) := by
  refine {
    closureIdentity := rfl, irIdentity := rfl, finiteAdapter := finiteAdapter scenario,
    boundary := ?_, normalCalls := ?_ } <;> cases scenario <;> decide

theorem admitted_vectors_execute_theorem (scenario : Scenario) : R.SourceAttachedNormal source (vector scenario) :=
  R.generated_normal_refines_spec source (vector scenario) (adapter scenario)

theorem normal_adapter_is_inhabited : ∃ i, R.NormalAdapter source i :=
  ⟨vector .two, adapter .two⟩

theorem empty_publication_rejected : (G.run { vector .one with finite := emptyFinite, preparedBlocks := [], blocksToProcess := [] }).outcome =
    .outsideBoundary := by decide
theorem one_block_return : (G.run (vector .one)).returnedBlock = some 101 := by decide
theorem two_block_last : (G.run (vector .two)).returnedBlock = some 102 := by decide
theorem inclusion_false_return : (G.run (vector .inclusionFalse)).returnedBlock = some 101 := by decide
theorem skipped_validation_return : (G.run (vector .skippedValidation)).returnedBlock = some 101 := by decide
theorem read_only_no_head : (G.run (vector .readOnly)).state.map (·.headReturn) = some none := by decide
theorem mark_without_head : (G.run (vector .markWithoutHead)).state.map (·.markCompleted) = some true := by decide
theorem false_head_observed : (.headReturned false : G.Event) ∈ (G.run (vector .markAfterHeadFalse)).events := by decide
theorem mark_after_false_head : (.markReturned : G.Event) ∈ (G.run (vector .markAfterHeadFalse)).events := by decide
theorem read_only_mark : (G.run (vector .readOnlyMark)).state.map (·.markCompleted) = some true := by decide
theorem forced_empty_head_payload : (.headInvoked 1001 true false [] : G.Event) ∈ (G.run (vector .forceEmptyPreload)).events := by decide
theorem forced_empty_return : (G.run (vector .forceEmptyPreload)).returnedBlock = some 101 := by decide
theorem forced_nonempty_rejected : (G.run { vector .forceEmptyPreload with preparedBlocks := [1] }).outcome = .outsideBoundary := by decide
theorem forced_multiple_rejected : (G.run { vector .two with finite := { finite .two with options := 6 }, preparedBlocks := [] }).outcome =
    .outsideBoundary := by decide
theorem ordinary_list_divergence_rejected : (G.run { vector .one with preparedBlocks := [999] }).outcome = .outsideBoundary := by decide
theorem input_resource_mismatch_rejected : (G.run { vector .one with finite := { finite .one with originalList := 700 } }).outcome = .outsideBoundary := by decide
theorem difficulty_identity : (G.run (vector .one)).state.map (·.difficultyWrites) = some [(1101, some 1078)] := by decide
theorem normal_event_order : (G.run (vector .one)).events =
  [.branchReturned [101], .errorCleared, .stopwatchStopped, .selectedLast 101,
   .difficultyAssigned 1101 (some 1078), .statsUpdated [101] (some 900),
   .headInvoked 1001 true false [1], .headReturned true, .bestKnownMetricRead,
   .returnPrepared (some 101), .disposeBlocksInvoked 800, .disposeInputsInvoked 801,
   .returned (some 101)] := by decide

def failsAt (site : G.Site) : G.Input :=
  { vector .markAfterHeadFalse with normal := fun candidate => candidate != site }
theorem mark_escape_unknown : (G.run (failsAt .mark)).state = none := by decide
theorem disposal_escape_no_return : (G.run (failsAt .disposeBlocks)).returnedBlock = none := by decide
theorem first_disposal_stops_model : (.disposeInputsInvoked 801 : G.Event) ∉ (G.run (failsAt .disposeBlocks)).events := by decide
theorem second_disposal_unknown : (G.run (failsAt .disposeInputs)).state = none := by decide

def invalid : G.FailureInput :=
  { failure := .invalid (some 5) "invalid", options := 4, blocks := [1, 2, 3],
    hash := fun block => if block == 3 then some 6 else some 5, handlerNormal := true }
theorem invalid_first_match : (G.classifyFailure invalid).invalidEventBlock = some (some 1) := by decide
theorem invalid_all_deletions : (G.classifyFailure invalid).deletionCalls = some [1, 2] := by decide
theorem read_only_no_deletions : (G.classifyFailure { invalid with options := 69 }).deletionCalls = some [] := by decide
theorem null_hash_first_match : (G.classifyFailure { invalid with failure := .invalid none "invalid", hash := fun _ => none }).invalidEventBlock =
    some (some 1) := by decide
theorem null_hash_no_deletions : (G.classifyFailure { invalid with failure := .invalid none "invalid", hash := fun _ => none }).deletionCalls =
    some [] := by decide
theorem unknown_failure_error : (G.classifyFailure { invalid with failure := .escaped }).error = none := by decide
theorem handler_failure_unknown_deletions : (G.classifyFailure { invalid with handlerNormal := false }).deletionCalls = none := by decide
theorem invalid_contract : BlockProcessorExtractor.Specification.BlockchainPublication.failureContract
    (BlockProcessorExtractor.Refinement.BlockchainPublication.mapFailureInput invalid)
    (BlockProcessorExtractor.Refinement.BlockchainPublication.mapFailureObservation (G.classifyFailure invalid)) :=
  R.failure_classification_refines_boundary_contract invalid

end BlockProcessorExtractor.Vectors.OuterBlockVectors
