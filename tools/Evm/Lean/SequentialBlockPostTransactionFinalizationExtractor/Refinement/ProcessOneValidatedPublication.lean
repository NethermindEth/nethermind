-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import SequentialBlockPostTransactionFinalizationExtractor.Generated.ProcessOneValidatedPublication
import SequentialBlockPostTransactionFinalizationExtractor.Specification.ProcessOneValidatedPublication

namespace SequentialBlockPostTransactionFinalizationExtractor.Refinement.ProcessOneValidatedPublication

namespace G
export SequentialBlockPostTransactionFinalizationExtractor.Generated.ProcessOneValidatedPublication
  (SourceSite sourceSites sourceClosureSha256 EscapeSite Outcome ExecutionArtifacts PublicationInput
   PublicationState PublicationResult Event boundaryValid validatorContractValid initialState
   copyPostValidation validationEvents copyEvents escape run)
end G

namespace S
export SequentialBlockPostTransactionFinalizationExtractor.Specification.ProcessOneValidatedPublication
  (EscapeSite Outcome ExecutionArtifacts Input State Event Observation publicationFields
   validationPrefix publishedPrefix publicationTrace normalPublication rejectionPrefix rejectedPublication escapedPublication)
end S

def mapEscape : G.EscapeSite → S.EscapeSite
| .unsupportedBoundary => .unsupportedBoundary
| .validator => .validator
| .rejectionCleanup => .rejectionCleanup
| .postValidation => .postValidation
| .receiptStorage => .receiptStorage

def mapOutcome : G.Outcome → S.Outcome
| .completed => .completed
| .rejected => .rejected
| .escaped site => .escaped (mapEscape site)

def mapArtifacts (artifacts : G.ExecutionArtifacts) : S.ExecutionArtifacts :=
  { accountChanges := artifacts.accountChanges
    executionRequests := artifacts.executionRequests
    generatedBlockAccessList := artifacts.generatedBlockAccessList
    encodedBlockAccessList := artifacts.encodedBlockAccessList }

def mapInput (input : G.PublicationInput) : S.Input :=
  { suggestedBlock := input.suggestedBlock
    processedBlock := input.processBlock.processedBlock
    receipts := input.processBlock.receipts
    suggested := mapArtifacts input.suggestedArtifacts
    processed := mapArtifacts input.processedArtifacts
    noValidation := input.noValidation
    storeReceipts := input.storeReceipts
    proposedGeneratedBlockAccessList := input.validator.proposedGeneratedBlockAccessListAfter }

def mapState (state : G.PublicationState) : S.State :=
  { suggestedBlock := state.suggestedBlock
    suggestedArtifacts := mapArtifacts state.suggestedArtifacts
    processedBlock := state.processedBlock
    receipts := state.receipts
    accountChangesDisposed := state.accountChangesDisposed }

def mapEvent : G.Event → S.Event
| .processBlockReturned => .processBlockReturned
| .validationSkipped => .validationSkipped
| .validatorObserved proposed => .validatorObserved proposed
| .validatorAccepted => .validatorAccepted
| .validatorRejected => .validatorRejected
| .accountChangesDisposed => .accountChangesDisposed
| .invalidBlockException => .invalidBlockException
| .accountChangesPublished => .accountChangesPublished
| .executionRequestsPublished => .executionRequestsPublished
| .generatedBlockAccessListPublished => .generatedBlockAccessListPublished
| .encodedBlockAccessListPublished fallback => .encodedBlockAccessListPublished fallback
| .insertDeferred => .insertDeferred
| .returnedProcessedBlockAndReceipts => .returnedProcessedBlockAndReceipts
| .escaped site => .escaped (mapEscape site)

def mapResult (result : G.PublicationResult) : S.Observation :=
  { outcome := mapOutcome result.outcome
    state := result.state.map mapState
    returnedTuple := result.returnedTuple
    events := result.events.map mapEvent }

structure SourceWitness where
  closureSha256 : String
  sites : List G.SourceSite

