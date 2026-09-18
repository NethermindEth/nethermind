-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import BlockProcessorExtractor.Generated.BlockchainPublication
import BlockProcessorExtractor.Specification.BlockchainPublication
import BlockProcessorExtractor.Refinement.NormalFiniteBranchCompletion

namespace BlockProcessorExtractor.Refinement.BlockchainPublication

namespace G
export BlockProcessorExtractor.Generated.BlockchainPublication
  (Input Result State Event Site Outcome Op run execute step program initial call update escape refused
   readOnly updateHead markProcessed forceProcessing preparedEntry suggestedHeader boundary callsNormal Failure FailureInput FailureObservation classifyFailure)
end G
namespace S
export BlockProcessorExtractor.Specification.BlockchainPublication
  (Input Observation State Event Site normalPublication normalState normalTrace writes readOnly updateHead
   markProcessed forceProcessing preparedEntry suggestedHeader Failure FailureInput FailureObservation failureContract)
end S
namespace F
export BlockProcessorExtractor.Refinement.NormalFiniteBranchCompletion
  (SourceWitness NormalAdapter mapInput mapResult generated_normal_refines_spec)
end F
namespace FG
export BlockProcessorExtractor.Generated.NormalFiniteBranchCompletion (Result run)
end FG

def mapInput (i : G.Input) : S.Input :=
  { finite := F.mapInput i.finite, suggestedBlock := i.suggestedBlock, preparedBlocks := i.preparedBlocks,
    blocksToProcess := i.blocksToProcess,
    blocksResource := i.blocksResource, inputsResource := i.inputsResource, difficulty := i.difficulty,
    baseBlock := i.baseBlock, headResult := i.headResult, warnEnabled := i.warnEnabled }
def mapSite : G.Site → S.Site
  | .stop => .stop | .stats => .stats | .head => .head | .mark => .mark | .metrics => .metrics
  | .disposeBlocks => .disposeBlocks | .disposeInputs => .disposeInputs
def mapEvent : G.Event → S.Event
  | .branchReturned blocks => .branchReturned blocks
  | .errorCleared => .errorCleared | .stopwatchStopped => .stopwatchStopped
  | .selectedLast block => .selectedLast block | .difficultyAssigned header value => .difficultyAssigned header value
  | .statsUpdated blocks baseBlock => .statsUpdated blocks baseBlock
  | .headInvoked header processed force preloaded => .headInvoked header processed force preloaded
  | .headReturned result => .headReturned result | .headWarning => .headWarning
  | .markInvoked blocks => .markInvoked blocks | .markReturned => .markReturned
  | .bestKnownMetricRead => .bestKnownMetricRead | .returnPrepared block => .returnPrepared block
  | .disposeBlocksInvoked resource => .disposeBlocksInvoked resource
  | .disposeInputsInvoked resource => .disposeInputsInvoked resource
  | .returned block => .returned block | .escaped site => .escaped (mapSite site)
def mapState (s : G.State) : S.State :=
  { lastProcessed := s.lastProcessed, difficultyWrites := s.difficultyWrites, headReturn := s.headReturn,
    markCompleted := s.markCompleted, blocksDisposed := s.blocksDisposed, inputsDisposed := s.inputsDisposed }
def mapResult (r : G.Result) : S.Observation :=
  { finite := F.mapResult r.finite, state := r.state.map mapState, events := r.events.map mapEvent,
    returnedBlock := r.returnedBlock, error := r.error }

theorem map_readOnly (i : G.Input) : S.readOnly (mapInput i) = G.readOnly i := rfl
theorem map_updateHead (i : G.Input) : S.updateHead (mapInput i) = G.updateHead i := rfl
theorem map_markProcessed (i : G.Input) : S.markProcessed (mapInput i) = G.markProcessed i := rfl
theorem map_suggestedHeader (i : G.Input) : S.suggestedHeader (mapInput i) = G.suggestedHeader i := rfl

