-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import BlockProcessorExtractor.Specification.NormalFiniteBranchCompletion

namespace BlockProcessorExtractor.Specification.BlockchainPublication

namespace F
export BlockProcessorExtractor.Specification.NormalFiniteBranchCompletion (Input Observation normalCompletion containsFlag)
end F
structure Input where
  finite : F.Input
  suggestedBlock : Nat
  preparedBlocks : List Nat
  blocksToProcess : List Nat
  blocksResource : Nat
  inputsResource : Nat
  difficulty : Nat → Option Nat
  baseBlock : Option Nat
  headResult : Bool
  warnEnabled : Bool
def readOnly (i : Input) : Bool := F.containsFlag i.finite.options 65
def updateHead (i : Input) : Bool := !F.containsFlag i.finite.options 64
def markProcessed (i : Input) : Bool := F.containsFlag i.finite.options 128
def forceProcessing (i : Input) : Bool := F.containsFlag i.finite.options 2
def suggestedHeader (i : Input) : Nat := i.finite.header i.suggestedBlock
def preparedEntry (i : Input) : Prop :=
  i.finite.originalList = i.inputsResource ∧
  i.blocksToProcess = i.finite.publications.map (·.suggestedBlock) ∧
  if forceProcessing i then i.preparedBlocks = [] ∧ i.blocksToProcess = [i.suggestedBlock]
  else i.preparedBlocks = i.blocksToProcess ∧ i.blocksToProcess ≠ []
inductive Site where
  | stop | stats | head | mark | metrics | disposeBlocks | disposeInputs
  deriving DecidableEq, Repr
inductive Event where
  | branchReturned (blocks : List Nat)
  | errorCleared | stopwatchStopped
  | selectedLast (block : Nat)
  | difficultyAssigned (header : Nat) (value : Option Nat)
  | statsUpdated (blocks : List Nat) (baseBlock : Option Nat)
  | headInvoked (suggestedHeader : Nat) (wereProcessed force : Bool) (preloaded : List Nat)
  | headReturned (result : Bool) | headWarning
  | markInvoked (prepared : List Nat) | markReturned | bestKnownMetricRead
  | returnPrepared (block : Option Nat)
  | disposeBlocksInvoked (resource : Nat) | disposeInputsInvoked (resource : Nat)
  | returned (block : Option Nat) | escaped (site : Site)
  deriving DecidableEq, Repr
structure State where
  lastProcessed : Option Nat
  difficultyWrites : List (Nat × Option Nat)
  headReturn : Option Bool
  markCompleted : Bool
  blocksDisposed : Bool
  inputsDisposed : Bool
  deriving DecidableEq, Repr
structure Observation where
  finite : F.Observation
  state : Option State
  events : List Event
  returnedBlock : Option Nat
  error : Option String

def writes (i : Input) (blocks : List Nat) : List (Nat × Option Nat) :=
  match blocks.getLast? with
  | none => []
  | some last => [(i.finite.header last, i.difficulty (suggestedHeader i))]
def normalState (i : Input) (blocks : List Nat) : State :=
  { lastProcessed := blocks.getLast?, difficultyWrites := writes i blocks,
    headReturn := if updateHead i then some i.headResult else none,
    markCompleted := markProcessed i, blocksDisposed := true, inputsDisposed := true }
def normalTrace (i : Input) (blocks : List Nat) : List Event :=
  [.branchReturned blocks, .errorCleared, .stopwatchStopped] ++
  (match blocks.getLast? with
   | none => []
   | some last => [.selectedLast last, .difficultyAssigned (i.finite.header last) (i.difficulty (suggestedHeader i))]) ++
  (if readOnly i then [] else [.statsUpdated blocks i.baseBlock]) ++
  (if updateHead i then
    [.headInvoked (suggestedHeader i) true false i.preparedBlocks, .headReturned i.headResult] ++
      (if !i.headResult && i.warnEnabled then [.headWarning] else []) else []) ++
  (if markProcessed i then [.markInvoked i.preparedBlocks, .markReturned] else []) ++
  (if readOnly i then [] else [.bestKnownMetricRead]) ++
  [.returnPrepared blocks.getLast?, .disposeBlocksInvoked i.blocksResource,
   .disposeInputsInvoked i.inputsResource, .returned blocks.getLast?]
def normalPublication (i : Input) (o : Observation) : Prop :=
  preparedEntry i ∧ F.normalCompletion i.finite o.finite ∧
  ∃ blocks, o.finite.returnedBlocks = some blocks ∧ o.state = some (normalState i blocks) ∧
    o.returnedBlock = blocks.getLast? ∧ o.error = none ∧ o.events = normalTrace i blocks

inductive Failure where
  | unknownParent | notBetter | invalid (headerHash : Option Nat) (message : String) | escaped
  deriving DecidableEq, Repr
structure FailureInput where
  failure : Failure
  options : Nat
  blocks : List Nat
  hash : Nat → Option Nat
  handlerNormal : Bool
structure FailureObservation where
  returned : Bool
  returnedBlock : Option Nat
  error : Option (Option String)
  invalidEventBlock : Option (Option Nat)
  deletionCalls : Option (List Nat)
  finalStateKnown : Bool
  deriving DecidableEq, Repr
def failureContract (i : FailureInput) (o : FailureObservation) : Prop :=
  o.returnedBlock = none ∧ o.finalStateKnown = false ∧
  if !i.handlerNormal then
    o.returned = false ∧ o.error = none ∧ o.invalidEventBlock = none ∧ o.deletionCalls = none
  else
  match i.failure with
  | .unknownParent | .notBetter =>
      o.returned = true ∧ o.error = some none ∧ o.invalidEventBlock = some none ∧ o.deletionCalls = some []
  | .escaped =>
      o.returned = false ∧ o.error = none ∧ o.invalidEventBlock = none ∧ o.deletionCalls = none
  | .invalid hash message =>
      o.returned = true ∧ o.error = some (some message) ∧
      o.invalidEventBlock = some ((i.blocks.filter (fun block => i.hash block == hash)).head?) ∧
      o.deletionCalls = some (if hash.isSome && !F.containsFlag i.options 65
        then i.blocks.filter (fun block => i.hash block == hash) else [])

end BlockProcessorExtractor.Specification.BlockchainPublication
