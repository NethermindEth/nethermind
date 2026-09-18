-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import BlockProcessorExtractor.Generated.BranchAcceptedIteration
import BlockProcessorExtractor.Specification.BranchAcceptedIteration
import SequentialBlockPostTransactionFinalizationExtractor.Refinement.ProcessOneValidatedPublication

namespace BlockProcessorExtractor.Refinement.BranchAcceptedIteration

namespace G
export BlockProcessorExtractor.Generated.BranchAcceptedIteration
  (Input State Result Site Outcome Event Op Route sourceClosureSha256 semanticIrSha256
   boundaryValid callsNormal terminal suggestedHeader suggestedNumber checkpoint program initial update escape call step execute run)
end G
namespace S
export BlockProcessorExtractor.Specification.BranchAcceptedIteration
  (Input State Observation Site Outcome Event terminal suggestedHeader suggestedNumber checkpoint finalScope signal filledSlots
   normalState normalTrace normalIteration unknownEscape)
end S
namespace U
export SequentialBlockPostTransactionFinalizationExtractor.Refinement.ProcessOneValidatedPublication
  (SourceWitness NormalReturnAdapter mapInput mapResult generated_normal_refines_spec)
end U
namespace UG
export SequentialBlockPostTransactionFinalizationExtractor.Generated.ProcessOneValidatedPublication
  (PublicationResult run)
end UG

def mapSite : G.Site → S.Site
  | .cancel => .cancel | .inclusion => .inclusion | .queueClear => .queueClear
  | .waitPrewarm => .waitPrewarm | .commitTree => .commitTree | .blockEvent => .blockEvent
  | .checkpointDispose => .checkpointDispose | .checkpointReopen => .checkpointReopen
  | .reset => .reset | .scheduleHashes => .scheduleHashes | .unsubscribe => .unsubscribe
  | .disposeFinally => .disposeFinally | .completionEvent => .completionEvent

def mapEvent : G.Event → S.Event
  | .acceptedPublication b r => .acceptedPublication b r
  | .cancelInvoked c => .cancelInvoked c | .slotAssigned i b => .slotAssigned i b
  | .inclusionChecked p s => .inclusionChecked p s
  | .processedSignalAssigned b s => .processedSignalAssigned b s
  | .suggestedSignalAssigned b s => .suggestedSignalAssigned b s
  | .clearQueueInvoked t => .clearQueueInvoked t | .clearInlineInvoked => .clearInlineInvoked | .noClearWork => .noClearWork
  | .prewarmWaitInvoked t => .prewarmWaitInvoked t | .commitTreeInvoked s n => .commitTreeInvoked s n
  | .preCommitInvoked h => .preCommitInvoked h
  | .successfulPrefixAssigned n => .successfulPrefixAssigned n
  | .blockEventEvaluated b r => .blockEventEvaluated b r
  | .checkpointDisposeInvoked s => .checkpointDisposeInvoked s
  | .checkpointReopenInvoked h => .checkpointReopenInvoked h
  | .nextBaseAssigned h => .nextBaseAssigned h | .prefetchCleared => .prefetchCleared
  | .resetInvoked => .resetInvoked | .hashScheduleInvoked b => .hashScheduleInvoked b
  | .returnPrepared => .returnPrepared | .unsubscribeInvoked => .unsubscribeInvoked
  | .finalDisposeInvoked s => .finalDisposeInvoked s | .completionEvaluated l n => .completionEvaluated l n
  | .returnedArray => .returnedArray | .nextIteration => .nextIteration
  | .escaped site => .escaped (mapSite site)

def mapOutcome : G.Outcome → S.Outcome
  | .running => .running | .nextIteration => .nextIteration | .returnedBranch => .returnedBranch
  | .outsideBoundary => .outsideBoundary | .escaped site => .escaped (mapSite site)

def mapState (s : G.State) : S.State :=
  { processedBlock := s.processedBlock, receipts := s.receipts, slots := s.slots, count := s.count,
    processedSignal := s.processedSignal, suggestedSignal := s.suggestedSignal, scope := s.scope,
    scopeClosed := s.scopeClosed, nextBase := s.nextBase, prewarmTask := s.prewarmTask,
    cancellation := s.cancellation, prefetch := s.prefetch, commitCompleted := s.commitCompleted,
    resetCompleted := s.resetCompleted }

