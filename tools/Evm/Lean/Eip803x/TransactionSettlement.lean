-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

namespace Eip803x.TransactionSettlement

def uint64Modulus : Nat := 2 ^ 64
def uint64Max : Nat := uint64Modulus - 1
def int64Modulus : Int := (uint64Modulus : Int)
def int64SignBit : Int := 2 ^ 63
def int64Min : Int := -int64SignBit
def int64Max : Int := int64SignBit - 1
def int32Modulus : Int := 2 ^ 32
def int32SignBit : Int := 2 ^ 31
def int32Min : Int := -int32SignBit
def int32Max : Int := int32SignBit - 1

def normalizeUInt64 (value : Nat) : Nat :=
  if value ≤ uint64Max then value else value % uint64Modulus

def wrapUInt64 (value : Int) : Nat :=
  if 0 ≤ value ∧ value ≤ (uint64Max : Int) then
    Int.toNat value
  else
    Int.toNat (value % (uint64Modulus : Int))

def wrapInt64 (value : Int) : Int :=
  if int64Min ≤ value ∧ value ≤ int64Max then
    value
  else
    let residue := value % int64Modulus
    if residue < int64SignBit then residue else residue - int64Modulus

def wrapInt32 (value : Int) : Int :=
  if int32Min ≤ value ∧ value ≤ int32Max then
    value
  else
    let residue := value % int32Modulus
    if residue < int32SignBit then residue else residue - int32Modulus

def addUInt64 (left right : Nat) : Nat :=
  wrapUInt64 ((left : Int) + (right : Int))

def subUInt64 (left right : Nat) : Nat :=
  wrapUInt64 ((left : Int) - (right : Int))

def addInt64 (left right : Int) : Int :=
  wrapInt64 (left + right)

def mulInt64 (left right : Int) : Int :=
  wrapInt64 (left * right)

def negInt64 (value : Int) : Int :=
  wrapInt64 (-value)

def int64ToUInt64 (value : Int) : Nat :=
  wrapUInt64 value

def uint64ToInt64 (value : Nat) : Int :=
  wrapInt64 (value : Int)

def saturatingSubUInt64 (left right : Nat) : Nat :=
  if left > right then subUInt64 left right else 0

structure Input where
  transactionGasLimit : Nat
  preRefundGas : Nat
  refundCounter : Int
  destroyCount : Int
  destroyRefund : Nat
  codeInsertExecutionRefund : Nat
  calldataFloorGas : Nat
  stateGasUsed : Int
  refundQuotient : Nat
  isError : Bool
  shouldRevert : Bool
  isEip8037Enabled : Bool
  isEip7778Enabled : Bool
  deriving DecidableEq, Repr

structure Result where
  spentGas : Nat
  operationGas : Nat
  blockGas : Nat
  blockStateGas : Nat
  maxUsedGas : Nat
  gasRefund : Nat
  deriving DecidableEq, Repr

def FitsUInt64 (value : Nat) : Prop := value ≤ uint64Max
def FitsInt64 (value : Int) : Prop := int64Min ≤ value ∧ value ≤ int64Max
def FitsInt32 (value : Int) : Prop := int32Min ≤ value ∧ value ≤ int32Max

/-- The C# scalar calling domain. Arithmetic itself deliberately has modular machine semantics. -/
def Input.Valid (input : Input) : Prop :=
  FitsUInt64 input.transactionGasLimit ∧
  FitsUInt64 input.preRefundGas ∧
  FitsInt64 input.refundCounter ∧
  FitsInt32 input.destroyCount ∧
  FitsUInt64 input.destroyRefund ∧
  FitsUInt64 input.codeInsertExecutionRefund ∧
  FitsUInt64 input.calldataFloorGas ∧
  FitsInt64 input.stateGasUsed ∧
  FitsUInt64 input.refundQuotient ∧
  0 < input.refundQuotient

instance (input : Input) : Decidable input.Valid := by
  unfold Input.Valid FitsUInt64 FitsInt64 FitsInt32
  infer_instance

def totalRefund
    (codeInsertExecutionRefund : Nat)
    (refundCounter destroyCount : Int)
    (destroyRefund : Nat)
    (isError shouldRevert : Bool) : Int :=
  let codeRefund := uint64ToInt64 codeInsertExecutionRefund
  let destroyRefundTotal := mulInt64 destroyCount (uint64ToInt64 destroyRefund)
  if !isError && !shouldRevert then
    addInt64 codeRefund (addInt64 refundCounter destroyRefundTotal)
  else
    codeRefund

def applySignedRefund (gasUsedBeforeRefund : Nat) (refund : Int) : Nat :=
  if 0 ≤ refund then
    subUInt64 gasUsedBeforeRefund (int64ToUInt64 refund)
  else
    addUInt64 gasUsedBeforeRefund (int64ToUInt64 (negInt64 refund))

/-- Handwritten scalar settlement semantics, including C# unchecked integer behavior. -/
def settle (raw : Input) : Result :=
  let transactionGasLimit := normalizeUInt64 raw.transactionGasLimit
  let preRefundGas := normalizeUInt64 raw.preRefundGas
  let refundCounter := wrapInt64 raw.refundCounter
  let destroyCount := wrapInt32 raw.destroyCount
  let destroyRefund := normalizeUInt64 raw.destroyRefund
  let codeInsertExecutionRefund := normalizeUInt64 raw.codeInsertExecutionRefund
  let calldataFloorGas := normalizeUInt64 raw.calldataFloorGas
  let stateGasUsed := wrapInt64 raw.stateGasUsed
  let refundQuotient := normalizeUInt64 raw.refundQuotient
  let gasUsedBeforeRefund := if raw.isError then transactionGasLimit else preRefundGas
  let refund := min
    (uint64ToInt64 (gasUsedBeforeRefund / refundQuotient))
    (totalRefund codeInsertExecutionRefund refundCounter destroyCount destroyRefund
      raw.isError raw.shouldRevert)
  let operationGas := applySignedRefund gasUsedBeforeRefund refund
  let spentGas := max operationGas calldataFloorGas
  let blockStateGas := if raw.isEip8037Enabled then int64ToUInt64 stateGasUsed else 0
  let blockGas :=
    if raw.isEip8037Enabled then
      max (saturatingSubUInt64 gasUsedBeforeRefund blockStateGas) calldataFloorGas
    else if raw.isEip7778Enabled then
      max gasUsedBeforeRefund calldataFloorGas
    else
      0
  { spentGas
    operationGas
    blockGas
    blockStateGas
    maxUsedGas := max gasUsedBeforeRefund calldataFloorGas
    gasRefund := if 0 < refund then int64ToUInt64 refund else 0 }

end Eip803x.TransactionSettlement
