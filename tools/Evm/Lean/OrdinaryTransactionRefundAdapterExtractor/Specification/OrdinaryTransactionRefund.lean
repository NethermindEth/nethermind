-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.TransactionSettlement
import Lean.Elab.Tactic.Omega

namespace OrdinaryTransactionRefundAdapterExtractor.Specification.OrdinaryTransactionRefund

namespace T
export Eip803x.TransactionSettlement
  (Input Result FitsUInt64 FitsInt64 FitsInt32 uint64Max uint64Modulus int64Min int64Max
   int64Modulus int64SignBit
   wrapUInt64 wrapInt64 uint64ToInt64 int64ToUInt64 addUInt64 subUInt64 saturatingSubUInt64 settle)
end T

inductive Entry where
  | ordinaryRefund | preparationOutOfGas | createStateOutOfGas | contractCollision | failedDeposit
  deriving DecidableEq, Repr

structure GasState where
  value : Nat
  stateReservoir : Int
  stateGasUsed : Int
  stateGasSpill : Int
  stateGasSpillRefunded : Int
  deriving DecidableEq, Repr

structure Constants where
  executionCap : Nat
  createStateCost : Int
  newAccountCost : Nat
  perAuthorizationCost : Nat
  legacyRefundQuotient : Nat
  eip3529RefundQuotient : Nat
  deriving DecidableEq, Repr

structure Input where
  entry : Entry
  transactionGasLimit : Nat
  gasPrice : Nat
  maxFeePerGas : Nat
  maxPriorityFeePerGas : Nat
  skipValidation : Bool
  isContractCreation : Bool
  isEip8037Enabled : Bool
  isEip3529Enabled : Bool
  isEip7778Enabled : Bool
  isError : Bool
  shouldRevert : Bool
  refundCounter : Int
  destroyCount : Int
  destroyRefund : Nat
  codeInsertRefundCount : Nat
  incomingGas : GasState
  intrinsicStandard : GasState
  floorGas : GasState
  postIntrinsicStateReservoir : Int
  topLevelCreateStateGasCharged : Bool
  deriving DecidableEq, Repr

structure Result where
  workingGas : GasState
  callerGas : GasState
  settlement : Option T.Input
  consumed : T.Result
  payRefundCalled : Bool
  paymentAmount : Nat
  senderCredit : Option Nat
  haltStateFloor : Option Int
  deriving DecidableEq, Repr

def uint256Modulus : Nat := 2 ^ 256
def FitsUInt256 (value : Nat) : Prop := value < uint256Modulus

def GasState.Valid (gas : GasState) : Prop :=
  T.FitsUInt64 gas.value ∧ T.FitsInt64 gas.stateReservoir ∧ T.FitsInt64 gas.stateGasUsed ∧
  T.FitsInt64 gas.stateGasSpill ∧ T.FitsInt64 gas.stateGasSpillRefunded

def Constants.Valid (constants : Constants) : Prop :=
  T.FitsUInt64 constants.executionCap ∧ T.FitsInt64 constants.createStateCost ∧
  T.FitsUInt64 constants.newAccountCost ∧ T.FitsUInt64 constants.perAuthorizationCost ∧
  T.FitsUInt64 constants.legacyRefundQuotient ∧ 0 < constants.legacyRefundQuotient ∧
  T.FitsUInt64 constants.eip3529RefundQuotient ∧ 0 < constants.eip3529RefundQuotient

/-- Scalar representation and helper-entry domain, not proof of upstream snapshot provenance. -/
def Input.Valid (input : Input) : Prop :=
  T.FitsUInt64 input.transactionGasLimit ∧ FitsUInt256 input.gasPrice ∧
  FitsUInt256 input.maxFeePerGas ∧ FitsUInt256 input.maxPriorityFeePerGas ∧
  T.FitsInt64 input.refundCounter ∧ T.FitsInt32 input.destroyCount ∧ 0 ≤ input.destroyCount ∧
  T.FitsUInt64 input.destroyRefund ∧ T.FitsUInt64 input.codeInsertRefundCount ∧
  input.incomingGas.Valid ∧ input.intrinsicStandard.Valid ∧ input.floorGas.Valid ∧
  T.FitsInt64 input.postIntrinsicStateReservoir ∧
  ((input.entry = .preparationOutOfGas ∨ input.entry = .createStateOutOfGas) →
    input.isEip8037Enabled = true)

instance (gas : GasState) : Decidable gas.Valid := by
  unfold GasState.Valid T.FitsUInt64 T.FitsInt64
  infer_instance

