-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import OrdinaryTransactionRefundAdapterExtractor.Generated.OrdinaryTransactionRefund
import OrdinaryTransactionRefundAdapterExtractor.Specification.OrdinaryTransactionRefund
import Eip803x.Refinement.TransactionSettlement
import Eip803x.Refinement.StateGasTransitionAdapterKernel

namespace OrdinaryTransactionRefundAdapterExtractor.Refinement.OrdinaryTransactionRefund

namespace G
export OrdinaryTransactionRefundAdapterExtractor.Generated.OrdinaryTransactionRefund
  (Entry GasState Constants Input SettlementInput Result constants sourceClosureSha256 semanticIrSha256
   preRefundGas refundStateGas resetForHalt clearExecutionGas initialStateReservoir haltStateFloor
   completeHaltGas codeInsertExecutionRefund shouldValidateGas shouldRefundGas refundQuotient
   prepareOrdinaryGas settlementInput settle modifiesCaller finish haltConsumed halt fullGas evaluate)
end G

namespace S
export OrdinaryTransactionRefundAdapterExtractor.Specification.OrdinaryTransactionRefund
  (Entry GasState Constants Input Result uint256Modulus preRefundGas preRefundDifference refundApplied
   refundToExecution refundStateGas resetForHalt clearExecutionGas refundRevertedExecutionStateGas
   initialStateReservoir haltStateFloor completeHaltGas codeInsertExecutionRefund shouldRefundGas
   refundQuotient prepareOrdinaryGas settlementInput modifiesCaller finish haltConsumed halt fullGas
   evaluate mulUInt64)
end S

namespace T
export Eip803x.TransactionSettlement
  (Input Result FitsUInt64 FitsInt64 FitsInt32 uint64Max wrapUInt64 wrapInt64 uint64ToInt64
   int64ToUInt64 addUInt64 subUInt64 saturatingSubUInt64 settle)
end T

def mapEntry : G.Entry → S.Entry
  | .ordinaryRefund => .ordinaryRefund
  | .preparationOutOfGas => .preparationOutOfGas
  | .createStateOutOfGas => .createStateOutOfGas
  | .contractCollision => .contractCollision
  | .failedDeposit => .failedDeposit

def mapGas (gas : G.GasState) : S.GasState :=
  { value := gas.value, stateReservoir := gas.stateReservoir, stateGasUsed := gas.stateGasUsed,
    stateGasSpill := gas.stateGasSpill, stateGasSpillRefunded := gas.stateGasSpillRefunded }

def mapConstants (constants : G.Constants) : S.Constants :=
  { executionCap := constants.executionCap, createStateCost := constants.createStateCost,
    newAccountCost := constants.newAccountCost, perAuthorizationCost := constants.perAuthorizationCost,
    legacyRefundQuotient := constants.legacyRefundQuotient,
    eip3529RefundQuotient := constants.eip3529RefundQuotient }

def mapInput (input : G.Input) : S.Input :=
  { entry := mapEntry input.entry, transactionGasLimit := input.transactionGasLimit,
    gasPrice := input.gasPrice, maxFeePerGas := input.maxFeePerGas,
    maxPriorityFeePerGas := input.maxPriorityFeePerGas, skipValidation := input.skipValidation,
    isContractCreation := input.isContractCreation, isEip8037Enabled := input.isEip8037Enabled,
    isEip3529Enabled := input.isEip3529Enabled, isEip7778Enabled := input.isEip7778Enabled,
    isError := input.isError, shouldRevert := input.shouldRevert, refundCounter := input.refundCounter,
    destroyCount := input.destroyCount, destroyRefund := input.destroyRefund,
    codeInsertRefundCount := input.codeInsertRefundCount, incomingGas := mapGas input.incomingGas,
    intrinsicStandard := mapGas input.intrinsicStandard, floorGas := mapGas input.floorGas,
    postIntrinsicStateReservoir := input.postIntrinsicStateReservoir,
    topLevelCreateStateGasCharged := input.topLevelCreateStateGasCharged }

