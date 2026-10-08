-- SPDX-License-Identifier: LGPL-3.0-only

import OrdinaryTransactionMachineExtractor.Generated.OrdinaryTransactionMachine
import OrdinaryTransactionMachineExtractor.Specification.OrdinaryTransactionMachine

/-!
Fieldwise relation for the bounded ordinary source route and adapter-supplied settled fold. The
propositions in this file are deliberately adapter obligations: Roslyn supplies identities and control-flow facts,
while normal-return and settled-terminal observations supply effectful values. No C# execution is
claimed and no whole production result is compared with Lean equality.
-/

namespace OrdinaryTransactionMachineExtractor.Refinement


def terminalResultIsOk : ReceiptTerminalFoldExtractor.Generated.ReceiptTerminalFoldKernel.TransactionResult → Prop
  | .ok => True
  | .evmException _ _ => False

def terminalStatusIsSuccess : ReceiptTerminalFoldExtractor.Generated.ReceiptTerminalFoldKernel.Status → Prop
  | .success => True
  | .failure => False

def settledEntryInvariant (entry : OrdinaryTransactionMachineExtractor.Generated.SettledEntry) : Prop :=
  entry.wellFormed = true ∧ terminalResultIsOk entry.result ∧
  terminalStatusIsSuccess entry.status

/- The block adapter is explicit about resetting caller-provided tracer state. The seed is an
   input identity only; no receipt, gas, cumulative value, or index is inherited from it. -/
def FreshSequentialTracer (_seed : OrdinaryTransactionMachineExtractor.Generated.TracerSeed) (headerGasUsed : Nat)
    (state : ReceiptTerminalFoldExtractor.Generated.ReceiptTerminalFoldKernel.State) : Prop :=
  state.receipts = [] ∧
  state.gasHistory = [] ∧
  state.cumulativeReceiptGas = 0 ∧
  state.parallel = false ∧
  state.currentIndex = 0 ∧
  state.headerGasUsed = headerGasUsed

theorem fresh_sequential_tracer_is_explicit
    (seed : OrdinaryTransactionMachineExtractor.Generated.TracerSeed) (headerGasUsed : Nat) :
    FreshSequentialTracer seed headerGasUsed
      (OrdinaryTransactionMachineExtractor.Generated.freshSequentialTracer seed headerGasUsed) := by
  simp [FreshSequentialTracer, OrdinaryTransactionMachineExtractor.Generated.freshSequentialTracer]

theorem terminalResultIsOk_iff (result : ReceiptTerminalFoldExtractor.Generated.ReceiptTerminalFoldKernel.TransactionResult) :
    terminalResultIsOk result ↔ result = .ok := by
  cases result <;> simp [terminalResultIsOk]

theorem terminalStatusIsSuccess_iff (status : ReceiptTerminalFoldExtractor.Generated.ReceiptTerminalFoldKernel.Status) :
    terminalStatusIsSuccess status ↔ status = .success := by
  cases status <;> simp [terminalStatusIsSuccess]

def onlyOkTerminalDomain (input : OrdinaryTransactionMachineExtractor.Generated.MachineInput) (_premises : OrdinaryTransactionMachineExtractor.Generated.NormalReturnPremises)
    (entries : List OrdinaryTransactionMachineExtractor.Generated.SettledEntry) : Prop :=
  (OrdinaryTransactionMachineExtractor.Generated.routeValid input = true ∨
    OrdinaryTransactionMachineExtractor.Generated.evmRouteValid input = true) ∧
  ∀ entry ∈ entries, settledEntryInvariant entry

def mapOptionNat {α : Type} (value : Option α) (map : α → Nat) : Option Nat :=
  value.map map

/- The generated oracle identifiers are intentionally projected, not interpreted as bytes,
hashes, addresses or CLR values. -/
def receiptFields (receipt : ReceiptTerminalFoldExtractor.Generated.ReceiptTerminalFoldKernel.Receipt) : OrdinaryTransactionMachineExtractor.Specification.ReceiptFields :=
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

def gasFields (gas : ReceiptTerminalFoldExtractor.Generated.ReceiptTerminalFoldKernel.Gas) : OrdinaryTransactionMachineExtractor.Specification.GasFields :=
  { spentGas := gas.spentGas
    operationGas := gas.operationGas
    blockGas := gas.blockGas
    blockStateGas := gas.blockStateGas
    maxUsedGas := gas.maxUsedGas
    gasRefund := gas.gasRefund }

def totalsFields (totals : ReceiptTerminalFoldExtractor.Generated.ReceiptTerminalFoldKernel.GasTotals) : OrdinaryTransactionMachineExtractor.Specification.GasTotals :=
  { executionGas := totals.executionGas
    stateGas := totals.stateGas }

def mapEvent : OrdinaryTransactionMachineExtractor.Generated.Event → Nat
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

def mapState (state : ReceiptTerminalFoldExtractor.Generated.ReceiptTerminalFoldKernel.State) (events : List OrdinaryTransactionMachineExtractor.Generated.Event) : OrdinaryTransactionMachineExtractor.Specification.State :=
  { receipts := state.receipts.map receiptFields
    gasHistory := state.gasHistory.map totalsFields
    cumulativeReceiptGas := state.cumulativeReceiptGas
    headerGasUsed := state.headerGasUsed
    currentIndex := state.currentIndex
    events := events.map mapEvent }

def observationFields (entry : OrdinaryTransactionMachineExtractor.Generated.SettledEntry) : OrdinaryTransactionMachineExtractor.Specification.EntryObservation :=
  { receipt := receiptFields entry.receipt
    gas := gasFields entry.gas
    gasTotals := totalsFields entry.gasTotals
    resultIsOk := OrdinaryTransactionMachineExtractor.Generated.resultIsOk entry.result
    statusIsSuccess := OrdinaryTransactionMachineExtractor.Generated.statusIsSuccess entry.status
    nestedTracer := entry.nestedTracer
    currentTxTracerIsTracingReceipt := entry.currentTxTracerIsTracingReceipt
    wellFormed := entry.wellFormed }

def specInput (input : OrdinaryTransactionMachineExtractor.Generated.MachineInput) : OrdinaryTransactionMachineExtractor.Specification.Input :=
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