instance (constants : Constants) : Decidable constants.Valid := by
  unfold Constants.Valid T.FitsUInt64 T.FitsInt64
  infer_instance

instance (input : Input) : Decidable input.Valid := by
  unfold Input.Valid FitsUInt256 T.FitsUInt64 T.FitsInt64 T.FitsInt32
  infer_instance

def mulUInt64 (left right : Nat) : Nat := T.wrapUInt64 ((left : Int) * (right : Int))

def refundQuotient (constants : Constants) (input : Input) : Nat :=
  if input.isEip3529Enabled then constants.eip3529RefundQuotient else constants.legacyRefundQuotient

/-- The signed intermediate is Int128 in C#, rather than saturating natural subtraction. -/
def preRefundDifference (gas : GasState) (limit : Nat) : Int :=
  (limit : Int) - (gas.value : Int) - gas.stateReservoir

def preRefundGas (gas : GasState) (limit : Nat) : Nat :=
  let difference := preRefundDifference gas limit
  if 0 ≤ difference ∧ difference ≤ (T.uint64Max : Int) then difference.toNat else limit

def refundApplied (gas : GasState) (amount floor : Int) : Int :=
  min amount (max 0 (T.wrapInt64 (gas.stateGasUsed - floor)))

def refundToExecution (gas : GasState) (amount floor : Int) (trackSpillRefund : Bool) : Int :=
  if trackSpillRefund then
    min (refundApplied gas amount floor)
      (max 0 (T.wrapInt64 (gas.stateGasSpill - gas.stateGasSpillRefunded)))
  else 0

def refundStateGas (gas : GasState) (amount floor : Int) (trackSpillRefund : Bool) : GasState :=
  let applied := refundApplied gas amount floor
  let toExecution := refundToExecution gas amount floor trackSpillRefund
  { value := T.addUInt64 gas.value (T.int64ToUInt64 toExecution),
    stateReservoir := T.wrapInt64 (gas.stateReservoir + T.wrapInt64 (applied - toExecution)),
    stateGasUsed := T.wrapInt64 (gas.stateGasUsed - applied),
    stateGasSpill := gas.stateGasSpill,
    stateGasSpillRefunded :=
      if trackSpillRefund then T.wrapInt64 (gas.stateGasSpillRefunded + toExecution)
      else gas.stateGasSpillRefunded }

def resetForHalt (gas : GasState) (reservoir used : Int) : GasState :=
  { gas with stateReservoir := reservoir, stateGasUsed := used, stateGasSpill := 0 }

def clearExecutionGas (gas : GasState) : GasState := { gas with value := 0 }

def refundRevertedExecutionStateGas (enabled : Bool) (floor : Int) (gas : GasState) : GasState :=
  if enabled && gas.stateGasUsed > floor then refundStateGas gas gas.stateGasUsed floor true else gas

def initialStateReservoir (constants : Constants) (input : Input) : Int :=
  max 0 (T.wrapInt64
    (T.wrapInt64 (T.uint64ToInt64 input.transactionGasLimit - input.intrinsicStandard.stateReservoir) -
      T.uint64ToInt64 constants.executionCap))

def haltStateFloor (constants : Constants) (input : Input) : Int :=
  let refundedIntrinsic := max 0 (T.wrapInt64
    (input.postIntrinsicStateReservoir - initialStateReservoir constants input))
  max 0 (T.wrapInt64 (input.intrinsicStandard.stateReservoir - refundedIntrinsic))

/-- Reset follows the state refund; execution gas is cleared only after both. -/
def completeHaltGas (constants : Constants) (input : Input) (gas : GasState) : GasState :=
  let floor := haltStateFloor constants input
  clearExecutionGas (resetForHalt
    (refundRevertedExecutionStateGas input.isEip8037Enabled floor gas)
    input.postIntrinsicStateReservoir floor)

def codeInsertExecutionRefund (constants : Constants) (count : Nat) (enabled : Bool) : Nat :=
  if count == 0 || enabled then 0
  else mulUInt64 (T.subUInt64 constants.newAccountCost constants.perAuthorizationCost) count

def prepareOrdinaryGas (constants : Constants) (input : Input) : GasState :=
  if input.isEip8037Enabled && input.shouldRevert && input.topLevelCreateStateGasCharged then
    let amount := if input.isContractCreation then constants.createStateCost else 0
    if amount > 0 then refundStateGas input.incomingGas amount input.intrinsicStandard.stateReservoir false
    else input.incomingGas
  else input.incomingGas

