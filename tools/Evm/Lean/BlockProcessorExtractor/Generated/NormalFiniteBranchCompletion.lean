-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import BlockProcessorExtractor.Generated.BranchAcceptedIteration

namespace BlockProcessorExtractor.Generated.NormalFiniteBranchCompletion

namespace U
export BlockProcessorExtractor.Generated.BranchAcceptedIteration (Input State Result Outcome Site run)
end U
abbrev PublicationInput := SequentialBlockPostTransactionFinalizationExtractor.Generated.ProcessOneValidatedPublication.PublicationInput

def acceptanceState : String := "source-admitted"
def sourceClosureSha256 : String := "71b9785c50347536614e595dd9d3ddf1cc6c3a8e36062acd2831f30510fcaa8f"
def semanticIrSha256 : String := "ebccc9751bc3f65dc577ad78e9202497b9eeb38d410ec43c0c5d8808e7a15816"
def loopIncrement : Nat := 1
def containsFlag (options flag : Nat) : Bool := options &&& flag == flag

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
  ownedScope : Bool
  scope : Nat
  baseHeader : Option Nat
  header : Nat → Nat
  blockNumber : Nat → Nat
  hasTransactions : Nat → Bool
  nullTracer : Bool
  preWarmerPresent : Bool
  resources : Nat → Resources
  preludeNormal : Nat → Bool
  normal : Nat → U.Site → Bool

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

inductive Outcome where
  | completed | outsideBoundary | escaped (index : Nat)
  deriving DecidableEq, Repr

structure Result where
  outcome : Outcome
  state : Option Accumulator
  entries : List Entry
  iterations : List U.Result
  observedPrefix : Nat
  returnedBlocks : Option (List Nat)
  deriving DecidableEq, Repr

def initial (i : Input) : Accumulator :=
  { slots := List.replicate i.publications.length none, count := 0, scope := i.scope, baseHeader := i.baseHeader }
def next (s : U.State) : Accumulator :=
  { slots := s.slots, count := s.count, scope := s.scope, baseHeader := s.nextBase }
def suggested (i : Input) : List Nat := i.publications.map (·.suggestedBlock)
def entry (i : Input) (index : Nat) (a : Accumulator) (p : PublicationInput) : Entry :=
  { index := index, suggested := p.suggestedBlock, baseHeader := a.baseHeader,
    refreshedSpec := index > 0, blockOptions := if i.nullTracer then i.options else i.options ||| 512 }
def iteration (i : Input) (index : Nat) (a : Accumulator) (p : PublicationInput) : U.Input :=
  { publication := p, route := .normalSequential, ownedScope := i.ownedScope,
    index := index, prefixCount := a.count, suggestedBlocks := suggested i, processedSlots := a.slots,
    originalList := i.originalList, header := i.header, blockNumber := i.blockNumber,
    scope := a.scope, reopenedScope := (i.resources index).reopenedScope,
    readOnly := containsFlag i.options 65, hasTransactions := i.hasTransactions,
    preWarmerPresent := i.preWarmerPresent, prewarmTask := (i.resources index).prewarm,
    cancellation := (i.resources index).cancellation, prefetch := (i.resources index).prefetch,
    inclusionSignal := (i.resources index).inclusion, normal := i.normal index }

def domain (i : Input) : Bool :=
  i.options < 4294967296 && i.publications.length ≤ 2147483647 &&
  (i.publications.isEmpty || i.ownedScope) &&
  i.publications.all (fun p => p.noValidation == containsFlag i.options 8 && p.storeReceipts == containsFlag i.options 4)

def refused (index : Nat) : Result :=
  { outcome := .outsideBoundary, state := none, entries := [], iterations := [], observedPrefix := index, returnedBlocks := none }
def escaped (index successfulPrefix : Nat) (frames : List U.Result) : Result :=
  { outcome := .escaped index, state := none, entries := [], iterations := frames, observedPrefix := successfulPrefix, returnedBlocks := none }
def finish (a : Accumulator) : Result :=
  if a.slots.all Option.isSome then
    { outcome := .completed, state := some a, entries := [], iterations := [], observedPrefix := a.count,
      returnedBlocks := some (a.slots.filterMap id) }
  else refused a.count
def prepend (e : Entry) (r : U.Result) (tail : Result) : Result :=
  { tail with entries := e :: tail.entries, iterations := r :: tail.iterations }

def complete (i : Input) : Nat → Accumulator → List PublicationInput → Result
  | _, a, [] => finish a
  | index, a, p :: rest =>
    if !i.preludeNormal index then escaped index a.count [] else
    let r := U.run (iteration i index a p)
    match r.state with
    | none =>
      let failure := if r.outcome == .outsideBoundary then refused r.observedPrefix
        else escaped index r.observedPrefix [r]
      { failure with entries := [entry i index a p], iterations := [r] }
    | some s =>
      if (r.outcome == .nextIteration && !rest.isEmpty) || (r.outcome == .returnedBranch && rest.isEmpty) then
        prepend (entry i index a p) r (complete i (index + loopIncrement) (next s) rest)
      else { refused r.observedPrefix with entries := [entry i index a p], iterations := [r] }

def emptyResult : Result :=
  { outcome := .completed, state := none, entries := [], iterations := [], observedPrefix := 0, returnedBlocks := some [] }
def run (i : Input) : Result :=
  if !domain i then refused 0
  else if i.publications.isEmpty then emptyResult
  else complete i 0 (initial i) i.publications

end BlockProcessorExtractor.Generated.NormalFiniteBranchCompletion
