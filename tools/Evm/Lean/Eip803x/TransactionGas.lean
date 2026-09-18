-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Lean.Elab.Tactic.Omega

namespace Eip803x
namespace TransactionGas

/--
Inputs to the production EIP-8037 transaction gas split. `intrinsicGas` is the
execution-gas component. `intrinsicStateGas` is an independently supplied
state-gas baseline; it is zero at pinned-EIP transaction admission and may be
nonzero in an adapter state that has folded pre-execution state charges.
-/
structure InitializationInput where
  txGas : Nat
  intrinsicGas : Nat
  intrinsicStateGas : Nat := 0
  txMaxGasLimit : Nat
  deriving DecidableEq, Repr

/-- The split is interpreted only when the total baseline is affordable and execution fits its cap. -/
def InitializationInput.Valid (input : InitializationInput) : Prop :=
  input.intrinsicGas + input.intrinsicStateGas ≤ input.txGas ∧
    input.intrinsicGas ≤ input.txMaxGasLimit

instance (input : InitializationInput) : Decidable input.Valid :=
  inferInstanceAs (Decidable
    (input.intrinsicGas + input.intrinsicStateGas ≤ input.txGas ∧
      input.intrinsicGas ≤ input.txMaxGasLimit))

/-- The complete production gas-policy projection established by initialization. -/
structure InitialGas where
  evmGas : Nat
  executionGasBudget : Nat
  gasLeft : Nat
  stateGasReservoir : Nat
  stateGasUsed : Nat := 0
  stateGasSpill : Nat := 0
  stateGasSpillRefunded : Nat := 0
  deriving DecidableEq, Repr

def totalIntrinsicGas (input : InitializationInput) : Nat :=
  input.intrinsicGas + input.intrinsicStateGas

def evmGas (input : InitializationInput) : Nat :=
  input.txGas - totalIntrinsicGas input

def executionGasBudget (input : InitializationInput) : Nat :=
  input.txMaxGasLimit - input.intrinsicGas

def initialGasLeft (input : InitializationInput) : Nat :=
  min (executionGasBudget input) (evmGas input)

def initialStateGasReservoir (input : InitializationInput) : Nat :=
  evmGas input - initialGasLeft input

/-- EIP-8037's reservoir initialization after intrinsic-gas validation. -/
def initializeTransactionGas (input : InitializationInput) : InitialGas :=
  { evmGas := evmGas input
    executionGasBudget := executionGasBudget input
    gasLeft := initialGasLeft input
    stateGasReservoir := initialStateGasReservoir input
    stateGasUsed := input.intrinsicStateGas
    stateGasSpill := 0
    stateGasSpillRefunded := 0 }

theorem initialize_evm_gas (input : InitializationInput) :
    (initializeTransactionGas input).evmGas =
      input.txGas - (input.intrinsicGas + input.intrinsicStateGas) := rfl

theorem initialize_execution_budget (input : InitializationInput) :
    (initializeTransactionGas input).executionGasBudget = input.txMaxGasLimit - input.intrinsicGas := rfl

theorem initialize_gas_left (input : InitializationInput) :
    (initializeTransactionGas input).gasLeft =
      min ((initializeTransactionGas input).executionGasBudget)
        ((initializeTransactionGas input).evmGas) := rfl

theorem initialize_reservoir (input : InitializationInput) :
    (initializeTransactionGas input).stateGasReservoir =
      (initializeTransactionGas input).evmGas - (initializeTransactionGas input).gasLeft := rfl

theorem initialize_state_gas_used (input : InitializationInput) :
    (initializeTransactionGas input).stateGasUsed = input.intrinsicStateGas := rfl

theorem initialize_state_gas_spill (input : InitializationInput) :
    (initializeTransactionGas input).stateGasSpill = 0 := rfl

theorem initialize_state_gas_spill_refunded (input : InitializationInput) :
    (initializeTransactionGas input).stateGasSpillRefunded = 0 := rfl

theorem initialize_conserves_evm_gas (input : InitializationInput) :
    (initializeTransactionGas input).gasLeft + (initializeTransactionGas input).stateGasReservoir =
      (initializeTransactionGas input).evmGas := by
  simp [initializeTransactionGas, initialStateGasReservoir, initialGasLeft]
  omega