def settlementInput (constants : Constants) (input : Input) (gas : GasState) (codeRefund : Nat) : T.Input :=
  { transactionGasLimit := input.transactionGasLimit,
    preRefundGas := if input.isError then 0 else preRefundGas gas input.transactionGasLimit,
    refundCounter := input.refundCounter, destroyCount := input.destroyCount,
    destroyRefund := input.destroyRefund, codeInsertExecutionRefund := codeRefund,
    calldataFloorGas := input.floorGas.value,
    stateGasUsed := if input.isEip8037Enabled then gas.stateGasUsed else 0,
    refundQuotient := refundQuotient constants input,
    isError := input.isError, shouldRevert := input.shouldRevert,
    isEip8037Enabled := input.isEip8037Enabled, isEip7778Enabled := input.isEip7778Enabled }

def shouldRefundGas (input : Input) : Bool :=
  input.gasPrice != 0 && (!input.skipValidation || input.maxFeePerGas != 0 || input.maxPriorityFeePerGas != 0)

def modifiesCaller (input : Input) : Bool :=
  input.entry == .preparationOutOfGas || input.entry == .createStateOutOfGas ||
  (input.entry == .failedDeposit && input.isEip8037Enabled)

/-- Modular UInt256 amounts and call observations; concrete sender/world-state effects remain external. -/
def finish (input : Input) (gas : GasState) (settlement : Option T.Input) (consumed : T.Result)
    (payRefundCalled : Bool) (floor : Option Int) : Result :=
  let amount := if payRefundCalled then
    (T.subUInt64 input.transactionGasLimit consumed.spentGas * input.gasPrice) % uint256Modulus
    else 0
  { workingGas := gas, callerGas := if modifiesCaller input then gas else input.incomingGas,
    settlement, consumed, payRefundCalled, paymentAmount := amount,
    senderCredit := if payRefundCalled && amount != 0 then some amount else none,
    haltStateFloor := floor }

def haltConsumed (constants : Constants) (input : Input) (gas : GasState) (codeRefund : Nat) : T.Result :=
  let before := T.subUInt64 input.transactionGasLimit (T.int64ToUInt64 gas.stateReservoir)
  let claimed := min (before / refundQuotient constants input) codeRefund
  let spent := max (T.subUInt64 before claimed) input.floorGas.value
  let state := T.int64ToUInt64 gas.stateGasUsed
  { spentGas := spent, operationGas := spent,
    blockGas := max (T.saturatingSubUInt64 before state) input.floorGas.value,
    blockStateGas := state, maxUsedGas := spent, gasRefund := claimed }

def halt (constants : Constants) (input : Input) (gas : GasState) (codeRefund : Nat) : Result :=
  let after := completeHaltGas constants input gas
  let consumed := haltConsumed constants input after codeRefund
  finish input after none consumed
    (shouldRefundGas input && consumed.spentGas < input.transactionGasLimit)
    (some (haltStateFloor constants input))

def fullGas (limit : Nat) : T.Result :=
  { spentGas := limit, operationGas := limit, blockGas := 0, blockStateGas := 0,
    maxUsedGas := limit, gasRefund := 0 }

/-- Exact helper-entry semantics on `Input.Valid`; invalid direct legacy halt routes are excluded. -/
def evaluate (constants : Constants) (input : Input) : Result :=
  if input.entry == .ordinaryRefund then
    let gas := prepareOrdinaryGas constants input
    let codeRefund := codeInsertExecutionRefund constants input.codeInsertRefundCount input.isEip8037Enabled
    if input.isError && input.isEip8037Enabled then halt constants input gas codeRefund
    else
      let settlement := settlementInput constants input gas codeRefund
      finish input gas (some settlement) (T.settle settlement) (shouldRefundGas input) none
  else if input.isEip8037Enabled then halt constants input input.incomingGas 0
  else finish input input.incomingGas none (fullGas input.transactionGasLimit) false none

theorem preRefundDifference_fits_int128 (gas : GasState) (limit : Nat)
    (hg : gas.Valid) (hl : T.FitsUInt64 limit) :
    -(2 ^ 127 : Int) ≤ preRefundDifference gas limit ∧
      preRefundDifference gas limit < 2 ^ 127 := by
  rcases hg with ⟨hv, hr, _, _, _⟩
  simp only [T.FitsUInt64, T.uint64Max, T.uint64Modulus] at hv hl
  simp only [T.FitsInt64, T.int64Min, T.int64Max,
    Eip803x.TransactionSettlement.int64SignBit] at hr
  unfold preRefundDifference
  omega

