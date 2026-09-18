-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

-- This file is generated. Do not edit.
-- Extractor version: 1.2.0
-- Roslyn compiler version: 5.6.0.0
-- Production source: src/Nethermind/Nethermind.Evm/GasPolicy/StateGasTransitionKernel.cs
-- Production source SHA-256: 4a31dc76fb8284a8ea5994de9a63810abe0fd7efbe6add4aa8b3d6cb564a75cc
-- Canonical transition IR SHA-256: d3a920f44b72d22500d8184f79843b38c02840bc86d671b33c6ad7b79cd03583

namespace Eip803x.Generated.StateGasTransitionKernel

def uint64Modulus : Nat := 2 ^ 64
def uint64Max : Nat := uint64Modulus - 1
def int64Modulus : Int := (uint64Modulus : Int)
def int64SignBit : Int := 2 ^ 63
def int64Min : Int := -int64SignBit
def int64Max : Int := int64SignBit - 1

def normalizeUInt64 (value : Nat) : Nat :=
  if value <= uint64Max then value else value % uint64Modulus

def wrapUInt64 (value : Int) : Nat :=
  if 0 <= value ∧ value <= (uint64Max : Int) then
    Int.toNat value
  else
    Int.toNat (value % int64Modulus)

def wrapInt64 (value : Int) : Int :=
  if int64Min <= value ∧ value <= int64Max then
    value
  else
    let residue := value % int64Modulus
    if residue < int64SignBit then residue else residue - int64Modulus

def addInt64 (left right : Int) : Int :=
  wrapInt64 (left + right)

def subInt64 (left right : Int) : Int :=
  wrapInt64 (left - right)

def addUInt64 (left right : Nat) : Nat :=
  wrapUInt64 ((left : Int) + (right : Int))

def int64ToUInt64 (value : Int) : Nat :=
  wrapUInt64 value

structure StateGasTransitionResult where
  value : Nat
  stateReservoir : Int
  stateGasUsed : Int
  stateGasSpill : Int
  stateGasSpillRefunded : Int
  unappliedAmount : Int
  deriving DecidableEq, Repr

private def positivePartNormalized
    (value : Int)
    : Int :=
  if (value > 0) then
    value
  else
    0

private def getUnrefundedStateGasSpillNormalized
    (stateGasSpill : Int)
    (stateGasSpillRefunded : Int)
    : Int :=
  let unrefundedSpill : Int := subInt64 stateGasSpill stateGasSpillRefunded
  positivePartNormalized unrefundedSpill

private def addStateGasRefundToReservoirNormalized
    (value : Nat)
    (stateReservoir : Int)
    (stateGasUsed : Int)
    (stateGasSpill : Int)
    (stateGasSpillRefunded : Int)
    (amount : Int)
    (trackSpillRefund : Bool)
    : StateGasTransitionResult :=
  let toGasLeft : Int := if trackSpillRefund then
    min amount (getUnrefundedStateGasSpillNormalized stateGasSpill stateGasSpillRefunded)
  else
    0
  { value := addUInt64 value (int64ToUInt64 toGasLeft)
    stateReservoir := addInt64 stateReservoir (subInt64 amount toGasLeft)
    stateGasUsed := stateGasUsed
    stateGasSpill := stateGasSpill
    stateGasSpillRefunded := if trackSpillRefund then
    addInt64 stateGasSpillRefunded toGasLeft
  else
    stateGasSpillRefunded
    unappliedAmount := 0 }

def addStateGasRefundToReservoir
    (value : Nat)
    (stateReservoir : Int)
    (stateGasUsed : Int)
    (stateGasSpill : Int)
    (stateGasSpillRefunded : Int)
    (amount : Int)
    (trackSpillRefund : Bool)
    : StateGasTransitionResult :=
  addStateGasRefundToReservoirNormalized
    (normalizeUInt64 value)
    (wrapInt64 stateReservoir)
    (wrapInt64 stateGasUsed)
    (wrapInt64 stateGasSpill)
    (wrapInt64 stateGasSpillRefunded)
    (wrapInt64 amount)
    trackSpillRefund

private def clampToRefundAmountNormalized
    (stateReservoir : Int)
    (amount : Int)
    : Int :=
  if (stateReservoir <= 0) then
    0
  else
    if (stateReservoir >= amount) then
      amount
    else
      stateReservoir

