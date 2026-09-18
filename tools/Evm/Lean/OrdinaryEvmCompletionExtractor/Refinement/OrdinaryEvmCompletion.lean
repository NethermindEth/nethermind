-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import OrdinaryEvmCompletionExtractor.Specification.OrdinaryEvmCompletion
import EvmTransactionPreparationExtractor.Refinement.EvmTransactionPreparation
import OrdinaryTransactionRefundAdapterExtractor.Refinement.OrdinaryTransactionRefund
import ReceiptTerminalFoldExtractor.Refinement.ReceiptTerminalFold

namespace OrdinaryEvmCompletionExtractor.Refinement.OrdinaryEvmCompletion

namespace S
export OrdinaryEvmCompletionExtractor.Specification.OrdinaryEvmCompletion
  (Input TailInput Boundary Representation ExternalPredicates ExternalAssumptions CounterRanges
   refundConstants prepared refundInput refundResult consumedGas receiptTransaction recipient
   finalizeInput receiptEntry receiptResult evaluate counter_ranges_receipt_sums)
end S
namespace P
export EvmTransactionPreparationExtractor.Refinement
  (mapInput mapResult RepresentationObligation generated_refines_reference)
end P
namespace FG
export OrdinaryTransactionRefundAdapterExtractor.Generated.OrdinaryTransactionRefund
  (GasState Input Result constants evaluate sourceClosureSha256 semanticIrSha256)
end FG
namespace FR
export OrdinaryTransactionRefundAdapterExtractor.Refinement.OrdinaryTransactionRefund
  (mapGas mapConstants mapInput mapResult generated_refines_spec
   SourceWitness Adapter SourceAttached source_attached_refines local_copy_routes_preserve_caller)
end FR
namespace FS
export OrdinaryTransactionRefundAdapterExtractor.Specification.OrdinaryTransactionRefund
  (GasState Input Result)
end FS
namespace RG
export ReceiptTerminalFoldExtractor.Generated.ReceiptTerminalFoldKernel
  (BytesOracle LogOracle ErrorOracle EvmExceptionOracle HashOracle AddressOracle PriceOracle
   Status Receipt State Gas GasTotals TransactionInput BlockInput FinalizeInput
   FinalizationObservation finalizeTransaction previousTotals effectiveBlockGas)
end RG
namespace RR
export ReceiptTerminalFoldExtractor.Refinement.ReceiptTerminalFold
  (mapBytes mapLog mapError mapEvmException mapHash mapAddress mapPrice mapStatus mapGas
   mapGasTotals mapReceipt mapState mapTransaction mapBlock mapFinalizeInput
   mapFinalizationObservation AccountingValid ProductionFinalizeDomain FitsUInt64
   generatedFinalizeTransaction_refines_spec previousTotals_map)
end RR
namespace RS
export ReceiptTerminalFoldExtractor.Specification.ReceiptTerminalFold
  (BytesOracle LogOracle ErrorOracle EvmExceptionOracle HashOracle AddressOracle PriceOracle
   Status Receipt State Gas GasTotals TransactionInput BlockInput FinalizeInput
   FinalizationObservation previousTotals effectiveBlockGas)
end RS

/-- Draft composition input, not a generated completion entry or source-admission witness. -/
structure Input where
  preparation : EvmTransactionPreparationExtractor.Generated.Input
  tail : S.TailInput

def mapInput (i : Input) : S.Input :=
  { preparation := P.mapInput i.preparation, tail := i.tail }

def toRefundGas (gas : FS.GasState) : FG.GasState :=
  { value := gas.value, stateReservoir := gas.stateReservoir, stateGasUsed := gas.stateGasUsed,
    stateGasSpill := gas.stateGasSpill, stateGasSpillRefunded := gas.stateGasSpillRefunded }

def toOrdinaryRefundInput (input : FS.Input) : FG.Input :=
  { entry := .ordinaryRefund, transactionGasLimit := input.transactionGasLimit,
    gasPrice := input.gasPrice, maxFeePerGas := input.maxFeePerGas,
    maxPriorityFeePerGas := input.maxPriorityFeePerGas, skipValidation := input.skipValidation,
    isContractCreation := input.isContractCreation, isEip8037Enabled := input.isEip8037Enabled,
    isEip3529Enabled := input.isEip3529Enabled, isEip7778Enabled := input.isEip7778Enabled,
    isError := input.isError, shouldRevert := input.shouldRevert, refundCounter := input.refundCounter,
    destroyCount := input.destroyCount, destroyRefund := input.destroyRefund,
    codeInsertRefundCount := input.codeInsertRefundCount, incomingGas := toRefundGas input.incomingGas,
    intrinsicStandard := toRefundGas input.intrinsicStandard, floorGas := toRefundGas input.floorGas,
    postIntrinsicStateReservoir := input.postIntrinsicStateReservoir,
    topLevelCreateStateGasCharged := input.topLevelCreateStateGasCharged }

