-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.Generated.TransactionSettlementKernel

namespace SimpleTransferCompletionExtractor.Reference

def uint64Max : Nat := 2 ^ 64 - 1
def sourceTransferLogAddress : Nat := 0xfffffffffffffffffffffffffffffffffffffffe
def sourceTransferSignature : Nat := 0xddf252ad1be2c89b69c2b068fc378daa952ba7f163c4a11628f55a4df523b3ef

-- This vocabulary is intentionally independent from Generated. It records the payloads used by the
-- handwritten request-boundary model rather than reducing observations to event tags. It is not a
-- model of production callback/world implementations.
structure StorageCell where
  address : Nat
  key : Nat
  deriving DecidableEq, Repr

structure AccessEntry where
  address : Nat
  storageKeys : List Nat
  deriving DecidableEq, Repr

structure AccessObservation where
  addresses : List Nat
  storageCells : List StorageCell
  deriving DecidableEq, Repr

structure SpecView where
  eip8037 : Bool
  eip7708 : Bool
  eip658 : Bool
  eip3529 : Bool
  eip7778 : Bool
  eip1559 : Bool
  eip4844FeeCollectorEnabled : Bool
  warmStorage : Bool
  accessListEnabled : Bool
  coinbaseInAccessList : Bool
  feeCollector : Option Nat
  destroyRefund : Nat
  refundQuotient : Nat
  deriving DecidableEq, Repr

structure GasState where
  execution : Nat
  reservoir : Int
  stateUsed : Int
  spill : Int
  spillRefunded : Int
  deriving DecidableEq, Repr

structure StateCharge where
  succeeded : Bool
  gas : GasState
  deriving DecidableEq, Repr

structure TransferLog where
  address : Nat
  topics : List Nat
  data : List Nat
  fromAddress : Nat
  toAddress : Nat
  amount : Nat
  deriving DecidableEq, Repr

inductive EvmException where
  | none
  | outOfGas
  deriving DecidableEq, Repr

inductive ActionKind where
  | transaction
  deriving DecidableEq, Repr

inductive WorldRequest where
  | subtract (address value : Nat) (spec : SpecView)
  | add (address value : Nat) (spec : SpecView)
  | addOrCreate (address value : Nat) (spec : SpecView)
  | reset (resetBlockChanges : Bool)
  | deleteAccount (address : Nat)
  | decrementNonce (address : Nat)
  | commit (spec : SpecView) (commitRoots stateTracing : Bool)
  | resetTransient
  | reapEmptyAccounts
  | recalculateStateRoot (before after : Option Nat)
  deriving DecidableEq, Repr

inductive TraceRequest where
  | actionStart (gas value : Nat) (fromAddress toAddress : Nat) (data : List Nat) (kind : ActionKind) (isPrecompileCall : Bool)
  | byteCode (code : List Nat)
  | actionError (exception : EvmException)
  | actionEnd (gas : Nat) (output : List Nat)
  | log (log : TransferLog)
  | access (addresses : List Nat) (storageCells : List StorageCell)
  | fees (fees burnt : Nat)
  | receiptFailed (account spent : Nat) (output : List Nat) (error : Option String) (root : Option Nat)
  | receiptSuccess (account spent : Nat) (output : List Nat) (logs : List TransferLog) (root : Option Nat)
  deriving DecidableEq, Repr

structure SubstateView where
  output : List Nat
  refund : Int
  logs : List TransferLog
  shouldRevert : Bool
  isError : Bool
  error : Option String
  exception : EvmException
  deriving DecidableEq, Repr

structure GasView where
  spentGas : Nat
  operationGas : Nat
  blockGas : Nat
  blockStateGas : Nat
  maxUsedGas : Nat
  gasRefund : Nat
  deriving DecidableEq, Repr

structure SettlementInput where
  gasLimit : Nat
  preRefundGas : Nat
  refundCounter : Int
  destroyCount : Int
  destroyRefund : Nat
  codeInsertExecutionRefund : Nat
  calldataFloorGas : Nat
  stateGasUsed : Int
  refundQuotient : Nat
  isError : Bool
  shouldRevert : Bool
  isEip8037Enabled : Bool
  isEip7778Enabled : Bool
  deriving DecidableEq, Repr

def settlement (input : SettlementInput) : Eip803x.Generated.TransactionSettlementKernel.TransactionSettlementResult :=
  Eip803x.Generated.TransactionSettlementKernel.calculate
    input.gasLimit input.preRefundGas input.refundCounter input.destroyCount input.destroyRefund
    input.codeInsertExecutionRefund input.calldataFloorGas input.stateGasUsed input.refundQuotient
    input.isError input.shouldRevert input.isEip8037Enabled input.isEip7778Enabled

