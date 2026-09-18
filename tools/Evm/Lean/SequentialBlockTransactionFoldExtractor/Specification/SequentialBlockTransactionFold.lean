-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import SequentialBlockTransactionFoldExtractor.Generated.SequentialBlockTransactionFold

namespace SequentialBlockTransactionFoldExtractor.Specification.SequentialBlockTransactionFold

namespace Receipt
abbrev State := ReceiptTerminalFoldExtractor.Generated.ReceiptTerminalFoldKernel.State
abbrev FinalizationObservation := ReceiptTerminalFoldExtractor.Generated.ReceiptTerminalFoldKernel.FinalizationObservation
abbrev TransactionResult := ReceiptTerminalFoldExtractor.Generated.ReceiptTerminalFoldKernel.TransactionResult
end Receipt

/-!
This module is a declarative relation over the generated boundary. It describes admissible
prefix traces and terminal states without defining a second executable transaction fold.
-/

abbrev FailureReason :=
  SequentialBlockTransactionFoldExtractor.Generated.SequentialBlockTransactionFold.FailureReason
abbrev OutcomeKind :=
  SequentialBlockTransactionFoldExtractor.Generated.SequentialBlockTransactionFold.OutcomeKind
abbrev FoldEvent :=
  SequentialBlockTransactionFoldExtractor.Generated.SequentialBlockTransactionFold.FoldEvent
abbrev SettledTransaction :=
  SequentialBlockTransactionFoldExtractor.Generated.SequentialBlockTransactionFold.SettledTransaction
abbrev TransactionEntry :=
  SequentialBlockTransactionFoldExtractor.Generated.SequentialBlockTransactionFold.TransactionEntry
abbrev FoldInput :=
  SequentialBlockTransactionFoldExtractor.Generated.SequentialBlockTransactionFold.FoldInput
abbrev FoldResult :=
  SequentialBlockTransactionFoldExtractor.Generated.SequentialBlockTransactionFold.FoldResult

def ordinary (isStandard isSystem : Bool) : Bool :=
  isStandard && !isSystem

def terminalObservation (start : Receipt.State)
    (transaction : SettledTransaction) : Prop :=
  let terminal := transaction.observation.trace.state
  start.parallel = false ∧
  transaction.observation.result = .ok ∧
  transaction.startState = start ∧
  transaction.index = start.currentIndex ∧
  terminal.currentIndex = start.currentIndex ∧
  terminal.receipts.take start.receipts.length = start.receipts ∧
  terminal.gasHistory.take start.gasHistory.length = start.gasHistory ∧
  terminal.receipts.length = start.receipts.length + 1 ∧
  terminal.gasHistory.length = start.gasHistory.length + 1 ∧
  terminal.gasHistory.length = terminal.receipts.length ∧
  terminal.receipts.getLast?.map (fun receipt => receipt.index) = some transaction.index ∧
  terminal.receipts.getLast?.map (fun receipt => receipt.gasUsedTotal) =
    some terminal.cumulativeReceiptGas ∧
  terminal.parallel = false

def afterTxTrace (state : Receipt.State) : Receipt.State :=
  { state with currentIndex := state.currentIndex + 1 }

def transactionEvents (events : List FoldEvent) (index : Nat) : List FoldEvent :=
  events ++ [.txTraceStarted index, .terminalObserved index, .txTraceEnded index,
    .gasLimitChecked index]

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

/-!
`FoldRelation` is the specification boundary. Its constructors describe the ordered decision
points of the source fold; the recursive constructor carries only the next trace state and the
remaining entries, so no executable reference fold is hidden in this module.
-/
inductive FoldRelation (input : FoldInput) :
    Receipt.State → List Receipt.TransactionResult → List Nat → List FoldEvent →
    List TransactionEntry → FoldResult → Prop
| emptyBal (state : Receipt.State) (results : List Receipt.TransactionResult)
    (completed : List Nat) (events : List FoldEvent)
    (hBal : input.balEnabled = true) :
    FoldRelation input state results completed events []
      (unsupportedResult state results completed events .balDecoratorActive)
| emptyParallel (state : Receipt.State) (results : List Receipt.TransactionResult)
    (completed : List Nat) (events : List FoldEvent)
    (hBal : input.balEnabled = false)
    (hParallel : (input.parallel || state.parallel) = true) :
    FoldRelation input state results completed events []
      (unsupportedResult state results completed events .parallelExecution)
