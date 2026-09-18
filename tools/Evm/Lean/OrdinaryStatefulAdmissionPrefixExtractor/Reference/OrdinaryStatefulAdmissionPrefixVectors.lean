-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import OrdinaryStatefulAdmissionPrefixExtractor.Refinement.OrdinaryStatefulAdmissionPrefix

namespace OrdinaryStatefulAdmissionPrefixExtractor.Reference.Vectors

open OrdinaryStatefulAdmissionPrefixExtractor.Generated

private def base : Input :=
  { grammarSemanticsAuditPassed := true
    staticError := .none
    tx := { sender := some 7
            signaturePresent := true
            isMessageCall := false
            txType := 0
            isSystem := false
            isServiceTransaction := false
            supports1559 := false
            isFree := false
            supportsBlobs := false
            gasLimit := 21_000
            nonce := 0
            maxFeePerGas := 3
            maxPriorityFeePerGas := 3
            value := 0
            maxFeePerBlobGas := some 0
            blobVersionedHashes := none }
    world := { suppliedSenderAccountExists := true, effectiveSenderAccountExists := true, recoveredSenderAccountExists := true,
               effectiveSenderInvalidContract := false, effectiveSenderBalance := 100_000, effectiveSenderNonce := 0,
               createAccountSucceeds := true, standardMainnetWorldState := true }
    spec := { eip658Enabled := true, eip1559Enabled := false, eip2780Enabled := false }
    header := { baseFeePerGas := 0 }
    options := { raw := 0 }
    skipSenderCodeCheck := false
    preIntrinsicSender := some 7
    preIntrinsicSenderAccountExists := true
    preIntrinsicRecoveredSender := some 7
    preIntrinsicRecoveredSenderAccountExists := true
    recoveredSender := some 7
    blobOracle := { feePerBlobGasCalculationSucceeds := true,
                    blobBaseFeeCalculationSucceeds := true, feePerBlobGas := 0, blobBaseFee := 0 }
    logger := { debugEnabled := true, warnEnabled := true } }

example : ordinaryStandardInput base := by
  simp [ordinaryStandardInput, inputAdapterCoherent, transactionSnapshotCoherent,
    transactionTypeCoherent, blobProjectionCoherent, standardMainnetWorldStateCoherent,
    headerSpecAndStaticCoherent, preIntrinsicEip2780Coherent, sourceGrammarSemanticsCoherent,
    blobFieldsCoherent, base,
    preIntrinsicRecoveryApplies, postPreIntrinsicSender, postPreIntrinsicSenderAccountExists,
    recoveryUsesChangedSenderFacts, effectiveAbsentFacts, suppliedEffectiveFactsConsistent, executionOptionsIntLimit,
    executionOptionSkipValidation, executionOptionBuildUp]
  native_decide

example : ¬ headerSpecAndStaticCoherent { base with tx := { base.tx with gasLimit := 20_999 } } := by
  simp [headerSpecAndStaticCoherent, base]

example : ¬ ordinaryStandardInput { base with options := { raw := executionOptionSkipValidation } } := by
  simp [ordinaryStandardInput, base, executionOptionSkipValidation]

example : ¬ ordinaryStandardInput { base with options := { raw := executionOptionBuildUp } } := by
  simp [ordinaryStandardInput, base, executionOptionBuildUp]

example : ¬ ordinaryStandardInput { base with options := { raw := executionOptionsIntLimit } } := by
  simp [ordinaryStandardInput, base, executionOptionsIntLimit]

example : ¬ ordinaryStandardInput { base with
    tx := { base.tx with supportsBlobs := true, txType := 3, maxFeePerBlobGas := none } } := by
  simp [ordinaryStandardInput, inputAdapterCoherent, transactionSnapshotCoherent,
    transactionTypeCoherent, blobProjectionCoherent, standardMainnetWorldStateCoherent,
    headerSpecAndStaticCoherent, preIntrinsicEip2780Coherent, blobFieldsCoherent, base]

example :
    let output := run { base with
      tx := { base.tx with supportsBlobs := true, txType := 3, maxFeePerBlobGas := none } }
    output.outcome = .threw .malformedBlobFields ∧
      output.trace = [.validateStatic, .calculateEffectiveGasPrice, .updateMetrics,
        .recoverSenderIfNeeded, .validateSender, .buyGas] := by native_decide