theorem preRefundGas_of_in_range (gas : GasState) (limit : Nat)
    (h : 0 ≤ preRefundDifference gas limit ∧ preRefundDifference gas limit ≤ (T.uint64Max : Int)) :
    preRefundGas gas limit = (preRefundDifference gas limit).toNat := by
  simp [preRefundGas, h]

theorem preRefundGas_of_out_of_range (gas : GasState) (limit : Nat)
    (h : ¬(0 ≤ preRefundDifference gas limit ∧ preRefundDifference gas limit ≤ (T.uint64Max : Int))) :
    preRefundGas gas limit = limit := by
  simp [preRefundGas, h]

theorem refundStateGas_preserves_spill (gas : GasState) (amount floor : Int) (track : Bool) :
    (refundStateGas gas amount floor track).stateGasSpill = gas.stateGasSpill := rfl

theorem untracked_refund_preserves_refunded_spill (gas : GasState) (amount floor : Int) :
    (refundStateGas gas amount floor false).stateGasSpillRefunded = gas.stateGasSpillRefunded := rfl

theorem resetForHalt_preserves_execution_and_refunded_spill (gas : GasState) (reservoir used : Int) :
    (resetForHalt gas reservoir used).value = gas.value ∧
    (resetForHalt gas reservoir used).stateGasSpillRefunded = gas.stateGasSpillRefunded ∧
    (resetForHalt gas reservoir used).stateGasSpill = 0 := by
  exact ⟨rfl, rfl, rfl⟩

theorem clearExecutionGas_preserves_state_fields (gas : GasState) :
    (clearExecutionGas gas).stateReservoir = gas.stateReservoir ∧
    (clearExecutionGas gas).stateGasUsed = gas.stateGasUsed ∧
    (clearExecutionGas gas).stateGasSpill = gas.stateGasSpill ∧
    (clearExecutionGas gas).stateGasSpillRefunded = gas.stateGasSpillRefunded := by
  exact ⟨rfl, rfl, rfl, rfl⟩

theorem completeHaltGas_exact_fields (constants : Constants) (input : Input) (gas : GasState) :
    completeHaltGas constants input gas =
      { value := 0, stateReservoir := input.postIntrinsicStateReservoir,
        stateGasUsed := haltStateFloor constants input, stateGasSpill := 0,
        stateGasSpillRefunded := (refundRevertedExecutionStateGas input.isEip8037Enabled
          (haltStateFloor constants input) gas).stateGasSpillRefunded } := rfl

theorem haltStateFloor_nonnegative (constants : Constants) (input : Input) :
    0 ≤ haltStateFloor constants input := by
  unfold haltStateFloor
  exact Int.le_max_left _ _

theorem pinned_code_insert_refund_zero (constants : Constants) (count : Nat) :
    codeInsertExecutionRefund constants count true = 0 := by
  simp [codeInsertExecutionRefund]

theorem zero_count_code_insert_refund_zero (constants : Constants) (enabled : Bool) :
    codeInsertExecutionRefund constants 0 enabled = 0 := by
  simp [codeInsertExecutionRefund]

/-- Upstream authorization processing must supply this count provenance separately. -/
def PinnedAuthorizationCount (input : Input) : Prop :=
  input.isEip8037Enabled = true → input.codeInsertRefundCount = 0

theorem pinned_positive_code_insert_refund_unreachable (constants : Constants) (input : Input)
    (h : input.isEip8037Enabled = true) :
    ¬0 < codeInsertExecutionRefund constants input.codeInsertRefundCount input.isEip8037Enabled := by
  simp [h, pinned_code_insert_refund_zero]

theorem zero_price_never_requests_refund (input : Input) (h : input.gasPrice = 0) :
    shouldRefundGas input = false := by simp [shouldRefundGas, h]

theorem skipped_zero_fee_caps_never_request_refund (input : Input)
    (hSkip : input.skipValidation = true) (hMax : input.maxFeePerGas = 0)
    (hPriority : input.maxPriorityFeePerGas = 0) : shouldRefundGas input = false := by
  simp [shouldRefundGas, hSkip, hMax, hPriority]

theorem finish_suppressed_payment (input : Input) (gas : GasState)
    (settlement : Option T.Input) (consumed : T.Result) (floor : Option Int) :
    (finish input gas settlement consumed false floor).paymentAmount = 0 ∧
    (finish input gas settlement consumed false floor).senderCredit = none := by
  simp [finish]

theorem finish_zero_payment_has_no_credit (input : Input) (gas : GasState)
    (settlement : Option T.Input) (consumed : T.Result) (floor : Option Int)
    (h : consumed.spentGas = input.transactionGasLimit) :
    (finish input gas settlement consumed true floor).payRefundCalled = true ∧
    (finish input gas settlement consumed true floor).senderCredit = none := by
  simp [finish, h, T.subUInt64, T.wrapUInt64]

