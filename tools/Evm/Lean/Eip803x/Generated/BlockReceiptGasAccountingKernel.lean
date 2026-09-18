-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

-- This file is generated. Do not edit.
-- Extractor version: 1.2.0
-- Roslyn compiler version: 5.6.0.0
-- Production source: src/Nethermind/Nethermind.Blockchain/Tracing/BlockReceiptGasAccountingKernel.cs
-- Production source SHA-256: 0882c798e6ffb2243735a9ec0cec5d442b20d51277feeba95e5b68ecfaf89e32
-- Canonical block-receipt-gas-accounting IR SHA-256: 5278c354453a3adab99df5a68b4475d9feab798eba1e4df42ee80bf34a259c41

import Eip803x.Generated.TransactionGasInitializationKernel

namespace Eip803x.Generated.BlockReceiptGasAccountingKernel

def uint64Max : Nat := Eip803x.Generated.TransactionGasInitializationKernel.uint64Max

def normalizeUInt64 (value : Nat) : Nat :=
  Eip803x.Generated.TransactionGasInitializationKernel.normalizeUInt64 value

def addUInt64 (left right : Nat) : Nat :=
  Eip803x.Generated.TransactionGasInitializationKernel.addUInt64 left right

/-- Source-validated `EthereumGasPolicy.CombineBlockGas` delegation. -/
def combineBlockGas (blockExecutionGas blockStateGas : Nat) : Nat :=
  Eip803x.Generated.TransactionGasInitializationKernel.combine blockExecutionGas blockStateGas

structure Result where
  cumulativeExecutionGas : Nat
  cumulativeStateGas : Nat
  cumulativeReceiptGas : Nat
  headerGasUsed : Nat
  deriving DecidableEq, Repr

def fromTotalsNormalized
    (cumulativeExecutionGas : Nat)
    (cumulativeStateGas : Nat)
    (cumulativeReceiptGas : Nat)
    : Result :=
  { cumulativeExecutionGas := cumulativeExecutionGas
    cumulativeStateGas := cumulativeStateGas
    cumulativeReceiptGas := cumulativeReceiptGas
    headerGasUsed := combineBlockGas (cumulativeExecutionGas) (cumulativeStateGas) }

def fromTotals
    (cumulativeExecutionGas : Nat)
    (cumulativeStateGas : Nat)
    (cumulativeReceiptGas : Nat)
    : Result :=
  fromTotalsNormalized (normalizeUInt64 cumulativeExecutionGas) (normalizeUInt64 cumulativeStateGas) (normalizeUInt64 cumulativeReceiptGas)

def accumulateNormalized
    (previousExecutionGas : Nat)
    (previousStateGas : Nat)
    (previousReceiptGas : Nat)
    (transactionExecutionGas : Nat)
    (transactionStateGas : Nat)
    (transactionPaidGas : Nat)
    : Result :=
  let cumulativeExecutionGas : Nat := addUInt64 (previousExecutionGas) (transactionExecutionGas)
  let cumulativeStateGas : Nat := addUInt64 (previousStateGas) (transactionStateGas)
  let cumulativeReceiptGas : Nat := addUInt64 (previousReceiptGas) (transactionPaidGas)
  fromTotalsNormalized (cumulativeExecutionGas) (cumulativeStateGas) (cumulativeReceiptGas)

def accumulate
    (previousExecutionGas : Nat)
    (previousStateGas : Nat)
    (previousReceiptGas : Nat)
    (transactionExecutionGas : Nat)
    (transactionStateGas : Nat)
    (transactionPaidGas : Nat)
    : Result :=
  accumulateNormalized (normalizeUInt64 previousExecutionGas) (normalizeUInt64 previousStateGas) (normalizeUInt64 previousReceiptGas) (normalizeUInt64 transactionExecutionGas) (normalizeUInt64 transactionStateGas) (normalizeUInt64 transactionPaidGas)

end Eip803x.Generated.BlockReceiptGasAccountingKernel