example : (run { base with
    staticError := .staticAdmissionFailure
    tx := { base.tx with supportsBlobs := true, txType := 3, maxFeePerBlobGas := none } }).outcome =
    .returned .validateStatic .staticAdmissionFailure := by native_decide

example : preIntrinsicEip2780Coherent { base with
    spec := { base.spec with eip2780Enabled := true }
    tx := { base.tx with sender := some 9, isMessageCall := true, signaturePresent := true }
    preIntrinsicSender := none
    preIntrinsicSenderAccountExists := false
    preIntrinsicRecoveredSender := some 9
    preIntrinsicRecoveredSenderAccountExists := true } := by
  simp [preIntrinsicEip2780Coherent, preIntrinsicRecoveryApplies, postPreIntrinsicSender,
    postPreIntrinsicSenderAccountExists, base]

example :
    let input := { base with
      options := { raw := executionOptionSkipValidation }
      tx := { base.tx with signaturePresent := false }
      world := { base.world with suppliedSenderAccountExists := false, effectiveSenderAccountExists := false,
                                 effectiveSenderBalance := 999, effectiveSenderNonce := 55,
                                 standardMainnetWorldState := false } }
    let state := initialState input
    (createRecoveredAccount input state true).world.effectiveSenderBalance = 999 ∧
      (createRecoveredAccount input state true).world.effectiveSenderNonce = 55 := by native_decide

example : (run { base with staticError := .staticAdmissionFailure }).outcome =
    .returned .validateStatic .staticAdmissionFailure := by native_decide

example :
    let output := run { base with tx := { base.tx with sender := none, signaturePresent := false },
                                  world := { base.world with suppliedSenderAccountExists := false } }
    output.outcome = .threw .recoverSender ∧
    output.logs = [.recoveryAttempt, .senderAccountDoesNotExist] := by native_decide

example :
    let output := run { base with
      world := { base.world with suppliedSenderAccountExists := false }
      recoveredSender := none }
    output.outcome = .threw .recoverSender ∧ output.transaction.sender = none ∧
    output.world.accountCreated = false ∧
    output.logs = [.recoveryAttempt, .recoveryChangedSender] := by native_decide

example :
    let output := run { base with
      options := { raw := executionOptionSkipValidation + executionOptionWarmup }
      world := { base.world with suppliedSenderAccountExists := false, effectiveSenderAccountExists := false,
                                       effectiveSenderBalance := 999, effectiveSenderNonce := 55,
                                       createAccountSucceeds := true }
      tx := { base.tx with signaturePresent := false, nonce := 0 } }
    output.outcome = .continue ∧ output.world.accountCreated = true ∧
    output.world.effectiveSenderBalance = 0 ∧ output.world.effectiveSenderNonce = 1 ∧
    output.logs = [.recoveryAttempt, .senderAccountDoesNotExist] := by native_decide

example :
    let output := run { base with
      options := { raw := executionOptionSkipValidation }
      world := { base.world with suppliedSenderAccountExists := false, createAccountSucceeds := false }
      tx := { base.tx with sender := none, signaturePresent := false } }
    output.outcome = .threw .createAccount ∧ output.world.accountCreated = false ∧
    output.logs = [.recoveryAttempt, .senderAccountDoesNotExist] := by native_decide

example :
    let output := run { base with
      options := { raw := executionOptionCommit }
      world := { base.world with suppliedSenderAccountExists := false, effectiveSenderAccountExists := false,
                                 recoveredSenderAccountExists := false, effectiveSenderBalance := 0 }
      tx := { base.tx with maxFeePerGas := 0, maxPriorityFeePerGas := 0 }
      recoveredSender := some 8 }
    output.outcome = .threw .setNonceAbsentAccount ∧ output.transaction.sender = some 8 ∧
      output.world.accountCreated = false ∧ output.world.effectiveSenderNonce = 0 := by native_decide

example :
    let output := run { base with
      world := { base.world with suppliedSenderAccountExists := false, effectiveSenderInvalidContract := true }
      options := { raw := executionOptionRestore }
      tx := { base.tx with signaturePresent := false } }
    output.outcome = .returned .validateSender .senderHasDeployedCode ∧
    output.world.accountCreated = true ∧
    output.deleteCallerAccount = true ∧
    output.journal.resetCalled = true ∧
    output.journal.resetBlockChangesFalse = true := by native_decide