theorem initialize_conserves_tx_gas {input : InitializationInput}
    (hValid : input.Valid) :
    (initializeTransactionGas input).gasLeft +
        (initializeTransactionGas input).stateGasReservoir +
          input.intrinsicGas + input.intrinsicStateGas =
      input.txGas := by
  rw [initialize_conserves_evm_gas, initialize_evm_gas]
  have hIntrinsicGas := hValid.1
  omega

theorem initialize_gas_left_le_execution_budget (input : InitializationInput) :
    (initializeTransactionGas input).gasLeft ≤ (initializeTransactionGas input).executionGasBudget := by
  exact Nat.min_le_left _ _

theorem initialize_gas_left_le_evm_gas (input : InitializationInput) :
    (initializeTransactionGas input).gasLeft ≤ (initializeTransactionGas input).evmGas := by
  exact Nat.min_le_right _ _

/--
Post-execution values used only by transaction and block settlement. The refund
counter is the non-negative final transaction counter at this boundary.
-/
structure SettlementInput where
  txGas : Nat
  gasLeft : Nat
  stateGasReservoir : Nat
  refundCounter : Nat
  evmStateGasUsed : Nat
  calldataFloorGasCost : Nat
  deriving DecidableEq, Repr

def txGasUsedBeforeRefund (input : SettlementInput) : Nat :=
  input.txGas - input.gasLeft - input.stateGasReservoir

/--
The settlement slice starts after validation and execution. These premises exclude
natural-number underflow and keep the calldata floor within the transaction gas.
-/
def SettlementInput.Valid (input : SettlementInput) : Prop :=
  input.gasLeft + input.stateGasReservoir ≤ input.txGas ∧
    input.evmStateGasUsed ≤ txGasUsedBeforeRefund input ∧
      input.calldataFloorGasCost ≤ input.txGas

instance (input : SettlementInput) : Decidable input.Valid :=
  inferInstanceAs (Decidable
    (input.gasLeft + input.stateGasReservoir ≤ input.txGas ∧
      input.evmStateGasUsed ≤ txGasUsedBeforeRefund input ∧
        input.calldataFloorGasCost ≤ input.txGas))

def txGasRefund (input : SettlementInput) : Nat :=
  min (txGasUsedBeforeRefund input / 5) input.refundCounter

def txGasUsedAfterRefund (input : SettlementInput) : Nat :=
  txGasUsedBeforeRefund input - txGasRefund input

def paidGas (input : SettlementInput) : Nat :=
  max (txGasUsedAfterRefund input) input.calldataFloorGasCost

def stateGas (input : SettlementInput) : Nat :=
  input.evmStateGasUsed

/-- The pinned proof configuration enables EIP-7778, so block execution gas is pre-refund. -/
def executionGas (input : SettlementInput) : Nat :=
  max (txGasUsedBeforeRefund input - stateGas input) input.calldataFloorGasCost

/-- All transaction-level values needed by the two-dimensional block fold. -/
structure Settlement where
  gasUsedBeforeRefund : Nat
  gasRefund : Nat
  gasUsedAfterRefund : Nat
  paidGas : Nat
  stateGas : Nat
  executionGas : Nat
  deriving DecidableEq, Repr

def settle (input : SettlementInput) : Settlement :=
  { gasUsedBeforeRefund := txGasUsedBeforeRefund input
    gasRefund := txGasRefund input
    gasUsedAfterRefund := txGasUsedAfterRefund input
    paidGas := paidGas input
    stateGas := stateGas input
    executionGas := executionGas input }

theorem before_refund_formula (input : SettlementInput) :
    (settle input).gasUsedBeforeRefund =
      input.txGas - input.gasLeft - input.stateGasReservoir := rfl

theorem before_refund_eq_total_consumed (input : SettlementInput) :
    (settle input).gasUsedBeforeRefund =
      input.txGas - (input.gasLeft + input.stateGasReservoir) := by
  exact Nat.sub_sub _ _ _

theorem before_refund_conservation {input : SettlementInput}
    (hValid : input.Valid) :
    (settle input).gasUsedBeforeRefund + input.gasLeft + input.stateGasReservoir =
      input.txGas := by
  rw [before_refund_eq_total_consumed]
  have hRemaining := hValid.1
  omega