| emptyCompleted (state : Receipt.State) (results : List Receipt.TransactionResult)
    (completed : List Nat) (events : List FoldEvent)
    (hBal : input.balEnabled = false)
    (hParallel : (input.parallel || state.parallel) = false) :
    FoldRelation input state results completed events []
      (completedResult state results completed events)
| invalidBal (state : Receipt.State) (results : List Receipt.TransactionResult)
    (completed : List Nat) (events : List FoldEvent)
    (index : Nat) (isStandard isSystem : Bool) (rest : List TransactionEntry)
    (hBal : input.balEnabled = true) :
    FoldRelation input state results completed events
      (.invalid index isStandard isSystem :: rest)
      (unsupportedResult state results completed events .balDecoratorActive)
| invalidParallel (state : Receipt.State) (results : List Receipt.TransactionResult)
    (completed : List Nat) (events : List FoldEvent)
    (index : Nat) (isStandard isSystem : Bool) (rest : List TransactionEntry)
    (hBal : input.balEnabled = false)
    (hParallel : (input.parallel || state.parallel) = true) :
    FoldRelation input state results completed events
      (.invalid index isStandard isSystem :: rest)
      (unsupportedResult state results completed events .parallelExecution)
| invalidIndex (state : Receipt.State) (results : List Receipt.TransactionResult)
    (completed : List Nat) (events : List FoldEvent)
    (index : Nat) (isStandard isSystem : Bool) (rest : List TransactionEntry)
    (hBal : input.balEnabled = false)
    (hParallel : (input.parallel || state.parallel) = false)
    (hIndex : index ≠ completed.length) :
    FoldRelation input state results completed events
      (.invalid index isStandard isSystem :: rest)
      (unsupportedResult state results completed events .indexOrderMismatch)
| invalidUnsupported (state : Receipt.State) (results : List Receipt.TransactionResult)
    (completed : List Nat) (events : List FoldEvent)
    (index : Nat) (isStandard isSystem : Bool) (rest : List TransactionEntry)
    (hBal : input.balEnabled = false)
    (hParallel : (input.parallel || state.parallel) = false)
    (hIndex : index = completed.length)
    (hOrdinary : ordinary isStandard isSystem = false) :
    FoldRelation input state results completed events
      (.invalid index isStandard isSystem :: rest)
      (unsupportedResult state results completed (events ++ [.invalidObserved index])
        (if isSystem then .systemTransaction else .nonStandardTransaction))
| invalidPrefix (state : Receipt.State) (results : List Receipt.TransactionResult)
    (completed : List Nat) (events : List FoldEvent)
    (index : Nat) (isStandard isSystem : Bool) (rest : List TransactionEntry)
    (hBal : input.balEnabled = false)
    (hParallel : (input.parallel || state.parallel) = false)
    (hIndex : index = completed.length)
    (hOrdinary : ordinary isStandard isSystem = true) :
    FoldRelation input state results completed events
      (.invalid index isStandard isSystem :: rest)
      (invalidResult state results completed (events ++ [.invalidObserved index]) .invalidTransaction)
| settledBal (state : Receipt.State) (results : List Receipt.TransactionResult)
    (completed : List Nat) (events : List FoldEvent)
    (transaction : SettledTransaction) (rest : List TransactionEntry)
    (hBal : input.balEnabled = true) :
    FoldRelation input state results completed events
      (.settled transaction :: rest)
      (unsupportedResult state results completed events .balDecoratorActive)
| settledParallel (state : Receipt.State) (results : List Receipt.TransactionResult)
    (completed : List Nat) (events : List FoldEvent)
    (transaction : SettledTransaction) (rest : List TransactionEntry)
    (hBal : input.balEnabled = false)
    (hParallel : (input.parallel || state.parallel) = true) :
    FoldRelation input state results completed events
      (.settled transaction :: rest)
      (unsupportedResult state results completed events .parallelExecution)
| settledIndex (state : Receipt.State) (results : List Receipt.TransactionResult)
    (completed : List Nat) (events : List FoldEvent)
    (transaction : SettledTransaction) (rest : List TransactionEntry)
    (hBal : input.balEnabled = false)
    (hParallel : (input.parallel || state.parallel) = false)
    (hIndex : transaction.index ≠ completed.length) :
    FoldRelation input state results completed events
      (.settled transaction :: rest)
      (unsupportedResult state results completed events .indexOrderMismatch)
| settledUnsupported (state : Receipt.State) (results : List Receipt.TransactionResult)
    (completed : List Nat) (events : List FoldEvent)
    (transaction : SettledTransaction) (rest : List TransactionEntry)
    (hBal : input.balEnabled = false)
    (hParallel : (input.parallel || state.parallel) = false)
    (hIndex : transaction.index = completed.length)
    (hOrdinary : ordinary transaction.isStandard transaction.isSystem = false) :
    FoldRelation input state results completed events
      (.settled transaction :: rest)
      (unsupportedResult state results completed events
        (if transaction.isSystem then .systemTransaction else .nonStandardTransaction))
