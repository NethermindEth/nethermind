-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import OrdinaryTransactionRefundAdapterExtractor.Specification.OrdinaryTransactionRefund

namespace OrdinaryTransactionRefundAdapterExtractor.Vectors.OrdinaryTransactionRefundVectors

open OrdinaryTransactionRefundAdapterExtractor.Specification.OrdinaryTransactionRefund

def constants : Constants :=
  { executionCap := 16777216, createStateCost := 183600,
    newAccountCost := 25000, perAuthorizationCost := 12500,
    legacyRefundQuotient := 2, eip3529RefundQuotient := 5 }

def gas (value : Nat) (reservoir used spill refunded : Int) : GasState :=
  { value, stateReservoir := reservoir, stateGasUsed := used,
    stateGasSpill := spill, stateGasSpillRefunded := refunded }

def consumed (spent operation execution state maximum refund : Nat) : T.Result :=
  { spentGas := spent, operationGas := operation, blockGas := execution,
    blockStateGas := state, maxUsedGas := maximum, gasRefund := refund }

def ordinary : Input :=
  { entry := .ordinaryRefund, transactionGasLimit := 1000000,
    gasPrice := 2, maxFeePerGas := 0, maxPriorityFeePerGas := 0, skipValidation := false,
    isContractCreation := false, isEip8037Enabled := true, isEip3529Enabled := true,
    isEip7778Enabled := true, isError := false, shouldRevert := false,
    refundCounter := 1000, destroyCount := 0, destroyRefund := 0, codeInsertRefundCount := 0,
    incomingGas := gas 700000 10000 30000 500 100,
    intrinsicStandard := gas 21000 10000 0 0 0, floorGas := gas 21000 0 0 0 0,
    postIntrinsicStateReservoir := 10000, topLevelCreateStateGasCharged := false }

def vmError : Input := { ordinary with isError := true }
def reverted : Input := { ordinary with shouldRevert := true }
def createReverted : Input :=
  { reverted with
    isContractCreation := true, topLevelCreateStateGasCharged := true,
    incomingGas := gas 500000 20000 193600 500 100, postIntrinsicStateReservoir := 20000 }

theorem pinned_constants_are_machine_valid : constants.Valid := by decide
theorem ordinary_raw_entry_is_machine_valid : ordinary.Valid := by decide

theorem normal_derives_all_thirteen_settlement_inputs :
    (evaluate constants ordinary).settlement = some
      { transactionGasLimit := 1000000, preRefundGas := 290000,
        refundCounter := 1000, destroyCount := 0, destroyRefund := 0,
        codeInsertExecutionRefund := 0, calldataFloorGas := 21000,
        stateGasUsed := 30000, refundQuotient := 5, isError := false,
        shouldRevert := false, isEip8037Enabled := true, isEip7778Enabled := true } := by decide

theorem normal_projects_all_six_gas_fields :
    (evaluate constants ordinary).consumed = consumed 289000 289000 260000 30000 290000 1000 := by decide

theorem normal_working_gas_remains_unchanged :
    (evaluate constants ordinary).workingGas = gas 700000 10000 30000 500 100 := by decide

theorem normal_refund_requests_exact_sender_credit :
    (evaluate constants ordinary).payRefundCalled = true ∧
    (evaluate constants ordinary).paymentAmount = 1422000 ∧
    (evaluate constants ordinary).senderCredit = some 1422000 := by decide

theorem revert_does_not_claim_execution_refund_counter :
    (evaluate constants reverted).consumed = consumed 290000 290000 260000 30000 290000 0 := by decide

theorem create_revert_refills_only_reservoir :
    (evaluate constants createReverted).workingGas = gas 500000 203600 10000 500 100 := by decide

theorem create_revert_preserves_callers_original_gas :
    (evaluate constants createReverted).callerGas = gas 500000 20000 193600 500 100 := by decide

theorem create_revert_settles_after_refill :
    (evaluate constants createReverted).consumed = consumed 296400 296400 286400 10000 296400 0 := by decide

theorem raw_error_and_revert_flags_preserve_create_refill_order :
    (evaluate constants { createReverted with
      isError := true,
      incomingGas := gas 500000 20000 193600 190000 100 }).workingGas = gas 0 20000 0 0 10100 := by decide

theorem vm_error_resets_but_preserves_refunded_spill :
    (evaluate constants vmError).workingGas = gas 0 10000 0 0 500 := by decide

theorem vm_error_uses_local_copy :
    (evaluate constants vmError).callerGas = gas 700000 10000 30000 500 100 := by decide

theorem vm_error_uses_halt_projection_not_normal_kernel :
    (evaluate constants vmError).settlement = none ∧
    (evaluate constants vmError).consumed = consumed 990000 990000 990000 0 990000 0 ∧
    (evaluate constants vmError).senderCredit = some 20000 := by decide

