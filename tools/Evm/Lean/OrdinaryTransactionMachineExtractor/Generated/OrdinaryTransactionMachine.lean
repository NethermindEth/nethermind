-- SPDX-License-Identifier: LGPL-3.0-only

-- Deliberately stale static draft, not a current extracted artifact. The serialized extraction
-- lane must replace it from compiler-closed IR before any Lean or artifact gate may accept it.
-- Scope after regeneration: an adapter-supplied settled-entry list under OnlyOkTerminal, exact
-- Commit, ordinary, sequential, BAL-disabled, exact-guard simple-transfer premises. No source
-- transaction-list derivation, EVM-frame interpretation, or false-result path is claimed.

import ReceiptTerminalFoldExtractor.Generated.ReceiptTerminalFoldKernel

namespace OrdinaryTransactionMachineExtractor.Generated

abbrev TerminalReceipt := ReceiptTerminalFoldExtractor.Generated.ReceiptTerminalFoldKernel.Receipt
abbrev TerminalGasTotals := ReceiptTerminalFoldExtractor.Generated.ReceiptTerminalFoldKernel.GasTotals
abbrev TerminalGas := ReceiptTerminalFoldExtractor.Generated.ReceiptTerminalFoldKernel.Gas
abbrev TerminalState := ReceiptTerminalFoldExtractor.Generated.ReceiptTerminalFoldKernel.State
abbrev TerminalBlock := ReceiptTerminalFoldExtractor.Generated.ReceiptTerminalFoldKernel.BlockInput
abbrev TerminalTransaction := ReceiptTerminalFoldExtractor.Generated.ReceiptTerminalFoldKernel.TransactionInput
abbrev TerminalStatus := ReceiptTerminalFoldExtractor.Generated.ReceiptTerminalFoldKernel.Status
abbrev TerminalResult := ReceiptTerminalFoldExtractor.Generated.ReceiptTerminalFoldKernel.TransactionResult
abbrev TerminalAddress := ReceiptTerminalFoldExtractor.Generated.ReceiptTerminalFoldKernel.AddressOracle
abbrev TerminalBytes := ReceiptTerminalFoldExtractor.Generated.ReceiptTerminalFoldKernel.BytesOracle
abbrev TerminalLog := ReceiptTerminalFoldExtractor.Generated.ReceiptTerminalFoldKernel.LogOracle
abbrev TerminalHash := ReceiptTerminalFoldExtractor.Generated.ReceiptTerminalFoldKernel.HashOracle
abbrev TerminalException := ReceiptTerminalFoldExtractor.Generated.ReceiptTerminalFoldKernel.EvmExceptionOracle
abbrev TerminalError := ReceiptTerminalFoldExtractor.Generated.ReceiptTerminalFoldKernel.ErrorOracle

def semanticIrSha256 : String := "static-draft-reemit-required"
def sourceClosureSha256 : String := "static-draft-reemit-required"
def compilerClosureSha256 : String := "6185996666fdad205d2bf17895ed7b570948deea9ba9ea3d80b28bafb9717ca7"