def mapSettlement (input : G.SettlementInput) : T.Input :=
  { transactionGasLimit := input.transactionGasLimit, preRefundGas := input.preRefundGas,
    refundCounter := input.refundCounter, destroyCount := input.destroyCount,
    destroyRefund := input.destroyRefund, codeInsertExecutionRefund := input.codeInsertExecutionRefund,
    calldataFloorGas := input.calldataFloorGas, stateGasUsed := input.stateGasUsed,
    refundQuotient := input.refundQuotient, isError := input.isError, shouldRevert := input.shouldRevert,
    isEip8037Enabled := input.isEip8037Enabled, isEip7778Enabled := input.isEip7778Enabled }

def mapConsumed (result : Eip803x.Generated.TransactionSettlementKernel.TransactionSettlementResult) : T.Result :=
  { spentGas := result.spentGas, operationGas := result.operationGas, blockGas := result.blockGas,
    blockStateGas := result.blockStateGas, maxUsedGas := result.maxUsedGas, gasRefund := result.gasRefund }

def mapResult (result : G.Result) : S.Result :=
  { workingGas := mapGas result.workingGas, callerGas := mapGas result.callerGas,
    settlement := result.settlement.map mapSettlement, consumed := mapConsumed result.consumed,
    payRefundCalled := result.payRefundCalled, paymentAmount := result.paymentAmount,
    senderCredit := result.senderCredit, haltStateFloor := result.haltStateFloor }

/-- Identities checked by the source/compiler admission gate, not a semantic assumption about outputs. -/
structure SourceWitness where
  closureSha256 : String
  semanticIrSha256 : String

/-- Raw typed helper-entry values; upstream snapshot provenance and external hooks remain separate. -/
structure Adapter (source : SourceWitness) (input : G.Input) : Prop where
  closureIdentity : source.closureSha256 = G.sourceClosureSha256
  irIdentity : source.semanticIrSha256 = G.semanticIrSha256
  inputDomain : (mapInput input).Valid

structure SourceAttached (source : SourceWitness) (input : G.Input) : Prop where
  closureIdentity : source.closureSha256 = G.sourceClosureSha256
  irIdentity : source.semanticIrSha256 = G.semanticIrSha256
  refines : mapResult (G.evaluate input) = S.evaluate (mapConstants G.constants) (mapInput input)

namespace K
export Eip803x.Generated.StateGasTransitionKernel
  (StateGasTransitionResult normalizeUInt64 wrapUInt64 wrapInt64 addUInt64 addInt64 subInt64
   int64ToUInt64 refundStateGas)
end K

private theorem state_normalize_of_bounded (value : Nat) (h : T.FitsUInt64 value) :
    K.normalizeUInt64 value = value := by
  change value ≤ Eip803x.Generated.StateGasTransitionKernel.uint64Max at h
  simp [K.normalizeUInt64, h]

private theorem state_wrap_of_bounded (value : Int) (h : T.FitsInt64 value) :
    K.wrapInt64 value = value := by
  exact OrdinaryTransactionRefundAdapterExtractor.Specification.OrdinaryTransactionRefund.wrapInt64_of_bounded value h

private theorem positive_part (value : Int) : (if value > 0 then value else 0) = max 0 value := by
  split <;> omega

