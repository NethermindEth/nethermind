-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import OrdinaryStaticAdmissionExtractor.Generated.OrdinaryStaticAdmissionKernel
import OrdinaryStaticAdmissionExtractor.Reference.OrdinaryStaticAdmissionReference
import Eip803x.Generated.TransactionGasInitializationKernel
import Eip803x.Refinement.TransactionGasInitialization
import Lean.Elab.Tactic.Omega

namespace OrdinaryStaticAdmissionExtractor.Refinement

open Generated
open Reference

def FitsUInt64 (value : Nat) : Prop := value ≤ Generated.uint64Max
def FitsInt64 (value : Int) : Prop := Generated.int64Min ≤ value ∧ value ≤ Generated.int64Max

def SourceWidthInitialization (input : Generated.InitializationInput) : Prop :=
  FitsUInt64 input.gasLimit ∧
  FitsUInt64 input.intrinsicExecutionGas ∧
  FitsInt64 input.intrinsicStateGas ∧
  FitsUInt64 input.executionGasLimitCap

def SourceWidthStatic (input : Generated.StaticInput) : Prop :=
  FitsUInt64 input.nonce ∧
  input.dataLength ≤ Generated.intMax ∧
  input.transactionType ≤ Generated.uint8Max ∧
  input.authorizationListLength ≤ Generated.intMax ∧
  FitsUInt64 input.txGasLimit ∧
  FitsUInt64 input.headerGasLimit ∧
  FitsUInt64 input.headerGasUsed ∧
  FitsInt64 input.maxInitCodeSize ∧
  FitsUInt64 input.standardValue ∧
  FitsInt64 input.standardStateReservoir ∧
  FitsUInt64 input.floorValue

def toReferenceInitialization (input : Generated.InitializationInput) : Reference.InitializationInput :=
  { gasLimit := input.gasLimit
    intrinsicExecutionGas := input.intrinsicExecutionGas
    intrinsicStateGas := input.intrinsicStateGas
    eip8037Enabled := input.eip8037Enabled
    executionGasLimitCap := input.executionGasLimitCap }

def toReferenceStatic (input : Generated.StaticInput) : Reference.StaticInput :=
  { senderPresent := input.senderPresent
    nonce := input.nonce
    toPresent := input.toPresent
    dataLength := input.dataLength
    transactionType := input.transactionType
    authorizationListPresent := input.authorizationListPresent
    authorizationListLength := input.authorizationListLength
    txGasLimit := input.txGasLimit
    headerGasLimit := input.headerGasLimit
    headerGasUsed := input.headerGasUsed
    skipValidation := input.skipValidation
    processorParallel := input.processorParallel
    eip3860Enabled := input.eip3860Enabled
    eip8037Enabled := input.eip8037Enabled
    maxInitCodeSize := input.maxInitCodeSize
    standardValue := input.standardValue
    standardStateReservoir := input.standardStateReservoir
    floorValue := input.floorValue }

def mapInitializationOutcome (outcome : Reference.InitializationOutcome) : Generated.InitializationOutcome :=
  match outcome with
  | .success => .success
  | .intrinsicGasExceedsLimit => .intrinsicGasExceedsLimit

def mapInitializationResult (result : Reference.InitializationResult) : Generated.InitializationResult :=
  { outcome := mapInitializationOutcome result.outcome
    value := result.value
    stateReservoir := result.stateReservoir
    stateGasUsed := result.stateGasUsed
    stateGasSpill := result.stateGasSpill
    stateGasSpillRefunded := result.stateGasSpillRefunded }

def mapError (error : Reference.ErrorType) : Generated.ErrorType :=
  match error with
  | .none => .none
  | .blockGasLimitExceeded => .blockGasLimitExceeded
  | .gasLimitBelowIntrinsicGas => .gasLimitBelowIntrinsicGas
  | .gasLimitBelowFloorGas => .gasLimitBelowFloorGas
  | .malformedTransaction => .malformedTransaction
  | .nonceOverflow => .nonceOverflow
  | .senderNotSpecified => .senderNotSpecified
  | .transactionSizeOverMaxInitCodeSize => .transactionSizeOverMaxInitCodeSize

