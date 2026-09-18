-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.Generated.TransactionSettlementKernel
import Eip803x.Generated.StateGasTransitionKernel

namespace OrdinaryTransactionRefundAdapterExtractor.Generated.OrdinaryTransactionRefund

namespace T
export Eip803x.Generated.TransactionSettlementKernel
  (wrapUInt64 wrapInt64 uint64ToInt64 int64ToUInt64 uint64Max addUInt64 subUInt64
   saturatingSubUInt64 TransactionSettlementResult calculate)
end T

def sourceClosureSha256 : String := "6e86b85a492ff232caf31cdb126f75266350349b0c16112bd40ed0d3f99ee809"
def semanticIrSha256 : String := "53f6a24671a917e2675a1a45eb1b49e812b91283fc1c78bf49014f2ad08a1666"
def acceptanceState : String := "source-admitted"
def uint256Modulus : Nat := 2 ^ 256

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

def constants : Constants := { executionCap := 16777216, createStateCost := 183600, newAccountCost := 25000, perAuthorizationCost := 12500, legacyRefundQuotient := 2, eip3529RefundQuotient := 5 }

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

structure SettlementInput where
  transactionGasLimit : Nat
  preRefundGas : Nat
  refundCounter : Int
  destroyCount : Int
  destroyRefund : Nat
  codeInsertExecutionRefund : Nat
  calldataFloorGas : Nat
  stateGasUsed : Int
  refundQuotient : Nat
  isError : Bool
  shouldRevert : Bool
  isEip8037Enabled : Bool
  isEip7778Enabled : Bool
  deriving DecidableEq, Repr

structure Result where
  workingGas : GasState
  callerGas : GasState
  settlement : Option SettlementInput
  consumed : T.TransactionSettlementResult
  payRefundCalled : Bool
  paymentAmount : Nat
  senderCredit : Option Nat
  haltStateFloor : Option Int
  deriving DecidableEq, Repr

def preRefundGas (gas : GasState) (limit : Nat) : Nat :=
  let difference : Int := (((((limit : Int) : Int) - (((gas).value : Int) : Int)) : Int) - (((gas).stateReservoir : Int) : Int))
  let inRange : Bool := ((decide (difference ≥ ((0) : Int))) && (decide (difference ≤ ((18446744073709551615) : Int))))
  (if inRange then (T.wrapUInt64 difference) else limit)

def refundStateGas (gas : GasState) (amount floor : Int) (track : Bool) : GasState :=
  let next := Eip803x.Generated.StateGasTransitionKernel.refundStateGas
    gas.value gas.stateReservoir gas.stateGasUsed gas.stateGasSpill gas.stateGasSpillRefunded
    amount floor track
  { value := next.value, stateReservoir := next.stateReservoir, stateGasUsed := next.stateGasUsed,
    stateGasSpill := next.stateGasSpill, stateGasSpillRefunded := next.stateGasSpillRefunded }

def resetForHalt (gas : GasState) (reservoir used : Int) : GasState :=
  { gas with
    stateReservoir := reservoir
    stateGasUsed := used
    stateGasSpill := (0) }

def clearExecutionGas (gas : GasState) : GasState := { gas with value := (0) }

def initialStateReservoir (constants : Constants) (input : Input) : Int :=
  let signedLimit := T.uint64ToInt64 input.transactionGasLimit
  (max (0) (T.wrapInt64 (((T.wrapInt64 ((signedLimit : Int) - (((input).intrinsicStandard).stateReservoir : Int))) : Int) - ((T.uint64ToInt64 (constants).executionCap) : Int))))

def haltStateFloor (constants : Constants) (input : Input) : Int :=
  let initial := initialStateReservoir constants input
  let refundedIntrinsic : Int := (max (0) (T.wrapInt64 (((input).postIntrinsicStateReservoir : Int) - (initial : Int))))
  (max (0) (T.wrapInt64 ((((input).intrinsicStandard).stateReservoir : Int) - (refundedIntrinsic : Int))))

def completeHaltGas (constants : Constants) (input : Input) (gas : GasState) : GasState :=
  let floor := haltStateFloor constants input
  let refunded := if input.isEip8037Enabled && gas.stateGasUsed > floor then
    refundStateGas gas gas.stateGasUsed floor true else gas
  let restored := resetForHalt refunded input.postIntrinsicStateReservoir floor
  clearExecutionGas restored

def codeInsertExecutionRefund (input : Input) (count : Nat) : Nat :=
  if (count == (0)) then 0
  else if (input).isEip8037Enabled then 0
  else (T.wrapUInt64 (((T.wrapUInt64 (((25000) : Int) - ((12500) : Int))) : Int) * (count : Int)))

def shouldValidateGas (input : Input) : Bool := (((!(input).skipValidation) || ((input).maxFeePerGas != (0))) || ((input).maxPriorityFeePerGas != (0)))
def shouldRefundGas (input : Input) : Bool := ((!((input).gasPrice == (0))) && (shouldValidateGas input))
def refundQuotient (constants : Constants) (input : Input) : Nat :=
  if input.isEip3529Enabled then constants.eip3529RefundQuotient else constants.legacyRefundQuotient

