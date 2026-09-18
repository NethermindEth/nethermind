-- SPDX-License-Identifier: LGPL-3.0-only

/-!
Fieldwise relation for the bounded ordinary source route and adapter-supplied settled fold. The
propositions in this file are deliberately adapter obligations: Roslyn supplies identities and control-flow facts,
while normal-return and settled-terminal observations supply effectful values. No C# execution is
claimed and no whole production result is compared with Lean equality.
-/

import OrdinaryTransactionMachineExtractor.Generated.OrdinaryTransactionMachine
import OrdinaryTransactionMachineExtractor.Specification.OrdinaryTransactionMachine

namespace OrdinaryTransactionMachineExtractor.Refinement

namespace G := OrdinaryTransactionMachineExtractor.Generated
namespace S := OrdinaryTransactionMachineExtractor.Specification
namespace T := ReceiptTerminalFoldExtractor.Generated.ReceiptTerminalFoldKernel

def terminalResultIsOk : T.TransactionResult → Prop
  | .ok => True
  | .evmException _ _ => False

def terminalStatusIsSuccess : T.Status → Prop
  | .success => True
  | .failure => False

def settledEntryInvariant (entry : G.SettledEntry) : Prop :=
  entry.wellFormed = true ∧ terminalResultIsOk entry.result ∧
  terminalStatusIsSuccess entry.status

/- The block adapter is explicit about resetting caller-provided tracer state. The seed is an
   input identity only; no receipt, gas, cumulative value, or index is inherited from it. -/
def FreshSequentialTracer (seed : G.TracerSeed) (headerGasUsed : Nat)
    (state : T.State) : Prop :=
  state.receipts = [] ∧
  state.gasHistory = [] ∧
  state.cumulativeReceiptGas = 0 ∧
  state.parallel = false ∧
  state.currentIndex = 0 ∧
  state.headerGasUsed = headerGasUsed

theorem fresh_sequential_tracer_is_explicit
    (seed : G.TracerSeed) (headerGasUsed : Nat) :
    FreshSequentialTracer seed headerGasUsed
      (G.freshSequentialTracer seed headerGasUsed) := by
  simp [FreshSequentialTracer, G.freshSequentialTracer]

theorem terminalResultIsOk_iff (result : T.TransactionResult) :
    terminalResultIsOk result ↔ result = .ok := by
  cases result <;> simp [terminalResultIsOk]

theorem terminalStatusIsSuccess_iff (status : T.Status) :
    terminalStatusIsSuccess status ↔ status = .success := by
  cases status <;> simp [terminalStatusIsSuccess]

def onlyOkTerminalDomain (input : G.MachineInput) (premises : G.NormalReturnPremises)
    (entries : List G.SettledEntry) : Prop :=
  G.routeValid input = true ∧ ∀ entry ∈ entries, settledEntryInvariant entry

def mapOptionNat {α : Type} (value : Option α) (map : α → Nat) : Option Nat :=
  value.map map

/- The generated oracle identifiers are intentionally projected, not interpreted as bytes,
hashes, addresses or CLR values. -/
def receiptFields (receipt : T.Receipt) : S.ReceiptFields :=
  { logs := receipt.logs.map (fun log => log.id)
    txType := receipt.txType
    gasUsedTotal := receipt.gasUsedTotal
    statusCode := receipt.statusCode
    recipient := mapOptionNat receipt.recipient (fun address => address.id)
    blockHash := mapOptionNat receipt.blockHash (fun hash => hash.id)
    blockNumber := receipt.blockNumber
    index := receipt.index
    gasUsed := receipt.gasUsed
    effectiveGasPrice := receipt.effectiveGasPrice.id
    sender := mapOptionNat receipt.sender (fun address => address.id)
    contractAddress := mapOptionNat receipt.contractAddress (fun address => address.id)
    txHash := mapOptionNat receipt.txHash (fun hash => hash.id)
    postTransactionState := mapOptionNat receipt.postTransactionState (fun hash => hash.id)
    blockGasUsed := receipt.blockGasUsed
    executionGasUsed := receipt.executionGasUsed
    storageGasUsed := receipt.storageGasUsed
    error := mapOptionNat receipt.error (fun error => error.id) }

def gasFields (gas : T.Gas) : S.GasFields :=
  { spentGas := gas.spentGas
    operationGas := gas.operationGas
    blockGas := gas.blockGas
    blockStateGas := gas.blockStateGas
    maxUsedGas := gas.maxUsedGas
    gasRefund := gas.gasRefund }

def totalsFields (totals : T.GasTotals) : S.GasTotals :=
  { executionGas := totals.executionGas
    stateGas := totals.stateGas }

def mapEvent : G.Event → Nat
  | .blockStart => 0
  | .preTransactionCommit => 1
  | .foldStart => 2
  | .startNewTxTrace => 3
  | .execute => 4
  | .transactionCommit => 5
  | .gasMutation => 6
  | .receiptAppend => 7
  | .nestedTracerForward => 8
  | .currentTracerForward => 9
  | .endTxTrace => 10
  | .currentIndexIncrement => 11
  | .foldReturn => 12
  | .transactionsExecuted => 13
  | .postTransactionCommit => 14

