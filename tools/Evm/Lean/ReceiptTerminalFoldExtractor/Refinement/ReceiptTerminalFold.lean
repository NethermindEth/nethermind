-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import ReceiptTerminalFoldExtractor.Generated.ReceiptTerminalFoldKernel
import ReceiptTerminalFoldExtractor.Specification.ReceiptTerminalFold
import Eip803x.Refinement.BlockReceiptGasAccounting

namespace ReceiptTerminalFoldExtractor.Refinement.ReceiptTerminalFold

namespace Generated
export ReceiptTerminalFoldExtractor.Generated.ReceiptTerminalFoldKernel
  (BytesOracle LogOracle ErrorOracle EvmExceptionOracle HashOracle AddressOracle PriceOracle Status
    TransactionResult Event Gas effectiveBlockGas statusCode TransactionInput isContractCreation BlockInput GasTotals
    Receipt State Trace FinalizationObservation previousTotals lastReceiptGas
    updateCumulativeGasTracking guardedBlockGasUsed guardedExecutionGasUsed guardedStateGasUsed
    buildReceipt buildFailedReceipt forwardingEvents markAsSuccess markAsFailed takeSnapshot
    restorePrefix normalPrefix FinalizeInput emptyBytes failureOutput failureError transactionResult
    finalizeTransaction)
end Generated

namespace Spec
export ReceiptTerminalFoldExtractor.Specification.ReceiptTerminalFold
  (BytesOracle LogOracle ErrorOracle EvmExceptionOracle HashOracle AddressOracle PriceOracle Status
    TransactionResult Event Gas effectiveBlockGas statusCode TransactionInput isContractCreation BlockInput GasTotals
    Receipt State Trace FinalizationObservation previousTotals lastReceiptGas
    updateCumulativeGasTracking guardedBlockGasUsed guardedExecutionGasUsed guardedStateGasUsed
    buildReceipt buildFailedReceipt receiptProjection forwardingEvents markAsSuccess markAsFailed takeSnapshot
    restorePrefix normalPrefix FinalizeInput emptyBytes failureOutput failureError transactionResult
    TerminalDecision appendReceipt terminalTrace applyTerminalDecision decideTerminal finalizeTransaction)
end Spec

def mapBytes (value : Generated.BytesOracle) : Spec.BytesOracle :=
  { id := value.id }

def mapLog (value : Generated.LogOracle) : Spec.LogOracle :=
  { id := value.id }

def mapError (value : Generated.ErrorOracle) : Spec.ErrorOracle :=
  { id := value.id }

def mapEvmException (value : Generated.EvmExceptionOracle) : Spec.EvmExceptionOracle :=
  { id := value.id }

def mapHash (value : Generated.HashOracle) : Spec.HashOracle :=
  { id := value.id }

def mapAddress (value : Generated.AddressOracle) : Spec.AddressOracle :=
  { id := value.id }

def mapPrice (value : Generated.PriceOracle) : Spec.PriceOracle :=
  { id := value.id }

def mapGas (value : Generated.Gas) : Spec.Gas :=
  { spentGas := value.spentGas
    operationGas := value.operationGas
    blockGas := value.blockGas
    blockStateGas := value.blockStateGas
    maxUsedGas := value.maxUsedGas
    gasRefund := value.gasRefund }

def mapTransaction (value : Generated.TransactionInput) : Spec.TransactionInput :=
  { txType := value.txType
    to := value.to.map mapAddress
    sender := value.sender.map mapAddress
    txHash := value.txHash.map mapHash
    effectiveGasPrice := mapPrice value.effectiveGasPrice }

theorem map_isContractCreation (value : Generated.TransactionInput) :
    Spec.isContractCreation (mapTransaction value) = Generated.isContractCreation value := by
  cases hTo : value.to with
  | none => simp [mapTransaction, Spec.isContractCreation, Generated.isContractCreation, hTo]
  | some address => simp [mapTransaction, Spec.isContractCreation, Generated.isContractCreation, hTo]

private theorem map_isContractCreation_expanded (value : Generated.TransactionInput) :
    Spec.isContractCreation
        { txType := value.txType
          to := value.to.map mapAddress
          sender := value.sender.map mapAddress
          txHash := value.txHash.map mapHash
          effectiveGasPrice := { id := value.effectiveGasPrice.id } } =
      Generated.isContractCreation value := by
  simpa [mapTransaction, mapPrice] using map_isContractCreation value

