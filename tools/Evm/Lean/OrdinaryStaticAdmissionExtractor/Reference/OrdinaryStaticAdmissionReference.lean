-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

/-!
  Handwritten reference for the two admitted helper surfaces.  This file is
  deliberately independent of `Generated.OrdinaryStaticAdmissionKernel`: it
  restates fixed-width arithmetic, the ordered admission guards, and the
  available-policy projection in a separate namespace.  The refinement file
  supplies the bridge to the generated definitions.
-/

namespace OrdinaryStaticAdmissionExtractor.Reference

def uint64Modulus : Nat := 2 ^ 64
def uint64Max : Nat := uint64Modulus - 1
def int64Modulus : Int := (uint64Modulus : Int)
def int64SignBit : Int := 2 ^ 63

def normalizeUInt64 (value : Nat) : Nat :=
  if value <= uint64Max then value else value % uint64Modulus

def wrapUInt64 (value : Int) : Nat :=
  if 0 ≤ value ∧ value ≤ (uint64Max : Int) then
    Int.toNat value
  else
    Int.toNat (value % int64Modulus)

def int64ToUInt64 (value : Int) : Nat := wrapUInt64 value

def wrapInt64 (value : Int) : Int :=
  if -(int64SignBit) ≤ value ∧ value ≤ int64SignBit - 1 then
    value
  else
    let residue := value % int64Modulus
    if residue < int64SignBit then residue else residue - int64Modulus

def uint64ToInt64 (value : Nat) : Int := wrapInt64 (value : Int)

def addUInt64 (left right : Nat) : Nat :=
  int64ToUInt64 ((left : Int) + right)

def subUInt64 (left right : Nat) : Nat :=
  int64ToUInt64 ((left : Int) - right)

inductive InitializationOutcome where
  | success
  | intrinsicGasExceedsLimit
  deriving DecidableEq, Repr

structure InitializationInput where
  gasLimit : Nat
  intrinsicExecutionGas : Nat
  intrinsicStateGas : Int
  eip8037Enabled : Bool
  executionGasLimitCap : Nat
  deriving DecidableEq, Repr

structure InitializationResult where
  outcome : InitializationOutcome
  value : Nat
  stateReservoir : Int
  stateGasUsed : Int
  stateGasSpill : Int
  stateGasSpillRefunded : Int
  deriving DecidableEq, Repr

def failureResult : InitializationResult :=
  { outcome := .intrinsicGasExceedsLimit
    value := 0
    stateReservoir := 0
    stateGasUsed := 0
    stateGasSpill := 0
    stateGasSpillRefunded := 0 }

def tryCreate (input : InitializationInput) : InitializationResult :=
  let gasLimit := normalizeUInt64 input.gasLimit
  let intrinsicExecutionGas := normalizeUInt64 input.intrinsicExecutionGas
  let intrinsicStateGas := input.intrinsicStateGas
  let executionGasLimitCap := normalizeUInt64 input.executionGasLimitCap
  let intrinsicTotal := addUInt64 intrinsicExecutionGas (int64ToUInt64 intrinsicStateGas)
  if gasLimit < intrinsicTotal then
    failureResult
  else
    let availableGas := subUInt64 gasLimit intrinsicTotal
    let gasLeft := if input.eip8037Enabled then
      let executionGasAfterIntrinsicCap :=
        if intrinsicExecutionGas >= executionGasLimitCap then 0
        else subUInt64 executionGasLimitCap intrinsicExecutionGas
      Nat.min availableGas executionGasAfterIntrinsicCap
    else availableGas
    let stateReservoir := subUInt64 availableGas gasLeft
    { outcome := .success
      value := gasLeft
      stateReservoir := uint64ToInt64 stateReservoir
      stateGasUsed := intrinsicStateGas
      stateGasSpill := 0
      stateGasSpillRefunded := 0 }

inductive ErrorType where
  | none
  | blockGasLimitExceeded
  | gasLimitBelowIntrinsicGas
  | gasLimitBelowFloorGas
  | malformedTransaction
  | nonceOverflow
  | senderNotSpecified
  | transactionSizeOverMaxInitCodeSize
  deriving DecidableEq, Repr

inductive EvmExceptionType where
  | none
  deriving DecidableEq, Repr

inductive ReturnSite where
  | validateStaticSenderAbsent
  | validateStaticNonceOverflow
  | validateStaticInitcodeOversize
  | validateStaticSetCodeCreation
  | validateStaticSetCodeAuthorization
  | validateStaticIntrinsicCap
  | validateStaticExecutionIntrinsic
  | validateStaticFloorIntrinsic
  | validateGasMinimumIntrinsic
  | validateGasEip8037BlockLimit
  | validateGasLegacyBlockLimit
  | validateGasOk
  | calculateAvailableGasFailure
  | calculateAvailableGasSuccess
  deriving DecidableEq, Repr