def mapInput (i : G.Input) : S.Input :=
  { publication := U.mapInput i.publication, index := i.index, suggestedBlocks := i.suggestedBlocks,
    processedSlots := i.processedSlots, originalList := i.originalList, header := i.header,
    blockNumber := i.blockNumber, scope := i.scope,
    reopenedScope := i.reopenedScope, readOnly := i.readOnly, hasTransactions := i.hasTransactions,
    preWarmerPresent := i.preWarmerPresent, prewarmTask := i.prewarmTask, cancellation := i.cancellation,
    inclusionSignal := i.inclusionSignal }

def mapResult (r : G.Result) : S.Observation :=
  { publication := U.mapResult r.publication, outcome := mapOutcome r.outcome,
    state := r.state.map mapState, observedPrefix := r.observedPrefix,
    events := r.events.map mapEvent, returnedSlots := r.returnedSlots }

structure SourceWitness where
  closureSha256 : String
  semanticIrSha256 : String
  publicationSource : U.SourceWitness

structure NormalAdapter (source : SourceWitness) (i : G.Input) : Prop where
  closureIdentity : source.closureSha256 = G.sourceClosureSha256
  irIdentity : source.semanticIrSha256 = G.semanticIrSha256
  publicationAdapter : U.NormalReturnAdapter source.publicationSource i.publication
  boundary : G.boundaryValid i = true
  normalCalls : G.callsNormal i = true

structure SourceAttachedNormal (source : SourceWitness) (i : G.Input) : Prop where
  closureIdentity : source.closureSha256 = G.sourceClosureSha256
  irIdentity : source.semanticIrSha256 = G.semanticIrSha256
  refines : S.normalIteration (mapInput i) (mapResult (G.run i))

structure NormalCalls (i : G.Input) : Prop where
  cancel : i.normal .cancel = true
  inclusion : (i.publication.noValidation || i.normal .inclusion) = true
  queueClear : i.normal .queueClear = true
  waitPrewarm : i.normal .waitPrewarm = true
  commitTree : i.normal .commitTree = true
  blockEvent : (i.readOnly || i.normal .blockEvent) = true
  checkpoint : (!G.checkpoint i || (i.normal .checkpointDispose && i.normal .checkpointReopen)) = true
  reset : i.normal .reset = true
  scheduleHashes : (!i.hasTransactions i.publication.suggestedBlock || i.normal .scheduleHashes) = true
  terminal : (!G.terminal i || (i.normal .unsubscribe && i.normal .disposeFinally && i.normal .completionEvent)) = true

theorem normal_calls_fields (i : G.Input) (h : G.callsNormal i = true) : NormalCalls i := by
  simp only [G.callsNormal, Bool.and_eq_true] at h
  rcases h with ⟨⟨⟨⟨⟨⟨⟨⟨⟨hc, hi⟩, hq⟩, hw⟩, ht⟩, hb⟩, hcp⟩, hr⟩, hh⟩, hf⟩
  exact ⟨hc, hi, hq, hw, ht, hb, hcp, hr, hh, hf⟩

def committedState (i : G.Input) : G.State :=
  { processedBlock := i.publication.processBlock.processedBlock,
    receipts := i.publication.processBlock.receipts,
    slots := i.processedSlots.set i.index (some i.publication.processBlock.processedBlock),
    count := i.index + 1,
    processedSignal := some (i.publication.noValidation || i.inclusionSignal),
    suggestedSignal := some (i.publication.noValidation || i.inclusionSignal),
    scope := i.scope, scopeClosed := false, nextBase := none,
    prewarmTask := none, cancellation := none, prefetch := i.prefetch,
    commitCompleted := true, resetCompleted := false }

def committedEvents (i : G.Input) : List G.Event :=
  [.acceptedPublication i.publication.processBlock.processedBlock i.publication.processBlock.receipts,
   .cancelInvoked i.cancellation, .slotAssigned i.index i.publication.processBlock.processedBlock] ++
  (if i.publication.noValidation then [] else
    [.inclusionChecked i.publication.processBlock.processedBlock i.publication.suggestedBlock]) ++
  [.processedSignalAssigned i.publication.processBlock.processedBlock (i.publication.noValidation || i.inclusionSignal),
   .suggestedSignalAssigned i.publication.suggestedBlock (i.publication.noValidation || i.inclusionSignal)] ++
  (match i.prewarmTask with
   | some task => [.clearQueueInvoked task]
   | none => if i.preWarmerPresent then [.clearInlineInvoked] else [.noClearWork]) ++
  [.prewarmWaitInvoked i.prewarmTask, .preCommitInvoked (G.suggestedHeader i),
   .commitTreeInvoked i.scope (G.suggestedNumber i), .successfulPrefixAssigned (i.index + 1)]