def sourcePaths : List String := [
  "src/Nethermind/Nethermind.Evm/TransactionProcessing/TransactionProcessor.cs",
  "src/Nethermind/Nethermind.Evm/TransactionProcessing/ExecutionOptions.cs",
  "src/Nethermind/Nethermind.Evm/TransactionProcessing/SystemTransactionRoutingKernel.cs",
  "src/Nethermind/Nethermind.Evm/TransactionProcessing/TransactionSettlementKernel.cs",
  "src/Nethermind/Nethermind.Evm/TransactionProcessing/ITransactionProcessor.cs",
  "src/Nethermind/Nethermind.Evm/TransactionProcessing/ITransactionProcessorAdapter.cs",
  "src/Nethermind/Nethermind.Evm/TransactionProcessing/ExecuteTransactionProcessorAdapter.cs",
  "src/Nethermind/Nethermind.Evm/GasPolicy/IGasPolicy.cs",
  "src/Nethermind/Nethermind.Evm/GasPolicy/EthereumGasPolicy.cs",
  "src/Nethermind/Nethermind.Evm/TransactionProcessing/GasConsumed.cs",
  "src/Nethermind/Nethermind.Evm/TransactionSubstate.cs",
  "src/Nethermind/Nethermind.Evm/Tracing/ITxTracer.cs",
  "src/Nethermind/Nethermind.Evm/State/IWorldState.cs",
  "src/Nethermind/Nethermind.Evm/BlockExecutionContext.cs",
  "src/Nethermind/Nethermind.Core/Transaction.cs",
  "src/Nethermind/Nethermind.Core/TransactionExtensions.cs",
  "src/Nethermind/Nethermind.Core/BlockHeader.cs",
  "src/Nethermind/Nethermind.Core/TransactionReceipt.cs",
  "src/Nethermind/Nethermind.Consensus/Processing/TransactionProcessorAdapterExtensions.cs",
  "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.BlockValidationTransactionsExecutor.cs",
  "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.ParallelBlockValidationTransactionsExecutor.cs",
  "src/Nethermind/Nethermind.Consensus/Processing/BlockAccessListManager.cs",
  "src/Nethermind/Nethermind.Consensus/Processing/IBlockAccessListManager.cs",
  "src/Nethermind/Nethermind.Init/Modules/BlockProcessingModule.cs",
  "src/Nethermind/Nethermind.Blockchain/Tracing/BlockReceiptsTracer.cs",
  "src/Nethermind/Nethermind.Core/Specs/IReleaseSpec.cs",
  "src/Nethermind/Nethermind.Specs/Forks/25_Amsterdam.cs",
  "src/Nethermind/Nethermind.Specs/ReleaseSpec.cs",
  "src/Nethermind/Nethermind.Core/Specs/ISpecProvider.cs",
  "src/Nethermind/Nethermind.Specs/ChainSpecStyle/ChainSpecBasedSpecProvider.cs",
  "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.cs",
  "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.std.cs",
  "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.BlockAccessListSystemContractHandler.cs",
  "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.SystemContractHandler.cs",
  "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.BlockProductionTransactionPicker.cs",
  "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.BlockProductionTransactionsExecutor.cs",
  "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.IBlockProductionTransactionPicker.cs",
  "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.IBlockProductionTransactionsExecutor.cs",
  "src/Nethermind/Nethermind.Consensus/Processing/BlockAccessListManager.StateChanges.cs",
  "src/Nethermind/Nethermind.Consensus/Processing/BlockAccessListManager.SystemContracts.cs",
  "src/Nethermind/Nethermind.Consensus/Processing/BlockAccessListManager.TxProcessorPool.cs",
  "src/Nethermind/Nethermind.Consensus/Processing/BlockAccessListManager.Validation.cs"
]

def dependencyPaths : List String := [
  "tools/Evm/Lean/OrdinaryStaticAdmissionExtractor/Generated/OrdinaryStaticAdmissionKernel.ir.json",
  "tools/Evm/Lean/OrdinaryStaticAdmissionExtractor/Generated/OrdinaryStaticAdmissionKernel.lean",
  "tools/Evm/Lean/OrdinaryStaticAdmissionExtractor/Generated/OrdinaryStaticAdmissionKernel.source-manifest.json",
  "tools/Evm/Lean/OrdinaryStatefulAdmissionPrefixExtractor/Generated/OrdinaryStatefulAdmissionPrefix.ir.json",
  "tools/Evm/Lean/OrdinaryStatefulAdmissionPrefixExtractor/Generated/OrdinaryStatefulAdmissionPrefix.lean",
  "tools/Evm/Lean/OrdinaryStatefulAdmissionPrefixExtractor/Generated/OrdinaryStatefulAdmissionPrefix.source-manifest.json",
  "tools/Evm/Lean/OrdinaryPostNonceDispatchExtractor/Generated/OrdinaryPostNonceDispatch.ir.json",
  "tools/Evm/Lean/OrdinaryPostNonceDispatchExtractor/Generated/OrdinaryPostNonceDispatch.lean",
  "tools/Evm/Lean/OrdinaryPostNonceDispatchExtractor/Generated/OrdinaryPostNonceDispatch.source-manifest.json",
  "tools/Evm/Lean/SimpleTransferCompletionExtractor/Generated/SimpleTransferCompletion.ir.json",
  "tools/Evm/Lean/SimpleTransferCompletionExtractor/Generated/SimpleTransferCompletion.lean",
  "tools/Evm/Lean/SimpleTransferCompletionExtractor/Generated/SimpleTransferCompletion.source-manifest.json",
  "tools/Evm/Lean/ReceiptTerminalFoldExtractor/Generated/ReceiptTerminalFoldKernel.ir.json",
  "tools/Evm/Lean/ReceiptTerminalFoldExtractor/Generated/ReceiptTerminalFoldKernel.lean",
  "tools/Evm/Lean/ReceiptTerminalFoldExtractor/Generated/ReceiptTerminalFoldKernel.source-manifest.json",
  "tools/Evm/Lean/ReceiptTerminalFoldExtractor/SOURCE_PINS.json",
  "tools/Evm/Lean/ReceiptTerminalFoldExtractor/COMPILER_REFERENCE_PINS.json"
]