theorem preparation_oog_updates_ref_argument :
    (evaluate constants { ordinary with entry := .preparationOutOfGas }).callerGas = gas 0 10000 0 0 500 := by decide

theorem create_state_oog_updates_ref_argument :
    (evaluate constants { ordinary with entry := .createStateOutOfGas }).callerGas = gas 0 10000 0 0 500 := by decide

theorem collision_uses_local_copy_and_halt_result :
    (evaluate constants { ordinary with entry := .contractCollision }).callerGas = gas 700000 10000 30000 500 100 ∧
    (evaluate constants { ordinary with entry := .contractCollision }).consumed =
      consumed 990000 990000 990000 0 990000 0 := by decide

theorem failed_deposit_halts_despite_false_error_flag :
    (evaluate constants { ordinary with entry := .failedDeposit }).settlement = none ∧
    (evaluate constants { ordinary with entry := .failedDeposit }).callerGas = gas 0 10000 0 0 500 ∧
    (evaluate constants { ordinary with entry := .failedDeposit }).consumed =
      consumed 990000 990000 990000 0 990000 0 := by decide

theorem legacy_collision_is_full_gas_without_payment :
    (evaluate constants { ordinary with entry := .contractCollision, isEip8037Enabled := false }).consumed =
      consumed 1000000 1000000 0 0 1000000 0 ∧
    (evaluate constants { ordinary with entry := .contractCollision, isEip8037Enabled := false }).payRefundCalled = false := by decide

theorem legacy_failed_deposit_preserves_caller :
    (evaluate constants { ordinary with entry := .failedDeposit, isEip8037Enabled := false }).callerGas =
      gas 700000 10000 30000 500 100 ∧
    (evaluate constants { ordinary with entry := .failedDeposit, isEip8037Enabled := false }).consumed =
      consumed 1000000 1000000 0 0 1000000 0 := by decide

theorem legacy_error_calls_payment_with_zero_amount_but_no_credit :
    (evaluate constants { vmError with isEip8037Enabled := false }).consumed =
      consumed 1000000 1000000 1000000 0 1000000 0 ∧
    (evaluate constants { vmError with isEip8037Enabled := false }).payRefundCalled = true ∧
    (evaluate constants { vmError with isEip8037Enabled := false }).paymentAmount = 0 ∧
    (evaluate constants { vmError with isEip8037Enabled := false }).senderCredit = none := by decide

theorem legacy_authorization_count_derives_execution_refund :
    (evaluate constants { ordinary with isEip8037Enabled := false, codeInsertRefundCount := 2 }).consumed =
      consumed 264000 264000 290000 0 290000 26000 := by decide

theorem pinned_authorization_count_cannot_create_execution_refund :
    (evaluate constants { ordinary with codeInsertRefundCount := 2 }).consumed =
      consumed 289000 289000 260000 30000 290000 1000 := by decide

theorem calldata_floor_keeps_operation_and_maximum_distinct :
    (evaluate constants { ordinary with floorGas := gas 350000 0 0 0 0 }).consumed =
      consumed 350000 289000 350000 30000 350000 1000 := by decide

theorem signed_negative_refund_adds_operation_gas :
    (evaluate constants { ordinary with refundCounter := -1000 }).consumed =
      consumed 291000 291000 260000 30000 290000 0 := by decide

theorem success_destroy_refund_is_included :
    (evaluate constants { ordinary with destroyCount := 2, destroyRefund := 10000 }).consumed =
      consumed 269000 269000 260000 30000 290000 21000 := by decide

theorem revert_destroy_refund_is_excluded :
    (evaluate constants { reverted with destroyCount := 2, destroyRefund := 10000 }).consumed =
      consumed 290000 290000 260000 30000 290000 0 := by decide

theorem refund_cap_is_one_fifth_when_eip3529_enabled :
    (evaluate constants { ordinary with refundCounter := 100000 }).consumed =
      consumed 232000 232000 260000 30000 290000 58000 := by decide

theorem legacy_refund_quotient_is_one_half :
    (evaluate constants { ordinary with refundCounter := 100000, isEip3529Enabled := false }).consumed =
      consumed 190000 190000 260000 30000 290000 100000 := by decide

theorem pre_7778_legacy_block_dimension_is_zero :
    (evaluate constants { ordinary with isEip8037Enabled := false, isEip7778Enabled := false }).consumed =
      consumed 289000 289000 0 0 290000 1000 := by decide

theorem halt_retains_intrinsic_floor_when_none_refunded :
    (evaluate constants { vmError with postIntrinsicStateReservoir := 0 }).workingGas = gas 0 0 10000 0 500 ∧
    (evaluate constants { vmError with postIntrinsicStateReservoir := 0 }).consumed =
      consumed 1000000 1000000 990000 10000 1000000 0 ∧
    (evaluate constants { vmError with postIntrinsicStateReservoir := 0 }).payRefundCalled = false := by decide

