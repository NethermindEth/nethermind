-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

-- This file is generated. Do not edit.
-- Extractor version: 1.2.0
-- Roslyn compiler version: 5.6.0.0
-- Production source: src/Nethermind/Nethermind.Evm/GasPolicy/TransactionGasInitializationKernel.cs
-- Production source SHA-256: 65ab0742596ea7ebf49d2c79601eae00152d8d039ac8e417076bf63123be1e82
-- Canonical transaction-gas IR SHA-256: a48a5bf063439cd9a21f4eee0e9b2509dfe07a4fa98f075aa5430db59f900f52

namespace Eip803x.Generated.TransactionGasInitializationKernel

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

def addUInt64 (left right : Nat) : Nat :=
  wrapUInt64 ((left : Int) + (right : Int))

def subUInt64 (left right : Nat) : Nat :=
  wrapUInt64 ((left : Int) - (right : Int))

def int64ToUInt64 (value : Int) : Nat :=
  wrapUInt64 value

def uint64ToInt64 (value : Nat) : Int :=
  wrapInt64 (value : Int)

inductive TransactionGasInitializationOutcome where
  | success
  | intrinsicGasExceedsLimit
  deriving DecidableEq, Repr

structure TransactionGasInitializationResult where
  outcome : TransactionGasInitializationOutcome
  value : Nat
  stateReservoir : Int
  stateGasUsed : Int
  stateGasSpill : Int
  stateGasSpillRefunded : Int
  deriving DecidableEq, Repr

def tryCreateNormalized
    (gasLimit : Nat)
    (intrinsicExecutionGas : Nat)
    (intrinsicStateGas : Int)
    (eip8037Enabled : Bool)
    (executionGasLimitCap : Nat)
    : TransactionGasInitializationResult :=
  let intrinsicTotal : Nat := addUInt64 intrinsicExecutionGas (int64ToUInt64 intrinsicStateGas)
  if (gasLimit < intrinsicTotal) then
    { outcome := .intrinsicGasExceedsLimit
      value := 0
      stateReservoir := 0
      stateGasUsed := 0
      stateGasSpill := 0
      stateGasSpillRefunded := 0 }
  else
    let availableGas : Nat := subUInt64 gasLimit intrinsicTotal
    let gasLeft : Nat := availableGas
    let gasLeftAfterEip8037 : Nat := if eip8037Enabled then
      let executionGasAfterIntrinsicCap : Nat := if (intrinsicExecutionGas >= executionGasLimitCap) then
        0
      else
        subUInt64 executionGasLimitCap intrinsicExecutionGas
      min availableGas executionGasAfterIntrinsicCap
    else
      gasLeft
    let stateReservoir : Nat := subUInt64 availableGas gasLeftAfterEip8037
    { outcome := .success
      value := gasLeftAfterEip8037
      stateReservoir := uint64ToInt64 stateReservoir
      stateGasUsed := intrinsicStateGas
      stateGasSpill := 0
      stateGasSpillRefunded := 0 }

def tryCreate
    (gasLimit : Nat)
    (intrinsicExecutionGas : Nat)
    (intrinsicStateGas : Int)
    (eip8037Enabled : Bool)
    (executionGasLimitCap : Nat)
    : TransactionGasInitializationResult :=
  tryCreateNormalized
    (normalizeUInt64 gasLimit)
    (normalizeUInt64 intrinsicExecutionGas)
    (wrapInt64 intrinsicStateGas)
    eip8037Enabled
    (normalizeUInt64 executionGasLimitCap)

def combineNormalized
    (blockExecutionGas : Nat)
    (blockStateGas : Nat)
    : Nat :=
  max blockExecutionGas blockStateGas

def combine
    (blockExecutionGas : Nat)
    (blockStateGas : Nat)
    : Nat :=
  combineNormalized
    (normalizeUInt64 blockExecutionGas)
    (normalizeUInt64 blockStateGas)

end Eip803x.Generated.TransactionGasInitializationKernel
