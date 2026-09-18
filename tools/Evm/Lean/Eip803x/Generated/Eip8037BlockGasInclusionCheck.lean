-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

-- This file is generated. Do not edit.
-- Extractor version: 1.2.0
-- Roslyn compiler version: 5.6.0.0
-- Production source: src/Nethermind/Nethermind.Evm/GasPolicy/Eip8037BlockGasInclusionCheck.cs
-- Production source SHA-256: 79e4c84096c72bd0241553f114f08973279eb841cfa469f944566fb9490afe4e
-- Canonical block-gas-inclusion IR SHA-256: 42e002a586312b8bd704d293caf576c8bcdd299df551b1b0488793c02939e0c9

namespace Eip803x.Generated.Eip8037BlockGasInclusionCheck

def uint64Modulus : Nat := 2 ^ 64
def uint64Max : Nat := uint64Modulus - 1

def normalizeUInt64 (value : Nat) : Nat :=
  if value <= uint64Max then value else value % uint64Modulus

def subUInt64 (left right : Nat) : Nat :=
  if left >= right then left - right else uint64Modulus - (right - left)

def saturatingSubUInt64 (left right : Nat) : Nat :=
  if left > right then subUInt64 left right else 0

inductive Outcome where
  | ok
  | executionDimensionExceeded
  | stateDimensionExceeded
  deriving DecidableEq, Repr

def txMaxGasLimit : Nat := 16777216

def validateNormalized
    (blockGasLimit : Nat)
    (cumulativeBlockExecution : Nat)
    (cumulativeBlockState : Nat)
    (txGas : Nat)
    : Outcome :=
  if cumulativeBlockExecution > blockGasLimit then
    .executionDimensionExceeded
  else
    if cumulativeBlockState > blockGasLimit then
      .stateDimensionExceeded
    else
      let executionAvailable : Nat := subUInt64 (blockGasLimit) (cumulativeBlockExecution)
      let stateAvailable : Nat := subUInt64 (blockGasLimit) (cumulativeBlockState)
      let worstCaseExecution : Nat := min (txMaxGasLimit) (txGas)
      if worstCaseExecution > executionAvailable then
        .executionDimensionExceeded
      else
        if txGas > stateAvailable then
          .stateDimensionExceeded
        else
          .ok

def validate
    (blockGasLimit : Nat)
    (cumulativeBlockExecution : Nat)
    (cumulativeBlockState : Nat)
    (txGas : Nat)
    : Outcome :=
  validateNormalized (normalizeUInt64 blockGasLimit) (normalizeUInt64 cumulativeBlockExecution) (normalizeUInt64 cumulativeBlockState) (normalizeUInt64 txGas)

def calculateBlockExecutionGasNormalized
    (preRefundGas : Nat)
    (blockStateGas : Nat)
    (calldataFloor : Nat)
    : Nat :=
  max (saturatingSubUInt64 (preRefundGas) (blockStateGas)) (calldataFloor)

def calculateBlockExecutionGas
    (preRefundGas : Nat)
    (blockStateGas : Nat)
    (calldataFloor : Nat)
    : Nat :=
  calculateBlockExecutionGasNormalized (normalizeUInt64 preRefundGas) (normalizeUInt64 blockStateGas) (normalizeUInt64 calldataFloor)

end Eip803x.Generated.Eip8037BlockGasInclusionCheck
