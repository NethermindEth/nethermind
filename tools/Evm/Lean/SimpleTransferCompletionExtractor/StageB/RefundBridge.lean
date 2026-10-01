-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import SimpleTransferCompletionExtractor.StageB.RuntimeSyntax
import SimpleTransferCompletionExtractor.StageB.Dispatch.Refinement
import OrdinaryTransactionRefundAdapterExtractor.Refinement.OrdinaryTransactionRefund

namespace SimpleTransferCompletionExtractor.StageB.RefundBridge

namespace G
export OrdinaryTransactionRefundAdapterExtractor.Generated.OrdinaryTransactionRefund
  (GasState Input Result constants evaluate)
end G

namespace S
export OrdinaryTransactionRefundAdapterExtractor.Specification.OrdinaryTransactionRefund
  (GasState Result evaluate)
end S

namespace R
export OrdinaryTransactionRefundAdapterExtractor.Refinement.OrdinaryTransactionRefund
  (SourceWitness Adapter SourceAttached mapGas mapInput mapConstants mapResult mapSettlement mapConsumed
   source_attached_refines generated_gas_and_payment_fields_refine local_copy_routes_preserve_caller)
end R

namespace D
export SimpleTransferCompletionExtractor.StageB.Dispatch.Generated (ReceiverKind)
export SimpleTransferCompletionExtractor.StageB.Dispatch.Refinement
  (SourceWitness RuntimeConcreteTypeObservation standardRuntimeReceiver Boundary)
end D

/-- These source observations are absent from `Runtime.Input` and must be supplied separately. -/
structure RawRefundObservations where
  maxFeePerGas : Nat
  maxPriorityFeePerGas : Nat
  skipValidation : Bool
  isEip3529Enabled : Bool
  isEip7778Enabled : Bool
  destroyRefund : Nat
  deriving DecidableEq, Repr

structure OpaqueReferences where
  tx : String
  header : String
  spec : String
  opts : String
  deriving DecidableEq, Repr

def OpaqueReferences.txValue (refs : OpaqueReferences) : Runtime.Value :=
  .reference "global::Nethermind.Core.Transaction" refs.tx

def OpaqueReferences.headerValue (refs : OpaqueReferences) : Runtime.Value :=
  .reference "global::Nethermind.Core.BlockHeader" refs.header

def OpaqueReferences.specValue (refs : OpaqueReferences) : Runtime.Value :=
  .reference "global::Nethermind.Core.Specs.IReleaseSpec" refs.spec

def OpaqueReferences.optsValue (refs : OpaqueReferences) : Runtime.Value :=
  .reference "global::Nethermind.Evm.TransactionProcessing.ExecutionOptions" refs.opts

/-- An external source-read relation; the runtime syntax does not implement these reads. -/
abbrev SourceObservation := Runtime.Value → List String → Runtime.Value → Prop

/-- Coherence with the same opaque objects used by the twelve operands, not upstream reachability. -/
def RawObservationsCoherent (observes : SourceObservation) (input : Runtime.Input)
    (refs : OpaqueReferences) (raw : RawRefundObservations) : Prop :=
  input.tx.recipient.isSome = true ∧
  observes refs.txValue ["GasLimit"] (.unsigned input.tx.gasLimit) ∧
  observes refs.txValue ["IsContractCreation"] (.boolean false) ∧
  observes refs.txValue ["MaxFeePerGas"] (.uint256 raw.maxFeePerGas) ∧
  observes refs.txValue ["MaxPriorityFeePerGas"] (.uint256 raw.maxPriorityFeePerGas) ∧
  observes refs.optsValue ["HasFlag", "SkipValidation"] (.boolean raw.skipValidation) ∧
  observes refs.specValue ["IsEip8037Enabled"] (.boolean input.spec.eip8037Enabled) ∧
  observes refs.specValue ["IsEip3529Enabled"] (.boolean raw.isEip3529Enabled) ∧
  observes refs.specValue ["IsEip7778Enabled"] (.boolean raw.isEip7778Enabled) ∧
  observes refs.specValue ["GasCosts", "DestroyRefund"] (.unsigned raw.destroyRefund)

def mapRuntimeGas (gas : Runtime.Gas) : G.GasState :=
  { value := gas.value, stateReservoir := gas.stateReservoir, stateGasUsed := gas.stateGasUsed,
    stateGasSpill := gas.stateGasSpill, stateGasSpillRefunded := gas.stateGasSpillRefunded }

def encodeGas (gas : Runtime.Gas) : Runtime.Value :=
  .struct "global::Nethermind.Evm.GasPolicy.EthereumGasPolicy"
    [("Value", .unsigned gas.value), ("StateReservoir", .signed gas.stateReservoir),
     ("StateGasUsed", .signed gas.stateGasUsed), ("StateGasSpill", .signed gas.stateGasSpill),
     ("StateGasSpillRefunded", .signed gas.stateGasSpillRefunded)]

