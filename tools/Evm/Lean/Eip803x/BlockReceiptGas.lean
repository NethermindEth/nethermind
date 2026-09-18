-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.TransactionGas

namespace Eip803x
namespace BlockReceiptGas

/-- The three independent cumulative counters held by `BlockReceiptsTracer`. -/
structure Totals where
  executionGas : Nat
  stateGas : Nat
  receiptGas : Nat
  deriving DecidableEq, Repr

/-- The transaction projection consumed by the block receipt accounting fold. -/
structure Delta where
  executionGas : Nat
  stateGas : Nat
  paidGas : Nat
  deriving DecidableEq, Repr

/-- The stored counters together with the consensus header gas projection. -/
structure Result where
  totals : Totals
  headerGasUsed : Nat
  deriving DecidableEq, Repr

def fromTotals (totals : Totals) : Result :=
  { totals
    headerGasUsed := max totals.executionGas totals.stateGas }

def accumulate (previous : Totals) (transaction : Delta) : Result :=
  fromTotals
    { executionGas := previous.executionGas + transaction.executionGas
      stateGas := previous.stateGas + transaction.stateGas
      receiptGas := previous.receiptGas + transaction.paidGas }

theorem accumulate_execution (previous : Totals) (transaction : Delta) :
    (accumulate previous transaction).totals.executionGas =
      previous.executionGas + transaction.executionGas := rfl

theorem accumulate_state (previous : Totals) (transaction : Delta) :
    (accumulate previous transaction).totals.stateGas =
      previous.stateGas + transaction.stateGas := rfl

theorem accumulate_receipt (previous : Totals) (transaction : Delta) :
    (accumulate previous transaction).totals.receiptGas =
      previous.receiptGas + transaction.paidGas := rfl

theorem accumulate_header (previous : Totals) (transaction : Delta) :
    (accumulate previous transaction).headerGasUsed =
      max (previous.executionGas + transaction.executionGas)
        (previous.stateGas + transaction.stateGas) := rfl

theorem fromTotals_preserves_totals (totals : Totals) :
    (fromTotals totals).totals = totals := rfl

theorem fromTotals_header (totals : Totals) :
    (fromTotals totals).headerGasUsed =
      max totals.executionGas totals.stateGas := rfl

theorem fromTotals_idempotent (totals : Totals) :
    fromTotals (fromTotals totals).totals = fromTotals totals := rfl

def asTransactionBlockGas (totals : Totals) : TransactionGas.BlockGas :=
  { executionGasUsed := totals.executionGas
    stateGasUsed := totals.stateGas
    cumulativeReceiptGasUsed := totals.receiptGas }

def deltaAsSettlement (delta : Delta) : TransactionGas.Settlement :=
  { gasUsedBeforeRefund := delta.executionGas
    gasRefund := 0
    gasUsedAfterRefund := delta.paidGas
    paidGas := delta.paidGas
    stateGas := delta.stateGas
    executionGas := delta.executionGas }

/-- The block receipt fold is exactly the common transaction-gas block fold projection. -/
theorem accumulate_agrees_with_transaction_block_fold
    (previous : Totals) (transaction : Delta) :
    asTransactionBlockGas (accumulate previous transaction).totals =
      TransactionGas.applyUser (asTransactionBlockGas previous)
        (deltaAsSettlement transaction) := rfl

/-- Header gas is independent of the post-refund receipt counter. -/
theorem header_independent_of_receipt_gas
    (executionGas stateGas firstReceiptGas secondReceiptGas : Nat) :
    (fromTotals { executionGas, stateGas, receiptGas := firstReceiptGas }).headerGasUsed =
      (fromTotals { executionGas, stateGas, receiptGas := secondReceiptGas }).headerGasUsed := rfl

end BlockReceiptGas
end Eip803x