theorem ordinary_refund_input_roundtrip (input : FS.Input)
    (ordinary : input.entry = .ordinaryRefund) :
    FR.mapInput (toOrdinaryRefundInput input) = input := by
  cases input
  cases ordinary
  rfl

def derivedRefundInput (i : Input) : FG.Input :=
  toOrdinaryRefundInput (S.refundInput (mapInput i))

theorem derived_refund_input_identity (i : Input) :
    FR.mapInput (derivedRefundInput i) = S.refundInput (mapInput i) :=
  ordinary_refund_input_roundtrip (S.refundInput (mapInput i)) rfl

theorem refund_constants_identity : FR.mapConstants FG.constants = S.refundConstants := rfl

def refundSource : FR.SourceWitness :=
  { closureSha256 := FG.sourceClosureSha256, semanticIrSha256 := FG.semanticIrSha256 }

theorem derived_refund_source_adapter (i : Input)
    (domain : (S.refundInput (mapInput i)).Valid) :
    FR.Adapter refundSource (derivedRefundInput i) :=
  { closureIdentity := rfl, irIdentity := rfl,
    inputDomain := by simpa only [derived_refund_input_identity] using domain }

theorem accepted_refund_at_derived_input (i : Input)
    (domain : (S.refundInput (mapInput i)).Valid) :
    FR.mapResult (FG.evaluate (derivedRefundInput i)) = S.refundResult (mapInput i) := by
  have attached := FR.source_attached_refines refundSource (derivedRefundInput i)
    (derived_refund_source_adapter i domain)
  simpa only [derived_refund_input_identity, refund_constants_identity, S.refundResult] using
    attached.refines

theorem accepted_refund_keeps_call_local_gas (i : Input)
    (domain : (S.refundInput (mapInput i)).Valid) :
    FR.mapGas (FG.evaluate (derivedRefundInput i)).callerGas = i.tail.vm.postGas := by
  have valid : (FR.mapInput (derivedRefundInput i)).Valid := by
    simpa only [derived_refund_input_identity] using domain
  exact FR.local_copy_routes_preserve_caller (derivedRefundInput i) valid (Or.inl rfl)

namespace ToReceipt

def bytes (value : RS.BytesOracle) : RG.BytesOracle := { id := value.id }
def log (value : RS.LogOracle) : RG.LogOracle := { id := value.id }
def error (value : RS.ErrorOracle) : RG.ErrorOracle := { id := value.id }
def exception (value : RS.EvmExceptionOracle) : RG.EvmExceptionOracle := { id := value.id }
def hash (value : RS.HashOracle) : RG.HashOracle := { id := value.id }
def address (value : RS.AddressOracle) : RG.AddressOracle := { id := value.id }
def price (value : RS.PriceOracle) : RG.PriceOracle := { id := value.id }
def status : RS.Status → RG.Status
  | .success => .success
  | .failure => .failure

def gas (value : RS.Gas) : RG.Gas :=
  { spentGas := value.spentGas, operationGas := value.operationGas, blockGas := value.blockGas,
    blockStateGas := value.blockStateGas, maxUsedGas := value.maxUsedGas, gasRefund := value.gasRefund }

def totals (value : RS.GasTotals) : RG.GasTotals :=
  { executionGas := value.executionGas, stateGas := value.stateGas }

def receipt (value : RS.Receipt) : RG.Receipt :=
  { logs := value.logs.map log, txType := value.txType, gasUsedTotal := value.gasUsedTotal,
    statusCode := value.statusCode, recipient := value.recipient.map address,
    blockHash := value.blockHash.map hash, blockNumber := value.blockNumber, index := value.index,
    gasUsed := value.gasUsed, effectiveGasPrice := price value.effectiveGasPrice,
    sender := value.sender.map address, contractAddress := value.contractAddress.map address,
    txHash := value.txHash.map hash, postTransactionState := value.postTransactionState.map hash,
    blockGasUsed := value.blockGasUsed, executionGasUsed := value.executionGasUsed,
    storageGasUsed := value.storageGasUsed, error := value.error.map error }