private def discardStateGasNormalized
    (value : Nat)
    (stateReservoir : Int)
    (stateGasUsed : Int)
    (stateGasSpill : Int)
    (stateGasSpillRefunded : Int)
    (amount : Int)
    (stateGasFloor : Int)
    : StateGasTransitionResult :=
  let discardableStateGas : Int := positivePartNormalized (subInt64 stateGasUsed stateGasFloor)
  let appliedRefund : Int := min amount discardableStateGas
  { value := value
    stateReservoir := stateReservoir
    stateGasUsed := subInt64 stateGasUsed appliedRefund
    stateGasSpill := stateGasSpill
    stateGasSpillRefunded := stateGasSpillRefunded
    unappliedAmount := subInt64 amount appliedRefund }

def discardStateGas
    (value : Nat)
    (stateReservoir : Int)
    (stateGasUsed : Int)
    (stateGasSpill : Int)
    (stateGasSpillRefunded : Int)
    (amount : Int)
    (stateGasFloor : Int)
    : StateGasTransitionResult :=
  discardStateGasNormalized
    (normalizeUInt64 value)
    (wrapInt64 stateReservoir)
    (wrapInt64 stateGasUsed)
    (wrapInt64 stateGasSpill)
    (wrapInt64 stateGasSpillRefunded)
    (wrapInt64 amount)
    (wrapInt64 stateGasFloor)

private def refundNormalized
    (value : Nat)
    (stateReservoir : Int)
    (stateGasUsed : Int)
    (stateGasSpill : Int)
    (stateGasSpillRefunded : Int)
    (childValue : Nat)
    (childStateReservoir : Int)
    (childStateGasUsed : Int)
    (childStateGasSpill : Int)
    (childStateGasSpillRefunded : Int)
    : StateGasTransitionResult :=
  { value := addUInt64 value childValue
    stateReservoir := addInt64 stateReservoir childStateReservoir
    stateGasUsed := addInt64 stateGasUsed childStateGasUsed
    stateGasSpill := addInt64 stateGasSpill childStateGasSpill
    stateGasSpillRefunded := addInt64 stateGasSpillRefunded childStateGasSpillRefunded
    unappliedAmount := 0 }

def refund
    (value : Nat)
    (stateReservoir : Int)
    (stateGasUsed : Int)
    (stateGasSpill : Int)
    (stateGasSpillRefunded : Int)
    (childValue : Nat)
    (childStateReservoir : Int)
    (childStateGasUsed : Int)
    (childStateGasSpill : Int)
    (childStateGasSpillRefunded : Int)
    : StateGasTransitionResult :=
  refundNormalized
    (normalizeUInt64 value)
    (wrapInt64 stateReservoir)
    (wrapInt64 stateGasUsed)
    (wrapInt64 stateGasSpill)
    (wrapInt64 stateGasSpillRefunded)
    (normalizeUInt64 childValue)
    (wrapInt64 childStateReservoir)
    (wrapInt64 childStateGasUsed)
    (wrapInt64 childStateGasSpill)
    (wrapInt64 childStateGasSpillRefunded)

private def refundStateGasNormalized
    (value : Nat)
    (stateReservoir : Int)
    (stateGasUsed : Int)
    (stateGasSpill : Int)
    (stateGasSpillRefunded : Int)
    (amount : Int)
    (stateGasFloor : Int)
    (trackSpillRefund : Bool)
    : StateGasTransitionResult :=
  let refundableStateGas : Int := positivePartNormalized (subInt64 stateGasUsed stateGasFloor)
  let appliedRefund : Int := min amount refundableStateGas
  let toGasLeft : Int := if trackSpillRefund then
    min appliedRefund (getUnrefundedStateGasSpillNormalized stateGasSpill stateGasSpillRefunded)
  else
    0
  { value := addUInt64 value (int64ToUInt64 toGasLeft)
    stateReservoir := addInt64 stateReservoir (subInt64 appliedRefund toGasLeft)
    stateGasUsed := subInt64 stateGasUsed appliedRefund
    stateGasSpill := stateGasSpill
    stateGasSpillRefunded := if trackSpillRefund then
    addInt64 stateGasSpillRefunded toGasLeft
  else
    stateGasSpillRefunded
    unappliedAmount := 0 }

def refundStateGas
    (value : Nat)
    (stateReservoir : Int)
    (stateGasUsed : Int)
    (stateGasSpill : Int)
    (stateGasSpillRefunded : Int)
    (amount : Int)
    (stateGasFloor : Int)
    (trackSpillRefund : Bool)
    : StateGasTransitionResult :=
  refundStateGasNormalized
    (normalizeUInt64 value)
    (wrapInt64 stateReservoir)
    (wrapInt64 stateGasUsed)
    (wrapInt64 stateGasSpill)
    (wrapInt64 stateGasSpillRefunded)
    (wrapInt64 amount)
    (wrapInt64 stateGasFloor)
    trackSpillRefund