def mapBlock (value : Generated.BlockInput) : Spec.BlockInput :=
  { hash := value.hash.map mapHash
    number := value.number
    baseFeePerGas := value.baseFeePerGas }

def mapGasTotals (value : Generated.GasTotals) : Spec.GasTotals :=
  { executionGas := value.executionGas
    stateGas := value.stateGas }

def mapStatus : Generated.Status → Spec.Status
| .success => .success
| .failure => .failure

def mapTransactionResult : Generated.TransactionResult → Spec.TransactionResult
| .ok => .ok
| .evmException exceptionType substateError =>
    .evmException (mapEvmException exceptionType) (substateError.map mapError)

def mapReceipt (value : Generated.Receipt) : Spec.Receipt :=
  { logs := value.logs.map mapLog
    txType := value.txType
    gasUsedTotal := value.gasUsedTotal
    statusCode := value.statusCode
    recipient := value.recipient.map mapAddress
    blockHash := value.blockHash.map mapHash
    blockNumber := value.blockNumber
    index := value.index
    gasUsed := value.gasUsed
    effectiveGasPrice := mapPrice value.effectiveGasPrice
    sender := value.sender.map mapAddress
    contractAddress := value.contractAddress.map mapAddress
    txHash := value.txHash.map mapHash
    postTransactionState := value.postTransactionState.map mapHash
    blockGasUsed := value.blockGasUsed
    executionGasUsed := value.executionGasUsed
    storageGasUsed := value.storageGasUsed
    error := value.error.map mapError }

def mapState (value : Generated.State) : Spec.State :=
  { receipts := value.receipts.map mapReceipt
    gasHistory := value.gasHistory.map mapGasTotals
    cumulativeReceiptGas := value.cumulativeReceiptGas
    headerGasUsed := value.headerGasUsed
    parallel := value.parallel
    currentIndex := value.currentIndex }

def mapEvent : Generated.Event → Spec.Event
| .gasMutation => .gasMutation
| .receiptAppend => .receiptAppend
| .nestedTracerForward => .nestedTracerForward
| .currentTracerForward => .currentTracerForward

def mapTrace (value : Generated.Trace) : Spec.Trace :=
  { state := mapState value.state
    events := value.events.map mapEvent
    forwardedOutput := mapBytes value.forwardedOutput
    forwardedLogs := value.forwardedLogs.map mapLog
    forwardedError := value.forwardedError.map mapError
    forwardedStateRoot := value.forwardedStateRoot.map mapHash }

def mapFinalizeInput (value : Generated.FinalizeInput) : Spec.FinalizeInput :=
  { status := mapStatus value.status
    shouldRevert := value.shouldRevert
    output := mapBytes value.output
    substateError := value.substateError.map mapError
    vmError := mapError value.vmError
    evmExceptionType := value.evmExceptionType.map mapEvmException
    substateResultError := value.substateResultError.map mapError
    logs := value.logs.map mapLog
    stateRoot := value.stateRoot.map mapHash }

def mapFinalizationObservation (value : Generated.FinalizationObservation) :
    Spec.FinalizationObservation :=
  { trace := mapTrace value.trace
    result := mapTransactionResult value.result }

def FitsUInt64 (value : Nat) : Prop :=
  value ≤ Eip803x.Generated.TransactionGasInitializationKernel.uint64Max

def AccountingValid (state : Generated.State) (gas : Generated.Gas) : Prop :=
  let (previousExecutionGas, previousStateGas) := Generated.previousTotals state.gasHistory
  FitsUInt64 previousExecutionGas ∧
  FitsUInt64 previousStateGas ∧
  FitsUInt64 state.cumulativeReceiptGas ∧
  FitsUInt64 (Generated.effectiveBlockGas gas) ∧
  FitsUInt64 gas.blockStateGas ∧
  FitsUInt64 gas.spentGas ∧
  previousExecutionGas + Generated.effectiveBlockGas gas ≤
    Eip803x.Generated.TransactionGasInitializationKernel.uint64Max ∧
  previousStateGas + gas.blockStateGas ≤
    Eip803x.Generated.TransactionGasInitializationKernel.uint64Max ∧
  state.cumulativeReceiptGas + gas.spentGas ≤
    Eip803x.Generated.TransactionGasInitializationKernel.uint64Max

