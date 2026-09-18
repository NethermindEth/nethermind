-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import SequentialBlockPostTransactionFinalizationExtractor.Specification.ProcessOneValidatedPublication

namespace BlockProcessorExtractor.Specification.BranchAcceptedIteration

namespace U
export SequentialBlockPostTransactionFinalizationExtractor.Specification.ProcessOneValidatedPublication
  (Input Observation normalPublication)
end U

structure Input where
  publication : U.Input
  index : Nat
  suggestedBlocks : List Nat
  processedSlots : List (Option Nat)
  originalList : Nat
  header : Nat → Nat
  blockNumber : Nat → Nat
  scope : Nat
  reopenedScope : Nat
  readOnly : Bool
  hasTransactions : Nat → Bool
  preWarmerPresent : Bool
  prewarmTask : Option Nat
  cancellation : Option Nat
  inclusionSignal : Bool
inductive Site where
  | cancel | inclusion | queueClear | waitPrewarm | commitTree | blockEvent
  | checkpointDispose | checkpointReopen | reset | scheduleHashes
  | unsubscribe | disposeFinally | completionEvent
  deriving DecidableEq, Repr

inductive Event where
  | acceptedPublication (block receipts : Nat)
  | cancelInvoked (source : Option Nat)
  | slotAssigned (index block : Nat)
  | inclusionChecked (processed suggested : Nat)
  | processedSignalAssigned (block : Nat) (signal : Bool)
  | suggestedSignalAssigned (block : Nat) (signal : Bool)
  | clearQueueInvoked (task : Nat)
  | clearInlineInvoked | noClearWork
  | prewarmWaitInvoked (task : Option Nat)
  | preCommitInvoked (header : Nat)
  | commitTreeInvoked (scope blockNumber : Nat)
  | successfulPrefixAssigned (count : Nat)
  | blockEventEvaluated (block receipts : Nat)
  | checkpointDisposeInvoked (scope : Nat)
  | checkpointReopenInvoked (header : Nat)
  | nextBaseAssigned (header : Nat)
  | prefetchCleared | resetInvoked
  | hashScheduleInvoked (suggested : Nat)
  | returnPrepared | unsubscribeInvoked
  | finalDisposeInvoked (scope : Nat)
  | completionEvaluated (originalList count : Nat)
  | returnedArray | nextIteration
  | escaped (site : Site)
  deriving DecidableEq, Repr

structure State where
  processedBlock : Nat
  receipts : Nat
  slots : List (Option Nat)
  count : Nat
  processedSignal : Option Bool
  suggestedSignal : Option Bool
  scope : Nat
  scopeClosed : Bool
  nextBase : Option Nat
  prewarmTask : Option Nat
  cancellation : Option Nat
  prefetch : Option Nat
  commitCompleted : Bool
  resetCompleted : Bool
  deriving DecidableEq, Repr

inductive Outcome where
  | running | nextIteration | returnedBranch | outsideBoundary | escaped (site : Site)
  deriving DecidableEq, Repr

structure Observation where
  publication : U.Observation
  outcome : Outcome
  state : Option State
  observedPrefix : Nat
  events : List Event
  returnedSlots : Option (List (Option Nat))
  deriving DecidableEq, Repr

def terminal (i : Input) : Bool := i.index + 1 == i.suggestedBlocks.length
def suggestedHeader (i : Input) : Nat := i.header i.publication.suggestedBlock
def suggestedNumber (i : Input) : Nat := i.blockNumber (suggestedHeader i)
def checkpoint (i : Input) : Bool :=
  !i.readOnly && i.index > 0 && i.index + 1 < i.suggestedBlocks.length && i.index % 64 == 0
def finalScope (i : Input) : Nat := if checkpoint i then i.reopenedScope else i.scope
def signal (i : Input) : Bool := i.publication.noValidation || i.inclusionSignal
def filledSlots (i : Input) : List (Option Nat) :=
  i.processedSlots.set i.index (some i.publication.processedBlock)

def normalState (i : Input) : State :=
  { processedBlock := i.publication.processedBlock, receipts := i.publication.receipts,
    slots := filledSlots i, count := i.index + 1,
    processedSignal := some (signal i), suggestedSignal := some (signal i),
    scope := finalScope i, scopeClosed := terminal i, nextBase := some (i.header i.publication.processedBlock),
    prewarmTask := none, cancellation := none, prefetch := none,
    commitCompleted := true, resetCompleted := true }

-- This is an independent ordered observation contract, not an interpreter of generated operations.
def normalTrace (i : Input) : List Event :=
  [.acceptedPublication i.publication.processedBlock i.publication.receipts,
   .cancelInvoked i.cancellation, .slotAssigned i.index i.publication.processedBlock] ++
  (if i.publication.noValidation then [] else
    [.inclusionChecked i.publication.processedBlock i.publication.suggestedBlock]) ++
  [.processedSignalAssigned i.publication.processedBlock (signal i),
   .suggestedSignalAssigned i.publication.suggestedBlock (signal i)] ++
  (match i.prewarmTask with
   | some task => [.clearQueueInvoked task]
   | none => if i.preWarmerPresent then [.clearInlineInvoked] else [.noClearWork]) ++
  [.prewarmWaitInvoked i.prewarmTask, .preCommitInvoked (suggestedHeader i), .commitTreeInvoked i.scope (suggestedNumber i),
   .successfulPrefixAssigned (i.index + 1)] ++
  (if i.readOnly then [] else [.blockEventEvaluated i.publication.processedBlock i.publication.receipts]) ++
  (if checkpoint i then [.checkpointDisposeInvoked i.scope, .checkpointReopenInvoked (suggestedHeader i)] else []) ++
  [.nextBaseAssigned (i.header i.publication.processedBlock), .prefetchCleared, .resetInvoked] ++
  (if i.hasTransactions i.publication.suggestedBlock then [.hashScheduleInvoked i.publication.suggestedBlock] else []) ++
  (if terminal i then [.returnPrepared, .unsubscribeInvoked, .finalDisposeInvoked (finalScope i),
      .completionEvaluated i.originalList (i.index + 1), .returnedArray]
   else [.nextIteration])

def normalIteration (i : Input) (o : Observation) : Prop :=
  U.normalPublication i.publication o.publication ∧
  o.outcome = (if terminal i then .returnedBranch else .nextIteration) ∧
  o.state = some (normalState i) ∧ o.observedPrefix = i.index + 1 ∧
  o.events = normalTrace i ∧
  o.returnedSlots = (if terminal i then some (filledSlots i) else none)

def unknownEscape (site : Site) (o : Observation) : Prop :=
  o.outcome = .escaped site ∧ o.state = none ∧ o.returnedSlots = none

end BlockProcessorExtractor.Specification.BranchAcceptedIteration
