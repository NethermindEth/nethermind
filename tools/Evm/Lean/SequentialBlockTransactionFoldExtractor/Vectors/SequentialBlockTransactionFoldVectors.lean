-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import SequentialBlockTransactionFoldExtractor.Generated.SequentialBlockTransactionFold
import SequentialBlockTransactionFoldExtractor.Refinement.SequentialBlockTransactionFold
import ReceiptTerminalFoldExtractor.Vectors.ReceiptTerminalFoldVectors

namespace SequentialBlockTransactionFoldExtractor.Vectors.SequentialBlockTransactionFoldVectors

namespace Generated
export SequentialBlockTransactionFoldExtractor.Generated.SequentialBlockTransactionFold
  (FailureReason OutcomeKind FoldEvent SettledTransaction TransactionEntry FoldInput FoldResult
    run onlyOkTerminal hasPostTransactionCommit receiptCount afterTxTrace)
end Generated

namespace Receipt
export ReceiptTerminalFoldExtractor.Generated.ReceiptTerminalFoldKernel
  (State FinalizationObservation finalizeTransaction)
end Receipt

namespace ReceiptVectors
export ReceiptTerminalFoldExtractor.Vectors.ReceiptTerminalFoldVectors
  (initial block messageCall contractCreation successGas failureGas successInput failureInput
    nestedTracerPresent currentTxTracerIsTracingReceipt)
end ReceiptVectors

namespace Refinement
export SequentialBlockTransactionFoldExtractor.Refinement.SequentialBlockTransactionFold
  (FreshSequentialTracer NormalReturnEventTail ReceiptTerminalChain ReceiptTerminalObservation)
end Refinement

def foldInitial : Receipt.State :=
  { ReceiptVectors.initial with currentIndex := 0 }

def firstObservation : Receipt.FinalizationObservation :=
  Receipt.finalizeTransaction foldInitial ReceiptVectors.block ReceiptVectors.messageCall
    { id := 24 } ReceiptVectors.successGas ReceiptVectors.successInput
    ReceiptVectors.nestedTracerPresent ReceiptVectors.currentTxTracerIsTracingReceipt

def secondStart : Receipt.State :=
  Generated.afterTxTrace firstObservation.trace.state

def secondObservation : Receipt.FinalizationObservation :=
  Receipt.finalizeTransaction secondStart ReceiptVectors.block ReceiptVectors.contractCreation
    { id := 34 } ReceiptVectors.failureGas ReceiptVectors.failureInput
    ReceiptVectors.nestedTracerPresent ReceiptVectors.currentTxTracerIsTracingReceipt

def first : Generated.SettledTransaction :=
  { index := 0
    startState := foldInitial
    observation := firstObservation
    isStandard := true
    isSystem := false }

def second : Generated.SettledTransaction :=
  { index := 1
    startState := secondStart
    observation := secondObservation
    isStandard := true
    isSystem := false }

def successfulInput : Generated.FoldInput :=
  { balEnabled := false
    parallel := false
    shouldValidate := true
    gasLimit := 100
    entries := [.settled first, .settled second] }

def emptyInput : Generated.FoldInput :=
  { balEnabled := false
    parallel := false
    shouldValidate := true
    gasLimit := 100
    entries := [] }

def invalidFirstInput : Generated.FoldInput :=
  { balEnabled := false
    parallel := false
    shouldValidate := true
    gasLimit := 100
    entries := [.invalid 0 true false, .settled first] }

def invalidPrefixInput : Generated.FoldInput :=
  { balEnabled := false
    parallel := false
    shouldValidate := true
    gasLimit := 100
    entries := [.settled first, .invalid 1 true false] }

def gasLimitInput : Generated.FoldInput :=
  { balEnabled := false
    parallel := false
    shouldValidate := true
    gasLimit := 10
    entries := [.settled first] }

def noValidationInput : Generated.FoldInput :=
  { balEnabled := false
    parallel := false
    shouldValidate := false
    gasLimit := 10
    entries := [.settled first] }

def parallelInput : Generated.FoldInput :=
  { balEnabled := false
    parallel := true
    shouldValidate := true
    gasLimit := 100
    entries := [.settled first] }

def terminalParallelInput : Generated.FoldInput :=
  { balEnabled := false
    parallel := false
    shouldValidate := true
    gasLimit := 100
    entries := [.settled { first with observation :=
      { first.observation with trace :=
        { first.observation.trace with state :=
          { first.observation.trace.state with parallel := true } } } }] }

def indexMismatchInput : Generated.FoldInput :=
  { balEnabled := false
    parallel := false
    shouldValidate := true
    gasLimit := 100
    entries := [.settled { first with index := 1 }] }