theorem refund_state_modular_refines (gas : G.GasState) (amount floor : Int) (track : Bool)
    (hGas : (mapGas gas).Valid) (hAmount : T.FitsInt64 amount) (hFloor : T.FitsInt64 floor) :
    mapGas (G.refundStateGas gas amount floor track) = S.refundStateGas (mapGas gas) amount floor track := by
  rcases hGas with ⟨hv, hr, hu, hp, hq⟩
  dsimp only [mapGas] at hv hr hu hp hq
  have hKernel : K.refundStateGas gas.value gas.stateReservoir gas.stateGasUsed gas.stateGasSpill
      gas.stateGasSpillRefunded amount floor track =
      let applied := S.refundApplied (mapGas gas) amount floor
      let toExecution := S.refundToExecution (mapGas gas) amount floor track
      { value := T.addUInt64 gas.value (T.int64ToUInt64 toExecution),
        stateReservoir := T.wrapInt64 (gas.stateReservoir + T.wrapInt64 (applied - toExecution)),
        stateGasUsed := T.wrapInt64 (gas.stateGasUsed - applied),
        stateGasSpill := gas.stateGasSpill,
        stateGasSpillRefunded := if track then T.wrapInt64 (gas.stateGasSpillRefunded + toExecution)
          else gas.stateGasSpillRefunded, unappliedAmount := 0 } := by
    rw [K.refundStateGas, state_normalize_of_bounded _ hv,
      state_wrap_of_bounded _ hr, state_wrap_of_bounded _ hu, state_wrap_of_bounded _ hp,
      state_wrap_of_bounded _ hq, state_wrap_of_bounded _ hAmount, state_wrap_of_bounded _ hFloor]
    change (let applied := min amount (if K.subInt64 gas.stateGasUsed floor > 0 then
        K.subInt64 gas.stateGasUsed floor else 0)
      let toExecution := if track then min applied
        (if K.subInt64 gas.stateGasSpill gas.stateGasSpillRefunded > 0 then
          K.subInt64 gas.stateGasSpill gas.stateGasSpillRefunded else 0) else 0
      { value := K.addUInt64 gas.value (K.int64ToUInt64 toExecution),
        stateReservoir := K.addInt64 gas.stateReservoir (K.subInt64 applied toExecution),
        stateGasUsed := K.subInt64 gas.stateGasUsed applied, stateGasSpill := gas.stateGasSpill,
        stateGasSpillRefunded := if track then K.addInt64 gas.stateGasSpillRefunded toExecution
          else gas.stateGasSpillRefunded, unappliedAmount := 0 } : K.StateGasTransitionResult) = _
    simp only [positive_part]
    rfl
  unfold G.refundStateGas
  rw [hKernel]
  rfl

theorem pre_refund_refines (gas : G.GasState) (limit : Nat) :
    G.preRefundGas gas limit = S.preRefundGas (mapGas gas) limit := by
  simp only [G.preRefundGas, S.preRefundGas, S.preRefundDifference, mapGas,
    Bool.and_eq_true, decide_eq_true_eq]
  change (if 0 ≤ (limit : Int) - gas.value - gas.stateReservoir ∧
      (limit : Int) - gas.value - gas.stateReservoir ≤ (T.uint64Max : Int) then
    T.wrapUInt64 ((limit : Int) - gas.value - gas.stateReservoir) else limit) =
    (if 0 ≤ (limit : Int) - gas.value - gas.stateReservoir ∧
      (limit : Int) - gas.value - gas.stateReservoir ≤ (T.uint64Max : Int) then
    ((limit : Int) - gas.value - gas.stateReservoir).toNat else limit)
  split
  · rename_i h
    simp only [T.wrapUInt64, if_pos h]
  · rfl

theorem halt_floor_refines (constants : G.Constants) (input : G.Input) :
    G.haltStateFloor constants input = S.haltStateFloor (mapConstants constants) (mapInput input) := rfl

private theorem reset_refines (gas : G.GasState) (reservoir used : Int) :
    mapGas (G.resetForHalt gas reservoir used) = S.resetForHalt (mapGas gas) reservoir used := rfl

private theorem clear_refines (gas : G.GasState) :
    mapGas (G.clearExecutionGas gas) = S.clearExecutionGas (mapGas gas) := rfl