theorem normal_tail_projects_six_settlement_fields (constants : Constants) (input : Input)
    (hEntry : input.entry = .ordinaryRefund)
    (hNormal : (input.isError && input.isEip8037Enabled) = false) :
    (evaluate constants input).consumed = T.settle
      (settlementInput constants input (prepareOrdinaryGas constants input)
        (codeInsertExecutionRefund constants input.codeInsertRefundCount input.isEip8037Enabled)) := by
  simp [evaluate, hEntry, hNormal, finish]

theorem ordinary_refund_preserves_caller (constants : Constants) (input : Input)
    (h : input.entry = .ordinaryRefund) :
    (evaluate constants input).callerGas = input.incomingGas := by
  cases hError : input.isError <;> cases hEnabled : input.isEip8037Enabled <;>
    simp [evaluate, h, hError, hEnabled, halt, finish, modifiesCaller]

theorem collision_preserves_caller (constants : Constants) (input : Input)
    (h : input.entry = .contractCollision) :
    (evaluate constants input).callerGas = input.incomingGas := by
  cases hEnabled : input.isEip8037Enabled <;>
    simp [evaluate, h, hEnabled, halt, finish, modifiesCaller]

theorem direct_halt_updates_caller (constants : Constants) (input : Input)
    (hEntry : input.entry = .preparationOutOfGas ∨ input.entry = .createStateOutOfGas ∨
      input.entry = .failedDeposit) (hEnabled : input.isEip8037Enabled = true) :
    (evaluate constants input).callerGas = (evaluate constants input).workingGas := by
  rcases hEntry with hEntry | hEntry | hEntry <;>
    simp [evaluate, hEntry, hEnabled, halt, finish, modifiesCaller]

theorem failed_deposit_is_halt_independently_of_error_flag (constants : Constants) (input : Input)
    (hEntry : input.entry = .failedDeposit) (hEnabled : input.isEip8037Enabled = true) :
    evaluate constants input = halt constants input input.incomingGas 0 := by
  simp [evaluate, hEntry, hEnabled]

theorem halt_projection_is_distinct (constants : Constants) (input : Input) (gas : GasState) (codeRefund : Nat) :
    (haltConsumed constants input gas codeRefund).operationGas =
      (haltConsumed constants input gas codeRefund).spentGas ∧
    (haltConsumed constants input gas codeRefund).maxUsedGas =
      (haltConsumed constants input gas codeRefund).spentGas := by
  exact ⟨rfl, rfl⟩

theorem halt_never_uses_normal_settlement (constants : Constants) (input : Input) (gas : GasState) (codeRefund : Nat) :
    (halt constants input gas codeRefund).settlement = none := rfl

theorem pinned_vm_error_has_no_code_insert_gas_refund (constants : Constants) (input : Input)
    (hEntry : input.entry = .ordinaryRefund) (hEnabled : input.isEip8037Enabled = true)
    (hError : input.isError = true) : (evaluate constants input).consumed.gasRefund = 0 := by
  simp [evaluate, hEntry, hEnabled, hError, codeInsertExecutionRefund, halt, finish, haltConsumed]

theorem direct_halt_has_no_code_insert_gas_refund (constants : Constants) (input : Input)
    (hEntry : input.entry ≠ .ordinaryRefund) (hEnabled : input.isEip8037Enabled = true) :
    (evaluate constants input).consumed.gasRefund = 0 := by
  simp [evaluate, hEntry, hEnabled, halt, finish, haltConsumed]

theorem halt_at_or_above_limit_never_calls_payRefund (constants : Constants) (input : Input)
    (gas : GasState) (codeRefund : Nat)
    (h : input.transactionGasLimit ≤
      (haltConsumed constants input (completeHaltGas constants input gas) codeRefund).spentGas) :
    (halt constants input gas codeRefund).payRefundCalled = false := by
  simp [halt, finish, Nat.not_lt.mpr h]

def naturalRefundApplied (gas : GasState) (amount floor : Int) : Int :=
  min amount (max 0 (gas.stateGasUsed - floor))

def naturalRefundToExecution (gas : GasState) (amount floor : Int) (track : Bool) : Int :=
  if track then
    min (naturalRefundApplied gas amount floor) (max 0 (gas.stateGasSpill - gas.stateGasSpillRefunded))
  else 0