def systemInput : Generated.FoldInput :=
  { balEnabled := false
    parallel := false
    shouldValidate := true
    gasLimit := 100
    entries := [.invalid 0 true true] }

def nonStandardInput : Generated.FoldInput :=
  { balEnabled := false
    parallel := false
    shouldValidate := true
    gasLimit := 100
    entries := [.settled { first with isStandard := false }] }

def malformedObservationInput : Generated.FoldInput :=
  { balEnabled := false
    parallel := false
    shouldValidate := true
    gasLimit := 100
    entries := [.settled { first with observation :=
      { first.observation with trace :=
        { first.observation.trace with state := foldInitial } } }] }

def invalidTerminalResultInput : Generated.FoldInput :=
  { balEnabled := false
    parallel := false
    shouldValidate := true
    gasLimit := 100
    entries := [.settled { first with observation :=
      { first.observation with result := .evmException { id := 99 } none } }] }

def receiptIndexMismatchInput : Generated.FoldInput :=
  { balEnabled := false
    parallel := false
    shouldValidate := true
    gasLimit := 100
    entries := [.settled { first with observation :=
      { first.observation with trace :=
        { first.observation.trace with state :=
          { first.observation.trace.state with receipts :=
            first.observation.trace.state.receipts.map (fun receipt => { receipt with index := 9 }) } } } }] }

def nonZeroInitialObservation : Receipt.FinalizationObservation :=
  Receipt.finalizeTransaction ReceiptVectors.initial ReceiptVectors.block ReceiptVectors.messageCall
    { id := 44 } ReceiptVectors.successGas ReceiptVectors.successInput
    ReceiptVectors.nestedTracerPresent ReceiptVectors.currentTxTracerIsTracingReceipt

def nonZeroInitialTransaction : Generated.SettledTransaction :=
  { index := ReceiptVectors.initial.currentIndex
    startState := ReceiptVectors.initial
    observation := nonZeroInitialObservation
    isStandard := true
    isSystem := false }

def nonZeroInitialInput : Generated.FoldInput :=
  { balEnabled := false
    parallel := false
    shouldValidate := true
    gasLimit := 100
    entries := [.settled nonZeroInitialTransaction] }

def balDecoratorInput : Generated.FoldInput :=
  { balEnabled := true
    parallel := false
    shouldValidate := true
    gasLimit := 100
    entries := [.settled first] }

def successfulRun : Generated.FoldResult := Generated.run successfulInput foldInitial

def emptyRun : Generated.FoldResult := Generated.run emptyInput foldInitial

def invalidFirstRun : Generated.FoldResult := Generated.run invalidFirstInput foldInitial

def invalidPrefixRun : Generated.FoldResult :=
  Generated.run invalidPrefixInput foldInitial

def gasLimitRun : Generated.FoldResult := Generated.run gasLimitInput foldInitial

def noValidationRun : Generated.FoldResult :=
  Generated.run noValidationInput foldInitial

def parallelRun : Generated.FoldResult := Generated.run parallelInput foldInitial

def terminalParallelRun : Generated.FoldResult :=
  Generated.run terminalParallelInput foldInitial

def indexMismatchRun : Generated.FoldResult :=
  Generated.run indexMismatchInput foldInitial

def systemRun : Generated.FoldResult := Generated.run systemInput foldInitial

def nonStandardRun : Generated.FoldResult :=
  Generated.run nonStandardInput foldInitial

def malformedObservationRun : Generated.FoldResult :=
  Generated.run malformedObservationInput foldInitial

def invalidTerminalResultRun : Generated.FoldResult :=
  Generated.run invalidTerminalResultInput foldInitial

def receiptIndexMismatchRun : Generated.FoldResult :=
  Generated.run receiptIndexMismatchInput foldInitial

def nonZeroInitialRun : Generated.FoldResult :=
  Generated.run nonZeroInitialInput ReceiptVectors.initial

def balDecoratorRun : Generated.FoldResult :=
  Generated.run balDecoratorInput foldInitial

theorem settled_observations_chain :
    SequentialBlockTransactionFoldExtractor.Refinement.SequentialBlockTransactionFold.ReceiptTerminalChain
      foldInitial [first, second] := by
  simp only [Refinement.ReceiptTerminalChain, Refinement.ReceiptTerminalObservation]
  decide

example : Refinement.FreshSequentialTracer foldInitial 0 := by
  unfold Refinement.FreshSequentialTracer
  decide
example : ¬ Refinement.FreshSequentialTracer ReceiptVectors.initial 0 := by
  unfold Refinement.FreshSequentialTracer
  decide
theorem scenario_empty : emptyRun.outcome = .completed := by decide
example : emptyRun.committed = true := by decide
example : emptyRun.state.receipts = [] ∧ emptyRun.state.gasHistory = [] ∧
    emptyRun.state.cumulativeReceiptGas = 0 ∧ emptyRun.state.currentIndex = 0 := by
  decide