structure TransactionResult where
  error : ErrorType
  evmExceptionType : EvmExceptionType
  returnSite : ReturnSite
  deriving DecidableEq, Repr

def result (error : ErrorType) (returnSite : ReturnSite) : TransactionResult :=
  { error, evmExceptionType := .none, returnSite }

structure StaticInput where
  senderPresent : Bool
  nonce : Nat
  toPresent : Bool
  dataLength : Nat
  transactionType : Nat
  authorizationListPresent : Bool
  authorizationListLength : Nat
  txGasLimit : Nat
  headerGasLimit : Nat
  headerGasUsed : Nat
  skipValidation : Bool
  processorParallel : Bool
  eip3860Enabled : Bool
  eip8037Enabled : Bool
  maxInitCodeSize : Int
  standardValue : Nat
  standardStateReservoir : Int
  floorValue : Nat
  deriving DecidableEq, Repr

def txGasLimitCap : Nat := 16_777_216
def setCodeType : Nat := 4
def isCreation (input : StaticInput) : Bool := !input.toPresent
def isSetCode (input : StaticInput) : Bool := input.transactionType == setCodeType
def isAboveInitCode (input : StaticInput) : Bool :=
  isCreation input && input.eip3860Enabled &&
    (input.dataLength : Int) > input.maxInitCodeSize
def standardGasTotal (input : StaticInput) : Nat :=
  addUInt64 input.standardValue (int64ToUInt64 input.standardStateReservoir)
def minRequiredGasLimit (input : StaticInput) : Nat :=
  Nat.max (standardGasTotal input) input.floorValue
def legacyGasAllowance (input : StaticInput) : Nat :=
  subUInt64 input.headerGasLimit (if input.processorParallel then 0 else input.headerGasUsed)

def validateStatic (input : StaticInput) : TransactionResult :=
  let validate := !input.skipValidation
  if !input.senderPresent then result .senderNotSpecified .validateStaticSenderAbsent else
  if validate && input.nonce == uint64Max then result .nonceOverflow .validateStaticNonceOverflow else
  if isAboveInitCode input then result .transactionSizeOverMaxInitCodeSize .validateStaticInitcodeOversize else
  if isSetCode input && isCreation input then result .malformedTransaction .validateStaticSetCodeCreation else
  if isSetCode input && (!input.authorizationListPresent || input.authorizationListLength == 0) then
    result .malformedTransaction .validateStaticSetCodeAuthorization else
  if input.eip8037Enabled &&
      (input.standardValue > txGasLimitCap || input.floorValue > txGasLimitCap) then
    result .gasLimitBelowIntrinsicGas .validateStaticIntrinsicCap else
  if input.txGasLimit < normalizeUInt64 input.standardValue then
    result .gasLimitBelowIntrinsicGas .validateStaticExecutionIntrinsic else
  if input.txGasLimit < normalizeUInt64 input.floorValue then
    result .gasLimitBelowFloorGas .validateStaticFloorIntrinsic else
  if input.txGasLimit < minRequiredGasLimit input then
    result .gasLimitBelowIntrinsicGas .validateGasMinimumIntrinsic else
  if validate && input.eip8037Enabled &&
      input.txGasLimit > normalizeUInt64 input.headerGasLimit then
    result .blockGasLimitExceeded .validateGasEip8037BlockLimit else
  if validate && !input.eip8037Enabled && input.txGasLimit > legacyGasAllowance input then
    result .blockGasLimitExceeded .validateGasLegacyBlockLimit else
  result .none .validateGasOk

structure AvailableGasPolicy where
  value : Nat
  stateReservoir : Int
  stateGasUsed : Int
  stateGasSpill : Int
  stateGasSpillRefunded : Int
  deriving DecidableEq, Repr

def defaultAvailablePolicy : AvailableGasPolicy :=
  { value := 0
    stateReservoir := 0
    stateGasUsed := 0
    stateGasSpill := 0
    stateGasSpillRefunded := 0 }

def policyFromInitialization (initialization : InitializationResult) : AvailableGasPolicy :=
  { value := initialization.value
    stateReservoir := initialization.stateReservoir
    stateGasUsed := initialization.stateGasUsed
    stateGasSpill := initialization.stateGasSpill
    stateGasSpillRefunded := initialization.stateGasSpillRefunded }

structure AvailableGasResult where
  result : TransactionResult
  available : AvailableGasPolicy
  initialization : InitializationResult
  deriving DecidableEq, Repr

def calculateAvailableGas (input : InitializationInput) : AvailableGasResult :=
  let initialization := tryCreate input
  match initialization.outcome with
  | .intrinsicGasExceedsLimit =>
    { result := result .gasLimitBelowIntrinsicGas .calculateAvailableGasFailure
      available := defaultAvailablePolicy
      initialization }
  | .success =>
    { result := result .none .calculateAvailableGasSuccess
      available := policyFromInitialization initialization
      initialization }

end OrdinaryStaticAdmissionExtractor.Reference