def sourceAnchors : List String := [
  "context.reset.executionGas", "context.reset.stateGas", "context.install",
  "process.toExecuteCore", "route.systemGuard", "route.ordinaryExecute",
  "admission.recoverBeforeIntrinsic", "admission.intrinsic", "admission.contextHandoff",
  "admission.staticValidation", "admission.senderValidation", "admission.buyGas",
  "admission.incrementNonce", "dispatch.prepareFastPath", "dispatch.precommit",
  "dispatch.availableGas", "dispatch.simpleTransfer", "dispatch.evmExcluded",
  "simple.stateCharge", "simple.payValue", "simple.recipientWrite", "simple.oogForfeit",
  "simple.refund", "simple.access", "simple.headerAndFees", "simple.finalize",
  "fees.counterGuard", "fees.payFees", "finalize.commit", "finalize.receiptFailure",
  "finalize.receiptSuccess", "finalize.resultReturn", "adapter.startTxTrace",
  "adapter.execute", "adapter.endTxTrace", "executor.adapterCall", "executor.invalidResultGuard",
  "executor.invalidResultThrow", "executor.processedEvent", "executor.validationMode",
  "executor.processCall", "executor.blockGasLimitGuard", "executor.gasLimitThrow",
  "bal.directInnerGuard", "bal.directInnerCall",
  "tracer.blockReset.index", "tracer.blockReset.currentTx", "tracer.blockReset.tracer",
  "tracer.blockReset.receipts", "tracer.blockReset.receiptGas", "tracer.receiptAppend",
  "tracer.delegateSuccess", "tracer.currentSuccess", "tracer.gasUpdate",
  "tracer.endTx.indexIncrement", "tracer.txStart.currentTx", "tracer.txStart.tracer",
  "block.startTrace", "block.preCommit", "block.fold", "block.transactionsExecuted",
  "block.postCommit", "block.balPrepareCall", "block.processBlockCall", "di.processor",
  "di.worldState", "di.blockProcessor", "di.directExecutor", "di.parallelDecorator",
  "bal.enabledMember", "bal.enabledDerivation", "route.ethereumProcessorType",
  "route.ethereumBaseType", "route.genericBaseType", "fork.amsterdam", "route.systemPredicate",
  "options.commit"
]

def semanticOperationIds : List String := [
  "context.reset.executionGas", "context.reset.stateGas", "context.install",
  "process.toExecuteCore", "route.systemGuard", "route.ordinaryExecute",
  "admission.recoverBeforeIntrinsic", "admission.intrinsic", "admission.contextHandoff",
  "admission.staticValidation", "admission.senderValidation", "admission.buyGas",
  "admission.incrementNonce", "dispatch.prepareFastPath", "dispatch.precommit",
  "dispatch.availableGas", "dispatch.simpleTransfer", "dispatch.evmExcluded",
  "simple.stateCharge", "simple.payValue", "simple.recipientWrite", "simple.oogForfeit",
  "simple.refund", "simple.access", "simple.headerAndFees", "simple.finalize",
  "fees.counterGuard", "fees.payFees", "finalize.commit", "finalize.receiptFailure",
  "finalize.receiptSuccess", "finalize.resultReturn", "adapter.startTxTrace",
  "adapter.execute", "adapter.endTxTrace", "executor.adapterCall", "executor.invalidResultGuard",
  "executor.invalidResultThrow", "executor.processedEvent", "executor.validationMode",
  "executor.processCall", "executor.blockGasLimitGuard", "executor.gasLimitThrow",
  "bal.directInnerGuard", "bal.directInnerCall", "tracer.blockReset.index",
  "tracer.blockReset.currentTx", "tracer.blockReset.tracer", "tracer.blockReset.receipts",
  "tracer.blockReset.receiptGas", "tracer.receiptAppend", "tracer.delegateSuccess",
  "tracer.currentSuccess", "tracer.gasUpdate", "tracer.endTx.indexIncrement",
  "tracer.txStart.currentTx", "tracer.txStart.tracer", "block.startTrace", "block.preCommit",
  "block.fold", "block.transactionsExecuted", "block.postCommit", "block.balPrepareCall",
  "block.processBlockCall", "di.processor", "di.worldState", "di.blockProcessor",
  "di.directExecutor", "di.parallelDecorator", "bal.enabledMember", "bal.enabledDerivation",
  "route.ethereumProcessorType", "route.ethereumBaseType", "route.genericBaseType",
  "fork.amsterdam", "route.systemPredicate", "options.commit"
]