def mapReturnSite (site : Reference.ReturnSite) : Generated.ReturnSite :=
  match site with
  | .validateStaticSenderAbsent => .validateStaticSenderAbsent
  | .validateStaticNonceOverflow => .validateStaticNonceOverflow
  | .validateStaticInitcodeOversize => .validateStaticInitcodeOversize
  | .validateStaticSetCodeCreation => .validateStaticSetCodeCreation
  | .validateStaticSetCodeAuthorization => .validateStaticSetCodeAuthorization
  | .validateStaticIntrinsicCap => .validateStaticIntrinsicCap
  | .validateStaticExecutionIntrinsic => .validateStaticExecutionIntrinsic
  | .validateStaticFloorIntrinsic => .validateStaticFloorIntrinsic
  | .validateGasMinimumIntrinsic => .validateGasMinimumIntrinsic
  | .validateGasEip8037BlockLimit => .validateGasEip8037BlockLimit
  | .validateGasLegacyBlockLimit => .validateGasLegacyBlockLimit
  | .validateGasOk => .validateGasOk
  | .calculateAvailableGasFailure => .calculateAvailableGasFailure
  | .calculateAvailableGasSuccess => .calculateAvailableGasSuccess

def mapEvmExceptionType (_ : Reference.EvmExceptionType) : Generated.EvmExceptionType := .none

def mapTransactionResult (result : Reference.TransactionResult) : Generated.TransactionResult :=
  { error := mapError result.error
    evmExceptionType := mapEvmExceptionType result.evmExceptionType
    returnSite := mapReturnSite result.returnSite }

private theorem mapTransactionResult_ite {condition : Prop} [Decidable condition]
    (whenTrue whenFalse : Reference.TransactionResult) :
    mapTransactionResult (if condition then whenTrue else whenFalse) =
      if condition then mapTransactionResult whenTrue else mapTransactionResult whenFalse := by
  by_cases h : condition <;> simp [h]

def mapAvailablePolicy (policy : Reference.AvailableGasPolicy) : Generated.AvailableGasPolicy :=
  { value := policy.value
    stateReservoir := policy.stateReservoir
    stateGasUsed := policy.stateGasUsed
    stateGasSpill := policy.stateGasSpill
    stateGasSpillRefunded := policy.stateGasSpillRefunded }

def mapAvailableResult (result : Reference.AvailableGasResult) : Generated.AvailableGasResult :=
  { result := mapTransactionResult result.result
    available := mapAvailablePolicy result.available
    initialization := mapInitializationResult result.initialization }