def mapState (state : T.State) (events : List G.Event) : S.State :=
  { receipts := state.receipts.map receiptFields
    gasHistory := state.gasHistory.map totalsFields
    cumulativeReceiptGas := state.cumulativeReceiptGas
    headerGasUsed := state.headerGasUsed
    currentIndex := state.currentIndex
    events := events.map mapEvent }

def observationFields (entry : G.SettledEntry) : S.EntryObservation :=
  { receipt := receiptFields entry.receipt
    gas := gasFields entry.gas
    gasTotals := totalsFields entry.gasTotals
    resultIsOk := G.resultIsOk entry.result
    statusIsSuccess := G.statusIsSuccess entry.status
    nestedTracer := entry.nestedTracer
    currentTxTracerIsTracingReceipt := entry.currentTxTracerIsTracingReceipt
    wellFormed := entry.wellFormed }

def specInput (input : G.MachineInput) : S.Input :=
  { optionsRaw := input.optionsRaw
    standardMainnet := input.block.standardMainnet
    chainId := input.block.chainId
    forkNumber := input.block.forkNumber
    isSystem := input.isSystem
    parallel := input.parallel
    balEnabled := input.balEnabled
    isCreate := input.isCreate
    isCodeOverridable := input.isCodeOverridable
    hasAuthorizationList := input.hasAuthorizationList
    forceSimpleTransferDisabled := input.forceSimpleTransferDisabled
    recipientLive := input.recipientLive
    recipientHasCode := input.recipientHasCode
    recipientHasDelegation := input.recipientHasDelegation
    headerGasUsed := input.block.headerGasUsed }

def specEntries (entries : List G.SettledEntry) : List S.EntryObservation :=
  entries.map observationFields

def eventFields (generated : List G.Event) (specification : List Nat) : Prop :=
  generated.map mapEvent = specification

/- Every receipt field used by the terminal kernel is named here. In particular, this does not
use `TransactionResult` equality or a record-level equality bridge. -/
def receiptFieldsExtensional (generated : T.Receipt) (specification : S.ReceiptFields) : Prop :=
  generated.logs.map (fun log => log.id) = specification.logs ∧
  generated.txType = specification.txType ∧
  generated.gasUsedTotal = specification.gasUsedTotal ∧
  generated.statusCode = specification.statusCode ∧
  mapOptionNat generated.recipient (fun address => address.id) = specification.recipient ∧
  mapOptionNat generated.blockHash (fun hash => hash.id) = specification.blockHash ∧
  generated.blockNumber = specification.blockNumber ∧
  generated.index = specification.index ∧
  generated.gasUsed = specification.gasUsed ∧
  generated.effectiveGasPrice.id = specification.effectiveGasPrice ∧
  mapOptionNat generated.sender (fun address => address.id) = specification.sender ∧
  mapOptionNat generated.contractAddress (fun address => address.id) = specification.contractAddress ∧
  mapOptionNat generated.txHash (fun hash => hash.id) = specification.txHash ∧
  mapOptionNat generated.postTransactionState (fun hash => hash.id) = specification.postTransactionState ∧
  generated.blockGasUsed = specification.blockGasUsed ∧
  generated.executionGasUsed = specification.executionGasUsed ∧
  generated.storageGasUsed = specification.storageGasUsed ∧
  mapOptionNat generated.error (fun error => error.id) = specification.error

def gasFieldsExtensional (generated : T.Gas) (specification : S.GasFields) : Prop :=
  generated.spentGas = specification.spentGas ∧
  generated.operationGas = specification.operationGas ∧
  generated.blockGas = specification.blockGas ∧
  generated.blockStateGas = specification.blockStateGas ∧
  generated.maxUsedGas = specification.maxUsedGas ∧
  generated.gasRefund = specification.gasRefund

def resultFieldsExtensional (generated : T.TransactionResult)
    (specification : Bool) : Prop :=
  match generated, specification with
  | .ok, true => True
  | .evmException _ _, false => False
  | _, _ => False

def observationFieldsExtensional (entry : G.SettledEntry)
    (specification : S.EntryObservation) : Prop :=
  receiptFieldsExtensional entry.receipt specification.receipt ∧
  gasFieldsExtensional entry.gas specification.gas ∧
  resultFieldsExtensional entry.result specification.resultIsOk ∧
  G.resultIsOk entry.result = specification.resultIsOk ∧
  G.statusIsSuccess entry.status = specification.statusIsSuccess ∧
  entry.nestedTracer = specification.nestedTracer ∧
  entry.currentTxTracerIsTracingReceipt = specification.currentTxTracerIsTracingReceipt ∧
  entry.wellFormed = specification.wellFormed

def observationListFields : List G.SettledEntry → List S.EntryObservation → Prop
  | [], [] => True
  | entry :: entryTail, specification :: specificationTail =>
      observationFieldsExtensional entry specification ∧
      observationListFields entryTail specificationTail
  | _, _ => False