def refundInput (input : Runtime.Input) (incomingGas : Runtime.Gas)
    (raw : RawRefundObservations) : G.Input :=
  { entry := .ordinaryRefund, transactionGasLimit := input.tx.gasLimit,
    gasPrice := input.opcodeGasPrice, maxFeePerGas := raw.maxFeePerGas,
    maxPriorityFeePerGas := raw.maxPriorityFeePerGas, skipValidation := raw.skipValidation,
    isContractCreation := false, isEip8037Enabled := input.spec.eip8037Enabled,
    isEip3529Enabled := raw.isEip3529Enabled, isEip7778Enabled := raw.isEip7778Enabled,
    isError := false, shouldRevert := false, refundCounter := 0, destroyCount := 0,
    destroyRefund := raw.destroyRefund, codeInsertRefundCount := 0,
    incomingGas := mapRuntimeGas incomingGas,
    intrinsicStandard := mapRuntimeGas input.intrinsic.standard,
    floorGas := mapRuntimeGas input.intrinsic.floorGas,
    postIntrinsicStateReservoir := incomingGas.stateReservoir,
    topLevelCreateStateGasCharged := false }

structure Locations where
  substate : Runtime.Location
  incomingGas : Runtime.Location
  gasPrice : Runtime.Location
  floorGas : Runtime.Location
  standardGas : Runtime.Location
  deriving DecidableEq, Repr

def simpleTransferSubstate (oog : Bool) : Runtime.Value :=
  .struct "global::Nethermind.Evm.TransactionSubstate"
    [("_logs", .struct "global::Nethermind.Core.Collections.JournalCollection<global::Nethermind.Core.LogEntry>" []),
     ("_destroyList", .null ""), ("Output", .bytes []), ("Refund", .signed 0),
     ("ShouldRevert", .boolean false),
     ("EvmExceptionType", .enum "global::Nethermind.Evm.EvmExceptionType" (if oog then 4 else 0)),
     ("Error", .null ""),
     ("_logger", .reference "global::Nethermind.Logging.ILogger" "logger")]

def valueOperand (ordinal : Nat) (value : Runtime.Value) : Runtime.RefundOperand :=
  { ordinal, mode := .value, value, location := none, provenance := none }

def locatedOperand (ordinal : Nat) (value : Runtime.Value)
    (location : Runtime.Location) : Runtime.RefundOperand :=
  { ordinal, mode := .readOnlyLocation, value, location := some location,
    provenance := some location.provenance }

def expectedOperands (input : Runtime.Input) (incomingGas : Runtime.Gas)
    (refs : OpaqueReferences) (locations : Locations) (oog : Bool) : List Runtime.RefundOperand :=
  [valueOperand 0 refs.txValue,
   valueOperand 1 refs.headerValue,
   valueOperand 2 refs.specValue,
   valueOperand 3 refs.optsValue,
   locatedOperand 4 (simpleTransferSubstate oog) locations.substate,
   locatedOperand 5 (encodeGas incomingGas) locations.incomingGas,
   locatedOperand 6 (.uint256 input.opcodeGasPrice) locations.gasPrice,
   valueOperand 7 (.unsigned 0),
   locatedOperand 8 (encodeGas input.intrinsic.floorGas) locations.floorGas,
   locatedOperand 9 (encodeGas input.intrinsic.standard) locations.standardGas,
   valueOperand 10 (.signed incomingGas.stateReservoir),
   valueOperand 11 (.boolean false)]

/-- Exact equality retains roots, field paths, readonly flags, and unabridged provenance. -/
def OperandsRepresent (operands : List Runtime.RefundOperand) (input : Runtime.Input)
    (incomingGas : Runtime.Gas) (refs : OpaqueReferences) (locations : Locations) (oog : Bool) : Prop :=
  operands = expectedOperands input incomingGas refs locations oog

structure Boundary (source : R.SourceWitness) (observes : SourceObservation)
    (operands : List Runtime.RefundOperand) (input : Runtime.Input) (incomingGas : Runtime.Gas)
    (refs : OpaqueReferences) (locations : Locations) (raw : RawRefundObservations) (oog : Bool) : Prop where
  representation : OperandsRepresent operands input incomingGas refs locations oog
  coherence : RawObservationsCoherent observes input refs raw
  adapter : R.Adapter source (refundInput input incomingGas raw)

