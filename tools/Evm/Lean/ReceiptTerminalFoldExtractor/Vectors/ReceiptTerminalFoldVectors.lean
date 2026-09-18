-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import ReceiptTerminalFoldExtractor.Generated.ReceiptTerminalFoldKernel
import ReceiptTerminalFoldExtractor.Specification.ReceiptTerminalFold
import ReceiptTerminalFoldExtractor.Refinement.ReceiptTerminalFold

namespace ReceiptTerminalFoldExtractor.Vectors.ReceiptTerminalFoldVectors

namespace Generated
export ReceiptTerminalFoldExtractor.Generated.ReceiptTerminalFoldKernel
  (AddressOracle HashOracle BytesOracle LogOracle ErrorOracle EvmExceptionOracle PriceOracle State
    BlockInput TransactionInput Gas FinalizeInput TransactionResult FinalizationObservation Trace
    Receipt isContractCreation finalizeTransaction restorePrefix takeSnapshot)
end Generated

def address (id : Nat) : Generated.AddressOracle := { id := id }

def hash (id : Nat) : Generated.HashOracle := { id := id }

def bytes (id : Nat) : Generated.BytesOracle := { id := id }

def log (id : Nat) : Generated.LogOracle := { id := id }

def error (id : Nat) : Generated.ErrorOracle := { id := id }

def exception (id : Nat) : Generated.EvmExceptionOracle := { id := id }

def price (id : Nat) : Generated.PriceOracle := { id := id }

def initial : Generated.State :=
  { receipts := []
    gasHistory := []
    cumulativeReceiptGas := 0
    headerGasUsed := 0
    parallel := false
    currentIndex := 7 }

def block : Generated.BlockInput :=
  { hash := some (hash 11)
    number := 42
    baseFeePerGas := 100 }

def messageCall : Generated.TransactionInput :=
  { txType := 2
    to := some (address 20)
    sender := some (address 21)
    txHash := some (hash 22)
    effectiveGasPrice := price 23 }

def contractCreation : Generated.TransactionInput :=
  { txType := 1
    to := none
    sender := some (address 31)
    txHash := some (hash 32)
    effectiveGasPrice := price 33 }

def successGas : Generated.Gas :=
  { spentGas := 5
    operationGas := 8
    blockGas := 12
    blockStateGas := 3
    maxUsedGas := 10
    gasRefund := 2 }

def failureGas : Generated.Gas :=
  { spentGas := 4
    operationGas := 0
    blockGas := 0
    blockStateGas := 0
    maxUsedGas := 4
    gasRefund := 1 }

def successInput : Generated.FinalizeInput :=
  { status := .success
    shouldRevert := false
    output := bytes 40
    substateError := none
    vmError := error 42
    evmExceptionType := none
    substateResultError := none
    logs := [log 50]
    stateRoot := some (hash 60) }

def failureInput : Generated.FinalizeInput :=
  { status := .failure
    shouldRevert := true
    output := bytes 70
    substateError := some (error 80)
    vmError := error 81
    evmExceptionType := none
    substateResultError := none
    logs := [log 90]
    stateRoot := none }

def exceptionFailureInput : Generated.FinalizeInput :=
  { status := .failure
    shouldRevert := false
    output := bytes 100
    substateError := none
    vmError := error 102
    evmExceptionType := some (exception 103)
    substateResultError := some (error 104)
    logs := [log 105]
    stateRoot := none }

-- The exact base tracer always has IsTracingReceipt = true.  These two
-- booleans model only nested-tracer presence and the current transaction
-- tracer's forwarding gate inside BlockReceiptsTracer.MarkAs*.
def nestedTracerPresent : Bool := true

def currentTxTracerIsTracingReceipt : Bool := true

def successVector : Generated.FinalizationObservation :=
  Generated.finalizeTransaction initial block messageCall (address 24) successGas successInput
    nestedTracerPresent currentTxTracerIsTracingReceipt

def failureVector : Generated.FinalizationObservation :=
  Generated.finalizeTransaction successVector.trace.state block contractCreation (address 34) failureGas failureInput
    nestedTracerPresent currentTxTracerIsTracingReceipt

def exceptionFailureVector : Generated.FinalizationObservation :=
  Generated.finalizeTransaction failureVector.trace.state block messageCall (address 24) failureGas
    exceptionFailureInput nestedTracerPresent currentTxTracerIsTracingReceipt

def restoredVector : Generated.State :=
  Generated.restorePrefix failureVector.trace.state (Generated.takeSnapshot initial)

theorem successAccounting :
    ReceiptTerminalFoldExtractor.Refinement.ReceiptTerminalFold.AccountingValid initial successGas := by
  unfold ReceiptTerminalFoldExtractor.Refinement.ReceiptTerminalFold.AccountingValid
    ReceiptTerminalFoldExtractor.Refinement.ReceiptTerminalFold.FitsUInt64
  decide

theorem successSequential : initial.parallel = false := by
  decide

theorem successDomain :
    ReceiptTerminalFoldExtractor.Refinement.ReceiptTerminalFold.ProductionFinalizeDomain
      successInput := by
  constructor
  decide

def lastReceipt (state : Generated.State) : Option Generated.Receipt :=
  state.receipts.getLast?