def naturalRefundState (gas : GasState) (amount floor : Int) (track : Bool) : GasState :=
  let applied := naturalRefundApplied gas amount floor
  let toExecution := naturalRefundToExecution gas amount floor track
  { value := gas.value + toExecution.toNat,
    stateReservoir := gas.stateReservoir + (applied - toExecution),
    stateGasUsed := gas.stateGasUsed - applied,
    stateGasSpill := gas.stateGasSpill,
    stateGasSpillRefunded := if track then gas.stateGasSpillRefunded + toExecution else gas.stateGasSpillRefunded }

/-- Every signed intermediate and the unsigned sum require a separate no-wrap bound. -/
structure RefundNoWrap (gas : GasState) (amount floor : Int) (track : Bool) : Prop where
  gasBounded : gas.Valid
  amountBounded : T.FitsInt64 amount
  floorBounded : T.FitsInt64 floor
  amountNonnegative : 0 ≤ amount
  availableBounded : T.FitsInt64 (gas.stateGasUsed - floor)
  outstandingBounded : T.FitsInt64 (gas.stateGasSpill - gas.stateGasSpillRefunded)
  deltaBounded : T.FitsInt64 (naturalRefundApplied gas amount floor - naturalRefundToExecution gas amount floor track)
  reservoirBounded : T.FitsInt64 (gas.stateReservoir +
    (naturalRefundApplied gas amount floor - naturalRefundToExecution gas amount floor track))
  usedBounded : T.FitsInt64 (gas.stateGasUsed - naturalRefundApplied gas amount floor)
  refundedBounded : T.FitsInt64 (gas.stateGasSpillRefunded + naturalRefundToExecution gas amount floor track)
  executionBounded : T.FitsUInt64 (gas.value + (naturalRefundToExecution gas amount floor track).toNat)

theorem wrapInt64_of_bounded (value : Int) (h : T.FitsInt64 value) : T.wrapInt64 value = value := by
  simp only [T.FitsInt64] at h
  simp [T.wrapInt64, h]

theorem wrapUInt64_of_nat_bounded (value : Nat) (h : T.FitsUInt64 value) :
    T.wrapUInt64 (value : Int) = value := by
  have bound : (value : Int) ≤ (T.uint64Max : Int) := by
    unfold T.FitsUInt64 at h
    omega
  simp [T.wrapUInt64, bound]

theorem untracked_refund_preserves_execution (gas : GasState) (amount floor : Int)
    (h : T.FitsUInt64 gas.value) : (refundStateGas gas amount floor false).value = gas.value := by
  have hZero : T.int64ToUInt64 0 = 0 := by simp [T.int64ToUInt64, T.wrapUInt64]
  change T.addUInt64 gas.value (T.int64ToUInt64 0) = gas.value
  rw [hZero]
  simpa [T.addUInt64] using wrapUInt64_of_nat_bounded gas.value h

theorem natural_refund_parts_nonnegative (gas : GasState) (amount floor : Int) (track : Bool)
    (h : 0 ≤ amount) :
    0 ≤ naturalRefundApplied gas amount floor ∧
      0 ≤ naturalRefundToExecution gas amount floor track := by
  have ha : 0 ≤ naturalRefundApplied gas amount floor := by
    unfold naturalRefundApplied
    omega
  refine ⟨ha, ?_⟩
  cases track <;> simp only [naturalRefundToExecution, Bool.false_eq_true, ↓reduceIte]
  · omega
  · omega

theorem refundStateGas_eq_natural_of_no_wrap (gas : GasState) (amount floor : Int) (track : Bool)
    (h : RefundNoWrap gas amount floor track) :
    refundStateGas gas amount floor track = naturalRefundState gas amount floor track := by
  have hApplied : refundApplied gas amount floor = naturalRefundApplied gas amount floor := by
    simp [refundApplied, naturalRefundApplied, wrapInt64_of_bounded _ h.availableBounded]
  have hTo : refundToExecution gas amount floor track = naturalRefundToExecution gas amount floor track := by
    simp [refundToExecution, naturalRefundToExecution, hApplied, wrapInt64_of_bounded _ h.outstandingBounded]
  have hNonnegative := (natural_refund_parts_nonnegative gas amount floor track h.amountNonnegative).2
  have hToBound : T.FitsUInt64 (naturalRefundToExecution gas amount floor track).toNat := by
    have bound := h.executionBounded
    unfold T.FitsUInt64 at bound ⊢
    omega
  have hCast : T.int64ToUInt64 (naturalRefundToExecution gas amount floor track) =
      (naturalRefundToExecution gas amount floor track).toNat := by
    unfold T.int64ToUInt64
    rw [← Int.toNat_of_nonneg hNonnegative]
    exact wrapUInt64_of_nat_bounded _ hToBound
  have hValue : T.addUInt64 gas.value
      (T.int64ToUInt64 (naturalRefundToExecution gas amount floor track)) =
      gas.value + (naturalRefundToExecution gas amount floor track).toNat := by
    rw [hCast]
    exact wrapUInt64_of_nat_bounded _ h.executionBounded
  simp only [refundStateGas, naturalRefundState, hApplied, hTo, hValue,
    wrapInt64_of_bounded _ h.deltaBounded, wrapInt64_of_bounded _ h.reservoirBounded,
    wrapInt64_of_bounded _ h.usedBounded, wrapInt64_of_bounded _ h.refundedBounded]

