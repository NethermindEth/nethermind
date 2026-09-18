-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import SequentialBlockTransactionFoldExtractor.Generated.SequentialBlockTransactionFold
import SequentialBlockTransactionFoldExtractor.Specification.SequentialBlockTransactionFold
import ReceiptTerminalFoldExtractor.Refinement.ReceiptTerminalFold

namespace SequentialBlockTransactionFoldExtractor.Refinement.SequentialBlockTransactionFold

namespace Generated
export SequentialBlockTransactionFoldExtractor.Generated.SequentialBlockTransactionFold
  (FailureReason OutcomeKind FoldEvent SettledTransaction TransactionEntry FoldInput FoldResult
    ordinary terminalObservationValid afterTxTrace foldEntries unsupportedResult invalidResult completedResult run
    onlyOkTerminal hasPostTransactionCommit receiptCount)
end Generated

namespace Spec
export SequentialBlockTransactionFoldExtractor.Specification.SequentialBlockTransactionFold
  (FailureReason OutcomeKind FoldEvent SettledTransaction TransactionEntry FoldInput FoldResult
    ordinary terminalObservation afterTxTrace transactionEvents unsupportedResult invalidResult
    completedResult FoldRelation)
end Spec

namespace Receipt
abbrev State := ReceiptTerminalFoldExtractor.Generated.ReceiptTerminalFoldKernel.State
abbrev TransactionResult :=
  ReceiptTerminalFoldExtractor.Generated.ReceiptTerminalFoldKernel.TransactionResult
end Receipt

def mapInput (input : Generated.FoldInput) : Spec.FoldInput := input

def mapResult (result : Generated.FoldResult) : Spec.FoldResult := result

/-!
`FreshSequentialTracer` is the adapter for the exact-base `BlockReceiptsTracer` block boundary.
It records the reset state rather than silently assuming it: a fresh tracer starts at receipt index
zero, has empty receipt and gas histories, has zero cumulative receipt gas, is sequential, and
relates the projected header gas to the production block header gas used by the guard.
-/
def FreshSequentialTracer (state : Receipt.State) (productionHeaderGasUsed : Nat) : Prop :=
  state.receipts = [] ∧
  state.gasHistory = [] ∧
  state.cumulativeReceiptGas = 0 ∧
  state.parallel = false ∧
  state.currentIndex = 0 ∧
  state.headerGasUsed = productionHeaderGasUsed

def freshSequentialTracer (productionHeaderGasUsed : Nat) : Receipt.State :=
  { receipts := []
    gasHistory := []
    cumulativeReceiptGas := 0
    headerGasUsed := productionHeaderGasUsed
    parallel := false
    currentIndex := 0 }

theorem fresh_sequential_tracer_is_explicit
    (productionHeaderGasUsed : Nat) :
    FreshSequentialTracer (freshSequentialTracer productionHeaderGasUsed) productionHeaderGasUsed := by
  simp [FreshSequentialTracer, freshSequentialTracer]

def OnlyOkTerminal (transaction : Generated.SettledTransaction) : Prop :=
  transaction.observation.result = .ok

def settledPrefix : List Generated.TransactionEntry → List Generated.SettledTransaction
| [] => []
| .settled transaction :: rest => transaction :: settledPrefix rest
| .invalid _ _ _ :: _ => []

def NormalReturnEventTail (result : Generated.FoldResult) : Prop :=
  result.outcome = .completed ∧
  result.failure = none ∧
  result.committed = true ∧
  result.events.reverse.take 3 =
    [.postTransactionCommit, .transactionsExecuted, .transactionFoldCompleted]

/-!
This is the adapter boundary for already-settled receipt-terminal observations. It is a
proposition, rather than an executable fold step: every admitted observation must preserve both
accumulator prefixes, append exactly one receipt/gas total, agree on cumulative receipt gas, and
project the transaction and tracer indices at the same pre-EndTxTrace position.
-/
def ReceiptTerminalObservation (start : Receipt.State)
    (transaction : Generated.SettledTransaction) : Prop :=
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

theorem receipt_terminal_observation_is_spec
    (start : Receipt.State) (transaction : Generated.SettledTransaction)
    (h : ReceiptTerminalObservation start transaction) :
    Spec.terminalObservation start transaction := by
  simpa [ReceiptTerminalObservation, Spec.terminalObservation] using h

theorem receipt_terminal_observation_is_only_ok
    (start : Receipt.State) (transaction : Generated.SettledTransaction)
    (h : ReceiptTerminalObservation start transaction) :
    OnlyOkTerminal transaction := by
  exact h.2.1

