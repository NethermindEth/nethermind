-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

-- This file is generated. Do not edit.
-- Extractor version: 1.9.2
-- Roslyn compiler version: 5.6.0.0
-- Production source: src/Nethermind/Nethermind.Blockchain/Tracing/BlockReceiptsTracer.cs
-- Production source SHA-256: d4504f54b50dd43e2ab5bc7172ce2cf9e48453fcede990e5e667e743146262ff
-- Canonical typed terminal-fold IR SHA-256: 0bf061d43d54e3eb9bdc7ccff4d0541d6dee189ac1ccea7f139095d3e1b7945a

import Eip803x.Generated.BlockReceiptGasAccountingKernel

namespace ReceiptTerminalFoldExtractor.Generated.ReceiptTerminalFoldKernel

structure BytesOracle where
  id : Nat
  deriving DecidableEq, Repr

structure LogOracle where
  id : Nat
  deriving DecidableEq, Repr

structure ErrorOracle where
  id : Nat
  deriving DecidableEq, Repr

structure EvmExceptionOracle where
  id : Nat
  deriving DecidableEq, Repr

structure HashOracle where
  id : Nat
  deriving DecidableEq, Repr

structure AddressOracle where
  id : Nat
  deriving DecidableEq, Repr

structure PriceOracle where
  id : Nat
  deriving DecidableEq, Repr

inductive Status where
| success
| failure
  deriving DecidableEq, Repr

inductive TransactionResult where
| ok
| evmException (exceptionType : EvmExceptionOracle) (substateError : Option ErrorOracle)
  deriving DecidableEq, Repr

inductive Event where
| gasMutation
| receiptAppend
| nestedTracerForward
| currentTracerForward
  deriving DecidableEq, Repr

structure Gas where
  spentGas : Nat
  operationGas : Nat
  blockGas : Nat
  blockStateGas : Nat
  maxUsedGas : Nat
  gasRefund : Nat
  deriving DecidableEq, Repr

def effectiveBlockGas (gas : Gas) : Nat :=
  if gas.blockGas > 0 || gas.blockStateGas > 0 then gas.blockGas else gas.spentGas

def statusCode : Status → Nat
| .success => 1
| .failure => 0

structure TransactionInput where
  txType : Nat
  to : Option AddressOracle
  sender : Option AddressOracle
  txHash : Option HashOracle
  effectiveGasPrice : PriceOracle
  deriving DecidableEq, Repr

def isContractCreation (transaction : TransactionInput) : Bool :=
  match transaction.to with
  | none => true
  | some _ => false

structure BlockInput where
  hash : Option HashOracle
  number : Nat
  baseFeePerGas : Nat
  deriving DecidableEq, Repr

structure GasTotals where
  executionGas : Nat
  stateGas : Nat
  deriving DecidableEq, Repr

structure Receipt where
  logs : List LogOracle
  txType : Nat
  gasUsedTotal : Nat
  statusCode : Nat
  recipient : Option AddressOracle
  blockHash : Option HashOracle
  blockNumber : Nat
  index : Nat
  gasUsed : Nat
  effectiveGasPrice : PriceOracle
  sender : Option AddressOracle
  contractAddress : Option AddressOracle
  txHash : Option HashOracle
  postTransactionState : Option HashOracle
  blockGasUsed : Nat
  executionGasUsed : Nat
  storageGasUsed : Nat
  error : Option ErrorOracle
  deriving DecidableEq, Repr

structure State where
  receipts : List Receipt
  gasHistory : List GasTotals
  cumulativeReceiptGas : Nat
  headerGasUsed : Nat
  parallel : Bool
  currentIndex : Nat
  deriving DecidableEq, Repr

structure Trace where
  state : State
  events : List Event
  forwardedOutput : BytesOracle
  forwardedLogs : List LogOracle
  forwardedError : Option ErrorOracle
  forwardedStateRoot : Option HashOracle
  deriving DecidableEq, Repr

structure FinalizationObservation where
  trace : Trace
  result : TransactionResult
  deriving DecidableEq, Repr