theorem halt_gas_refines (constants : G.Constants) (input : G.Input) (gas : G.GasState)
    (hGas : (mapGas gas).Valid) :
    mapGas (G.completeHaltGas constants input gas) =
      S.completeHaltGas (mapConstants constants) (mapInput input) (mapGas gas) := by
  simp only [G.completeHaltGas, clear_refines, reset_refines, halt_floor_refines,
    S.completeHaltGas, S.refundRevertedExecutionStateGas]
  apply congrArg (fun refunded => S.clearExecutionGas (S.resetForHalt refunded
    input.postIntrinsicStateReservoir (S.haltStateFloor (mapConstants constants) (mapInput input))))
  change mapGas (if input.isEip8037Enabled && gas.stateGasUsed >
      S.haltStateFloor (mapConstants constants) (mapInput input) then
    G.refundStateGas gas gas.stateGasUsed (S.haltStateFloor (mapConstants constants) (mapInput input)) true
    else gas) = (if input.isEip8037Enabled && gas.stateGasUsed >
      S.haltStateFloor (mapConstants constants) (mapInput input) then
    S.refundStateGas (mapGas gas) gas.stateGasUsed (S.haltStateFloor (mapConstants constants) (mapInput input)) true
    else mapGas gas)
  split
  · exact refund_state_modular_refines gas gas.stateGasUsed _ true hGas hGas.2.2.1
      (OrdinaryTransactionRefundAdapterExtractor.Specification.OrdinaryTransactionRefund.haltStateFloor_is_bounded _ _)
  · rfl

private theorem code_refund_refines (input : G.Input) (count : Nat) :
    G.codeInsertExecutionRefund input count =
      S.codeInsertExecutionRefund (mapConstants G.constants) count input.isEip8037Enabled := by
  cases h : input.isEip8037Enabled <;>
    simp [G.codeInsertExecutionRefund, S.codeInsertExecutionRefund, h, G.constants,
      mapConstants, S.mulUInt64, T.subUInt64]
  rfl

private theorem payment_guard_refines (input : G.Input) :
    G.shouldRefundGas input = S.shouldRefundGas (mapInput input) := rfl

private theorem caller_mutation_refines (input : G.Input) :
    G.modifiesCaller input = S.modifiesCaller (mapInput input) := by
  cases h : input.entry <;> simp [G.modifiesCaller, S.modifiesCaller, mapInput, mapEntry, h]
  all_goals rfl

theorem prepare_ordinary_refines (input : G.Input) (hInput : (mapInput input).Valid) :
    mapGas (G.prepareOrdinaryGas G.constants input) =
      S.prepareOrdinaryGas (mapConstants G.constants) (mapInput input) := by
  have hGas := hInput.2.2.2.2.2.2.2.2.2.1
  have hFloor := hInput.2.2.2.2.2.2.2.2.2.2.1.2.1
  have hAmount : T.FitsInt64 (if input.isContractCreation then G.constants.createStateCost else 0) := by
    cases input.isContractCreation <;> unfold T.FitsInt64 G.constants <;> decide
  cases hEnabled : input.isEip8037Enabled <;> cases hRevert : input.shouldRevert <;>
    cases hCharged : input.topLevelCreateStateGasCharged <;> cases hCreate : input.isContractCreation <;>
    simp only [G.prepareOrdinaryGas, S.prepareOrdinaryGas, mapInput, mapConstants,
      hEnabled, hRevert, hCharged, hCreate, Bool.and_false,
      Bool.and_true, Bool.false_eq_true, ↓reduceIte]
  all_goals try rfl
  exact refund_state_modular_refines _ _ _ _ hGas
    (by unfold T.FitsInt64 G.constants; decide) hFloor

theorem settlement_input_refines (input : G.Input) (gas : G.GasState) (codeRefund : Nat) :
    mapSettlement (G.settlementInput G.constants input gas codeRefund) =
      S.settlementInput (mapConstants G.constants) (mapInput input) (mapGas gas) codeRefund := by
  simp only [G.settlementInput, mapSettlement, S.settlementInput, mapInput, mapGas,
    S.refundQuotient, mapConstants, G.constants, pre_refund_refines]
  rfl

private theorem settle_refines (input : G.SettlementInput) :
    mapConsumed (G.settle input) = T.settle (mapSettlement input) := rfl

