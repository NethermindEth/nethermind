-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import BlockProcessorExtractor.Generated.NormalFiniteBranchCompletion
import BlockProcessorExtractor.Specification.NormalFiniteBranchCompletion
import BlockProcessorExtractor.Refinement.BranchAcceptedIteration

namespace BlockProcessorExtractor.Refinement.NormalFiniteBranchCompletion

namespace G
export BlockProcessorExtractor.Generated.NormalFiniteBranchCompletion
  (Input Resources Accumulator Entry Result PublicationInput Outcome initial next iteration entry complete run finish prepend refused escaped domain loopIncrement)
end G
namespace S
export BlockProcessorExtractor.Specification.NormalFiniteBranchCompletion
  (Input Resources Accumulator Entry Observation initial next iteration entry NormalSteps normalCompletion)
end S
namespace L
export BlockProcessorExtractor.Refinement.BranchAcceptedIteration
  (SourceWitness NormalAdapter mapInput mapResult mapState mapOutcome generated_normal_refines_spec)
end L
namespace LG
export BlockProcessorExtractor.Generated.BranchAcceptedIteration (State Outcome run terminal)
end LG
namespace LS
export BlockProcessorExtractor.Specification.BranchAcceptedIteration (State Outcome normalState normalIteration terminal)
end LS
namespace P
export SequentialBlockPostTransactionFinalizationExtractor.Refinement.ProcessOneValidatedPublication (mapInput)
end P

def mapResources (r : G.Resources) : S.Resources :=
  { cancellation := r.cancellation, prewarm := r.prewarm, prefetch := r.prefetch,
    reopenedScope := r.reopenedScope, inclusion := r.inclusion }
def mapInput (i : G.Input) : S.Input :=
  { publications := i.publications.map P.mapInput, originalList := i.originalList, options := i.options,
    scope := i.scope, baseHeader := i.baseHeader, header := i.header, blockNumber := i.blockNumber,
    hasTransactions := i.hasTransactions, nullTracer := i.nullTracer, preWarmerPresent := i.preWarmerPresent,
    resources := fun index => mapResources (i.resources index) }
def mapAccumulator (a : G.Accumulator) : S.Accumulator :=
  { slots := a.slots, count := a.count, scope := a.scope, baseHeader := a.baseHeader }
def mapEntry (e : G.Entry) : S.Entry :=
  { index := e.index, suggested := e.suggested, baseHeader := e.baseHeader,
    refreshedSpec := e.refreshedSpec, blockOptions := e.blockOptions }
def mapResult (r : G.Result) : S.Observation :=
  { state := r.state.map mapAccumulator, entries := r.entries.map mapEntry,
    iterations := r.iterations.map L.mapResult, observedPrefix := r.observedPrefix, returnedBlocks := r.returnedBlocks }

def unmapLeafState (s : LS.State) : LG.State :=
  { processedBlock := s.processedBlock, receipts := s.receipts, slots := s.slots, count := s.count,
    processedSignal := s.processedSignal, suggestedSignal := s.suggestedSignal, scope := s.scope,
    scopeClosed := s.scopeClosed, nextBase := s.nextBase, prewarmTask := s.prewarmTask,
    cancellation := s.cancellation, prefetch := s.prefetch, commitCompleted := s.commitCompleted,
    resetCompleted := s.resetCompleted }
def unmapLeafOutcome : LS.Outcome → LG.Outcome
  | .running => .running | .nextIteration => .nextIteration | .returnedBranch => .returnedBranch
  | .outsideBoundary => .outsideBoundary
  | .escaped site => .escaped (match site with
      | .cancel => .cancel | .inclusion => .inclusion | .queueClear => .queueClear
      | .waitPrewarm => .waitPrewarm | .commitTree => .commitTree | .blockEvent => .blockEvent
      | .checkpointDispose => .checkpointDispose | .checkpointReopen => .checkpointReopen
      | .reset => .reset | .scheduleHashes => .scheduleHashes | .unsubscribe => .unsubscribe
      | .disposeFinally => .disposeFinally | .completionEvent => .completionEvent)

theorem unmap_map_leaf_state (s : LG.State) : unmapLeafState (L.mapState s) = s := rfl
theorem unmap_map_leaf_outcome (o : LG.Outcome) : unmapLeafOutcome (L.mapOutcome o) = o := by
  cases o with
  | escaped site => cases site <;> rfl
  | _ => rfl

def expectedNext (i : G.Input) (index : Nat) (a : G.Accumulator) (p : G.PublicationInput) : G.Accumulator :=
  G.next (unmapLeafState (LS.normalState (L.mapInput (G.iteration i index a p))))

theorem map_iteration (i : G.Input) (index : Nat) (a : G.Accumulator) (p : G.PublicationInput) :
    L.mapInput (G.iteration i index a p) = S.iteration (mapInput i) index (mapAccumulator a) (P.mapInput p) := by
  simp only [G.iteration, L.mapInput, S.iteration, mapInput, mapAccumulator, mapResources,
    BlockProcessorExtractor.Generated.NormalFiniteBranchCompletion.suggested, List.map_map]
  rfl