inductive JournalEvent where
  | metric
  | gasCharge (response : StateCharge)
  | world (request : WorldRequest)
  | trace (request : TraceRequest)
  | log (transfer : TransferLog)
  | substate (value : SubstateView)
  | clearExecutionGas (before after : GasState)
  | settlement (gas : GasView)
  | header (gas : Nat)
  | transaction (blockGas spentGas : Nat)
  | receipt (request : TraceRequest)
  deriving DecidableEq, Repr

structure Case where
  sender : Nat
  recipient : Nat
  beneficiary : Nat
  value : Nat
  data : List Nat
  accessList : List AccessEntry
  gasLimit : Nat
  freeTransaction : Bool
  blobTransaction : Bool
  maxFeePerGas : Nat
  maxPriorityFeePerGas : Nat
  eip8037 : Bool
  eip7708 : Bool
  eip658 : Bool
  eip3529 : Bool
  eip7778 : Bool
  eip1559 : Bool
  eip4844FeeCollectorEnabled : Bool
  feeCollector : Option Nat
  warmStorage : Bool
  accessListEnabled : Bool
  coinbaseInAccessList : Bool
  restore : Bool
  commit : Bool
  deleteCaller : Bool
  warmup : Bool
  buildUp : Bool
  skipValidation : Bool
  parallel : Bool
  tracingActions : Bool
  tracingCode : Bool
  tracingLogs : Bool
  tracingAccess : Bool
  tracingFees : Bool
  tracingReceipt : Bool
  tracingState : Bool
  normalReturn : Bool
  recipientDead : Bool
  senderBalance : Nat
  gasExecution : Nat
  gasReservoir : Int
  gasStateUsed : Int
  gasSpill : Int
  gasSpillRefunded : Int
  chargedSucceeded : Bool
  chargedExecution : Nat
  chargedReservoir : Int
  chargedStateUsed : Int
  chargedSpill : Int
  chargedSpillRefunded : Int
  preRefundGas : Nat
  floorGas : Nat
  baseFee : Nat
  opcodePrice : Nat
  premium : Nat
  senderReserved : Nat
  blobBaseFee : Nat
  destroyRefund : Nat
  refundQuotient : Nat
  headerGasUsed : Nat
  initialTransactionBlockGas : Nat
  initialTransactionSpentGas : Nat
  cumulativeExecutionGas : Nat
  cumulativeStateGas : Nat
  postCumulativeExecutionGas : Nat
  postCumulativeStateGas : Nat
  stateRootBeforeRecalculate : Option Nat
  stateRootAfterRecalculate : Option Nat
  newAccountStateCost : Nat

def normalReturnDomain (case : Case) : Prop := case.normalReturn = true

structure Receipt where
  tracing : Bool
  failed : Bool
  executingAccount : Nat
  spent : Nat
  output : List Nat
  error : Option String
  logs : List TransferLog
  root : Option Nat
  deriving DecidableEq, Repr

structure Result where
  failed : Bool
  spent : Nat
  preRefundGas : Nat
  headerGasUsed : Nat
  transactionBlockGas : Nat
  transactionSpentGas : Nat
  postCumulativeExecutionGas : Nat
  postCumulativeStateGas : Nat
  receipt : Receipt
  journal : List JournalEvent
  deriving DecidableEq, Repr

def transferDataFor (value : Nat) : List Nat :=
  (List.range 32).reverse.map (fun index => (value / (256 ^ index)) % 256)

def expectedTransferLog (case : Case) : TransferLog :=
  { address := sourceTransferLogAddress
    topics := [sourceTransferSignature, case.sender, case.recipient]
    data := transferDataFor case.value
    fromAddress := case.sender
    toAddress := case.recipient
    amount := case.value }

def stateRootRecalculationShape (case : Case) : Prop :=
  case.tracingReceipt && !case.eip658 → case.stateRootAfterRecalculate.isSome

def commitOption : Nat := 1
def restoreOption : Nat := 2
def skipValidationOption : Nat := 4
def warmupOption : Nat := 8
def buildUpOption : Nat := 16

def hasFlag (options flag : Nat) : Bool := flag != 0 && ((options / flag) % 2 == 1)
def optionIsCommit (options : Nat) : Bool := hasFlag options commitOption
def optionIsRestore (options : Nat) : Bool := hasFlag options restoreOption
def optionIsSkipValidation (options : Nat) : Bool := hasFlag options skipValidationOption
def optionIsWarmup (options : Nat) : Bool := hasFlag options warmupOption
def optionIsBuildUp (options : Nat) : Bool := options == buildUpOption