example : emptyRun.events =
    [.preTransactionCommit, .transactionFoldCompleted, .transactionsExecuted,
      .postTransactionCommit] := by decide
example : Refinement.NormalReturnEventTail emptyRun := by
  unfold Refinement.NormalReturnEventTail
  decide

theorem empty_model_refinement :
    SequentialBlockTransactionFoldExtractor.Specification.SequentialBlockTransactionFold.FoldRelation
      emptyInput foldInitial [] [] [.preTransactionCommit] []
      (SequentialBlockTransactionFoldExtractor.Refinement.SequentialBlockTransactionFold.mapResult emptyRun) := by
  exact (SequentialBlockTransactionFoldExtractor.Refinement.SequentialBlockTransactionFold.generated_fold_refines_spec
    emptyInput foldInitial (by trivial)).1

theorem scenario_invalidFirst : invalidFirstRun.outcome = .invalidPrefix := by decide
example : invalidFirstRun.failure = some .invalidTransaction := by decide
example : invalidFirstRun.terminalResults = [] := by decide
example : invalidFirstRun.state.receipts = foldInitial.receipts ∧
    invalidFirstRun.state.gasHistory = foldInitial.gasHistory ∧
    invalidFirstRun.state.cumulativeReceiptGas = foldInitial.cumulativeReceiptGas := by
  decide
example : invalidFirstRun.events = [.preTransactionCommit, .invalidObserved 0] := by
  decide

theorem scenario_successful : successfulRun.outcome = .completed := by decide
example : successfulRun.committed = true := by decide
example : Generated.hasPostTransactionCommit successfulRun = true := by decide
example : successfulRun.completedIndices = [0, 1] := by decide
example : successfulRun.terminalResults = [.ok, .ok] := by decide
example : successfulRun.state.receipts.length = 2 := by decide
example : successfulRun.state.gasHistory.length = 2 := by decide
example : successfulRun.state.cumulativeReceiptGas = 9 := by decide
example : successfulRun.state.headerGasUsed = 16 := by decide
example : successfulRun.state.currentIndex = 2 := by decide
example : firstObservation.trace.state.receipts.getLast?.map (fun receipt => receipt.index) = some 0 := by
  decide
example : secondObservation.trace.state.receipts.getLast?.map (fun receipt => receipt.index) = some 1 := by
  decide
example : (Generated.afterTxTrace firstObservation.trace.state).currentIndex = first.index + 1 := by
  decide
example : (Generated.afterTxTrace secondObservation.trace.state).currentIndex = second.index + 1 := by
  decide
example : successfulRun.events =
    [.preTransactionCommit, .txTraceStarted 0, .terminalObserved 0, .txTraceEnded 0,
      .gasLimitChecked 0, .txTraceStarted 1, .terminalObserved 1, .txTraceEnded 1,
      .gasLimitChecked 1, .transactionFoldCompleted, .transactionsExecuted,
      .postTransactionCommit] := by decide

theorem scenario_invalidPrefix : invalidPrefixRun.outcome = .invalidPrefix := by decide
example : invalidPrefixRun.failure = some .invalidTransaction := by decide
example : invalidPrefixRun.committed = false := by decide
example : Generated.hasPostTransactionCommit invalidPrefixRun = false := by decide
example : invalidPrefixRun.completedIndices = [0] := by decide
example : invalidPrefixRun.terminalResults = [.ok] := by decide
example : invalidPrefixRun.state.receipts.length = 1 := by decide
example : invalidPrefixRun.events =
    [.preTransactionCommit, .txTraceStarted 0, .terminalObserved 0, .txTraceEnded 0,
      .gasLimitChecked 0, .invalidObserved 1] := by decide

theorem scenario_gasLimit : gasLimitRun.outcome = .invalidPrefix := by decide
example : gasLimitRun.failure = some .blockGasLimitExceeded := by decide
example : gasLimitRun.committed = false := by decide
example : Generated.hasPostTransactionCommit gasLimitRun = false := by decide
example : gasLimitRun.state.headerGasUsed = 12 := by decide

theorem scenario_noValidation : noValidationRun.outcome = .completed := by decide
example : noValidationRun.committed = true := by decide
example : Generated.hasPostTransactionCommit noValidationRun = true := by decide

theorem scenario_parallel : parallelRun.outcome = .unsupported := by decide
example : parallelRun.failure = some .parallelExecution := by decide
example : parallelRun.committed = false := by decide

theorem scenario_terminalParallel : terminalParallelRun.outcome = .unsupported := by decide
example : terminalParallelRun.failure = some .parallelExecution := by decide
example : terminalParallelRun.committed = false := by decide