def receiptListFields : List T.Receipt → List S.ReceiptFields → Prop
  | [], [] => True
  | generated :: generatedTail, specification :: specificationTail =>
      receiptFieldsExtensional generated specification ∧
      receiptListFields generatedTail specificationTail
  | _, _ => False

def totalsListFields : List T.GasTotals → List S.GasTotals → Prop
  | [], [] => True
  | generated :: generatedTail, specification :: specificationTail =>
      generated.executionGas = specification.executionGas ∧
      generated.stateGas = specification.stateGas ∧
      totalsListFields generatedTail specificationTail
  | _, _ => False

def stateFields (generated : T.State) (specification : S.State) : Prop :=
  generated.receipts.length = specification.receipts.length ∧
  receiptListFields generated.receipts specification.receipts ∧
  generated.gasHistory.length = specification.gasHistory.length ∧
  totalsListFields generated.gasHistory specification.gasHistory ∧
  generated.cumulativeReceiptGas = specification.cumulativeReceiptGas ∧
  generated.headerGasUsed = specification.headerGasUsed ∧
  generated.currentIndex = specification.currentIndex

def sequentialEntriesValid : S.State → List S.EntryObservation → Prop
  | _, [] => True
  | state, entry :: tail =>
      S.entryValid state entry = true ∧ sequentialEntriesValid (S.step state entry) tail

def generatedStepState (block : G.Block) (state : T.State)
    (entry : G.SettledEntry) : T.State :=
  let terminal :=
    T.markAsSuccess state block.terminal entry.transaction entry.recipient entry.gas entry.output
      entry.logs entry.stateRoot entry.nestedTracer entry.currentTxTracerIsTracingReceipt
  { terminal.state with currentIndex := state.currentIndex + 1 }

def generatedStepEvents (events : List G.Event) (state : T.State)
    (block : G.Block) (entry : G.SettledEntry) : List G.Event :=
  let terminal :=
    T.markAsSuccess state block.terminal entry.transaction entry.recipient entry.gas entry.output
      entry.logs entry.stateRoot entry.nestedTracer entry.currentTxTracerIsTracingReceipt
    events ++ [G.Event.startNewTxTrace, G.Event.execute, G.Event.transactionCommit] ++
    terminal.events.map G.mapTerminalEvent ++ [G.Event.endTxTrace, G.Event.currentIndexIncrement]

/- A terminal observation is a source-adapter invariant for one already-settled successful
   entry. It records the append-only receipt/gas boundary and exact conditional forwarding events;
   it is not inferred from the generated fold result. -/
def ReceiptTerminalObservation (block : G.Block) (start : T.State)
    (entry : G.SettledEntry) : Prop :=
  let terminal :=
    T.markAsSuccess start block.terminal entry.transaction entry.recipient entry.gas entry.output
      entry.logs entry.stateRoot entry.nestedTracer entry.currentTxTracerIsTracingReceipt
  start.parallel = false ∧
  entry.result = .ok ∧
  terminal.state.currentIndex = start.currentIndex ∧
  terminal.state.receipts.take start.receipts.length = start.receipts ∧
  terminal.state.gasHistory.take start.gasHistory.length = start.gasHistory ∧
  terminal.state.receipts.length = start.receipts.length + 1 ∧
  terminal.state.gasHistory.length = start.gasHistory.length + 1 ∧
  terminal.state.gasHistory.length = terminal.state.receipts.length ∧
  (match G.lastReceipt terminal.state.receipts with
  | some actual => G.receiptObservationMatches actual entry.receipt = true
  | none => False) ∧
  (match G.lastGasTotals terminal.state.gasHistory with
  | some actual => G.gasTotalsObservationMatches actual entry.gasTotals = true
  | none => False) ∧
  terminal.events.map G.mapTerminalEvent = G.terminalForwardingEvents entry

def ReceiptTerminalChain (block : G.Block) : T.State → List G.SettledEntry → Prop
  | _, [] => True
  | state, entry :: tail =>
      ReceiptTerminalObservation block state entry ∧
      ReceiptTerminalChain block (generatedStepState block state entry) tail

/- A local bridge for one terminal step. It exposes the receipt/gas observations, the source
   terminal event order, and the resulting state fields; it does not quantify over a whole run. -/
def localStepBridge (block : G.Block) (generated : T.State) (specification : S.State)
    (entry : G.SettledEntry) : Prop :=
  let terminal :=
    T.markAsSuccess generated block.terminal entry.transaction entry.recipient entry.gas entry.output
      entry.logs entry.stateRoot entry.nestedTracer entry.currentTxTracerIsTracingReceipt
  stateFields (generatedStepState block generated entry)
    (S.step specification (observationFields entry)) ∧
  G.sequentialIndexInvariant (generatedStepState block generated entry) ∧
  S.sequentialIndexInvariant (S.step specification (observationFields entry)) ∧
  terminal.events.map G.mapTerminalEvent = G.terminalForwardingEvents entry

/- Typed source-return premises carry proofs about the corresponding generated observations.
   Roslyn binds the call sites but does not execute their callees; a caller of the refinement
   theorem must construct these proofs from its adapter observations. -/
def NormalReturnObserved (flag : Bool) : Prop := flag = true