def specView (case : Case) : SpecView :=
  { eip8037 := case.eip8037
    eip7708 := case.eip7708
    eip658 := case.eip658
    eip3529 := case.eip3529
    eip7778 := case.eip7778
    eip1559 := case.eip1559
    eip4844FeeCollectorEnabled := case.eip4844FeeCollectorEnabled
    warmStorage := case.warmStorage
    accessListEnabled := case.accessListEnabled
    coinbaseInAccessList := case.coinbaseInAccessList
    feeCollector := case.feeCollector
    destroyRefund := case.destroyRefund
    refundQuotient := case.refundQuotient }

def chargeGuard (case : Case) (selfSend : Bool) : Bool :=
  case.eip8037 && case.value != 0 && !selfSend && case.recipientDead

def writesGuard (_case : Case) (selfSend oog : Bool) : Bool :=
  !selfSend && !oog

def logsGuard (case : Case) (selfSend oog : Bool) : Bool :=
  case.eip7708 && case.value != 0 &&
    !selfSend && !oog

def dedupNat (values : List Nat) : List Nat :=
  values.foldl (fun seen value => if value ∈ seen then seen else seen ++ [value]) []

def dedupStorageCells (values : List StorageCell) : List StorageCell :=
  values.foldl (fun seen value => if value ∈ seen then seen else seen ++ [value]) []

def accessStorageCells (entries : List AccessEntry) : List StorageCell :=
  entries.flatMap (fun entry => entry.storageKeys.map (fun key => { address := entry.address, key }))

def accessWarmupEntries (case : Case) : List AccessEntry :=
  if !case.warmStorage then [] else
    let txEntries := if case.accessListEnabled then case.accessList else []
    let coinbaseEntries :=
      if case.coinbaseInAccessList then [{ address := case.beneficiary, storageKeys := [] }] else []
    txEntries ++ coinbaseEntries ++
      [{ address := case.recipient, storageKeys := [] }, { address := case.sender, storageKeys := [] }]

def accessObservation (case : Case) : AccessObservation :=
  let entries := accessWarmupEntries case
  { addresses := dedupNat (entries.map (fun entry => entry.address))
    storageCells := dedupStorageCells (accessStorageCells entries) }

-- ReportAccess is a set-valued boundary. The list representation is only a finite carrier for
-- membership; callers must not rely on its enumeration order (the live JournalSet uses HashSet
-- storage whose iteration order is not an insertion-order contract).
def accessObservationExtensional (left right : AccessObservation) : Prop :=
  (∀ address : Nat, address ∈ left.addresses ↔ address ∈ right.addresses) ∧
    (∀ cell : StorageCell, cell ∈ left.storageCells ↔ cell ∈ right.storageCells)

-- Event sequencing remains ordered. Only the two finite collections carried by an access trace
-- are quotiented, because their production carrier is a HashSet rather than an insertion-ordered
-- list. Every other trace/world/gas payload still compares structurally and positionally.
def traceRequestExtensional : TraceRequest → TraceRequest → Prop
  | .access leftAddresses leftStorage, .access rightAddresses rightStorage =>
      accessObservationExtensional
        { addresses := leftAddresses, storageCells := leftStorage }
        { addresses := rightAddresses, storageCells := rightStorage }
  | left, right => left = right

def journalEventExtensional : JournalEvent → JournalEvent → Prop
  | .trace left, .trace right => traceRequestExtensional left right
  | left, right => left = right

def journalExtensional : List JournalEvent → List JournalEvent → Prop
  | [], [] => True
  | left :: leftRest, right :: rightRest =>
      journalEventExtensional left right ∧ journalExtensional leftRest rightRest
  | _, _ => False

theorem journalExtensional_refl (events : List JournalEvent) : journalExtensional events events := by
  induction events with
  | nil => trivial
  | cons head tail ih =>
    constructor
    · cases head <;>
        simp [journalEventExtensional, traceRequestExtensional,
          accessObservationExtensional]
    · exact ih

theorem journalExtensional_of_eq {left right : List JournalEvent} (h : left = right) :
    journalExtensional left right := by
  cases h
  exact journalExtensional_refl _

def gasState (case : Case) : GasState :=
  { execution := case.gasExecution
    reservoir := case.gasReservoir
    stateUsed := case.gasStateUsed
    spill := case.gasSpill
    spillRefunded := case.gasSpillRefunded }

