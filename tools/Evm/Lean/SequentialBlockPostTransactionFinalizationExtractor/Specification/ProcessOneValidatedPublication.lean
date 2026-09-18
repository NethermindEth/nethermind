-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

-- Independent relational contract; no generated-kernel import or executable run function.
namespace SequentialBlockPostTransactionFinalizationExtractor.Specification.ProcessOneValidatedPublication

inductive EscapeSite where
| unsupportedBoundary | validator | rejectionCleanup | postValidation | receiptStorage
  deriving DecidableEq, Repr

inductive Outcome where
| completed | rejected | escaped (site : EscapeSite)
  deriving DecidableEq, Repr

structure ExecutionArtifacts where
  accountChanges : Option Nat
  executionRequests : Option Nat
  generatedBlockAccessList : Option Nat
  encodedBlockAccessList : Option Nat
  deriving DecidableEq, Repr

structure Input where
  suggestedBlock : Nat
  processedBlock : Nat
  receipts : Nat
  suggested : ExecutionArtifacts
  processed : ExecutionArtifacts
  noValidation : Bool
  storeReceipts : Bool
  proposedGeneratedBlockAccessList : Option Nat
  deriving DecidableEq, Repr

structure State where
  suggestedBlock : Nat
  suggestedArtifacts : ExecutionArtifacts
  processedBlock : Nat
  receipts : Nat
  accountChangesDisposed : Bool
  deriving DecidableEq, Repr

inductive Event where
| processBlockReturned | validationSkipped
| validatorObserved (proposedGeneratedBlockAccessList : Option Nat)
| validatorAccepted | validatorRejected
| accountChangesDisposed | invalidBlockException
| accountChangesPublished | executionRequestsPublished | generatedBlockAccessListPublished
| encodedBlockAccessListPublished (usedFallback : Bool)
| insertDeferred | returnedProcessedBlockAndReceipts
| escaped (site : EscapeSite)
  deriving DecidableEq, Repr

structure Observation where
  outcome : Outcome
  state : Option State
  returnedTuple : Option (Nat × Nat)
  events : List Event
  deriving DecidableEq, Repr

def publicationFields (input : Input) (fields : ExecutionArtifacts) : Prop :=
  fields.accountChanges = input.processed.accountChanges ∧
  fields.executionRequests = input.processed.executionRequests ∧
  fields.generatedBlockAccessList = input.processed.generatedBlockAccessList ∧
  (match input.processed.encodedBlockAccessList with
   | some value => fields.encodedBlockAccessList = some value
   | none => fields.encodedBlockAccessList = input.suggested.encodedBlockAccessList)

def validationPrefix (input : Input) : List Event :=
  [.processBlockReturned] ++
    (if input.noValidation then [.validationSkipped]
     else [.validatorObserved input.proposedGeneratedBlockAccessList, .validatorAccepted])

def publishedPrefix (input : Input) : List Event :=
  validationPrefix input ++
    [.accountChangesPublished, .executionRequestsPublished, .generatedBlockAccessListPublished,
      .encodedBlockAccessListPublished input.processed.encodedBlockAccessList.isNone]

def publicationTrace (input : Input) : List Event :=
  publishedPrefix input ++
    (if input.storeReceipts then [.insertDeferred] else []) ++
    [.returnedProcessedBlockAndReceipts]

def normalPublication (input : Input) (observation : Observation) : Prop :=
  observation.outcome = .completed ∧
  (∃ state, observation.state = some state ∧
    state.suggestedBlock = input.suggestedBlock ∧
    publicationFields input state.suggestedArtifacts ∧
    state.processedBlock = input.processedBlock ∧ state.receipts = input.receipts ∧
    state.accountChangesDisposed = false) ∧
  observation.returnedTuple = some (input.processedBlock, input.receipts) ∧
  observation.events = publicationTrace input

def rejectionPrefix (input : Input) : List Event :=
  [.processBlockReturned, .validatorObserved input.proposedGeneratedBlockAccessList, .validatorRejected]

def rejectedPublication (input : Input) (observation : Observation) : Prop :=
  input.noValidation = false ∧ observation.outcome = .rejected ∧
  (∃ state, observation.state = some state ∧
    state.suggestedBlock = input.suggestedBlock ∧
    state.suggestedArtifacts.accountChanges = input.suggested.accountChanges ∧
    state.suggestedArtifacts.executionRequests = input.suggested.executionRequests ∧
    state.suggestedArtifacts.generatedBlockAccessList = input.proposedGeneratedBlockAccessList ∧
    state.suggestedArtifacts.encodedBlockAccessList = input.suggested.encodedBlockAccessList ∧
    state.processedBlock = input.processedBlock ∧ state.receipts = input.receipts ∧
    state.accountChangesDisposed = true) ∧
  observation.returnedTuple = none ∧
  observation.events = rejectionPrefix input ++ [.accountChangesDisposed, .invalidBlockException]

-- External exceptions carry no invented final state. Disposal or diagnostics may escape
-- before InvalidBlockException is constructed, so those cases are separate outcomes.
def escapedPublication (site : EscapeSite) (eventPrefix : List Event) (observation : Observation) : Prop :=
  observation.outcome = .escaped site ∧ observation.state = none ∧
  observation.returnedTuple = none ∧ observation.events = eventPrefix ++ [.escaped site]

end SequentialBlockPostTransactionFinalizationExtractor.Specification.ProcessOneValidatedPublication