theorem halt_consumed_refines (constants : G.Constants) (input : G.Input) (gas : G.GasState)
    (codeRefund : Nat) :
    mapConsumed (G.haltConsumed constants input gas codeRefund) =
      S.haltConsumed (mapConstants constants) (mapInput input) (mapGas gas) codeRefund := rfl

private theorem finish_refines (input : G.Input) (gas : G.GasState)
    (settlement : Option G.SettlementInput)
    (consumed : Eip803x.Generated.TransactionSettlementKernel.TransactionSettlementResult)
    (payRefundCalled : Bool) (floor : Option Int) :
    mapResult (G.finish input gas settlement consumed payRefundCalled floor) =
      S.finish (mapInput input) (mapGas gas) (settlement.map mapSettlement)
        (mapConsumed consumed) payRefundCalled floor := by
  simp only [G.finish, mapResult, S.finish, caller_mutation_refines]
  cases h : S.modifiesCaller (mapInput input) <;>
    simp only [Bool.false_eq_true, ↓reduceIte] <;> rfl

private theorem halt_refines (constants : G.Constants) (input : G.Input) (gas : G.GasState)
    (codeRefund : Nat) (hGas : (mapGas gas).Valid) :
    mapResult (G.halt constants input gas codeRefund) =
      S.halt (mapConstants constants) (mapInput input) (mapGas gas) codeRefund := by
  simp only [G.halt, finish_refines, halt_gas_refines _ _ _ hGas,
    halt_consumed_refines, halt_floor_refines, payment_guard_refines, S.halt]
  rfl

private theorem full_gas_refines (limit : Nat) : mapConsumed (G.fullGas limit) = S.fullGas limit := rfl

private theorem ordinary_entry_refines (input : G.Input) :
    (input.entry == .ordinaryRefund) = ((mapInput input).entry == .ordinaryRefund) := by
  cases h : input.entry <;> simp [mapInput, mapEntry, h]
  all_goals rfl

theorem generated_refines_spec (input : G.Input) (hInput : (mapInput input).Valid) :
    mapResult (G.evaluate input) = S.evaluate (mapConstants G.constants) (mapInput input) := by
  have hGas := hInput.2.2.2.2.2.2.2.2.2.1
  have hPrepared := prepare_ordinary_refines input hInput
  have hPreparedGas : (mapGas (G.prepareOrdinaryGas G.constants input)).Valid := by
    rw [hPrepared]
    exact OrdinaryTransactionRefundAdapterExtractor.Specification.OrdinaryTransactionRefund.prepareOrdinaryGas_preserves_machine_bounds _ _ hGas
  simp only [G.evaluate, S.evaluate, ← ordinary_entry_refines]
  split
  · by_cases hHalt : (input.isError && input.isEip8037Enabled) = true
    · have hMapped : ((mapInput input).isError && (mapInput input).isEip8037Enabled) = true := hHalt
      rw [if_pos hHalt, if_pos hMapped, halt_refines _ _ _ _ hPreparedGas, hPrepared, code_refund_refines]
      rfl
    · have hMapped : ¬((mapInput input).isError && (mapInput input).isEip8037Enabled) = true := hHalt
      rw [if_neg hHalt, if_neg hMapped]
      simp only [finish_refines, Option.map_some, settle_refines, settlement_input_refines,
        hPrepared, code_refund_refines, payment_guard_refines]
      rfl
  · by_cases hEnabled : input.isEip8037Enabled = true
    · have hMapped : (mapInput input).isEip8037Enabled = true := hEnabled
      rw [if_pos hEnabled, if_pos hMapped]
      exact halt_refines _ _ _ _ hGas
    · have hMapped : ¬(mapInput input).isEip8037Enabled = true := hEnabled
      rw [if_neg hEnabled, if_neg hMapped]
      simp only [finish_refines, Option.map_none, full_gas_refines]
      rfl