structure EntrySourceNormality (normality : G.EntryNormality)
    (entry : G.SettledEntry) : Prop where
  startNewTxTraceNormalReturn : NormalReturnObserved normality.startNewTxTrace
  executeNormalReturn : NormalReturnObserved normality.execute
  endTxTraceNormalReturn : NormalReturnObserved normality.endTxTrace
  settledReturnObservation : settledEntryInvariant entry

def transactionProcessorNormalReturnFlags (premises : G.NormalReturnPremises) : Prop :=
  premises.setBlockExecutionContext = true ∧
  premises.process = true ∧
  premises.executeCore = true ∧
  premises.ordinaryExecute = true ∧
  premises.staticAdmission = true ∧
  premises.statefulAdmission = true ∧
  premises.nonceAndPreparation = true ∧
  premises.preExecutionCommit = true ∧
  premises.availableGas = true ∧
  premises.executeSimpleTransfer = true ∧
  premises.settlement = true ∧
  premises.transactionCommit = true ∧
  premises.finalizeTransaction = true ∧
  premises.receiptTerminal = true

def EntrySourceNormalityChain : List G.EntryNormality → List G.SettledEntry → Prop
  | [], [] => True
  | normality :: normalityTail, entry :: entryTail =>
      EntrySourceNormality normality entry ∧
      EntrySourceNormalityChain normalityTail entryTail
  | _, _ => False

structure SourceAdapterPremises (premises : G.NormalReturnPremises)
    (entries : List G.SettledEntry) : Prop where
  transactionProcessorNormalReturn : transactionProcessorNormalReturnFlags premises
  perEntry : EntrySourceNormalityChain premises.entries entries
  executorNormalReturn : NormalReturnObserved premises.executorReturn
  transactionsExecutedSubscriberNormalReturn :
    NormalReturnObserved premises.transactionsExecutedSubscriber
  postTransactionCommitStateNormalReturn :
    NormalReturnObserved premises.postTransactionCommitState

private theorem EntrySourceNormality.valid
    (source : EntrySourceNormality normality entry) :
    G.EntryNormality.valid normality = true := by
  rcases source with ⟨hStart, hExecute, hEnd, _⟩
  simp [NormalReturnObserved] at hStart hExecute hEnd
  simp [G.EntryNormality.valid, hStart, hExecute, hEnd]

private theorem EntrySourceNormalityChain.valid
    (source : EntrySourceNormalityChain normalities entries) :
    normalities.length = entries.length ∧
    (normalities.map G.EntryNormality.valid).all (fun value => value) = true := by
  induction normalities generalizing entries with
  | nil =>
      cases entries with
      | nil => simp [EntrySourceNormalityChain]
      | cons entry tail => simp [EntrySourceNormalityChain] at source
  | cons normality normalityTail ih =>
      cases entries with
      | nil => simp [EntrySourceNormalityChain] at source
      | cons entry entryTail =>
          have hHead : G.EntryNormality.valid normality = true := source.1.valid
          have hTail := ih entryTail source.2
          constructor
          · simp [hTail.1]
          · simp [hHead, hTail.2]

private theorem EntrySourceNormalityChain.settled
    (source : EntrySourceNormalityChain normalities entries) :
    ∀ entry ∈ entries, settledEntryInvariant entry := by
  induction normalities generalizing entries with
  | nil =>
      cases entries with
      | nil => simp
      | cons entry tail => simp [EntrySourceNormalityChain] at source
  | cons normality normalityTail ih =>
      cases entries with
      | nil => simp [EntrySourceNormalityChain] at source
      | cons entry entryTail =>
          intro candidate hMembership
          rcases source with ⟨hHead, hTail⟩
          simp only [List.mem_cons] at hMembership
          rcases hMembership with rfl | hTailMembership
          · exact hHead.settledReturnObservation
          · exact ih entryTail hTail candidate hTailMembership

theorem SourceAdapterPremises.validFor
    (adapter : SourceAdapterPremises premises entries) :
    premises.validFor entries = true := by
  rcases adapter.transactionProcessorNormalReturn with ⟨hSetContext, hProcess, hExecuteCore,
    hOrdinaryExecute, hStatic, hStateful, hNonce, hPreCommit, hAvailableGas, hSimple,
    hSettlement, hTransactionCommit, hFinalize, hReceiptTerminal⟩
  rcases adapter.perEntry.valid with ⟨hEntryLength, hEntryFlags⟩
  have hExecutor := adapter.executorNormalReturn
  have hSubscriber := adapter.transactionsExecutedSubscriberNormalReturn
  have hPostCommit := adapter.postTransactionCommitStateNormalReturn
  simp [NormalReturnObserved] at hExecutor hSubscriber hPostCommit
  simp [G.NormalReturnPremises.validFor, G.NormalReturnPremises.all,
    hSetContext, hProcess, hExecuteCore, hOrdinaryExecute, hStatic, hStateful,
    hNonce, hPreCommit, hAvailableGas, hSimple, hSettlement, hTransactionCommit,
    hFinalize, hExecutor, hSubscriber, hPostCommit,
    hReceiptTerminal, hEntryLength, hEntryFlags]