def lastBlockGasUsed (state : Generated.State) : Nat :=
  match lastReceipt state with
  | some receipt => receipt.blockGasUsed
  | none => 0

def lastStatus (state : Generated.State) : Option Nat :=
  match lastReceipt state with
  | some receipt => some receipt.statusCode
  | none => none

def lastExecutionGasUsed (state : Generated.State) : Nat :=
  match lastReceipt state with
  | some receipt => receipt.executionGasUsed
  | none => 0

def lastStorageGasUsed (state : Generated.State) : Nat :=
  match lastReceipt state with
  | some receipt => receipt.storageGasUsed
  | none => 0

def lastLogs (state : Generated.State) : List Generated.LogOracle :=
  match lastReceipt state with
  | some receipt => receipt.logs
  | none => []

def lastError (state : Generated.State) : Option Generated.ErrorOracle :=
  match lastReceipt state with
  | some receipt => receipt.error
  | none => none

def lastRecipient (state : Generated.State) : Option Generated.AddressOracle :=
  match lastReceipt state with
  | some receipt => receipt.recipient
  | none => none

def lastContractAddress (state : Generated.State) : Option Generated.AddressOracle :=
  match lastReceipt state with
  | some receipt => receipt.contractAddress
  | none => none

-- Each executable vector is a checked proposition, not an inert Boolean report.
example : successVector.trace.state.cumulativeReceiptGas = 5 := by decide
example : Generated.isContractCreation messageCall = false ∧ Generated.isContractCreation contractCreation = true := by decide
#check ReceiptTerminalFoldExtractor.Refinement.ReceiptTerminalFold.transaction_isContractCreation_iff_to_isNone
example : successVector.trace.state.headerGasUsed = 12 := by decide
example : lastBlockGasUsed successVector.trace.state = 12 := by decide
example : lastExecutionGasUsed successVector.trace.state = 8 := by decide
example : lastStorageGasUsed successVector.trace.state = 3 := by decide
example : lastLogs failureVector.trace.state = [] := by decide
example : failureVector.trace.forwardedOutput = bytes 70 := by decide
example : lastError failureVector.trace.state = some (error 80) := by decide
example : lastBlockGasUsed failureVector.trace.state = 0 ∧
    lastExecutionGasUsed failureVector.trace.state = 0 ∧
    lastStorageGasUsed failureVector.trace.state = 0 := by decide
example : lastRecipient failureVector.trace.state = none := by decide
example : lastContractAddress failureVector.trace.state = some (address 34) := by decide
example : lastStatus failureVector.trace.state = some 0 := by decide
example : failureVector.result = .ok := by decide
example : lastStatus exceptionFailureVector.trace.state = some 0 := by decide
example : exceptionFailureVector.trace.forwardedOutput = bytes 0 := by decide
example : lastError exceptionFailureVector.trace.state = some (error 102) := by decide
example : exceptionFailureVector.result =
    .evmException (exception 103) (some (error 104)) := by decide
example : failureVector.result ≠ exceptionFailureVector.result := by decide
example : restoredVector.receipts.length = 0 := by decide
example : restoredVector.cumulativeReceiptGas = 0 := by decide

example :
    ReceiptTerminalFoldExtractor.Refinement.ReceiptTerminalFold.mapFinalizationObservation
        successVector =
      ReceiptTerminalFoldExtractor.Specification.ReceiptTerminalFold.finalizeTransaction
        (ReceiptTerminalFoldExtractor.Refinement.ReceiptTerminalFold.mapState initial)
        (ReceiptTerminalFoldExtractor.Refinement.ReceiptTerminalFold.mapBlock block)
        (ReceiptTerminalFoldExtractor.Refinement.ReceiptTerminalFold.mapTransaction messageCall)
        (ReceiptTerminalFoldExtractor.Refinement.ReceiptTerminalFold.mapAddress (address 24))
        (ReceiptTerminalFoldExtractor.Refinement.ReceiptTerminalFold.mapGas successGas)
        (ReceiptTerminalFoldExtractor.Refinement.ReceiptTerminalFold.mapFinalizeInput successInput)
         nestedTracerPresent currentTxTracerIsTracingReceipt := by
  exact ReceiptTerminalFoldExtractor.Refinement.ReceiptTerminalFold.generatedFinalizeTransaction_refines_spec
    initial block messageCall (address 24) successGas successInput nestedTracerPresent currentTxTracerIsTracingReceipt
    successSequential successDomain successAccounting

#check ReceiptTerminalFoldExtractor.Refinement.ReceiptTerminalFold.generatedMarkAsSuccess_refines_spec
#check ReceiptTerminalFoldExtractor.Refinement.ReceiptTerminalFold.generatedMarkAsFailed_refines_spec
#check ReceiptTerminalFoldExtractor.Refinement.ReceiptTerminalFold.generatedFinalizeTransaction_refines_spec
#check ReceiptTerminalFoldExtractor.Refinement.ReceiptTerminalFold.generatedRestorePrefix_refines_spec
#eval successVector
#eval failureVector
#eval exceptionFailureVector
#eval restoredVector

end ReceiptTerminalFoldExtractor.Vectors.ReceiptTerminalFoldVectors