theorem natural_refund_conserves_remaining_plus_used (gas : GasState) (amount floor : Int) (track : Bool)
    (h : 0 ≤ amount) :
    ((naturalRefundState gas amount floor track).value : Int) +
        (naturalRefundState gas amount floor track).stateReservoir +
        (naturalRefundState gas amount floor track).stateGasUsed =
      (gas.value : Int) + gas.stateReservoir + gas.stateGasUsed := by
  have hNonnegative := (natural_refund_parts_nonnegative gas amount floor track h).2
  simp only [naturalRefundState, Int.natCast_add]
  rw [Int.toNat_of_nonneg hNonnegative]
  omega

def naturalInitialStateReservoir (constants : Constants) (input : Input) : Int :=
  max 0 ((input.transactionGasLimit : Int) - input.intrinsicStandard.stateReservoir - constants.executionCap)

def naturalHaltStateFloor (constants : Constants) (input : Input) : Int :=
  max 0 (input.intrinsicStandard.stateReservoir -
    max 0 (input.postIntrinsicStateReservoir - naturalInitialStateReservoir constants input))

structure HaltFloorNoWrap (constants : Constants) (input : Input) : Prop where
  limitBounded : T.FitsInt64 (input.transactionGasLimit : Int)
  capBounded : T.FitsInt64 (constants.executionCap : Int)
  afterIntrinsicBounded : T.FitsInt64 ((input.transactionGasLimit : Int) - input.intrinsicStandard.stateReservoir)
  afterCapBounded : T.FitsInt64
    ((input.transactionGasLimit : Int) - input.intrinsicStandard.stateReservoir - constants.executionCap)
  refundBounded : T.FitsInt64 (input.postIntrinsicStateReservoir - naturalInitialStateReservoir constants input)
  floorBounded : T.FitsInt64 (input.intrinsicStandard.stateReservoir -
    max 0 (input.postIntrinsicStateReservoir - naturalInitialStateReservoir constants input))

theorem haltStateFloor_eq_natural_of_no_wrap (constants : Constants) (input : Input)
    (h : HaltFloorNoWrap constants input) :
    haltStateFloor constants input = naturalHaltStateFloor constants input := by
  have hInitial : initialStateReservoir constants input = naturalInitialStateReservoir constants input := by
    simp only [initialStateReservoir, naturalInitialStateReservoir, T.uint64ToInt64,
      wrapInt64_of_bounded _ h.limitBounded, wrapInt64_of_bounded _ h.capBounded,
      wrapInt64_of_bounded _ h.afterIntrinsicBounded, wrapInt64_of_bounded _ h.afterCapBounded]
  simp only [haltStateFloor, naturalHaltStateFloor, hInitial,
    wrapInt64_of_bounded _ h.refundBounded, wrapInt64_of_bounded _ h.floorBounded]

theorem natural_halt_floor_retains_only_intrinsic (constants : Constants) (input : Input) :
    0 ≤ naturalHaltStateFloor constants input ∧
    naturalHaltStateFloor constants input ≤ max 0 input.intrinsicStandard.stateReservoir := by
  unfold naturalHaltStateFloor
  omega

theorem payment_eq_natural_of_no_wrap (input : Input) (gas : GasState)
    (settlement : Option T.Input) (consumed : T.Result) (floor : Option Int)
    (hLimit : T.FitsUInt64 input.transactionGasLimit)
    (hSpent : consumed.spentGas ≤ input.transactionGasLimit)
    (hProduct : (input.transactionGasLimit - consumed.spentGas) * input.gasPrice < uint256Modulus) :
    (finish input gas settlement consumed true floor).paymentAmount =
      (input.transactionGasLimit - consumed.spentGas) * input.gasPrice := by
  have hDifference : T.subUInt64 input.transactionGasLimit consumed.spentGas =
      input.transactionGasLimit - consumed.spentGas := by
    unfold T.subUInt64
    rw [← Int.ofNat_sub hSpent]
    apply wrapUInt64_of_nat_bounded
    unfold T.FitsUInt64 at hLimit ⊢
    omega
  simp [finish, hDifference, Nat.mod_eq_of_lt hProduct]