def fieldwiseSeamIds : List String := [
  "tx.sender", "tx.recipient", "tx.value", "tx.nonce", "tx.type", "tx.gasLimit", "tx.fees",
  "header.gasUsed", "gas.spent", "gas.operation", "gas.block", "gas.state", "gas.maxUsed",
  "gas.refund", "substate.output", "substate.logs", "substate.error", "substate.exception",
  "receipt.status", "receipt.gas", "receipt.index", "receipt.recipient", "receipt.logs",
  "result.constructor", "result.exception", "result.substateError"
]

def sourceStages : List String := [
  "SetBlockExecutionContext", "Process -> ExecuteCore -> ordinary Execute",
  "static/stateful admission and IncrementNonce", "PrepareSimpleTransferFastPath",
  "pre-execution Commit", "CalculateAvailableGas", "ExecuteSimpleTransfer",
  "Refund -> UpdateHeaderGasUsedAndPayFees -> FinalizeTransaction",
  "base BlockReceiptsTracer receipt terminal", "TransactionsExecuted -> post-transaction CommitState"
]

def excludedBranches : List String := [
  "ExecuteEvmTransaction", "CREATE", "BuildUp", "Restore", "SkipValidation system route",
  "BAL-enabled", "parallel", "invalid-result poststate", "roots/trie/RLP/hash", "persistence/CLR"
]

structure Block where
  terminal : TerminalBlock
  headerGasUsed : Nat
  standardMainnet : Bool
  chainId : Nat
  forkNumber : Nat
  deriving DecidableEq, Repr

structure TracerSeed where
  receipts : List TerminalReceipt
  gasHistory : List TerminalGasTotals
  cumulativeReceiptGas : Nat
  headerGasUsed : Nat
  currentIndex : Nat
  deriving DecidableEq, Repr

structure MachineInput where
  block : Block
  optionsRaw : Nat
  isSystem : Bool
  parallel : Bool
  balEnabled : Bool
  isCreate : Bool
  isCodeOverridable : Bool
  hasAuthorizationList : Bool
  forceSimpleTransferDisabled : Bool
  recipientLive : Bool
  recipientHasCode : Bool
  recipientHasDelegation : Bool
  tracerSeed : TracerSeed
  deriving DecidableEq, Repr

structure EntryNormality where
  startNewTxTrace : Bool
  execute : Bool
  endTxTrace : Bool
  deriving DecidableEq, Repr

def EntryNormality.valid (entry : EntryNormality) : Bool :=
  entry.startNewTxTrace && entry.execute && entry.endTxTrace

structure NormalReturnPremises where
  setBlockExecutionContext : Bool
  process : Bool
  executeCore : Bool
  ordinaryExecute : Bool
  staticAdmission : Bool
  statefulAdmission : Bool
  nonceAndPreparation : Bool
  preExecutionCommit : Bool
  availableGas : Bool
  executeSimpleTransfer : Bool
  settlement : Bool
  transactionCommit : Bool
  finalizeTransaction : Bool
  executorReturn : Bool
  transactionsExecutedSubscriber : Bool
  postTransactionCommitState : Bool
  receiptTerminal : Bool
  entries : List EntryNormality
  deriving DecidableEq, Repr

