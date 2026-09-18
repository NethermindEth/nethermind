-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

-- This file is generated. Do not edit.
-- The terminal observations are imported from the settled receipt-terminal package.
-- This module is a conditional model-to-model artifact: it models only the direct-inner
-- sequential block fold under a BAL-disabled premise and its post-fold commit boundary. It does
-- not by itself establish that production C# executed or that a source return has this event tail.

import ReceiptTerminalFoldExtractor.Generated.ReceiptTerminalFoldKernel

namespace SequentialBlockTransactionFoldExtractor.Generated.SequentialBlockTransactionFold

namespace Receipt
abbrev State := ReceiptTerminalFoldExtractor.Generated.ReceiptTerminalFoldKernel.State
abbrev FinalizationObservation := ReceiptTerminalFoldExtractor.Generated.ReceiptTerminalFoldKernel.FinalizationObservation
abbrev TransactionResult := ReceiptTerminalFoldExtractor.Generated.ReceiptTerminalFoldKernel.TransactionResult
end Receipt

inductive FailureReason where
| invalidTransaction
| blockGasLimitExceeded
| nonStandardTransaction
| systemTransaction
| parallelExecution
| stateChainMismatch
| indexOrderMismatch
| malformedTerminalObservation
| balDecoratorActive
  deriving DecidableEq, Repr

inductive OutcomeKind where
| completed
| invalidPrefix
| unsupported
  deriving DecidableEq, Repr

inductive FoldEvent where
| preTransactionCommit
| txTraceStarted (index : Nat)
| terminalObserved (index : Nat)
| txTraceEnded (index : Nat)
| gasLimitChecked (index : Nat)
| invalidObserved (index : Nat)
| transactionFoldCompleted
| transactionsExecuted
| postTransactionCommit
  deriving DecidableEq, Repr

structure SettledTransaction where
  index : Nat
  startState : Receipt.State
  observation : Receipt.FinalizationObservation
  isStandard : Bool
  isSystem : Bool
  deriving DecidableEq, Repr

inductive TransactionEntry where
| settled (transaction : SettledTransaction)
| invalid (index : Nat) (isStandard : Bool) (isSystem : Bool)
  deriving DecidableEq, Repr

structure FoldInput where
  balEnabled : Bool
  parallel : Bool
  shouldValidate : Bool
  gasLimit : Nat
  entries : List TransactionEntry
  deriving DecidableEq, Repr

structure FoldResult where
  outcome : OutcomeKind
  state : Receipt.State
  terminalResults : List Receipt.TransactionResult
  completedIndices : List Nat
  events : List FoldEvent
  failure : Option FailureReason
  committed : Bool
  deriving DecidableEq, Repr

def ordinary (isStandard isSystem : Bool) : Bool :=
  isStandard && !isSystem

def onlyOkTerminal (transaction : SettledTransaction) : Bool :=
  transaction.observation.result == .ok

def terminalObservationValid (start : Receipt.State)
    (transaction : SettledTransaction) : Bool :=
  let terminal := transaction.observation.trace.state
  start.parallel == false &&
  onlyOkTerminal transaction &&
  transaction.startState == start &&
  transaction.index == start.currentIndex &&
  terminal.currentIndex == start.currentIndex &&
  terminal.receipts.take start.receipts.length == start.receipts &&
  terminal.gasHistory.take start.gasHistory.length == start.gasHistory &&
  terminal.receipts.length == start.receipts.length + 1 &&
  terminal.gasHistory.length == start.gasHistory.length + 1 &&
  terminal.gasHistory.length == terminal.receipts.length &&
  terminal.receipts.getLast?.map (fun receipt => receipt.index) == some transaction.index &&
  terminal.receipts.getLast?.map (fun receipt => receipt.gasUsedTotal) ==
    some terminal.cumulativeReceiptGas &&
  terminal.parallel == false

def afterTxTrace (state : Receipt.State) : Receipt.State :=
  { state with currentIndex := state.currentIndex + 1 }

def unsupportedResult (state : Receipt.State) (results : List Receipt.TransactionResult)
    (completed : List Nat) (events : List FoldEvent) (reason : FailureReason) : FoldResult :=
  { outcome := .unsupported
    state := state
    terminalResults := results
    completedIndices := completed
    events := events
    failure := some reason
    committed := false }

def invalidResult (state : Receipt.State) (results : List Receipt.TransactionResult)
    (completed : List Nat) (events : List FoldEvent) (reason : FailureReason) : FoldResult :=
  { outcome := .invalidPrefix
    state := state
    terminalResults := results
    completedIndices := completed
    events := events
    failure := some reason
    committed := false }

def completedResult (state : Receipt.State) (results : List Receipt.TransactionResult)
    (completed : List Nat) (events : List FoldEvent) : FoldResult :=
  { outcome := .completed
    state := state
    terminalResults := results
    completedIndices := completed
    events := events ++ [.transactionFoldCompleted, .transactionsExecuted, .postTransactionCommit]
    failure := none
    committed := true }