def resultListFields : List T.TransactionResult → List G.SettledEntry → Prop
  | [], [] => True
  | .ok :: generatedTail, entry :: entryTail =>
      (match entry.result with | .ok => True | .evmException _ _ => False) ∧
      resultListFields generatedTail entryTail
  | .evmException _ _ :: _, _ => False
  | _, _ => False

def generatedSpecRelation (input : G.MachineInput) (premises : G.NormalReturnPremises)
    (entries : List G.SettledEntry) (generated : G.Outcome) (specification : S.Outcome) : Prop :=
  onlyOkTerminalDomain input premises entries ∧
  SourceAdapterPremises premises entries ∧
  ReceiptTerminalChain input.block (G.startNewBlockTrace input) entries ∧
  FreshSequentialTracer input.tracerSeed input.block.headerGasUsed
    (G.startNewBlockTrace input) ∧
  observationListFields entries (specEntries entries) ∧
  match generated, specification with
  | .completed generatedState generatedEvents generatedResults,
      .completed specificationState =>
      generatedState.receipts.length = specificationState.receipts.length ∧
      receiptListFields generatedState.receipts specificationState.receipts ∧
      generatedState.gasHistory.length = specificationState.gasHistory.length ∧
      totalsListFields generatedState.gasHistory specificationState.gasHistory ∧
      generatedState.cumulativeReceiptGas = specificationState.cumulativeReceiptGas ∧
      generatedState.headerGasUsed = specificationState.headerGasUsed ∧
      generatedState.currentIndex = specificationState.currentIndex ∧
      G.sequentialIndexInvariant generatedState ∧
      S.sequentialIndexInvariant specificationState ∧
      eventFields generatedEvents specificationState.events ∧
      generatedResults.length = entries.length ∧
      resultListFields generatedResults entries
  | .rejected reason generatedState generatedEvents processed,
      .rejected _ specificationState =>
      generatedState.receipts.length = specificationState.receipts.length ∧
      receiptListFields generatedState.receipts specificationState.receipts ∧
      generatedState.gasHistory.length = specificationState.gasHistory.length ∧
      totalsListFields generatedState.gasHistory specificationState.gasHistory ∧
      generatedState.cumulativeReceiptGas = specificationState.cumulativeReceiptGas ∧
      generatedState.headerGasUsed = specificationState.headerGasUsed ∧
      generatedState.currentIndex = specificationState.currentIndex ∧
      generatedEvents.map mapEvent = specificationState.events ∧
      processed.length ≤ entries.length ∧
      match reason with
      | .malformedSettled => True
      | .invalidResult | .exceptionResult => True
      | .route | .normalReturn => False
  | _, _ => False

/- This is the source-route part of the exact theorem domain. `SourceAdapterPremises` separately
   relates the proof-carrying normal-return and settled-entry observations positionally to the
   supplied list. Neither is a relation to the source block's transaction list. -/
def adapterSuppliedFoldDomain (input : G.MachineInput) : Prop :=
  G.routeValid input = true

private theorem resultListFields_append_ok
    (generated : List T.TransactionResult) (entries : List G.SettledEntry)
    (entry : G.SettledEntry)
    (hFields : resultListFields generated entries)
    (hResult : entry.result = .ok) :
    resultListFields (generated ++ [.ok]) (entries ++ [entry]) := by
  induction generated generalizing entries with
  | nil =>
      cases entries with
      | nil => simp [resultListFields, hResult]
      | cons head tail => simp [resultListFields] at hFields
  | cons result generatedTail ih =>
      cases entries with
      | nil => simp [resultListFields] at hFields
      | cons sourceEntry sourceTail =>
          cases result with
          | ok =>
              simp only [List.cons_append, resultListFields] at hFields ⊢
              exact ⟨hFields.1, ih sourceTail hFields.2⟩
          | evmException exception substateError =>
              simp [resultListFields] at hFields

private theorem resultListFields_length
    (generated : List T.TransactionResult) (entries : List G.SettledEntry)
    (hFields : resultListFields generated entries) :
    generated.length = entries.length := by
  induction generated generalizing entries with
  | nil =>
      cases entries with
      | nil => rfl
      | cons head tail => simp [resultListFields] at hFields
  | cons result generatedTail ih =>
      cases entries with
      | nil => simp [resultListFields] at hFields
      | cons sourceEntry sourceTail =>
          cases result with
          | ok =>
              have hTail : resultListFields generatedTail sourceTail := by
                simpa [resultListFields] using hFields.2
              simpa only [List.length_cons] using
                congrArg Nat.succ (ih sourceTail hTail)
          | evmException exception substateError =>
              simp [resultListFields] at hFields

/- The generated and independent folds are related by one-step fieldwise bridges. The induction
   proves the complete adapter-supplied multi-entry result; no source transaction-list or
   whole-run output relation is an input premise. -/