| settledStateMismatch (state : Receipt.State) (results : List Receipt.TransactionResult)
    (completed : List Nat) (events : List FoldEvent)
    (transaction : SettledTransaction) (rest : List TransactionEntry)
    (hBal : input.balEnabled = false)
    (hParallel : (input.parallel || state.parallel) = false)
    (hIndex : transaction.index = completed.length)
    (hOrdinary : ordinary transaction.isStandard transaction.isSystem = true)
    (hStart : transaction.startState ≠ state) :
    FoldRelation input state results completed events
      (.settled transaction :: rest)
      (unsupportedResult state results completed events .stateChainMismatch)
| settledMalformed (state : Receipt.State) (results : List Receipt.TransactionResult)
    (completed : List Nat) (events : List FoldEvent)
    (transaction : SettledTransaction) (rest : List TransactionEntry)
    (hBal : input.balEnabled = false)
    (hParallel : (input.parallel || state.parallel) = false)
    (hIndex : transaction.index = completed.length)
    (hOrdinary : ordinary transaction.isStandard transaction.isSystem = true)
    (hStart : transaction.startState = state)
    (hObservation : terminalObservation state transaction → False) :
    FoldRelation input state results completed events
      (.settled transaction :: rest)
      (unsupportedResult state results completed events .malformedTerminalObservation)
| settledTerminalParallel (state : Receipt.State) (results : List Receipt.TransactionResult)
    (completed : List Nat) (events : List FoldEvent)
    (transaction : SettledTransaction) (rest : List TransactionEntry)
    (hBal : input.balEnabled = false)
    (hParallel : (input.parallel || state.parallel) = false)
    (hIndex : transaction.index = completed.length)
    (hOrdinary : ordinary transaction.isStandard transaction.isSystem = true)
    (hStart : transaction.startState = state)
    (hObservation : terminalObservation state transaction)
    (hTerminalParallel : transaction.observation.trace.state.parallel = true) :
    FoldRelation input state results completed events
      (.settled transaction :: rest)
      (unsupportedResult state results completed events .parallelExecution)
| settledGasLimit (state : Receipt.State) (results : List Receipt.TransactionResult)
    (completed : List Nat) (events : List FoldEvent)
    (transaction : SettledTransaction) (rest : List TransactionEntry)
    (hBal : input.balEnabled = false)
    (hParallel : (input.parallel || state.parallel) = false)
    (hIndex : transaction.index = completed.length)
    (hOrdinary : ordinary transaction.isStandard transaction.isSystem = true)
    (hStart : transaction.startState = state)
    (hObservation : terminalObservation state transaction)
    (hTerminalParallel : transaction.observation.trace.state.parallel = false)
    (hGas : input.shouldValidate &&
      (afterTxTrace transaction.observation.trace.state).headerGasUsed > input.gasLimit) :
    FoldRelation input state results completed events
      (.settled transaction :: rest)
      (invalidResult (afterTxTrace transaction.observation.trace.state)
        (results ++ [transaction.observation.result])
        (completed ++ [transaction.index]) (transactionEvents events transaction.index)
        .blockGasLimitExceeded)
| settledContinue (state : Receipt.State) (results : List Receipt.TransactionResult)
    (completed : List Nat) (events : List FoldEvent)
    (transaction : SettledTransaction) (rest : List TransactionEntry)
    (hBal : input.balEnabled = false)
    (hParallel : (input.parallel || state.parallel) = false)
    (hIndex : transaction.index = completed.length)
    (hOrdinary : ordinary transaction.isStandard transaction.isSystem = true)
    (hStart : transaction.startState = state)
    (hObservation : terminalObservation state transaction)
    (hTerminalParallel : transaction.observation.trace.state.parallel = false)
    (hGas : ¬(input.shouldValidate &&
      (afterTxTrace transaction.observation.trace.state).headerGasUsed > input.gasLimit))
    (tail : FoldRelation input (afterTxTrace transaction.observation.trace.state)
      (results ++ [transaction.observation.result]) (completed ++ [transaction.index])
      (transactionEvents events transaction.index) rest result) :
    FoldRelation input state results completed events
      (.settled transaction :: rest) result

end SequentialBlockTransactionFoldExtractor.Specification.SequentialBlockTransactionFold