def specEntries (entries : List OrdinaryTransactionMachineExtractor.Generated.SettledEntry) : List OrdinaryTransactionMachineExtractor.Specification.EntryObservation :=
  entries.map observationFields

def eventFields (generated : List OrdinaryTransactionMachineExtractor.Generated.Event) (specification : List Nat) : Prop :=
  generated.map mapEvent = specification

/- Every receipt field used by the terminal kernel is named here. In particular, this does not
use `TransactionResult` equality or a record-level equality bridge. -/
def receiptFieldsExtensional (generated : ReceiptTerminalFoldExtractor.Generated.ReceiptTerminalFoldKernel.Receipt) (specification : OrdinaryTransactionMachineExtractor.Specification.ReceiptFields) : Prop :=
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

def gasFieldsExtensional (generated : ReceiptTerminalFoldExtractor.Generated.ReceiptTerminalFoldKernel.Gas) (specification : OrdinaryTransactionMachineExtractor.Specification.GasFields) : Prop :=
  generated.spentGas = specification.spentGas ∧
  generated.operationGas = specification.operationGas ∧
  generated.blockGas = specification.blockGas ∧
  generated.blockStateGas = specification.blockStateGas ∧
  generated.maxUsedGas = specification.maxUsedGas ∧
  generated.gasRefund = specification.gasRefund

def resultFieldsExtensional (generated : ReceiptTerminalFoldExtractor.Generated.ReceiptTerminalFoldKernel.TransactionResult)
    (specification : Bool) : Prop :=
  match generated, specification with
  | .ok, true => True
  | .evmException _ _, false => False
  | _, _ => False

def observationFieldsExtensional (entry : OrdinaryTransactionMachineExtractor.Generated.SettledEntry)
    (specification : OrdinaryTransactionMachineExtractor.Specification.EntryObservation) : Prop :=
  receiptFieldsExtensional entry.receipt specification.receipt ∧
  gasFieldsExtensional entry.gas specification.gas ∧
  resultFieldsExtensional entry.result specification.resultIsOk ∧
  OrdinaryTransactionMachineExtractor.Generated.resultIsOk entry.result = specification.resultIsOk ∧
  OrdinaryTransactionMachineExtractor.Generated.statusIsSuccess entry.status = specification.statusIsSuccess ∧
  entry.nestedTracer = specification.nestedTracer ∧
  entry.currentTxTracerIsTracingReceipt = specification.currentTxTracerIsTracingReceipt ∧
  entry.wellFormed = specification.wellFormed

def observationListFields : List OrdinaryTransactionMachineExtractor.Generated.SettledEntry → List OrdinaryTransactionMachineExtractor.Specification.EntryObservation → Prop
  | [], [] => True
  | entry :: entryTail, specification :: specificationTail =>
      observationFieldsExtensional entry specification ∧
      observationListFields entryTail specificationTail
  | _, _ => False

def receiptListFields : List ReceiptTerminalFoldExtractor.Generated.ReceiptTerminalFoldKernel.Receipt → List OrdinaryTransactionMachineExtractor.Specification.ReceiptFields → Prop
  | [], [] => True
  | generated :: generatedTail, specification :: specificationTail =>
      receiptFieldsExtensional generated specification ∧
      receiptListFields generatedTail specificationTail
  | _, _ => False

def totalsListFields : List ReceiptTerminalFoldExtractor.Generated.ReceiptTerminalFoldKernel.GasTotals → List OrdinaryTransactionMachineExtractor.Specification.GasTotals → Prop
  | [], [] => True
  | generated :: generatedTail, specification :: specificationTail =>
      generated.executionGas = specification.executionGas ∧
      generated.stateGas = specification.stateGas ∧
      totalsListFields generatedTail specificationTail
  | _, _ => False

def stateFields (generated : ReceiptTerminalFoldExtractor.Generated.ReceiptTerminalFoldKernel.State) (specification : OrdinaryTransactionMachineExtractor.Specification.State) : Prop :=
  generated.receipts.length = specification.receipts.length ∧
  receiptListFields generated.receipts specification.receipts ∧
  generated.gasHistory.length = specification.gasHistory.length ∧
  totalsListFields generated.gasHistory specification.gasHistory ∧
  generated.cumulativeReceiptGas = specification.cumulativeReceiptGas ∧
  generated.headerGasUsed = specification.headerGasUsed ∧
  generated.currentIndex = specification.currentIndex

def sequentialEntriesValid : OrdinaryTransactionMachineExtractor.Specification.State → List OrdinaryTransactionMachineExtractor.Specification.EntryObservation → Prop
  | _, [] => True
  | state, entry :: tail =>
      OrdinaryTransactionMachineExtractor.Specification.entryValid state entry = true ∧ sequentialEntriesValid (OrdinaryTransactionMachineExtractor.Specification.step state entry) tail

def generatedStepState (block : OrdinaryTransactionMachineExtractor.Generated.Block) (state : ReceiptTerminalFoldExtractor.Generated.ReceiptTerminalFoldKernel.State)
    (entry : OrdinaryTransactionMachineExtractor.Generated.SettledEntry) : ReceiptTerminalFoldExtractor.Generated.ReceiptTerminalFoldKernel.State :=
  let terminal :=
    ReceiptTerminalFoldExtractor.Generated.ReceiptTerminalFoldKernel.markAsSuccess state block.terminal entry.transaction entry.recipient entry.gas entry.output
      entry.logs entry.stateRoot entry.nestedTracer entry.currentTxTracerIsTracingReceipt
  { terminal.state with currentIndex := state.currentIndex + 1 }