/-- The admitted identities attach the computed observation theorem to the frozen source closure. -/
theorem source_attached_refines (source : SourceWitness) (input : G.Input)
    (adapter : Adapter source input) : SourceAttached source input :=
  ⟨adapter.closureIdentity, adapter.irIdentity, generated_refines_spec input adapter.inputDomain⟩

theorem generated_gas_and_payment_fields_refine (input : G.Input) (hInput : (mapInput input).Valid) :
    mapGas (G.evaluate input).workingGas = (S.evaluate (mapConstants G.constants) (mapInput input)).workingGas ∧
    mapGas (G.evaluate input).callerGas = (S.evaluate (mapConstants G.constants) (mapInput input)).callerGas ∧
    (G.evaluate input).settlement.map mapSettlement = (S.evaluate (mapConstants G.constants) (mapInput input)).settlement ∧
    mapConsumed (G.evaluate input).consumed = (S.evaluate (mapConstants G.constants) (mapInput input)).consumed ∧
    (G.evaluate input).payRefundCalled = (S.evaluate (mapConstants G.constants) (mapInput input)).payRefundCalled ∧
    (G.evaluate input).paymentAmount = (S.evaluate (mapConstants G.constants) (mapInput input)).paymentAmount ∧
    (G.evaluate input).senderCredit = (S.evaluate (mapConstants G.constants) (mapInput input)).senderCredit ∧
    (G.evaluate input).haltStateFloor = (S.evaluate (mapConstants G.constants) (mapInput input)).haltStateFloor := by
  have h := generated_refines_spec input hInput
  exact ⟨congrArg (fun r : S.Result => r.workingGas) h, congrArg (fun r : S.Result => r.callerGas) h,
    congrArg (fun r : S.Result => r.settlement) h, congrArg (fun r : S.Result => r.consumed) h,
    congrArg (fun r : S.Result => r.payRefundCalled) h, congrArg (fun r : S.Result => r.paymentAmount) h,
    congrArg (fun r : S.Result => r.senderCredit) h, congrArg (fun r : S.Result => r.haltStateFloor) h⟩

theorem local_copy_routes_preserve_caller (input : G.Input) (hInput : (mapInput input).Valid)
    (hRoute : input.entry = .ordinaryRefund ∨ input.entry = .contractCollision) :
    mapGas (G.evaluate input).callerGas = mapGas input.incomingGas := by
  have h := (generated_gas_and_payment_fields_refine input hInput).2.1
  rw [h]
  rcases hRoute with hRoute | hRoute
  · apply OrdinaryTransactionRefundAdapterExtractor.Specification.OrdinaryTransactionRefund.ordinary_refund_preserves_caller
    simp [mapInput, mapEntry, hRoute]
  · apply OrdinaryTransactionRefundAdapterExtractor.Specification.OrdinaryTransactionRefund.collision_preserves_caller
    simp [mapInput, mapEntry, hRoute]

theorem direct_routes_publish_halt_gas (input : G.Input) (hInput : (mapInput input).Valid)
    (hRoute : input.entry = .preparationOutOfGas ∨ input.entry = .createStateOutOfGas ∨ input.entry = .failedDeposit)
    (hEnabled : input.isEip8037Enabled = true) :
    mapGas (G.evaluate input).callerGas = mapGas (G.evaluate input).workingGas := by
  have h := generated_gas_and_payment_fields_refine input hInput
  rw [h.1, h.2.1]
  apply OrdinaryTransactionRefundAdapterExtractor.Specification.OrdinaryTransactionRefund.direct_halt_updates_caller
  · rcases hRoute with hRoute | hRoute | hRoute <;> simp [mapInput, mapEntry, hRoute]
  · exact hEnabled