private theorem foldEntries_completed_fields
    (block : G.Block) (generated : T.State) (specification : S.State)
    (events : List G.Event) (results : List T.TransactionResult)
    (processed : List G.SettledEntry) (entries : List G.SettledEntry)
    (hState : stateFields generated specification)
    (hEvents : eventFields events specification.events)
    (hResults : resultListFields results processed)
    (hValid : sequentialEntriesValid specification (specEntries entries))
    (hSettled : ∀ entry ∈ entries, settledEntryInvariant entry)
    (hChain : ReceiptTerminalChain block generated entries)
    (hGeneratedIndex : G.sequentialIndexInvariant generated)
    (hSpecificationIndex : S.sequentialIndexInvariant specification)
    (hStep : ∀ (g : T.State) (s : S.State) (entry : G.SettledEntry),
      stateFields g s →
      S.entryValid s (observationFields entry) = true →
      localStepBridge block g s entry) :
    ∃ finalGenerated finalEvents finalResults finalSpecification,
      G.foldEntries block generated events results entries =
        .completed finalGenerated finalEvents finalResults ∧
      S.fold specification (specEntries entries) = .completed finalSpecification ∧
      stateFields finalGenerated finalSpecification ∧
      eventFields finalEvents finalSpecification.events ∧
      G.sequentialIndexInvariant finalGenerated ∧
      S.sequentialIndexInvariant finalSpecification ∧
      resultListFields finalResults (processed ++ entries) := by
  induction entries generalizing generated specification events results processed with
  | nil =>
      refine ⟨generated, events, results, specification, ?_, ?_, hState, hEvents,
        hGeneratedIndex, hSpecificationIndex, ?_⟩
      · simp [G.foldEntries]
      · simp [S.fold]
      · simpa using hResults
  | cons entry tail ih =>
      have hEntrySettled : settledEntryInvariant entry := hSettled entry (by simp)
      have hTailSettled : ∀ tailEntry ∈ tail, settledEntryInvariant tailEntry := by
        intro tailEntry hMembership
        exact hSettled tailEntry (by simp [hMembership])
      have hEntryWellFormed : entry.wellFormed = true := hEntrySettled.1
      have hEntryResult : entry.result = .ok :=
        (terminalResultIsOk_iff entry.result).mp hEntrySettled.2.1
      have hEntryStatus : entry.status = .success :=
        (terminalStatusIsSuccess_iff entry.status).mp hEntrySettled.2.2
      have hEntryResultOk : G.resultIsOk entry.result = true := by
        simp [hEntryResult]
      have hEntryStatusSuccess : G.statusIsSuccess entry.status = true := by
        simp [hEntryStatus]
      have hEntryValid : S.entryValid specification (observationFields entry) = true :=
        hValid.1
      have hTailValid :
          sequentialEntriesValid (S.step specification (observationFields entry))
            (specEntries tail) := hValid.2
      have hChainEntry : ReceiptTerminalObservation block generated entry := hChain.1
      have hChainTail :
          ReceiptTerminalChain block (generatedStepState block generated entry) tail := hChain.2
      let terminal :=
        T.markAsSuccess generated block.terminal entry.transaction entry.recipient entry.gas
          entry.output entry.logs entry.stateRoot entry.nestedTracer
            entry.currentTxTracerIsTracingReceipt
      have hChainData :
          generated.parallel = false ∧
          entry.result = .ok ∧
          terminal.state.currentIndex = generated.currentIndex ∧
          terminal.state.receipts.take generated.receipts.length = generated.receipts ∧
          terminal.state.gasHistory.take generated.gasHistory.length = generated.gasHistory ∧
          terminal.state.receipts.length = generated.receipts.length + 1 ∧
          terminal.state.gasHistory.length = generated.gasHistory.length + 1 ∧
          terminal.state.gasHistory.length = terminal.state.receipts.length ∧
          (match G.lastReceipt terminal.state.receipts with
          | some actual => G.receiptObservationMatches actual entry.receipt = true
          | none => False) ∧
          (match G.lastGasTotals terminal.state.gasHistory with
          | some actual => G.gasTotalsObservationMatches actual entry.gasTotals = true
          | none => False) ∧
          terminal.events.map G.mapTerminalEvent =
            G.terminalForwardingEvents entry := by
        simpa [ReceiptTerminalObservation, terminal] using hChainEntry
      rcases hChainData with ⟨hParallel, hChainResult, hCurrentIndex, hReceiptPrefix,
        hGasPrefix, hReceiptLength, hGasLength, hHistoryLength, hReceiptMatch,
        hGasMatch, hChainEvents⟩
      have hLocal := hStep generated specification entry hState hEntryValid
      have hNextState :
          stateFields (generatedStepState block generated entry)
            (S.step specification (observationFields entry)) := hLocal.1
      have hNextGeneratedIndex :
          G.sequentialIndexInvariant (generatedStepState block generated entry) := hLocal.2.1
      have hNextSpecificationIndex :
          S.sequentialIndexInvariant (S.step specification (observationFields entry)) := hLocal.2.2.1
      have hLocalEvents :
          terminal.events.map G.mapTerminalEvent =
            G.terminalForwardingEvents entry := by
        simpa [localStepBridge, terminal] using hLocal.2.2.2
      cases hReceiptLast : G.lastReceipt terminal.state.receipts with
      | none =>
          simp [hReceiptLast] at hReceiptMatch
      | some actualReceipt =>
          have hReceiptObservation :
              G.receiptObservationMatches actualReceipt entry.receipt = true := by
            simpa [hReceiptLast] using hReceiptMatch
          cases hGasLast : G.lastGasTotals terminal.state.gasHistory with
          | none =>
              simp [hGasLast] at hGasMatch
          | some actualTotals =>
              have hGasObservation :
                  G.gasTotalsObservationMatches actualTotals entry.gasTotals = true := by
                simpa [hGasLast] using hGasMatch
              have hGeneratedStep :
                  G.foldEntries block generated events results (entry :: tail) =
                    G.foldEntries block (generatedStepState block generated entry)
                      (generatedStepEvents events generated block entry)
                      (results ++ [entry.result]) tail := by
                simp [G.foldEntries, hEntryWellFormed, hEntryResultOk,
                  hEntryStatusSuccess, terminal, hReceiptLast, hGasLast,
                  hReceiptObservation, hGasObservation, generatedStepState,
                  generatedStepEvents]
              have hSpecStep :
                  S.fold specification (specEntries (entry :: tail)) =
                    S.fold (S.step specification (observationFields entry))
                      (specEntries tail) := by
                simp [specEntries, S.fold, observationFields, hEntryWellFormed,
                  hEntryResultOk, hEntryValid]
              have hEventsNext :
                  eventFields (generatedStepEvents events generated block entry)
                    (S.step specification (observationFields entry)).events := by
                simp [eventFields, generatedStepEvents, S.step, mapEvent,
                  G.mapTerminalEvent, G.terminalForwardingEvents,
                  T.forwardingEvents, S.terminalForwardingEvents,
                  List.map_append, List.append_assoc,
                  hEvents, hLocalEvents, terminal]
              have hResultsNext :
                  resultListFields (results ++ [entry.result]) (processed ++ [entry]) := by
                simpa [hEntryResult] using
                  resultListFields_append_ok results processed entry hResults hEntryResult
              have hTail := ih
                (generated := generatedStepState block generated entry)
                (specification := S.step specification (observationFields entry))
                (events := generatedStepEvents events generated block entry)
                (results := results ++ [entry.result])
                (processed := processed ++ [entry])
                hNextState hEventsNext hResultsNext hTailValid hTailSettled hChainTail
                hNextGeneratedIndex hNextSpecificationIndex hStep
              rcases hTail with ⟨finalGenerated, finalEvents, finalResults,
                finalSpecification, hGenerated, hSpecification, hFinalState,
                hFinalEvents, hFinalGeneratedIndex, hFinalSpecificationIndex,
                hFinalResults⟩
              refine ⟨finalGenerated, finalEvents, finalResults, finalSpecification, ?_, ?_,
                hFinalState, hFinalEvents, hFinalGeneratedIndex,
                hFinalSpecificationIndex, hFinalResults⟩
              · calc
                  G.foldEntries block generated events results (entry :: tail) =
                      G.foldEntries block (generatedStepState block generated entry)
                        (generatedStepEvents events generated block entry)
                        (results ++ [entry.result]) tail := hGeneratedStep
                  _ = .completed finalGenerated finalEvents finalResults := hGenerated
              · calc
                  S.fold specification (specEntries (entry :: tail)) =
                      S.fold (S.step specification (observationFields entry))
                        (specEntries tail) := hSpecStep
                  _ = .completed finalSpecification := hSpecification