def ReceiptTerminalChain (start : Receipt.State)
    (transactions : List Generated.SettledTransaction) : Prop :=
  match transactions with
  | [] => True
  | transaction :: rest =>
      ReceiptTerminalObservation start transaction ∧
      ReceiptTerminalChain (Generated.afterTxTrace transaction.observation.trace.state) rest

/-- The observation that an independently defined C# execution relation must supply. -/
structure SourceObservation where
  input : Generated.FoldInput
  initial : Receipt.State
  productionHeaderGasUsed : Nat
  result : Spec.FoldResult

/-- Unproved: production execution, runtime route selection and normal hook returns are not
established by the source extractor or the conditional model theorem below. `executes` must be
an independent execution relation, not an equality to `Generated.run`. -/
def OpenSourceCompositionObligation (Execution : Type)
    (executes : Execution → SourceObservation → Prop) : Prop :=
  ∀ execution observation, executes execution observation →
    FreshSequentialTracer observation.initial observation.productionHeaderGasUsed ∧
    observation.input.balEnabled = false ∧
    observation.input.parallel = false ∧
    ReceiptTerminalChain observation.initial (settledPrefix observation.input.entries) ∧
    Spec.FoldRelation observation.input observation.initial [] [] [.preTransactionCommit]
      observation.input.entries observation.result

theorem terminal_observation_admits_generated_guard
    (start : Receipt.State) (transaction : Generated.SettledTransaction)
    (h : ReceiptTerminalObservation start transaction) :
    Generated.terminalObservationValid start transaction = true := by
  simpa [ReceiptTerminalObservation, Generated.terminalObservationValid,
    Generated.onlyOkTerminal, and_assoc] using h

@[local simp] private theorem unsupported_result_eq (state : Receipt.State)
    (results : List Receipt.TransactionResult) (completed : List Nat)
    (events : List Generated.FoldEvent) (reason : Generated.FailureReason) :
    Spec.unsupportedResult state results completed events reason =
      Generated.unsupportedResult state results completed events reason := rfl

@[local simp] private theorem invalid_result_eq (state : Receipt.State)
    (results : List Receipt.TransactionResult) (completed : List Nat)
    (events : List Generated.FoldEvent) (reason : Generated.FailureReason) :
    Spec.invalidResult state results completed events reason =
      Generated.invalidResult state results completed events reason := rfl

@[local simp] private theorem completed_result_eq (state : Receipt.State)
    (results : List Receipt.TransactionResult) (completed : List Nat)
    (events : List Generated.FoldEvent) :
    Spec.completedResult state results completed events =
      Generated.completedResult state results completed events := rfl

@[local simp] private theorem ordinary_eq (isStandard isSystem : Bool) :
    Spec.ordinary isStandard isSystem = Generated.ordinary isStandard isSystem := rfl

@[local simp] private theorem after_tx_trace_eq (state : Receipt.State) :
    Spec.afterTxTrace state = Generated.afterTxTrace state := rfl