def previousTotals : List GasTotals → Nat × Nat
| [] => (0, 0)
| [last] => (last.executionGas, last.stateGas)
| _ :: tail => previousTotals tail

def lastReceiptGas : List Receipt → Nat
| [] => 0
| [last] => last.gasUsedTotal
| _ :: tail => lastReceiptGas tail

def updateCumulativeGasTracking (state : State) (gas : Gas) : State :=
  let (previousExecutionGas, previousStateGas) := previousTotals state.gasHistory
  let accounting :=
    Eip803x.Generated.BlockReceiptGasAccountingKernel.accumulate
      previousExecutionGas previousStateGas state.cumulativeReceiptGas
      (effectiveBlockGas gas) gas.blockStateGas gas.spentGas
  let nextHeaderGasUsed :=
    if ! state.parallel then accounting.headerGasUsed else state.headerGasUsed
  { state with
      gasHistory := state.gasHistory ++
        [{ executionGas := accounting.cumulativeExecutionGas
           stateGas := accounting.cumulativeStateGas }]
      cumulativeReceiptGas := accounting.cumulativeReceiptGas
      headerGasUsed := nextHeaderGasUsed }

def guardedBlockGasUsed (gas : Gas) : Nat :=
  if gas.blockGas > 0 then effectiveBlockGas gas else 0

def guardedExecutionGasUsed (gas : Gas) : Nat :=
  if gas.blockGas > 0 then gas.operationGas else 0

def guardedStateGasUsed (gas : Gas) : Nat :=
  if gas.blockStateGas > 0 then gas.blockStateGas else 0

def buildReceipt
    (state : State)
    (block : BlockInput)
    (transaction : TransactionInput)
    (recipient : AddressOracle)
    (gas : Gas)
    (status : Status)
    (logs : List LogOracle)
    (stateRoot : Option HashOracle) : State × Receipt :=
  let updated := updateCumulativeGasTracking state gas
  let receipt : Receipt :=
                {
                  logs := logs
                  txType := transaction.txType
                  gasUsedTotal := updated.cumulativeReceiptGas
                  statusCode := statusCode status
                  recipient := if isContractCreation transaction then none else some recipient
                  blockHash := block.hash
                  blockNumber := block.number
                  index := state.currentIndex
                  gasUsed := gas.spentGas
                  effectiveGasPrice := transaction.effectiveGasPrice
                  sender := transaction.sender
                  contractAddress := if isContractCreation transaction then some recipient else none
                  txHash := transaction.txHash
                  postTransactionState := stateRoot
                  blockGasUsed := if gas.blockGas > 0 then effectiveBlockGas gas else 0
                  executionGasUsed := if gas.blockGas > 0 then gas.operationGas else 0
                  storageGasUsed := if gas.blockStateGas > 0 then gas.blockStateGas else 0
                  error := none
                }
  (updated, receipt)

def buildFailedReceipt
    (state : State)
    (block : BlockInput)
    (transaction : TransactionInput)
    (recipient : AddressOracle)
    (gas : Gas)
    (error : Option ErrorOracle)
    (stateRoot : Option HashOracle) : State × Receipt :=
  let (updated, receipt) :=
    buildReceipt state block transaction recipient gas .failure [] stateRoot
  (updated, { receipt with error := error })

def emptyBytes : BytesOracle := { id := 0 }

def forwardingEvents (nestedTracer currentTxTracerIsTracingReceipt : Bool) : List Event :=
  [Event.gasMutation] ++
    [Event.receiptAppend] ++
    (if nestedTracer then [Event.nestedTracerForward] else []) ++
    (if currentTxTracerIsTracingReceipt then [Event.currentTracerForward] else [])

