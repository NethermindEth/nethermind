-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

-- This file is generated. Do not edit.
-- Extractor version: 1.2.0
-- Roslyn compiler version: 5.6.0.0
-- Production source: src/Nethermind/Nethermind.Evm/GasPolicy/StateGasChargeKernel.cs
-- Production source SHA-256: ec3276958cbc6b0255fadedf15dbe49691195aa95570b332937f8d1703b103fd
-- Normalized IR SHA-256: 6e2fc2cf4904f95bf017a22056ce55da9c2d518c5ee5af759ebf2e380d88b690

namespace Eip803x.Generated.StateGasChargeKernel

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

def subUInt64 (left right : Nat) : Nat :=
  wrapUInt64 ((left : Int) - (right : Int))

def int64ToUInt64 (value : Int) : Nat :=
  wrapUInt64 value

def uint64ToInt64 (value : Nat) : Int :=
  wrapInt64 (value : Int)

inductive StateGasChargeOutcome where
  | success
  | outOfGas
  deriving DecidableEq, Repr

structure StateGasChargeResult where
  outcome : StateGasChargeOutcome
  value : Nat
  stateReservoir : Int
  stateGasUsed : Int
  stateGasSpill : Int
  stateGasSpillRefunded : Int
  deriving DecidableEq, Repr

private def calculateSpillNormalized
    (stateReservoir : Int)
    (stateGasCost : Int) : Nat :=
  if (stateGasCost <= 0) then
    0
  else
    if (stateReservoir <= 0) then
      int64ToUInt64 stateGasCost
    else
      if (stateGasCost > stateReservoir) then int64ToUInt64 (subInt64 stateGasCost stateReservoir) else 0

def calculateSpill (stateReservoir stateGasCost : Int) : Nat :=
  calculateSpillNormalized (wrapInt64 stateReservoir) (wrapInt64 stateGasCost)

private def tryChargeNormalized
    (value : Nat)
    (stateReservoir : Int)
    (stateGasUsed : Int)
    (stateGasSpill : Int)
    (stateGasSpillRefunded : Int)
    (stateGasCost : Int) : StateGasChargeResult :=
  if (stateReservoir >= stateGasCost) then
    { outcome := .success
      value := value
      stateReservoir := subInt64 stateReservoir stateGasCost
      stateGasUsed := addInt64 stateGasUsed stateGasCost
      stateGasSpill := stateGasSpill
      stateGasSpillRefunded := stateGasSpillRefunded }
  else
    let spillAmount : Nat := calculateSpillNormalized stateReservoir stateGasCost
    if (value < spillAmount) then
      { outcome := .outOfGas
        value := value
        stateReservoir := stateReservoir
        stateGasUsed := stateGasUsed
        stateGasSpill := stateGasSpill
        stateGasSpillRefunded := stateGasSpillRefunded }
    else
      { outcome := .success
        value := subUInt64 value spillAmount
        stateReservoir := min 0 stateReservoir
        stateGasUsed := addInt64 stateGasUsed stateGasCost
        stateGasSpill := addInt64 stateGasSpill (uint64ToInt64 spillAmount)
        stateGasSpillRefunded := stateGasSpillRefunded }

def tryCharge
    (value : Nat)
    (stateReservoir : Int)
    (stateGasUsed : Int)
    (stateGasSpill : Int)
    (stateGasSpillRefunded : Int)
    (stateGasCost : Int) : StateGasChargeResult :=
  tryChargeNormalized
    (normalizeUInt64 value)
    (wrapInt64 stateReservoir)
    (wrapInt64 stateGasUsed)
    (wrapInt64 stateGasSpill)
    (wrapInt64 stateGasSpillRefunded)
    (wrapInt64 stateGasCost)

end Eip803x.Generated.StateGasChargeKernel