theorem prepared_entry_refines (i : G.Input) (entry : G.preparedEntry i = true) :
    S.preparedEntry (mapInput i) := by
  have suggested : (mapInput i).finite.publications.map (·.suggestedBlock) =
      BlockProcessorExtractor.Generated.NormalFiniteBranchCompletion.suggested i.finite := by
    simp only [mapInput, F.mapInput, List.map_map]
    rfl
  unfold S.preparedEntry
  rw [suggested]
  rw [show S.forceProcessing (mapInput i) = G.forceProcessing i from rfl]
  change i.finite.originalList = i.inputsResource ∧
    i.blocksToProcess = BlockProcessorExtractor.Generated.NormalFiniteBranchCompletion.suggested i.finite ∧
    if G.forceProcessing i then i.preparedBlocks = [] ∧ i.blocksToProcess = [i.suggestedBlock]
    else i.preparedBlocks = i.blocksToProcess ∧ i.blocksToProcess ≠ []
  cases forced : G.forceProcessing i <;>
    simpa [G.preparedEntry, forced, Bool.and_eq_true, and_assoc] using entry

theorem boundary_has_prepared_entry (i : G.Input) (boundary : G.boundary i = true) :
    S.preparedEntry (mapInput i) := by
  simp only [G.boundary, Bool.and_eq_true] at boundary
  exact prepared_entry_refines i boundary.1

structure NormalCalls (i : G.Input) : Prop where
  stop : i.normal .stop = true
  stats : (G.readOnly i || i.normal .stats) = true
  head : (!G.updateHead i || i.normal .head) = true
  mark : (!G.markProcessed i || i.normal .mark) = true
  metrics : (G.readOnly i || i.normal .metrics) = true
  disposeBlocks : i.normal .disposeBlocks = true
  disposeInputs : i.normal .disposeInputs = true

theorem normal_calls_fields (i : G.Input) (normal : G.callsNormal i = true) : NormalCalls i := by
  simp only [G.callsNormal, Bool.and_eq_true] at normal
  rcases normal with ⟨⟨⟨⟨⟨⟨stop, stats⟩, head⟩, mark⟩, metrics⟩, blocks⟩, inputs⟩
  exact ⟨stop, stats, head, mark, metrics, blocks, inputs⟩

def selectionState (i : G.Input) (blocks : List Nat) : G.State :=
  { lastProcessed := blocks.getLast?,
    difficultyWrites := match blocks.getLast? with
      | none => []
      | some last => [(i.finite.header last, i.difficulty (G.suggestedHeader i))],
    headReturn := none, markCompleted := false, blocksDisposed := false, inputsDisposed := false }
def selectionEvents (i : G.Input) (blocks : List Nat) : List G.Event :=
  [.branchReturned blocks, .errorCleared, .stopwatchStopped] ++
  (match blocks.getLast? with
   | none => []
   | some last => [.selectedLast last, .difficultyAssigned (i.finite.header last) (i.difficulty (G.suggestedHeader i))]) ++
  (if G.readOnly i then [] else [.statsUpdated blocks i.baseBlock])
def selectionResult (i : G.Input) (finite : FG.Result) (blocks : List Nat) : G.Result :=
  { finite := finite, outcome := .running, state := some (selectionState i blocks),
    events := selectionEvents i blocks, returnedBlock := none, error := none }

theorem execute_selection (i : G.Input) (finite : FG.Result) (blocks : List Nat) (normal : NormalCalls i) :
    G.execute i blocks [.stop, .difficulty, .stats] (G.initial finite blocks) = selectionResult i finite blocks := by
  rcases normal with ⟨stop, stats, head, mark, metrics, disposeBlocks, disposeInputs⟩
  cases read : G.readOnly i <;> cases last : blocks.getLast? <;>
    simp_all [G.execute, G.step, G.initial, G.call, G.update,
      selectionResult, selectionState, selectionEvents]

def publicationState (i : G.Input) (blocks : List Nat) : G.State :=
  { selectionState i blocks with
    headReturn := if G.updateHead i then some i.headResult else none,
    markCompleted := G.markProcessed i }
def publicationEvents (i : G.Input) (blocks : List Nat) : List G.Event :=
  selectionEvents i blocks ++
  (if G.updateHead i then
    [.headInvoked (G.suggestedHeader i) true false i.preparedBlocks, .headReturned i.headResult] ++
      (if !i.headResult && i.warnEnabled then [.headWarning] else []) else []) ++
  (if G.markProcessed i then [.markInvoked i.preparedBlocks, .markReturned] else []) ++
  (if G.readOnly i then [] else [.bestKnownMetricRead])