def foldEntries (input : FoldInput) (state : Receipt.State)
    (results : List Receipt.TransactionResult) (completed : List Nat)
    (events : List FoldEvent) : List TransactionEntry → FoldResult
| [] =>
    if input.balEnabled then
      unsupportedResult state results completed events .balDecoratorActive
    else if input.parallel || state.parallel then
      unsupportedResult state results completed events .parallelExecution
    else
      completedResult state results completed events
| entry :: rest =>
    match entry with
    | .invalid index isStandard isSystem =>
        if input.balEnabled then
          unsupportedResult state results completed events .balDecoratorActive
        else if input.parallel || state.parallel then
          unsupportedResult state results completed events .parallelExecution
        else if index != completed.length then
          unsupportedResult state results completed events .indexOrderMismatch
        else if !ordinary isStandard isSystem then
          unsupportedResult state results completed (events ++ [.invalidObserved index])
            (if isSystem then .systemTransaction else .nonStandardTransaction)
        else
          invalidResult state results completed (events ++ [.invalidObserved index]) .invalidTransaction
    | .settled transaction =>
        if input.balEnabled then
          unsupportedResult state results completed events .balDecoratorActive
        else if input.parallel || state.parallel then
          unsupportedResult state results completed events .parallelExecution
        else if transaction.index != completed.length then
          unsupportedResult state results completed events .indexOrderMismatch
        else if !ordinary transaction.isStandard transaction.isSystem then
          unsupportedResult state results completed events
            (if transaction.isSystem then .systemTransaction else .nonStandardTransaction)
        else if transaction.startState != state then
          unsupportedResult state results completed events .stateChainMismatch
        else if transaction.observation.trace.state.parallel then
          unsupportedResult state results completed events .parallelExecution
        else if !terminalObservationValid state transaction then
          unsupportedResult state results completed events .malformedTerminalObservation
        else
          let nextState := afterTxTrace transaction.observation.trace.state
          let nextEvents := events ++
            [.txTraceStarted transaction.index, .terminalObserved transaction.index,
              .txTraceEnded transaction.index, .gasLimitChecked transaction.index]
          if input.shouldValidate && nextState.headerGasUsed > input.gasLimit then
            invalidResult nextState (results ++ [transaction.observation.result])
              (completed ++ [transaction.index]) nextEvents .blockGasLimitExceeded
          else
            foldEntries input nextState
              (results ++ [transaction.observation.result])
              (completed ++ [transaction.index]) nextEvents rest

def run (input : FoldInput) (initial : Receipt.State) : FoldResult :=
  foldEntries input initial [] [] [.preTransactionCommit] input.entries

def hasPostTransactionCommit (result : FoldResult) : Bool :=
  result.committed && result.events.any (fun event =>
    match event with
    | .postTransactionCommit => true
    | _ => false)

def receiptCount (result : FoldResult) : Nat := result.state.receipts.length

end SequentialBlockTransactionFoldExtractor.Generated.SequentialBlockTransactionFold
-- Source-bound anchors (canonical Roslyn syntax):
-- executor.metrics-setup: SetupTxTimingMetrics(block)
-- executor.validation-mode: shouldValidate=!processingOptions.ContainsFlag(ProcessingOptions.NoValidation)
-- executor.process-transaction-call: ProcessTransaction(block,currentTx,i,receiptsTracer,processingOptions)
-- executor.gas-limit-guard: shouldValidate&&block.Header.GasUsed>block.Header.GasLimit
-- executor.invalid-result-throw: ThrowInvalidTransactionException(result,block.Header,currentTx,index)
-- executor.transaction-adapter-call: transactionProcessor.ProcessTransaction(currentTx,receiptsTracer,processingOptions,_stateProvider)
-- executor.processed-event: _transactionProcessedEventHandler?.OnTransactionProcessed(newTxProcessedEventArgs(index,currentTx,block.Header,receiptsTracer.TxReceipts[index]))
-- block.transaction-fold-call: _blockTransactionsExecutor.ProcessTransactions(block,options,ReceiptsTracer,token)
-- block.pre-transaction-commit: CommitState(spec)
-- block.post-transaction-commit: CommitState(spec)
-- block.transactions-executed-signal: TransactionsExecuted?.Invoke()

-- Typed source identity digests:
-- executor.processTransactions: d303419cb8db67cd8afa9aea3b06b27f12789307abef342d68cb1985c2825444
-- executor.processTransaction: df003b229164f73b2c15972ce4c0b83214391a73264818a80032e2e7baa44a4f
-- block.processBlock: a5ab61515cd25804ad267b556e0a21686f8f037c2163f16b94fb8874aea96d3b