def generatedStepEvents (events : List OrdinaryTransactionMachineExtractor.Generated.Event) (state : ReceiptTerminalFoldExtractor.Generated.ReceiptTerminalFoldKernel.State)
    (block : OrdinaryTransactionMachineExtractor.Generated.Block) (entry : OrdinaryTransactionMachineExtractor.Generated.SettledEntry) : List OrdinaryTransactionMachineExtractor.Generated.Event :=
  let terminal :=
    ReceiptTerminalFoldExtractor.Generated.ReceiptTerminalFoldKernel.markAsSuccess state block.terminal entry.transaction entry.recipient entry.gas entry.output
      entry.logs entry.stateRoot entry.nestedTracer entry.currentTxTracerIsTracingReceipt
  events ++ [OrdinaryTransactionMachineExtractor.Generated.Event.startNewTxTrace, OrdinaryTransactionMachineExtractor.Generated.Event.execute, OrdinaryTransactionMachineExtractor.Generated.Event.transactionCommit] ++
  terminal.events.map OrdinaryTransactionMachineExtractor.Generated.mapTerminalEvent ++ [OrdinaryTransactionMachineExtractor.Generated.Event.endTxTrace, OrdinaryTransactionMachineExtractor.Generated.Event.currentIndexIncrement]

/- A terminal observation is a source-adapter invariant for one already-settled successful
   entry. It records the append-only receipt/gas boundary and exact conditional forwarding events;
   it is not inferred from the generated fold result. -/
def ReceiptTerminalObservation (block : OrdinaryTransactionMachineExtractor.Generated.Block) (start : ReceiptTerminalFoldExtractor.Generated.ReceiptTerminalFoldKernel.State)
    (entry : OrdinaryTransactionMachineExtractor.Generated.SettledEntry) : Prop :=
  let terminal :=
    ReceiptTerminalFoldExtractor.Generated.ReceiptTerminalFoldKernel.markAsSuccess start block.terminal entry.transaction entry.recipient entry.gas entry.output
      entry.logs entry.stateRoot entry.nestedTracer entry.currentTxTracerIsTracingReceipt
  start.parallel = false ∧
  entry.result = .ok ∧
  terminal.state.currentIndex = start.currentIndex ∧
  terminal.state.receipts.take start.receipts.length = start.receipts ∧
  terminal.state.gasHistory.take start.gasHistory.length = start.gasHistory ∧
  terminal.state.receipts.length = start.receipts.length + 1 ∧
  terminal.state.gasHistory.length = start.gasHistory.length + 1 ∧
  terminal.state.gasHistory.length = terminal.state.receipts.length ∧
  (match OrdinaryTransactionMachineExtractor.Generated.lastReceipt terminal.state.receipts with
  | some actual => OrdinaryTransactionMachineExtractor.Generated.receiptObservationMatches actual entry.receipt = true
  | none => False) ∧
  (match OrdinaryTransactionMachineExtractor.Generated.lastGasTotals terminal.state.gasHistory with
  | some actual => OrdinaryTransactionMachineExtractor.Generated.gasTotalsObservationMatches actual entry.gasTotals = true
  | none => False) ∧
  terminal.events.map OrdinaryTransactionMachineExtractor.Generated.mapTerminalEvent = OrdinaryTransactionMachineExtractor.Generated.terminalForwardingEvents entry

def ReceiptTerminalChain (block : OrdinaryTransactionMachineExtractor.Generated.Block) : ReceiptTerminalFoldExtractor.Generated.ReceiptTerminalFoldKernel.State → List OrdinaryTransactionMachineExtractor.Generated.SettledEntry → Prop
  | _, [] => True
  | state, entry :: tail =>
      ReceiptTerminalObservation block state entry ∧
      ReceiptTerminalChain block (generatedStepState block state entry) tail

/- A local bridge for one terminal step. It exposes the receipt/gas observations, the source
   terminal event order, and the resulting state fields; it does not quantify over a whole run. -/
def localStepBridge (block : OrdinaryTransactionMachineExtractor.Generated.Block) (generated : ReceiptTerminalFoldExtractor.Generated.ReceiptTerminalFoldKernel.State) (specification : OrdinaryTransactionMachineExtractor.Specification.State)
    (entry : OrdinaryTransactionMachineExtractor.Generated.SettledEntry) : Prop :=
  let terminal :=
    ReceiptTerminalFoldExtractor.Generated.ReceiptTerminalFoldKernel.markAsSuccess generated block.terminal entry.transaction entry.recipient entry.gas entry.output
      entry.logs entry.stateRoot entry.nestedTracer entry.currentTxTracerIsTracingReceipt
  stateFields (generatedStepState block generated entry)
    (OrdinaryTransactionMachineExtractor.Specification.step specification (observationFields entry)) ∧
  OrdinaryTransactionMachineExtractor.Generated.sequentialIndexInvariant (generatedStepState block generated entry) ∧
  OrdinaryTransactionMachineExtractor.Specification.sequentialIndexInvariant (OrdinaryTransactionMachineExtractor.Specification.step specification (observationFields entry)) ∧
  terminal.events.map OrdinaryTransactionMachineExtractor.Generated.mapTerminalEvent = OrdinaryTransactionMachineExtractor.Generated.terminalForwardingEvents entry

/- Typed source-return premises carry proofs about the corresponding generated observations.
   Roslyn binds the call sites but does not execute their callees; a caller of the refinement
   theorem must construct these proofs from its adapter observations. -/
def NormalReturnObserved (flag : Bool) : Prop := flag = true

structure EntrySourceNormality (normality : OrdinaryTransactionMachineExtractor.Generated.EntryNormality)
    (entry : OrdinaryTransactionMachineExtractor.Generated.SettledEntry) : Prop where
  startNewTxTraceNormalReturn : NormalReturnObserved normality.startNewTxTrace
  executeNormalReturn : NormalReturnObserved normality.execute
  endTxTraceNormalReturn : NormalReturnObserved normality.endTxTrace
  settledReturnObservation : settledEntryInvariant entry

def transactionProcessorNormalReturnFlags (premises : OrdinaryTransactionMachineExtractor.Generated.NormalReturnPremises) : Prop :=
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

def EntrySourceNormalityChain : List OrdinaryTransactionMachineExtractor.Generated.EntryNormality → List OrdinaryTransactionMachineExtractor.Generated.SettledEntry → Prop
  | [], [] => True
  | normality :: normalityTail, entry :: entryTail =>
      EntrySourceNormality normality entry ∧
      EntrySourceNormalityChain normalityTail entryTail
  | _, _ => False

