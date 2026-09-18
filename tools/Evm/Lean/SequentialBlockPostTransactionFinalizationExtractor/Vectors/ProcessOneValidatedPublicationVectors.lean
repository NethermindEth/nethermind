-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import SequentialBlockPostTransactionFinalizationExtractor.Generated.ProcessOneValidatedPublication
import SequentialBlockPostTransactionFinalizationExtractor.Refinement.ProcessOneValidatedPublication

namespace SequentialBlockPostTransactionFinalizationExtractor.Vectors.ProcessOneValidatedPublicationVectors

open SequentialBlockPostTransactionFinalizationExtractor.Generated.ProcessOneValidatedPublication

def baseInput : PublicationInput where
  suggestedBlock := 1
  processBlock := { normalReturn := true, processedBlock := 2, receipts := 3 }
  suggestedArtifacts := {
    accountChanges := some 11, executionRequests := some 12,
    generatedBlockAccessList := some 13, encodedBlockAccessList := some 14 }
  processedArtifacts := {
    accountChanges := some 21, executionRequests := some 22,
    generatedBlockAccessList := some 23, encodedBlockAccessList := none }
  exactBase := true
  standardSequential := true
  balEnabled := false
  parallelExecutionEnabled := false
  noValidation := false
  storeReceipts := true
  validator := { ran := true, accepted := true, normalReturn := true, proposedGeneratedBlockAccessListAfter := some 13 }
  rejectionCleanupNormalReturn := true
  postValidationNormalReturn := true
  insertDeferredNormalReturn := true
  noAdditionalBoundaryEffects := true

def rejectedInput : PublicationInput :=
  { baseInput with validator := { baseInput.validator with
      accepted := false, proposedGeneratedBlockAccessListAfter := some 24 } }

def noValidationInput : PublicationInput :=
  { baseInput with noValidation := true, storeReceipts := false, validator := { baseInput.validator with ran := false, normalReturn := false, accepted := false } }

theorem normal_returned_tuple : (run baseInput).returnedTuple = some (2, 3) := by decide
theorem normal_copied_artifacts : (run baseInput).state.map (·.suggestedArtifacts) =
    some { accountChanges := some 21, executionRequests := some 22, generatedBlockAccessList := some 23, encodedBlockAccessList := some 14 } := by decide

theorem normal_event_order : (run baseInput).events =
    [.processBlockReturned, .validatorObserved (some 13), .validatorAccepted,
     .accountChangesPublished, .executionRequestsPublished, .generatedBlockAccessListPublished,
     .encodedBlockAccessListPublished true, .insertDeferred, .returnedProcessedBlockAndReceipts] := by decide

theorem no_validation_completes : (run noValidationInput).outcome = .completed := by decide
theorem no_validation_event_order : (run noValidationInput).events =
    [.processBlockReturned, .validationSkipped,
     .accountChangesPublished, .executionRequestsPublished, .generatedBlockAccessListPublished,
     .encodedBlockAccessListPublished true, .returnedProcessedBlockAndReceipts] := by decide

theorem rejection_outcome : (run rejectedInput).outcome = .rejected := by decide
theorem rejection_retains_validator_bal : (run rejectedInput).state.map (·.suggestedArtifacts) =
    some { accountChanges := some 11, executionRequests := some 12, generatedBlockAccessList := some 24, encodedBlockAccessList := some 14 } := by decide
theorem rejection_event_order : (run rejectedInput).events =
    [.processBlockReturned, .validatorObserved (some 24), .validatorRejected,
     .accountChangesDisposed, .invalidBlockException] := by decide
theorem rejection_has_no_tuple : (run rejectedInput).returnedTuple = none := by decide

theorem encoded_bal_prefers_processed : (run { baseInput with processedArtifacts :=
    { baseInput.processedArtifacts with encodedBlockAccessList := some 25 } }).state.map
    (·.suggestedArtifacts.encodedBlockAccessList) = some (some 25) := by decide

theorem validator_escape : (run { baseInput with validator :=
    { baseInput.validator with normalReturn := false } }).outcome = .escaped .validator := by decide
theorem cleanup_escape : (run { rejectedInput with rejectionCleanupNormalReturn := false }).outcome =
    .escaped .rejectionCleanup := by decide
theorem post_validation_escape : (run { baseInput with postValidationNormalReturn := false }).outcome =
    .escaped .postValidation := by decide
theorem storage_escape : (run { baseInput with insertDeferredNormalReturn := false }).outcome =
    .escaped .receiptStorage := by decide
theorem storage_escape_has_no_final_state : (run { baseInput with insertDeferredNormalReturn := false }).state = none := by decide
theorem disabled_storage_skips_hook : (run { baseInput with storeReceipts := false, insertDeferredNormalReturn := false }).outcome =
    .completed := by decide

theorem invalid_skipped_validator_observation : (run { noValidationInput with validator :=
    { noValidationInput.validator with proposedGeneratedBlockAccessListAfter := some 999 } }).outcome =
    .escaped .unsupportedBoundary := by decide
theorem additional_effect_rejected : (run { baseInput with noAdditionalBoundaryEffects := false }).outcome =
    .escaped .unsupportedBoundary := by decide
theorem bal_rejected : (run { baseInput with balEnabled := true }).outcome = .escaped .unsupportedBoundary := by decide
theorem parallel_rejected : (run { baseInput with parallelExecutionEnabled := true }).outcome =
    .escaped .unsupportedBoundary := by decide

end SequentialBlockPostTransactionFinalizationExtractor.Vectors.ProcessOneValidatedPublicationVectors