def chargedState (case : Case) : StateCharge :=
  { succeeded := case.chargedSucceeded
    gas :=
      { execution := case.chargedExecution
        reservoir := case.chargedReservoir
        stateUsed := case.chargedStateUsed
        spill := case.chargedSpill
        spillRefunded := case.chargedSpillRefunded } }

def clearExecution (gas : GasState) : GasState := { gas with execution := 0 }

def preRefundFor (case : Case) (execution : Nat) (reservoir : Int) : Nat :=
  let candidate : Int := (case.gasLimit : Int) - (execution : Int) - reservoir
  if candidate >= 0 && candidate <= (uint64Max : Int) then Int.toNat candidate else case.gasLimit

def gasView (result : Eip803x.Generated.TransactionSettlementKernel.TransactionSettlementResult) : GasView :=
  { spentGas := result.spentGas
    operationGas := result.operationGas
    blockGas := result.blockGas
    blockStateGas := result.blockStateGas
    maxUsedGas := result.maxUsedGas
    gasRefund := result.gasRefund }

def effectiveBlockGas (gas : GasView) : Nat :=
  if gas.blockGas > 0 || gas.blockStateGas > 0 then gas.blockGas else gas.spentGas

def settlementInputOf (case : Case) (substate : SubstateView)
    (preRefund : Nat) (stateUsed : Int) : SettlementInput :=
  { gasLimit := case.gasLimit
    preRefundGas := preRefund
    refundCounter := substate.refund
    destroyCount := 0
    destroyRefund := case.destroyRefund
    codeInsertExecutionRefund := 0
    calldataFloorGas := case.floorGas
    stateGasUsed := stateUsed
    refundQuotient := case.refundQuotient
    isError := substate.isError
    shouldRevert := substate.shouldRevert
    isEip8037Enabled := case.eip8037
    isEip7778Enabled := case.eip7778 }

def receiptFor (case : Case) (substate : SubstateView) (spent : GasView) (failed : Bool) : Receipt :=
  let root := if case.tracingReceipt && !case.eip658 then case.stateRootAfterRecalculate else none
  if failed then
    let output := if substate.shouldRevert then substate.output else []
    let error := match substate.error with
      | some value => some value
      | none => match substate.exception with
          | EvmException.none => none
          | EvmException.outOfGas => some "OutOfGas"
    { tracing := case.tracingReceipt
      failed := true
      executingAccount := case.recipient
      spent := spent.spentGas
      output
      error
      logs := []
      root }
  else
    { tracing := case.tracingReceipt
      failed := false
      executingAccount := case.recipient
      spent := spent.spentGas
      output := substate.output
      error := none
      logs := substate.logs
      root }

def feeEvents (case : Case) (spent : GasView) : List JournalEvent :=
  let fees := case.premium * spent.spentGas
  let effectiveBaseFee := min case.baseFee case.opcodePrice
  let eip1559Fees := if !case.freeTransaction then effectiveBaseFee * spent.spentGas else 0
  let collectedBase := if case.eip1559 then eip1559Fees else 0
  let collectedBlob := if case.blobTransaction && case.eip4844FeeCollectorEnabled then case.blobBaseFee else 0
  let spec := specView case
  let collector := match case.feeCollector with
    | some address => if collectedBase + collectedBlob != 0 then [.world (.addOrCreate address (collectedBase + collectedBlob) spec)] else []
    | none => []
  let feeTrace := if case.tracingFees then [.trace (.fees fees (eip1559Fees + case.blobBaseFee))] else []
  [.world (.addOrCreate case.beneficiary fees spec)] ++ collector ++ feeTrace

def finalWorldEvents (case : Case) : List JournalEvent :=
  let spec := specView case
  if case.restore then
    [.world (.reset false)] ++
        (if case.deleteCaller then [.world (.deleteAccount case.sender)] else
        (if case.senderReserved != 0 then [.world (.add case.sender case.senderReserved spec)] else []) ++
          [.world (.decrementNonce case.sender), .world (.commit spec false false)])
  else if case.commit then
    [.world (.commit spec (!case.eip658) case.tracingState)]
  else
    [.world .resetTransient] ++ (if case.buildUp && case.eip8037 then [.world .reapEmptyAccounts] else [])

def rootEvent (case : Case) : List JournalEvent :=
  if case.tracingReceipt && !case.eip658 then
    [.world (.recalculateStateRoot case.stateRootBeforeRecalculate case.stateRootAfterRecalculate)]
  else []