/- Rejection is a separate, prefix-preserving model path. It deliberately does not state the
   production false-result EndTxTrace poststate; that path is outside the OnlyOkTerminal adapter. -/
theorem malformed_settled_entry_rejects_without_prefix_mutation
    (block : G.Block) (state : T.State) (events : List G.Event)
    (processed : List T.TransactionResult) (entry : G.SettledEntry)
    (tail : List G.SettledEntry) (hMalformed : entry.wellFormed = false) :
    G.foldEntries block state events processed (entry :: tail) =
      .rejected .malformedSettled state events processed := by
  simp [G.foldEntries, hMalformed]

theorem non_ok_result_rejects_without_prefix_mutation
    (block : G.Block) (state : T.State) (events : List G.Event)
    (processed : List T.TransactionResult) (entry : G.SettledEntry)
    (tail : List G.SettledEntry) (hWellFormed : entry.wellFormed = true)
    (hNonOk : G.resultIsOk entry.result = false) :
    ∃ reason,
      G.foldEntries block state events processed (entry :: tail) =
        .rejected reason state events processed := by
  cases hResult : entry.result with
  | ok =>
      simp [G.resultIsOk] at hNonOk
  | evmException exception substateError =>
      exact ⟨.exceptionResult, by simp [G.foldEntries, hWellFormed, G.resultIsOk]⟩

