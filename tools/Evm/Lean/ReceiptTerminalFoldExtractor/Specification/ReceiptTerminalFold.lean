-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

namespace ReceiptTerminalFoldExtractor.Specification.ReceiptTerminalFold

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
  let cumulativeExecutionGas := previousExecutionGas + effectiveBlockGas gas
  let cumulativeStateGas := previousStateGas + gas.blockStateGas
  let cumulativeReceiptGas := state.cumulativeReceiptGas + gas.spentGas
  let headerGasUsed := if state.parallel then state.headerGasUsed else max cumulativeExecutionGas cumulativeStateGas
  { state with
      gasHistory := state.gasHistory ++
        [{ executionGas := cumulativeExecutionGas, stateGas := cumulativeStateGas }]
      cumulativeReceiptGas := cumulativeReceiptGas
      headerGasUsed := headerGasUsed }

def guardedBlockGasUsed (gas : Gas) : Nat :=
  if gas.blockGas > 0 then effectiveBlockGas gas else 0

def guardedExecutionGasUsed (gas : Gas) : Nat :=
  if gas.blockGas > 0 then gas.operationGas else 0

def guardedStateGasUsed (gas : Gas) : Nat :=
  if gas.blockStateGas > 0 then gas.blockStateGas else 0

def receiptProjection
    (state : State)
    (block : BlockInput)
    (transaction : TransactionInput)
    (recipient : AddressOracle)
    (gas : Gas)
    (status : Status)
    (logs : List LogOracle)
    (stateRoot : Option HashOracle) : Receipt :=
  { logs := logs
    txType := transaction.txType
    gasUsedTotal := state.cumulativeReceiptGas
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
    blockGasUsed := guardedBlockGasUsed gas
    executionGasUsed := guardedExecutionGasUsed gas
    storageGasUsed := guardedStateGasUsed gas
    error := none }

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
  (updated, receiptProjection updated block transaction recipient gas status logs stateRoot)

def buildFailedReceipt
    (state : State)
    (block : BlockInput)
    (transaction : TransactionInput)
    (recipient : AddressOracle)
    (gas : Gas)
    (error : Option ErrorOracle)
    (stateRoot : Option HashOracle) : State × Receipt :=
  let (updated, receipt) := buildReceipt state block transaction recipient gas .failure [] stateRoot
  (updated, { receipt with error := error })

def forwardingEvents (nestedTracer currentTxTracerIsTracingReceipt : Bool) : List Event :=
  [Event.gasMutation, Event.receiptAppend] ++
    (if nestedTracer then [Event.nestedTracerForward] else []) ++
    (if currentTxTracerIsTracingReceipt then [Event.currentTracerForward] else [])

-- The reference is organized as a terminal decision fold.  Receipt construction
-- remains the observable projection, while the decision carries the forwarding
-- payload independently of receipt storage.
inductive TerminalDecision where
| success (output : BytesOracle) (logs : List LogOracle) (stateRoot : Option HashOracle)
| failure (output : BytesOracle) (error : Option ErrorOracle) (stateRoot : Option HashOracle)
  deriving DecidableEq, Repr

def appendReceipt (state : State) (receipt : Receipt) : State :=
  { state with receipts := state.receipts ++ [receipt] }

def terminalTrace
    (updated : State)
    (receipt : Receipt)
    (output : BytesOracle)
    (logs : List LogOracle)
    (error : Option ErrorOracle)
    (stateRoot : Option HashOracle)
    (nestedTracer currentTxTracerIsTracingReceipt : Bool) : Trace :=
  { state := appendReceipt updated receipt
    events := forwardingEvents nestedTracer currentTxTracerIsTracingReceipt
    forwardedOutput := output
    forwardedLogs := logs
    forwardedError := error
    forwardedStateRoot := stateRoot }

def applyTerminalDecision
    (state : State)
    (block : BlockInput)
    (transaction : TransactionInput)
    (recipient : AddressOracle)
    (gas : Gas)
    (decision : TerminalDecision)
    (nestedTracer currentTxTracerIsTracingReceipt : Bool) : Trace :=
  match decision with
  | .success output logs stateRoot =>
      let (updated, receipt) := buildReceipt state block transaction recipient gas .success logs stateRoot
      terminalTrace updated receipt output logs none stateRoot nestedTracer currentTxTracerIsTracingReceipt
  | .failure output error stateRoot =>
      let (updated, receipt) := buildFailedReceipt state block transaction recipient gas error stateRoot
      terminalTrace updated receipt output [] error stateRoot nestedTracer currentTxTracerIsTracingReceipt

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
  applyTerminalDecision state block transaction recipient gas
    (.success output logs stateRoot) nestedTracer currentTxTracerIsTracingReceipt

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
  applyTerminalDecision state block transaction recipient gas
    (.failure output error stateRoot) nestedTracer currentTxTracerIsTracingReceipt

def takeSnapshot (state : State) : Nat :=
  state.receipts.length

def restorePrefix (state : State) (snapshot : Nat) : State :=
  let receipts := state.receipts.take snapshot
  let gasHistory := state.gasHistory.take snapshot
  let (executionGas, stateGas) := previousTotals gasHistory
  { state with
      receipts := receipts
      gasHistory := gasHistory
      cumulativeReceiptGas := lastReceiptGas receipts
      headerGasUsed := if state.parallel then state.headerGasUsed else max executionGas stateGas }

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

def emptyBytes : BytesOracle := { id := 0 }

def failureOutput (input : FinalizeInput) : BytesOracle :=
  if input.shouldRevert then input.output else emptyBytes

def failureError (input : FinalizeInput) : Option ErrorOracle :=
  match input.substateError, input.evmExceptionType with
  | some error, _ => some error
  | none, some _ => some input.vmError
  | none, none => none

def transactionResult (input : FinalizeInput) : TransactionResult :=
  match input.evmExceptionType with
  | none => .ok
  | some exceptionType => .evmException exceptionType input.substateResultError

def decideTerminal (input : FinalizeInput) : TerminalDecision :=
  match input.status with
  | .success => .success input.output input.logs input.stateRoot
  | .failure => .failure (failureOutput input) (failureError input) input.stateRoot

def finalizeTransaction
    (state : State)
    (block : BlockInput)
    (transaction : TransactionInput)
    (recipient : AddressOracle)
    (gas : Gas)
    (input : FinalizeInput)
    (nestedTracer currentTxTracerIsTracingReceipt : Bool) : FinalizationObservation :=
  { trace := applyTerminalDecision state block transaction recipient gas
      (decideTerminal input) nestedTracer currentTxTracerIsTracingReceipt
    result := transactionResult input }

end ReceiptTerminalFoldExtractor.Specification.ReceiptTerminalFold