def committedResult (i : G.Input) (p : UG.PublicationResult) : G.Result :=
  { publication := p, outcome := .running, state := some (committedState i),
    observedPrefix := i.index + 1, events := committedEvents i, returnedSlots := none }

theorem execute_commit_prefix (i : G.Input) (p : UG.PublicationResult) (h : NormalCalls i) :
    G.execute i [.cancel, .assignSlot, .inclusion, .queueClear, .waitPrewarm, .commitTree, .countSuccess]
      (G.initial i p i.publication.processBlock.processedBlock i.publication.processBlock.receipts) =
      committedResult i p := by
  rcases h with ⟨hc, hi, hq, hw, ht, hb, hcp, hr, hh, hf⟩
  cases hn : i.publication.noValidation <;>
    simp_all [G.execute, G.step, G.initial, G.call, G.update,
      committedResult, committedState, committedEvents, List.append_assoc] <;> rfl

def resetState (i : G.Input) : G.State :=
  { committedState i with
    scope := if G.checkpoint i then i.reopenedScope else i.scope,
    nextBase := some (i.header i.publication.processBlock.processedBlock),
    prefetch := none, resetCompleted := true }

def resetEvents (i : G.Input) : List G.Event :=
  committedEvents i ++
  (if i.readOnly then [] else
    [.blockEventEvaluated i.publication.processBlock.processedBlock i.publication.processBlock.receipts]) ++
  (if G.checkpoint i then
    [.checkpointDisposeInvoked i.scope, .checkpointReopenInvoked (G.suggestedHeader i)] else []) ++
  [.nextBaseAssigned (i.header i.publication.processBlock.processedBlock), .prefetchCleared, .resetInvoked]

def resetResult (i : G.Input) (p : UG.PublicationResult) : G.Result :=
  { publication := p, outcome := .running, state := some (resetState i),
    observedPrefix := i.index + 1, events := resetEvents i, returnedSlots := none }

theorem execute_checkpoint_reset (i : G.Input) (p : UG.PublicationResult) (h : NormalCalls i) :
    G.execute i [.blockEvent, .checkpointDispose, .checkpointReopen, .nextBase, .clearPrefetch, .reset]
      (committedResult i p) = resetResult i p := by
  rcases h with ⟨hc, hi, hq, hw, ht, hb, hcp, hr, hh, hf⟩
  cases hRead : i.readOnly <;> cases hCheckpoint : G.checkpoint i <;>
    simp_all [G.execute, G.step, G.call, G.update, committedResult, committedState,
      resetResult, resetState, resetEvents, List.append_assoc]

def normalResult (i : G.Input) (p : UG.PublicationResult) : G.Result :=
  { publication := p,
    outcome := if G.terminal i then .returnedBranch else .nextIteration,
    state := some { resetState i with scopeClosed := G.terminal i },
    observedPrefix := i.index + 1,
    events := resetEvents i ++
      (if i.hasTransactions i.publication.suggestedBlock then [.hashScheduleInvoked i.publication.suggestedBlock] else []) ++
      (if G.terminal i then
        [.returnPrepared, .unsubscribeInvoked, .finalDisposeInvoked (resetState i).scope,
         .completionEvaluated i.originalList (i.index + 1), .returnedArray]
       else [.nextIteration]),
    returnedSlots := if G.terminal i then some (committedState i).slots else none }

theorem execute_normal_return (i : G.Input) (p : UG.PublicationResult) (h : NormalCalls i) :
    G.execute i [.scheduleHashes, .unsubscribe, .disposeFinally, .completionEvent, .«return»]
      (resetResult i p) = normalResult i p := by
  rcases h with ⟨hc, hi, hq, hw, ht, hb, hcp, hr, hh, hf⟩
  cases hHashes : i.hasTransactions i.publication.suggestedBlock <;>
    cases hTerminal : G.terminal i <;>
    simp_all [G.execute, G.step, G.call, G.update, resetResult, resetState, committedState,
      normalResult, List.append_assoc]