theorem scenario_indexMismatch : indexMismatchRun.outcome = .unsupported := by decide
example : indexMismatchRun.failure = some .indexOrderMismatch := by decide
example : indexMismatchRun.committed = false := by decide

theorem scenario_system : systemRun.outcome = .unsupported := by decide
example : systemRun.failure = some .systemTransaction := by decide
example : systemRun.events = [.preTransactionCommit, .invalidObserved 0] := by decide

theorem scenario_nonStandard : nonStandardRun.outcome = .unsupported := by decide
example : nonStandardRun.failure = some .nonStandardTransaction := by decide
example : nonStandardRun.committed = false := by decide

theorem scenario_malformedObservation : malformedObservationRun.outcome = .unsupported := by decide
example : malformedObservationRun.failure = some .malformedTerminalObservation := by decide
example : malformedObservationRun.terminalResults = [] := by decide
example : malformedObservationRun.state.receipts = foldInitial.receipts ∧
    malformedObservationRun.state.gasHistory = foldInitial.gasHistory ∧
    malformedObservationRun.state.cumulativeReceiptGas = foldInitial.cumulativeReceiptGas ∧
    malformedObservationRun.state.headerGasUsed = foldInitial.headerGasUsed := by
  decide

example : malformedObservationRun.state.currentIndex = foldInitial.currentIndex := by
  decide
example : malformedObservationRun.state.headerGasUsed = foldInitial.headerGasUsed := by
  decide
example : malformedObservationRun.terminalResults = [] := by decide

theorem scenario_invalidTerminalResult : invalidTerminalResultRun.outcome = .unsupported := by decide
example : invalidTerminalResultRun.failure = some .malformedTerminalObservation := by
  decide
example : invalidTerminalResultRun.terminalResults = [] ∧
    invalidTerminalResultRun.state.receipts = foldInitial.receipts ∧
    invalidTerminalResultRun.state.gasHistory = foldInitial.gasHistory := by
  decide

theorem scenario_receiptIndexMismatch : receiptIndexMismatchRun.outcome = .unsupported := by decide
example : receiptIndexMismatchRun.failure = some .malformedTerminalObservation := by
  decide
example : receiptIndexMismatchRun.state.receipts = foldInitial.receipts ∧
    receiptIndexMismatchRun.state.gasHistory = foldInitial.gasHistory ∧
    receiptIndexMismatchRun.state.cumulativeReceiptGas = foldInitial.cumulativeReceiptGas := by
  decide

theorem scenario_nonZeroInitial : nonZeroInitialRun.outcome = .unsupported := by decide
example : nonZeroInitialRun.failure = some .indexOrderMismatch := by decide
example : nonZeroInitialRun.state.currentIndex = ReceiptVectors.initial.currentIndex := by
  decide
example : nonZeroInitialTransaction.observation.trace.state.currentIndex =
    ReceiptVectors.initial.currentIndex := by decide

theorem scenario_balDecorator : balDecoratorRun.outcome = .unsupported := by decide
example : balDecoratorRun.failure = some .balDecoratorActive := by decide
example : balDecoratorRun.committed = false := by decide

theorem settled_model_refinement :
    SequentialBlockTransactionFoldExtractor.Specification.SequentialBlockTransactionFold.FoldRelation
      successfulInput foldInitial [] [] [.preTransactionCommit] successfulInput.entries
      (SequentialBlockTransactionFoldExtractor.Refinement.SequentialBlockTransactionFold.mapResult
        (Generated.run successfulInput foldInitial)) := by
  exact (SequentialBlockTransactionFoldExtractor.Refinement.SequentialBlockTransactionFold.generated_fold_refines_spec
    successfulInput foldInitial settled_observations_chain).1

example :
    SequentialBlockTransactionFoldExtractor.Refinement.SequentialBlockTransactionFold.FreshSequentialTracer
      foldInitial 0 ∧
      SequentialBlockTransactionFoldExtractor.Refinement.SequentialBlockTransactionFold.NormalReturnEventTail
        emptyRun ∧
      SequentialBlockTransactionFoldExtractor.Refinement.SequentialBlockTransactionFold.ReceiptTerminalChain
        foldInitial [] := by
  refine ⟨?_, ?_, by trivial⟩
  · unfold Refinement.FreshSequentialTracer
    decide
  · unfold Refinement.NormalReturnEventTail
    decide

#check SequentialBlockTransactionFoldExtractor.Refinement.SequentialBlockTransactionFold.generated_fold_refines_spec
#eval successfulRun
#eval invalidPrefixRun
#eval gasLimitRun

end SequentialBlockTransactionFoldExtractor.Vectors.SequentialBlockTransactionFoldVectors