def NormalReturnPremises.all (premises : NormalReturnPremises) : Bool :=
  premises.setBlockExecutionContext && premises.process && premises.executeCore &&
  premises.ordinaryExecute && premises.staticAdmission && premises.statefulAdmission &&
  premises.nonceAndPreparation && premises.preExecutionCommit && premises.availableGas &&
  premises.executeSimpleTransfer && premises.settlement && premises.transactionCommit &&
  premises.finalizeTransaction && premises.executorReturn &&
  premises.transactionsExecutedSubscriber && premises.postTransactionCommitState &&
  premises.receiptTerminal

structure SettledEntry where
  transaction : TerminalTransaction
  receipt : TerminalReceipt
  gasTotals : TerminalGasTotals
  recipient : TerminalAddress
  gas : TerminalGas
  output : TerminalBytes
  logs : List TerminalLog
  stateRoot : Option TerminalHash
  status : TerminalStatus
  result : TerminalResult
  nestedTracer : Bool
  currentTxTracerIsTracingReceipt : Bool
  wellFormed : Bool
  deriving DecidableEq, Repr

def NormalReturnPremises.validFor (premises : NormalReturnPremises)
    (entries : List SettledEntry) : Bool :=
  premises.all && (premises.entries.length == entries.length) &&
  (premises.entries.map EntryNormality.valid).all (fun value => value)

def resultIsOk : TerminalResult → Bool
  | .ok => true
  | .evmException _ _ => false

def statusIsSuccess : TerminalStatus → Bool
  | .success => true
  | .failure => false

def onlyOkTerminal (entry : SettledEntry) : Bool :=
  entry.wellFormed && resultIsOk entry.result && statusIsSuccess entry.status

def lastReceipt : List TerminalReceipt → Option TerminalReceipt
  | [] => none
  | [last] => some last
  | _ :: tail => lastReceipt tail

def lastGasTotals : List TerminalGasTotals → Option TerminalGasTotals
  | [] => none
  | [last] => some last
  | _ :: tail => lastGasTotals tail

def receiptObservationMatches (actual expected : TerminalReceipt) : Bool :=
  actual.logs == expected.logs && actual.txType == expected.txType &&
  actual.gasUsedTotal == expected.gasUsedTotal && actual.statusCode == expected.statusCode &&
  actual.recipient == expected.recipient && actual.blockHash == expected.blockHash &&
  actual.blockNumber == expected.blockNumber && actual.index == expected.index &&
  actual.gasUsed == expected.gasUsed &&
  actual.effectiveGasPrice == expected.effectiveGasPrice && actual.sender == expected.sender &&
  actual.contractAddress == expected.contractAddress && actual.txHash == expected.txHash &&
  actual.postTransactionState == expected.postTransactionState &&
  actual.blockGasUsed == expected.blockGasUsed && actual.executionGasUsed == expected.executionGasUsed &&
  actual.storageGasUsed == expected.storageGasUsed && actual.error == expected.error

def gasTotalsObservationMatches (actual expected : TerminalGasTotals) : Bool :=
  actual.executionGas == expected.executionGas && actual.stateGas == expected.stateGas

def freshSequentialTracer (seed : TracerSeed) (headerGasUsed : Nat) : TerminalState :=
  { receipts := []
    gasHistory := []
    cumulativeReceiptGas := 0
    headerGasUsed := headerGasUsed
    parallel := false
    currentIndex := 0 }

def startNewBlockTrace (input : MachineInput) : TerminalState :=
  freshSequentialTracer input.tracerSeed input.block.headerGasUsed

def freshTracerInvariant (input : MachineInput) (state : TerminalState) : Prop :=
  state.parallel = false ∧ state.currentIndex = 0 ∧ state.receipts = [] ∧
  state.gasHistory = [] ∧ state.cumulativeReceiptGas = 0 ∧
  state.headerGasUsed = input.block.headerGasUsed

def routeValid (input : MachineInput) : Bool :=
  input.optionsRaw == 1 && input.block.standardMainnet && input.block.chainId == 1 &&
  input.block.forkNumber == 25 && !input.isSystem && !input.parallel && !input.balEnabled &&
  !input.isCreate && !input.isCodeOverridable && !input.hasAuthorizationList &&
  !input.forceSimpleTransferDisabled && input.recipientLive && !input.recipientHasCode &&
  !input.recipientHasDelegation

inductive Event where
  | blockStart
  | preTransactionCommit
  | foldStart
  | startNewTxTrace
  | execute
  | transactionCommit
  | gasMutation
  | receiptAppend
  | nestedTracerForward
  | currentTracerForward
  | endTxTrace
  | currentIndexIncrement
  | foldReturn
  | transactionsExecuted
  | postTransactionCommit
  deriving DecidableEq, Repr