structure SourceAdapterPremises (premises : OrdinaryTransactionMachineExtractor.Generated.NormalReturnPremises)
    (entries : List OrdinaryTransactionMachineExtractor.Generated.SettledEntry) : Prop where
  transactionProcessorNormalReturn : transactionProcessorNormalReturnFlags premises
  perEntry : EntrySourceNormalityChain premises.entries entries
  executorNormalReturn : NormalReturnObserved premises.executorReturn
  transactionsExecutedSubscriberNormalReturn :
    NormalReturnObserved premises.transactionsExecutedSubscriber
  postTransactionCommitStateNormalReturn :
    NormalReturnObserved premises.postTransactionCommitState
  entriesKindHomogeneous :
    entries.all (fun entry => entry.entryKind == .simpleTransfer) ∨
    entries.all (fun entry => entry.entryKind == .evmSuccess)
  evmExecuteNormalReturn :
    (entries.all fun entry => entry.entryKind == .evmSuccess) →
    NormalReturnObserved premises.executeEvmTransaction

private theorem EntrySourceNormality.valid
    (source : EntrySourceNormality normality entry) :
    OrdinaryTransactionMachineExtractor.Generated.EntryNormality.valid normality = true := by
  rcases source with ⟨hStart, hExecute, hEnd, _⟩
  simp [NormalReturnObserved] at hStart hExecute hEnd
  simp [OrdinaryTransactionMachineExtractor.Generated.EntryNormality.valid, hStart, hExecute, hEnd]

private theorem EntrySourceNormalityChain.valid
    (source : EntrySourceNormalityChain normalities entries) :
    normalities.length = entries.length ∧
    (normalities.map OrdinaryTransactionMachineExtractor.Generated.EntryNormality.valid).all (fun value => value) = true := by
  induction normalities generalizing entries with
  | nil =>
      cases entries with
      | nil => simp
      | cons entry tail => simp [EntrySourceNormalityChain] at source
  | cons normality normalityTail ih =>
      cases entries with
      | nil => simp [EntrySourceNormalityChain] at source
      | cons entry entryTail =>
          have hHead : OrdinaryTransactionMachineExtractor.Generated.EntryNormality.valid normality = true := source.1.valid
          have hTail := ih (entries := entryTail) source.2
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
          · exact ih (entries := entryTail) hTail candidate hTailMembership

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
  rcases adapter.entriesKindHomogeneous with hSimpleKinds | hEvmKinds
  · simp [OrdinaryTransactionMachineExtractor.Generated.NormalReturnPremises.validFor,
      OrdinaryTransactionMachineExtractor.Generated.NormalReturnPremises.all,
      hSetContext, hProcess, hExecuteCore, hOrdinaryExecute, hStatic, hStateful,
      hNonce, hPreCommit, hAvailableGas, hSimple, hSettlement, hTransactionCommit,
      hFinalize, hExecutor, hSubscriber, hPostCommit,
      hReceiptTerminal, hEntryLength, hEntryFlags, hSimpleKinds]
  · have hEvm := adapter.evmExecuteNormalReturn hEvmKinds
    simp [NormalReturnObserved] at hEvm
    simp [OrdinaryTransactionMachineExtractor.Generated.NormalReturnPremises.validFor,
      OrdinaryTransactionMachineExtractor.Generated.NormalReturnPremises.evmAll,
      hSetContext, hProcess, hExecuteCore, hOrdinaryExecute, hStatic, hStateful,
      hNonce, hPreCommit, hAvailableGas, hEvm, hSettlement, hTransactionCommit,
      hFinalize, hExecutor, hSubscriber, hPostCommit,
      hReceiptTerminal, hEntryLength, hEntryFlags, hEvmKinds]

def resultListFields : List ReceiptTerminalFoldExtractor.Generated.ReceiptTerminalFoldKernel.TransactionResult → List OrdinaryTransactionMachineExtractor.Generated.SettledEntry → Prop
  | [], [] => True
  | .ok :: generatedTail, entry :: entryTail =>
      (match entry.result with | .ok => True | .evmException _ _ => False) ∧
      resultListFields generatedTail entryTail
  | .evmException _ _ :: _, _ => False
  | _, _ => False

def generatedSpecRelation (input : OrdinaryTransactionMachineExtractor.Generated.MachineInput) (premises : OrdinaryTransactionMachineExtractor.Generated.NormalReturnPremises)
    (entries : List OrdinaryTransactionMachineExtractor.Generated.SettledEntry) (generated : OrdinaryTransactionMachineExtractor.Generated.Outcome) (specification : OrdinaryTransactionMachineExtractor.Specification.Outcome) : Prop :=
  onlyOkTerminalDomain input premises entries ∧
  SourceAdapterPremises premises entries ∧
  ReceiptTerminalChain input.block (OrdinaryTransactionMachineExtractor.Generated.startNewBlockTrace input) entries ∧
  FreshSequentialTracer input.tracerSeed input.block.headerGasUsed
    (OrdinaryTransactionMachineExtractor.Generated.startNewBlockTrace input) ∧
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
      OrdinaryTransactionMachineExtractor.Generated.sequentialIndexInvariant generatedState ∧
      OrdinaryTransactionMachineExtractor.Specification.sequentialIndexInvariant specificationState ∧
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

/- This is the source-route part of the exact theorem domain: either the simple-transfer
   route or the oracle-routed EVM-success route holds for the run. `SourceAdapterPremises`
   separately relates the proof-carrying normal-return and settled-entry observations
   positionally to the supplied list. Neither is a relation to the source block's
   transaction list. -/
def adapterSuppliedFoldDomain (input : OrdinaryTransactionMachineExtractor.Generated.MachineInput) : Prop :=
  OrdinaryTransactionMachineExtractor.Generated.routeValid input = true ∨
    OrdinaryTransactionMachineExtractor.Generated.evmRouteValid input = true