def markAsSuccess
    (state : State)
    (block : BlockInput)
    (transaction : TransactionInput)
    (recipient : AddressOracle)
    (gas : Gas)
    (output : BytesOracle)
    (logs : List LogOracle)
    (stateRoot : Option HashOracle)
    (nestedTracer currentTxTracerIsTracingReceipt : Bool) : Trace :=
  let (updated, receipt) :=
    buildReceipt state block transaction recipient gas .success logs stateRoot
  { state := { updated with receipts := updated.receipts ++ [receipt] }
    events := forwardingEvents nestedTracer currentTxTracerIsTracingReceipt
    forwardedOutput := output
    forwardedLogs := logs
    forwardedError := none
    forwardedStateRoot := stateRoot }

def markAsFailed
    (state : State)
    (block : BlockInput)
    (transaction : TransactionInput)
    (recipient : AddressOracle)
    (gas : Gas)
    (output : BytesOracle)
    (error : Option ErrorOracle)
    (stateRoot : Option HashOracle)
    (nestedTracer currentTxTracerIsTracingReceipt : Bool) : Trace :=
  let (updated, receipt) :=
    buildFailedReceipt state block transaction recipient gas error stateRoot
  { state := { updated with receipts := updated.receipts ++ [receipt] }
    events := forwardingEvents nestedTracer currentTxTracerIsTracingReceipt
    forwardedOutput := output
    forwardedLogs := []
    forwardedError := error
    forwardedStateRoot := stateRoot }

def takeSnapshot (state : State) : Nat :=
  state.receipts.length

def restorePrefix (state : State) (snapshot : Nat) : State :=
  let numToRemove := state.receipts.length - snapshot
  let retainedLength := state.receipts.length - numToRemove
  let receipts := state.receipts.take retainedLength
  let gasHistory := state.gasHistory.take retainedLength
  let (cumulativeExecution, cumulativeState) := previousTotals gasHistory
  let cumulativeReceipt := lastReceiptGas receipts
  let accounting :=
    Eip803x.Generated.BlockReceiptGasAccountingKernel.fromTotals
      cumulativeExecution cumulativeState cumulativeReceipt
  { state with
      receipts := receipts
      gasHistory := gasHistory
      cumulativeReceiptGas := accounting.cumulativeReceiptGas
      headerGasUsed := accounting.headerGasUsed }

def normalPrefix (state : State) (snapshot : Nat) : Prop :=
  snapshot ≤ state.receipts.length ∧
  state.receipts.length = state.gasHistory.length

structure FinalizeInput where
  status : Status
  shouldRevert : Bool
  output : BytesOracle
  substateError : Option ErrorOracle
  vmError : ErrorOracle
  evmExceptionType : Option EvmExceptionOracle
  substateResultError : Option ErrorOracle
  logs : List LogOracle
  stateRoot : Option HashOracle
  deriving DecidableEq, Repr

def failureOutput (input : FinalizeInput) : BytesOracle :=
  if input.shouldRevert then input.output else emptyBytes

def failureError (input : FinalizeInput) : Option ErrorOracle :=
  let initialError := input.substateError
  match initialError with
  | some error => some error
  | none => if initialError = none && input.evmExceptionType != none then some input.vmError else none

def transactionResult (input : FinalizeInput) : TransactionResult :=
  if input.evmExceptionType != none then
    match input.evmExceptionType with
    | some exceptionType => .evmException exceptionType input.substateResultError
    | none => .ok
  else
    .ok

def finalizeTransaction
    (state : State)
    (block : BlockInput)
    (transaction : TransactionInput)
    (recipient : AddressOracle)
    (gas : Gas)
    (input : FinalizeInput)
    (nestedTracer currentTxTracerIsTracingReceipt : Bool) : FinalizationObservation :=
  let trace :=
    if statusCode input.status == statusCode .failure then
      markAsFailed state block transaction recipient gas (failureOutput input) (failureError input) input.stateRoot nestedTracer currentTxTracerIsTracingReceipt
    else
      markAsSuccess state block transaction recipient gas input.output input.logs input.stateRoot nestedTracer currentTxTracerIsTracingReceipt
  { trace := trace, result := transactionResult input }

end ReceiptTerminalFoldExtractor.Generated.ReceiptTerminalFoldKernel