theorem refund_formula (input : SettlementInput) :
    (settle input).gasRefund =
      min ((settle input).gasUsedBeforeRefund / 5) input.refundCounter := rfl

theorem refund_le_refund_cap (input : SettlementInput) :
    (settle input).gasRefund ≤ (settle input).gasUsedBeforeRefund / 5 := by
  exact Nat.min_le_left _ _

theorem refund_le_counter (input : SettlementInput) :
    (settle input).gasRefund ≤ input.refundCounter := by
  exact Nat.min_le_right _ _

theorem refund_le_before_refund (input : SettlementInput) :
    (settle input).gasRefund ≤ (settle input).gasUsedBeforeRefund := by
  calc
    (settle input).gasRefund ≤ (settle input).gasUsedBeforeRefund / 5 :=
      refund_le_refund_cap input
    _ ≤ (settle input).gasUsedBeforeRefund := Nat.div_le_self _ _

theorem after_refund_formula (input : SettlementInput) :
    (settle input).gasUsedAfterRefund =
      (settle input).gasUsedBeforeRefund - (settle input).gasRefund := rfl

theorem after_refund_add_refund (input : SettlementInput) :
    (settle input).gasUsedAfterRefund + (settle input).gasRefund =
      (settle input).gasUsedBeforeRefund := by
  rw [after_refund_formula, Nat.sub_add_cancel (refund_le_before_refund input)]

theorem paid_gas_formula (input : SettlementInput) :
    (settle input).paidGas =
      max (settle input).gasUsedAfterRefund input.calldataFloorGasCost := rfl

theorem paid_gas_ge_after_refund (input : SettlementInput) :
    (settle input).gasUsedAfterRefund ≤ (settle input).paidGas := by
  exact Nat.le_max_left _ _

theorem paid_gas_ge_calldata_floor (input : SettlementInput) :
    input.calldataFloorGasCost ≤ (settle input).paidGas := by
  exact Nat.le_max_right _ _

theorem paid_gas_le_tx_gas {input : SettlementInput} (hValid : input.Valid) :
    (settle input).paidGas ≤ input.txGas := by
  rw [paid_gas_formula]
  apply Nat.max_le.mpr
  constructor
  · calc
      (settle input).gasUsedAfterRefund ≤ (settle input).gasUsedBeforeRefund := by
        rw [after_refund_formula]
        exact Nat.sub_le _ _
      _ = input.txGas - input.gasLeft - input.stateGasReservoir := before_refund_formula input
      _ ≤ input.txGas - input.gasLeft := Nat.sub_le _ _
      _ ≤ input.txGas := Nat.sub_le _ _
  · exact hValid.2.2

theorem state_gas_formula (input : SettlementInput) :
    (settle input).stateGas = input.evmStateGasUsed := rfl

theorem execution_gas_formula (input : SettlementInput) :
    (settle input).executionGas =
      max ((settle input).gasUsedBeforeRefund - (settle input).stateGas)
        input.calldataFloorGasCost := rfl

theorem execution_gas_ge_pre_refund_execution (input : SettlementInput) :
    (settle input).gasUsedBeforeRefund - (settle input).stateGas ≤
      (settle input).executionGas := by
  exact Nat.le_max_left _ _

theorem execution_gas_ge_calldata_floor (input : SettlementInput) :
    input.calldataFloorGasCost ≤ (settle input).executionGas := by
  exact Nat.le_max_right _ _

theorem state_gas_le_tx_gas {input : SettlementInput} (hValid : input.Valid) :
    (settle input).stateGas ≤ input.txGas := by
  rw [state_gas_formula]
  calc
    input.evmStateGasUsed ≤ txGasUsedBeforeRefund input := hValid.2.1
    _ ≤ input.txGas - input.gasLeft := Nat.sub_le _ _
    _ ≤ input.txGas := Nat.sub_le _ _

theorem execution_gas_le_tx_gas {input : SettlementInput} (hValid : input.Valid) :
    (settle input).executionGas ≤ input.txGas := by
  rw [execution_gas_formula]
  apply Nat.max_le.mpr
  constructor
  · calc
      (settle input).gasUsedBeforeRefund - (settle input).stateGas ≤
          (settle input).gasUsedBeforeRefund := Nat.sub_le _ _
      _ = input.txGas - input.gasLeft - input.stateGasReservoir := before_refund_formula input
      _ ≤ input.txGas - input.gasLeft := Nat.sub_le _ _
      _ ≤ input.txGas := Nat.sub_le _ _
  · exact hValid.2.2