private theorem resultListFields_append_ok
    (generated : List ReceiptTerminalFoldExtractor.Generated.ReceiptTerminalFoldKernel.TransactionResult) (entries : List OrdinaryTransactionMachineExtractor.Generated.SettledEntry)
    (entry : OrdinaryTransactionMachineExtractor.Generated.SettledEntry)
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
      | nil => cases result <;> simp [resultListFields] at hFields
      | cons sourceEntry sourceTail =>
          cases result with
          | ok =>
              simp only [List.cons_append, resultListFields] at hFields ⊢
              exact ⟨hFields.1, ih (entries := sourceTail) hFields.2⟩
          | evmException exception substateError =>
              simp [resultListFields] at hFields

private theorem resultListFields_length
    (generated : List ReceiptTerminalFoldExtractor.Generated.ReceiptTerminalFoldKernel.TransactionResult) (entries : List OrdinaryTransactionMachineExtractor.Generated.SettledEntry)
    (hFields : resultListFields generated entries) :
    generated.length = entries.length := by
  induction generated generalizing entries with
  | nil =>
      cases entries with
      | nil => rfl
      | cons head tail => simp [resultListFields] at hFields
  | cons result generatedTail ih =>
      cases entries with
      | nil => cases result <;> simp [resultListFields] at hFields
      | cons sourceEntry sourceTail =>
          cases result with
          | ok =>
              have hTail : resultListFields generatedTail sourceTail := by
                simpa [resultListFields] using hFields.2
              simpa only [List.length_cons] using
                congrArg Nat.succ (ih (entries := sourceTail) hTail)
          | evmException exception substateError =>
              simp [resultListFields] at hFields

/- The generated and independent folds are related by one-step fieldwise bridges. The induction
   proves the complete adapter-supplied multi-entry result; no source transaction-list or
   whole-run output relation is an input premise. -/