def publicationResult (i : G.Input) (finite : FG.Result) (blocks : List Nat) : G.Result :=
  { finite := finite, outcome := .running, state := some (publicationState i blocks),
    events := publicationEvents i blocks, returnedBlock := none, error := none }

theorem execute_publication (i : G.Input) (finite : FG.Result) (blocks : List Nat) (normal : NormalCalls i) :
    G.execute i blocks [.head, .mark, .metrics] (selectionResult i finite blocks) = publicationResult i finite blocks := by
  rcases normal with ⟨stop, stats, head, mark, metrics, disposeBlocks, disposeInputs⟩
  cases headEnabled : G.updateHead i <;> cases markEnabled : G.markProcessed i <;> cases read : G.readOnly i <;>
    simp_all [G.execute, G.step, G.call, G.update, selectionResult,
      selectionState, publicationResult, publicationState, publicationEvents, List.append_assoc]

def returnedResult (i : G.Input) (finite : FG.Result) (blocks : List Nat) : G.Result :=
  { finite := finite, outcome := .returned,
    state := some { publicationState i blocks with blocksDisposed := true, inputsDisposed := true },
    events := publicationEvents i blocks ++
      [.returnPrepared blocks.getLast?, .disposeBlocksInvoked i.blocksResource,
       .disposeInputsInvoked i.inputsResource, .returned blocks.getLast?],
    returnedBlock := blocks.getLast?, error := none }

theorem execute_cleanup (i : G.Input) (finite : FG.Result) (blocks : List Nat) (normal : NormalCalls i) :
    G.execute i blocks [.prepareReturn, .disposeBlocks, .disposeInputs, .«return»]
      (publicationResult i finite blocks) = returnedResult i finite blocks := by
  simp [G.execute, G.step, G.call, G.update, normal.disposeBlocks, normal.disposeInputs,
    publicationResult, publicationState, selectionState, returnedResult, List.append_assoc]

theorem returned_observation (i : G.Input) (finite : FG.Result) (blocks : List Nat) :
    let result := returnedResult i finite blocks
    result.outcome = .returned ∧ result.finite = finite ∧
      result.state.map mapState = some (S.normalState (mapInput i) blocks) ∧
      result.returnedBlock = blocks.getLast? ∧ result.error = none ∧
      result.events.map mapEvent = S.normalTrace (mapInput i) blocks := by
  cases last : blocks.getLast? <;> cases read : G.readOnly i <;>
    cases head : G.updateHead i <;> cases mark : G.markProcessed i <;>
    cases warning : (!i.headResult && i.warnEnabled) <;>
    simp only [returnedResult, publicationState, selectionState, publicationEvents, selectionEvents,
      S.normalState, S.normalTrace, S.writes, map_readOnly, map_updateHead, map_markProcessed, map_suggestedHeader] <;>
    simp [mapState, mapEvent, mapInput, last, read, head, mark, warning] <;> rfl

theorem execute_normal (i : G.Input) (finite : FG.Result) (blocks : List Nat)
    (normal : G.callsNormal i = true) :
    let result := G.execute i blocks G.program (G.initial finite blocks)
    result.outcome = .returned ∧ result.finite = finite ∧
      result.state.map mapState = some (S.normalState (mapInput i) blocks) ∧
      result.returnedBlock = blocks.getLast? ∧ result.error = none ∧
      result.events.map mapEvent = S.normalTrace (mapInput i) blocks := by
  have calls := normal_calls_fields i normal
  have stages : G.execute i blocks G.program (G.initial finite blocks) =
      G.execute i blocks [.prepareReturn, .disposeBlocks, .disposeInputs, .«return»]
        (G.execute i blocks [.head, .mark, .metrics]
          (G.execute i blocks [.stop, .difficulty, .stats] (G.initial finite blocks))) := rfl
  rw [stages, execute_selection i finite blocks calls, execute_publication i finite blocks calls,
    execute_cleanup i finite blocks calls]
  exact returned_observation i finite blocks

