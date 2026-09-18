-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

-- This file is generated. Do not edit.
-- Extractor version: 1.2.0
-- Roslyn compiler version: 5.6.0.0
-- Production source: src/Nethermind/Nethermind.Evm/TransactionProcessing/TransactionSettlementKernel.cs
-- Production source SHA-256: 37add297aaaf07a4260b45c05c5f866a48f92eef2ac6fb16ef2bfd96103f8c44
-- Canonical transaction-settlement IR SHA-256: 51dfcde8e9441beae09b05699a31c2e9e8457674db74210e4474fa67f81c1ee7

namespace Eip803x.Generated.TransactionSettlementKernel

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
  if value <= uint64Max then value else value % uint64Modulus

def wrapUInt64 (value : Int) : Nat :=
  if 0 <= value ∧ value <= (uint64Max : Int) then
    Int.toNat value
  else
    Int.toNat (value % (uint64Modulus : Int))

def wrapInt64 (value : Int) : Int :=
  if int64Min <= value ∧ value <= int64Max then
    value
  else
    let residue := value % int64Modulus
    if residue < int64SignBit then residue else residue - int64Modulus

def wrapInt32 (value : Int) : Int :=
  if int32Min <= value ∧ value <= int32Max then
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

structure TransactionSettlementResult where
  spentGas : Nat
  operationGas : Nat
  blockGas : Nat
  blockStateGas : Nat
  maxUsedGas : Nat
  gasRefund : Nat
  deriving DecidableEq, Repr

def calculateNormalized
    (transactionGasLimit preRefundGas : Nat)
    (refundCounter destroyCount : Int)
    (destroyRefund codeInsertExecutionRefund calldataFloorGas : Nat)
    (stateGasUsed : Int)
    (refundQuotient : Nat)
    (isError shouldRevert isEip8037Enabled isEip7778Enabled : Bool) :
    TransactionSettlementResult :=
  let gasUsedBeforeRefund := if isError then transactionGasLimit else preRefundGas
  let codeRefund := uint64ToInt64 codeInsertExecutionRefund
  let destroyRefundTotal := mulInt64 destroyCount (uint64ToInt64 destroyRefund)
  let totalToRefund :=
    if !isError && !shouldRevert then
      addInt64 codeRefund (addInt64 refundCounter destroyRefundTotal)
    else
      codeRefund
  let refund := min (uint64ToInt64 (gasUsedBeforeRefund / refundQuotient)) totalToRefund
  let operationGas :=
    if 0 <= refund then
      subUInt64 gasUsedBeforeRefund (int64ToUInt64 refund)
    else
      addUInt64 gasUsedBeforeRefund (int64ToUInt64 (negInt64 refund))
  let spentGas := max operationGas calldataFloorGas
  let blockStateGas := if isEip8037Enabled then int64ToUInt64 stateGasUsed else 0
  let blockGas :=
    if isEip8037Enabled then
      max (saturatingSubUInt64 gasUsedBeforeRefund blockStateGas) calldataFloorGas
    else if isEip7778Enabled then
      max gasUsedBeforeRefund calldataFloorGas
    else
      0
  { spentGas
    operationGas
    blockGas
    blockStateGas
    maxUsedGas := max gasUsedBeforeRefund calldataFloorGas
    gasRefund := if 0 < refund then int64ToUInt64 refund else 0 }

def calculate
    (transactionGasLimit preRefundGas : Nat)
    (refundCounter destroyCount : Int)
    (destroyRefund codeInsertExecutionRefund calldataFloorGas : Nat)
    (stateGasUsed : Int)
    (refundQuotient : Nat)
    (isError shouldRevert isEip8037Enabled isEip7778Enabled : Bool) :
    TransactionSettlementResult :=
  calculateNormalized
    (normalizeUInt64 transactionGasLimit)
    (normalizeUInt64 preRefundGas)
    (wrapInt64 refundCounter)
    (wrapInt32 destroyCount)
    (normalizeUInt64 destroyRefund)
    (normalizeUInt64 codeInsertExecutionRefund)
    (normalizeUInt64 calldataFloorGas)
    (wrapInt64 stateGasUsed)
    (normalizeUInt64 refundQuotient)
    isError shouldRevert isEip8037Enabled isEip7778Enabled

end Eip803x.Generated.TransactionSettlementKernel