def receiptEvent (case : Case) (receipt : Receipt) : List JournalEvent :=
  if !case.tracingReceipt then [] else
    if receipt.failed then
      [.receipt (.receiptFailed receipt.executingAccount receipt.spent receipt.output receipt.error receipt.root)]
    else
      [.receipt (.receiptSuccess receipt.executingAccount receipt.spent receipt.output receipt.logs receipt.root)]

def run (case : Case) : Result :=
  let selfSend := case.sender == case.recipient
  let charge := chargeGuard case selfSend
  let charged := if charge then chargedState case else { succeeded := true, gas := gasState case }
  let oog := !charged.succeeded
  let gasAfterOog := if oog then clearExecution charged.gas else charged.gas
  let write := writesGuard case selfSend oog
  let transfer := expectedTransferLog case
  let actionStart := if case.tracingActions then
      [.trace (.actionStart charged.gas.execution case.value case.sender case.recipient case.data .transaction false)] else []
  let actionCode := if case.tracingActions && case.tracingCode then [.trace (.byteCode [])] else []
  let logEvents := if logsGuard case selfSend oog then
      [.log transfer] ++ (if case.tracingLogs then [.trace (.log transfer)] else []) else []
  let substate : SubstateView :=
    { output := []
      refund := 0
      logs := if logsGuard case selfSend oog then [transfer] else []
      shouldRevert := false
      isError := false
      error := none
      exception := if oog then EvmException.outOfGas else EvmException.none }
  let terminal := if case.tracingActions then
      if oog then [.trace (.actionError EvmException.outOfGas)] else [.trace (.actionEnd gasAfterOog.execution [])] else []
  let clearEvent := if oog then [.clearExecutionGas charged.gas gasAfterOog] else []
  let stateUsed := if case.eip8037 then gasAfterOog.stateUsed else 0
  let preRefund := preRefundFor case gasAfterOog.execution gasAfterOog.reservoir
  let settlementInput := settlementInputOf case substate preRefund stateUsed
  let settlementResult := settlement settlementInput
  let spent := gasView settlementResult
  let refundAmount := (case.gasLimit - spent.spentGas) * case.opcodePrice
  let shouldValidateGas := !case.skipValidation || case.maxFeePerGas != 0 || case.maxPriorityFeePerGas != 0
  let refundEvents := if case.opcodePrice != 0 && shouldValidateGas && refundAmount != 0 then
      [.world (.add case.sender refundAmount (specView case))] else []
  let access := accessObservation case
  let accessEvents := if case.tracingAccess then [.trace (.access access.addresses access.storageCells)] else []
  let effectiveBlock := effectiveBlockGas spent
  let cumulativeExecution := if case.eip8037 then case.cumulativeExecutionGas + effectiveBlock else case.cumulativeExecutionGas
  let cumulativeState := if case.eip8037 then case.cumulativeStateGas + spent.blockStateGas else case.cumulativeStateGas
  let normalCounters := !case.skipValidation && !case.parallel
  let newHeaderGas := if normalCounters then
      if case.eip8037 then max cumulativeExecution cumulativeState else case.headerGasUsed + effectiveBlock
    else case.headerGasUsed
  let failed := oog
  let receipt := receiptFor case substate spent failed
  let fields := if case.warmup then [] else [.transaction effectiveBlock spent.spentGas]
  let headerEvents := if normalCounters then [.header newHeaderGas] else []
  { failed
    spent := spent.spentGas
    preRefundGas := preRefund
    headerGasUsed := newHeaderGas
    transactionBlockGas := if case.warmup then case.initialTransactionBlockGas else effectiveBlock
    transactionSpentGas := if case.warmup then case.initialTransactionSpentGas else spent.spentGas
    postCumulativeExecutionGas := if normalCounters then cumulativeExecution else case.cumulativeExecutionGas
    postCumulativeStateGas := if normalCounters then cumulativeState else case.cumulativeStateGas
    receipt
    journal := [.metric] ++
      (if charge then [.gasCharge charged] else []) ++
      (if write then (if case.value != 0 then [.world (.subtract case.sender (if case.warmup then min case.value case.senderBalance else case.value) (specView case))] else []) ++
        [.world (.addOrCreate case.recipient case.value (specView case))] else []) ++
      actionStart ++ actionCode ++ clearEvent ++ logEvents ++ [.substate substate] ++ terminal ++
      [.settlement spent] ++ refundEvents ++ accessEvents ++ headerEvents ++ feeEvents case spent ++
      fields ++ finalWorldEvents case ++ rootEvent case ++ receiptEvent case receipt }

end SimpleTransferCompletionExtractor.Reference