/-- The gas counters retained by the block-processing settlement slice. -/
structure BlockGas where
  executionGasUsed : Nat
  stateGasUsed : Nat
  cumulativeReceiptGasUsed : Nat
  deriving DecidableEq, Repr

def headerGasUsed (block : BlockGas) : Nat :=
  max block.executionGasUsed block.stateGasUsed

def applyUser (block : BlockGas) (transaction : Settlement) : BlockGas :=
  { executionGasUsed := block.executionGasUsed + transaction.executionGas
    stateGasUsed := block.stateGasUsed + transaction.stateGas
    cumulativeReceiptGasUsed := block.cumulativeReceiptGasUsed + transaction.paidGas }

inductive BlockItem where
  | user (settlement : Settlement)
  | system
  deriving DecidableEq, Repr

def applyBlockItem (block : BlockGas) : BlockItem → BlockGas
  | .user settlement => applyUser block settlement
  | .system => block

theorem user_execution_counter_formula (block : BlockGas) (transaction : Settlement) :
    (applyUser block transaction).executionGasUsed =
      block.executionGasUsed + transaction.executionGas := rfl

theorem user_state_counter_formula (block : BlockGas) (transaction : Settlement) :
    (applyUser block transaction).stateGasUsed =
      block.stateGasUsed + transaction.stateGas := rfl

theorem receipt_cumulative_formula (block : BlockGas) (transaction : Settlement) :
    (applyUser block transaction).cumulativeReceiptGasUsed =
      block.cumulativeReceiptGasUsed + transaction.paidGas := rfl

theorem user_header_formula (block : BlockGas) (transaction : Settlement) :
    headerGasUsed (applyUser block transaction) =
      max (block.executionGasUsed + transaction.executionGas)
        (block.stateGasUsed + transaction.stateGas) := rfl

theorem header_gas_limit_iff (block : BlockGas) (gasLimit : Nat) :
    headerGasUsed block ≤ gasLimit ↔
      block.executionGasUsed ≤ gasLimit ∧ block.stateGasUsed ≤ gasLimit := by
  exact Nat.max_le

theorem user_header_within_limit {block : BlockGas} {transaction : Settlement} {gasLimit : Nat}
    (hExecution : block.executionGasUsed + transaction.executionGas ≤ gasLimit)
    (hState : block.stateGasUsed + transaction.stateGas ≤ gasLimit) :
    headerGasUsed (applyUser block transaction) ≤ gasLimit := by
  rw [user_header_formula]
  exact Nat.max_le.mpr ⟨hExecution, hState⟩

theorem apply_system_preserves_counters (block : BlockGas) :
    applyBlockItem block .system = block := rfl

theorem system_preserves_header_gas (block : BlockGas) :
    headerGasUsed (applyBlockItem block .system) = headerGasUsed block := rfl

namespace BoundaryVector

structure InitializationVector where
  name : String
  input : InitializationInput
  expected : InitialGas
  deriving DecidableEq, Repr

structure SettlementVector where
  name : String
  input : SettlementInput
  block : BlockGas
  expectedSettlement : Settlement
  expectedBlock : BlockGas
  expectedHeaderGas : Nat
  deriving DecidableEq, Repr

structure SystemVector where
  name : String
  block : BlockGas
  expected : BlockGas
  deriving DecidableEq, Repr