def RestoreValid (state : Generated.State) (snapshot : Nat) : Prop :=
  snapshot ≤ state.receipts.length ∧
  state.receipts.length = state.gasHistory.length ∧
  FitsUInt64 ((Generated.previousTotals (state.gasHistory.take snapshot)).1) ∧
  FitsUInt64 ((Generated.previousTotals (state.gasHistory.take snapshot)).2) ∧
  FitsUInt64 (Generated.lastReceiptGas (state.receipts.take snapshot))

theorem previousTotals_map (values : List Generated.GasTotals) :
    Spec.previousTotals (values.map mapGasTotals) = Generated.previousTotals values := by
  induction values with
  | nil => rfl
  | cons head tail ih =>
      cases tail with
      | nil => rfl
      | cons next rest =>
          simpa [Generated.previousTotals, Spec.previousTotals] using ih

theorem lastReceiptGas_map (values : List Generated.Receipt) :
    Spec.lastReceiptGas (values.map mapReceipt) = Generated.lastReceiptGas values := by
  induction values with
  | nil => rfl
  | cons head tail ih =>
      cases tail with
      | nil => rfl
      | cons next rest =>
          simpa [Generated.lastReceiptGas, Spec.lastReceiptGas] using ih

private theorem generatedAccumulate_fields
    (state : Generated.State)
    (gas : Generated.Gas)
    (h : AccountingValid state gas) :
    let (previousExecutionGas, previousStateGas) := Generated.previousTotals state.gasHistory
    let result :=
      Eip803x.Generated.BlockReceiptGasAccountingKernel.accumulate
        previousExecutionGas previousStateGas state.cumulativeReceiptGas
        (Generated.effectiveBlockGas gas) gas.blockStateGas gas.spentGas
    result.cumulativeExecutionGas = previousExecutionGas + Generated.effectiveBlockGas gas ∧
    result.cumulativeStateGas = previousStateGas + gas.blockStateGas ∧
    result.cumulativeReceiptGas = state.cumulativeReceiptGas + gas.spentGas ∧
    result.headerGasUsed = max
      (previousExecutionGas + Generated.effectiveBlockGas gas)
      (previousStateGas + gas.blockStateGas) := by
  dsimp [AccountingValid] at h
  generalize hTotals : Generated.previousTotals state.gasHistory = totals at h ⊢
  rcases totals with ⟨previousExecutionGas, previousStateGas⟩
  rcases h with ⟨hPreviousExecution, hPreviousState, hPreviousReceipt,
    hTransactionExecution, hTransactionState, hTransactionReceipt,
    hExecutionSum, hStateSum, hReceiptSum⟩
  have hAccounting := Eip803x.Refinement.BlockReceiptGasAccounting.generatedAccumulate_refines_spec
    { executionGas := previousExecutionGas
      stateGas := previousStateGas
      receiptGas := state.cumulativeReceiptGas }
    { executionGas := Generated.effectiveBlockGas gas
      stateGas := gas.blockStateGas
      paidGas := gas.spentGas }
    ⟨⟨hPreviousExecution, hPreviousState, hPreviousReceipt⟩,
      hTransactionExecution, hTransactionState, hTransactionReceipt,
      hExecutionSum, hStateSum, hReceiptSum⟩
  have hExecution := congrArg
    (fun result => result.totals.executionGas) hAccounting
  have hState := congrArg
    (fun result => result.totals.stateGas) hAccounting
  have hReceipt := congrArg
    (fun result => result.totals.receiptGas) hAccounting
  have hHeader := congrArg
    (fun result => result.headerGasUsed) hAccounting
  simpa [Eip803x.Refinement.BlockReceiptGasAccounting.toSpecResult,
    Eip803x.BlockReceiptGas.accumulate,
    Eip803x.BlockReceiptGas.fromTotals] using ⟨hExecution, hState, hReceipt, hHeader⟩

theorem generatedUpdate_refines_spec
    (state : Generated.State)
    (gas : Generated.Gas)
    (h : AccountingValid state gas) :
    mapState (Generated.updateCumulativeGasTracking state gas) =
      Spec.updateCumulativeGasTracking (mapState state) (mapGas gas) := by
  generalize hTotals : Generated.previousTotals state.gasHistory = totals at h ⊢
  rcases totals with ⟨previousExecutionGas, previousStateGas⟩
  have hFields := generatedAccumulate_fields state gas h
  simp [hTotals] at hFields
  have hEffective : Generated.effectiveBlockGas gas = Spec.effectiveBlockGas (mapGas gas) := by
    rfl
  simp only [Generated.updateCumulativeGasTracking, Spec.updateCumulativeGasTracking,
    mapState, List.map_append, previousTotals_map]
  rw [← hEffective]
  cases hParallel : state.parallel <;>
    simp [mapGas, mapGasTotals, hTotals, hFields]