private theorem normalizeUInt64_of_source_width {value : Nat} (h : FitsUInt64 value) :
    Reference.normalizeUInt64 value = value := by
  unfold Reference.normalizeUInt64
  unfold FitsUInt64 at h
  change value ≤ Eip803x.Generated.TransactionGasInitializationKernel.uint64Max at h
  have h' : value ≤ Reference.uint64Max := by
    change value ≤ (2 ^ 64 - 1)
    exact h
  rw [if_pos h']

private theorem generatedNormalizeUInt64_of_source_width {value : Nat} (h : FitsUInt64 value) :
    Generated.normalizeUInt64 value = value := by
  unfold Generated.normalizeUInt64 Eip803x.Generated.TransactionGasInitializationKernel.normalizeUInt64
  unfold FitsUInt64 at h
  change value ≤ Eip803x.Generated.TransactionGasInitializationKernel.uint64Max at h
  rw [if_pos h]

private theorem canonicalNormalizeUInt64_of_source_width {value : Nat} (h : FitsUInt64 value) :
    Eip803x.Generated.TransactionGasInitializationKernel.normalizeUInt64 value = value := by
  unfold Eip803x.Generated.TransactionGasInitializationKernel.normalizeUInt64
  unfold FitsUInt64 at h
  change value ≤ Eip803x.Generated.TransactionGasInitializationKernel.uint64Max at h
  rw [if_pos h]

private theorem reference_wrapUInt64_eq_generated (value : Int) :
    Reference.wrapUInt64 value = Generated.int64ToUInt64 value := by
  unfold Reference.wrapUInt64 Generated.int64ToUInt64
  unfold Eip803x.Generated.TransactionGasInitializationKernel.int64ToUInt64
    Eip803x.Generated.TransactionGasInitializationKernel.wrapUInt64
  unfold Reference.int64Modulus Reference.uint64Max Reference.uint64Modulus
  unfold Eip803x.Generated.TransactionGasInitializationKernel.uint64Max
    Eip803x.Generated.TransactionGasInitializationKernel.uint64Modulus
    Eip803x.Generated.TransactionGasInitializationKernel.int64Modulus
  rfl

private theorem reference_int64ToUInt64_of_source_width {value : Int} (_h : FitsInt64 value) :
    Reference.int64ToUInt64 value = Generated.int64ToUInt64 value := by
  unfold Reference.int64ToUInt64 Generated.int64ToUInt64
  exact reference_wrapUInt64_eq_generated value

private theorem reference_uint64ToInt64_eq_generated (value : Nat) :
    Reference.uint64ToInt64 value = Generated.uint64ToInt64 value := by
  unfold Reference.uint64ToInt64 Generated.uint64ToInt64
  unfold Reference.wrapInt64
    Eip803x.Generated.TransactionGasInitializationKernel.uint64ToInt64
    Eip803x.Generated.TransactionGasInitializationKernel.wrapInt64
  unfold Reference.int64Modulus Reference.int64SignBit
  unfold Eip803x.Generated.TransactionGasInitializationKernel.int64Min
    Eip803x.Generated.TransactionGasInitializationKernel.int64Max
    Eip803x.Generated.TransactionGasInitializationKernel.int64SignBit
    Eip803x.Generated.TransactionGasInitializationKernel.int64Modulus
    Eip803x.Generated.TransactionGasInitializationKernel.uint64Modulus
  rfl

private theorem reference_add_eq_generated (left right : Nat) :
    Reference.addUInt64 left right = Generated.addUInt64 left right := by
  unfold Reference.addUInt64 Generated.addUInt64
  exact reference_wrapUInt64_eq_generated _

private theorem reference_sub_eq_generated (left right : Nat) :
    Reference.subUInt64 left right = Generated.subUInt64 left right := by
  unfold Reference.subUInt64 Generated.subUInt64
  exact reference_wrapUInt64_eq_generated _

private theorem generated_int64ToUInt64_of_nonnegative_fits {value : Int}
    (hNonnegative : 0 ≤ value) (hFits : FitsInt64 value) :
    Generated.int64ToUInt64 value = Int.toNat value := by
  unfold Generated.int64ToUInt64
    Eip803x.Generated.TransactionGasInitializationKernel.int64ToUInt64
    Eip803x.Generated.TransactionGasInitializationKernel.wrapUInt64
  have hUpper : value ≤ (Eip803x.Generated.TransactionGasInitializationKernel.uint64Max : Int) := by
    rcases hFits with ⟨_hMinimum, hMaximum⟩
    have hRange :
        (Eip803x.Generated.TransactionGasInitializationKernel.int64Max : Int) ≤
          Eip803x.Generated.TransactionGasInitializationKernel.uint64Max := by
      decide
    exact Int.le_trans hMaximum hRange
  rw [if_pos ⟨hNonnegative, hUpper⟩]

private theorem generated_addUInt64_of_no_wrap {left right : Nat}
    (hFits : left + right ≤ Generated.uint64Max) :
    Generated.addUInt64 left right = left + right := by
  unfold Generated.addUInt64 Eip803x.Generated.TransactionGasInitializationKernel.addUInt64
    Eip803x.Generated.TransactionGasInitializationKernel.wrapUInt64
  have hNonnegative : (0 : Int) ≤ (left : Int) + right := by omega
  have hBounded : (left : Int) + right ≤
      (Eip803x.Generated.TransactionGasInitializationKernel.uint64Max : Int) := by
    change left + right ≤ Eip803x.Generated.TransactionGasInitializationKernel.uint64Max at hFits
    omega
  rw [if_pos ⟨hNonnegative, hBounded⟩]
  apply Int.ofNat_inj.mp
  rw [Int.toNat_of_nonneg hNonnegative]
  norm_cast

private theorem reference_tryCreate_eq_generated
    (input : Generated.InitializationInput)
    (h : SourceWidthInitialization input) :
    Generated.tryCreate input =
      mapInitializationResult (Reference.tryCreate (toReferenceInitialization input)) := by
  rcases h with ⟨hGas, hExecution, hState, hCap⟩
  cases input with
  | mk gasLimit intrinsicExecutionGas intrinsicStateGas eip8037Enabled executionGasLimitCap =>
    have hGas' : FitsUInt64 gasLimit := by simpa using hGas
    have hExecution' : FitsUInt64 intrinsicExecutionGas := by simpa using hExecution
    have hState' : FitsInt64 intrinsicStateGas := by simpa using hState
    have hCap' : FitsUInt64 executionGasLimitCap := by simpa using hCap
    have hGasRef : gasLimit ≤ Reference.uint64Max := by
      have h : gasLimit ≤ Generated.uint64Max := by
        exact hGas'
      change gasLimit ≤ Reference.uint64Max
      exact h
    have hExecutionRef : intrinsicExecutionGas ≤ Reference.uint64Max := by
      have h : intrinsicExecutionGas ≤ Generated.uint64Max := by
        exact hExecution'
      change intrinsicExecutionGas ≤ Reference.uint64Max
      exact h
    have hCapRef : executionGasLimitCap ≤ Reference.uint64Max := by
      have h : executionGasLimitCap ≤ Generated.uint64Max := by
        exact hCap'
      change executionGasLimitCap ≤ Reference.uint64Max
      exact h
    have hStateWrap : Eip803x.Generated.TransactionGasInitializationKernel.wrapInt64 intrinsicStateGas =
        intrinsicStateGas := by
      unfold Eip803x.Generated.TransactionGasInitializationKernel.wrapInt64
      unfold FitsInt64 Generated.int64Min Generated.int64Max at hState'
      unfold Eip803x.Generated.TransactionGasInitializationKernel.int64Min
        Eip803x.Generated.TransactionGasInitializationKernel.int64Max
      rw [if_pos ⟨hState'.1, hState'.2⟩]
    simp only [Generated.tryCreate,
      Eip803x.Generated.TransactionGasInitializationKernel.tryCreate,
      Eip803x.Generated.TransactionGasInitializationKernel.tryCreateNormalized,
      Reference.tryCreate, toReferenceInitialization, mapInitializationResult,
      mapInitializationOutcome]
    rw [canonicalNormalizeUInt64_of_source_width hGas',
      canonicalNormalizeUInt64_of_source_width hExecution',
      canonicalNormalizeUInt64_of_source_width hCap', hStateWrap]
    simp only [Reference.normalizeUInt64, if_pos hGasRef, if_pos hExecutionRef, if_pos hCapRef,
      reference_int64ToUInt64_of_source_width hState', reference_add_eq_generated,
      reference_sub_eq_generated, reference_uint64ToInt64_eq_generated]
    cases eip8037Enabled <;>
    by_cases hInsufficient : gasLimit <
        Generated.addUInt64 intrinsicExecutionGas (Generated.int64ToUInt64 intrinsicStateGas) <;>
        all_goals
          simp only [Generated.addUInt64, Generated.int64ToUInt64] at hInsufficient
          simp only [Generated.addUInt64, Generated.subUInt64, Generated.int64ToUInt64,
            Generated.uint64ToInt64]
          simp [hInsufficient, Reference.failureResult]

/-!
The static theorem intentionally has only source-width representation premises.
It has no transaction-validity, affordability, nonnegative-reservoir, or cap
premise: every ordered branch, including failure and wrap neighbors, is part
of the statement.
-/
theorem generatedValidateStatic_refines_orderedStaticAdmission :
    ∀ input : Generated.StaticInput, SourceWidthStatic input →
      Generated.validateStatic input =
        mapTransactionResult (Reference.validateStatic (toReferenceStatic input)) := by
  intro input h
  rcases h with ⟨hNonce, hData, hType, hAuth, hTx, hHeader, hUsed, hMax, hStandard, hState, hFloor⟩
  simp only [Generated.validateStatic, Reference.validateStatic, toReferenceStatic,
    Generated.result, Reference.result, Generated.isCreation, Reference.isCreation,
    Generated.isSetCode, Reference.isSetCode, Generated.isAboveInitCode,
    Reference.isAboveInitCode, Generated.standardGasTotal, Reference.standardGasTotal,
    Generated.minRequiredGasLimit, Reference.minRequiredGasLimit,
    Generated.legacyGasAllowance, Reference.legacyGasAllowance,
    Reference.uint64Max, Generated.uint64Max, Reference.uint64Modulus,
    Generated.uint64Modulus, Reference.txGasLimitCap, Generated.txGasLimitCap,
    Reference.setCodeType, Generated.setCodeType,
    generatedNormalizeUInt64_of_source_width hStandard,
    generatedNormalizeUInt64_of_source_width hFloor,
    generatedNormalizeUInt64_of_source_width hHeader,
    normalizeUInt64_of_source_width hStandard,
    normalizeUInt64_of_source_width hFloor,
    normalizeUInt64_of_source_width hHeader,
    reference_add_eq_generated,
    reference_sub_eq_generated,
    reference_int64ToUInt64_of_source_width hState,
    mapTransactionResult_ite]
  simp only [mapTransactionResult, mapError, mapEvmExceptionType, mapReturnSite]
  rfl

/-!
This theorem names every wrapper and initializer field.  In particular, a
failure observes the canonical kernel failure result while the wrapper's
available policy is the independent five-zero default.  No mathematical
validity or affordability premise is used; only representation widths are
assumed.
-/
theorem generatedCalculateAvailableGas_refines_fixedWidthInitialization :
    ∀ input : Generated.InitializationInput, SourceWidthInitialization input →
      let generated := Generated.calculateAvailableGas input
      let reference := Reference.calculateAvailableGas (toReferenceInitialization input)
      generated.result.error = mapError reference.result.error ∧
      generated.result.evmExceptionType = mapEvmExceptionType reference.result.evmExceptionType ∧
      generated.result.returnSite = mapReturnSite reference.result.returnSite ∧
      generated.available.value = (mapAvailablePolicy reference.available).value ∧
      generated.available.stateReservoir = (mapAvailablePolicy reference.available).stateReservoir ∧
      generated.available.stateGasUsed = (mapAvailablePolicy reference.available).stateGasUsed ∧
      generated.available.stateGasSpill = (mapAvailablePolicy reference.available).stateGasSpill ∧
      generated.available.stateGasSpillRefunded = (mapAvailablePolicy reference.available).stateGasSpillRefunded ∧
      generated.initialization.outcome = mapInitializationOutcome reference.initialization.outcome ∧
      generated.initialization.value = reference.initialization.value ∧
      generated.initialization.stateReservoir = reference.initialization.stateReservoir ∧
      generated.initialization.stateGasUsed = reference.initialization.stateGasUsed ∧
      generated.initialization.stateGasSpill = reference.initialization.stateGasSpill ∧
      generated.initialization.stateGasSpillRefunded = reference.initialization.stateGasSpillRefunded := by
  intro input h
  have hTry := reference_tryCreate_eq_generated input h
  cases hRef : Reference.tryCreate (toReferenceInitialization input) with
  | mk outcome value stateReservoir stateGasUsed stateGasSpill stateGasSpillRefunded =>
    simp [toReferenceInitialization] at hRef
    simp [toReferenceInitialization, hRef] at hTry
    cases outcome <;>
      simp [Generated.calculateAvailableGas, Reference.calculateAvailableGas,
        toReferenceInitialization, Generated.result, Reference.result,
        mapError, mapEvmExceptionType, mapReturnSite, mapAvailablePolicy,
        Generated.policyFromInitialization, Reference.policyFromInitialization,
        Generated.defaultAvailablePolicy, Reference.defaultAvailablePolicy,
        mapInitializationResult, mapInitializationOutcome, hTry, hRef]

private theorem accepted_not_sender
    (input : Generated.StaticInput)
    (hAccepted : Generated.validateStatic input = Generated.result .none .validateGasOk) :
    ¬ input.senderPresent = false := by
  intro hSender
  have hSite := congrArg Generated.TransactionResult.returnSite hAccepted
  simp [Generated.validateStatic, Generated.result, hSender] at hSite

private theorem accepted_guard_negations
    (input : Generated.StaticInput)
    (hAccepted : Generated.validateStatic input = Generated.result .none .validateGasOk) :
    (¬ input.senderPresent = false) ∧
    (¬ (input.skipValidation = false ∧ input.nonce = Generated.uint64Max)) ∧
    (¬ Generated.isAboveInitCode input = true) ∧
    (¬ ((Generated.isSetCode input && Generated.isCreation input) = true)) ∧
    (¬ ((Generated.isSetCode input &&
      (!input.authorizationListPresent || input.authorizationListLength == 0)) = true)) ∧
    (¬ (input.eip8037Enabled = true ∧
      (input.standardValue > Generated.txGasLimitCap ∨ input.floorValue > Generated.txGasLimitCap))) ∧
    (¬ input.txGasLimit < Generated.normalizeUInt64 input.standardValue) ∧
    (¬ input.txGasLimit < Generated.normalizeUInt64 input.floorValue) ∧
    (¬ input.txGasLimit < Generated.minRequiredGasLimit input) ∧
    (¬ ((input.skipValidation = false ∧ input.eip8037Enabled = true) ∧
      Generated.normalizeUInt64 input.headerGasLimit < input.txGasLimit)) ∧
    (¬ ((input.skipValidation = false ∧ input.eip8037Enabled = false) ∧
      Generated.legacyGasAllowance input < input.txGasLimit)) := by
  have hSender := accepted_not_sender input hAccepted
  have hNonce : ¬ (input.skipValidation = false ∧ input.nonce = Generated.uint64Max) := by
    intro h
    have hSite := congrArg Generated.TransactionResult.returnSite hAccepted
    simp [Generated.validateStatic, Generated.result, hSender, h] at hSite
  have hInitCode : ¬ Generated.isAboveInitCode input = true := by
    intro h
    have hSite := congrArg Generated.TransactionResult.returnSite hAccepted
    simp [Generated.validateStatic, Generated.result, hSender, hNonce, h] at hSite
  have hSetCodeCreation : ¬ ((Generated.isSetCode input && Generated.isCreation input) = true) := by
    intro h
    have hSite := congrArg Generated.TransactionResult.returnSite hAccepted
    simp [Generated.validateStatic, Generated.result, hSender, hNonce, hInitCode, h] at hSite
  have hSetCodeAuthorization : ¬ (Generated.isSetCode input &&
      (!input.authorizationListPresent || input.authorizationListLength == 0)) = true := by
    intro h
    have hSite := congrArg Generated.TransactionResult.returnSite hAccepted
    simp [Generated.validateStatic, Generated.result, hSender, hNonce, hInitCode,
      hSetCodeCreation, h] at hSite
  have hCap : ¬ (input.eip8037Enabled = true ∧
      (input.standardValue > Generated.txGasLimitCap ∨ input.floorValue > Generated.txGasLimitCap)) := by
    intro h
    have hSite := congrArg Generated.TransactionResult.returnSite hAccepted
    simp [Generated.validateStatic, Generated.result, hSender, hNonce, hInitCode,
      hSetCodeCreation, hSetCodeAuthorization, h] at hSite
  have hExecution : ¬ input.txGasLimit < Generated.normalizeUInt64 input.standardValue := by
    intro h
    have hSite := congrArg Generated.TransactionResult.returnSite hAccepted
    simp [Generated.validateStatic, Generated.result, hSender, hNonce, hInitCode,
      hSetCodeCreation, hSetCodeAuthorization, hCap, h] at hSite
  have hFloor : ¬ input.txGasLimit < Generated.normalizeUInt64 input.floorValue := by
    intro h
    have hSite := congrArg Generated.TransactionResult.returnSite hAccepted
    simp [Generated.validateStatic, Generated.result, hSender, hNonce, hInitCode,
      hSetCodeCreation, hSetCodeAuthorization, hCap, hExecution, h] at hSite
  have hMinimum : ¬ input.txGasLimit < Generated.minRequiredGasLimit input := by
    intro h
    have hSite := congrArg Generated.TransactionResult.returnSite hAccepted
    simp [Generated.validateStatic, Generated.result, hSender, hNonce, hInitCode,
      hSetCodeCreation, hSetCodeAuthorization, hCap, hExecution, hFloor, h] at hSite
  have hEipBlock : ¬ ((input.skipValidation = false ∧ input.eip8037Enabled = true) ∧
      Generated.normalizeUInt64 input.headerGasLimit < input.txGasLimit) := by
    intro h
    have hSite := congrArg Generated.TransactionResult.returnSite hAccepted
    have hNonceValue : ¬ input.nonce = Generated.uint64Max := by
      intro hNonceValue
      apply hNonce
      exact ⟨h.1.1, hNonceValue⟩
    have hCapValues : ¬ (Generated.txGasLimitCap < input.standardValue ∨
        Generated.txGasLimitCap < input.floorValue) := by
      intro hCapValues
      apply hCap
      exact ⟨h.1.2, hCapValues⟩
    simp [Generated.validateStatic, Generated.result, hSender, hInitCode,
      hSetCodeCreation, hSetCodeAuthorization, hNonceValue, hCapValues,
      hExecution, hFloor, hMinimum, h] at hSite
  have hLegacyBlock : ¬ ((input.skipValidation = false ∧ input.eip8037Enabled = false) ∧
      Generated.legacyGasAllowance input < input.txGasLimit) := by
    intro h
    have hSite := congrArg Generated.TransactionResult.returnSite hAccepted
    have hNonceValue : ¬ input.nonce = Generated.uint64Max := by
      intro hNonceValue
      apply hNonce
      exact ⟨h.1.1, hNonceValue⟩
    simp [Generated.validateStatic, Generated.result, hSender, hInitCode,
      hSetCodeCreation, hSetCodeAuthorization, hExecution, hFloor, hMinimum,
      hNonceValue, h] at hSite
  exact ⟨hSender, hNonce, hInitCode, hSetCodeCreation, hSetCodeAuthorization, hCap,
    hExecution, hFloor, hMinimum, hEipBlock, hLegacyBlock⟩

/-!
The corollary below deliberately exposes the adapter obligations instead of
packaging them as a pre-existing `RefinementValid` hypothesis.  In particular,
the accepted static branches derive affordability and the EIP-8037 cap; the
nonnegative and signed-reservoir bounds only say how those source-width values
are interpreted by the mathematical initializer.
-/
theorem staticAdmissionAccepted_implies_availableGasInitialization
    (input : Generated.StaticInput)
    (hWidth : SourceWidthStatic input)
    (hEip8037 : input.eip8037Enabled = true)
    (hAccepted : Generated.validateStatic input = Generated.result .none .validateGasOk)
    (hStateNonnegative : 0 ≤ input.standardStateReservoir)
    (hTotalFits : input.standardValue + Int.toNat input.standardStateReservoir ≤ Generated.uint64Max)
    (hReservoirSigned : FitsInt64
      (((input.txGasLimit -
        (input.standardValue + Int.toNat input.standardStateReservoir) -
        Nat.min (Generated.txGasLimitCap - input.standardValue)
          (input.txGasLimit - (input.standardValue + Int.toNat input.standardStateReservoir))) : Nat) : Int)) :
    let generated := Generated.calculateAvailableGas (Generated.initializationInput input)
    let canonicalInput :=
      Eip803x.Refinement.TransactionGasInitialization.referenceInput
        input.txGasLimit input.standardValue input.standardStateReservoir Generated.txGasLimitCap
    let model := Eip803x.TransactionGas.initializeTransactionGas canonicalInput
    input.standardValue ≤ Generated.txGasLimitCap ∧
      input.floorValue ≤ Generated.txGasLimitCap ∧
      input.standardValue + Int.toNat input.standardStateReservoir ≤ input.txGasLimit ∧
      canonicalInput.Valid ∧
      generated.result.error = Generated.ErrorType.none ∧
      generated.result.evmExceptionType = Generated.EvmExceptionType.none ∧
      generated.result.returnSite = Generated.ReturnSite.calculateAvailableGasSuccess ∧
      generated.available.value = model.gasLeft ∧
      generated.available.stateReservoir = (model.stateGasReservoir : Int) ∧
      generated.available.stateGasUsed = (model.stateGasUsed : Int) ∧
      generated.available.stateGasSpill = (model.stateGasSpill : Int) ∧
      generated.available.stateGasSpillRefunded = (model.stateGasSpillRefunded : Int) ∧
      generated.initialization.outcome = .success ∧
      generated.initialization.value = model.gasLeft ∧
      generated.initialization.stateReservoir = (model.stateGasReservoir : Int) ∧
      generated.initialization.stateGasUsed = (model.stateGasUsed : Int) ∧
      generated.initialization.stateGasSpill = (model.stateGasSpill : Int) ∧
      generated.initialization.stateGasSpillRefunded = (model.stateGasSpillRefunded : Int) ∧
      model.evmGas = input.txGasLimit -
        (input.standardValue + Int.toNat input.standardStateReservoir) ∧
      model.executionGasBudget = Generated.txGasLimitCap - input.standardValue := by
  rcases hWidth with
    ⟨_hNonceWidth, _hDataWidth, _hTypeWidth, _hAuthorizationWidth, hGasWidth,
      _hHeaderWidth, _hUsedWidth, _hMaxInitCodeWidth, hStandardWidth, hStateWidth, _hFloorWidth⟩
  have hStateSigned' : FitsInt64 input.standardStateReservoir := hStateWidth
  have hGuards := accepted_guard_negations input hAccepted
  rcases hGuards with
    ⟨_hSender, _hNonce, _hInitCode, _hSetCodeCreation, _hSetCodeAuthorization,
      hCap, _hExecution, _hFloor, hMinimum, _hEipBlock, _hLegacyBlock⟩
  have hCapValues : ¬ (Generated.txGasLimitCap < input.standardValue ∨
      Generated.txGasLimitCap < input.floorValue) := by
    intro hCapValues
    apply hCap
    exact ⟨hEip8037, hCapValues⟩
  have hStandardCap : input.standardValue ≤ Generated.txGasLimitCap := by
    exact Nat.le_of_not_gt (fun h => hCapValues (Or.inl h))
  have hFloorCap : input.floorValue ≤ Generated.txGasLimitCap := by
    exact Nat.le_of_not_gt (fun h => hCapValues (Or.inr h))
  have hMinimumLe : Generated.minRequiredGasLimit input ≤ input.txGasLimit :=
    Nat.le_of_not_gt hMinimum
  have hTotalLe : Generated.standardGasTotal input ≤ input.txGasLimit :=
    Nat.le_trans (Nat.le_max_left _ _) hMinimumLe
  have hStateToUInt := generated_int64ToUInt64_of_nonnegative_fits
    hStateNonnegative hStateSigned'
  have hTotalAdd : Generated.standardGasTotal input =
      input.standardValue + Int.toNat input.standardStateReservoir := by
    unfold Generated.standardGasTotal
    rw [hStateToUInt, generated_addUInt64_of_no_wrap hTotalFits]
  have hTotalGas : input.standardValue + Int.toNat input.standardStateReservoir ≤ input.txGasLimit := by
    rw [hTotalAdd] at hTotalLe
    exact hTotalLe
  have hCapFits : FitsUInt64 Generated.txGasLimitCap := by
    unfold FitsUInt64 Generated.uint64Max Generated.uint64Modulus
    decide
  have hRefinement :
      Eip803x.Refinement.TransactionGasInitialization.RefinementValid
        input.txGasLimit input.standardValue input.standardStateReservoir Generated.txGasLimitCap := by
    exact ⟨hGasWidth, hStandardWidth, hStateSigned', hStateNonnegative, hTotalFits,
      hTotalGas, hStandardCap, hCapFits, hReservoirSigned⟩
  have hCanonical :=
    Eip803x.Refinement.TransactionGasInitialization.generatedTryCreate_refines_initializeTransactionGas
      hRefinement
  dsimp at hCanonical
  rcases hCanonical with
    ⟨hValid, hOutcome, hValue, hReservoir, hUsed, hSpill, hRefunded, hEvmGas, hBudget⟩
  have hOutcome' :
      (Generated.tryCreate (Generated.initializationInput input)).outcome = .success := by
    simpa [Generated.tryCreate, Generated.initializationInput, hEip8037] using hOutcome
  have hValue' :
      (Generated.tryCreate (Generated.initializationInput input)).value =
        (Eip803x.TransactionGas.initializeTransactionGas
          (Eip803x.Refinement.TransactionGasInitialization.referenceInput
            input.txGasLimit input.standardValue input.standardStateReservoir
              Generated.txGasLimitCap)).gasLeft := by
    simpa [Generated.tryCreate, Generated.initializationInput, hEip8037] using hValue
  have hReservoir' :
      (Generated.tryCreate (Generated.initializationInput input)).stateReservoir =
        ((Eip803x.TransactionGas.initializeTransactionGas
          (Eip803x.Refinement.TransactionGasInitialization.referenceInput
            input.txGasLimit input.standardValue input.standardStateReservoir
              Generated.txGasLimitCap)).stateGasReservoir : Int) := by
    simpa [Generated.tryCreate, Generated.initializationInput, hEip8037] using hReservoir
  have hUsed' :
      (Generated.tryCreate (Generated.initializationInput input)).stateGasUsed =
        ((Eip803x.TransactionGas.initializeTransactionGas
          (Eip803x.Refinement.TransactionGasInitialization.referenceInput
            input.txGasLimit input.standardValue input.standardStateReservoir
              Generated.txGasLimitCap)).stateGasUsed : Int) := by
    simpa [Generated.tryCreate, Generated.initializationInput, hEip8037] using hUsed
  have hSpill' :
      (Generated.tryCreate (Generated.initializationInput input)).stateGasSpill =
        ((Eip803x.TransactionGas.initializeTransactionGas
          (Eip803x.Refinement.TransactionGasInitialization.referenceInput
            input.txGasLimit input.standardValue input.standardStateReservoir
              Generated.txGasLimitCap)).stateGasSpill : Int) := by
    simpa [Generated.tryCreate, Generated.initializationInput, hEip8037] using hSpill
  have hRefunded' :
      (Generated.tryCreate (Generated.initializationInput input)).stateGasSpillRefunded =
        ((Eip803x.TransactionGas.initializeTransactionGas
          (Eip803x.Refinement.TransactionGasInitialization.referenceInput
            input.txGasLimit input.standardValue input.standardStateReservoir
              Generated.txGasLimitCap)).stateGasSpillRefunded : Int) := by
    simpa [Generated.tryCreate, Generated.initializationInput, hEip8037] using hRefunded
  have hEvmGas' :
      (Eip803x.TransactionGas.initializeTransactionGas
        (Eip803x.Refinement.TransactionGasInitialization.referenceInput
          input.txGasLimit input.standardValue input.standardStateReservoir
            Generated.txGasLimitCap)).evmGas =
        input.txGasLimit - (input.standardValue + Int.toNat input.standardStateReservoir) := hEvmGas
  have hBudget' :
      (Eip803x.TransactionGas.initializeTransactionGas
        (Eip803x.Refinement.TransactionGasInitialization.referenceInput
          input.txGasLimit input.standardValue input.standardStateReservoir
            Generated.txGasLimitCap)).executionGasBudget =
        Generated.txGasLimitCap - input.standardValue := hBudget
  simp [Generated.calculateAvailableGas, Generated.policyFromInitialization, Generated.result,
    hStandardCap, hFloorCap, hTotalGas, hValid, hOutcome', hValue', hReservoir',
    hUsed', hSpill', hRefunded', hEvmGas', hBudget']

end OrdinaryStaticAdmissionExtractor.Refinement
