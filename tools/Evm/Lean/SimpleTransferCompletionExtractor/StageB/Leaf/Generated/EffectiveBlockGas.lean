-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only
-- Generated from the source-admitted positional UInt64 getter. Do not edit.

namespace SimpleTransferCompletionExtractor.StageB.Leaf.Generated

structure GasConsumed where
  spentGas : Nat
  operationGas : Nat
  blockGas : Nat
  blockStateGas : Nat
  maxUsedGas : Nat
  gasRefund : Nat
  deriving DecidableEq, Repr

def effectiveBlockGas (gas : GasConsumed) : Nat :=
  if gas.blockGas > 0 || gas.blockStateGas > 0 then gas.blockGas else gas.spentGas

end SimpleTransferCompletionExtractor.StageB.Leaf.Generated