-- Runtime selection and external-call facts are supplied by a typed adapter. The source
-- witness must match the emitted Roslyn identities; it is not evidence that C# executed.
structure SourceAdapter (source : SourceWitness) (input : G.PublicationInput) : Prop where
  closureIdentity : source.closureSha256 = G.sourceClosureSha256
  siteIdentities : source.sites = G.sourceSites
  exactBase : input.exactBase = true
  standardSequential : input.standardSequential = true
  balDisabled : input.balEnabled = false
  nonparallel : input.parallelExecutionEnabled = false
  processBlockNormalReturn : input.processBlock.normalReturn = true
  noAdditionalEffects : input.noAdditionalBoundaryEffects = true

structure NormalReturnAdapter (source : SourceWitness) (input : G.PublicationInput) : Prop where
  sourceAdapter : SourceAdapter source input
  validatorObservation : G.validatorContractValid input = true
  validatorNormalReturn : input.noValidation = true ∨ input.validator.normalReturn = true
  acceptedOrSkipped : input.noValidation = true ∨ input.validator.accepted = true
  postValidationNormalReturn : input.postValidationNormalReturn = true
  storeNormalReturn : input.storeReceipts = false ∨ input.insertDeferredNormalReturn = true

theorem source_boundary_valid {source : SourceWitness} {input : G.PublicationInput}
    (adapter : SourceAdapter source input) : G.boundaryValid input = true := by
  simp [G.boundaryValid, adapter.exactBase, adapter.standardSequential, adapter.balDisabled,
    adapter.nonparallel, adapter.processBlockNormalReturn, adapter.noAdditionalEffects]

theorem generated_normal_refines_spec (source : SourceWitness) (input : G.PublicationInput)
    (adapter : NormalReturnAdapter source input) :
    (G.run input).outcome = .completed ∧
      S.normalPublication (mapInput input) (mapResult (G.run input)) := by
  have hBoundary := source_boundary_valid adapter.sourceAdapter
  have hContract := adapter.validatorObservation
  have hNormal := adapter.validatorNormalReturn
  have hAccepted := adapter.acceptedOrSkipped
  have hPost := adapter.postValidationNormalReturn
  have hStore := adapter.storeNormalReturn
  cases hNo : input.noValidation <;>
    cases hStoreFlag : input.storeReceipts <;>
    cases hEncoded : input.processedArtifacts.encodedBlockAccessList <;>
    simp_all [G.run, G.initialState, G.copyPostValidation, G.validationEvents, G.copyEvents,
      mapResult, mapOutcome, mapState, mapArtifacts, mapInput, mapEvent,
      S.normalPublication, S.publicationFields, S.publicationTrace, S.publishedPrefix, S.validationPrefix]

structure SourceAttachedRefinement (source : SourceWitness) (input : G.PublicationInput) : Prop where
  closureIdentity : source.closureSha256 = G.sourceClosureSha256
  siteIdentities : source.sites = G.sourceSites
  completed : (G.run input).outcome = .completed
  generatedToReference : S.normalPublication (mapInput input) (mapResult (G.run input))

theorem source_attached_normal_refinement (source : SourceWitness) (input : G.PublicationInput)
    (adapter : NormalReturnAdapter source input) : SourceAttachedRefinement source input := by
  rcases generated_normal_refines_spec source input adapter with ⟨completed, refines⟩
  exact ⟨adapter.sourceAdapter.closureIdentity, adapter.sourceAdapter.siteIdentities, completed, refines⟩

theorem generated_rejection_refines_spec (source : SourceWitness) (input : G.PublicationInput)
    (adapter : SourceAdapter source input)
    (hNo : input.noValidation = false) (hRan : input.validator.ran = true)
    (hNormal : input.validator.normalReturn = true) (hRejected : input.validator.accepted = false)
    (hCleanup : input.rejectionCleanupNormalReturn = true) :
    S.rejectedPublication (mapInput input) (mapResult (G.run input)) := by
  have hBoundary := source_boundary_valid adapter
  simp [G.run, G.validatorContractValid, G.initialState, G.validationEvents,
    mapResult, mapOutcome, mapState, mapArtifacts, mapInput, mapEvent,
    S.rejectedPublication, S.rejectionPrefix, hBoundary, hNo, hRan, hNormal, hRejected, hCleanup]