def state (value : RS.State) : RG.State :=
  { receipts := value.receipts.map receipt, gasHistory := value.gasHistory.map totals,
    cumulativeReceiptGas := value.cumulativeReceiptGas, headerGasUsed := value.headerGasUsed,
    parallel := value.parallel, currentIndex := value.currentIndex }

def transaction (value : RS.TransactionInput) : RG.TransactionInput :=
  { txType := value.txType, to := value.to.map address, sender := value.sender.map address,
    txHash := value.txHash.map hash, effectiveGasPrice := price value.effectiveGasPrice }

def block (value : RS.BlockInput) : RG.BlockInput :=
  { hash := value.hash.map hash, number := value.number, baseFeePerGas := value.baseFeePerGas }

def finalize (value : RS.FinalizeInput) : RG.FinalizeInput :=
  { status := status value.status, shouldRevert := value.shouldRevert, output := bytes value.output,
    substateError := value.substateError.map error, vmError := error value.vmError,
    evmExceptionType := value.evmExceptionType.map exception,
    substateResultError := value.substateResultError.map error, logs := value.logs.map log,
    stateRoot := value.stateRoot.map hash }

@[simp] theorem bytes_roundtrip (value : RS.BytesOracle) : RR.mapBytes (bytes value) = value := rfl
@[simp] theorem log_roundtrip (value : RS.LogOracle) : RR.mapLog (log value) = value := rfl
@[simp] theorem error_roundtrip (value : RS.ErrorOracle) : RR.mapError (error value) = value := rfl
@[simp] theorem exception_roundtrip (value : RS.EvmExceptionOracle) :
    RR.mapEvmException (exception value) = value := rfl
@[simp] theorem hash_roundtrip (value : RS.HashOracle) : RR.mapHash (hash value) = value := rfl
@[simp] theorem address_roundtrip (value : RS.AddressOracle) :
    RR.mapAddress (address value) = value := rfl
@[simp] theorem price_roundtrip (value : RS.PriceOracle) : RR.mapPrice (price value) = value := rfl
@[simp] theorem status_roundtrip (value : RS.Status) : RR.mapStatus (status value) = value := by
  cases value <;> rfl
@[simp] theorem gas_roundtrip (value : RS.Gas) : RR.mapGas (gas value) = value := rfl
@[simp] theorem totals_roundtrip (value : RS.GasTotals) : RR.mapGasTotals (totals value) = value := rfl

@[simp] theorem receipt_roundtrip (value : RS.Receipt) : RR.mapReceipt (receipt value) = value := by
  simp [receipt, RR.mapReceipt, List.map_map, Option.map_map, Function.comp_def]

@[simp] theorem state_roundtrip (value : RS.State) : RR.mapState (state value) = value := by
  simp [state, RR.mapState, List.map_map, Function.comp_def]

@[simp] theorem transaction_roundtrip (value : RS.TransactionInput) :
    RR.mapTransaction (transaction value) = value := by
  simp [transaction, RR.mapTransaction, Option.map_map, Function.comp_def]

@[simp] theorem block_roundtrip (value : RS.BlockInput) : RR.mapBlock (block value) = value := by
  simp [block, RR.mapBlock, Option.map_map, Function.comp_def]

@[simp] theorem finalize_roundtrip (value : RS.FinalizeInput) :
    RR.mapFinalizeInput (finalize value) = value := by
  simp [finalize, RR.mapFinalizeInput, List.map_map, Option.map_map, Function.comp_def]

theorem previous_totals_projection (values : List RS.GasTotals) :
    RG.previousTotals (values.map totals) = RS.previousTotals values := by
  simpa [List.map_map, Function.comp_def] using (RR.previousTotals_map (values.map totals)).symm

theorem effective_block_gas_projection (value : RS.Gas) :
    RG.effectiveBlockGas (gas value) = RS.effectiveBlockGas value := rfl

end ToReceipt