private theorem foldEntries_completed_fields
    (block : OrdinaryTransactionMachineExtractor.Generated.Block) (generated : ReceiptTerminalFoldExtractor.Generated.ReceiptTerminalFoldKernel.State) (specification : OrdinaryTransactionMachineExtractor.Specification.State)
    (events : List OrdinaryTransactionMachineExtractor.Generated.Event) (results : List ReceiptTerminalFoldExtractor.Generated.ReceiptTerminalFoldKernel.TransactionResult)
    (processed : List OrdinaryTransactionMachineExtractor.Generated.SettledEntry) (entries : List OrdinaryTransactionMachineExtractor.Generated.SettledEntry)
    (hState : stateFields generated specification)
    (hEvents : eventFields events specification.events)
    (hResults : resultListFields results processed)
    (hValid : sequentialEntriesValid specification (specEntries entries))
    (hSettled : ∀ entry ∈ entries, settledEntryInvariant entry)
    (hChain : ReceiptTerminalChain block generated entries)
    (hGeneratedIndex : OrdinaryTransactionMachineExtractor.Generated.sequentialIndexInvariant generated)
    (hSpecificationIndex : OrdinaryTransactionMachineExtractor.Specification.sequentialIndexInvariant specification)
    (hStep : ∀ (g : ReceiptTerminalFoldExtractor.Generated.ReceiptTerminalFoldKernel.State) (s : OrdinaryTransactionMachineExtractor.Specification.State) (entry : OrdinaryTransactionMachineExtractor.Generated.SettledEntry),
      stateFields g s →
      OrdinaryTransactionMachineExtractor.Specification.entryValid s (observationFields entry) = true →
      localStepBridge block g s entry) :
    ∃ finalGenerated finalEvents finalResults finalSpecification,
      OrdinaryTransactionMachineExtractor.Generated.foldEntries block generated events results entries =
        .completed finalGenerated finalEvents finalResults ∧
      OrdinaryTransactionMachineExtractor.Specification.fold specification (specEntries entries) = .completed finalSpecification ∧
      stateFields finalGenerated finalSpecification ∧
      eventFields finalEvents finalSpecification.events ∧
      OrdinaryTransactionMachineExtractor.Generated.sequentialIndexInvariant finalGenerated ∧
      OrdinaryTransactionMachineExtractor.Specification.sequentialIndexInvariant finalSpecification ∧
      resultListFields finalResults (processed ++ entries) := by
  induction entries generalizing generated specification events results processed with
  | nil =>
      refine ⟨generated, events, results, specification, ?_, ?_, hState, hEvents,
        hGeneratedIndex, hSpecificationIndex, ?_⟩
      · simp [OrdinaryTransactionMachineExtractor.Generated.foldEntries]
      · simp [OrdinaryTransactionMachineExtractor.Specification.fold, specEntries]
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
      have hEntryResultOk : OrdinaryTransactionMachineExtractor.Generated.resultIsOk entry.result = true := by
        simp [hEntryResult, OrdinaryTransactionMachineExtractor.Generated.resultIsOk]
      have hEntryStatusSuccess : OrdinaryTransactionMachineExtractor.Generated.statusIsSuccess entry.status = true := by
        simp [hEntryStatus, OrdinaryTransactionMachineExtractor.Generated.statusIsSuccess]
      have hEntryValid : OrdinaryTransactionMachineExtractor.Specification.entryValid specification (observationFields entry) = true :=
        hValid.1
      have hTailValid :
          sequentialEntriesValid (OrdinaryTransactionMachineExtractor.Specification.step specification (observationFields entry))
            (specEntries tail) := hValid.2
      have hChainEntry : ReceiptTerminalObservation block generated entry := hChain.1
      have hChainTail :
          ReceiptTerminalChain block (generatedStepState block generated entry) tail := hChain.2
      let terminal :=
        ReceiptTerminalFoldExtractor.Generated.ReceiptTerminalFoldKernel.markAsSuccess generated block.terminal entry.transaction entry.recipient entry.gas
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
          (match OrdinaryTransactionMachineExtractor.Generated.lastReceipt terminal.state.receipts with
          | some actual => OrdinaryTransactionMachineExtractor.Generated.receiptObservationMatches actual entry.receipt = true
          | none => False) ∧
          (match OrdinaryTransactionMachineExtractor.Generated.lastGasTotals terminal.state.gasHistory with
          | some actual => OrdinaryTransactionMachineExtractor.Generated.gasTotalsObservationMatches actual entry.gasTotals = true
          | none => False) ∧
          terminal.events.map OrdinaryTransactionMachineExtractor.Generated.mapTerminalEvent =
            OrdinaryTransactionMachineExtractor.Generated.terminalForwardingEvents entry := by
        simpa [ReceiptTerminalObservation, terminal] using hChainEntry
      rcases hChainData with ⟨hParallel, hChainResult, hCurrentIndex, hReceiptPrefix,
        hGasPrefix, hReceiptLength, hGasLength, hHistoryLength, hReceiptMatch,
        hGasMatch, hChainEvents⟩
      have hLocal := hStep generated specification entry hState hEntryValid
      have hNextState :
          stateFields (generatedStepState block generated entry)
            (OrdinaryTransactionMachineExtractor.Specification.step specification (observationFields entry)) := hLocal.1
      have hNextGeneratedIndex :
          OrdinaryTransactionMachineExtractor.Generated.sequentialIndexInvariant (generatedStepState block generated entry) := hLocal.2.1
      have hNextSpecificationIndex :
          OrdinaryTransactionMachineExtractor.Specification.sequentialIndexInvariant (OrdinaryTransactionMachineExtractor.Specification.step specification (observationFields entry)) := hLocal.2.2.1
      have hLocalEvents :
          terminal.events.map OrdinaryTransactionMachineExtractor.Generated.mapTerminalEvent =
            OrdinaryTransactionMachineExtractor.Generated.terminalForwardingEvents entry := by
        simpa [localStepBridge, terminal] using hLocal.2.2.2
      cases hReceiptLast : OrdinaryTransactionMachineExtractor.Generated.lastReceipt terminal.state.receipts with
      | none =>
          simp [hReceiptLast] at hReceiptMatch
      | some actualReceipt =>
          have hReceiptObservation :
              OrdinaryTransactionMachineExtractor.Generated.receiptObservationMatches actualReceipt entry.receipt = true := by
            simpa [hReceiptLast] using hReceiptMatch
          cases hGasLast : OrdinaryTransactionMachineExtractor.Generated.lastGasTotals terminal.state.gasHistory with
          | none =>
              simp [hGasLast] at hGasMatch
          | some actualTotals =>
              have hGasObservation :
                  OrdinaryTransactionMachineExtractor.Generated.gasTotalsObservationMatches actualTotals entry.gasTotals = true := by
                simpa [hGasLast] using hGasMatch
              have hGeneratedStep :
                  OrdinaryTransactionMachineExtractor.Generated.foldEntries block generated events results (entry :: tail) =
                    OrdinaryTransactionMachineExtractor.Generated.foldEntries block (generatedStepState block generated entry)
                      (generatedStepEvents events generated block entry)
                      (results ++ [entry.result]) tail := by
                simp [OrdinaryTransactionMachineExtractor.Generated.foldEntries, hEntryWellFormed, hEntryResultOk,
                  hEntryStatusSuccess, terminal, hReceiptLast, hGasLast,
                  hReceiptObservation, hGasObservation, generatedStepState,
                  generatedStepEvents]
              have hObsWf : (observationFields entry).wellFormed = true := by
                simpa [observationFields] using hEntryWellFormed
              have hObsOk : (observationFields entry).resultIsOk = true := by
                simpa [observationFields] using hEntryResultOk
              have hSpecStep :
                  OrdinaryTransactionMachineExtractor.Specification.fold specification (specEntries (entry :: tail)) =
                    OrdinaryTransactionMachineExtractor.Specification.fold (OrdinaryTransactionMachineExtractor.Specification.step specification (observationFields entry))
                      (specEntries tail) := by
                simp [specEntries, OrdinaryTransactionMachineExtractor.Specification.fold,
                  hObsWf, hObsOk, hEntryValid]
              have hEventsNext :
                  eventFields (generatedStepEvents events generated block entry)
                    (OrdinaryTransactionMachineExtractor.Specification.step specification (observationFields entry)).events := by
                have hMap : events.map mapEvent = specification.events := by
                  simpa [eventFields] using hEvents
                have hFwd : ∀ (nested current : Bool),
                    List.map (mapEvent ∘ OrdinaryTransactionMachineExtractor.Generated.mapTerminalEvent)
                      (ReceiptTerminalFoldExtractor.Generated.ReceiptTerminalFoldKernel.markAsSuccess generated block.terminal
                        entry.transaction entry.recipient entry.gas entry.output entry.logs entry.stateRoot
                        nested current).events =
                      ([6, 7] ++ (if nested then [8] else []) ++ (if current then [9] else [])) := by
                  intro nested current
                  by_cases hN : nested = true <;> by_cases hC : current = true <;>
                    simp [ReceiptTerminalFoldExtractor.Generated.ReceiptTerminalFoldKernel.markAsSuccess,
                      ReceiptTerminalFoldExtractor.Generated.ReceiptTerminalFoldKernel.forwardingEvents,
                      OrdinaryTransactionMachineExtractor.Generated.mapTerminalEvent, mapEvent, hN, hC]
                by_cases hNested : entry.nestedTracer = true <;>
                  by_cases hCurrent : entry.currentTxTracerIsTracingReceipt = true <;>
                    simp [eventFields, generatedStepEvents, OrdinaryTransactionMachineExtractor.Specification.step, mapEvent,
                      OrdinaryTransactionMachineExtractor.Specification.terminalForwardingEvents,
                      observationFields, List.map_append, List.append_assoc,
                      hMap, hNested, hCurrent, hFwd]
              have hResultsNext :
                  resultListFields (results ++ [entry.result]) (processed ++ [entry]) := by
                simpa [hEntryResult] using
                  resultListFields_append_ok results processed entry hResults hEntryResult
              have hTail := ih
                (generated := generatedStepState block generated entry)
                (specification := OrdinaryTransactionMachineExtractor.Specification.step specification (observationFields entry))
                (events := generatedStepEvents events generated block entry)
                (results := results ++ [entry.result])
                (processed := processed ++ [entry])
                hNextState hEventsNext hResultsNext hTailValid hTailSettled hChainTail
                hNextGeneratedIndex hNextSpecificationIndex
              rcases hTail with ⟨finalGenerated, finalEvents, finalResults,
                finalSpecification, hGenerated, hSpecification, hFinalState,
                hFinalEvents, hFinalGeneratedIndex, hFinalSpecificationIndex,
                hFinalResults⟩
              refine ⟨finalGenerated, finalEvents, finalResults, finalSpecification, ?_, ?_,
                hFinalState, hFinalEvents, hFinalGeneratedIndex,
                hFinalSpecificationIndex, (by simpa [List.append_assoc] using hFinalResults)⟩
              · calc
                  OrdinaryTransactionMachineExtractor.Generated.foldEntries block generated events results (entry :: tail) =
                      OrdinaryTransactionMachineExtractor.Generated.foldEntries block (generatedStepState block generated entry)
                        (generatedStepEvents events generated block entry)
                        (results ++ [entry.result]) tail := hGeneratedStep
                  _ = .completed finalGenerated finalEvents finalResults := hGenerated
              · calc
                  OrdinaryTransactionMachineExtractor.Specification.fold specification (specEntries (entry :: tail)) =
                      OrdinaryTransactionMachineExtractor.Specification.fold (OrdinaryTransactionMachineExtractor.Specification.step specification (observationFields entry))
                        (specEntries tail) := hSpecStep
                  _ = .completed finalSpecification := hSpecification