private def removeStateGasRefundFromReservoirNormalized
    (value : Nat)
    (stateReservoir : Int)
    (stateGasUsed : Int)
    (stateGasSpill : Int)
    (stateGasSpillRefunded : Int)
    (amount : Int)
    : StateGasTransitionResult :=
  let fromReservoir : Int := clampToRefundAmountNormalized stateReservoir amount
  let remainingAmount : Int := subInt64 amount fromReservoir
  let nextStateReservoir : Int := subInt64 stateReservoir fromReservoir
  if (remainingAmount <= 0) then
    { value := value
      stateReservoir := nextStateReservoir
      stateGasUsed := stateGasUsed
      stateGasSpill := stateGasSpill
      stateGasSpillRefunded := stateGasSpillRefunded
      unappliedAmount := 0 }
  else
    let fromUsed : Int := min remainingAmount stateGasUsed
    { value := value
      stateReservoir := subInt64 nextStateReservoir (subInt64 remainingAmount fromUsed)
      stateGasUsed := subInt64 stateGasUsed fromUsed
      stateGasSpill := stateGasSpill
      stateGasSpillRefunded := stateGasSpillRefunded
      unappliedAmount := 0 }

def removeStateGasRefundFromReservoir
    (value : Nat)
    (stateReservoir : Int)
    (stateGasUsed : Int)
    (stateGasSpill : Int)
    (stateGasSpillRefunded : Int)
    (amount : Int)
    : StateGasTransitionResult :=
  removeStateGasRefundFromReservoirNormalized
    (normalizeUInt64 value)
    (wrapInt64 stateReservoir)
    (wrapInt64 stateGasUsed)
    (wrapInt64 stateGasSpill)
    (wrapInt64 stateGasSpillRefunded)
    (wrapInt64 amount)

private def repayStateGasSpillNormalized
    (value : Nat)
    (stateReservoir : Int)
    (stateGasUsed : Int)
    (stateGasSpill : Int)
    (stateGasSpillRefunded : Int)
    : StateGasTransitionResult :=
  let repayment : Int := min stateReservoir (getUnrefundedStateGasSpillNormalized stateGasSpill stateGasSpillRefunded)
  if (repayment <= 0) then
    { value := value
      stateReservoir := stateReservoir
      stateGasUsed := stateGasUsed
      stateGasSpill := stateGasSpill
      stateGasSpillRefunded := stateGasSpillRefunded
      unappliedAmount := 0 }
  else
    { value := addUInt64 value (int64ToUInt64 repayment)
      stateReservoir := subInt64 stateReservoir repayment
      stateGasUsed := stateGasUsed
      stateGasSpill := stateGasSpill
      stateGasSpillRefunded := addInt64 stateGasSpillRefunded repayment
      unappliedAmount := 0 }

def repayStateGasSpill
    (value : Nat)
    (stateReservoir : Int)
    (stateGasUsed : Int)
    (stateGasSpill : Int)
    (stateGasSpillRefunded : Int)
    : StateGasTransitionResult :=
  repayStateGasSpillNormalized
    (normalizeUInt64 value)
    (wrapInt64 stateReservoir)
    (wrapInt64 stateGasUsed)
    (wrapInt64 stateGasSpill)
    (wrapInt64 stateGasSpillRefunded)

private def restoreChildStateGasNormalized
    (parentValue : Nat)
    (parentStateReservoir : Int)
    (parentStateGasUsed : Int)
    (parentStateGasSpill : Int)
    (parentStateGasSpillRefunded : Int)
    (childStateReservoir : Int)
    (childStateGasUsed : Int)
    (childStateGasSpill : Int)
    (childStateGasSpillRefunded : Int)
    : StateGasTransitionResult :=
  let childNetSpill : Int := getUnrefundedStateGasSpillNormalized childStateGasSpill childStateGasSpillRefunded
  { value := addUInt64 parentValue (int64ToUInt64 childNetSpill)
    stateReservoir := subInt64 (addInt64 (addInt64 parentStateReservoir childStateReservoir) childStateGasUsed) childNetSpill
    stateGasUsed := parentStateGasUsed
    stateGasSpill := parentStateGasSpill
    stateGasSpillRefunded := parentStateGasSpillRefunded
    unappliedAmount := 0 }

