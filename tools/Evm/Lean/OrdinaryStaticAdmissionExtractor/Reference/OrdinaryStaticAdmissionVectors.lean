-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import OrdinaryStaticAdmissionExtractor.Reference.OrdinaryStaticAdmissionReference

namespace OrdinaryStaticAdmissionExtractor.Reference.Vectors

open OrdinaryStaticAdmissionExtractor.Reference

private def base : StaticInput :=
  { senderPresent := true
    nonce := 0
    toPresent := true
    dataLength := 0
    transactionType := 0
    authorizationListPresent := false
    authorizationListLength := 0
    txGasLimit := 80_000
    headerGasLimit := 100_000
    headerGasUsed := 30_000
    skipValidation := false
    processorParallel := false
    eip3860Enabled := false
    eip8037Enabled := false
    maxInitCodeSize := 100
    standardValue := 21_000
    standardStateReservoir := 0
    floorValue := 21_000 }

private theorem sender_precedes_all_simultaneous_failures :
    validateStatic { base with
      senderPresent := false
      nonce := uint64Max
      toPresent := false
      dataLength := 101
      eip3860Enabled := true
      transactionType := setCodeType
      authorizationListPresent := false
      eip8037Enabled := true
      standardValue := txGasLimitCap + 1
      floorValue := txGasLimitCap + 1
      txGasLimit := 0 } =
      result .senderNotSpecified .validateStaticSenderAbsent := by native_decide

private theorem nonce_precedes_initcode_and_gas :
    validateStatic { base with
      nonce := uint64Max
      skipValidation := false
      toPresent := false
      dataLength := 101
      eip3860Enabled := true
      txGasLimit := 0 } =
      result .nonceOverflow .validateStaticNonceOverflow := by native_decide

private theorem skip_validation_does_not_disable_initcode :
    validateStatic { base with
      skipValidation := true
      toPresent := false
      dataLength := 101
      eip3860Enabled := true } =
      result .transactionSizeOverMaxInitCodeSize .validateStaticInitcodeOversize := by native_decide

private theorem setcode_creation_precedes_authorization :
    validateStatic { base with
      transactionType := setCodeType
      toPresent := false
      authorizationListPresent := false
      authorizationListLength := 0 } =
      result .malformedTransaction .validateStaticSetCodeCreation := by native_decide

private theorem setcode_requires_nonempty_authorization :
    validateStatic { base with
      transactionType := setCodeType
      authorizationListPresent := false
      authorizationListLength := 0 } =
      result .malformedTransaction .validateStaticSetCodeAuthorization := by native_decide

private theorem cap_precedes_execution_and_floor :
    validateStatic { base with
      eip8037Enabled := true
      txGasLimit := txGasLimitCap
      headerGasLimit := txGasLimitCap
      standardValue := txGasLimitCap + 1
      floorValue := txGasLimitCap + 1 } =
      result .gasLimitBelowIntrinsicGas .validateStaticIntrinsicCap := by native_decide

private theorem execution_floor_minimum_are_distinct :
    validateStatic { base with
      txGasLimit := 99
      standardValue := 100
      floorValue := 100 } =
      result .gasLimitBelowIntrinsicGas .validateStaticExecutionIntrinsic ∧
    validateStatic { base with
      txGasLimit := 150
      standardValue := 100
      floorValue := 200 } =
      result .gasLimitBelowFloorGas .validateStaticFloorIntrinsic ∧
    validateStatic { base with
      txGasLimit := 125
      standardValue := 100
      standardStateReservoir := 50
      floorValue := 100 } =
      result .gasLimitBelowIntrinsicGas .validateGasMinimumIntrinsic := by native_decide

private theorem block_limit_equality_is_allowed :
    validateStatic { base with
      txGasLimit := 100
      headerGasLimit := 100
      headerGasUsed := 0
      standardValue := 100
      floorValue := 100 } =
      result .none .validateGasOk := by native_decide

private theorem eip8037_block_limit_is_after_intrinsic_guards :
    validateStatic { base with
      eip8037Enabled := true
      txGasLimit := 200
      headerGasLimit := 199
      headerGasUsed := 0
      standardValue := 100
      floorValue := 100 } =
      result .blockGasLimitExceeded .validateGasEip8037BlockLimit := by native_decide

private theorem legacy_parallel_allowance_ignores_gas_used :
    validateStatic { base with processorParallel := true } =
      result .none .validateGasOk ∧
    validateStatic { base with processorParallel := false } =
      result .blockGasLimitExceeded .validateGasLegacyBlockLimit := by native_decide

private theorem legacy_header_subtraction_wrap_is_preserved :
    validateStatic { base with
      txGasLimit := 6
      headerGasLimit := 5
      headerGasUsed := 10
      standardValue := 0
      floorValue := 0 } =
      result .none .validateGasOk := by native_decide

private theorem signed_state_cast_wrap_is_preserved :
    validateStatic { base with
      txGasLimit := 100
      headerGasLimit := 100
      headerGasUsed := 0
      standardValue := 100
      standardStateReservoir := -1
      floorValue := 0 } =
      result .none .validateGasOk := by native_decide

private def initSuccess
    (value : Nat)
    (stateReservoir stateGasUsed : Int) : InitializationResult :=
  { outcome := .success
    value
    stateReservoir
    stateGasUsed
    stateGasSpill := 0
    stateGasSpillRefunded := 0 }

private def initFailure : InitializationResult := failureResult

private def policy
    (value : Nat)
    (stateReservoir stateGasUsed : Int) : AvailableGasPolicy :=
  { value
    stateReservoir
    stateGasUsed
    stateGasSpill := 0
    stateGasSpillRefunded := 0 }

private theorem initializer_and_wrapper_matrix :
    calculateAvailableGas
        { gasLimit := 100
          intrinsicExecutionGas := 100
          intrinsicStateGas := 0
          eip8037Enabled := false
          executionGasLimitCap := 0 } =
      { result := result .none .calculateAvailableGasSuccess
        available := policy 0 0 0
        initialization := initSuccess 0 0 0 } ∧
    calculateAvailableGas
        { gasLimit := 1_000
          intrinsicExecutionGas := 100
          intrinsicStateGas := 50
          eip8037Enabled := true
          executionGasLimitCap := 600 } =
      { result := result .none .calculateAvailableGasSuccess
        available := policy 500 350 50
        initialization := initSuccess 500 350 50 } ∧
    calculateAvailableGas
        { gasLimit := 1_000
          intrinsicExecutionGas := 100
          intrinsicStateGas := 50
          eip8037Enabled := false
          executionGasLimitCap := 0 } =
      { result := result .none .calculateAvailableGasSuccess
        available := policy 850 0 50
        initialization := initSuccess 850 0 50 } ∧
    calculateAvailableGas
        { gasLimit := 149
          intrinsicExecutionGas := 100
          intrinsicStateGas := 50
          eip8037Enabled := true
          executionGasLimitCap := 1_000 } =
      { result := result .gasLimitBelowIntrinsicGas .calculateAvailableGasFailure
        available := defaultAvailablePolicy
        initialization := initFailure } ∧
    calculateAvailableGas
        { gasLimit := 0
          intrinsicExecutionGas := 1
          intrinsicStateGas := -1
          eip8037Enabled := true
          executionGasLimitCap := txGasLimitCap } =
      { result := result .none .calculateAvailableGasSuccess
        available := policy 0 0 (-1)
        initialization := initSuccess 0 0 (-1) } := by native_decide

end OrdinaryStaticAdmissionExtractor.Reference.Vectors
