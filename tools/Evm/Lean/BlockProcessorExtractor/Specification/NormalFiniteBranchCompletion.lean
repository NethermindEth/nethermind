-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import BlockProcessorExtractor.Specification.BranchAcceptedIteration

namespace BlockProcessorExtractor.Specification.NormalFiniteBranchCompletion

namespace U
export BlockProcessorExtractor.Specification.BranchAcceptedIteration (Input State Observation normalState normalTrace normalIteration)
end U
abbrev PublicationInput := SequentialBlockPostTransactionFinalizationExtractor.Specification.ProcessOneValidatedPublication.Input

structure Resources where
  cancellation : Option Nat
  prewarm : Option Nat
  prefetch : Option Nat
  reopenedScope : Nat
  inclusion : Bool
  deriving DecidableEq, Repr
structure Input where
  publications : List PublicationInput
  originalList : Nat
  options : Nat
  scope : Nat
  baseHeader : Option Nat
  header : Nat → Nat
  blockNumber : Nat → Nat
  hasTransactions : Nat → Bool
  nullTracer : Bool
  preWarmerPresent : Bool
  resources : Nat → Resources
structure Accumulator where
  slots : List (Option Nat)
  count : Nat
  scope : Nat
  baseHeader : Option Nat
  deriving DecidableEq, Repr
structure Entry where
  index : Nat
  suggested : Nat
  baseHeader : Option Nat
  refreshedSpec : Bool
  blockOptions : Nat
  deriving DecidableEq, Repr
structure Observation where
  state : Option Accumulator
  entries : List Entry
  iterations : List U.Observation
  observedPrefix : Nat
  returnedBlocks : Option (List Nat)

def containsFlag (options flag : Nat) : Bool := options &&& flag == flag
def initial (i : Input) : Accumulator :=
  { slots := List.replicate i.publications.length none, count := 0, scope := i.scope, baseHeader := i.baseHeader }
def next (s : U.State) : Accumulator :=
  { slots := s.slots, count := s.count, scope := s.scope, baseHeader := s.nextBase }
def entry (i : Input) (index : Nat) (a : Accumulator) (p : PublicationInput) : Entry :=
  { index := index, suggested := p.suggestedBlock, baseHeader := a.baseHeader,
    refreshedSpec := index > 0, blockOptions := if i.nullTracer then i.options else i.options ||| 512 }
def iteration (i : Input) (index : Nat) (a : Accumulator) (p : PublicationInput) : U.Input :=
  { publication := p, index := index, suggestedBlocks := i.publications.map (·.suggestedBlock), processedSlots := a.slots,
    originalList := i.originalList, header := i.header, blockNumber := i.blockNumber,
    scope := a.scope, reopenedScope := (i.resources index).reopenedScope,
    readOnly := containsFlag i.options 65, hasTransactions := i.hasTransactions,
    preWarmerPresent := i.preWarmerPresent, prewarmTask := (i.resources index).prewarm,
    cancellation := (i.resources index).cancellation, inclusionSignal := (i.resources index).inclusion }

-- The relation reconstructs every prefix from the preceding independently specified state.
inductive NormalSteps (i : Input) : Nat → Accumulator → List PublicationInput → Observation → Prop
  | done (index : Nat) (a : Accumulator) (filled : a.slots.all Option.isSome = true) :
      NormalSteps i index a []
        { state := some a, entries := [], iterations := [], observedPrefix := a.count,
          returnedBlocks := some (a.slots.filterMap id) }
  | step (index : Nat) (a : Accumulator) (p : PublicationInput) (rest : List PublicationInput)
      (observation : U.Observation) (tail : Observation)
      (leaf : U.normalIteration (iteration i index a p) observation)
      (continuation : NormalSteps i (index + 1) (next (U.normalState (iteration i index a p))) rest tail) :
      NormalSteps i index a (p :: rest)
        { tail with entries := entry i index a p :: tail.entries, iterations := observation :: tail.iterations }

def normalCompletion (i : Input) (o : Observation) : Prop :=
  if i.publications.isEmpty then
    o.state = none ∧ o.entries = [] ∧ o.iterations = [] ∧ o.observedPrefix = 0 ∧ o.returnedBlocks = some []
  else NormalSteps i 0 (initial i) i.publications o

theorem normalSteps_has_return {i : Input} {index : Nat} {a : Accumulator}
    {remaining : List PublicationInput} {o : Observation} (h : NormalSteps i index a remaining o) :
    ∃ blocks, o.returnedBlocks = some blocks := by
  induction h with
  | done => exact ⟨_, rfl⟩
  | step _ _ _ _ _ _ _ _ inductionHypothesis => exact inductionHypothesis

theorem normalCompletion_has_return {i : Input} {o : Observation} (h : normalCompletion i o) :
    ∃ blocks, o.returnedBlocks = some blocks := by
  unfold normalCompletion at h
  split at h
  · exact ⟨[], h.2.2.2.2⟩
  · exact normalSteps_has_return h

end BlockProcessorExtractor.Specification.NormalFiniteBranchCompletion