theorem refund_state_natural_refines (gas : G.GasState) (amount floor : Int) (track : Bool)
    (h : OrdinaryTransactionRefundAdapterExtractor.Specification.OrdinaryTransactionRefund.RefundNoWrap
      (mapGas gas) amount floor track) :
    mapGas (G.refundStateGas gas amount floor track) =
      OrdinaryTransactionRefundAdapterExtractor.Specification.OrdinaryTransactionRefund.naturalRefundState
        (mapGas gas) amount floor track := by
  rw [refund_state_modular_refines gas amount floor track h.gasBounded h.amountBounded h.floorBounded]
  exact OrdinaryTransactionRefundAdapterExtractor.Specification.OrdinaryTransactionRefund.refundStateGas_eq_natural_of_no_wrap _ _ _ _ h

theorem halt_floor_natural_refines (input : G.Input)
    (h : OrdinaryTransactionRefundAdapterExtractor.Specification.OrdinaryTransactionRefund.HaltFloorNoWrap
      (mapConstants G.constants) (mapInput input)) :
    G.haltStateFloor G.constants input =
      OrdinaryTransactionRefundAdapterExtractor.Specification.OrdinaryTransactionRefund.naturalHaltStateFloor
        (mapConstants G.constants) (mapInput input) := by
  rw [halt_floor_refines]
  exact OrdinaryTransactionRefundAdapterExtractor.Specification.OrdinaryTransactionRefund.haltStateFloor_eq_natural_of_no_wrap _ _ h

theorem payment_natural_refines (input : G.Input) (gas : G.GasState)
    (settlement : Option G.SettlementInput)
    (consumed : Eip803x.Generated.TransactionSettlementKernel.TransactionSettlementResult) (floor : Option Int)
    (hLimit : T.FitsUInt64 input.transactionGasLimit) (hSpent : consumed.spentGas ≤ input.transactionGasLimit)
    (hProduct : (input.transactionGasLimit - consumed.spentGas) * input.gasPrice < S.uint256Modulus) :
    (G.finish input gas settlement consumed true floor).paymentAmount =
      (input.transactionGasLimit - consumed.spentGas) * input.gasPrice := by
  have h := congrArg (fun r : S.Result => r.paymentAmount) (finish_refines input gas settlement consumed true floor)
  change (G.finish input gas settlement consumed true floor).paymentAmount = _ at h
  rw [h]
  exact OrdinaryTransactionRefundAdapterExtractor.Specification.OrdinaryTransactionRefund.payment_eq_natural_of_no_wrap
    _ _ _ _ _ hLimit hSpent hProduct

def normalSettlementInput (input : G.Input) : T.Input :=
  S.settlementInput (mapConstants G.constants) (mapInput input)
    (S.prepareOrdinaryGas (mapConstants G.constants) (mapInput input))
    (S.codeInsertExecutionRefund (mapConstants G.constants) input.codeInsertRefundCount input.isEip8037Enabled)

private theorem normal_consumed_refines (input : G.Input) (hInput : (mapInput input).Valid)
    (hEntry : input.entry = .ordinaryRefund) (hNormal : (input.isError && input.isEip8037Enabled) = false) :
    mapConsumed (G.evaluate input).consumed = T.settle (normalSettlementInput input) := by
  rw [(generated_gas_and_payment_fields_refine input hInput).2.2.2.1]
  exact OrdinaryTransactionRefundAdapterExtractor.Specification.OrdinaryTransactionRefund.normal_tail_projects_six_settlement_fields
    _ _ (by simp [mapInput, mapEntry, hEntry]) hNormal

namespace R
export Eip803x.Refinement.TransactionSettlement
  (PinnedNormalDomain SuccessNoWrap RevertNoWrap transactionGasInput successTotalRefund
   success_refines_transactionGas revert_refines_transactionGas)
end R

def NaturalSettlementObservation (observed : T.Result) (input : T.Input) (refund : Int) : Prop :=
  let naturalInput := R.transactionGasInput input refund
  let natural := Eip803x.TransactionGas.settle naturalInput
  naturalInput.Valid ∧ observed.spentGas = natural.paidGas ∧
    observed.operationGas = natural.gasUsedAfterRefund ∧ observed.blockGas = natural.executionGas ∧
    observed.blockStateGas = natural.stateGas ∧ observed.gasRefund = natural.gasRefund