theorem halt_retains_only_unrefunded_intrinsic_floor :
    (evaluate constants { vmError with postIntrinsicStateReservoir := 4000 }).workingGas = gas 0 4000 6000 0 500 ∧
    (evaluate constants { vmError with postIntrinsicStateReservoir := 4000 }).consumed =
      consumed 996000 996000 990000 6000 996000 0 := by decide

theorem halt_floor_never_goes_negative_after_excess_intrinsic_refund :
    (evaluate constants { vmError with postIntrinsicStateReservoir := 20000 }).workingGas = gas 0 20000 0 0 500 := by decide

theorem terminal_refunded_spill_may_exceed_reset_spill :
    (evaluate constants vmError).workingGas.stateGasSpillRefunded = 500 ∧
    (evaluate constants vmError).workingGas.stateGasSpill = 0 := by decide

theorem reservoir_below_execution_cap_boundary :
    initialStateReservoir constants { ordinary with transactionGasLimit := 16787215 } = 0 := by decide

theorem reservoir_at_execution_cap_boundary :
    initialStateReservoir constants { ordinary with transactionGasLimit := 16787216 } = 0 := by decide

theorem reservoir_above_execution_cap_boundary :
    initialStateReservoir constants { ordinary with transactionGasLimit := 16787217 } = 1 := by decide

theorem existing_reservoir_is_not_an_intrinsic_refund :
    haltStateFloor constants { ordinary with transactionGasLimit := 16787316, postIntrinsicStateReservoir := 100 } = 10000 := by decide

theorem initial_reservoir_respects_unchecked_signed_limit_cast :
    initialStateReservoir constants { ordinary with transactionGasLimit := 18446744073709551615 } = 0 := by decide

theorem legacy_code_refund_multiplication_wraps_at_ulong :
    codeInsertExecutionRefund constants 18446744073709551615 false = 18446744073709539116 := by decide

theorem raw_negative_halt_reservoir_retains_unchecked_cast_semantics :
    (evaluate constants { vmError with postIntrinsicStateReservoir := -1 }).consumed =
      consumed 1000001 1000001 990001 10000 1000001 0 ∧
    (evaluate constants { vmError with postIntrinsicStateReservoir := -1 }).payRefundCalled = false := by decide

theorem pre_refund_accepts_negative_reservoir : preRefundGas (gas 20 (-10) 0 0 0) 100 = 90 := by decide
theorem pre_refund_negative_difference_falls_back : preRefundGas (gas 101 0 0 0 0) 100 = 100 := by decide
theorem pre_refund_above_ulong_falls_back :
    preRefundGas (gas 0 (-1) 0 0 0) 18446744073709551615 = 18446744073709551615 := by decide
theorem pre_refund_exact_zero_is_accepted : preRefundGas (gas 90 10 0 0 0) 100 = 0 := by decide
theorem pre_refund_exact_ulong_is_accepted :
    preRefundGas (gas 0 (-9223372036854775807) 0 0 0) 9223372036854775808 = 18446744073709551615 := by decide

theorem below_floor_refund_is_zero :
    refundStateGas (gas 10 20 9 5 2) 100 10 true = gas 10 20 9 5 2 := by decide
theorem at_floor_refund_is_zero :
    refundStateGas (gas 10 20 10 5 2) 100 10 true = gas 10 20 10 5 2 := by decide
theorem one_above_floor_refunds_one_spill :
    refundStateGas (gas 10 20 11 5 2) 100 10 true = gas 11 20 10 5 3 := by decide
theorem partial_spill_refund_replenishes_reservoir_after_execution :
    refundStateGas (gas 10 20 30 8 3) 20 10 true = gas 15 35 10 8 8 := by decide
theorem fully_refunded_spill_sends_all_refill_to_reservoir :
    refundStateGas (gas 10 20 30 3 3) 20 10 true = gas 10 40 10 3 3 := by decide
theorem full_spill_refund_sends_all_refill_to_execution :
    refundStateGas (gas 10 20 30 30 0) 20 10 true = gas 30 20 10 30 20 := by decide
theorem excess_refunded_spill_is_not_repaid_again :
    refundStateGas (gas 10 20 30 3 8) 20 10 true = gas 10 40 10 3 8 := by decide
theorem unchecked_signed_refund_difference_is_not_natural_subtraction :
    refundStateGas (gas 10 20 (-9223372036854775808) 0 0) 1 1 false =
      gas 10 21 9223372036854775807 0 0 := by decide

theorem zero_price_suppresses_payment :
    (evaluate constants { ordinary with gasPrice := 0 }).payRefundCalled = false ∧
    (evaluate constants { ordinary with gasPrice := 0 }).senderCredit = none := by decide