open SequentialBlockTransactionFoldExtractor.Specification.SequentialBlockTransactionFold (FoldRelation) in
private theorem generated_fold_relation
    (input : Generated.FoldInput) (initial : Receipt.State)
    (results : List Receipt.TransactionResult)
    (completed : List Nat)
    (events : List Generated.FoldEvent)
    (entries : List Generated.TransactionEntry)
    (hChain : ReceiptTerminalChain initial (settledPrefix entries)) :
    Spec.FoldRelation input initial results completed events entries
      (Generated.foldEntries input initial results completed events entries) := by
  induction entries generalizing input initial results completed events with
  | nil =>
      cases hBal : input.balEnabled with
      | true =>
          simpa [Generated.foldEntries, hBal] using
            (FoldRelation.emptyBal (input := input) initial results completed events hBal)
      | false =>
          by_cases hParallel : (input.parallel || initial.parallel) = true
          · simpa [Generated.foldEntries, hBal, hParallel] using
              (FoldRelation.emptyParallel (input := input) initial results completed events hBal hParallel)
          · simpa [Generated.foldEntries, hBal, hParallel] using
              (FoldRelation.emptyCompleted (input := input) initial results completed events hBal
                (by simpa only [Bool.not_eq_true] using hParallel))
  | cons entry rest ih =>
      cases entry with
      | invalid index isStandard isSystem =>
          cases hBal : input.balEnabled with
          | true =>
              simpa [Generated.foldEntries, hBal] using
                (FoldRelation.invalidBal (input := input) initial results completed events
                  index isStandard isSystem rest hBal)
          | false =>
              by_cases hParallel : (input.parallel || initial.parallel) = true
              · simpa [Generated.foldEntries, hBal, hParallel] using
                  (FoldRelation.invalidParallel (input := input) initial results completed events
                    index isStandard isSystem rest hBal hParallel)
              · by_cases hIndex : index = completed.length
                · by_cases hOrdinary : Generated.ordinary isStandard isSystem
                  · simpa [Generated.foldEntries, hBal, hParallel, hIndex, hOrdinary] using
                      (FoldRelation.invalidPrefix (input := input) initial results completed events
                        index isStandard isSystem rest hBal
                        (by simpa only [Bool.not_eq_true] using hParallel) hIndex hOrdinary)
                  · simpa [Generated.foldEntries, hBal, hParallel, hIndex, hOrdinary] using
                      (FoldRelation.invalidUnsupported (input := input) initial results completed events
                        index isStandard isSystem rest hBal
                        (by simpa only [Bool.not_eq_true] using hParallel) hIndex
                        (by simpa only [ordinary_eq, Bool.not_eq_true] using hOrdinary))
                · simpa [Generated.foldEntries, hBal, hParallel, hIndex] using
                    (FoldRelation.invalidIndex (input := input) initial results completed events
                      index isStandard isSystem rest hBal (by simpa only [Bool.not_eq_true] using hParallel) hIndex)
      | settled transaction =>
          cases hBal : input.balEnabled with
          | true =>
              simpa [Generated.foldEntries, hBal] using
                (FoldRelation.settledBal (input := input) initial results completed events transaction rest hBal)
          | false =>
              by_cases hParallel : (input.parallel || initial.parallel) = true
              · simpa [Generated.foldEntries, hBal, hParallel] using
                  (FoldRelation.settledParallel (input := input) initial results completed events
                    transaction rest hBal hParallel)
              · by_cases hIndex : transaction.index = completed.length
                · by_cases hOrdinary : Generated.ordinary transaction.isStandard transaction.isSystem
                  · by_cases hStart : transaction.startState = initial
                    · have hChainObservation : ReceiptTerminalObservation initial transaction := hChain.1
                      have hSpecObservation : Spec.terminalObservation initial transaction :=
                        receipt_terminal_observation_is_spec initial transaction hChainObservation
                      have hAdmitted : Generated.terminalObservationValid initial transaction = true :=
                        terminal_observation_admits_generated_guard initial transaction hChainObservation
                      by_cases hTerminalParallel : transaction.observation.trace.state.parallel = true
                      · have : False := by
                          simp [Generated.terminalObservationValid, hTerminalParallel] at hAdmitted
                        exact this.elim
                      · by_cases hGas : input.shouldValidate &&
                            (Generated.afterTxTrace transaction.observation.trace.state).headerGasUsed > input.gasLimit
                        · simpa [Generated.foldEntries, Spec.transactionEvents,
                            hBal, hParallel, hIndex, hOrdinary, hStart, hAdmitted,
                            hTerminalParallel, hGas] using
                            (FoldRelation.settledGasLimit (input := input) initial results completed events
                              transaction rest hBal (by simpa only [Bool.not_eq_true] using hParallel)
                              hIndex hOrdinary hStart
                              hSpecObservation (by simpa only [Bool.not_eq_true] using hTerminalParallel) hGas)
                        · have hTail : ReceiptTerminalChain
                              (Generated.afterTxTrace transaction.observation.trace.state)
                              (settledPrefix rest) := hChain.2
                          have hRelation := ih input
                            (Generated.afterTxTrace transaction.observation.trace.state)
                            (results ++ [transaction.observation.result])
                            (completed ++ [transaction.index])
                            (Spec.transactionEvents events transaction.index) hTail
                          simpa [Generated.foldEntries, Spec.transactionEvents,
                              hBal, hParallel, hIndex, hOrdinary, hStart, hAdmitted,
                              hTerminalParallel, hGas] using
                            (FoldRelation.settledContinue (input := input) initial results completed events
                                transaction rest hBal (by simpa only [Bool.not_eq_true] using hParallel)
                                hIndex hOrdinary hStart
                                hSpecObservation (by simpa only [Bool.not_eq_true] using hTerminalParallel) hGas hRelation)
                    · simpa [Generated.foldEntries, hBal, hParallel, hIndex, hOrdinary, hStart] using
                        (FoldRelation.settledStateMismatch (input := input) initial results completed events
                          transaction rest hBal (by simpa only [Bool.not_eq_true] using hParallel) hIndex hOrdinary hStart)
                  · simpa [Generated.foldEntries, hBal, hParallel, hIndex, hOrdinary] using
                      (FoldRelation.settledUnsupported (input := input) initial results completed events
                        transaction rest hBal (by simpa only [Bool.not_eq_true] using hParallel) hIndex
                        (by simpa only [ordinary_eq, Bool.not_eq_true] using hOrdinary))
                · simpa [Generated.foldEntries, hBal, hParallel, hIndex] using
                    (FoldRelation.settledIndex (input := input) initial results completed events
                      transaction rest hBal (by simpa only [Bool.not_eq_true] using hParallel) hIndex)