example : (run { base with
    spec := { base.spec with eip1559Enabled := true }
    header := { base.header with baseFeePerGas := 4 }
    tx := { base.tx with supports1559 := true, maxFeePerGas := 3 } }).outcome =
    .returned .buyGasPremiumBelowBaseFee .maxFeePerGasBelowBaseFee := by native_decide

example : (run { base with
    tx := { base.tx with gasLimit := uint64Max, maxPriorityFeePerGas := uint256Max } }).outcome =
    .returned .buyGasReservedPaymentOverflow .insufficientMaxFeePerGasForSenderBalance := by native_decide

example :
    let output := run { base with tx := { base.tx with gasLimit := 2, maxPriorityFeePerGas := uint256Max } }
    output.outcome = .returned .buyGasReservedPaymentOverflow .insufficientMaxFeePerGasForSenderBalance ∧
    output.senderReservedGasPayment = uint256Max - 1 := by native_decide

example :
    let output := run { base with
      world := { base.world with effectiveSenderBalance := 50 }
      options := { raw := executionOptionWarmup }
      tx := { base.tx with gasLimit := 10, maxPriorityFeePerGas := 10 } }
    output.outcome = .continue ∧ output.world.effectiveSenderBalance = 0 ∧ output.world.effectiveSenderNonce = 1 := by native_decide

example :
    let output := run { base with
      tx := { base.tx with supportsBlobs := true, txType := 3, maxFeePerBlobGas := some 1 }
      blobOracle := { feePerBlobGasCalculationSucceeds := true,
                      blobBaseFeeCalculationSucceeds := true, feePerBlobGas := 2, blobBaseFee := 3 } }
    output.outcome = .returned .buyGasBlobFeeCapBelowBaseFee .insufficientSenderBalance ∧
    output.blobBaseFee = 3 := by native_decide

example :
    let output := run { base with
      tx := { base.tx with supportsBlobs := true, txType := 3, maxFeePerBlobGas := some 1 }
      blobOracle := { feePerBlobGasCalculationSucceeds := false,
                      blobBaseFeeCalculationSucceeds := true, feePerBlobGas := 0, blobBaseFee := 9 } }
    output.outcome = .returned .buyGasBlobFeeCalculationOverflow .insufficientMaxFeePerGasForSenderBalance ∧
    output.blobBaseFee = 0 := by native_decide

example :
    let output := run { base with
      tx := { base.tx with supportsBlobs := true, txType := 3, maxFeePerBlobGas := some 1 }
      blobOracle := { feePerBlobGasCalculationSucceeds := true,
                      blobBaseFeeCalculationSucceeds := false, feePerBlobGas := 0, blobBaseFee := 9 } }
    output.outcome = .returned .buyGasBlobFeeCalculationOverflow .insufficientMaxFeePerGasForSenderBalance ∧
    output.blobBaseFee = 9 := by native_decide

example :
    let output := run { base with
      tx := { base.tx with supportsBlobs := true, txType := 3, gasLimit := 1, maxPriorityFeePerGas := 1, maxFeePerBlobGas := some 1 }
      blobOracle := { feePerBlobGasCalculationSucceeds := true,
                      blobBaseFeeCalculationSucceeds := true, feePerBlobGas := 0, blobBaseFee := uint256Max } }
    output.outcome = .returned .buyGasBlobPaymentOverflow .insufficientMaxFeePerGasForSenderBalance ∧
    output.senderReservedGasPayment = 0 := by native_decide

example : blobGas { base.tx with blobVersionedHashes := some [some 1] } = gasPerBlob := by native_decide

example : u64 ((uint64Modulus + 1) * gasPerBlob) = gasPerBlob := by native_decide

example :
    let output := run base
    output.outcome = .continue ∧ output.world.effectiveSenderBalance = 37_000 ∧ output.world.effectiveSenderNonce = 1 ∧
    output.trace.length = 7 := by native_decide

example :
    let output := run { base with
      options := { raw := executionOptionSkipValidation }
      world := { base.world with effectiveSenderNonce := uint64Max }
      tx := { base.tx with nonce := 0 } }
    output.outcome = .continue ∧ output.world.effectiveSenderNonce = 0 := by native_decide

end OrdinaryStatefulAdmissionPrefixExtractor.Reference.Vectors