def restoreChildStateGas
    (parentValue : Nat)
    (parentStateReservoir : Int)
    (parentStateGasUsed : Int)
    (parentStateGasSpill : Int)
    (parentStateGasSpillRefunded : Int)
    (childStateReservoir : Int)
    (childStateGasUsed : Int)
    (childStateGasSpill : Int)
    (childStateGasSpillRefunded : Int)
    : StateGasTransitionResult :=
  restoreChildStateGasNormalized
    (normalizeUInt64 parentValue)
    (wrapInt64 parentStateReservoir)
    (wrapInt64 parentStateGasUsed)
    (wrapInt64 parentStateGasSpill)
    (wrapInt64 parentStateGasSpillRefunded)
    (wrapInt64 childStateReservoir)
    (wrapInt64 childStateGasUsed)
    (wrapInt64 childStateGasSpill)
    (wrapInt64 childStateGasSpillRefunded)

private def restoreChildStateGasOnHaltNormalized
    (parentValue : Nat)
    (parentStateReservoir : Int)
    (parentStateGasUsed : Int)
    (parentStateGasSpill : Int)
    (parentStateGasSpillRefunded : Int)
    (childStateReservoir : Int)
    (childStateGasUsed : Int)
    (childStateGasSpill : Int)
    (childStateGasSpillRefunded : Int)
    : StateGasTransitionResult :=
  let childNetSpill : Int := getUnrefundedStateGasSpillNormalized childStateGasSpill childStateGasSpillRefunded
  { value := parentValue
    stateReservoir := subInt64 (addInt64 (addInt64 parentStateReservoir childStateReservoir) childStateGasUsed) childNetSpill
    stateGasUsed := parentStateGasUsed
    stateGasSpill := parentStateGasSpill
    stateGasSpillRefunded := parentStateGasSpillRefunded
    unappliedAmount := 0 }

def restoreChildStateGasOnHalt
    (parentValue : Nat)
    (parentStateReservoir : Int)
    (parentStateGasUsed : Int)
    (parentStateGasSpill : Int)
    (parentStateGasSpillRefunded : Int)
    (childStateReservoir : Int)
    (childStateGasUsed : Int)
    (childStateGasSpill : Int)
    (childStateGasSpillRefunded : Int)
    : StateGasTransitionResult :=
  restoreChildStateGasOnHaltNormalized
    (normalizeUInt64 parentValue)
    (wrapInt64 parentStateReservoir)
    (wrapInt64 parentStateGasUsed)
    (wrapInt64 parentStateGasSpill)
    (wrapInt64 parentStateGasSpillRefunded)
    (wrapInt64 childStateReservoir)
    (wrapInt64 childStateGasUsed)
    (wrapInt64 childStateGasSpill)
    (wrapInt64 childStateGasSpillRefunded)

private def revertRefundToHaltNormalized
    (parentValue : Nat)
    (parentStateReservoir : Int)
    (parentStateGasUsed : Int)
    (parentStateGasSpill : Int)
    (parentStateGasSpillRefunded : Int)
    (childStateGasUsed : Int)
    (childStateGasSpill : Int)
    (childStateGasSpillRefunded : Int)
    : StateGasTransitionResult :=
  let childNetSpill : Int := getUnrefundedStateGasSpillNormalized childStateGasSpill childStateGasSpillRefunded
  { value := parentValue
    stateReservoir := subInt64 (addInt64 parentStateReservoir childStateGasUsed) childNetSpill
    stateGasUsed := subInt64 parentStateGasUsed childStateGasUsed
    stateGasSpill := subInt64 parentStateGasSpill childStateGasSpill
    stateGasSpillRefunded := subInt64 parentStateGasSpillRefunded childStateGasSpillRefunded
    unappliedAmount := 0 }

def revertRefundToHalt
    (parentValue : Nat)
    (parentStateReservoir : Int)
    (parentStateGasUsed : Int)
    (parentStateGasSpill : Int)
    (parentStateGasSpillRefunded : Int)
    (childStateGasUsed : Int)
    (childStateGasSpill : Int)
    (childStateGasSpillRefunded : Int)
    : StateGasTransitionResult :=
  revertRefundToHaltNormalized
    (normalizeUInt64 parentValue)
    (wrapInt64 parentStateReservoir)
    (wrapInt64 parentStateGasUsed)
    (wrapInt64 parentStateGasSpill)
    (wrapInt64 parentStateGasSpillRefunded)
    (wrapInt64 childStateGasUsed)
    (wrapInt64 childStateGasSpill)
    (wrapInt64 childStateGasSpillRefunded)

end Eip803x.Generated.StateGasTransitionKernel