/- Rejection is a separate, prefix-preserving model path. It deliberately does not state the
   production false-result EndTxTrace poststate; that path is outside the OnlyOkTerminal adapter. -/
theorem malformed_settled_entry_rejects_without_prefix_mutation
    (block : OrdinaryTransactionMachineExtractor.Generated.Block) (state : ReceiptTerminalFoldExtractor.Generated.ReceiptTerminalFoldKernel.State) (events : List OrdinaryTransactionMachineExtractor.Generated.Event)
    (processed : List ReceiptTerminalFoldExtractor.Generated.ReceiptTerminalFoldKernel.TransactionResult) (entry : OrdinaryTransactionMachineExtractor.Generated.SettledEntry)
    (tail : List OrdinaryTransactionMachineExtractor.Generated.SettledEntry) (hMalformed : entry.wellFormed = false) :
    OrdinaryTransactionMachineExtractor.Generated.foldEntries block state events processed (entry :: tail) =
      .rejected .malformedSettled state events processed := by
  simp [OrdinaryTransactionMachineExtractor.Generated.foldEntries, hMalformed]

theorem non_ok_result_rejects_without_prefix_mutation
    (block : OrdinaryTransactionMachineExtractor.Generated.Block) (state : ReceiptTerminalFoldExtractor.Generated.ReceiptTerminalFoldKernel.State) (events : List OrdinaryTransactionMachineExtractor.Generated.Event)
    (processed : List ReceiptTerminalFoldExtractor.Generated.ReceiptTerminalFoldKernel.TransactionResult) (entry : OrdinaryTransactionMachineExtractor.Generated.SettledEntry)
    (tail : List OrdinaryTransactionMachineExtractor.Generated.SettledEntry) (hWellFormed : entry.wellFormed = true)
    (hNonOk : OrdinaryTransactionMachineExtractor.Generated.resultIsOk entry.result = false) :
    ∃ reason,
      OrdinaryTransactionMachineExtractor.Generated.foldEntries block state events processed (entry :: tail) =
        .rejected reason state events processed := by
  cases hResult : entry.result with
  | ok =>
      rw [hResult] at hNonOk
      simp [OrdinaryTransactionMachineExtractor.Generated.resultIsOk] at hNonOk
  | evmException exception substateError =>
      rw [hResult] at hNonOk
      exact ⟨.exceptionResult, by simp [OrdinaryTransactionMachineExtractor.Generated.foldEntries, hWellFormed, hNonOk, hResult]⟩