def initializationVectors : List InitializationVector :=
  [ { name := "execution-budget-covers-evm-gas"
      input := { txGas := 100, intrinsicGas := 20, txMaxGasLimit := 100 }
      expected := { evmGas := 80, executionGasBudget := 80, gasLeft := 80, stateGasReservoir := 0 } }
  , { name := "reservoir-holds-execution-cap-overflow"
      input := { txGas := 150, intrinsicGas := 20, txMaxGasLimit := 100 }
      expected := { evmGas := 130, executionGasBudget := 80, gasLeft := 80, stateGasReservoir := 50 } }
  , { name := "eip-7825-cap-with-state-reservoir"
      input := { txGas := 20_000_000, intrinsicGas := 21_000, txMaxGasLimit := 16_777_216 }
      expected :=
        { evmGas := 19_979_000
          executionGasBudget := 16_756_216
          gasLeft := 16_756_216
          stateGasReservoir := 3_222_784 } }
  , { name := "folded-authorization-state-baseline"
      input :=
        { txGas := 16_812_407
          intrinsicGas := 21_000
          intrinsicStateGas := 35_190
          txMaxGasLimit := 16_777_216 }
      expected :=
        { evmGas := 16_756_217
          executionGasBudget := 16_756_216
          gasLeft := 16_756_216
          stateGasReservoir := 1
          stateGasUsed := 35_190
          stateGasSpill := 0
          stateGasSpillRefunded := 0 } } ]

def settlementVectors : List SettlementVector :=
  [ { name := "refund-cap-separates-paid-and-block-execution-gas"
      input :=
        { txGas := 200
          gasLeft := 120
          stateGasReservoir := 20
          refundCounter := 100
          evmStateGasUsed := 20
          calldataFloorGasCost := 10 }
      block := { executionGasUsed := 5, stateGasUsed := 7, cumulativeReceiptGasUsed := 11 }
      expectedSettlement :=
        { gasUsedBeforeRefund := 60
          gasRefund := 12
          gasUsedAfterRefund := 48
          paidGas := 48
          stateGas := 20
          executionGas := 40 }
      expectedBlock := { executionGasUsed := 45, stateGasUsed := 27, cumulativeReceiptGasUsed := 59 }
      expectedHeaderGas := 45 }
  , { name := "calldata-floor-after-refund"
      input :=
        { txGas := 100
          gasLeft := 50
          stateGasReservoir := 10
          refundCounter := 100
          evmStateGasUsed := 7
          calldataFloorGasCost := 35 }
      block := { executionGasUsed := 10, stateGasUsed := 12, cumulativeReceiptGasUsed := 20 }
      expectedSettlement :=
        { gasUsedBeforeRefund := 40
          gasRefund := 8
          gasUsedAfterRefund := 32
          paidGas := 35
          stateGas := 7
          executionGas := 35 }
      expectedBlock := { executionGasUsed := 45, stateGasUsed := 19, cumulativeReceiptGasUsed := 55 }
      expectedHeaderGas := 45 }
  , { name := "state-dimension-controls-header"
      input :=
        { txGas := 100
          gasLeft := 55
          stateGasReservoir := 0
          refundCounter := 0
          evmStateGasUsed := 45
          calldataFloorGasCost := 0 }
      block := { executionGasUsed := 40, stateGasUsed := 10, cumulativeReceiptGasUsed := 3 }
      expectedSettlement :=
        { gasUsedBeforeRefund := 45
          gasRefund := 0
          gasUsedAfterRefund := 45
          paidGas := 45
          stateGas := 45
          executionGas := 0 }
      expectedBlock := { executionGasUsed := 40, stateGasUsed := 55, cumulativeReceiptGasUsed := 48 }
      expectedHeaderGas := 55 } ]

def systemVectors : List SystemVector :=
  [ { name := "system-transaction-is-excluded-from-all-counters"
      block := { executionGasUsed := 17, stateGasUsed := 19, cumulativeReceiptGasUsed := 23 }
      expected := { executionGasUsed := 17, stateGasUsed := 19, cumulativeReceiptGasUsed := 23 } } ]

def initializationPasses (vector : InitializationVector) : Bool :=
  decide (vector.input.Valid ∧ initializeTransactionGas vector.input = vector.expected)

def settlementPasses (vector : SettlementVector) : Bool :=
  decide (vector.input.Valid ∧
    settle vector.input = vector.expectedSettlement ∧
    applyUser vector.block (settle vector.input) = vector.expectedBlock ∧
    headerGasUsed (applyUser vector.block (settle vector.input)) = vector.expectedHeaderGas)

def systemPasses (vector : SystemVector) : Bool :=
  decide (applyBlockItem vector.block .system = vector.expected)

theorem all_pass :
    initializationVectors.all initializationPasses = true ∧
      settlementVectors.all settlementPasses = true ∧
      systemVectors.all systemPasses = true := by
  decide

end BoundaryVector
end TransactionGas
end Eip803x