def derivedReceiptState (i : Input) : RG.State := ToReceipt.state (S.receiptEntry (mapInput i))
def derivedReceiptGas (i : Input) : RG.Gas := ToReceipt.gas (S.consumedGas (mapInput i))
def derivedFinalizeInput (i : Input) : RG.FinalizeInput := ToReceipt.finalize (S.finalizeInput (mapInput i))

theorem derived_receipt_prefix (i : Input) :
    RG.previousTotals (derivedReceiptState i).gasHistory =
      RS.previousTotals i.tail.receiptBefore.gasHistory :=
  ToReceipt.previous_totals_projection i.tail.receiptBefore.gasHistory

theorem derived_receipt_paid_prefix (i : Input) :
    (derivedReceiptState i).cumulativeReceiptGas = i.tail.receiptBefore.cumulativeReceiptGas := rfl

theorem derived_receipt_effective_gas (i : Input) :
    RG.effectiveBlockGas (derivedReceiptGas i) = RS.effectiveBlockGas (S.consumedGas (mapInput i)) :=
  ToReceipt.effective_block_gas_projection (S.consumedGas (mapInput i))

theorem derived_receipt_state_gas (i : Input) :
    (derivedReceiptGas i).blockStateGas = (S.consumedGas (mapInput i)).blockStateGas := rfl

theorem derived_receipt_spent_gas (i : Input) :
    (derivedReceiptGas i).spentGas = (S.consumedGas (mapInput i)).spentGas := rfl

theorem receipt_accounting_of_counter_ranges (i : Input)
    (ranges : S.CounterRanges (mapInput i)) :
    RR.AccountingValid (derivedReceiptState i) (derivedReceiptGas i) := by
  rcases S.counter_ranges_receipt_sums (mapInput i) ranges with ⟨execution, state, paid⟩
  have bounds :
      (RS.previousTotals i.tail.receiptBefore.gasHistory).1 ≤ Eip803x.TransactionSettlement.uint64Max ∧
      (RS.previousTotals i.tail.receiptBefore.gasHistory).2 ≤ Eip803x.TransactionSettlement.uint64Max ∧
      i.tail.receiptBefore.cumulativeReceiptGas ≤ Eip803x.TransactionSettlement.uint64Max ∧
      RS.effectiveBlockGas (S.consumedGas (mapInput i)) ≤ Eip803x.TransactionSettlement.uint64Max ∧
      (S.consumedGas (mapInput i)).blockStateGas ≤ Eip803x.TransactionSettlement.uint64Max ∧
      (S.consumedGas (mapInput i)).spentGas ≤ Eip803x.TransactionSettlement.uint64Max ∧
      (RS.previousTotals i.tail.receiptBefore.gasHistory).1 +
        RS.effectiveBlockGas (S.consumedGas (mapInput i)) ≤ Eip803x.TransactionSettlement.uint64Max ∧
      (RS.previousTotals i.tail.receiptBefore.gasHistory).2 +
        (S.consumedGas (mapInput i)).blockStateGas ≤ Eip803x.TransactionSettlement.uint64Max ∧
      i.tail.receiptBefore.cumulativeReceiptGas +
        (S.consumedGas (mapInput i)).spentGas ≤ Eip803x.TransactionSettlement.uint64Max := by
    dsimp only [mapInput] at execution state paid ⊢
    omega
  have sameBound : Eip803x.TransactionSettlement.uint64Max =
      Eip803x.Generated.TransactionGasInitializationKernel.uint64Max := rfl
  simpa only [RR.AccountingValid, RR.FitsUInt64, derived_receipt_prefix, derived_receipt_paid_prefix,
    derived_receipt_effective_gas, derived_receipt_state_gas, derived_receipt_spent_gas,
    sameBound] using bounds

def acceptedReceiptRun (i : Input) : RG.FinalizationObservation :=
  RG.finalizeTransaction (derivedReceiptState i) (ToReceipt.block i.tail.block)
    (ToReceipt.transaction (S.receiptTransaction (mapInput i)))
    (ToReceipt.address (S.recipient (mapInput i))) (derivedReceiptGas i) (derivedFinalizeInput i)
    i.tail.nestedReceiptTracer i.tail.currentTracerIsTracingReceipt

theorem derived_finalize_domain (i : Input) : RR.ProductionFinalizeDomain (derivedFinalizeInput i) := by
  constructor
  intro success
  cases terminal : i.tail.vm.terminal <;>
    simp_all [derivedFinalizeInput, S.finalizeInput, mapInput, ToReceipt.finalize, ToReceipt.status]