theorem map_expectedNext (i : G.Input) (index : Nat) (a : G.Accumulator) (p : G.PublicationInput) :
    mapAccumulator (expectedNext i index a p) =
      S.next (LS.normalState (S.iteration (mapInput i) index (mapAccumulator a) (P.mapInput p))) := by
  rw [← map_iteration]
  rfl
theorem map_entry (i : G.Input) (index : Nat) (a : G.Accumulator) (p : G.PublicationInput) :
    mapEntry (G.entry i index a p) = S.entry (mapInput i) index (mapAccumulator a) (P.mapInput p) := rfl

def SlotInvariant (i : G.Input) (index : Nat) (a : G.Accumulator)
    (remaining : List G.PublicationInput) : Prop :=
  ∃ processed : List Nat,
    processed.length = index ∧ index + remaining.length = i.publications.length ∧
    a.slots = processed.map some ++ List.replicate remaining.length none

theorem initial_slot_invariant (i : G.Input) : SlotInvariant i 0 (G.initial i) i.publications := by
  exact ⟨[], rfl, by simp, rfl⟩

theorem expectedNext_slot_invariant (i : G.Input) (index : Nat) (a : G.Accumulator)
    (p : G.PublicationInput) (rest : List G.PublicationInput)
    (invariant : SlotInvariant i index a (p :: rest)) :
    SlotInvariant i (index + 1) (expectedNext i index a p) rest := by
  rcases invariant with ⟨processed, length, remainingLength, slots⟩
  refine ⟨processed ++ [p.processBlock.processedBlock], by simp [length], ?_, ?_⟩
  · simp only [List.length_cons] at remainingLength
    omega
  · change a.slots.set index (some p.processBlock.processedBlock) =
      (processed ++ [p.processBlock.processedBlock]).map some ++ List.replicate rest.length none
    rw [slots, List.set_append_right index (some p.processBlock.processedBlock) (by simp [length])]
    simp [length, List.replicate_succ, List.map_append, List.append_assoc]

theorem completed_slots_are_filled (i : G.Input) (index : Nat) (a : G.Accumulator)
    (invariant : SlotInvariant i index a []) : a.slots.all Option.isSome = true := by
  rcases invariant with ⟨processed, _, _, slots⟩
  simp [slots]

theorem iteration_terminal_matches_remaining (i : G.Input) (index : Nat) (a : G.Accumulator)
    (p : G.PublicationInput) (rest : List G.PublicationInput)
    (invariant : SlotInvariant i index a (p :: rest)) :
    LG.terminal (G.iteration i index a p) = rest.isEmpty := by
  rcases invariant with ⟨_, _, remainingLength, _⟩
  change (index + 1 == (i.publications.map (·.suggestedBlock)).length) = rest.isEmpty
  simp only [List.length_map]
  cases rest with
  | nil =>
      have last : index + 1 = i.publications.length := by simpa using remainingLength
      simp [last]
  | cons next remaining =>
      have notLast : index + 1 ≠ i.publications.length := by
        simp only [List.length_cons] at remainingLength
        omega
      simp [notLast]

structure SourceWitness where
  closureSha256 : String
  semanticIrSha256 : String
  iterationSource : L.SourceWitness

-- These are input/source/hook obligations at constructed prefixes, never output equalities.
def Adapters (source : SourceWitness) (i : G.Input) : Nat → G.Accumulator → List G.PublicationInput → Prop
  | _, _, [] => True
  | index, a, p :: rest =>
      i.preludeNormal index = true ∧
      L.NormalAdapter source.iterationSource (G.iteration i index a p) ∧
      Adapters source i (index + 1) (expectedNext i index a p) rest

structure NormalAdapter (source : SourceWitness) (i : G.Input) : Prop where
  closureIdentity : source.closureSha256 = BlockProcessorExtractor.Generated.NormalFiniteBranchCompletion.sourceClosureSha256
  irIdentity : source.semanticIrSha256 = BlockProcessorExtractor.Generated.NormalFiniteBranchCompletion.semanticIrSha256
  boundary : G.domain i = true
  steps : Adapters source i 0 (G.initial i) i.publications