/- This theorem is conditional on an adapter-supplied settled list and local fieldwise bridges.
Per-entry Start/Execute/End,
executor, subscriber, post-fold commit, terminal-chain, and one-step receipt/gas bridges are
explicit premises; no source transaction-list composition or whole-run production equality is assumed. -/
theorem generatedRun_refines_independentSpec
    (input : OrdinaryTransactionMachineExtractor.Generated.MachineInput) (premises : OrdinaryTransactionMachineExtractor.Generated.NormalReturnPremises)
    (entries : List OrdinaryTransactionMachineExtractor.Generated.SettledEntry)
    (hDomain : adapterSuppliedFoldDomain input)
    (hAdapter : SourceAdapterPremises premises entries)
    (hEntryFields : observationListFields entries (specEntries entries))
    (hFresh : FreshSequentialTracer input.tracerSeed input.block.headerGasUsed
      (OrdinaryTransactionMachineExtractor.Generated.startNewBlockTrace input))
    (hInitial : stateFields (OrdinaryTransactionMachineExtractor.Generated.startNewBlockTrace input)
      (OrdinaryTransactionMachineExtractor.Specification.freshState (specInput input)))
    (hSequential : sequentialEntriesValid (OrdinaryTransactionMachineExtractor.Specification.freshState (specInput input))
      (specEntries entries))
    (hChain : ReceiptTerminalChain input.block (OrdinaryTransactionMachineExtractor.Generated.startNewBlockTrace input) entries)
    (hStep : ∀ (generated : ReceiptTerminalFoldExtractor.Generated.ReceiptTerminalFoldKernel.State) (specification : OrdinaryTransactionMachineExtractor.Specification.State) (entry : OrdinaryTransactionMachineExtractor.Generated.SettledEntry),
      stateFields generated specification →
      OrdinaryTransactionMachineExtractor.Specification.entryValid specification (observationFields entry) = true →
      localStepBridge input.block generated specification entry) :
    generatedSpecRelation input premises entries
      (OrdinaryTransactionMachineExtractor.Generated.run input premises entries)
      (OrdinaryTransactionMachineExtractor.Specification.run (specInput input) (specEntries entries)) := by
  have hAdapterSettled : ∀ entry ∈ entries, settledEntryInvariant entry :=
    hAdapter.perEntry.settled
  have hOnlyOk : onlyOkTerminalDomain input premises entries :=
    ⟨hDomain, hAdapterSettled⟩
  have hRoute : OrdinaryTransactionMachineExtractor.Generated.routeValid input = true ∨
      OrdinaryTransactionMachineExtractor.Generated.evmRouteValid input = true := hOnlyOk.1
  have hSpecRoute : OrdinaryTransactionMachineExtractor.Specification.routeValid (specInput input) = true ∨
      OrdinaryTransactionMachineExtractor.Specification.evmRouteValid (specInput input) = true := by
    rcases hRoute with hR | hE
    · left
      simpa [OrdinaryTransactionMachineExtractor.Generated.routeValid, OrdinaryTransactionMachineExtractor.Specification.routeValid, specInput] using hR
    · right
      simpa [OrdinaryTransactionMachineExtractor.Generated.evmRouteValid, OrdinaryTransactionMachineExtractor.Specification.evmRouteValid, specInput] using hE
  have hNormalReturnGate : premises.validFor entries = true :=
    SourceAdapterPremises.validFor hAdapter
  have hInitialEvents :
      eventFields [OrdinaryTransactionMachineExtractor.Generated.Event.blockStart, OrdinaryTransactionMachineExtractor.Generated.Event.preTransactionCommit, OrdinaryTransactionMachineExtractor.Generated.Event.foldStart]
        (OrdinaryTransactionMachineExtractor.Specification.freshState (specInput input)).events := by
    simp [eventFields, OrdinaryTransactionMachineExtractor.Specification.freshState, mapEvent]
  have hInitialResults : resultListFields ([] : List ReceiptTerminalFoldExtractor.Generated.ReceiptTerminalFoldKernel.TransactionResult) [] := by
    simp [resultListFields]
  have hInitialGeneratedIndex :
      OrdinaryTransactionMachineExtractor.Generated.sequentialIndexInvariant (OrdinaryTransactionMachineExtractor.Generated.startNewBlockTrace input) := by
    simp [OrdinaryTransactionMachineExtractor.Generated.sequentialIndexInvariant, OrdinaryTransactionMachineExtractor.Generated.receiptIndices, OrdinaryTransactionMachineExtractor.Generated.startNewBlockTrace,
      OrdinaryTransactionMachineExtractor.Generated.freshSequentialTracer]
  have hInitialSpecificationIndex :
      OrdinaryTransactionMachineExtractor.Specification.sequentialIndexInvariant (OrdinaryTransactionMachineExtractor.Specification.freshState (specInput input)) := by
    simp [OrdinaryTransactionMachineExtractor.Specification.sequentialIndexInvariant, OrdinaryTransactionMachineExtractor.Specification.receiptIndices, OrdinaryTransactionMachineExtractor.Specification.freshState]
  have hFold := foldEntries_completed_fields input.block
    (OrdinaryTransactionMachineExtractor.Generated.startNewBlockTrace input) (OrdinaryTransactionMachineExtractor.Specification.freshState (specInput input))
    [OrdinaryTransactionMachineExtractor.Generated.Event.blockStart, OrdinaryTransactionMachineExtractor.Generated.Event.preTransactionCommit, OrdinaryTransactionMachineExtractor.Generated.Event.foldStart] [] [] entries
    hInitial hInitialEvents hInitialResults hSequential hOnlyOk.2 hChain
    hInitialGeneratedIndex hInitialSpecificationIndex hStep
  rcases hFold with ⟨finalGenerated, finalEvents, finalResults, finalSpecification,
    hGeneratedFold, hSpecificationFold, hFinalState, hFinalEvents,
    hFinalGeneratedIndex, hFinalSpecificationIndex, hFinalResults⟩
  have hSpecRun :
      OrdinaryTransactionMachineExtractor.Specification.run (specInput input) (specEntries entries) =
        .completed (OrdinaryTransactionMachineExtractor.Specification.finishCompleted finalSpecification) := by
    simp [OrdinaryTransactionMachineExtractor.Specification.run, hSpecRoute, hSpecificationFold, OrdinaryTransactionMachineExtractor.Specification.finishCompleted]
  have hGeneratedRun :
    OrdinaryTransactionMachineExtractor.Generated.run input premises entries =
        .completed finalGenerated
          (finalEvents ++ [OrdinaryTransactionMachineExtractor.Generated.Event.foldReturn, OrdinaryTransactionMachineExtractor.Generated.Event.transactionsExecuted,
            OrdinaryTransactionMachineExtractor.Generated.Event.postTransactionCommit]) finalResults := by
    simp [OrdinaryTransactionMachineExtractor.Generated.run, hRoute, hNormalReturnGate, hGeneratedFold]
  unfold generatedSpecRelation
  refine ⟨hOnlyOk, hAdapter, hChain, hFresh, hEntryFields, ?_⟩
  rw [hGeneratedRun, hSpecRun]
  have hFinishedState :
      stateFields finalGenerated (OrdinaryTransactionMachineExtractor.Specification.finishCompleted finalSpecification) :=
    hFinalState
  have hFinishedEvents :
      eventFields
        (finalEvents ++ [OrdinaryTransactionMachineExtractor.Generated.Event.foldReturn, OrdinaryTransactionMachineExtractor.Generated.Event.transactionsExecuted,
          OrdinaryTransactionMachineExtractor.Generated.Event.postTransactionCommit])
        (OrdinaryTransactionMachineExtractor.Specification.finishCompleted finalSpecification).events := by
    have hFinalMap : finalEvents.map mapEvent = finalSpecification.events := by
      simpa [eventFields] using hFinalEvents
    simp [eventFields, OrdinaryTransactionMachineExtractor.Specification.finishCompleted, mapEvent,
      List.map_append, hFinalMap]
  have hResultLength : finalResults.length = entries.length :=
    resultListFields_length finalResults entries hFinalResults
  rcases hFinishedState with ⟨hReceiptLength, hReceiptFields, hGasLength, hGasFields,
    hCumulativeGas, hHeaderGas, hCurrentIndex⟩
  refine ⟨hReceiptLength, hReceiptFields, hGasLength, hGasFields, hCumulativeGas,
    hHeaderGas, hCurrentIndex, hFinalGeneratedIndex, ?_, hFinishedEvents,
    hResultLength, hFinalResults⟩
  exact hFinalSpecificationIndex

end OrdinaryTransactionMachineExtractor.Refinement
