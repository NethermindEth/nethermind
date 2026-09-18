-- SPDX-License-Identifier: LGPL-3.0-only

/-!
An independent observational specification for the bounded ordinary transaction machine.
It intentionally does not import the generated kernel or any handwritten transaction-state
runner. Terminal gas and receipt fields are supplied by an explicit settled-observation adapter;
the refinement layer relates them field by field to the generated ReceiptTerminal fold.
-/

namespace OrdinaryTransactionMachineExtractor.Specification

structure ReceiptFields where
  logs : List Nat
  txType : Nat
  gasUsedTotal : Nat
  statusCode : Nat
  recipient : Option Nat
  blockHash : Option Nat
  blockNumber : Nat
  index : Nat
  gasUsed : Nat
  effectiveGasPrice : Nat
  sender : Option Nat
  contractAddress : Option Nat
  txHash : Option Nat
  postTransactionState : Option Nat
  blockGasUsed : Nat
  executionGasUsed : Nat
  storageGasUsed : Nat
  error : Option Nat
  deriving DecidableEq, Repr

structure GasFields where
  spentGas : Nat
  operationGas : Nat
  blockGas : Nat
  blockStateGas : Nat
  maxUsedGas : Nat
  gasRefund : Nat
  deriving DecidableEq, Repr

structure GasTotals where
  executionGas : Nat
  stateGas : Nat
  deriving DecidableEq, Repr

structure EntryObservation where
  receipt : ReceiptFields
  gas : GasFields
  gasTotals : GasTotals
  resultIsOk : Bool
  statusIsSuccess : Bool
  nestedTracer : Bool
  currentTxTracerIsTracingReceipt : Bool
  wellFormed : Bool
  deriving DecidableEq, Repr

structure State where
  receipts : List ReceiptFields
  gasHistory : List GasTotals
  cumulativeReceiptGas : Nat
  headerGasUsed : Nat
  currentIndex : Nat
  events : List Nat
  deriving DecidableEq, Repr

structure Input where
  optionsRaw : Nat
  standardMainnet : Bool
  chainId : Nat
  forkNumber : Nat
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
  headerGasUsed : Nat
  deriving DecidableEq, Repr

inductive RejectReason where
  | route
  | malformed
  | invalidResult
  deriving DecidableEq, Repr

inductive Outcome where
  | rejected (reason : RejectReason) (state : State)
  | completed (state : State)
  deriving DecidableEq, Repr

def freshState (input : Input) : State :=
  { receipts := []
    gasHistory := []
    cumulativeReceiptGas := 0
    headerGasUsed := input.headerGasUsed
    currentIndex := 0
    events := [0, 1, 2] }

def routeValid (input : Input) : Bool :=
  input.optionsRaw == 1 && input.standardMainnet && input.chainId == 1 &&
  input.forkNumber == 25 && !input.isSystem && !input.parallel && !input.balEnabled &&
  !input.isCreate && !input.isCodeOverridable && !input.hasAuthorizationList &&
  !input.forceSimpleTransferDisabled && input.recipientLive && !input.recipientHasCode &&
  !input.recipientHasDelegation

def terminalForwardingEvents (entry : EntryObservation) : List Nat :=
  [6, 7] ++ (if entry.nestedTracer then [8] else []) ++
  (if entry.currentTxTracerIsTracingReceipt then [9] else [])

def entryValid (state : State) (entry : EntryObservation) : Bool :=
  entry.wellFormed && entry.resultIsOk && entry.statusIsSuccess &&
  entry.receipt.index == state.currentIndex

def step (state : State) (entry : EntryObservation) : State :=
  { receipts := state.receipts ++ [entry.receipt]
    gasHistory := state.gasHistory ++ [entry.gasTotals]
    cumulativeReceiptGas := entry.receipt.gasUsedTotal
    headerGasUsed := max state.headerGasUsed (max entry.gasTotals.executionGas entry.gasTotals.stateGas)
    currentIndex := state.currentIndex + 1
    events := state.events ++ [3, 4, 5] ++ terminalForwardingEvents entry ++ [10, 11] }

def fold (state : State) : List EntryObservation → Outcome
  | [] => .completed state
  | entry :: tail =>
      if !entry.wellFormed then
        .rejected .malformed state
      else if !entry.resultIsOk then
        .rejected .invalidResult state
      else if !entryValid state entry then
        .rejected .malformed state
      else
        fold (step state entry) tail

def finishCompleted (state : State) : State :=
  { state with events := state.events ++ [12, 13, 14] }

def finishRejected (state : State) : State :=
  { state with events := state.events ++ [12] }

def run (input : Input) (entries : List EntryObservation) : Outcome :=
  let initial := freshState input
  if !routeValid input then
    .rejected .route initial
  else
    match fold initial entries with
    | .completed state => .completed (finishCompleted state)
    | .rejected reason state => .rejected reason (finishRejected state)

def receiptIndices (state : State) : List Nat :=
  state.receipts.map (fun receipt => receipt.index)

def gasHistoryLengthsMatch (state : State) : Prop :=
  state.receipts.length = state.gasHistory.length

def sequentialIndexInvariant (state : State) : Prop :=
  state.receiptIndices = List.range state.receipts.length

end OrdinaryTransactionMachineExtractor.Specification