theorem execute_normal_program (i : G.Input) (p : UG.PublicationResult) (h : NormalCalls i) :
    G.execute i G.program
      (G.initial i p i.publication.processBlock.processedBlock i.publication.processBlock.receipts) =
      normalResult i p := by
  change G.execute i [.scheduleHashes, .unsubscribe, .disposeFinally, .completionEvent, .«return»]
    (G.execute i [.blockEvent, .checkpointDispose, .checkpointReopen, .nextBase, .clearPrefetch, .reset]
      (G.execute i [.cancel, .assignSlot, .inclusion, .queueClear, .waitPrewarm, .commitTree, .countSuccess]
        (G.initial i p i.publication.processBlock.processedBlock i.publication.processBlock.receipts))) = _
  rw [execute_commit_prefix i p h, execute_checkpoint_reset i p h, execute_normal_return i p h]

theorem mapped_terminal (i : G.Input) : S.terminal (mapInput i) = G.terminal i := rfl
theorem mapped_checkpoint (i : G.Input) : S.checkpoint (mapInput i) = G.checkpoint i := rfl

theorem mapped_normal_state (i : G.Input) :
    mapState { resetState i with scopeClosed := G.terminal i } = S.normalState (mapInput i) := rfl

theorem mapped_normal_events (i : G.Input) (p : UG.PublicationResult) :
    ((normalResult i p).events.map mapEvent) = S.normalTrace (mapInput i) := by
  simp only [S.normalTrace, S.finalScope, mapped_terminal, mapped_checkpoint]
  by_cases hNo : i.publication.noValidation = true <;> by_cases hRead : i.readOnly = true <;>
    by_cases hCheckpoint : G.checkpoint i = true <;> by_cases hTerminal : G.terminal i = true <;>
    by_cases hHashes : i.hasTransactions i.publication.suggestedBlock = true <;>
    cases hPrewarm : i.prewarmTask <;> by_cases hPrewarmer : i.preWarmerPresent = true <;>
    simp [normalResult, resetState, resetEvents, committedState, committedEvents,
      S.signal,
      S.suggestedHeader, S.suggestedNumber, G.suggestedHeader, G.suggestedNumber,
      mapInput, U.mapInput, mapEvent, hNo, hRead, hCheckpoint, hTerminal, hHashes, hPrewarm, hPrewarmer]

theorem generated_normal_refines_spec (source : SourceWitness) (i : G.Input)
    (adapter : NormalAdapter source i) : SourceAttachedNormal source i := by
  have upstream := U.generated_normal_refines_spec source.publicationSource i.publication adapter.publicationAdapter
  have returned : (UG.run i.publication).returnedTuple =
      some (i.publication.processBlock.processedBlock, i.publication.processBlock.receipts) := by
    exact upstream.2.2.2.1
  refine ⟨adapter.closureIdentity, adapter.irIdentity, ?_⟩
  have completed := upstream.1
  have publication := upstream.2
  have execution : G.run i = normalResult i (UG.run i.publication) := by
    simp only [G.run, adapter.boundary, completed, returned, Bool.not_true, Bool.false_eq_true,
      ↓reduceIte, bne_self_eq_false]
    exact execute_normal_program i _ (normal_calls_fields i adapter.normalCalls)
  rw [execution]
  refine ⟨publication, ?_, ?_, ?_, mapped_normal_events i (UG.run i.publication), ?_⟩
  · cases ht : G.terminal i <;> simp [mapResult, normalResult, mapOutcome, mapped_terminal, ht]
  · exact congrArg some (mapped_normal_state i)
  · rfl
  · rfl

theorem escaping_hook_has_unknown_final_state (r : G.Result) (site : G.Site) (events : List G.Event) :
    S.unknownEscape (mapSite site) (mapResult (G.escape r site events)) := by
  simp [S.unknownEscape, mapResult, G.escape, mapOutcome]

-- This is a model cut, not a claim that C# exception unwinding stops at the failed hook.
theorem escape_stops_the_modeled_suffix (i : G.Input) (r : G.Result) (site : G.Site)
    (events : List G.Event) (ops : List G.Op) :
    G.execute i ops (G.escape r site events) = G.escape r site events := by
  induction ops with
  | nil => rfl
  | cons op rest inductionHypothesis =>
      simpa [G.execute, G.step, G.escape] using inductionHypothesis

theorem rejected_boundary_has_no_return (i : G.Input) (h : G.boundaryValid i = false) :
    (G.run i).outcome = .outsideBoundary ∧ (G.run i).state = none ∧ (G.run i).returnedSlots = none := by
  simp [G.run, h]

end BlockProcessorExtractor.Refinement.BranchAcceptedIteration