private theorem mapState_append_receipt
    (state : Generated.State)
    (receipt : Generated.Receipt) :
    mapState { state with receipts := state.receipts ++ [receipt] } =
      { mapState state with receipts := (mapState state).receipts ++ [mapReceipt receipt] } := by
  simp [mapState, List.map_append]

private theorem generatedBuildReceipt_refines_spec
    (state : Generated.State)
    (block : Generated.BlockInput)
    (transaction : Generated.TransactionInput)
    (recipient : Generated.AddressOracle)
    (gas : Generated.Gas)
    (status : Generated.Status)
    (logs : List Generated.LogOracle)
    (stateRoot : Option Generated.HashOracle)
    (h : AccountingValid state gas) :
    mapState (Generated.buildReceipt state block transaction recipient gas status logs stateRoot).1 =
        (Spec.buildReceipt (mapState state) (mapBlock block) (mapTransaction transaction)
          (mapAddress recipient) (mapGas gas) (mapStatus status) (logs.map mapLog)
          (stateRoot.map mapHash)).1 ∧
      mapReceipt (Generated.buildReceipt state block transaction recipient gas status logs stateRoot).2 =
        (Spec.buildReceipt (mapState state) (mapBlock block) (mapTransaction transaction)
          (mapAddress recipient) (mapGas gas) (mapStatus status) (logs.map mapLog)
          (stateRoot.map mapHash)).2 := by
  have hUpdate := generatedUpdate_refines_spec state gas h
  have hCumulative := congrArg (fun value => value.cumulativeReceiptGas) hUpdate
  have hCumulative' :
      (Generated.updateCumulativeGasTracking state gas).cumulativeReceiptGas =
        (Spec.updateCumulativeGasTracking (mapState state) (mapGas gas)).cumulativeReceiptGas := by
    simpa [mapState] using hCumulative
  have hEffective : Generated.effectiveBlockGas gas = Spec.effectiveBlockGas (mapGas gas) := by
    rfl
  have hRecipient :
      Option.map mapAddress (if Generated.isContractCreation transaction then none else some recipient) =
        if Generated.isContractCreation transaction then none else some (mapAddress recipient) := by
    by_cases hCreation : Generated.isContractCreation transaction <;> simp [hCreation, mapAddress]
  have hContractAddress :
      (if Generated.isContractCreation transaction then some (mapAddress recipient) else none) =
        if Generated.isContractCreation transaction then some (mapAddress recipient) else none := by
    rfl
  constructor
  · simpa [Generated.buildReceipt, Spec.buildReceipt, Spec.receiptProjection] using hUpdate
  · cases status <;>
      by_cases hCreation : Generated.isContractCreation transaction <;>
        simp [Generated.buildReceipt, Spec.buildReceipt, Spec.receiptProjection,
          map_isContractCreation_expanded,
          mapReceipt, mapBlock,
           mapState, mapTransaction, mapAddress, mapGas, mapStatus, mapPrice,
           Spec.updateCumulativeGasTracking,
           Spec.guardedBlockGasUsed, Spec.guardedExecutionGasUsed,
           Spec.guardedStateGasUsed, Generated.statusCode, Spec.statusCode,
           hCumulative', hEffective, hCreation]

theorem generatedMarkAsSuccess_refines_spec
    (state : Generated.State)
    (block : Generated.BlockInput)
    (transaction : Generated.TransactionInput)
    (recipient : Generated.AddressOracle)
    (gas : Generated.Gas)
    (output : Generated.BytesOracle)
    (logs : List Generated.LogOracle)
    (stateRoot : Option Generated.HashOracle)
    (nestedTracer currentTxTracerIsTracingReceipt : Bool)
    (_hSequential : state.parallel = false)
    (h : AccountingValid state gas) :
    mapTrace
        (Generated.markAsSuccess state block transaction recipient gas output logs stateRoot nestedTracer currentTxTracerIsTracingReceipt) =
      Spec.markAsSuccess (mapState state) (mapBlock block) (mapTransaction transaction)
        (mapAddress recipient) (mapGas gas) (mapBytes output) (logs.map mapLog)
    (stateRoot.map mapHash) nestedTracer currentTxTracerIsTracingReceipt := by
  have hBuild := generatedBuildReceipt_refines_spec state block transaction recipient gas
    (.success) logs stateRoot h
  have hBuildState :
      mapState (Generated.buildReceipt state block transaction recipient gas .success logs stateRoot).1 =
        (Spec.buildReceipt (mapState state) (mapBlock block) (mapTransaction transaction)
          (mapAddress recipient) (mapGas gas) .success (logs.map mapLog) (stateRoot.map mapHash)).1 := by
    simpa [mapStatus] using hBuild.1
  have hBuildReceipt :
      mapReceipt (Generated.buildReceipt state block transaction recipient gas .success logs stateRoot).2 =
        (Spec.buildReceipt (mapState state) (mapBlock block) (mapTransaction transaction)
          (mapAddress recipient) (mapGas gas) .success (logs.map mapLog) (stateRoot.map mapHash)).2 := by
    simpa [mapStatus, Spec.receiptProjection] using hBuild.2
  have hAppend :
      mapState
          { (Generated.buildReceipt state block transaction recipient gas .success logs stateRoot).1 with
              receipts :=
                (Generated.buildReceipt state block transaction recipient gas .success logs stateRoot).1.receipts ++
                  [(Generated.buildReceipt state block transaction recipient gas .success logs stateRoot).2] } =
        { (Spec.buildReceipt (mapState state) (mapBlock block) (mapTransaction transaction)
              (mapAddress recipient) (mapGas gas) .success (logs.map mapLog) (stateRoot.map mapHash)).1 with
            receipts :=
              (Spec.buildReceipt (mapState state) (mapBlock block) (mapTransaction transaction)
                  (mapAddress recipient) (mapGas gas) .success (logs.map mapLog) (stateRoot.map mapHash)).1.receipts ++
                [(Spec.buildReceipt (mapState state) (mapBlock block) (mapTransaction transaction)
                    (mapAddress recipient) (mapGas gas) .success (logs.map mapLog) (stateRoot.map mapHash)).2] } := by
    rw [mapState_append_receipt, hBuildState, hBuildReceipt]
  have hEvents :
      (Generated.forwardingEvents nestedTracer currentTxTracerIsTracingReceipt).map mapEvent =
        Spec.forwardingEvents nestedTracer currentTxTracerIsTracingReceipt := by
    by_cases hNested : nestedTracer <;>
      by_cases hTracing : currentTxTracerIsTracingReceipt <;>
        simp [Generated.forwardingEvents, Spec.forwardingEvents, mapEvent, hNested, hTracing]
  simp only [Generated.markAsSuccess, Spec.markAsSuccess, Spec.applyTerminalDecision,
    Spec.terminalTrace, Spec.appendReceipt, mapTrace]
  rw [hAppend, hEvents]
  simp

theorem generatedMarkAsFailed_refines_spec
    (state : Generated.State)
    (block : Generated.BlockInput)
    (transaction : Generated.TransactionInput)
    (recipient : Generated.AddressOracle)
    (gas : Generated.Gas)
    (output : Generated.BytesOracle)
    (error : Option Generated.ErrorOracle)
    (stateRoot : Option Generated.HashOracle)
    (nestedTracer currentTxTracerIsTracingReceipt : Bool)
    (_hSequential : state.parallel = false)
    (h : AccountingValid state gas) :
    mapTrace
        (Generated.markAsFailed state block transaction recipient gas output error stateRoot nestedTracer currentTxTracerIsTracingReceipt) =
      Spec.markAsFailed (mapState state) (mapBlock block) (mapTransaction transaction)
        (mapAddress recipient) (mapGas gas) (mapBytes output) (error.map mapError)
    (stateRoot.map mapHash) nestedTracer currentTxTracerIsTracingReceipt := by
  have hBuild := generatedBuildReceipt_refines_spec state block transaction recipient gas
    (.failure) [] stateRoot h
  have hBuildState :
      mapState (Generated.buildFailedReceipt state block transaction recipient gas error stateRoot).1 =
        (Spec.buildFailedReceipt (mapState state) (mapBlock block) (mapTransaction transaction)
          (mapAddress recipient) (mapGas gas) (error.map mapError) (stateRoot.map mapHash)).1 := by
    simp only [Generated.buildFailedReceipt, Spec.buildFailedReceipt]
    simpa [mapStatus] using hBuild.1
  have hBaseReceipt :
      mapReceipt (Generated.buildReceipt state block transaction recipient gas .failure [] stateRoot).2 =
        (Spec.buildReceipt (mapState state) (mapBlock block) (mapTransaction transaction)
          (mapAddress recipient) (mapGas gas) .failure [] (stateRoot.map mapHash)).2 := by
    simpa [mapStatus, Spec.receiptProjection] using hBuild.2
  have hFailedReceipt :
      mapReceipt
          { (Generated.buildReceipt state block transaction recipient gas .failure [] stateRoot).2 with
              error := error } =
        { (Spec.buildReceipt (mapState state) (mapBlock block) (mapTransaction transaction)
              (mapAddress recipient) (mapGas gas) .failure [] (stateRoot.map mapHash)).2 with
            error := error.map mapError } := by
    calc
      mapReceipt
          { (Generated.buildReceipt state block transaction recipient gas .failure [] stateRoot).2 with
              error := error } =
          { mapReceipt (Generated.buildReceipt state block transaction recipient gas .failure [] stateRoot).2 with
              error := error.map mapError } := by
            cases error <;> rfl
      _ = { (Spec.buildReceipt (mapState state) (mapBlock block) (mapTransaction transaction)
                (mapAddress recipient) (mapGas gas) .failure [] (stateRoot.map mapHash)).2 with
              error := error.map mapError } := by
            rw [hBaseReceipt]
  have hBuildReceipt :
      mapReceipt (Generated.buildFailedReceipt state block transaction recipient gas error stateRoot).2 =
        (Spec.buildFailedReceipt (mapState state) (mapBlock block) (mapTransaction transaction)
          (mapAddress recipient) (mapGas gas) (error.map mapError) (stateRoot.map mapHash)).2 := by
    simp only [Generated.buildFailedReceipt, Spec.buildFailedReceipt]
    simpa only using hFailedReceipt
  have hAppend :
      mapState
          { (Generated.buildFailedReceipt state block transaction recipient gas error stateRoot).1 with
              receipts :=
                (Generated.buildFailedReceipt state block transaction recipient gas error stateRoot).1.receipts ++
                  [(Generated.buildFailedReceipt state block transaction recipient gas error stateRoot).2] } =
        { (Spec.buildFailedReceipt (mapState state) (mapBlock block) (mapTransaction transaction)
              (mapAddress recipient) (mapGas gas) (error.map mapError) (stateRoot.map mapHash)).1 with
            receipts :=
              (Spec.buildFailedReceipt (mapState state) (mapBlock block) (mapTransaction transaction)
                  (mapAddress recipient) (mapGas gas) (error.map mapError) (stateRoot.map mapHash)).1.receipts ++
                [(Spec.buildFailedReceipt (mapState state) (mapBlock block) (mapTransaction transaction)
                    (mapAddress recipient) (mapGas gas) (error.map mapError) (stateRoot.map mapHash)).2] } := by
    rw [mapState_append_receipt, hBuildState, hBuildReceipt]
  have hEvents :
      (Generated.forwardingEvents nestedTracer currentTxTracerIsTracingReceipt).map mapEvent =
        Spec.forwardingEvents nestedTracer currentTxTracerIsTracingReceipt := by
    by_cases hNested : nestedTracer <;>
      by_cases hTracing : currentTxTracerIsTracingReceipt <;>
        simp [Generated.forwardingEvents, Spec.forwardingEvents, mapEvent, hNested, hTracing]
  simp only [Generated.markAsFailed, Spec.markAsFailed, Spec.applyTerminalDecision,
    Spec.terminalTrace, Spec.appendReceipt, mapTrace]
  rw [hAppend, hEvents]
  simp

private theorem mapFailureOutput_commutes (input : Generated.FinalizeInput) :
    mapBytes (Generated.failureOutput input) =
      Spec.failureOutput (mapFinalizeInput input) := by
  cases hRevert : input.shouldRevert <;>
    simp [Generated.failureOutput, Spec.failureOutput, Generated.emptyBytes, Spec.emptyBytes,
      mapFinalizeInput, mapBytes, hRevert]

private theorem mapFailureError_commutes (input : Generated.FinalizeInput) :
    (Generated.failureError input).map mapError =
      Spec.failureError (mapFinalizeInput input) := by
  cases hSubstateError : input.substateError with
  | none =>
      cases hException : input.evmExceptionType with
      | none =>
          simp [Generated.failureError, Spec.failureError, mapFinalizeInput,
            hSubstateError, hException]
      | some exceptionType =>
          simp [Generated.failureError, Spec.failureError, mapFinalizeInput,
            hSubstateError, hException, mapError]
  | some error =>
      simp [Generated.failureError, Spec.failureError, mapFinalizeInput,
        hSubstateError, mapError]

private theorem mapTransactionResult_commutes (input : Generated.FinalizeInput) :
    mapTransactionResult (Generated.transactionResult input) =
      Spec.transactionResult (mapFinalizeInput input) := by
  cases hException : input.evmExceptionType with
  | none =>
      simp [Generated.transactionResult, Spec.transactionResult, mapFinalizeInput,
        mapTransactionResult, hException]
  | some exceptionType =>
      cases hError : input.substateResultError with
      | none =>
          simp [Generated.transactionResult, Spec.transactionResult, mapFinalizeInput,
            mapTransactionResult, hException, hError]
      | some error =>
          simp [Generated.transactionResult, Spec.transactionResult, mapFinalizeInput,
            mapTransactionResult, hException, hError, mapEvmException, mapError]

theorem transaction_isContractCreation_iff_to_isNone
    (transaction : Generated.TransactionInput) :
    Generated.isContractCreation transaction = true ↔ transaction.to = none := by
  cases hTo : transaction.to with
  | none => simp [Generated.isContractCreation, hTo]
  | some recipient => simp [Generated.isContractCreation, hTo]

-- This is the caller-to-model adapter obligation.  The extractor admits the
-- terminal fold for all typed inputs, while the normal FinalizeTransaction
-- path supplies this status/exception relation as a reachable-finalize witness.
structure ProductionFinalizeDomain
    (input : Generated.FinalizeInput) : Prop where
  statusException : input.status = .success → input.evmExceptionType = none

private theorem generatedFinalizeTransaction_refines_spec_universal
    (state : Generated.State)
    (block : Generated.BlockInput)
    (transaction : Generated.TransactionInput)
    (recipient : Generated.AddressOracle)
    (gas : Generated.Gas)
    (input : Generated.FinalizeInput)
    (nestedTracer currentTxTracerIsTracingReceipt : Bool)
    (hSequential : state.parallel = false)
    (h : AccountingValid state gas) :
    mapFinalizationObservation
        (Generated.finalizeTransaction state block transaction recipient gas input
          nestedTracer currentTxTracerIsTracingReceipt) =
      Spec.finalizeTransaction (mapState state) (mapBlock block) (mapTransaction transaction)
        (mapAddress recipient) (mapGas gas) (mapFinalizeInput input)
        nestedTracer currentTxTracerIsTracingReceipt := by
  have hResult := mapTransactionResult_commutes input
  have hOutput := mapFailureOutput_commutes input
  have hError := mapFailureError_commutes input
  cases hStatus : input.status with
  | success =>
      have hTrace := generatedMarkAsSuccess_refines_spec state block transaction recipient gas
        input.output input.logs input.stateRoot nestedTracer currentTxTracerIsTracingReceipt hSequential h
      simp only [Generated.finalizeTransaction, Spec.finalizeTransaction, Spec.decideTerminal,
        mapFinalizationObservation, mapFinalizeInput, hStatus, mapStatus, Generated.statusCode]
      have hGate : (1 == 0) = false := by decide
      simp only [hGate, Bool.false_eq_true, ↓reduceIte]
      rw [hTrace, hResult] <;>
        simp [Spec.markAsSuccess, Spec.transactionResult, mapFinalizeInput, mapStatus,
          mapBytes, mapError] <;>
        rfl
  | failure =>
      have hTrace := generatedMarkAsFailed_refines_spec state block transaction recipient gas
        (Generated.failureOutput input) (Generated.failureError input) input.stateRoot
        nestedTracer currentTxTracerIsTracingReceipt hSequential h
      simp only [Generated.finalizeTransaction, Spec.finalizeTransaction, Spec.decideTerminal,
        mapFinalizationObservation, mapFinalizeInput, hStatus, mapStatus, Generated.statusCode]
      have hGate : (0 == 0) = true := by decide
      simp only [hGate, ↓reduceIte]
      rw [hTrace, hOutput, hError, hResult] <;>
        simp [Spec.markAsFailed, Spec.applyTerminalDecision, Spec.terminalTrace,
          Spec.appendReceipt, Spec.failureOutput, Spec.failureError, Spec.transactionResult,
          mapFinalizeInput, mapStatus, mapBytes, mapError] <;>
        rfl

theorem generatedFinalizeTransaction_refines_spec
    (state : Generated.State)
    (block : Generated.BlockInput)
    (transaction : Generated.TransactionInput)
    (recipient : Generated.AddressOracle)
    (gas : Generated.Gas)
    (input : Generated.FinalizeInput)
    (nestedTracer currentTxTracerIsTracingReceipt : Bool)
    (hSequential : state.parallel = false)
    (hDomain : ProductionFinalizeDomain input)
    (h : AccountingValid state gas) :
    mapFinalizationObservation
        (Generated.finalizeTransaction state block transaction recipient gas input
          nestedTracer currentTxTracerIsTracingReceipt) =
      Spec.finalizeTransaction (mapState state) (mapBlock block) (mapTransaction transaction)
        (mapAddress recipient) (mapGas gas) (mapFinalizeInput input)
        nestedTracer currentTxTracerIsTracingReceipt := by
  have hUniversal := generatedFinalizeTransaction_refines_spec_universal
    state block transaction recipient gas input nestedTracer currentTxTracerIsTracingReceipt hSequential h
  cases hStatus : input.status with
  | success =>
      have hExceptionNone : input.evmExceptionType = none := hDomain.statusException hStatus
      simpa [hExceptionNone] using hUniversal
  | failure =>
      simpa [hStatus] using hUniversal

private theorem generatedFromTotals_fields
    (executionGas stateGas receiptGas : Nat)
    (hExecution : FitsUInt64 executionGas)
    (hState : FitsUInt64 stateGas)
    (hReceipt : FitsUInt64 receiptGas) :
    let result :=
      Eip803x.Generated.BlockReceiptGasAccountingKernel.fromTotals
        executionGas stateGas receiptGas
    result.cumulativeExecutionGas = executionGas ∧
    result.cumulativeStateGas = stateGas ∧
    result.cumulativeReceiptGas = receiptGas ∧
    result.headerGasUsed = max executionGas stateGas := by
  have hTotals := Eip803x.Refinement.BlockReceiptGasAccounting.generatedFromTotals_refines_spec
    { executionGas := executionGas, stateGas := stateGas, receiptGas := receiptGas }
    ⟨hExecution, hState, hReceipt⟩
  have hExecution' := congrArg
    (fun result => result.totals.executionGas) hTotals
  have hState' := congrArg
    (fun result => result.totals.stateGas) hTotals
  have hReceipt' := congrArg
    (fun result => result.totals.receiptGas) hTotals
  have hHeader' := congrArg
    (fun result => result.headerGasUsed) hTotals
  simpa [Eip803x.Refinement.BlockReceiptGasAccounting.toSpecResult,
    Eip803x.BlockReceiptGas.fromTotals] using ⟨hExecution', hState', hReceipt', hHeader'⟩

theorem generatedRestorePrefix_refines_spec
    (state : Generated.State)
    (snapshot : Nat)
    (hSequential : state.parallel = false)
    (hValid : RestoreValid state snapshot) :
    mapState (Generated.restorePrefix state snapshot) =
      Spec.restorePrefix (mapState state) snapshot := by
  rcases hValid with ⟨hSnapshot, hPrefix, hExecution, hStateGas, hReceipt⟩
  have hRetained : state.receipts.length - (state.receipts.length - snapshot) = snapshot := by
    omega
  have hFields := generatedFromTotals_fields
    ((Generated.previousTotals (state.gasHistory.take snapshot)).1)
    ((Generated.previousTotals (state.gasHistory.take snapshot)).2)
    (Generated.lastReceiptGas (state.receipts.take snapshot))
    hExecution hStateGas hReceipt
  have hLast :
      Spec.lastReceiptGas (List.take snapshot (state.receipts.map mapReceipt)) =
        Generated.lastReceiptGas (List.take snapshot state.receipts) := by
    rw [← List.map_take]
    exact lastReceiptGas_map (state.receipts.take snapshot)
  have hPrevious :
      Spec.previousTotals (List.take snapshot (state.gasHistory.map mapGasTotals)) =
        Generated.previousTotals (List.take snapshot state.gasHistory) := by
    rw [← List.map_take]
    exact previousTotals_map (state.gasHistory.take snapshot)
  simp only [Generated.restorePrefix, Spec.restorePrefix, mapState, List.map_take]
  rw [hRetained]
  rw [hFields.2.2.1, hFields.2.2.2, hLast, hPrevious]
  simp [hSequential]

end ReceiptTerminalFoldExtractor.Refinement.ReceiptTerminalFold