theorem represented_operand_count (operands : List Runtime.RefundOperand) (input : Runtime.Input)
    (incomingGas : Runtime.Gas) (refs : OpaqueReferences) (locations : Locations) (oog : Bool)
    (represented : OperandsRepresent operands input incomingGas refs locations oog) :
    operands.length = 12 := by
  rw [represented]
  rfl

theorem represented_operand_ordinals (operands : List Runtime.RefundOperand) (input : Runtime.Input)
    (incomingGas : Runtime.Gas) (refs : OpaqueReferences) (locations : Locations) (oog : Bool)
    (represented : OperandsRepresent operands input incomingGas refs locations oog) :
    operands.map (·.ordinal) = List.range 12 := by
  rw [represented]
  rfl

section Refinement

variable {source : R.SourceWitness} {observes : SourceObservation}
  {operands : List Runtime.RefundOperand} {input : Runtime.Input} {incomingGas : Runtime.Gas}
  {refs : OpaqueReferences} {locations : Locations} {raw : RawRefundObservations} {oog : Bool}

/-- This boundary reuses the accepted refund theorem; it does not execute or resume the caller. -/
theorem source_attached_refines
    (boundary : Boundary source observes operands input incomingGas refs locations raw oog) :
    R.SourceAttached source (refundInput input incomingGas raw) :=
  R.source_attached_refines source _ boundary.adapter

/-- Payment fields describe the refund adapter's observations, not a world-state balance update. -/
theorem gas_and_payment_fields_refine
    (boundary : Boundary source observes operands input incomingGas refs locations raw oog) :
    let generated := G.evaluate (refundInput input incomingGas raw)
    let specified := S.evaluate (R.mapConstants G.constants) (R.mapInput (refundInput input incomingGas raw))
    R.mapGas generated.workingGas = specified.workingGas ∧
    R.mapGas generated.callerGas = specified.callerGas ∧
    generated.settlement.map R.mapSettlement = specified.settlement ∧
    generated.consumed.spentGas = specified.consumed.spentGas ∧
    generated.consumed.operationGas = specified.consumed.operationGas ∧
    generated.consumed.blockGas = specified.consumed.blockGas ∧
    generated.consumed.blockStateGas = specified.consumed.blockStateGas ∧
    generated.consumed.maxUsedGas = specified.consumed.maxUsedGas ∧
    generated.consumed.gasRefund = specified.consumed.gasRefund ∧
    generated.payRefundCalled = specified.payRefundCalled ∧
    generated.paymentAmount = specified.paymentAmount ∧
    generated.senderCredit = specified.senderCredit ∧
    generated.haltStateFloor = specified.haltStateFloor := by
  have h := R.generated_gas_and_payment_fields_refine _ boundary.adapter.inputDomain
  have consumed := h.2.2.2.1
  exact ⟨h.1, h.2.1, h.2.2.1,
    congrArg Eip803x.TransactionSettlement.Result.spentGas consumed,
    congrArg Eip803x.TransactionSettlement.Result.operationGas consumed,
    congrArg Eip803x.TransactionSettlement.Result.blockGas consumed,
    congrArg Eip803x.TransactionSettlement.Result.blockStateGas consumed,
    congrArg Eip803x.TransactionSettlement.Result.maxUsedGas consumed,
    congrArg Eip803x.TransactionSettlement.Result.gasRefund consumed,
    h.2.2.2.2⟩

theorem ordinary_refund_preserves_caller
    (boundary : Boundary source observes operands input incomingGas refs locations raw oog) :
    (G.evaluate (refundInput input incomingGas raw)).callerGas = mapRuntimeGas incomingGas := by
  have h := R.local_copy_routes_preserve_caller _ boundary.adapter.inputDomain (Or.inl rfl)
  have injective : ∀ left right : G.GasState, R.mapGas left = R.mapGas right → left = right := by
    intro left right equal
    cases left
    cases right
    cases equal
    rfl
  exact injective _ _ h

end Refinement

def encodeGasConsumed
    (consumed : Eip803x.Generated.TransactionSettlementKernel.TransactionSettlementResult) : Runtime.Value :=
  .struct "global::Nethermind.Evm.TransactionProcessing.GasConsumed"
    [("SpentGas", .unsigned consumed.spentGas), ("OperationGas", .unsigned consumed.operationGas),
     ("BlockGas", .unsigned consumed.blockGas), ("BlockStateGas", .unsigned consumed.blockStateGas),
     ("MaxUsedGas", .unsigned consumed.maxUsedGas), ("GasRefund", .unsigned consumed.gasRefund)]

def resultValue (input : Runtime.Input) (incomingGas : Runtime.Gas)
    (raw : RawRefundObservations) : Runtime.Value :=
  encodeGasConsumed (G.evaluate (refundInput input incomingGas raw)).consumed