def prepareOrdinaryGas (constants : Constants) (input : Input) : GasState :=
  if (((input).isEip8037Enabled && (input).shouldRevert) && (input).topLevelCreateStateGasCharged) then
    let amount : Int := (if (input).isContractCreation then (constants).createStateCost else (0))
    if amount > 0 then refundStateGas input.incomingGas amount input.intrinsicStandard.stateReservoir false
    else input.incomingGas
  else input.incomingGas

def settlementInput (_constants : Constants) (input : Input) (gas : GasState) (codeRefund : Nat) : SettlementInput :=
  let pre : Nat := (if (input).isError then (T.wrapUInt64 (0)) else (preRefundGas gas (input).transactionGasLimit))
  let used : Int := (if (input).isEip8037Enabled then (gas).stateGasUsed else (0))
  let quotient : Nat := (if (input).isEip3529Enabled then (5) else (2))
  let floor := input.floorGas.value
  { transactionGasLimit := (input).transactionGasLimit, preRefundGas := pre,
    refundCounter := (input).refundCounter, destroyCount := (input).destroyCount, destroyRefund := (input).destroyRefund,
    codeInsertExecutionRefund := codeRefund, calldataFloorGas := floor,
    stateGasUsed := used, refundQuotient := quotient,
    isError := (input).isError, shouldRevert := (input).shouldRevert,
    isEip8037Enabled := (input).isEip8037Enabled, isEip7778Enabled := (input).isEip7778Enabled }

def settle (input : SettlementInput) : T.TransactionSettlementResult :=
  let settlement := T.calculate input.transactionGasLimit input.preRefundGas input.refundCounter input.destroyCount
    input.destroyRefund input.codeInsertExecutionRefund input.calldataFloorGas input.stateGasUsed
    input.refundQuotient input.isError input.shouldRevert input.isEip8037Enabled input.isEip7778Enabled
  { spentGas := (settlement).spentGas, operationGas := (settlement).operationGas, blockGas := (settlement).blockGas,
    blockStateGas := (settlement).blockStateGas, maxUsedGas := (settlement).maxUsedGas, gasRefund := (settlement).gasRefund }

def modifiesCaller (input : Input) : Bool :=
  input.entry == .preparationOutOfGas || input.entry == .createStateOutOfGas ||
  (input.entry == .failedDeposit && input.isEip8037Enabled)

def finish (input : Input) (gas : GasState) (settlement : Option SettlementInput)
    (consumed : T.TransactionSettlementResult) (payRefundCalled : Bool) (floor : Option Int) : Result :=
  let amount := if payRefundCalled then
    (T.subUInt64 input.transactionGasLimit consumed.spentGas * input.gasPrice) % uint256Modulus else 0
  { workingGas := gas, callerGas := if modifiesCaller input then gas else input.incomingGas,
    settlement, consumed, payRefundCalled, paymentAmount := amount,
    senderCredit := if payRefundCalled && amount != 0 then some amount else none, haltStateFloor := floor }

def haltConsumed (constants : Constants) (input : Input) (gas : GasState) (codeRefund : Nat) : T.TransactionSettlementResult :=
  let before : Nat := (T.wrapUInt64 (((input).transactionGasLimit : Int) - ((T.wrapUInt64 (gas).stateReservoir) : Int)))
  let claimed : Nat := (min (before / refundQuotient constants input) codeRefund)
  let spent : Nat := (max (T.wrapUInt64 ((before : Int) - (claimed : Int))) ((input).floorGas).value)
  let stateUsed := gas.stateGasUsed
  let blockGas := max (T.saturatingSubUInt64 before (T.int64ToUInt64 stateUsed)) input.floorGas.value
  { spentGas := spent, operationGas := spent, blockGas := blockGas,
    blockStateGas := (T.wrapUInt64 stateUsed), maxUsedGas := spent, gasRefund := claimed }

def halt (constants : Constants) (input : Input) (gas : GasState) (codeRefund : Nat) : Result :=
  let after := completeHaltGas constants input gas
  let consumed := haltConsumed constants input after codeRefund
  let spent := consumed.spentGas
  finish input after none consumed ((shouldRefundGas input) && (decide (spent < (input).transactionGasLimit))) (some (haltStateFloor constants input))

def fullGas (limit : Nat) : T.TransactionSettlementResult :=
  { spentGas := limit, operationGas := limit, blockGas := 0, blockStateGas := 0, maxUsedGas := limit, gasRefund := 0 }

def evaluate (input : Input) : Result :=
  if input.entry == .ordinaryRefund then
    let gas := prepareOrdinaryGas constants input
    let codeRefund := codeInsertExecutionRefund input input.codeInsertRefundCount
    if ((input).isError && (input).isEip8037Enabled) then halt constants input gas codeRefund
    else
      let settlement := settlementInput constants input gas codeRefund
      finish input gas (some settlement) (settle settlement) (shouldRefundGas input) none
  else if input.isEip8037Enabled then halt constants input input.incomingGas 0
  else finish input input.incomingGas none (fullGas input.transactionGasLimit) false none

end OrdinaryTransactionRefundAdapterExtractor.Generated.OrdinaryTransactionRefund