theorem completed_result_has_normal_event_tail
    (state : Receipt.State) (results : List Receipt.TransactionResult)
    (completed : List Nat) (events : List Generated.FoldEvent) :
    NormalReturnEventTail (Generated.completedResult state results completed events) := by
  simp [NormalReturnEventTail, Generated.completedResult, List.reverse_append]

theorem generated_fold_refines_spec
    (input : Generated.FoldInput) (initial : Receipt.State)
    (hChain : ReceiptTerminalChain initial (settledPrefix input.entries)) :
    Spec.FoldRelation (mapInput input) initial [] [] [.preTransactionCommit] input.entries
        (mapResult (Generated.run input initial)) ∧
      (∀ state results completed events,
        Generated.run input initial = Generated.completedResult state results completed events →
          NormalReturnEventTail (Generated.run input initial)) := by
  constructor
  simpa [Generated.run, mapInput, mapResult] using
    generated_fold_relation input initial [] [] [.preTransactionCommit] input.entries hChain
  intro state results completed events hResult
  simpa [hResult] using completed_result_has_normal_event_tail state results completed events

theorem malformed_ok_entry_is_rejected_without_history_change
    (input : Generated.FoldInput) (initial : Receipt.State)
    (transaction : Generated.SettledTransaction) (rest : List Generated.TransactionEntry)
    (events : List Generated.FoldEvent)
    (hBal : input.balEnabled = false)
    (hInputParallel : input.parallel = false)
    (hInitialParallel : initial.parallel = false)
    (hIndex : transaction.index = 0)
    (hOrdinary : Generated.ordinary transaction.isStandard transaction.isSystem = true)
    (hStart : transaction.startState = initial)
    (hTerminalParallel : transaction.observation.trace.state.parallel = false)
    (hMalformed : Generated.terminalObservationValid initial transaction = false)
    (_hOnlyOk : OnlyOkTerminal transaction) :
    let result := Generated.foldEntries input initial [] [] events
      (.settled transaction :: rest)
    result.outcome = .unsupported ∧
      result.failure = some .malformedTerminalObservation ∧
      result.committed = false ∧
      result.state.receipts = initial.receipts ∧
      result.state.gasHistory = initial.gasHistory ∧
      result.state.cumulativeReceiptGas = initial.cumulativeReceiptGas ∧
      result.state.headerGasUsed = initial.headerGasUsed := by
  simp [Generated.foldEntries, Generated.unsupportedResult, hBal, hInputParallel, hInitialParallel, hIndex,
    hOrdinary, hStart, hTerminalParallel, hMalformed]

theorem malformed_ok_entry_rejects_without_accumulator_change
    (input : Generated.FoldInput) (initial : Receipt.State)
    (results : List Receipt.TransactionResult) (completed : List Nat)
    (transaction : Generated.SettledTransaction) (rest : List Generated.TransactionEntry)
    (events : List Generated.FoldEvent)
    (hBal : input.balEnabled = false)
    (hInputParallel : input.parallel = false)
    (hInitialParallel : initial.parallel = false)
    (hIndex : transaction.index = completed.length)
    (hOrdinary : Generated.ordinary transaction.isStandard transaction.isSystem = true)
    (hStart : transaction.startState = initial)
    (hTerminalParallel : transaction.observation.trace.state.parallel = false)
    (hMalformed : Generated.terminalObservationValid initial transaction = false)
    (_hOnlyOk : OnlyOkTerminal transaction) :
    let result := Generated.foldEntries input initial results completed events
      (.settled transaction :: rest)
    result.outcome = .unsupported ∧
      result.failure = some .malformedTerminalObservation ∧
      result.committed = false ∧
      result.terminalResults = results ∧
      result.completedIndices = completed ∧
      result.events = events ∧
      result.state.receipts = initial.receipts ∧
      result.state.gasHistory = initial.gasHistory ∧
      result.state.cumulativeReceiptGas = initial.cumulativeReceiptGas ∧
      result.state.headerGasUsed = initial.headerGasUsed ∧
      result.state.currentIndex = initial.currentIndex := by
  simp [Generated.foldEntries, Generated.unsupportedResult, hBal, hInputParallel, hInitialParallel, hIndex,
    hOrdinary, hStart, hTerminalParallel, hMalformed]