/-- Natural success accounting derives its settlement input from the raw production boundary. -/
theorem normal_success_refines_transaction_gas (input : G.Input) (hInput : (mapInput input).Valid)
    (hEntry : input.entry = .ordinaryRefund) (hSuccess : input.shouldRevert = false)
    (hPinned : R.PinnedNormalDomain (normalSettlementInput input))
    (hNoWrap : R.SuccessNoWrap (normalSettlementInput input)) :
    NaturalSettlementObservation (mapConsumed (G.evaluate input).consumed) (normalSettlementInput input)
      (R.successTotalRefund (normalSettlementInput input)) := by
  have hError : input.isError = false := hPinned.2.2.1
  rw [normal_consumed_refines input hInput hEntry (by simp [hError])]
  exact R.success_refines_transactionGas hPinned hSuccess hNoWrap

/-- Natural REVERT accounting uses the same derived input, with no supplied refund result. -/
theorem normal_revert_refines_transaction_gas (input : G.Input) (hInput : (mapInput input).Valid)
    (hEntry : input.entry = .ordinaryRefund) (hRevert : input.shouldRevert = true)
    (hPinned : R.PinnedNormalDomain (normalSettlementInput input))
    (hNoWrap : R.RevertNoWrap (normalSettlementInput input)) :
    NaturalSettlementObservation (mapConsumed (G.evaluate input).consumed) (normalSettlementInput input)
      ((normalSettlementInput input).codeInsertExecutionRefund : Int) := by
  have hError : input.isError = false := hPinned.2.2.1
  rw [normal_consumed_refines input hInput hEntry (by simp [hError])]
  exact R.revert_refines_transactionGas hPinned hRevert hNoWrap

def boundaryInput (entry : G.Entry) (isError shouldRevert : Bool) : G.Input :=
  { entry, transactionGasLimit := 1000000, gasPrice := 2, maxFeePerGas := 0, maxPriorityFeePerGas := 0,
    skipValidation := false, isContractCreation := false, isEip8037Enabled := true,
    isEip3529Enabled := true, isEip7778Enabled := true, isError, shouldRevert,
    refundCounter := 1000, destroyCount := 0, destroyRefund := 0, codeInsertRefundCount := 0,
    incomingGas := {
      value := 700000, stateReservoir := 10000, stateGasUsed := 30000,
      stateGasSpill := 500, stateGasSpillRefunded := 100 },
    intrinsicStandard := {
      value := 21000, stateReservoir := 10000, stateGasUsed := 0,
      stateGasSpill := 0, stateGasSpillRefunded := 0 },
    floorGas := {
      value := 21000, stateReservoir := 0, stateGasUsed := 0,
      stateGasSpill := 0, stateGasSpillRefunded := 0 },
    postIntrinsicStateReservoir := 10000, topLevelCreateStateGasCharged := false }

/-- Every raw tagged boundary is inhabited; this does not assert upstream reachability of arbitrary flags. -/
theorem all_tagged_boundaries_inhabited (entry : G.Entry) (isError shouldRevert : Bool) :
    let input := boundaryInput entry isError shouldRevert
    let source : SourceWitness := ⟨G.sourceClosureSha256, G.semanticIrSha256⟩
    Adapter source input ∧ SourceAttached source input := by
  have hDomain : (mapInput (boundaryInput entry isError shouldRevert)).Valid := by
    cases entry <;> cases isError <;> cases shouldRevert <;> decide
  let adapter : Adapter ⟨G.sourceClosureSha256, G.semanticIrSha256⟩
      (boundaryInput entry isError shouldRevert) := ⟨rfl, rfl, hDomain⟩
  exact ⟨adapter, source_attached_refines _ _ adapter⟩

end OrdinaryTransactionRefundAdapterExtractor.Refinement.OrdinaryTransactionRefund