structure SourceWitness where
  closureSha256 : String
  semanticIrSha256 : String
  finiteSource : F.SourceWitness
structure NormalAdapter (source : SourceWitness) (i : G.Input) : Prop where
  closureIdentity : source.closureSha256 = BlockProcessorExtractor.Generated.BlockchainPublication.sourceClosureSha256
  irIdentity : source.semanticIrSha256 = BlockProcessorExtractor.Generated.BlockchainPublication.semanticIrSha256
  finiteAdapter : F.NormalAdapter source.finiteSource i.finite
  boundary : G.boundary i = true
  normalCalls : G.callsNormal i = true
structure SourceAttachedNormal (source : SourceWitness) (i : G.Input) : Prop where
  closureIdentity : source.closureSha256 = BlockProcessorExtractor.Generated.BlockchainPublication.sourceClosureSha256
  irIdentity : source.semanticIrSha256 = BlockProcessorExtractor.Generated.BlockchainPublication.semanticIrSha256
  returned : (G.run i).outcome = .returned
  refines : S.normalPublication (mapInput i) (mapResult (G.run i))

theorem generated_normal_refines_spec (source : SourceWitness) (i : G.Input)
    (adapter : NormalAdapter source i) : SourceAttachedNormal source i := by
  have finite := F.generated_normal_refines_spec source.finiteSource i.finite adapter.finiteAdapter
  obtain ⟨blocks, returned⟩ :=
    BlockProcessorExtractor.Specification.NormalFiniteBranchCompletion.normalCompletion_has_return finite.refines
  have blocksEq : (FG.run i.finite).returnedBlocks = some blocks := returned
  have unfolded : G.run i = G.execute i blocks G.program (G.initial (FG.run i.finite) blocks) := by
    simp [G.run, adapter.boundary, finite.completed, blocksEq]
  have executed := execute_normal i (FG.run i.finite) blocks adapter.normalCalls
  refine ⟨adapter.closureIdentity, adapter.irIdentity, ?_, ?_⟩
  · rw [unfolded]
    exact executed.1
  · rw [unfolded]
    refine ⟨boundary_has_prepared_entry i adapter.boundary, ?_, blocks, ?_,
      executed.2.2.1, executed.2.2.2.1, executed.2.2.2.2.1, executed.2.2.2.2.2⟩
    · simpa [mapInput, mapResult, executed.2.1] using finite.refines
    · change (F.mapResult ((G.execute i blocks G.program (G.initial (FG.run i.finite) blocks)).finite)).returnedBlocks = some blocks
      rw [executed.2.1]
      exact blocksEq

def mapFailure : G.Failure → S.Failure
  | .unknownParent => .unknownParent | .notBetter => .notBetter
  | .invalid hash message => .invalid hash message | .escaped => .escaped
def mapFailureInput (i : G.FailureInput) : S.FailureInput :=
  { failure := mapFailure i.failure, options := i.options, blocks := i.blocks, hash := i.hash, handlerNormal := i.handlerNormal }
def mapFailureObservation (o : G.FailureObservation) : S.FailureObservation :=
  { returned := o.returned, returnedBlock := o.returnedBlock, error := o.error, invalidEventBlock := o.invalidEventBlock,
    deletionCalls := o.deletionCalls, finalStateKnown := o.finalStateKnown }

theorem failure_classification_refines_boundary_contract (i : G.FailureInput) :
    S.failureContract (mapFailureInput i) (mapFailureObservation (G.classifyFailure i)) := by
  have flag : BlockProcessorExtractor.Generated.NormalFiniteBranchCompletion.containsFlag i.options 65 =
      BlockProcessorExtractor.Specification.NormalFiniteBranchCompletion.containsFlag i.options 65 := rfl
  cases normal : i.handlerNormal <;> cases h : i.failure <;>
    simp [G.classifyFailure, h, normal, S.failureContract, mapFailureInput, mapFailureObservation, mapFailure, flag]

theorem escaped_publication_has_unknown_state (r : G.Result) (site : G.Site) (events : List G.Event) :
    (G.escape r site events).state = none ∧ (G.escape r site events).returnedBlock = none := ⟨rfl, rfl⟩

end BlockProcessorExtractor.Refinement.BlockchainPublication