theorem complete_refines_spec (source : SourceWitness) (i : G.Input)
    (index : Nat) (a : G.Accumulator) (remaining : List G.PublicationInput)
    (adapters : Adapters source i index a remaining)
    (invariant : SlotInvariant i index a remaining) :
    (G.complete i index a remaining).outcome = .completed ∧
      S.NormalSteps (mapInput i) index (mapAccumulator a) (remaining.map P.mapInput)
        (mapResult (G.complete i index a remaining)) := by
  induction remaining generalizing index a with
  | nil =>
      have filled := completed_slots_are_filled i index a invariant
      constructor
      · simp [G.complete, G.finish, filled]
      · simpa [G.complete, G.finish, filled, mapResult, mapAccumulator] using
          (BlockProcessorExtractor.Specification.NormalFiniteBranchCompletion.NormalSteps.done
            (i := mapInput i) index (mapAccumulator a) filled)
  | cons p rest inductionHypothesis =>
      rcases adapters with ⟨prelude, adapter, tailAdapters⟩
      have terminal := iteration_terminal_matches_remaining i index a p rest invariant
      have leaf := (L.generated_normal_refines_spec source.iterationSource (G.iteration i index a p) adapter).refines
      have stateFact := leaf.2.2.1
      have stateEq : (LG.run (G.iteration i index a p)).state =
          some (unmapLeafState (LS.normalState (L.mapInput (G.iteration i index a p)))) := by
        have transported := congrArg (Option.map unmapLeafState) stateFact
        simpa [L.mapResult, Option.map_map, Function.comp_def, unmap_map_leaf_state] using transported
      have outcomeFact := leaf.2.1
      have terminalSpec : LS.terminal (L.mapInput (G.iteration i index a p)) = rest.isEmpty := by
        exact terminal
      have outcomeEq : (LG.run (G.iteration i index a p)).outcome =
          if rest.isEmpty then .returnedBranch else .nextIteration := by
        have transported := congrArg unmapLeafOutcome outcomeFact
        rw [terminalSpec] at transported
        change unmapLeafOutcome (L.mapOutcome (LG.run (G.iteration i index a p)).outcome) =
          unmapLeafOutcome (if rest.isEmpty then .returnedBranch else .nextIteration) at transported
        rw [unmap_map_leaf_outcome] at transported
        cases h : rest.isEmpty <;>
          simpa [h, unmapLeafOutcome] using transported
      have tail := inductionHypothesis (index + 1) (expectedNext i index a p) tailAdapters
        (expectedNext_slot_invariant i index a p rest invariant)
      have unfolded : G.complete i index a (p :: rest) =
          G.prepend (G.entry i index a p) (LG.run (G.iteration i index a p))
            (G.complete i (index + 1) (expectedNext i index a p) rest) := by
        cases h : rest.isEmpty <;>
          simp [G.complete, prelude, stateEq, outcomeEq, h, G.loopIncrement, expectedNext]
      rw [unfolded]
      refine ⟨tail.1, ?_⟩
      have relation := BlockProcessorExtractor.Specification.NormalFiniteBranchCompletion.NormalSteps.step
        (i := mapInput i) index (mapAccumulator a) (P.mapInput p)
        (rest.map P.mapInput) (L.mapResult (LG.run (G.iteration i index a p)))
        (mapResult (G.complete i (index + 1) (expectedNext i index a p) rest))
        (by simpa [← map_iteration] using leaf)
        (by simpa [map_expectedNext] using tail.2)
      simpa [G.prepend, mapResult, map_entry] using relation

structure SourceAttachedNormal (source : SourceWitness) (i : G.Input) : Prop where
  closureIdentity : source.closureSha256 = BlockProcessorExtractor.Generated.NormalFiniteBranchCompletion.sourceClosureSha256
  irIdentity : source.semanticIrSha256 = BlockProcessorExtractor.Generated.NormalFiniteBranchCompletion.semanticIrSha256
  completed : (G.run i).outcome = .completed
  refines : S.normalCompletion (mapInput i) (mapResult (G.run i))

theorem generated_normal_refines_spec (source : SourceWitness) (i : G.Input)
    (adapter : NormalAdapter source i) : SourceAttachedNormal source i := by
  refine ⟨adapter.closureIdentity, adapter.irIdentity, ?_, ?_⟩
  · cases empty : i.publications.isEmpty
    · simpa [G.run, adapter.boundary, empty] using
        (complete_refines_spec source i 0 (G.initial i) i.publications adapter.steps (initial_slot_invariant i)).1
    · simp [G.run, adapter.boundary, empty, BlockProcessorExtractor.Generated.NormalFiniteBranchCompletion.emptyResult]
  · cases empty : i.publications.isEmpty
    · have result := (complete_refines_spec source i 0 (G.initial i) i.publications adapter.steps (initial_slot_invariant i)).2
      simpa [S.normalCompletion, G.run, adapter.boundary, empty, mapInput, G.initial, S.initial, mapAccumulator] using result
    · simp [S.normalCompletion, G.run, adapter.boundary, empty, mapInput, mapResult,
        BlockProcessorExtractor.Generated.NormalFiniteBranchCompletion.emptyResult]

theorem escape_has_no_return (index successfulPrefix : Nat) (frames : List BlockProcessorExtractor.Generated.BranchAcceptedIteration.Result) :
    (G.escaped index successfulPrefix frames).state = none ∧ (G.escaped index successfulPrefix frames).returnedBlocks = none := ⟨rfl, rfl⟩

end BlockProcessorExtractor.Refinement.NormalFiniteBranchCompletion