theorem skipped_validation_without_fee_caps_suppresses_payment :
    (evaluate constants { ordinary with skipValidation := true }).payRefundCalled = false := by decide
theorem nonzero_max_fee_restores_payment_under_skip_validation :
    (evaluate constants { ordinary with skipValidation := true, maxFeePerGas := 1 }).senderCredit = some 1422000 := by decide
theorem nonzero_priority_fee_restores_payment_under_skip_validation :
    (evaluate constants { ordinary with skipValidation := true, maxPriorityFeePerGas := 1 }).senderCredit = some 1422000 := by decide
theorem zero_price_still_suppresses_payment_with_nonzero_caps :
    (evaluate constants { ordinary with gasPrice := 0, maxFeePerGas := 1, maxPriorityFeePerGas := 1 }).payRefundCalled = false := by decide

theorem uint256_product_wrap_can_erase_world_credit_but_not_payment_call :
    (finish { ordinary with gasPrice := 2 ^ 255 } ordinary.incomingGas none
      (consumed 999998 0 0 0 0 0) true none).payRefundCalled = true ∧
    (finish { ordinary with gasPrice := 2 ^ 255 } ordinary.incomingGas none
      (consumed 999998 0 0 0 0 0) true none).paymentAmount = 0 ∧
    (finish { ordinary with gasPrice := 2 ^ 255 } ordinary.incomingGas none
      (consumed 999998 0 0 0 0 0) true none).senderCredit = none := by decide

theorem uint64_payment_subtraction_is_modular_on_raw_domain :
    (finish { ordinary with transactionGasLimit := 1, gasPrice := 1 } ordinary.incomingGas none
      (consumed 2 0 0 0 0 0) true none).paymentAmount = 18446744073709551615 := by decide

def clearBeforeRefundMutant : GasState :=
  resetForHalt (refundRevertedExecutionStateGas true 0 (clearExecutionGas vmError.incomingGas)) 10000 0

def resetBeforeRefundMutant : GasState :=
  clearExecutionGas (refundRevertedExecutionStateGas true 0 (resetForHalt vmError.incomingGas 10000 0))

theorem clearing_before_refund_is_detected :
    clearBeforeRefundMutant.value = 400 ∧
    clearBeforeRefundMutant ≠ (evaluate constants vmError).workingGas := by decide

theorem resetting_before_refund_is_detected :
    resetBeforeRefundMutant.stateGasSpillRefunded = 100 ∧
    resetBeforeRefundMutant ≠ (evaluate constants vmError).workingGas := by decide

theorem tracked_create_refill_mutant_is_detected :
    refundStateGas createReverted.incomingGas 183600 10000 true ≠
      (evaluate constants createReverted).workingGas := by decide

theorem routing_vm_error_through_normal_settlement_is_detected :
    T.settle (settlementInput constants vmError vmError.incomingGas 0) ≠
      (evaluate constants vmError).consumed := by decide

theorem routing_failed_deposit_by_error_flag_is_detected :
    (evaluate constants { ordinary with entry := .failedDeposit }).consumed ≠
      (evaluate constants ordinary).consumed := by decide

theorem clearing_refunded_spill_during_reset_is_detected :
    gas 0 10000 0 0 0 ≠ (evaluate constants vmError).workingGas := by decide

theorem partial_refill_no_wrap_domain : RefundNoWrap (gas 10 20 30 8 3) 20 10 true := by
  constructor <;> (try simp only [T.FitsInt64, T.FitsUInt64]) <;> decide

theorem partial_refill_invokes_natural_bridge :
    refundStateGas (gas 10 20 30 8 3) 20 10 true = naturalRefundState (gas 10 20 30 8 3) 20 10 true :=
  refundStateGas_eq_natural_of_no_wrap _ _ _ _ partial_refill_no_wrap_domain

theorem ordinary_halt_floor_no_wrap_domain : HaltFloorNoWrap constants ordinary := by
  constructor <;> simp only [T.FitsInt64] <;> decide

theorem ordinary_halt_floor_invokes_natural_bridge :
    haltStateFloor constants ordinary = naturalHaltStateFloor constants ordinary :=
  haltStateFloor_eq_natural_of_no_wrap _ _ ordinary_halt_floor_no_wrap_domain

theorem ordinary_payment_invokes_natural_bridge :
    (finish ordinary ordinary.incomingGas none (consumed 289000 289000 260000 30000 290000 1000) true none).paymentAmount =
      1422000 :=
  payment_eq_natural_of_no_wrap _ _ _ _ _ (by unfold T.FitsUInt64; decide) (by decide) (by decide)

end OrdinaryTransactionRefundAdapterExtractor.Vectors.OrdinaryTransactionRefundVectors