/- This theorem is conditional on an adapter-supplied settled list and local fieldwise bridges.
Per-entry Start/Execute/End,
executor, subscriber, post-fold commit, terminal-chain, and one-step receipt/gas bridges are
explicit premises; no source transaction-list composition or whole-run production equality is assumed. -/
theorem generatedRun_refines_independentSpec
    (input : G.MachineInput) (premises : G.NormalReturnPremises)
    (entries : List G.SettledEntry)
    (hDomain : adapterSuppliedFoldDomain input)
    (hAdapter : SourceAdapterPremises premises entries)
    (hEntryFields : observationListFields entries (specEntries entries))
    (hFresh : FreshSequentialTracer input.tracerSeed input.block.headerGasUsed
      (G.startNewBlockTrace input))
    (hInitial : stateFields (G.startNewBlockTrace input)
      (S.freshState (specInput input)))
    (hSequential : sequentialEntriesValid (S.freshState (specInput input))
      (specEntries entries))
    (hChain : ReceiptTerminalChain input.block (G.startNewBlockTrace input) entries)
    (hStep : ∀ (generated : T.State) (specification : S.State) (entry : G.SettledEntry),
      stateFields generated specification →
      S.entryValid specification (observationFields entry) = true →
      localStepBridge input.block generated specification entry) :
    generatedSpecRelation input premises entries
      (G.run input premises entries)
      (S.run (specInput input) (specEntries entries)) := by
  have hAdapterSettled : ∀ entry ∈ entries, settledEntryInvariant entry :=
    hAdapter.perEntry.settled
  have hOnlyOk : onlyOkTerminalDomain input premises entries :=
    ⟨hDomain, hAdapterSettled⟩
  have hRoute : G.routeValid input = true := hOnlyOk.1
  have hSpecRoute : S.routeValid (specInput input) = true := by
    simpa [G.routeValid, S.routeValid, specInput] using hRoute
  have hNormalReturnGate : premises.validFor entries = true :=
    SourceAdapterPremises.validFor hAdapter
  have hInitialEvents :
      eventFields [G.Event.blockStart, G.Event.preTransactionCommit, G.Event.foldStart]
        (S.freshState (specInput input)).events := by
    simp [eventFields, S.freshState, mapEvent]
  have hInitialResults : resultListFields ([] : List T.TransactionResult) [] := by
    simp [resultListFields]
  have hInitialGeneratedIndex :
      G.sequentialIndexInvariant (G.startNewBlockTrace input) := by
    simp [G.sequentialIndexInvariant, G.receiptIndices, G.startNewBlockTrace,
      G.freshSequentialTracer]
  have hInitialSpecificationIndex :
      S.sequentialIndexInvariant (S.freshState (specInput input)) := by
    simp [S.sequentialIndexInvariant, S.receiptIndices, S.freshState]
  have hFold := foldEntries_completed_fields input.block
    (G.startNewBlockTrace input) (S.freshState (specInput input))
    [G.Event.blockStart, G.Event.preTransactionCommit, G.Event.foldStart] [] [] entries
    hInitial hInitialEvents hInitialResults hSequential hOnlyOk.2 hChain
    hInitialGeneratedIndex hInitialSpecificationIndex hStep
  rcases hFold with ⟨finalGenerated, finalEvents, finalResults, finalSpecification,
    hGeneratedFold, hSpecificationFold, hFinalState, hFinalEvents,
    hFinalGeneratedIndex, hFinalSpecificationIndex, hFinalResults⟩
  have hSpecRun :
      S.run (specInput input) (specEntries entries) =
        .completed (S.finishCompleted finalSpecification) := by
    simp [S.run, hSpecRoute, hSpecificationFold, S.finishCompleted]
  have hGeneratedRun :
    G.run input premises entries =
        .completed finalGenerated
          (finalEvents ++ [G.Event.foldReturn, G.Event.transactionsExecuted,
            G.Event.postTransactionCommit]) finalResults := by
    simp [G.run, hRoute, hNormalReturnGate, hGeneratedFold]
  unfold generatedSpecRelation
  refine ⟨hOnlyOk, hAdapter, hChain, hFresh, hEntryFields, ?_⟩
  rw [hGeneratedRun, hSpecRun]
  have hFinishedState :
      stateFields finalGenerated (S.finishCompleted finalSpecification) := by
    simpa [S.finishCompleted] using hFinalState
  have hFinishedEvents :
      eventFields
        (finalEvents ++ [G.Event.foldReturn, G.Event.transactionsExecuted,
          G.Event.postTransactionCommit])
        (S.finishCompleted finalSpecification).events := by
    simp [eventFields, S.finishCompleted, hFinalEvents, mapEvent,
      List.map_append, List.append_assoc]
  have hResultLength : finalResults.length = entries.length :=
    resultListFields_length finalResults entries hFinalResults
  rcases hFinishedState with ⟨hReceiptLength, hReceiptFields, hGasLength, hGasFields,
    hCumulativeGas, hHeaderGas, hCurrentIndex⟩
  refine ⟨hReceiptLength, hReceiptFields, hGasLength, hGasFields, hCumulativeGas,
    hHeaderGas, hCurrentIndex, hFinalGeneratedIndex, ?_, hFinishedEvents,
    hResultLength, hFinalResults⟩
  simpa [S.finishCompleted] using hFinalSpecificationIndex

end OrdinaryTransactionMachineExtractor.Refinement
