-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

-- This file is generated. Do not edit.
-- Extractor version: 1.2.0
-- Roslyn compiler version: 5.6.0.0
-- Production source: src/Nethermind/Nethermind.Evm/GasPolicy/SStorePricingKernel.cs
-- Production source SHA-256: 6eb99aee5247c9d5d7c35659c41628d758b672923d0fc5ec76e7d5cb9a30a125
-- Canonical SSTORE-pricing IR SHA-256: 2b99d6fe91afff39dba20941535fd2b905db0785685a630e7a8b7c4a1c5d7b4f

namespace Eip803x.Generated.SStorePricingKernel

def uint64Modulus : Nat := 2 ^ 64
def uint64Max : Nat := uint64Modulus - 1
def int64Modulus : Int := (uint64Modulus : Int)
def int64SignBit : Int := 2 ^ 63
def int64Min : Int := -int64SignBit
def int64Max : Int := int64SignBit - 1

def normalizeUInt64 (value : Nat) : Nat :=
  if value <= uint64Max then value else value % uint64Modulus

def wrapInt64 (value : Int) : Int :=
  if int64Min <= value ∧ value <= int64Max then
    value
  else
    let residue := value % int64Modulus
    if residue < int64SignBit then residue else residue - int64Modulus

def uint64ToInt64 (value : Nat) : Int :=
  wrapInt64 (value : Int)

def negInt64 (value : Int) : Int :=
  wrapInt64 (-value)

inductive AccessStatus where
  | cold
  | warm
  deriving DecidableEq, Repr

structure Input where
  originalIsZero : Bool
  currentIsZero : Bool
  newIsZero : Bool
  currentSameAsOriginal : Bool
  newSameAsCurrent : Bool
  newSameAsOriginal : Bool
  deriving DecidableEq, Repr

structure AccessSchedule where
  coldStorageAccessGas : Nat
  warmAccessGas : Nat
  deriving DecidableEq, Repr

structure PostAccessSchedule where
  storageWriteGas : Nat
  storageClearRefund : Int
  storageSetStateGas : Int
  deriving DecidableEq, Repr

structure PostAccessResult where
  executionWriteGas : Nat
  storageClearRefund : Int
  storageClearRefundReversal : Int
  restoreOriginalRefund : Int
  stateGasCharge : Int
  stateGasRefund : Int
  deriving DecidableEq, Repr

structure Result where
  accessGas : Nat
  postAccess : PostAccessResult
  deriving DecidableEq, Repr

def priceAfterAccessNormalized
    (input : Input)
    (schedule : PostAccessSchedule) : PostAccessResult :=
  let writesFirstValue := !input.newSameAsCurrent && input.currentSameAsOriginal
  let restoreOriginalRefund := if input.newSameAsOriginal && !input.newSameAsCurrent then
    uint64ToInt64 schedule.storageWriteGas
  else
    0
  { executionWriteGas := if writesFirstValue then schedule.storageWriteGas else 0
    storageClearRefund :=
      if !input.originalIsZero && !input.currentIsZero && input.newIsZero then
        schedule.storageClearRefund
      else
        0
    storageClearRefundReversal :=
      if !input.originalIsZero && input.currentIsZero && !input.newIsZero then
        negInt64 schedule.storageClearRefund
      else
        0
    restoreOriginalRefund := restoreOriginalRefund
    stateGasCharge :=
      if input.originalIsZero && input.currentIsZero && !input.newIsZero then
        schedule.storageSetStateGas
      else
        0
    stateGasRefund :=
      if input.originalIsZero && !input.currentIsZero && input.newIsZero then
        schedule.storageSetStateGas
      else
        0 }

def priceAfterAccess (input : Input) (schedule : PostAccessSchedule) : PostAccessResult :=
  priceAfterAccessNormalized
    input
    { storageWriteGas := normalizeUInt64 schedule.storageWriteGas
      storageClearRefund := wrapInt64 schedule.storageClearRefund
      storageSetStateGas := wrapInt64 schedule.storageSetStateGas }

def priceNormalized
    (input : Input)
    (accessStatus : AccessStatus)
    (accessSchedule : AccessSchedule)
    (postAccessSchedule : PostAccessSchedule) : Result :=
  let accessGas := if accessStatus == .cold then
    accessSchedule.coldStorageAccessGas
  else
    accessSchedule.warmAccessGas
  let postAccess := priceAfterAccess input postAccessSchedule
  { accessGas := accessGas, postAccess := postAccess }

def price
    (input : Input)
    (accessStatus : AccessStatus)
    (accessSchedule : AccessSchedule)
    (postAccessSchedule : PostAccessSchedule) : Result :=
  priceNormalized
    input
    accessStatus
    { coldStorageAccessGas := normalizeUInt64 accessSchedule.coldStorageAccessGas
      warmAccessGas := normalizeUInt64 accessSchedule.warmAccessGas }
    postAccessSchedule

end Eip803x.Generated.SStorePricingKernel