theorem generated_validator_escape_refines_spec (source : SourceWitness) (input : G.PublicationInput)
    (adapter : SourceAdapter source input)
    (hNo : input.noValidation = false) (hRan : input.validator.ran = true)
    (hNormal : input.validator.normalReturn = false) :
    S.escapedPublication .validator [.processBlockReturned] (mapResult (G.run input)) := by
  have hBoundary := source_boundary_valid adapter
  simp [G.run, G.validatorContractValid, G.escape, mapResult, mapOutcome, mapEscape,
    mapEvent, S.escapedPublication, hBoundary, hNo, hRan, hNormal]

theorem generic_escape_has_no_result (site : G.EscapeSite) (eventPrefix : List G.Event) :
    S.escapedPublication (mapEscape site) (eventPrefix.map mapEvent) (mapResult (G.escape site eventPrefix)) := by
  simp [G.escape, mapResult, mapOutcome, mapEvent, S.escapedPublication]

theorem generated_cleanup_escape_refines_spec (source : SourceWitness) (input : G.PublicationInput)
    (adapter : SourceAdapter source input)
    (hNo : input.noValidation = false) (hRan : input.validator.ran = true)
    (hNormal : input.validator.normalReturn = true) (hRejected : input.validator.accepted = false)
    (hCleanup : input.rejectionCleanupNormalReturn = false) :
    S.escapedPublication .rejectionCleanup (S.rejectionPrefix (mapInput input))
      (mapResult (G.run input)) := by
  have hBoundary := source_boundary_valid adapter
  simp [G.run, G.validatorContractValid, G.validationEvents, G.escape, mapResult, mapOutcome,
    mapEscape, mapInput, mapEvent, S.rejectionPrefix, S.escapedPublication,
    hBoundary, hNo, hRan, hNormal, hRejected, hCleanup]

theorem generated_post_validation_escape_refines_spec (source : SourceWitness) (input : G.PublicationInput)
    (adapter : SourceAdapter source input)
    (hContract : G.validatorContractValid input = true)
    (hNormal : input.noValidation = true ∨ input.validator.normalReturn = true)
    (hAccepted : input.noValidation = true ∨ input.validator.accepted = true)
    (hPost : input.postValidationNormalReturn = false) :
    S.escapedPublication .postValidation (S.validationPrefix (mapInput input))
      (mapResult (G.run input)) := by
  have hBoundary := source_boundary_valid adapter
  cases hNo : input.noValidation <;>
    simp_all [G.run, G.validationEvents, G.escape, mapResult, mapOutcome, mapEscape,
      mapInput, mapEvent, S.validationPrefix, S.escapedPublication]

theorem generated_storage_escape_refines_spec (source : SourceWitness) (input : G.PublicationInput)
    (adapter : SourceAdapter source input)
    (hContract : G.validatorContractValid input = true)
    (hNormal : input.noValidation = true ∨ input.validator.normalReturn = true)
    (hAccepted : input.noValidation = true ∨ input.validator.accepted = true)
    (hPost : input.postValidationNormalReturn = true)
    (hStore : input.storeReceipts = true) (hStoreReturn : input.insertDeferredNormalReturn = false) :
    S.escapedPublication .receiptStorage (S.publishedPrefix (mapInput input) ++ [.insertDeferred])
      (mapResult (G.run input)) := by
  have hBoundary := source_boundary_valid adapter
  cases hNo : input.noValidation <;>
    simp_all [G.run, G.validationEvents, G.copyEvents, G.escape, mapResult, mapOutcome, mapEscape,
      mapArtifacts, mapInput, mapEvent, S.publishedPrefix, S.validationPrefix, S.escapedPublication]

end SequentialBlockPostTransactionFinalizationExtractor.Refinement.ProcessOneValidatedPublication