structure RefinedBoundary (source : R.SourceWitness) (observes : SourceObservation)
    (operands : List Runtime.RefundOperand) (input : Runtime.Input) (incomingGas : Runtime.Gas)
    (refs : OpaqueReferences) (locations : Locations) (raw : RawRefundObservations) (oog : Bool)
    (result : Runtime.Value) : Prop where
  representation : OperandsRepresent operands input incomingGas refs locations oog
  coherence : RawObservationsCoherent observes input refs raw
  sourceAttached : R.SourceAttached source (refundInput input incomingGas raw)
  resultEncoding : result = resultValue input incomingGas raw

theorem boundary_refines {source : R.SourceWitness} {observes : SourceObservation}
    {operands : List Runtime.RefundOperand} {input : Runtime.Input} {incomingGas : Runtime.Gas}
    {refs : OpaqueReferences} {locations : Locations} {raw : RawRefundObservations} {oog : Bool}
    (boundary : Boundary source observes operands input incomingGas refs locations raw oog) :
    RefinedBoundary source observes operands input incomingGas refs locations raw oog
      (resultValue input incomingGas raw) :=
  ⟨boundary.representation, boundary.coherence, source_attached_refines boundary, rfl⟩

/-- Couples the refund operand/arithmetic boundary to an explicitly observed standard receiver leaf. -/
structure StandardMainnetBoundary
    (refundSource : R.SourceWitness) (refundObserves : SourceObservation)
    (operands : List Runtime.RefundOperand) (input : Runtime.Input) (incomingGas : Runtime.Gas)
    (refs : OpaqueReferences) (locations : Locations) (raw : RawRefundObservations) (oog : Bool)
    (dispatchSource : D.SourceWitness) (dispatchObserves : D.RuntimeConcreteTypeObservation)
    (receiver : D.ReceiverKind) : Prop where
  refund : Boundary refundSource refundObserves operands input incomingGas refs locations raw oog
  dispatch : D.Boundary dispatchSource dispatchObserves receiver

/-- Adds the explicitly observed standard receiver leaf without claiming runtime DI selection or host execution. -/
structure StandardMainnetRefinedBoundary
    (refundSource : R.SourceWitness) (refundObserves : SourceObservation)
    (operands : List Runtime.RefundOperand) (input : Runtime.Input) (incomingGas : Runtime.Gas)
    (refs : OpaqueReferences) (locations : Locations) (raw : RawRefundObservations) (oog : Bool)
    (result : Runtime.Value) (dispatchSource : D.SourceWitness)
    (dispatchObserves : D.RuntimeConcreteTypeObservation) (receiver : D.ReceiverKind) : Prop where
  refund : RefinedBoundary refundSource refundObserves operands input incomingGas refs locations raw oog result
  dispatch : D.Boundary dispatchSource dispatchObserves receiver

theorem attach_standard_mainnet_dispatch
    {refundSource : R.SourceWitness} {refundObserves : SourceObservation}
    {operands : List Runtime.RefundOperand} {input : Runtime.Input} {incomingGas : Runtime.Gas}
    {refs : OpaqueReferences} {locations : Locations} {raw : RawRefundObservations} {oog : Bool}
    {result : Runtime.Value} {dispatchSource : D.SourceWitness}
    {dispatchObserves : D.RuntimeConcreteTypeObservation} {receiver : D.ReceiverKind}
    (refund : RefinedBoundary refundSource refundObserves operands input incomingGas refs locations raw oog result)
    (dispatch : D.Boundary dispatchSource dispatchObserves receiver) :
    StandardMainnetRefinedBoundary refundSource refundObserves operands input incomingGas refs locations raw oog
      result dispatchSource dispatchObserves receiver :=
  ⟨refund, dispatch⟩

theorem standard_mainnet_boundary_refines
    {refundSource : R.SourceWitness} {refundObserves : SourceObservation}
    {operands : List Runtime.RefundOperand} {input : Runtime.Input} {incomingGas : Runtime.Gas}
    {refs : OpaqueReferences} {locations : Locations} {raw : RawRefundObservations} {oog : Bool}
    {dispatchSource : D.SourceWitness} {dispatchObserves : D.RuntimeConcreteTypeObservation}
    {receiver : D.ReceiverKind}
    (boundary : StandardMainnetBoundary refundSource refundObserves operands input incomingGas refs locations raw oog
      dispatchSource dispatchObserves receiver) :
    StandardMainnetRefinedBoundary refundSource refundObserves operands input incomingGas refs locations raw oog
      (resultValue input incomingGas raw) dispatchSource dispatchObserves receiver :=
  attach_standard_mainnet_dispatch (boundary_refines boundary.refund) boundary.dispatch

end SimpleTransferCompletionExtractor.StageB.RefundBridge