theorem accepted_receipt_at_derived_input (i : Input)
    (boundary : S.Boundary (mapInput i))
    (ranges : S.CounterRanges (mapInput i)) :
    RR.mapFinalizationObservation (acceptedReceiptRun i) = S.receiptResult (mapInput i) := by
  have sequential : (derivedReceiptState i).parallel = false := boundary.sequentialReceipts
  have result := RR.generatedFinalizeTransaction_refines_spec
    (derivedReceiptState i) (ToReceipt.block i.tail.block)
    (ToReceipt.transaction (S.receiptTransaction (mapInput i)))
    (ToReceipt.address (S.recipient (mapInput i))) (derivedReceiptGas i) (derivedFinalizeInput i)
    i.tail.nestedReceiptTracer i.tail.currentTracerIsTracingReceipt sequential
    (derived_finalize_domain i) (receipt_accounting_of_counter_ranges i ranges)
  simpa only [acceptedReceiptRun, derivedReceiptState, derivedReceiptGas, derivedFinalizeInput,
    ToReceipt.state_roundtrip, ToReceipt.block_roundtrip, ToReceipt.transaction_roundtrip,
    ToReceipt.address_roundtrip, ToReceipt.gas_roundtrip, ToReceipt.finalize_roundtrip,
    S.receiptResult, mapInput] using result

theorem accepted_preparation_at_entry (i : Input)
    (representation : P.RepresentationObligation i.preparation) :
    P.mapResult (EvmTransactionPreparationExtractor.Generated.prepare i.preparation) =
      S.prepared (mapInput i) :=
  P.generated_refines_reference i.preparation representation

/-- The terminal consumes the six computed refund fields, not a supplied GasConsumed. -/
theorem receipt_gas_from_accepted_refund (i : Input)
    (domain : (S.refundInput (mapInput i)).Valid) :
    derivedReceiptGas i =
      let gas := (FR.mapResult (FG.evaluate (derivedRefundInput i))).consumed
      ({ spentGas := gas.spentGas, operationGas := gas.operationGas, blockGas := gas.blockGas,
         blockStateGas := gas.blockStateGas, maxUsedGas := gas.maxUsedGas,
         gasRefund := gas.gasRefund } : RG.Gas) := by
  rw [accepted_refund_at_derived_input i domain]
  rfl

structure Domain (predicates : S.ExternalPredicates) (i : Input) : Prop where
  preparation : P.RepresentationObligation i.preparation
  boundary : S.Boundary (mapInput i)
  representation : S.Representation (mapInput i)
  counters : S.CounterRanges (mapInput i)
  external : S.ExternalAssumptions predicates (mapInput i)

/-- Constructive scalar/identity domain; actual VM and hook provenance are not fabricated. -/
structure InternalDomain (i : Input) : Prop where
  preparation : P.RepresentationObligation i.preparation
  boundary : S.Boundary (mapInput i)
  representation : S.Representation (mapInput i)
  counters : S.CounterRanges (mapInput i)

theorem InternalDomain.attach_external (i : Input) (internal : InternalDomain i)
    (predicates : S.ExternalPredicates)
    (external : S.ExternalAssumptions predicates (mapInput i)) : Domain predicates i :=
  { preparation := internal.preparation, boundary := internal.boundary,
    representation := internal.representation, counters := internal.counters, external }

/-- Stage equalities only. No generated C# completion function or lowering theorem exists yet. -/
structure AcceptedStages (i : Input) : Prop where
  preparation : P.mapResult (EvmTransactionPreparationExtractor.Generated.prepare i.preparation) =
    S.prepared (mapInput i)
  refund : FR.mapResult (FG.evaluate (derivedRefundInput i)) = S.refundResult (mapInput i)
  receipt : RR.mapFinalizationObservation (acceptedReceiptRun i) = S.receiptResult (mapInput i)

theorem accepted_stages_at_computed_inputs (predicates : S.ExternalPredicates) (i : Input)
    (domain : Domain predicates i) : AcceptedStages i :=
  { preparation := accepted_preparation_at_entry i domain.preparation,
    refund := accepted_refund_at_derived_input i domain.representation.refundInput,
    receipt := accepted_receipt_at_derived_input i domain.boundary domain.counters }

end OrdinaryEvmCompletionExtractor.Refinement.OrdinaryEvmCompletion