def mapTerminalEvent : ReceiptTerminalFoldExtractor.Generated.ReceiptTerminalFoldKernel.Event → Event
  | .gasMutation => .gasMutation
  | .receiptAppend => .receiptAppend
  | .nestedTracerForward => .nestedTracerForward
  | .currentTracerForward => .currentTracerForward

def terminalForwardingEvents (entry : SettledEntry) : List Event :=
  (ReceiptTerminalFoldExtractor.Generated.ReceiptTerminalFoldKernel.forwardingEvents
    entry.nestedTracer entry.currentTxTracerIsTracingReceipt).map mapTerminalEvent

inductive RejectReason where
  | route
  | normalReturn
  | malformedSettled
  | invalidResult
  | exceptionResult
  deriving DecidableEq, Repr

inductive Outcome where
  | rejected (reason : RejectReason) (state : TerminalState) (events : List Event)
      (processed : List TerminalResult)
  | completed (state : TerminalState) (events : List Event)
      (results : List TerminalResult)
  deriving DecidableEq, Repr

def foldEntries (block : Block) (state : TerminalState) (events : List Event)
    (processed : List TerminalResult) : List SettledEntry → Outcome
  | [] => .completed state events processed
  | entry :: tail =>
      if !entry.wellFormed then
        .rejected .malformedSettled state events processed
      else if !resultIsOk entry.result then
        .rejected (match entry.result with
          | .ok => .invalidResult
          | .evmException _ _ => .exceptionResult) state events processed
      else if !statusIsSuccess entry.status then
        .rejected .malformedSettled state events processed
      else
        let terminal :=
          ReceiptTerminalFoldExtractor.Generated.ReceiptTerminalFoldKernel.markAsSuccess
            state block.terminal entry.transaction entry.recipient entry.gas entry.output
            entry.logs entry.stateRoot entry.nestedTracer entry.currentTxTracerIsTracingReceipt
        let nextState := { terminal.state with currentIndex := state.currentIndex + 1 }
        match lastReceipt terminal.state.receipts, lastGasTotals terminal.state.gasHistory with
        | some receipt, some totals =>
            if !receiptObservationMatches receipt entry.receipt ||
                !gasTotalsObservationMatches totals entry.gasTotals then
              .rejected .malformedSettled state events processed
            else
              let nextEvents := events ++ [Event.startNewTxTrace, Event.execute, Event.transactionCommit] ++
                terminal.events.map mapTerminalEvent ++ [Event.endTxTrace, Event.currentIndexIncrement]
              foldEntries block nextState nextEvents (processed ++ [entry.result]) tail
        | _, _ => .rejected .malformedSettled state events processed

def run (input : MachineInput) (premises : NormalReturnPremises)
    (entries : List SettledEntry) : Outcome :=
  let initial := startNewBlockTrace input
  let initialEvents := [Event.blockStart, Event.preTransactionCommit, Event.foldStart]
  if !routeValid input then
    .rejected .route initial initialEvents []
  else if !premises.validFor entries then
    .rejected .normalReturn initial initialEvents []
  else
    match foldEntries input.block initial initialEvents [] entries with
    | .rejected reason state events processed =>
        .rejected reason state (events ++ [Event.foldReturn]) processed
    | .completed state events results =>
        .completed state (events ++ [Event.foldReturn, Event.transactionsExecuted,
          Event.postTransactionCommit]) results

def receiptIndices (state : TerminalState) : List Nat :=
  state.receipts.map (fun receipt => receipt.index)

def receiptGasTotals (state : TerminalState) : List Nat :=
  state.receipts.map (fun receipt => receipt.gasUsedTotal)

def sequentialIndexInvariant (state : TerminalState) : Prop :=
  state.receiptIndices = List.range state.receipts.length

def gasHistoryLengthsMatch (state : TerminalState) : Prop :=
  state.receipts.length = state.gasHistory.length

def onlyOkSuccess (input : MachineInput) (premises : NormalReturnPremises)
    (entries : List SettledEntry) : Prop :=
  routeValid input = true ∧ premises.validFor entries = true ∧
  entries.all onlyOkTerminal = true

end OrdinaryTransactionMachineExtractor.Generated