theorem wrapUInt64_is_bounded (value : Int) : T.FitsUInt64 (T.wrapUInt64 value) := by
  unfold T.FitsUInt64 T.wrapUInt64
  split
  · rename_i h
    omega
  · have hNonnegative := Int.emod_nonneg value (by decide : (T.uint64Modulus : Int) ≠ 0)
    have hUpper := Int.emod_lt_of_pos value (by decide : (0 : Int) < (T.uint64Modulus : Int))
    simp only [T.uint64Max, T.uint64Modulus] at *
    omega

theorem wrapInt64_is_bounded (value : Int) : T.FitsInt64 (T.wrapInt64 value) := by
  unfold T.FitsInt64 T.wrapInt64
  split
  · assumption
  · have hNonnegative := Int.emod_nonneg value (by decide : T.int64Modulus ≠ 0)
    have hUpper := Int.emod_lt_of_pos value (by decide : 0 < T.int64Modulus)
    dsimp only
    split <;>
      simp only [T.int64Min, T.int64Max, T.int64Modulus, T.int64SignBit, T.uint64Modulus] at * <;>
      omega

theorem refundStateGas_preserves_machine_bounds (gas : GasState) (amount floor : Int) (track : Bool)
    (h : gas.Valid) : (refundStateGas gas amount floor track).Valid := by
  unfold GasState.Valid refundStateGas
  refine ⟨wrapUInt64_is_bounded _, wrapInt64_is_bounded _, wrapInt64_is_bounded _, h.2.2.2.1, ?_⟩
  split
  · exact wrapInt64_is_bounded _
  · exact h.2.2.2.2

theorem refundRevertedExecutionStateGas_preserves_machine_bounds
    (enabled : Bool) (floor : Int) (gas : GasState) (h : gas.Valid) :
    (refundRevertedExecutionStateGas enabled floor gas).Valid := by
  unfold refundRevertedExecutionStateGas
  split
  · exact refundStateGas_preserves_machine_bounds _ _ _ _ h
  · exact h

theorem haltStateFloor_is_bounded (constants : Constants) (input : Input) :
    T.FitsInt64 (haltStateFloor constants input) := by
  have h := wrapInt64_is_bounded (input.intrinsicStandard.stateReservoir -
    max 0 (T.wrapInt64 (input.postIntrinsicStateReservoir - initialStateReservoir constants input)))
  unfold T.FitsInt64 at h ⊢
  dsimp only [haltStateFloor]
  have hMin : T.int64Min ≤ 0 := by decide
  have hMax : 0 ≤ T.int64Max := by decide
  omega

theorem completeHaltGas_preserves_machine_bounds (constants : Constants) (input : Input) (gas : GasState)
    (hGas : gas.Valid) (hReservoir : T.FitsInt64 input.postIntrinsicStateReservoir) :
    (completeHaltGas constants input gas).Valid := by
  rw [completeHaltGas_exact_fields]
  have hRefund := refundRevertedExecutionStateGas_preserves_machine_bounds
    input.isEip8037Enabled (haltStateFloor constants input) gas hGas
  exact ⟨show T.FitsUInt64 0 from by unfold T.FitsUInt64; decide,
    hReservoir, haltStateFloor_is_bounded _ _,
    show T.FitsInt64 0 from by unfold T.FitsInt64; decide, hRefund.2.2.2.2⟩

theorem prepareOrdinaryGas_preserves_machine_bounds (constants : Constants) (input : Input)
    (h : input.incomingGas.Valid) : (prepareOrdinaryGas constants input).Valid := by
  have hRefund (amount : Int) : (if amount > 0 then
      refundStateGas input.incomingGas amount input.intrinsicStandard.stateReservoir false
      else input.incomingGas).Valid := by
    split
    · exact refundStateGas_preserves_machine_bounds _ _ _ _ h
    · exact h
  unfold prepareOrdinaryGas
  split
  · exact hRefund _
  · exact h

theorem preRefundGas_is_bounded (gas : GasState) (limit : Nat) (h : T.FitsUInt64 limit) :
    T.FitsUInt64 (preRefundGas gas limit) := by
  dsimp only [preRefundGas]
  split
  · rename_i hRange
    unfold T.FitsUInt64
    omega
  · exact h

end OrdinaryTransactionRefundAdapterExtractor.Specification.OrdinaryTransactionRefund