theorem non_ok_terminal_rejects_without_accumulator_change
    (input : Generated.FoldInput) (initial : Receipt.State)
    (results : List Receipt.TransactionResult) (completed : List Nat)
    (transaction : Generated.SettledTransaction) (rest : List Generated.TransactionEntry)
    (events : List Generated.FoldEvent)
    (hBal : input.balEnabled = false)
    (hInputParallel : input.parallel = false)
    (hInitialParallel : initial.parallel = false)
    (hIndex : transaction.index = completed.length)
    (hOrdinary : Generated.ordinary transaction.isStandard transaction.isSystem = true)
    (hStart : transaction.startState = initial)
    (hTerminalParallel : transaction.observation.trace.state.parallel = false)
    (hNonOk : transaction.observation.result ≠ .ok) :
    let result := Generated.foldEntries input initial results completed events
      (.settled transaction :: rest)
    result.outcome = .unsupported ∧
      result.failure = some .malformedTerminalObservation ∧
      result.committed = false ∧
      result.terminalResults = results ∧
      result.completedIndices = completed ∧
      result.events = events ∧
      result.state.receipts = initial.receipts ∧
      result.state.gasHistory = initial.gasHistory ∧
      result.state.cumulativeReceiptGas = initial.cumulativeReceiptGas ∧
      result.state.headerGasUsed = initial.headerGasUsed ∧
      result.state.currentIndex = initial.currentIndex := by
  simp [Generated.foldEntries, Generated.unsupportedResult, hBal, hInputParallel, hInitialParallel, hIndex,
    hOrdinary, hStart, hTerminalParallel, hNonOk,
    Generated.terminalObservationValid, Generated.onlyOkTerminal]

theorem completed_result_has_post_transaction_commit
    (state : Receipt.State) (results : List Receipt.TransactionResult)
    (completed : List Nat) (events : List Generated.FoldEvent) :
    (Generated.completedResult state results completed events).committed = true ∧
      Generated.hasPostTransactionCommit
        (Generated.completedResult state results completed events) = true := by
  simp [Generated.completedResult, Generated.hasPostTransactionCommit]

theorem terminal_observation_chain_preserves_start
    (start : Receipt.State) (transaction : Generated.SettledTransaction) :
    ReceiptTerminalObservation start transaction → transaction.startState = start := by
  intro h
  exact h.2.2.1

theorem terminal_observation_projects_indices
    (start : Receipt.State) (transaction : Generated.SettledTransaction)
    (h : ReceiptTerminalObservation start transaction) :
    transaction.index = start.currentIndex ∧
      transaction.observation.trace.state.currentIndex = start.currentIndex ∧
      transaction.observation.trace.state.receipts.getLast?.map (fun receipt => receipt.index) =
        some transaction.index := by
  rcases h with ⟨_, _, _, hIndex, hCurrentIndex, _, _, _, _, _, hReceiptIndex, _, _⟩
  exact ⟨hIndex, hCurrentIndex, hReceiptIndex⟩

theorem terminal_observation_advances_accumulators
    (start : Receipt.State) (transaction : Generated.SettledTransaction)
    (h : ReceiptTerminalObservation start transaction) :
    transaction.observation.trace.state.receipts.length = start.receipts.length + 1 ∧
      transaction.observation.trace.state.gasHistory.length = start.gasHistory.length + 1 := by
  rcases h with ⟨_, _, _, _, _, _, _, hReceipts, hGasHistory, _, _, _, _⟩
  exact ⟨hReceipts, hGasHistory⟩

theorem terminal_observation_advances_tracer_index
    (start : Receipt.State) (transaction : Generated.SettledTransaction)
    (h : ReceiptTerminalObservation start transaction) :
    (Generated.afterTxTrace transaction.observation.trace.state).currentIndex =
      transaction.index + 1 := by
  have hProjection := terminal_observation_projects_indices start transaction h
  simp [Generated.afterTxTrace, hProjection.1, hProjection.2.1]

end SequentialBlockTransactionFoldExtractor.Refinement.SequentialBlockTransactionFold
