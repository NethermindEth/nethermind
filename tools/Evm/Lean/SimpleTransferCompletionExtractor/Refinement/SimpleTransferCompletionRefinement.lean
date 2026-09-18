-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import SimpleTransferCompletionExtractor.Generated.SimpleTransferCompletion
import SimpleTransferCompletionExtractor.Reference.SimpleTransferCompletionReference

namespace SimpleTransferCompletionExtractor.Refinement

open SimpleTransferCompletionExtractor.Generated

def handoffIdentity : String := "OrdinaryPostNonceDispatchExtractor.SimpleHandoff"
def settlementIdentity : String := "Eip803x.Generated.TransactionSettlementKernel.calculate"
def stateChargeIdentity : String := "Eip803x.Generated.StateGasChargeKernel.tryCharge"
def receiptBoundary : String := "receipt-continuation-input-only"

def sourceIdentityCoherent : Prop :=
  handoffIdentity = "OrdinaryPostNonceDispatchExtractor.SimpleHandoff" ∧
    settlementIdentity = "Eip803x.Generated.TransactionSettlementKernel.calculate" ∧
    stateChargeIdentity = "Eip803x.Generated.StateGasChargeKernel.tryCharge" ∧
    receiptBoundary = "receipt-continuation-input-only"

def fitsUInt64 (value : Nat) : Prop := value ≤ Eip803x.Generated.StateGasChargeKernel.uint64Max

def uint256Max : Nat := 2 ^ 256 - 1

def fitsUInt256 (value : Nat) : Prop := value ≤ uint256Max

def fitsInt64 (value : Int) : Prop :=
  Eip803x.Generated.StateGasChargeKernel.int64Min ≤ value ∧
    value ≤ Eip803x.Generated.StateGasChargeKernel.int64Max

def allUInt64 (values : List Nat) : Prop :=
  match values with
  | [] => True
  | value :: rest => fitsUInt64 value ∧ allUInt64 rest

def allUInt256 (values : List Nat) : Prop :=
  match values with
  | [] => True
  | value :: rest => fitsUInt256 value ∧ allUInt256 rest

def allInt64 (values : List Int) : Prop :=
  match values with
  | [] => True
  | value :: rest => fitsInt64 value ∧ allInt64 rest

def arithmeticCoherent (input : CompletionInput) : Prop :=
  -- Arithmetic side conditions are stated over the input and the independently exposed kernel
  -- responses.  In particular, this premise does not inspect `Generated.run`; otherwise it would
  -- make the bounded refinement vacuous by admitting the generated result as its own oracle.
  let selfSend := Generated.sameAddress input.handoff.tx.sender input.handoff.recipient
  let chargeApplies := Generated.chargeApplies input selfSend
  let charge := Generated.stateChargeFromKernel input
  let chargedGas := if chargeApplies then charge.gas else input.handoff.gasAvailable
  let gasAfter :=
    if chargeApplies && !charge.succeeded then Generated.clearExecutionGas chargedGas else chargedGas
  let stateUsed := if input.handoff.spec.eip8037Enabled then Generated.stateGasUsed gasAfter else 0
  let preRefund := Generated.preRefundGas input.handoff.tx.gasLimit gasAfter
  let syntheticSubstate : Generated.Substate :=
    { output := []
      refund := 0
      logs := []
      shouldRevert := false
      isError := false
      error := none
      exception := if chargeApplies && !charge.succeeded then
          Generated.EvmException.outOfGas else Generated.EvmException.none }
  let settlementResponse := Generated.sourceOp_refundRequest
    (Generated.settlementInputOf input syntheticSubstate preRefund stateUsed)
  let spent := settlementResponse
  let effectiveBlock := Generated.effectiveBlockGas spent
  let stateBlock := spent.blockStateGas
  let effectiveBaseFee := min input.handoff.header.baseFeePerGas input.handoff.opcodeGasPrice
  let premiumFees := input.handoff.premiumPerGas * spent.spentGas
  let baseFees := effectiveBaseFee * spent.spentGas
  let refundGas := input.handoff.tx.gasLimit - spent.spentGas
  let refundValue := refundGas * input.handoff.opcodeGasPrice
  let collectedFees :=
    (if input.handoff.spec.eip1559Enabled then baseFees else 0) +
      (if input.handoff.tx.supportsBlobs && input.handoff.spec.eip4844FeeCollectorEnabled then input.handoff.blobBaseFee else 0)
  let senderValueTotal := input.world.senderBalance + input.handoff.tx.value
  let stateCost : Int := Generated.sourceNewAccountStateCost
  let spill := Eip803x.Generated.StateGasChargeKernel.calculateSpill
    input.handoff.gasAvailable.stateReservoir stateCost
  let stateUsedAfter := input.handoff.gasAvailable.stateGasUsed + stateCost
  let stateSpillAfter := input.handoff.gasAvailable.stateGasSpill + (spill : Int)
  let accessStorageKeys := input.handoff.tx.accessList.flatMap (fun entry => entry.storageKeys)
  allUInt64
      [ input.handoff.tx.value,
        input.world.senderBalance,
        input.handoff.tx.gasLimit,
        input.handoff.tx.maxFeePerGas,
        input.handoff.tx.maxPriorityFeePerGas,
        input.handoff.gasAvailable.execution,
        input.gas.stateCharge.gas.execution,
        charge.gas.execution,
        input.gas.newAccountStateCost,
        input.gas.preRefundGas,
        input.handoff.header.gasUsed,
        input.handoff.header.baseFeePerGas,
        input.handoff.opcodeGasPrice,
        input.handoff.premiumPerGas,
        input.handoff.senderReservedGasPayment,
        input.handoff.blobBaseFee,
        input.handoff.blockCumulativeExecutionGas,
        input.handoff.blockCumulativeStateGas,
        input.handoff.intrinsic.floorGas,
        input.handoff.intrinsic.standardGas,
        input.handoff.tx.blockGasUsed,
        input.handoff.tx.spentGas,
        spent.spentGas,
        spent.operationGas,
        spent.blockGas,
        spent.blockStateGas,
        spent.maxUsedGas,
        spent.gasRefund,
        effectiveBlock,
        input.handoff.header.gasUsed + effectiveBlock,
        input.handoff.blockCumulativeExecutionGas + effectiveBlock,
        input.handoff.blockCumulativeStateGas + stateBlock ] ∧
    spent.spentGas ≤ input.handoff.tx.gasLimit ∧
    allInt64
      [ input.handoff.gasAvailable.stateReservoir,
        input.handoff.gasAvailable.stateGasUsed,
        input.handoff.gasAvailable.stateGasSpill,
        input.handoff.gasAvailable.stateGasSpillRefunded,
        input.gas.stateCharge.gas.stateReservoir,
        input.gas.stateCharge.gas.stateGasUsed,
        input.gas.stateCharge.gas.stateGasSpill,
        input.gas.stateCharge.gas.stateGasSpillRefunded,
        stateCost,
        charge.gas.stateReservoir,
        charge.gas.stateGasUsed,
         charge.gas.stateGasSpill,
         charge.gas.stateGasSpillRefunded ] ∧
    (input.handoff.gasAvailable.stateReservoir >= stateCost →
      fitsInt64 (input.handoff.gasAvailable.stateReservoir - stateCost)) ∧
    (charge.succeeded → fitsInt64 stateUsedAfter ∧
      (input.handoff.gasAvailable.stateReservoir < stateCost → fitsInt64 stateSpillAfter)) ∧
    allUInt256
      [ input.handoff.tx.value,
        input.world.senderBalance,
        input.handoff.tx.maxFeePerGas,
        input.handoff.tx.maxPriorityFeePerGas,
        input.handoff.header.baseFeePerGas,
        input.handoff.opcodeGasPrice,
        input.handoff.premiumPerGas,
        input.handoff.senderReservedGasPayment,
        input.handoff.blobBaseFee,
        premiumFees,
        baseFees,
        refundValue,
        collectedFees,
        premiumFees + baseFees,
        baseFees + input.handoff.blobBaseFee,
        senderValueTotal ] ∧
    allUInt256 accessStorageKeys

def mapStateCharge (value : StateChargeResponse) : Reference.StateCharge :=
  { succeeded := value.succeeded
    gas :=
      { execution := value.gas.execution
        reservoir := value.gas.stateReservoir
        stateUsed := value.gas.stateGasUsed
        spill := value.gas.stateGasSpill
        spillRefunded := value.gas.stateGasSpillRefunded } }

def mapGasState (value : GasPolicy) : Reference.GasState :=
  { execution := value.execution
    reservoir := value.stateReservoir
    stateUsed := value.stateGasUsed
    spill := value.stateGasSpill
    spillRefunded := value.stateGasSpillRefunded }

def mapStorageCell (value : StorageCell) : Reference.StorageCell :=
  { address := value.address.id, key := value.key }

def mapAccessEntry (value : AccessEntry) : Reference.AccessEntry :=
  { address := value.address.id, storageKeys := value.storageKeys }

def mapSpec (value : Spec) : Reference.SpecView :=
  { eip8037 := value.eip8037Enabled
    eip7708 := value.eip7708Enabled
    eip658 := value.eip658Enabled
    eip3529 := value.eip3529Enabled
    eip7778 := value.eip7778Enabled
    eip1559 := value.eip1559Enabled
    eip4844FeeCollectorEnabled := value.eip4844FeeCollectorEnabled
    warmStorage := value.useHotAndColdStorage
    accessListEnabled := value.useTxAccessLists
    coinbaseInAccessList := value.addCoinbaseToTxAccessList
    feeCollector := value.feeCollector.map (fun address => address.id)
    destroyRefund := value.destroyRefund
    refundQuotient := value.refundQuotient }

def mapWorldRequest : WorldRequest → Reference.WorldRequest
  | .subtract address value spec => .subtract address.id value (mapSpec spec)
  | .add address value spec => .add address.id value (mapSpec spec)
  | .addOrCreate address value spec => .addOrCreate address.id value (mapSpec spec)
  | .reset resetBlockChanges => .reset resetBlockChanges
  | .deleteAccount address => .deleteAccount address.id
  | .decrementNonce address => .decrementNonce address.id
  | .commit spec commitRoots stateTracing => .commit (mapSpec spec) commitRoots stateTracing
  | .resetTransient => .resetTransient
  | .reapEmptyAccounts => .reapEmptyAccounts
  | .recalculateStateRoot before after => .recalculateStateRoot before after

def mapException : EvmException → Reference.EvmException
  | .none => .none
  | .outOfGas => .outOfGas

def mapActionKind : ActionKind → Reference.ActionKind
  | .transaction => .transaction

def mapTransferLog (value : TransferLog) : Reference.TransferLog :=
  { address := value.address.id
    topics := value.topics
    data := value.data
    fromAddress := value.fromAddress.id
    toAddress := value.toAddress.id
    amount := value.amount }

def mapTraceRequest : TraceRequest → Reference.TraceRequest
  | .actionStart gas value fromAddress toAddress data kind isPrecompileCall =>
      .actionStart gas value fromAddress.id toAddress.id data (mapActionKind kind) isPrecompileCall
  | .byteCode code => .byteCode code
  | .actionError exception => .actionError (mapException exception)
  | .actionEnd gas output => .actionEnd gas output
  | .log log => .log (mapTransferLog log)
  | .access addresses storageCells =>
      .access (addresses.map (fun address => address.id)) (storageCells.map mapStorageCell)
  | .fees fees burnt => .fees fees burnt
  | .receiptFailed account spent output error root =>
      .receiptFailed account.id spent output error root
  | .receiptSuccess account spent output logs root =>
      .receiptSuccess account.id spent output
        (logs.map mapTransferLog) root

def mapSubstate (value : Substate) : Reference.SubstateView :=
  { output := value.output
    refund := value.refund
    logs := value.logs.map mapTransferLog
    shouldRevert := value.shouldRevert
    isError := value.isError
    error := value.error
    exception := mapException value.exception }

def mapGas (value : GasConsumed) : Reference.GasView :=
  { spentGas := value.spentGas
    operationGas := value.operationGas
    blockGas := value.blockGas
    blockStateGas := value.blockStateGas
    maxUsedGas := value.maxUsedGas
    gasRefund := value.gasRefund }

def mapSettlementInput (value : SettlementInput) : Reference.SettlementInput :=
  { gasLimit := value.gasLimit
    preRefundGas := value.preRefundGas
    refundCounter := value.refundCounter
    destroyCount := value.destroyCount
    destroyRefund := value.destroyRefund
    codeInsertExecutionRefund := value.codeInsertExecutionRefund
    calldataFloorGas := value.calldataFloorGas
    stateGasUsed := value.stateGasUsed
    refundQuotient := value.refundQuotient
    isError := value.isError
    shouldRevert := value.shouldRevert
    isEip8037Enabled := value.isEip8037Enabled
    isEip7778Enabled := value.isEip7778Enabled }

theorem settlement_map_bridge (value : SettlementInput) :
    Reference.settlement (mapSettlementInput value) = Generated.settlement value := by
  rfl

theorem settlement_projection_bridge (value : SettlementInput) :
    mapGas (Generated.sourceOp_refundRequest value) =
      Reference.gasView (Reference.settlement (mapSettlementInput value)) := by
  rfl

def mapEffect : Effect → Reference.JournalEvent
  | .metricEmptyCalls => .metric
  | .gasStateCharge response => .gasCharge (mapStateCharge response)
  | .world request => .world (mapWorldRequest request)
  | .action request => .trace (mapTraceRequest request)
  | .logCreated transfer => .log (mapTransferLog transfer)
  | .substateBuilt value => .substate (mapSubstate value)
  | .clearExecutionGas before after => .clearExecutionGas (mapGasState before) (mapGasState after)
  | .settlement gas => .settlement (mapGas gas)
  | .headerGasUsed gas => .header gas
  | .transactionFields blockGas spentGas => .transaction blockGas spentGas
  | .receipt request => .receipt (mapTraceRequest request)

def mapReceipt (receipt : ReceiptContinuationInput) : Reference.Receipt :=
  { tracing := receipt.tracingReceipt
    failed := receipt.statusCode
    executingAccount := receipt.executingAccount.id
    spent := receipt.spentGas.spentGas
    output := receipt.output
    error := receipt.error
    logs := receipt.logs.map mapTransferLog
    root := receipt.stateRoot }

def mapInput (input : CompletionInput) : Reference.Case :=
  { sender := input.handoff.tx.sender.id
    recipient := input.handoff.recipient.id
    beneficiary := input.handoff.header.gasBeneficiary.id
    value := input.handoff.tx.value
    data := input.handoff.tx.data
    accessList := input.handoff.tx.accessList.map mapAccessEntry
    gasLimit := input.handoff.tx.gasLimit
    freeTransaction := input.handoff.tx.isFree
    blobTransaction := input.handoff.tx.supportsBlobs
    maxFeePerGas := input.handoff.tx.maxFeePerGas
    maxPriorityFeePerGas := input.handoff.tx.maxPriorityFeePerGas
    eip8037 := input.handoff.spec.eip8037Enabled
    eip7708 := input.handoff.spec.eip7708Enabled
    eip658 := input.handoff.spec.eip658Enabled
    eip3529 := input.handoff.spec.eip3529Enabled
    eip7778 := input.handoff.spec.eip7778Enabled
    eip1559 := input.handoff.spec.eip1559Enabled
    eip4844FeeCollectorEnabled := input.handoff.spec.eip4844FeeCollectorEnabled
    feeCollector := input.handoff.spec.feeCollector.map (fun address => address.id)
    warmStorage := input.handoff.spec.useHotAndColdStorage
    accessListEnabled := input.handoff.spec.useTxAccessLists
    coinbaseInAccessList := input.handoff.spec.addCoinbaseToTxAccessList
    restore := input.handoff.restore
    commit := input.handoff.commit
    deleteCaller := input.handoff.deleteCallerAccount
    warmup := Reference.optionIsWarmup input.handoff.options.raw
    buildUp := Reference.optionIsBuildUp input.handoff.options.raw
    skipValidation := Reference.optionIsSkipValidation input.handoff.options.raw
    parallel := input.handoff.parallel
    tracingActions := input.handoff.tracer.isTracingActions
    tracingCode := input.handoff.tracer.isTracingCode
    tracingLogs := input.handoff.tracer.isTracingLogs
    tracingAccess := input.handoff.tracer.isTracingAccess
    tracingFees := input.handoff.tracer.isTracingFees
    tracingReceipt := input.handoff.tracer.isTracingReceipt
    tracingState := input.handoff.tracer.isTracingState
    normalReturn := input.tracer.normalReturn
    recipientDead := input.world.recipientIsDead
    senderBalance := input.world.senderBalance
    gasExecution := input.handoff.gasAvailable.execution
    gasReservoir := input.handoff.gasAvailable.stateReservoir
    gasStateUsed := input.handoff.gasAvailable.stateGasUsed
    gasSpill := input.handoff.gasAvailable.stateGasSpill
    gasSpillRefunded := input.handoff.gasAvailable.stateGasSpillRefunded
    chargedSucceeded := input.gas.stateCharge.succeeded
    chargedExecution := input.gas.stateCharge.gas.execution
    chargedReservoir := input.gas.stateCharge.gas.stateReservoir
    chargedStateUsed := input.gas.stateCharge.gas.stateGasUsed
    chargedSpill := input.gas.stateCharge.gas.stateGasSpill
    chargedSpillRefunded := input.gas.stateCharge.gas.stateGasSpillRefunded
    preRefundGas := input.gas.preRefundGas
    floorGas := input.handoff.intrinsic.floorGas
    baseFee := input.handoff.header.baseFeePerGas
    opcodePrice := input.handoff.opcodeGasPrice
    premium := input.handoff.premiumPerGas
    senderReserved := input.handoff.senderReservedGasPayment
    blobBaseFee := input.handoff.blobBaseFee
    destroyRefund := input.handoff.spec.destroyRefund
    refundQuotient := input.handoff.spec.refundQuotient
    headerGasUsed := input.handoff.header.gasUsed
    initialTransactionBlockGas := input.handoff.tx.blockGasUsed
    initialTransactionSpentGas := input.handoff.tx.spentGas
    cumulativeExecutionGas := input.handoff.blockCumulativeExecutionGas
    cumulativeStateGas := input.handoff.blockCumulativeStateGas
    postCumulativeExecutionGas := input.handoff.blockCumulativeExecutionGas
    postCumulativeStateGas := input.handoff.blockCumulativeStateGas
    stateRootBeforeRecalculate := input.world.stateRootBeforeRecalculate
    stateRootAfterRecalculate := input.world.stateRootAfterRecalculate
    newAccountStateCost := input.gas.newAccountStateCost }

def mapAccessObservation (value : AccessObservation) : Reference.AccessObservation :=
  { addresses := value.addresses.map (fun address => address.id)
    storageCells := value.storageCells.map mapStorageCell }

def normalReturnDomain (input : CompletionInput) : Prop :=
  input.tracer.normalReturn = true

theorem address_mem_map (value : Address) (seen : List Address) :
    value ∈ seen ↔ value.id ∈ seen.map (fun address => address.id) := by
  induction seen with
  | nil => simp
  | cons head tail ih =>
    cases head
    cases value
    simp_all

theorem dedup_addresses_map (seen values : List Address) :
    (values.foldl (fun seen value => if value ∈ seen then seen else seen ++ [value]) seen).map
        (fun address => address.id) =
      (values.map (fun address => address.id)).foldl
        (fun seen value => if value ∈ seen then seen else seen ++ [value])
        (seen.map (fun address => address.id)) := by
  induction values generalizing seen with
  | nil => rfl
  | cons head tail ih =>
    simp only [List.foldl_cons, List.map_cons]
    by_cases h : head ∈ seen
    · have hId : head.id ∈ seen.map (fun address => address.id) := (address_mem_map head seen).1 h
      simp only [h, hId, ↓reduceIte]
      exact ih seen
    · have hId : head.id ∉ seen.map (fun address => address.id) := by
        intro hId
        apply h
        exact (address_mem_map head seen).2 hId
      simp only [h, hId, ↓reduceIte]
      simpa using ih (seen ++ [head])

theorem storage_cell_mem_map (value : StorageCell) (seen : List StorageCell) :
    value ∈ seen ↔ mapStorageCell value ∈ seen.map mapStorageCell := by
  induction seen with
  | nil => simp
  | cons head tail ih =>
    cases head with
    | mk headAddress headKey =>
      cases value with
      | mk valueAddress valueKey =>
        cases headAddress
        cases valueAddress
        simp_all [mapStorageCell]

theorem dedup_storage_cells_map (seen values : List StorageCell) :
    (values.foldl (fun seen value => if value ∈ seen then seen else seen ++ [value]) seen).map
        mapStorageCell =
      (values.map mapStorageCell).foldl
        (fun seen value => if value ∈ seen then seen else seen ++ [value])
        (seen.map mapStorageCell) := by
  induction values generalizing seen with
  | nil => rfl
  | cons head tail ih =>
    simp only [List.foldl_cons, List.map_cons]
    by_cases h : head ∈ seen
    · have hMapped : mapStorageCell head ∈ seen.map mapStorageCell :=
        (storage_cell_mem_map head seen).1 h
      simp only [h, hMapped, ↓reduceIte]
      exact ih seen
    · have hMapped : mapStorageCell head ∉ seen.map mapStorageCell := by
        intro hMapped
        apply h
        exact (storage_cell_mem_map head seen).2 hMapped
      simp only [h, hMapped, ↓reduceIte]
      simpa using ih (seen ++ [head])

theorem access_entries_bridge (input : CompletionInput) :
    (Generated.accessWarmupEntries input).map mapAccessEntry =
      Reference.accessWarmupEntries (mapInput input) := by
  cases warm : input.handoff.spec.useHotAndColdStorage with
  | false =>
    simp [Generated.accessWarmupEntries, Generated.evalSourcePredicate,
      Generated.sourceAccessHotPredicate, Reference.accessWarmupEntries, mapInput, warm]
  | true =>
    by_cases tx : input.handoff.spec.useTxAccessLists = true
    · by_cases coinbase : input.handoff.spec.addCoinbaseToTxAccessList = true
        · simp [Generated.accessWarmupEntries, Generated.evalSourcePredicate,
            Generated.sourceAccessHotPredicate, Reference.accessWarmupEntries,
            mapInput, warm, tx, coinbase]
        · simp [Generated.accessWarmupEntries, Generated.evalSourcePredicate,
            Generated.sourceAccessHotPredicate, Reference.accessWarmupEntries,
            mapInput, warm, tx, coinbase]
    · by_cases coinbase : input.handoff.spec.addCoinbaseToTxAccessList = true
      · simp [Generated.accessWarmupEntries, Generated.evalSourcePredicate,
          Generated.sourceAccessHotPredicate, Generated.sourceAccessListPredicate,
          Generated.sourceAccessCoinbasePredicate, Generated.sourceAccessRecipientPredicate,
          Reference.accessWarmupEntries, mapInput, mapAccessEntry, warm, tx, coinbase,
          List.map_append]
      · simp [Generated.accessWarmupEntries, Generated.evalSourcePredicate,
          Generated.sourceAccessHotPredicate, Generated.sourceAccessListPredicate,
          Generated.sourceAccessCoinbasePredicate, Generated.sourceAccessRecipientPredicate,
          Reference.accessWarmupEntries, mapInput, mapAccessEntry, warm, tx, coinbase,
          List.map_append]

theorem access_storage_cells_bridge (entries : List AccessEntry) :
    (Generated.accessStorageCells entries).map mapStorageCell =
      Reference.accessStorageCells (entries.map mapAccessEntry) := by
  induction entries with
  | nil => rfl
  | cons head tail ih =>
    simp only [Generated.accessStorageCells, Reference.accessStorageCells, List.flatMap_cons,
      List.map_append, List.map_cons, List.map_map]
    simpa [Generated.accessStorageCells, Reference.accessStorageCells, mapAccessEntry,
      mapStorageCell, Function.comp_def] using ih

theorem transfer_topic_projection_bridge (input : CompletionInput) :
    (Generated.sourceOp_transferLogRequest input).topics =
      [Generated.sourceTransferSignature,
        Generated.addressHashProjection input.handoff.tx.sender,
        Generated.addressHashProjection input.handoff.recipient] := by
  rfl

theorem transfer_log_bridge (input : CompletionInput) :
    mapTransferLog (Generated.sourceOp_transferLogRequest input) =
      Reference.expectedTransferLog (mapInput input) := by
  simp [mapTransferLog, Generated.sourceOp_transferLogRequest, Reference.expectedTransferLog,
    Reference.transferDataFor, mapInput, Generated.addressHashProjection]

theorem spec_view_bridge (input : CompletionInput) :
    mapSpec input.handoff.spec = Reference.specView (mapInput input) := by
  rfl

theorem pay_value_bridge (input : CompletionInput) :
    Generated.payValueAmount input =
      if (mapInput input).warmup then
        min (mapInput input).value (mapInput input).senderBalance
      else (mapInput input).value := by
  rfl

theorem access_observation_ordered_bridge (input : CompletionInput) :
    mapAccessObservation (Generated.sourceOp_accessRequest input) =
      Reference.accessObservation (mapInput input) := by
  unfold mapAccessObservation Generated.sourceOp_accessRequest Generated.accessObservation
    Reference.accessObservation
  let entries := Generated.accessWarmupEntries input
  have hEntries : entries.map mapAccessEntry = Reference.accessWarmupEntries (mapInput input) := by
    exact access_entries_bridge input
  have hAddresses :
      ((entries.map (fun entry => entry.address)).foldl
          (fun seen value => if value ∈ seen then seen else seen ++ [value]) []).map
            (fun address => address.id) =
        ((entries.map mapAccessEntry).map (fun entry => entry.address)).foldl
          (fun seen value => if value ∈ seen then seen else seen ++ [value]) [] := by
    simpa [Generated.dedupAddresses, Reference.dedupNat, mapAccessEntry, Function.comp_def] using
      (dedup_addresses_map [] (entries.map (fun entry => entry.address)))
  have hStorage :
      (Generated.dedupStorageCells (Generated.accessStorageCells entries)).map mapStorageCell =
        Reference.dedupStorageCells (Reference.accessStorageCells (entries.map mapAccessEntry)) := by
    rw [← access_storage_cells_bridge entries]
    simpa [Generated.dedupStorageCells, Reference.dedupStorageCells] using
      (dedup_storage_cells_map [] (Generated.accessStorageCells entries))
  dsimp [entries] at hEntries hAddresses hStorage ⊢
  simp only [Generated.dedupAddresses, Reference.dedupNat, hAddresses, hStorage, hEntries]

theorem access_observation_extensional_bridge (input : CompletionInput) :
    Reference.accessObservationExtensional
      (mapAccessObservation (Generated.sourceOp_accessRequest input))
      (Reference.accessObservation (mapInput input)) := by
  rw [access_observation_ordered_bridge input]
  simp [Reference.accessObservationExtensional]

theorem access_observation_bridge (input : CompletionInput) :
    Reference.accessObservationExtensional
      (mapAccessObservation (Generated.sourceOp_accessRequest input))
      (Reference.accessObservation (mapInput input)) := by
  exact access_observation_extensional_bridge input

theorem access_addresses_bridge (input : CompletionInput) :
    (Generated.sourceOp_accessRequest input).addresses.map (fun address => address.id) =
      (Reference.accessObservation (mapInput input)).addresses := by
  exact congrArg (fun value : Reference.AccessObservation => value.addresses)
    (access_observation_ordered_bridge input)

theorem access_storage_bridge (input : CompletionInput) :
    (Generated.sourceOp_accessRequest input).storageCells.map mapStorageCell =
      (Reference.accessObservation (mapInput input)).storageCells := by
  exact congrArg (fun value : Reference.AccessObservation => value.storageCells)
    (access_observation_ordered_bridge input)

theorem settlement_input_bridge (input : CompletionInput) (substate : Substate)
    (preRefund : Nat) (stateUsed : Int) :
    mapSettlementInput (Generated.settlementInputOf input substate preRefund stateUsed) =
      Reference.settlementInputOf (mapInput input) (mapSubstate substate)
        preRefund stateUsed := by
  rfl

theorem settlement_result_bridge (input : CompletionInput) (substate : Substate)
    (preRefund : Nat) (stateUsed : Int) :
    Reference.settlement
        (Reference.settlementInputOf (mapInput input) (mapSubstate substate)
          preRefund stateUsed) =
      Generated.settlement
        (Generated.settlementInputOf input substate preRefund stateUsed) := by
  rw [← settlement_input_bridge input substate preRefund stateUsed]
  exact settlement_map_bridge _

theorem settlement_gas_bridge (input : CompletionInput) (substate : Substate)
    (preRefund : Nat) (stateUsed : Int) :
    Reference.gasView
        (Reference.settlement
          (Reference.settlementInputOf (mapInput input) (mapSubstate substate)
            preRefund stateUsed)) =
      mapGas
        (Generated.sourceOp_refundRequest
          (Generated.settlementInputOf input substate preRefund stateUsed)) := by
  rw [settlement_result_bridge input substate preRefund stateUsed]
  rfl

theorem settlement_spent_bridge (input : CompletionInput) (substate : Substate)
    (preRefund : Nat) (stateUsed : Int) :
    (Generated.sourceOp_refundRequest
        (Generated.settlementInputOf input substate preRefund stateUsed)).spentGas =
      (Reference.gasView
        (Reference.settlement
          (Reference.settlementInputOf (mapInput input) (mapSubstate substate)
            preRefund stateUsed))).spentGas := by
  exact congrArg (fun value : Reference.GasView => value.spentGas)
    (settlement_gas_bridge input substate preRefund stateUsed).symm

theorem settlement_operation_bridge (input : CompletionInput) (substate : Substate)
    (preRefund : Nat) (stateUsed : Int) :
    (Generated.sourceOp_refundRequest
        (Generated.settlementInputOf input substate preRefund stateUsed)).operationGas =
      (Reference.gasView
        (Reference.settlement
          (Reference.settlementInputOf (mapInput input) (mapSubstate substate)
            preRefund stateUsed))).operationGas := by
  exact congrArg (fun value : Reference.GasView => value.operationGas)
    (settlement_gas_bridge input substate preRefund stateUsed).symm

theorem settlement_block_bridge (input : CompletionInput) (substate : Substate)
    (preRefund : Nat) (stateUsed : Int) :
    (Generated.sourceOp_refundRequest
        (Generated.settlementInputOf input substate preRefund stateUsed)).blockGas =
      (Reference.gasView
        (Reference.settlement
          (Reference.settlementInputOf (mapInput input) (mapSubstate substate)
            preRefund stateUsed))).blockGas := by
  exact congrArg (fun value : Reference.GasView => value.blockGas)
    (settlement_gas_bridge input substate preRefund stateUsed).symm

theorem settlement_state_block_bridge (input : CompletionInput) (substate : Substate)
    (preRefund : Nat) (stateUsed : Int) :
    (Generated.sourceOp_refundRequest
        (Generated.settlementInputOf input substate preRefund stateUsed)).blockStateGas =
      (Reference.gasView
        (Reference.settlement
          (Reference.settlementInputOf (mapInput input) (mapSubstate substate)
            preRefund stateUsed))).blockStateGas := by
  exact congrArg (fun value : Reference.GasView => value.blockStateGas)
    (settlement_gas_bridge input substate preRefund stateUsed).symm

theorem settlement_max_used_bridge (input : CompletionInput) (substate : Substate)
    (preRefund : Nat) (stateUsed : Int) :
    (Generated.sourceOp_refundRequest
        (Generated.settlementInputOf input substate preRefund stateUsed)).maxUsedGas =
      (Reference.gasView
        (Reference.settlement
          (Reference.settlementInputOf (mapInput input) (mapSubstate substate)
            preRefund stateUsed))).maxUsedGas := by
  exact congrArg (fun value : Reference.GasView => value.maxUsedGas)
    (settlement_gas_bridge input substate preRefund stateUsed).symm

theorem settlement_refund_bridge (input : CompletionInput) (substate : Substate)
    (preRefund : Nat) (stateUsed : Int) :
    (Generated.sourceOp_refundRequest
        (Generated.settlementInputOf input substate preRefund stateUsed)).gasRefund =
      (Reference.gasView
        (Reference.settlement
          (Reference.settlementInputOf (mapInput input) (mapSubstate substate)
            preRefund stateUsed))).gasRefund := by
  exact congrArg (fun value : Reference.GasView => value.gasRefund)
    (settlement_gas_bridge input substate preRefund stateUsed).symm

theorem effective_block_bridge (input : CompletionInput) (substate : Substate)
    (preRefund : Nat) (stateUsed : Int) :
    Generated.effectiveBlockGas
        (Generated.sourceOp_refundRequest
          (Generated.settlementInputOf input substate preRefund stateUsed)) =
      Reference.effectiveBlockGas
        (Reference.gasView
          (Reference.settlement
            (Reference.settlementInputOf (mapInput input) (mapSubstate substate)
              preRefund stateUsed))) := by
  rw [Generated.effectiveBlockGas]
  rw [Reference.effectiveBlockGas]
  rw [settlement_block_bridge input substate preRefund stateUsed]
  rw [settlement_state_block_bridge input substate preRefund stateUsed]
  rw [settlement_spent_bridge input substate preRefund stateUsed]

theorem exception_message_bridge (value : EvmException) :
    (match value with
      | .none => none
      | .outOfGas => some "OutOfGas") =
      (match mapException value with
        | Reference.EvmException.none => none
        | Reference.EvmException.outOfGas => some "OutOfGas") := by
  cases value <;> rfl

theorem receipt_error_bridge (error : Option String) (exception : EvmException) :
    (match error with
      | some value => some value
      | none =>
        match exception with
        | .none => none
        | .outOfGas => some "OutOfGas") =
      (match error with
        | some value => some value
        | none =>
          match mapException exception with
          | Reference.EvmException.none => none
          | Reference.EvmException.outOfGas => some "OutOfGas") := by
  cases error <;> cases exception <;> rfl

theorem gas_state_shape (gas : GasPolicy) :
    ({ execution := gas.execution
       reservoir := gas.stateReservoir
       stateUsed := gas.stateGasUsed
       spill := gas.stateGasSpill
       spillRefunded := gas.stateGasSpillRefunded } : Reference.GasState) = mapGasState gas := by
  rfl

theorem state_charge_gas_shape (value : StateChargeResponse) :
    (mapStateCharge value).gas = mapGasState value.gas := by
  rfl

theorem receipt_input_bridge (input : CompletionInput) (substate : Substate)
    (spent : GasConsumed) (statusFailure : Bool) :
    mapReceipt (Generated.receiptInput input substate spent statusFailure) =
      Reference.receiptFor (mapInput input) (mapSubstate substate) (mapGas spent) statusFailure := by
  cases statusFailure
  · rfl
  · simp [mapReceipt, Generated.receiptInput, Reference.receiptFor, mapSubstate, mapGas,
      mapException, mapInput] <;>
      cases substate.error <;> cases substate.exception <;> simp <;> rfl

theorem receipt_settlement_bridge (input : CompletionInput) (substate : Substate)
    (preRefund : Nat) (stateUsed : Int) (statusFailure : Bool) :
    mapReceipt
        (Generated.receiptInput input substate
          (Generated.sourceOp_refundRequest
            (Generated.settlementInputOf input substate preRefund stateUsed)) statusFailure) =
      Reference.receiptFor (mapInput input) (mapSubstate substate)
        (Reference.gasView
          (Reference.settlement
            (Reference.settlementInputOf (mapInput input) (mapSubstate substate)
              preRefund stateUsed))) statusFailure := by
  rw [receipt_input_bridge]
  rw [← settlement_gas_bridge]

theorem pre_refund_bridge (input : CompletionInput) (gas : GasPolicy) :
    Generated.preRefundGas input.handoff.tx.gasLimit gas =
      Reference.preRefundFor (mapInput input) gas.execution gas.stateReservoir := by
  rfl

theorem charged_state_bridge (input : CompletionInput) :
    Reference.chargedState (mapInput input) = mapStateCharge input.gas.stateCharge := by
  rfl

theorem gas_state_bridge (input : CompletionInput) :
    Reference.gasState (mapInput input) = mapGasState input.handoff.gasAvailable := by
  rfl

theorem charge_guard_bridge (input : CompletionInput) :
    Generated.chargeApplies input (Generated.sameAddress input.handoff.tx.sender input.handoff.recipient) =
      Reference.chargeGuard (mapInput input) (Generated.sameAddress input.handoff.tx.sender input.handoff.recipient) := by
  rfl

theorem write_guard_bridge (input : CompletionInput) (selfSend outOfGas : Bool) :
    Generated.writeApplies input selfSend outOfGas =
      Reference.writesGuard (mapInput input) selfSend outOfGas := by
  rfl

theorem log_guard_bridge (input : CompletionInput) (selfSend outOfGas : Bool) :
    Generated.logApplies input selfSend outOfGas =
      Reference.logsGuard (mapInput input) selfSend outOfGas := by
  rfl

theorem substate_bridge (input : CompletionInput) (selfSend outOfGas : Bool)
    mapSubstate
        (Generated.sourceOp_substateConstruction
          (if Generated.logApplies input selfSend outOfGas = true then
            [Generated.sourceOp_transferLogRequest input] else []) outOfGas) =
      { output := []
        refund := 0
        logs :=
          if Reference.logsGuard (mapInput input) selfSend outOfGas = true then
            [Reference.expectedTransferLog (mapInput input)] else []
        shouldRevert := false
        isError := false
        error := none
        exception := if outOfGas then Reference.EvmException.outOfGas else Reference.EvmException.none } := by
  cases outOfGas with
  | false =>
    by_cases logs : Reference.logsGuard (mapInput input) selfSend false = true
    · have generatedLogs : Generated.logApplies input selfSend false = true := by
        rw [log_guard_bridge]
        exact logs
      simp [mapSubstate, Generated.sourceOp_substateConstruction, logs, generatedLogs,
        mapException, transfer_log_bridge input]
    · have generatedLogs : ¬Generated.logApplies input selfSend false = true := by
        intro value
        apply logs
        rw [← log_guard_bridge]
        exact value
      simp [mapSubstate, Generated.sourceOp_substateConstruction, logs, generatedLogs,
        mapException, transfer_log_bridge input]
  | true =>
    by_cases logs : Reference.logsGuard (mapInput input) selfSend true = true
    · have generatedLogs : Generated.logApplies input selfSend true = true := by
        rw [log_guard_bridge]
        exact logs
      simp [mapSubstate, Generated.sourceOp_substateConstruction, logs, generatedLogs,
        mapException, transfer_log_bridge input]
    · have generatedLogs : ¬Generated.logApplies input selfSend true = true := by
        intro value
        apply logs
        rw [← log_guard_bridge]
        exact value
      simp [mapSubstate, Generated.sourceOp_substateConstruction, logs, generatedLogs,
        mapException, transfer_log_bridge input]

theorem actions_guard_bridge (input : CompletionInput) (selfSend outOfGas : Bool) :
    Generated.evalSourcePredicate Generated.sourceActionsPredicate input selfSend outOfGas =
      (mapInput input).tracingActions := by
  rfl

theorem counters_guard_bridge (input : CompletionInput) (selfSend outOfGas : Bool) :
    Generated.evalSourcePredicate Generated.sourceCountersPredicate input selfSend outOfGas =
      (!((mapInput input).skipValidation) && !((mapInput input).parallel)) := by
  rfl

theorem restore_guard_bridge (input : CompletionInput) (selfSend outOfGas : Bool) :
    Generated.evalSourcePredicate Generated.sourceRestorePredicate input selfSend outOfGas =
      (mapInput input).restore := by
  rfl

theorem commit_guard_bridge (input : CompletionInput) (selfSend outOfGas : Bool) :
    Generated.evalSourcePredicate Generated.sourceCommitPredicate input selfSend outOfGas =
      (mapInput input).commit := by
  rfl

theorem receipt_guard_bridge (input : CompletionInput) (selfSend outOfGas : Bool) :
    Generated.evalSourcePredicate Generated.sourceReceiptPredicate input selfSend outOfGas =
      (mapInput input).tracingReceipt := by
  rfl

theorem access_hot_guard_bridge (input : CompletionInput) :
    Generated.evalSourcePredicate Generated.sourceAccessHotPredicate input false false =
      !((mapInput input).warmStorage) := by
  rfl

theorem access_list_guard_bridge (input : CompletionInput) :
    Generated.evalSourcePredicate Generated.sourceAccessListPredicate input false false =
      (mapInput input).accessListEnabled := by
  rfl

theorem access_coinbase_guard_bridge (input : CompletionInput) :
    Generated.evalSourcePredicate Generated.sourceAccessCoinbasePredicate input false false =
      (mapInput input).coinbaseInAccessList := by
  rfl

theorem access_recipient_guard_bridge (input : CompletionInput) :
    Generated.evalSourcePredicate Generated.sourceAccessRecipientPredicate input false false = true := by
  rfl

theorem build_up_guard_bridge (input : CompletionInput) :
    Generated.evalSourcePredicate Generated.sourceBuildUpPredicate input false false =
      (mapInput input).buildUp := by
  rfl

theorem charge_condition_bridge (input : CompletionInput) :
    (Generated.chargeApplies input
        (Generated.sameAddress input.handoff.tx.sender input.handoff.recipient) = true) =
      (Reference.chargeGuard (mapInput input)
        ((mapInput input).sender == (mapInput input).recipient) = true) := by
  rw [charge_guard_bridge input]
  rfl

theorem write_condition_bridge (input : CompletionInput) (outOfGas : Bool) :
    (Generated.writeApplies input
        (Generated.sameAddress input.handoff.tx.sender input.handoff.recipient) outOfGas = true) =
      (Reference.writesGuard (mapInput input)
        ((mapInput input).sender == (mapInput input).recipient) outOfGas = true) := by
  rw [write_guard_bridge input
    (Generated.sameAddress input.handoff.tx.sender input.handoff.recipient) outOfGas]
  rfl

theorem log_condition_bridge (input : CompletionInput) (outOfGas : Bool) :
    (Generated.logApplies input
        (Generated.sameAddress input.handoff.tx.sender input.handoff.recipient) outOfGas = true) =
      (Reference.logsGuard (mapInput input)
        ((mapInput input).sender == (mapInput input).recipient) outOfGas = true) := by
  rw [log_guard_bridge input
    (Generated.sameAddress input.handoff.tx.sender input.handoff.recipient) outOfGas]
  rfl

theorem actions_condition_bridge (input : CompletionInput) (outOfGas : Bool) :
    (Generated.evalSourcePredicate Generated.sourceActionsPredicate input
        (Generated.sameAddress input.handoff.tx.sender input.handoff.recipient) outOfGas = true) =
      ((mapInput input).tracingActions = true) := by
  rw [actions_guard_bridge input
    (Generated.sameAddress input.handoff.tx.sender input.handoff.recipient) outOfGas]

theorem counters_condition_bridge (input : CompletionInput) (outOfGas : Bool) :
    (Generated.evalSourcePredicate Generated.sourceCountersPredicate input
        (Generated.sameAddress input.handoff.tx.sender input.handoff.recipient) outOfGas = true) =
      ((!((mapInput input).skipValidation) && !((mapInput input).parallel)) = true) := by
  rw [counters_guard_bridge input
    (Generated.sameAddress input.handoff.tx.sender input.handoff.recipient) outOfGas]

theorem restore_condition_bridge (input : CompletionInput) (outOfGas : Bool) :
    (Generated.evalSourcePredicate Generated.sourceRestorePredicate input
        (Generated.sameAddress input.handoff.tx.sender input.handoff.recipient) outOfGas = true) =
      ((mapInput input).restore = true) := by
  rw [restore_guard_bridge input
    (Generated.sameAddress input.handoff.tx.sender input.handoff.recipient) outOfGas]

theorem commit_condition_bridge (input : CompletionInput) (outOfGas : Bool) :
    (Generated.evalSourcePredicate Generated.sourceCommitPredicate input
        (Generated.sameAddress input.handoff.tx.sender input.handoff.recipient) outOfGas = true) =
      ((mapInput input).commit = true) := by
  rw [commit_guard_bridge input
    (Generated.sameAddress input.handoff.tx.sender input.handoff.recipient) outOfGas]

theorem receipt_condition_bridge (input : CompletionInput) (outOfGas : Bool) :
    (Generated.evalSourcePredicate Generated.sourceReceiptPredicate input
        (Generated.sameAddress input.handoff.tx.sender input.handoff.recipient) outOfGas = true) =
      ((mapInput input).tracingReceipt = true) := by
  rw [receipt_guard_bridge input
    (Generated.sameAddress input.handoff.tx.sender input.handoff.recipient) outOfGas]

theorem build_up_condition_bridge (input : CompletionInput) :
    (Generated.buildUpApplies input = true) = ((mapInput input).buildUp = true) := by
  simpa [Generated.buildUpApplies] using
    congrArg (fun value : Bool => value = true) (build_up_guard_bridge input)

theorem access_tracing_bridge (input : CompletionInput) :
    (input.handoff.tracer.isTracingAccess = true) = ((mapInput input).tracingAccess = true) := by
  rfl

theorem fees_tracing_bridge (input : CompletionInput) :
    (input.handoff.tracer.isTracingFees = true) = ((mapInput input).tracingFees = true) := by
  rfl

theorem warmup_condition_bridge (input : CompletionInput) :
    (Generated.isWarmup input.handoff.options = true) =
      ((mapInput input).warmup = true) := by
  rfl

theorem map_world_effect (request : WorldRequest) :
    ([Effect.world request] : List Effect).map mapEffect =
      [.world (mapWorldRequest request)] := by
  rfl

theorem map_trace_effect (request : TraceRequest) :
    ([Effect.action request] : List Effect).map mapEffect =
      [.trace (mapTraceRequest request)] := by
  rfl

theorem map_if_world_effect (condition : Bool) (request : WorldRequest) :
    (if condition then [Effect.world request] else []).map mapEffect =
      if condition then [.world (mapWorldRequest request)] else [] := by
  cases condition <;> rfl

theorem map_if_trace_effect (condition : Bool) (request : TraceRequest) :
    (if condition then [Effect.action request] else []).map mapEffect =
      if condition then [.trace (mapTraceRequest request)] else [] := by
  cases condition <;> rfl

theorem map_if_world_prop_effect (condition : Prop) [Decidable condition]
    (request : WorldRequest) :
    (if condition then [Effect.world request] else []).map mapEffect =
      if condition then [.world (mapWorldRequest request)] else [] := by
  by_cases value : condition <;> simp [value, mapEffect]

theorem map_if_trace_prop_effect (condition : Prop) [Decidable condition]
    (request : TraceRequest) :
    (if condition then [Effect.action request] else []).map mapEffect =
      if condition then [.trace (mapTraceRequest request)] else [] := by
  by_cases value : condition <;> simp [value, mapEffect]

theorem map_if_gas_charge_prop (condition : Prop) [Decidable condition]
    (response : StateChargeResponse) :
    (if condition then [Effect.gasStateCharge response] else []).map mapEffect =
      if condition then [.gasCharge (mapStateCharge response)] else [] := by
  by_cases value : condition <;> simp [value, mapEffect]

theorem map_if_clear_prop (condition : Prop) [Decidable condition]
    (before after : GasPolicy) :
    (if condition then [Effect.clearExecutionGas before after] else []).map mapEffect =
      if condition then [.clearExecutionGas (mapGasState before) (mapGasState after)] else [] := by
  by_cases value : condition <;> simp [value, mapEffect]

theorem map_if_log_prop (condition : Prop) [Decidable condition] (log : TransferLog) :
    (if condition then [Effect.logCreated log] else []).map mapEffect =
      if condition then [.log (mapTransferLog log)] else [] := by
  by_cases value : condition <;> simp [value, mapEffect]

theorem map_if_action_prop (condition : Prop) [Decidable condition]
    (request : TraceRequest) :
    (if condition then [Effect.action request] else []).map mapEffect =
      if condition then [.trace (mapTraceRequest request)] else [] := by
  by_cases value : condition <;> simp [value, mapEffect]

theorem map_if_header_prop (condition : Prop) [Decidable condition] (gas : Nat) :
    (if condition then [Effect.headerGasUsed gas] else []).map mapEffect =
      if condition then [.header gas] else [] := by
  by_cases value : condition <;> simp [value, mapEffect]

theorem map_if_transaction_prop (condition : Prop) [Decidable condition]
    (blockGas spentGas : Nat) :
    (if condition then [Effect.transactionFields blockGas spentGas] else []).map mapEffect =
      if condition then [.transaction blockGas spentGas] else [] := by
  by_cases value : condition <;> simp [value, mapEffect]

theorem map_if_receipt_prop (condition : Prop) [Decidable condition]
    (request : TraceRequest) :
    (if condition then [Effect.receipt request] else []).map mapEffect =
      if condition then [.receipt (mapTraceRequest request)] else [] := by
  by_cases value : condition <;> simp [value, mapEffect]

theorem map_pay_value_effect (input : CompletionInput) (write : Bool) :
    (Generated.sourceOp_payValueRequest input write).map mapEffect =
      if write && input.handoff.tx.value != 0 then
        [.world (.subtract input.handoff.tx.sender.id
          (Generated.payValueAmount input) (mapSpec input.handoff.spec))]
      else [] := by
  cases write with
  | false => rfl
  | true =>
    by_cases value : (input.handoff.tx.value != 0) = true
    · simp only [Generated.sourceOp_payValueRequest, value, Bool.true_and]
      rw [map_world_effect]
      simp [mapWorldRequest, mapSpec]
    · simp only [Generated.sourceOp_payValueRequest, value, Bool.true_and]
      rfl

theorem map_add_recipient_effect (input : CompletionInput) (write : Bool) :
    (Generated.sourceOp_addRecipientRequest input write).map mapEffect =
      if write then
        [.world (.addOrCreate input.handoff.recipient.id input.handoff.tx.value
          (mapSpec input.handoff.spec))]
      else [] := by
  cases write with
  | false => rfl
  | true =>
    simp only [Generated.sourceOp_addRecipientRequest]
    rw [map_world_effect]
    simp [mapWorldRequest, mapSpec]

theorem map_action_start_effect (input : CompletionInput) (selfSend outOfGas : Bool)
    (gasAfterCharge : GasPolicy) :
    (Generated.sourceOp_actionStartRequest input selfSend outOfGas gasAfterCharge).map mapEffect =
      if input.handoff.tracer.isTracingActions then
        [.trace (.actionStart gasAfterCharge.execution input.handoff.tx.value
          input.handoff.tx.sender.id input.handoff.recipient.id input.handoff.tx.data
          .transaction false)] ++
          (if input.handoff.tracer.isTracingCode then
            [.trace (.byteCode [])]
          else [])
      else [] := by
  by_cases actions : input.handoff.tracer.isTracingActions = true
  · by_cases code : input.handoff.tracer.isTracingCode = true
    · simp [actions, code, Generated.sourceOp_actionStartRequest, Generated.gasRemaining,
        Generated.evalSourcePredicate, Generated.sourceActionsPredicate,
        mapEffect, mapTraceRequest, mapActionKind]
    · simp [actions, code, Generated.sourceOp_actionStartRequest, Generated.gasRemaining,
        Generated.evalSourcePredicate, Generated.sourceActionsPredicate,
        mapEffect, mapTraceRequest, mapActionKind]
  · simp [actions, Generated.sourceOp_actionStartRequest,
      Generated.evalSourcePredicate, Generated.sourceActionsPredicate]

theorem map_clear_execution_effect (before after : GasPolicy) :
    ([Effect.clearExecutionGas before after] : List Effect).map mapEffect =
      [.clearExecutionGas (mapGasState before) (mapGasState after)] := by
  rfl

theorem map_log_effect (log : TransferLog) (predicate : Bool) :
    (if predicate then [Effect.logCreated log] else []).map mapEffect =
      if predicate then [.log (mapTransferLog log)] else [] := by
  cases predicate <;> rfl

theorem map_log_trace_effect (log : TransferLog) (predicate : Bool) :
    (if predicate then [Effect.action (.log log)] else []).map mapEffect =
      if predicate then [.trace (.log (mapTransferLog log))] else [] := by
  cases predicate <;> rfl

theorem map_substate_effect (substate : Substate) :
    ([Effect.substateBuilt substate] : List Effect).map mapEffect =
      [.substate (mapSubstate substate)] := by
  rfl

theorem map_action_error_effect (exception : EvmException) :
    ([Effect.action (.actionError exception)] : List Effect).map mapEffect =
      [.trace (.actionError (mapException exception))] := by
  rfl

theorem map_action_end_effect (gas : Nat) (output : List Nat) :
    ([Effect.action (.actionEnd gas output)] : List Effect).map mapEffect =
      [.trace (.actionEnd gas output)] := by
  rfl

theorem map_action_terminal_effect (actions outOfGas : Bool) (gas : GasPolicy) :
    (if actions then
      if outOfGas then [Effect.action (.actionError EvmException.outOfGas)]
      else [Effect.action (.actionEnd gas.execution [])]
    else []).map mapEffect =
      if actions then
        if outOfGas then [.trace (.actionError Reference.EvmException.outOfGas)]
        else [.trace (.actionEnd gas.execution [])]
      else [] := by
  cases actions <;> cases outOfGas <;> rfl

theorem map_action_terminal_prop (actions outOfGas : Prop) [Decidable actions] [Decidable outOfGas]
    (gas : GasPolicy) :
    (if actions then
      if outOfGas then [Effect.action (.actionError EvmException.outOfGas)]
      else [Effect.action (.actionEnd gas.execution [])]
    else []).map mapEffect =
      if actions then
        if outOfGas then [.trace (.actionError Reference.EvmException.outOfGas)]
        else [.trace (.actionEnd gas.execution [])]
      else [] := by
  by_cases hActions : actions <;> by_cases hOutOfGas : outOfGas <;>
    simp [hActions, hOutOfGas, mapEffect, mapTraceRequest, mapException]

theorem map_clear_if_effect (condition : Bool) (before after : GasPolicy) :
    (if condition then [Effect.clearExecutionGas before after] else []).map mapEffect =
      if condition then [.clearExecutionGas (mapGasState before) (mapGasState after)] else [] := by
  cases condition <;> rfl

theorem map_settlement_effect (input : CompletionInput) (substate : Substate)
    (preRefund : Nat) (stateUsed : Int) :
    ([Effect.settlement
        (Generated.sourceOp_refundRequest
          (Generated.settlementInputOf input substate preRefund stateUsed))] : List Effect).map mapEffect =
      [.settlement
        (Reference.gasView
          (Reference.settlement
            (Reference.settlementInputOf (mapInput input) (mapSubstate substate)
              preRefund stateUsed)))] := by
  simp only [List.map_cons, List.map_nil, mapEffect]
  rw [← settlement_gas_bridge input substate preRefund stateUsed]

-- This stronger equality is only a local normalization lemma for the finite generated carrier.
-- The public observation theorem below deliberately exposes the HashSet-compatible extensional
-- relation; callers must not depend on this carrier's enumeration order.
theorem map_access_effect_ordered (input : CompletionInput) :
    (if input.handoff.tracer.isTracingAccess then
      [Effect.action (.access (Generated.sourceOp_accessRequest input).addresses
        (Generated.sourceOp_accessRequest input).storageCells)]
    else []).map mapEffect =
      if (mapInput input).tracingAccess then
        [.trace (.access (Reference.accessObservation (mapInput input)).addresses
          (Reference.accessObservation (mapInput input)).storageCells)]
      else [] := by
  by_cases tracing : input.handoff.tracer.isTracingAccess = true
  · have hAddresses := access_addresses_bridge input
    have hStorage := access_storage_bridge input
    simp [tracing, mapInput, mapEffect, mapTraceRequest, hAddresses, hStorage]
  · simp [tracing, mapInput]

theorem map_access_effect (input : CompletionInput) :
    Reference.journalExtensional
      ((if input.handoff.tracer.isTracingAccess then
        [Effect.action (.access (Generated.sourceOp_accessRequest input).addresses
          (Generated.sourceOp_accessRequest input).storageCells)]
      else []).map mapEffect)
      (if (mapInput input).tracingAccess then
        [.trace (.access (Reference.accessObservation (mapInput input)).addresses
          (Reference.accessObservation (mapInput input)).storageCells)]
      else []) := by
  by_cases tracing : input.handoff.tracer.isTracingAccess = true
  · simpa [tracing, mapInput, mapEffect, mapTraceRequest, mapAccessObservation,
      Generated.sourceOp_accessRequest, Generated.accessObservation,
      Reference.journalExtensional, Reference.journalEventExtensional,
      Reference.traceRequestExtensional] using (access_observation_bridge input)
  · simp [tracing, mapInput, mapEffect, Reference.journalExtensional]

theorem map_fee_effect (input : CompletionInput) (substate : Substate)
    (spent : GasConsumed) (statusFailure : Bool) :
    (Generated.sourceOp_headerAndFeesEffects input substate spent statusFailure).map mapEffect =
      Reference.feeEvents (mapInput input) (mapGas spent) := by
  cases collector : input.handoff.spec.feeCollector with
  | none =>
    simp [collector, Generated.sourceOp_headerAndFeesEffects, Generated.feeEffects,
      Generated.feeCollectorRequest, Reference.feeEvents, mapEffect,
      mapWorldRequest, mapTraceRequest, mapSpec, mapGas, mapInput,
      Reference.specView]
  | some address =>
    simp [collector, Generated.sourceOp_headerAndFeesEffects, Generated.feeEffects,
      Generated.feeCollectorRequest, Reference.feeEvents, mapEffect,
      mapWorldRequest, mapTraceRequest, mapSpec, mapGas, mapInput,
      Reference.specView]
    rw [map_if_world_prop_effect]
    simp [collector, mapWorldRequest, mapSpec]

theorem map_final_world_effect (input : CompletionInput) (selfSend outOfGas : Bool) :
    (Generated.sourceOp_finalizeRequestWorld input selfSend outOfGas).map mapEffect =
      Reference.finalWorldEvents (mapInput input) := by
  by_cases restore : input.handoff.restore = true
  · by_cases deleteCaller : input.handoff.deleteCallerAccount = true
     · simp [restore, deleteCaller, Generated.sourceOp_finalizeRequestWorld,
         Generated.evalSourcePredicate, Generated.sourceRestorePredicate,
         Reference.finalWorldEvents, mapEffect, mapWorldRequest, mapInput]
    · by_cases reserved : input.handoff.senderReservedGasPayment = 0
       · simp [restore, deleteCaller, reserved, Generated.sourceOp_finalizeRequestWorld,
           Generated.evalSourcePredicate, Generated.sourceRestorePredicate,
           Reference.finalWorldEvents, mapEffect, mapWorldRequest, mapSpec, mapInput,
           Reference.specView]
       · simp [restore, deleteCaller, reserved, Generated.sourceOp_finalizeRequestWorld,
           Generated.evalSourcePredicate, Generated.sourceRestorePredicate,
           Reference.finalWorldEvents, mapEffect, mapWorldRequest, mapSpec, mapInput,
           Reference.specView]
  · by_cases commit : input.handoff.commit = true
    · simp [restore, commit, Generated.sourceOp_finalizeRequestWorld,
        Generated.evalSourcePredicate, Generated.sourceRestorePredicate,
        Generated.sourceCommitPredicate, Reference.finalWorldEvents, mapEffect,
        mapWorldRequest, mapSpec, mapInput, Reference.specView]
    · by_cases buildUp : input.handoff.options.raw = Generated.executionOptionBuildUp
      · by_cases eip8037 : input.handoff.spec.eip8037Enabled = true
         · simp [restore, commit, buildUp, eip8037, Generated.sourceOp_finalizeRequestWorld,
             Generated.evalSourcePredicate, Generated.sourceRestorePredicate,
             Generated.sourceCommitPredicate, Generated.sourceBuildUpPredicate,
             Generated.buildUpApplies,
             Reference.finalWorldEvents, Reference.optionIsBuildUp, Reference.buildUpOption,
             Generated.executionOptionBuildUp,
             mapEffect, mapWorldRequest, mapInput]
         · simp [restore, commit, buildUp, eip8037, Generated.sourceOp_finalizeRequestWorld,
             Generated.evalSourcePredicate, Generated.sourceRestorePredicate,
             Generated.sourceCommitPredicate, Generated.sourceBuildUpPredicate,
             Generated.buildUpApplies,
             Reference.finalWorldEvents, Reference.optionIsBuildUp, Reference.buildUpOption,
             Generated.executionOptionBuildUp,
             mapEffect, mapWorldRequest, mapSpec, mapInput,
             Reference.specView]
       · simp [restore, commit, Generated.sourceOp_finalizeRequestWorld,
           Generated.evalSourcePredicate, Generated.sourceRestorePredicate,
           Generated.sourceCommitPredicate, Generated.sourceBuildUpPredicate,
           Generated.buildUpApplies, map_if_world_prop_effect,
           Reference.finalWorldEvents, Reference.optionIsBuildUp, Reference.buildUpOption,
           Generated.executionOptionBuildUp,
           mapEffect, mapWorldRequest, mapInput]

theorem map_root_effect (input : CompletionInput) (selfSend outOfGas : Bool) :
    (if (Generated.evalSourcePredicate Generated.sourceReceiptPredicate input selfSend outOfGas &&
        !input.handoff.spec.eip658Enabled) = true then
      [Effect.world (.recalculateStateRoot input.world.stateRootBeforeRecalculate
        input.world.stateRootAfterRecalculate)]
    else []).map mapEffect =
      Reference.rootEvent (mapInput input) := by
  by_cases tracing : input.handoff.tracer.isTracingReceipt = true <;>
    by_cases eip658 : input.handoff.spec.eip658Enabled = true <;>
      simp [tracing, eip658, List.map, Generated.evalSourcePredicate,
        Generated.sourceReceiptPredicate, Reference.rootEvent, mapEffect,
        mapWorldRequest, mapInput]

theorem map_receipt_effect (input : CompletionInput) (substate : Substate)
    (spent : GasConsumed) (selfSend outOfGas : Bool) (failed : Bool) :
    (if Generated.evalSourcePredicate Generated.sourceReceiptPredicate input selfSend outOfGas = true then
      if failed then
        [Generated.sourceOp_receiptFailure
          (Generated.receiptInput input substate spent failed)]
      else
        [Generated.sourceOp_receiptSuccess
          (Generated.receiptInput input substate spent failed)]
    else []).map mapEffect =
      Reference.receiptEvent (mapInput input)
        (Reference.receiptFor (mapInput input) (mapSubstate substate) (mapGas spent) failed) := by
  by_cases tracing : input.handoff.tracer.isTracingReceipt = true
  · cases failed <;>
      simp [tracing, Generated.evalSourcePredicate, Generated.sourceReceiptPredicate,
        Generated.sourceOp_receiptFailure, Generated.sourceOp_receiptSuccess,
         Generated.receiptInput, Reference.receiptEvent, Reference.receiptFor,
         mapEffect, mapTraceRequest, mapException, mapInput, mapSubstate,
         mapGas] <;>
      cases substate.error <;> cases substate.exception <;> simp <;> rfl
   · simp [tracing, Generated.evalSourcePredicate, Generated.sourceReceiptPredicate,
       Reference.receiptEvent, mapInput]

-- The production TransactionResult carries CLR exception/description information that is not
-- represented by this bounded model. Keep this projection explicit: the theorem below proves the
-- status/receipt view only, not equality of the complete production result value.
def mapComplete (complete : SimpleComplete) : Reference.Result :=
  { failed := match complete.result with | TransactionResult.ok => false | TransactionResult.evmException _ => true
    spent := complete.spentGas.spentGas
    preRefundGas := complete.preRefundGas
    headerGasUsed := complete.header.gasUsed
    transactionBlockGas := complete.tx.blockGasUsed
    transactionSpentGas := complete.tx.spentGas
    postCumulativeExecutionGas := complete.blockCumulativeExecutionGas
    postCumulativeStateGas := complete.blockCumulativeStateGas
    receipt := mapReceipt complete.receiptContinuation
    journal := complete.effects.map mapEffect }

-- All scalar/result fields remain exact. Journal sequencing remains positional, while an access
-- trace compares only address/storage membership through Reference.journalExtensional.
def resultExtensional (left right : Reference.Result) : Prop :=
  left.failed = right.failed ∧
    left.spent = right.spent ∧
    left.preRefundGas = right.preRefundGas ∧
    left.headerGasUsed = right.headerGasUsed ∧
    left.transactionBlockGas = right.transactionBlockGas ∧
    left.transactionSpentGas = right.transactionSpentGas ∧
    left.postCumulativeExecutionGas = right.postCumulativeExecutionGas ∧
    left.postCumulativeStateGas = right.postCumulativeStateGas ∧
    left.receipt = right.receipt ∧
    Reference.journalExtensional left.journal right.journal

theorem result_ext {left right : Reference.Result}
    (failed : left.failed = right.failed)
    (spent : left.spent = right.spent)
    (preRefundGas : left.preRefundGas = right.preRefundGas)
    (headerGasUsed : left.headerGasUsed = right.headerGasUsed)
    (transactionBlockGas : left.transactionBlockGas = right.transactionBlockGas)
    (transactionSpentGas : left.transactionSpentGas = right.transactionSpentGas)
    (postCumulativeExecutionGas : left.postCumulativeExecutionGas = right.postCumulativeExecutionGas)
    (postCumulativeStateGas : left.postCumulativeStateGas = right.postCumulativeStateGas)
    (receipt : left.receipt = right.receipt)
    (journal : Reference.journalExtensional left.journal right.journal) :
    resultExtensional left right := by
  exact ⟨failed, spent, preRefundGas, headerGasUsed, transactionBlockGas,
    transactionSpentGas, postCumulativeExecutionGas, postCumulativeStateGas, receipt, journal⟩

theorem settlement_projection_input_bridge (input : CompletionInput) (substate : Substate)
    (preRefund : Nat) (stateUsed : Int) :
    mapGas
        (Generated.sourceOp_refundRequest
          (Generated.settlementInputOf input substate preRefund stateUsed)) =
      Reference.gasView
        (Reference.settlement
          (Reference.settlementInputOf (mapInput input) (mapSubstate substate)
            preRefund stateUsed)) := by
  simpa [mapSettlementInput, Generated.settlementInputOf,
    Reference.settlementInputOf, mapSubstate, mapInput] using
    (settlement_projection_bridge
      (Generated.settlementInputOf input substate preRefund stateUsed))

theorem header_gas_bridge (input : CompletionInput) (selfSend outOfGas : Bool)
    (spent : GasConsumed) (referenceSpent : Reference.GasView)
    (hSpent : mapGas spent = referenceSpent) :
    (Generated.sourceOp_headerAndFees input selfSend outOfGas
      (Generated.effectiveBlockGas spent)
      (if input.handoff.spec.eip8037Enabled = true then
        Generated.combineBlockGas
          (if input.handoff.spec.eip8037Enabled = true then
            input.handoff.blockCumulativeExecutionGas + Generated.effectiveBlockGas spent
          else input.handoff.blockCumulativeExecutionGas)
          (if input.handoff.spec.eip8037Enabled = true then
            input.handoff.blockCumulativeStateGas + spent.blockStateGas
          else input.handoff.blockCumulativeStateGas)
      else Generated.effectiveBlockGas spent)).gasUsed =
      (if (mapInput input).skipValidation = false ∧
          (mapInput input).parallel = false then
        if (mapInput input).eip8037 = true then
          max
            (if (mapInput input).eip8037 = true then
              input.handoff.blockCumulativeExecutionGas +
                Reference.effectiveBlockGas referenceSpent
            else input.handoff.blockCumulativeExecutionGas)
            (if (mapInput input).eip8037 = true then
              input.handoff.blockCumulativeStateGas + referenceSpent.blockStateGas
            else input.handoff.blockCumulativeStateGas)
        else input.handoff.header.gasUsed + Reference.effectiveBlockGas referenceSpent
      else input.handoff.header.gasUsed) := by
  have hSpentGas : spent.spentGas = referenceSpent.spentGas := by
    simpa [mapGas] using congrArg (fun value : Reference.GasView => value.spentGas) hSpent
  have hBlockGas : spent.blockGas = referenceSpent.blockGas := by
    simpa [mapGas] using congrArg (fun value : Reference.GasView => value.blockGas) hSpent
  have hStateBlockGas : spent.blockStateGas = referenceSpent.blockStateGas := by
    simpa [mapGas] using congrArg (fun value : Reference.GasView => value.blockStateGas) hSpent
  have hEffective : Generated.effectiveBlockGas spent =
      Reference.effectiveBlockGas referenceSpent := by
    simp [Generated.effectiveBlockGas, Reference.effectiveBlockGas,
      hSpentGas, hBlockGas, hStateBlockGas]
  have hNormal := counters_guard_bridge input selfSend outOfGas
  rw [Generated.sourceOp_headerAndFees]
  rw [hNormal]
  by_cases normal :
      Reference.optionIsSkipValidation input.handoff.options.raw = false ∧
        input.handoff.parallel = false
  · by_cases eip8037 : input.handoff.spec.eip8037Enabled = true
    <;> simp [normal, eip8037, hEffective, hStateBlockGas, mapInput,
      Generated.combineBlockGas]
  · by_cases eip8037 : input.handoff.spec.eip8037Enabled = true
    <;> simp [normal, eip8037, mapInput]

theorem header_result_bridge (input : CompletionInput) (selfSend outOfGas : Bool)
    (substate : Substate) (preRefund : Nat) (stateUsed : Int) :
    (Generated.sourceOp_headerAndFees input selfSend outOfGas
      (Generated.effectiveBlockGas
        (Generated.sourceOp_refundRequest
          (Generated.settlementInputOf input substate preRefund stateUsed)))
      (if input.handoff.spec.eip8037Enabled = true then
        Generated.combineBlockGas
          (if input.handoff.spec.eip8037Enabled = true then
            input.handoff.blockCumulativeExecutionGas +
              Generated.effectiveBlockGas
                (Generated.sourceOp_refundRequest
                  (Generated.settlementInputOf input substate preRefund stateUsed))
          else input.handoff.blockCumulativeExecutionGas)
          (if input.handoff.spec.eip8037Enabled = true then
            input.handoff.blockCumulativeStateGas +
              (Generated.sourceOp_refundRequest
                (Generated.settlementInputOf input substate preRefund stateUsed)).blockStateGas
          else input.handoff.blockCumulativeStateGas)
      else
        Generated.effectiveBlockGas
          (Generated.sourceOp_refundRequest
            (Generated.settlementInputOf input substate preRefund stateUsed)))).gasUsed =
      (if (mapInput input).skipValidation = false ∧
          (mapInput input).parallel = false then
        if (mapInput input).eip8037 = true then
          max
            (if (mapInput input).eip8037 = true then
              input.handoff.blockCumulativeExecutionGas +
                Reference.effectiveBlockGas
                  (Reference.gasView
                    (Reference.settlement
                      (Reference.settlementInputOf (mapInput input) (mapSubstate substate)
                        preRefund stateUsed)))
            else input.handoff.blockCumulativeExecutionGas)
            (if (mapInput input).eip8037 = true then
              input.handoff.blockCumulativeStateGas +
                (Reference.gasView
                  (Reference.settlement
                    (Reference.settlementInputOf (mapInput input) (mapSubstate substate)
                      preRefund stateUsed))).blockStateGas
            else input.handoff.blockCumulativeStateGas)
        else
          input.handoff.header.gasUsed +
            Reference.effectiveBlockGas
              (Reference.gasView
                (Reference.settlement
                  (Reference.settlementInputOf (mapInput input) (mapSubstate substate)
                    preRefund stateUsed)))
      else input.handoff.header.gasUsed) := by
  apply header_gas_bridge input selfSend outOfGas
    (Generated.sourceOp_refundRequest
      (Generated.settlementInputOf input substate preRefund stateUsed))
    (Reference.gasView
      (Reference.settlement
        (Reference.settlementInputOf (mapInput input) (mapSubstate substate)
          preRefund stateUsed)))
  exact settlement_projection_input_bridge input substate preRefund stateUsed

theorem post_execution_gas_bridge (input : CompletionInput) (selfSend outOfGas : Bool)
    (substate : Substate) (preRefund : Nat) (stateUsed : Int) :
    (if Generated.evalSourcePredicate Generated.sourceCountersPredicate input selfSend outOfGas = true then
      if input.handoff.spec.eip8037Enabled = true then
        input.handoff.blockCumulativeExecutionGas +
          Generated.effectiveBlockGas
            (Generated.sourceOp_refundRequest
              (Generated.settlementInputOf input substate preRefund stateUsed))
      else input.handoff.blockCumulativeExecutionGas
    else input.handoff.blockCumulativeExecutionGas) =
    (if ((!((mapInput input).skipValidation) && !((mapInput input).parallel)) = true) then
      if (mapInput input).eip8037 = true then
        input.handoff.blockCumulativeExecutionGas +
          Reference.effectiveBlockGas
            (Reference.gasView
              (Reference.settlement
                (Reference.settlementInputOf (mapInput input) (mapSubstate substate)
                  preRefund stateUsed)))
      else input.handoff.blockCumulativeExecutionGas
    else input.handoff.blockCumulativeExecutionGas) := by
  rw [counters_guard_bridge input selfSend outOfGas]
  have hEffective := effective_block_bridge input substate preRefund stateUsed
  by_cases normal :
      ((!((mapInput input).skipValidation) && !((mapInput input).parallel)) = true)
  · by_cases eip8037 : input.handoff.spec.eip8037Enabled = true
    · simp [eip8037, mapInput, hEffective]
    · simp [eip8037, mapInput]
  · by_cases eip8037 : input.handoff.spec.eip8037Enabled = true
    · simp [eip8037, mapInput, hEffective]
    · simp [eip8037, mapInput]

theorem post_state_gas_bridge (input : CompletionInput) (selfSend outOfGas : Bool)
    (substate : Substate) (preRefund : Nat) (stateUsed : Int) :
    (if Generated.evalSourcePredicate Generated.sourceCountersPredicate input selfSend outOfGas = true then
      if input.handoff.spec.eip8037Enabled = true then
        input.handoff.blockCumulativeStateGas +
          (Generated.sourceOp_refundRequest
            (Generated.settlementInputOf input substate preRefund stateUsed)).blockStateGas
      else input.handoff.blockCumulativeStateGas
    else input.handoff.blockCumulativeStateGas) =
    (if ((!((mapInput input).skipValidation) && !((mapInput input).parallel)) = true) then
      if (mapInput input).eip8037 = true then
        input.handoff.blockCumulativeStateGas +
          (Reference.gasView
            (Reference.settlement
              (Reference.settlementInputOf (mapInput input) (mapSubstate substate)
                preRefund stateUsed))).blockStateGas
      else input.handoff.blockCumulativeStateGas
    else input.handoff.blockCumulativeStateGas) := by
  rw [counters_guard_bridge input selfSend outOfGas]
  have hState := settlement_state_block_bridge input substate preRefund stateUsed
  by_cases normal :
      ((!((mapInput input).skipValidation) && !((mapInput input).parallel)) = true)
  · by_cases eip8037 : input.handoff.spec.eip8037Enabled = true
    · simp [eip8037, mapInput, hState]
    · simp [eip8037, mapInput]
  · by_cases eip8037 : input.handoff.spec.eip8037Enabled = true
    · simp [eip8037, mapInput, hState]
    · simp [eip8037, mapInput]

-- This is intentionally a normal-return request-boundary premise. In particular, it does not
-- equate a caller-supplied world/tracer result with Generated.run or with Reference.run: those
-- live adapter effects and callback prefixes require a separate production adapter theorem.
def AdapterCoherent (input : CompletionInput) : Prop :=
  let selfSend := sameAddress input.handoff.tx.sender input.handoff.recipient
  let charge := Generated.chargeApplies input selfSend
  let expectedCharge := Generated.stateChargeFromKernel input
  let chargedGas := if charge then expectedCharge.gas else input.handoff.gasAvailable
  let gasAfter := if charge && !expectedCharge.succeeded then Generated.clearExecutionGas chargedGas else chargedGas
  arithmeticCoherent input ∧
    input.handoff.spec.refundQuotient != 0 ∧
    input.gas.newAccountStateCost = Generated.sourceNewAccountStateCost ∧
    (if charge then input.gas.stateCharge = expectedCharge else True) ∧
    (!expectedCharge.succeeded → expectedCharge.gas = input.handoff.gasAvailable) ∧
    input.gas.preRefundGas = Generated.preRefundGas input.handoff.tx.gasLimit gasAfter

theorem normal_return_domain_bridge (input : CompletionInput) (h : normalReturnDomain input) :
    Reference.normalReturnDomain (mapInput input) := by
  simpa [normalReturnDomain, Reference.normalReturnDomain, mapInput] using h

theorem normal_return_domain_excludes_callback_prefix (input : CompletionInput)
    (h : normalReturnDomain input) : input.tracer.normalReturn ≠ false := by
  have hTrue : input.tracer.normalReturn = true := h
  simp [hTrue]

theorem adapter_state_charge_coherence (input : CompletionInput) (h : AdapterCoherent input) :
    (if Generated.chargeApplies input
        (Generated.sameAddress input.handoff.tx.sender input.handoff.recipient) then
      input.gas.stateCharge = Generated.stateChargeFromKernel input
    else True) := by
  have hFields := h
  simp only [AdapterCoherent] at hFields
  exact hFields.2.2.2.1

theorem adapter_pre_refund_coherence (input : CompletionInput) (h : AdapterCoherent input) :
    input.gas.preRefundGas = Generated.preRefundGas input.handoff.tx.gasLimit
      (if Generated.chargeApplies input
          (Generated.sameAddress input.handoff.tx.sender input.handoff.recipient) &&
          !(Generated.stateChargeFromKernel input).succeeded then
        Generated.clearExecutionGas
          (if Generated.chargeApplies input
              (Generated.sameAddress input.handoff.tx.sender input.handoff.recipient) then
            (Generated.stateChargeFromKernel input).gas
          else input.handoff.gasAvailable)
      else if Generated.chargeApplies input
          (Generated.sameAddress input.handoff.tx.sender input.handoff.recipient) then
        (Generated.stateChargeFromKernel input).gas
      else input.handoff.gasAvailable) := by
  have hFields := h
  simp only [AdapterCoherent] at hFields
  exact hFields.2.2.2.2.1

theorem charged_stage_bridge (input : CompletionInput) (h : AdapterCoherent input) :
    mapStateCharge
        (if Generated.chargeApplies input
              (Generated.sameAddress input.handoff.tx.sender input.handoff.recipient) = true then
          Generated.sourceOp_consumeStateGas input
        else { succeeded := true, gas := input.handoff.gasAvailable }) =
      if Reference.chargeGuard (mapInput input)
          ((mapInput input).sender == (mapInput input).recipient) = true then
        Reference.chargedState (mapInput input)
      else { succeeded := true, gas := Reference.gasState (mapInput input) } := by
  by_cases charge : Generated.chargeApplies input
      (Generated.sameAddress input.handoff.tx.sender input.handoff.recipient) = true
  · have hCharge := adapter_state_charge_coherence input h
    have hGenerated : Generated.sourceOp_consumeStateGas input = input.gas.stateCharge := by
      have hEq : input.gas.stateCharge = Generated.stateChargeFromKernel input := by
        simpa [charge] using hCharge
      simpa [Generated.sourceOp_consumeStateGas, Generated.stateChargeFromKernel,
        Generated.stateChargeFromKernelWithCost, Generated.sourceOp_newAccountStateCost] using hEq.symm
    have hReference : Reference.chargeGuard (mapInput input)
        ((mapInput input).sender == (mapInput input).recipient) = true := by
      have hGuard : Reference.chargeGuard (mapInput input)
          (Generated.sameAddress input.handoff.tx.sender input.handoff.recipient) = true := by
        rw [← charge_guard_bridge input]
        exact charge
      simpa [mapInput, Generated.sameAddress] using hGuard
    simp [charge, hGenerated, hReference, charged_state_bridge]
  · have hReference : ¬ Reference.chargeGuard (mapInput input)
        ((mapInput input).sender == (mapInput input).recipient) = true := by
      intro hGuard
      apply charge
      rw [charge_guard_bridge input]
      simpa [mapInput, Generated.sameAddress] using hGuard
    simpa [charge, hReference, mapStateCharge] using
      (gas_state_shape input.handoff.gasAvailable).trans (gas_state_bridge input).symm

theorem out_of_gas_stage_bridge (input : CompletionInput) (h : AdapterCoherent input) :
    (!(if Generated.chargeApplies input
            (Generated.sameAddress input.handoff.tx.sender input.handoff.recipient) = true then
          Generated.sourceOp_consumeStateGas input
        else { succeeded := true, gas := input.handoff.gasAvailable }).succeeded) =
      !(if Reference.chargeGuard (mapInput input)
            ((mapInput input).sender == (mapInput input).recipient) = true then
          Reference.chargedState (mapInput input)
        else { succeeded := true, gas := Reference.gasState (mapInput input) }).succeeded := by
  have hCharge := charged_stage_bridge input h
  have hSucceeded := congrArg (fun value : Reference.StateCharge => !value.succeeded) hCharge
  simpa [mapStateCharge] using hSucceeded

theorem gas_after_stage_bridge (input : CompletionInput) (h : AdapterCoherent input) :
    mapGasState
        (if (!(if Generated.chargeApplies input
                (Generated.sameAddress input.handoff.tx.sender input.handoff.recipient) = true then
              Generated.sourceOp_consumeStateGas input
            else { succeeded := true, gas := input.handoff.gasAvailable }).succeeded) = true then
          Generated.clearExecutionGas
            (if Generated.chargeApplies input
                (Generated.sameAddress input.handoff.tx.sender input.handoff.recipient) = true then
              Generated.sourceOp_consumeStateGas input
            else { succeeded := true, gas := input.handoff.gasAvailable }).gas
        else
          (if Generated.chargeApplies input
              (Generated.sameAddress input.handoff.tx.sender input.handoff.recipient) = true then
            Generated.sourceOp_consumeStateGas input
          else { succeeded := true, gas := input.handoff.gasAvailable }).gas) =
      if (!(if Reference.chargeGuard (mapInput input)
                ((mapInput input).sender == (mapInput input).recipient) = true then
              Reference.chargedState (mapInput input)
            else { succeeded := true, gas := Reference.gasState (mapInput input) }).succeeded) = true then
        Reference.clearExecution
          (if Reference.chargeGuard (mapInput input)
              ((mapInput input).sender == (mapInput input).recipient) = true then
            Reference.chargedState (mapInput input)
          else { succeeded := true, gas := Reference.gasState (mapInput input) }).gas
      else
        (if Reference.chargeGuard (mapInput input)
            ((mapInput input).sender == (mapInput input).recipient) = true then
          Reference.chargedState (mapInput input)
        else { succeeded := true, gas := Reference.gasState (mapInput input) }).gas := by
  by_cases charge : Generated.chargeApplies input
      (Generated.sameAddress input.handoff.tx.sender input.handoff.recipient) = true
  · have hCharge := adapter_state_charge_coherence input h
    have hEq : input.gas.stateCharge = Generated.stateChargeFromKernel input := by
      simpa [charge] using hCharge
    have hGenerated : Generated.sourceOp_consumeStateGas input = input.gas.stateCharge := by
      simpa [Generated.sourceOp_consumeStateGas, Generated.stateChargeFromKernel,
        Generated.stateChargeFromKernelWithCost, Generated.sourceOp_newAccountStateCost] using hEq.symm
    have hReference : Reference.chargeGuard (mapInput input)
        ((mapInput input).sender == (mapInput input).recipient) = true := by
      have hGuard : Reference.chargeGuard (mapInput input)
          (Generated.sameAddress input.handoff.tx.sender input.handoff.recipient) = true := by
        rw [← charge_guard_bridge input]
        exact charge
      simpa [mapInput, Generated.sameAddress] using hGuard
    cases chargeValue : input.gas.stateCharge.succeeded <;>
      simp [charge, hGenerated, hReference, charged_state_bridge,
        Generated.clearExecutionGas, Reference.clearExecution, mapStateCharge, mapGasState,
        chargeValue]
  · have hReference : ¬ Reference.chargeGuard (mapInput input)
        ((mapInput input).sender == (mapInput input).recipient) = true := by
      intro hGuard
      apply charge
      rw [charge_guard_bridge input]
      simpa [mapInput, Generated.sameAddress] using hGuard
    simpa [charge, hReference, Generated.clearExecutionGas, Reference.clearExecution,
      mapStateCharge] using (gas_state_bridge input).symm

theorem source_identity_bridge : sourceIdentityCoherent := by
  simp [sourceIdentityCoherent, handoffIdentity, settlementIdentity, stateChargeIdentity, receiptBoundary]

theorem append_write_value (write : Bool) (value : Nat)
    (subtract add : Reference.JournalEvent) :
    (if write && value != 0 then [subtract] else []) ++
        (if write then [add] else []) =
      if write then (if value = 0 then [] else [subtract]) ++ [add] else [] := by
  cases write <;> by_cases valueZero : value = 0 <;> simp [valueZero]

theorem append_log_trace (condition tracing : Bool)
    (log trace : Reference.JournalEvent) :
    (if condition then [log] else []) ++
        (if condition && tracing then [trace] else []) =
      if condition then [log] ++ (if tracing then [trace] else []) else [] := by
  cases condition <;> cases tracing <;> rfl

theorem append_action_start (actions code : Bool)
    (start codeEvent : Reference.JournalEvent) :
    (if actions then [start] ++ (if code then [codeEvent] else []) else []) =
      (if actions then [start] else []) ++
        (if actions && code then [codeEvent] else []) := by
  cases actions <;> cases code <;> rfl

theorem append_write_value_tail (write : Bool) (value : Nat)
    (subtract add : Reference.JournalEvent) (tail : List Reference.JournalEvent) :
    (if write && value != 0 then [subtract] else []) ++
        (if write then [add] else []) ++ tail =
      (if write then (if value = 0 then [] else [subtract]) ++ [add] else []) ++ tail := by
  cases write <;> by_cases valueZero : value = 0 <;> simp [valueZero]

theorem append_write_value_mixed_tail (write : Bool) (value : Nat)
    (subtract add : Reference.JournalEvent) (tail : List Reference.JournalEvent) :
    (if write && value != 0 then [subtract] else []) ++
        (if write then [add] else []) ++ tail =
      (if write = true then (if value = 0 then [] else [subtract]) ++ [add] else []) ++ tail := by
  cases write <;> by_cases valueZero : value = 0 <;> simp [valueZero]

theorem append_log_trace_tail (condition tracing : Bool)
    (log trace : Reference.JournalEvent) (tail : List Reference.JournalEvent) :
    (if condition then [log] else []) ++
        (if condition && tracing then [trace] else []) ++ tail =
      (if condition then [log] ++ (if tracing then [trace] else []) else []) ++ tail := by
  cases condition <;> cases tracing <;> rfl

theorem append_action_start_tail (actions code : Bool)
    (start codeEvent : Reference.JournalEvent) (tail : List Reference.JournalEvent) :
    (if actions then [start] ++ (if code then [codeEvent] else []) else []) ++ tail =
      (if actions then [start] else []) ++
        (if actions && code then [codeEvent] else []) ++ tail := by
  cases actions <;> cases code <;> rfl

theorem append_write_value_prefix (pre : List Reference.JournalEvent) (write : Bool)
    (value : Nat) (subtract add : Reference.JournalEvent)
    (tail : List Reference.JournalEvent) :
    ((pre ++ (if write && value != 0 then [subtract] else [])) ++
        (if write then [add] else [])) ++ tail =
      (pre ++ (if write then (if value = 0 then [] else [subtract]) ++ [add] else [])) ++ tail := by
  simpa [List.append_assoc] using
    congrArg (fun events => pre ++ events)
      (append_write_value_tail write value subtract add tail)

theorem append_action_start_prefix (pre : List Reference.JournalEvent) (actions code : Bool)
    (start codeEvent : Reference.JournalEvent) (tail : List Reference.JournalEvent) :
    ((pre ++ (if actions then [start] ++ (if code then [codeEvent] else []) else [])) ++ tail) =
      (pre ++ (if actions then [start] else [])) ++
        (if actions && code then [codeEvent] else []) ++ tail := by
  simpa [List.append_assoc] using
    congrArg (fun events => pre ++ events)
      (append_action_start_tail actions code start codeEvent tail)

theorem append_log_trace_prefix (pre : List Reference.JournalEvent) (condition tracing : Bool)
    (log trace : Reference.JournalEvent) (tail : List Reference.JournalEvent) :
    ((pre ++ (if condition then [log] else [])) ++
        (if condition && tracing then [trace] else [])) ++ tail =
      (pre ++ (if condition then [log] ++ (if tracing then [trace] else []) else [])) ++ tail := by
  simpa [List.append_assoc] using
    congrArg (fun events => pre ++ events)
      (append_log_trace_tail condition tracing log trace tail)

theorem journal_layout_eq
    (charge write logs actions code oog warmup receipt tracingLogs : Bool)
    (value : Nat)
    (gasCharge subtract add actionStart actionCode clear logEvent logTrace substate
      terminalError terminalEnd settlement : Reference.JournalEvent)
    (refundEvents accessEvents headerEvents feeEvents : List Reference.JournalEvent)
    (transaction : Reference.JournalEvent)
    (finalWorld rootEvents : List Reference.JournalEvent)
    (receiptFailure receiptSuccess : Reference.JournalEvent) :
    [Reference.JournalEvent.metric] ++
        (if charge then [gasCharge] else []) ++
        (if write && value != 0 then [subtract] else []) ++
        (if write then [add] else []) ++
        (if actions then [actionStart] ++ (if code then [actionCode] else []) else []) ++
        (if oog then [clear] else []) ++
        (if logs then [logEvent] else []) ++
        (if logs && tracingLogs then [logTrace] else []) ++
        [substate] ++
        (if actions then if oog then [terminalError] else [terminalEnd] else []) ++
        [settlement] ++ refundEvents ++ accessEvents ++ headerEvents ++ feeEvents ++
        (if warmup then [] else [transaction]) ++ finalWorld ++ rootEvents ++
        (if receipt then if oog then [receiptFailure] else [receiptSuccess] else []) =
    [Reference.JournalEvent.metric] ++
        (if charge then [gasCharge] else []) ++
        (if write then (if value = 0 then [] else [subtract]) ++ [add] else []) ++
        (if actions then [actionStart] else []) ++
        (if actions && code then [actionCode] else []) ++
        (if oog then [clear] else []) ++
        (if logs then [logEvent] ++ (if tracingLogs then [logTrace] else []) else []) ++
        [substate] ++
        (if actions then if oog then [terminalError] else [terminalEnd] else []) ++
        [settlement] ++ refundEvents ++ accessEvents ++ headerEvents ++ feeEvents ++
        (if warmup then [] else [transaction]) ++ finalWorld ++ rootEvents ++
      (if receipt then if oog then [receiptFailure] else [receiptSuccess] else []) := by
  rw [append_write_value_prefix]
  rw [append_action_start_prefix]
  rw [append_log_trace_prefix]

theorem journal_layout
    (charge write logs actions code oog warmup receipt tracingLogs : Bool)
    (value : Nat)
    (gasCharge subtract add actionStart actionCode clear logEvent logTrace substate
      terminalError terminalEnd settlement : Reference.JournalEvent)
    (refundEvents accessEvents headerEvents feeEvents : List Reference.JournalEvent)
    (transaction : Reference.JournalEvent)
    (finalWorld rootEvents : List Reference.JournalEvent)
    (receiptFailure receiptSuccess : Reference.JournalEvent) :
    Reference.journalExtensional
      ([Reference.JournalEvent.metric] ++
        (if charge then [gasCharge] else []) ++
        (if write && value != 0 then [subtract] else []) ++
        (if write then [add] else []) ++
        (if actions then [actionStart] ++ (if code then [actionCode] else []) else []) ++
        (if oog then [clear] else []) ++
        (if logs then [logEvent] else []) ++
        (if logs && tracingLogs then [logTrace] else []) ++
        [substate] ++
        (if actions then if oog then [terminalError] else [terminalEnd] else []) ++
        [settlement] ++ refundEvents ++ accessEvents ++ headerEvents ++ feeEvents ++
        (if warmup then [] else [transaction]) ++ finalWorld ++ rootEvents ++
        (if receipt then if oog then [receiptFailure] else [receiptSuccess] else []))
      ([Reference.JournalEvent.metric] ++
        (if charge then [gasCharge] else []) ++
        (if write then (if value = 0 then [] else [subtract]) ++ [add] else []) ++
        (if actions then [actionStart] else []) ++
        (if actions && code then [actionCode] else []) ++
        (if oog then [clear] else []) ++
        (if logs then [logEvent] ++ (if tracingLogs then [logTrace] else []) else []) ++
        [substate] ++
        (if actions then if oog then [terminalError] else [terminalEnd] else []) ++
        [settlement] ++ refundEvents ++ accessEvents ++ headerEvents ++ feeEvents ++
        (if warmup then [] else [transaction]) ++ finalWorld ++ rootEvents ++
        (if receipt then if oog then [receiptFailure] else [receiptSuccess] else [])) := by
  apply Reference.journalExtensional_of_eq
  exact journal_layout_eq charge write logs actions code oog warmup receipt tracingLogs value
    gasCharge subtract add actionStart actionCode clear logEvent logTrace substate terminalError
    terminalEnd settlement refundEvents accessEvents headerEvents feeEvents transaction finalWorld
    rootEvents receiptFailure receiptSuccess

theorem append_write_value_prop (write : Prop) [Decidable write] (value : Nat)
    (subtract add : Reference.JournalEvent) :
    (if write ∧ value ≠ 0 then [subtract] else []) ++
        (if write then [add] else []) =
      if write then (if value = 0 then [] else [subtract]) ++ [add] else [] := by
  by_cases writeValue : write <;> by_cases valueZero : value = 0 <;>
    simp [writeValue, valueZero]

theorem append_pay_recipient_final (self : Prop) [Decidable self] (value : Nat)
    (subtract add : Reference.JournalEvent) :
    (if ¬self ∧ value ≠ 0 then [subtract] else []) ++
        (if self then [] else [add]) =
      if self then [] else (if value = 0 then [] else [subtract]) ++ [add] := by
  by_cases selfValue : self <;> by_cases valueZero : value = 0 <;>
    simp [selfValue, valueZero]

theorem append_log_trace_prop (condition tracing : Prop)
    [Decidable condition] [Decidable tracing]
    (log trace : Reference.JournalEvent) :
    (if condition then [log] else []) ++
        (if condition ∧ tracing then [trace] else []) =
      if condition then [log] ++ (if tracing then [trace] else []) else [] := by
  by_cases conditionValue : condition <;> by_cases tracingValue : tracing <;>
    simp [conditionValue, tracingValue]

theorem append_action_start_prop (actions code : Prop)
    [Decidable actions] [Decidable code]
    (start codeEvent : Reference.JournalEvent) :
    (if actions then [start] ++ (if code then [codeEvent] else []) else []) =
      (if actions then [start] else []) ++
        (if actions ∧ code then [codeEvent] else []) := by
  by_cases actionsValue : actions <;> by_cases codeValue : code <;>
    simp [actionsValue, codeValue]

theorem append_write_value_prop_tail (write : Prop) [Decidable write] (value : Nat)
    (subtract add : Reference.JournalEvent) (tail : List Reference.JournalEvent) :
    (if write ∧ value ≠ 0 then [subtract] else []) ++
        (if write then [add] else []) ++ tail =
      (if write then (if value = 0 then [] else [subtract]) ++ [add] else []) ++ tail := by
  by_cases writeValue : write <;> by_cases valueZero : value = 0 <;>
    simp [writeValue, valueZero]

theorem append_log_trace_prop_tail (condition tracing : Prop)
    [Decidable condition] [Decidable tracing]
    (log trace : Reference.JournalEvent) (tail : List Reference.JournalEvent) :
    (if condition then [log] else []) ++
        (if condition ∧ tracing then [trace] else []) ++ tail =
      (if condition then [log] ++ (if tracing then [trace] else []) else []) ++ tail := by
  by_cases conditionValue : condition <;> by_cases tracingValue : tracing <;>
    simp [conditionValue, tracingValue]

theorem append_action_start_prop_tail (actions code : Prop)
    [Decidable actions] [Decidable code]
    (start codeEvent : Reference.JournalEvent) (tail : List Reference.JournalEvent) :
    (if actions then [start] ++ (if code then [codeEvent] else []) else []) ++ tail =
      (if actions then [start] else []) ++
        (if actions ∧ code then [codeEvent] else []) ++ tail := by
  by_cases actionsValue : actions <;> by_cases codeValue : code <;>
    simp [actionsValue, codeValue]

theorem append_write_value_prop_prefix (pre : List Reference.JournalEvent) (write : Prop)
    [Decidable write] (value : Nat) (subtract add : Reference.JournalEvent)
    (tail : List Reference.JournalEvent) :
    ((pre ++ (if write ∧ value ≠ 0 then [subtract] else [])) ++
        (if write then [add] else [])) ++ tail =
      (pre ++ (if write then (if value = 0 then [] else [subtract]) ++ [add] else [])) ++ tail := by
  simpa [List.append_assoc] using
    congrArg (fun events => pre ++ events)
      (append_write_value_prop_tail write value subtract add tail)

theorem append_action_start_prop_prefix (pre : List Reference.JournalEvent)
    (actions code : Prop) [Decidable actions] [Decidable code]
    (start codeEvent : Reference.JournalEvent) (tail : List Reference.JournalEvent) :
    ((pre ++ (if actions then [start] ++ (if code then [codeEvent] else []) else [])) ++ tail) =
      (pre ++ (if actions then [start] else [])) ++
        (if actions ∧ code then [codeEvent] else []) ++ tail := by
  simpa [List.append_assoc] using
    congrArg (fun events => pre ++ events)
      (append_action_start_prop_tail actions code start codeEvent tail)

theorem append_log_trace_prop_prefix (pre : List Reference.JournalEvent)
    (condition tracing : Prop) [Decidable condition] [Decidable tracing]
    (log trace : Reference.JournalEvent) (tail : List Reference.JournalEvent) :
    ((pre ++ (if condition then [log] else [])) ++
        (if condition ∧ tracing then [trace] else [])) ++ tail =
      (pre ++ (if condition then [log] ++ (if tracing then [trace] else []) else [])) ++ tail := by
  simpa [List.append_assoc] using
    congrArg (fun events => pre ++ events)
      (append_log_trace_prop_tail condition tracing log trace tail)

theorem if_bool_eq_true {α : Sort u} (condition : Bool) (thenValue elseValue : α) :
    (if condition = true then thenValue else elseValue) =
      (if condition then thenValue else elseValue) := by
  cases condition <;> rfl

theorem if_bool_as_prop {α : Sort u} (condition : Bool) (thenValue elseValue : α) :
    (if condition then thenValue else elseValue) =
      (if condition = true then thenValue else elseValue) := by
  cases condition <;> rfl

theorem journal_layout_prop
    (charge write logs actions code oog warmup receipt tracingLogs : Prop)
    [Decidable charge] [Decidable write] [Decidable logs] [Decidable actions]
    [Decidable code] [Decidable oog] [Decidable warmup] [Decidable receipt]
    [Decidable tracingLogs]
    (value : Nat)
    (gasCharge subtract add actionStart actionCode clear logEvent logTrace substate
      terminalError terminalEnd settlement : Reference.JournalEvent)
    (refundEvents accessEvents headerEvents feeEvents : List Reference.JournalEvent)
    (transaction : Reference.JournalEvent)
    (finalWorld rootEvents : List Reference.JournalEvent)
    (receiptFailure receiptSuccess : Reference.JournalEvent) :
    [Reference.JournalEvent.metric] ++
        (if charge then [gasCharge] else []) ++
        (if write ∧ value ≠ 0 then [subtract] else []) ++
        (if write then [add] else []) ++
        (if actions then [actionStart] ++ (if code then [actionCode] else []) else []) ++
        (if oog then [clear] else []) ++
        (if logs then [logEvent] else []) ++
        (if logs ∧ tracingLogs then [logTrace] else []) ++
        [substate] ++
        (if actions then if oog then [terminalError] else [terminalEnd] else []) ++
        [settlement] ++ refundEvents ++ accessEvents ++ headerEvents ++ feeEvents ++
        (if warmup then [] else [transaction]) ++ finalWorld ++ rootEvents ++
        (if receipt then if oog then [receiptFailure] else [receiptSuccess] else []) =
    [Reference.JournalEvent.metric] ++
        (if charge then [gasCharge] else []) ++
        (if write then (if value = 0 then [] else [subtract]) ++ [add] else []) ++
        (if actions then [actionStart] else []) ++
        (if actions ∧ code then [actionCode] else []) ++
        (if oog then [clear] else []) ++
        (if logs then [logEvent] ++ (if tracingLogs then [logTrace] else []) else []) ++
        [substate] ++
        (if actions then if oog then [terminalError] else [terminalEnd] else []) ++
        [settlement] ++ refundEvents ++ accessEvents ++ headerEvents ++ feeEvents ++
        (if warmup then [] else [transaction]) ++ finalWorld ++ rootEvents ++
         (if receipt then if oog then [receiptFailure] else [receiptSuccess] else []) := by
  rw [append_write_value_prop_prefix]
  rw [append_action_start_prop_prefix]
  rw [append_log_trace_prop_prefix]

set_option linter.unusedSimpArgs false in
set_option linter.unnecessarySimpa false in
set_option maxRecDepth 100000 in
theorem universal_refinement (input : CompletionInput) (h : AdapterCoherent input)
    (hNormalReturn : normalReturnDomain input) :
    resultExtensional (mapComplete (Generated.run input)) (Reference.run (mapInput input)) := by
  -- The machines have separate vocabularies and separate phase definitions. The adapter premise
  -- supplies the exact source-policy state charge; transfer topics are derived by the generated
  -- bounded address projection and access observations are computed from source-bound inputs and
  -- compared extensionally. Payload-bearing
  -- effect constructors are compared one-for-one, including addresses, amounts, gas, logs, and
  -- receipt callback inputs. No generated/reference equality is imported from Reference.
  have _referenceNormalReturn := normal_return_domain_bridge input hNormalReturn
  by_cases charge : Generated.chargeApplies input (Generated.sameAddress input.handoff.tx.sender input.handoff.recipient)
  · have hCharge := adapter_state_charge_coherence input h
    have hChargeEq : input.gas.stateCharge = Generated.stateChargeFromKernel input := by
      simpa [charge] using hCharge
    have hKernelEq : Generated.stateChargeFromKernel input = input.gas.stateCharge := hChargeEq.symm
    have hComputed : Generated.sourceOp_consumeStateGas input = input.gas.stateCharge := by
      simpa [Generated.sourceOp_consumeStateGas, Generated.stateChargeFromKernel,
        Generated.stateChargeFromKernelWithCost, Generated.sourceOp_newAccountStateCost] using hChargeEq.symm
    have hChargePredicate :
        Generated.evalSourcePredicate Generated.sourceNewAccountPredicate input
            (Generated.sameAddress input.handoff.tx.sender input.handoff.recipient) false = true := by
      simpa [Generated.chargeApplies] using charge
    have hReferenceCharge :
        Reference.chargeGuard (mapInput input)
            (Generated.sameAddress input.handoff.tx.sender input.handoff.recipient) = true := by
      rw [← charge_guard_bridge input]
      exact charge
    have hReferenceChargeExpanded := hReferenceCharge
    simp only [mapInput] at hReferenceChargeExpanded
    have hReferenceChargeMapSelf :
        Reference.chargeGuard (mapInput input)
            ((mapInput input).sender == (mapInput input).recipient) = true := by
      simpa [mapInput, Generated.sameAddress] using hReferenceCharge
    have hReferenceChargeSelf := hReferenceCharge
    simp only [mapInput, Generated.sameAddress] at hReferenceChargeSelf
    by_cases chargeSucceeded : (Generated.stateChargeFromKernel input).succeeded = true
    · have hInputChargeSucceeded : input.gas.stateCharge.succeeded = true := by
        rw [hChargeEq]
        exact chargeSucceeded
      have hMapChargeSucceeded : (mapInput input).chargedSucceeded = true := by
        simpa [mapInput] using hInputChargeSucceeded
      have hMapStateChargeSucceeded : (mapStateCharge input.gas.stateCharge).succeeded = true := by
        exact hInputChargeSucceeded
      have hCharged :
          (if chargeApplies input (sameAddress input.handoff.tx.sender input.handoff.recipient) = true then
            sourceOp_consumeStateGas input
          else { succeeded := true, gas := input.handoff.gasAvailable }) = input.gas.stateCharge := by
        simp [charge, hComputed]
      have hReferenceCharged :
          (if Reference.chargeGuard (mapInput input)
              ((mapInput input).sender == (mapInput input).recipient) = true then
            Reference.chargedState (mapInput input)
          else { succeeded := true, gas := Reference.gasState (mapInput input) }) =
            mapStateCharge input.gas.stateCharge := by
        simp [hReferenceChargeMapSelf, charged_state_bridge]
      have hOog :
          (!(if chargeApplies input (sameAddress input.handoff.tx.sender input.handoff.recipient) = true then
            sourceOp_consumeStateGas input
          else { succeeded := true, gas := input.handoff.gasAvailable }).succeeded) = false := by
        simp [hCharged, hInputChargeSucceeded]
      have hPreRefund := adapter_pre_refund_coherence input h
      have hPreRefundGenerated :
          Generated.preRefundGas input.handoff.tx.gasLimit
              (if (!(if chargeApplies input
                    (sameAddress input.handoff.tx.sender input.handoff.recipient) = true then
                  sourceOp_consumeStateGas input
                else { succeeded := true, gas := input.handoff.gasAvailable }).succeeded) = true then
                clearExecutionGas
                  (if chargeApplies input
                      (sameAddress input.handoff.tx.sender input.handoff.recipient) = true then
                    sourceOp_consumeStateGas input
                  else { succeeded := true, gas := input.handoff.gasAvailable }).gas
              else
                (if chargeApplies input
                    (sameAddress input.handoff.tx.sender input.handoff.recipient) = true then
                  sourceOp_consumeStateGas input
                else { succeeded := true, gas := input.handoff.gasAvailable }).gas) =
            input.gas.preRefundGas := by
        simpa [charge, hCharged, hInputChargeSucceeded, chargeSucceeded, hChargeEq, hComputed] using hPreRefund.symm
      have hPreRefundState :
          Generated.preRefundGas input.handoff.tx.gasLimit input.gas.stateCharge.gas =
            input.gas.preRefundGas := by
        simpa [charge, chargeSucceeded, hChargeEq] using hPreRefund.symm
      have hPreRefundReference :
          Reference.preRefundFor (mapInput input)
              (mapStateCharge input.gas.stateCharge).gas.execution
              (mapStateCharge input.gas.stateCharge).gas.reservoir =
            input.gas.preRefundGas := by
        calc
          Reference.preRefundFor (mapInput input)
              (mapStateCharge input.gas.stateCharge).gas.execution
              (mapStateCharge input.gas.stateCharge).gas.reservoir =
              Generated.preRefundGas input.handoff.tx.gasLimit input.gas.stateCharge.gas := by
                simpa [mapStateCharge] using
                  (pre_refund_bridge input input.gas.stateCharge.gas).symm
          _ = input.gas.preRefundGas := hPreRefundState
      have hStateUsed :
          (if input.handoff.spec.eip8037Enabled then
              stateGasUsed
                (if chargeApplies input
                    (sameAddress input.handoff.tx.sender input.handoff.recipient) = true then
                  sourceOp_consumeStateGas input
                else { succeeded := true, gas := input.handoff.gasAvailable }).gas
            else 0) =
            (if (mapInput input).eip8037 then
                (if Reference.chargeGuard (mapInput input)
                    ((mapInput input).sender == (mapInput input).recipient) = true then
                  (Reference.chargedState (mapInput input)).gas.stateUsed
                else
                  (Reference.gasState (mapInput input)).stateUsed)
      else 0) := by
        have hChargedStateExpanded := charged_state_bridge input
        simp only [mapInput] at hChargedStateExpanded
        simp [hInputChargeSucceeded] at hChargedStateExpanded
        rw [hComputed, hReferenceChargeMapSelf]
        simp [charge, hChargedStateExpanded, hInputChargeSucceeded,
          chargeSucceeded, mapStateCharge, mapInput, stateGasUsed]
      have hSubstate := substate_bridge input
        (sameAddress input.handoff.tx.sender input.handoff.recipient) false
      have hSubstateSelf :
          mapSubstate
              (sourceOp_substateConstruction
                (if logApplies input
                    (sameAddress input.handoff.tx.sender input.handoff.recipient) false = true then
                  [sourceOp_transferLogRequest input] else []) false) =
            { output := []
              refund := 0
              logs :=
                if Reference.logsGuard (mapInput input)
                    (sameAddress input.handoff.tx.sender input.handoff.recipient) false = true then
                  [Reference.expectedTransferLog (mapInput input)] else []
              shouldRevert := false
              isError := false
              error := none
              exception := if false then Reference.EvmException.outOfGas else Reference.EvmException.none } := by
        exact hSubstate
      have hSubstateMap :
          mapSubstate
              (sourceOp_substateConstruction
                (if logApplies input
                    (sameAddress input.handoff.tx.sender input.handoff.recipient) false = true then
                  [sourceOp_transferLogRequest input] else []) false) =
            { output := []
              refund := 0
              logs :=
                if Reference.logsGuard (mapInput input)
                    ((mapInput input).sender == (mapInput input).recipient) false = true then
                  [Reference.expectedTransferLog (mapInput input)] else []
              shouldRevert := false
              isError := false
              error := none
              exception := Reference.EvmException.none } := by
        change mapSubstate
              (sourceOp_substateConstruction
                (if logApplies input
                    (sameAddress input.handoff.tx.sender input.handoff.recipient) false = true then
                  [sourceOp_transferLogRequest input] else []) false) =
            { output := []
              refund := 0
              logs :=
                if Reference.logsGuard (mapInput input)
                    (sameAddress input.handoff.tx.sender input.handoff.recipient) false = true then
                  [Reference.expectedTransferLog (mapInput input)] else []
              shouldRevert := false
              isError := false
              error := none
              exception := if false then Reference.EvmException.outOfGas else Reference.EvmException.none }
        exact hSubstateSelf
      have hPreRefundReferenceSimple :
          Reference.preRefundFor (mapInput input)
              input.gas.stateCharge.gas.execution input.gas.stateCharge.gas.stateReservoir =
            input.gas.preRefundGas := by
        simpa [mapStateCharge] using hPreRefundReference
      have hStateUsedSimple :
          (if input.handoff.spec.eip8037Enabled = true then
              stateGasUsed input.gas.stateCharge.gas else 0) =
            (if (mapInput input).eip8037 = true then
              input.gas.stateCharge.gas.stateGasUsed else 0) := by
        rfl
      have hWarmupBridge :
          Generated.isWarmup input.handoff.options =
            Reference.optionIsWarmup input.handoff.options.raw := by
        rfl
      have hEffectiveSimple :
          Generated.effectiveBlockGas
              (sourceOp_refundRequest
                (settlementInputOf input
                  (sourceOp_substateConstruction
                    (if logApplies input
                        (sameAddress input.handoff.tx.sender input.handoff.recipient) false = true then
                      [sourceOp_transferLogRequest input] else []) false)
                  input.gas.preRefundGas
                  (if input.handoff.spec.eip8037Enabled = true then
                    stateGasUsed input.gas.stateCharge.gas else 0))) =
            Reference.effectiveBlockGas
              (Reference.gasView
                (Reference.settlement
                  (Reference.settlementInputOf (mapInput input)
                    (mapSubstate
                      (sourceOp_substateConstruction
                        (if logApplies input
                            (sameAddress input.handoff.tx.sender input.handoff.recipient) false = true then
                          [sourceOp_transferLogRequest input] else []) false))
                    input.gas.preRefundGas
                    (if input.handoff.spec.eip8037Enabled = true then
                      stateGasUsed input.gas.stateCharge.gas else 0)))) := by
        simpa [hStateUsedSimple] using
          (effective_block_bridge input
            (sourceOp_substateConstruction
              (if logApplies input
                  (sameAddress input.handoff.tx.sender input.handoff.recipient) false = true then
                [sourceOp_transferLogRequest input] else []) false)
            input.gas.preRefundGas
            (if input.handoff.spec.eip8037Enabled = true then
              stateGasUsed input.gas.stateCharge.gas else 0))
      apply result_ext
      · simp only [mapComplete, Generated.run, Reference.run]
        rw [hCharged]
        rw [hReferenceCharged]
        simp [hInputChargeSucceeded, mapStateCharge]
      · simp only [mapComplete, Generated.run, Reference.run]
        simp only [hCharged, hReferenceCharged, hOog, hPreRefundGenerated,
          hPreRefundReference, hStateUsed, hSubstate]
        simp [hInputChargeSucceeded, mapStateCharge, Bool.not_true]
        try simp only [sourceOp_clearExecutionGas]
        try rw [hPreRefundState]
        try rw [hPreRefundReferenceSimple]
        try rw [← hSubstateSelf]
        try rw [hStateUsedSimple]
        try rw [← hSubstateSelf]
        exact settlement_spent_bridge input
          (sourceOp_substateConstruction
            (if logApplies input
                (sameAddress input.handoff.tx.sender input.handoff.recipient) false = true then
              [sourceOp_transferLogRequest input] else []) false)
          input.gas.preRefundGas
          (if input.handoff.spec.eip8037Enabled = true then
            stateGasUsed (Generated.clearExecutionGas input.gas.stateCharge.gas) else 0)
      · simp only [mapComplete, Generated.run, Reference.run]
        simp only [hCharged, hReferenceCharged, hOog, hPreRefundGenerated,
          hPreRefundReference]
        simp only [hInputChargeSucceeded, mapStateCharge]
        exact hPreRefundState.trans hPreRefundReferenceSimple.symm
      · simp only [mapComplete, Generated.run, Reference.run]
        simp only [hCharged, hReferenceCharged, hOog, hPreRefundGenerated,
          hPreRefundReference]
        simp [hInputChargeSucceeded, mapStateCharge]
        try simp only [sourceOp_clearExecutionGas]
        try rw [hPreRefundState]
        try rw [hPreRefundReferenceSimple]
        try rw [← hSubstateSelf]
        refine Eq.trans ?_ (header_result_bridge input
          (sameAddress input.handoff.tx.sender input.handoff.recipient) false
          (sourceOp_substateConstruction
            (if logApplies input
                (sameAddress input.handoff.tx.sender input.handoff.recipient) false = true then
              [sourceOp_transferLogRequest input] else []) false)
          input.gas.preRefundGas
          (if input.handoff.spec.eip8037Enabled = true then
            stateGasUsed input.gas.stateCharge.gas else 0))
        simp [Generated.sourceOp_substateConstruction, Reference.settlementInputOf,
          mapSubstate, mapInput, Generated.combineBlockGas]
      · simp only [mapComplete, Generated.run, Reference.run]
        simp only [hCharged, hReferenceCharged, hOog, hPreRefundGenerated,
          hPreRefundReference, hStateUsed]
        simp [hInputChargeSucceeded, mapStateCharge]
        try simp only [sourceOp_clearExecutionGas]
        try rw [hPreRefundState]
        try rw [hPreRefundReferenceSimple]
        try rw [← hSubstateSelf]
        try rw [hEffectiveSimple]
        simp only [Generated.sourceOp_finalizeRequest]
        rw [hWarmupBridge]
        split <;> rename_i warmupCase
        · simp [warmupCase, mapInput, Reference.settlementInputOf,
            Reference.effectiveBlockGas, Reference.gasView, mapSubstate,
            Generated.sourceOp_substateConstruction, hStateUsedSimple]
        · simpa [warmupCase, mapInput, Reference.settlementInputOf,
            Reference.effectiveBlockGas, Reference.gasView, mapSubstate,
            Generated.sourceOp_substateConstruction, hStateUsedSimple] using
            hEffectiveSimple
      · simp only [mapComplete, Generated.run, Reference.run]
        simp only [hCharged, hReferenceCharged, hOog, hPreRefundGenerated,
          hPreRefundReference, hStateUsed]
        simp [hInputChargeSucceeded, mapStateCharge]
        try simp only [sourceOp_clearExecutionGas]
        try rw [hPreRefundState]
        try rw [hPreRefundReferenceSimple]
        try rw [← hSubstateSelf]
        try rw [hEffectiveSimple]
        simp only [Generated.sourceOp_finalizeRequest]
        rw [hWarmupBridge]
        simp only [mapInput]
        split <;> rename_i warmupCase
        · simp [warmupCase, Reference.settlementInputOf,
            Reference.effectiveBlockGas, Reference.gasView, mapSubstate,
            Generated.sourceOp_substateConstruction, hStateUsedSimple]
        · rw [settlement_spent_bridge input
            (sourceOp_substateConstruction
              (if logApplies input
                  (sameAddress input.handoff.tx.sender input.handoff.recipient) false = true then
                [sourceOp_transferLogRequest input] else []) false)
            input.gas.preRefundGas
            (if input.handoff.spec.eip8037Enabled = true then
              stateGasUsed input.gas.stateCharge.gas else 0)]
          <;> simp [warmupCase, Reference.settlementInputOf, Reference.gasView,
            mapSubstate, Generated.sourceOp_substateConstruction, mapInput,
            hStateUsedSimple] <;> rfl
      · simp only [mapComplete, Generated.run, Reference.run]
        simp only [hCharged, hReferenceCharged, hOog, hPreRefundGenerated,
          hPreRefundReference, hStateUsed]
        simp [hInputChargeSucceeded, mapStateCharge, Bool.not_true]
        try simp only [sourceOp_clearExecutionGas]
        try rw [hPreRefundState]
        try rw [hPreRefundReferenceSimple]
        try rw [← hSubstateSelf]
        rw [post_execution_gas_bridge input
            (sameAddress input.handoff.tx.sender input.handoff.recipient) false
            (sourceOp_substateConstruction
              (if logApplies input
                  (sameAddress input.handoff.tx.sender input.handoff.recipient) false = true then
                [sourceOp_transferLogRequest input] else []) false)
            input.gas.preRefundGas
            (if input.handoff.spec.eip8037Enabled = true then
              stateGasUsed input.gas.stateCharge.gas else 0)]
          <;> simp [mapInput, hSubstateSelf]
          <;> rfl
      · simp only [mapComplete, Generated.run, Reference.run]
        simp only [hCharged, hReferenceCharged, hOog, hPreRefundGenerated,
          hPreRefundReference, hStateUsed]
        simp [hInputChargeSucceeded, mapStateCharge, Bool.not_true]
        try simp only [sourceOp_clearExecutionGas]
        try rw [hPreRefundState]
        try rw [hPreRefundReferenceSimple]
        try rw [← hSubstateSelf]
        rw [post_state_gas_bridge input
            (sameAddress input.handoff.tx.sender input.handoff.recipient) false
            (sourceOp_substateConstruction
              (if logApplies input
                  (sameAddress input.handoff.tx.sender input.handoff.recipient) false = true then
                [sourceOp_transferLogRequest input] else []) false)
            input.gas.preRefundGas
            (if input.handoff.spec.eip8037Enabled = true then
              stateGasUsed input.gas.stateCharge.gas else 0)]
          <;> simp [mapInput, hSubstateSelf]
          <;> rfl
      · simp only [mapComplete, Generated.run, Reference.run]
        simp only [hCharged, hReferenceCharged, hOog, hPreRefundGenerated,
          hPreRefundReference, hSubstate]
        simp [hInputChargeSucceeded, mapStateCharge, Bool.not_true]
        try simp only [sourceOp_clearExecutionGas]
        try rw [hPreRefundState]
        try rw [hPreRefundReferenceSimple]
        try rw [← hSubstateSelf]
        simp only [mapInput]
        rw [receipt_settlement_bridge input
            (sourceOp_substateConstruction
            (if logApplies input
                (sameAddress input.handoff.tx.sender input.handoff.recipient) false = true then
              [sourceOp_transferLogRequest input] else []) false)
            input.gas.preRefundGas
            (if input.handoff.spec.eip8037Enabled = true then
              stateGasUsed input.gas.stateCharge.gas else 0)
            false]
        rw [hSubstateSelf]
        rfl
      · simp only [mapComplete, Generated.run, Reference.run]
        simp only [hCharged, hReferenceCharged, hOog, hPreRefundGenerated,
          hPreRefundReference, hStateUsed, hSubstate]
        have hWriteSelf :
            writeApplies input (sameAddress input.handoff.tx.sender input.handoff.recipient) false =
              Reference.writesGuard (mapInput input)
                ((mapInput input).sender == (mapInput input).recipient) false := by
          simpa [mapInput, Generated.sameAddress] using
            (write_guard_bridge input
              (sameAddress input.handoff.tx.sender input.handoff.recipient) false)
        have hLogSelf :
            logApplies input (sameAddress input.handoff.tx.sender input.handoff.recipient) false =
              Reference.logsGuard (mapInput input)
                ((mapInput input).sender == (mapInput input).recipient) false := by
          simpa [mapInput, Generated.sameAddress] using
            (log_guard_bridge input
              (sameAddress input.handoff.tx.sender input.handoff.recipient) false)
        have hActionsSelf :
            evalSourcePredicate sourceActionsPredicate input
                (sameAddress input.handoff.tx.sender input.handoff.recipient) false =
              (mapInput input).tracingActions :=
          actions_guard_bridge input
            (sameAddress input.handoff.tx.sender input.handoff.recipient) false
        have hCountersSelf :
            evalSourcePredicate sourceCountersPredicate input
                (sameAddress input.handoff.tx.sender input.handoff.recipient) false =
              (!((mapInput input).skipValidation) && !((mapInput input).parallel)) :=
          counters_guard_bridge input
            (sameAddress input.handoff.tx.sender input.handoff.recipient) false
        have hReceiptSelf :
            evalSourcePredicate sourceReceiptPredicate input
                (sameAddress input.handoff.tx.sender input.handoff.recipient) false =
              (mapInput input).tracingReceipt :=
          receipt_guard_bridge input
            (sameAddress input.handoff.tx.sender input.handoff.recipient) false
        have hWritePredicate :
            evalSourcePredicate sourceRecipientPredicate input
                (sameAddress input.handoff.tx.sender input.handoff.recipient) false =
              Reference.writesGuard (mapInput input)
                ((mapInput input).sender == (mapInput input).recipient) false := by
          simpa [Generated.writeApplies] using hWriteSelf
        have hLogPredicate :
            evalSourcePredicate sourceLogPredicate input
                (sameAddress input.handoff.tx.sender input.handoff.recipient) false =
              Reference.logsGuard (mapInput input)
                ((mapInput input).sender == (mapInput input).recipient) false := by
          simpa [Generated.logApplies] using hLogSelf
        simp only [hInputChargeSucceeded, hMapStateChargeSucceeded, Bool.not_true, Bool.not_false]
        simp only [List.map_append]
        rw [map_if_gas_charge_prop]
        rw [map_pay_value_effect input
          (writeApplies input (sameAddress input.handoff.tx.sender input.handoff.recipient) false)]
        rw [map_add_recipient_effect input
          (writeApplies input (sameAddress input.handoff.tx.sender input.handoff.recipient) false)]
        rw [map_action_start_effect input
          (sameAddress input.handoff.tx.sender input.handoff.recipient) false
          input.gas.stateCharge.gas]
        simp only [Generated.gasRemaining]
        rw [map_action_terminal_prop]
        rw [map_if_clear_prop]
        rw [map_if_log_prop]
        rw [map_if_action_prop]
        rw [map_substate_effect]
        try rw [← hSubstateMap]
        rw [map_settlement_effect]
        rw [map_if_world_prop_effect]
        rw [map_access_effect_ordered input]
        rw [map_if_header_prop]
        rw [map_fee_effect]
        rw [map_if_transaction_prop]
        rw [map_final_world_effect]
        rw [map_root_effect]
        rw [map_receipt_effect]
        simp only [Generated.sourceOp_incrementEmptyCalls, List.map_cons, List.map_nil]
        simp only [charge_condition_bridge input,
          write_condition_bridge input false, log_condition_bridge input false,
          actions_condition_bridge input false, counters_condition_bridge input false,
          receipt_condition_bridge input false, access_tracing_bridge input,
          fees_tracing_bridge input, warmup_condition_bridge input]
        simp only [hWriteSelf, hLogSelf, Bool.false_eq_true]
        try simp only [if_bool_eq_true]
        simp only [mapTraceRequest, mapEffect]
        rw [pay_value_bridge input]
        rw [spec_view_bridge input]
        rw [transfer_log_bridge input]
        try rw [hStateUsedSimple]
        try rw [header_result_bridge input
          (sameAddress input.handoff.tx.sender input.handoff.recipient) false
          (sourceOp_substateConstruction
            (if logApplies input
                (sameAddress input.handoff.tx.sender input.handoff.recipient) false = true then
              [sourceOp_transferLogRequest input] else []) false)
          input.gas.preRefundGas
          (if input.handoff.spec.eip8037Enabled = true then
            stateGasUsed input.gas.stateCharge.gas else 0)]
        try rw [hEffectiveSimple]
        try rw [settlement_spent_bridge input
          (sourceOp_substateConstruction
            (if logApplies input
                (sameAddress input.handoff.tx.sender input.handoff.recipient) false = true then
              [sourceOp_transferLogRequest input] else []) false)
          input.gas.preRefundGas
          (if input.handoff.spec.eip8037Enabled = true then
            stateGasUsed input.gas.stateCharge.gas else 0)]
        apply journal_layout
    · have hInputChargeSucceeded : input.gas.stateCharge.succeeded = false := by
        rw [hChargeEq]
        simpa using chargeSucceeded
      have hMapChargeSucceeded : (mapInput input).chargedSucceeded = false := by
        simpa [mapInput] using hInputChargeSucceeded
      have hCharged :
          (if chargeApplies input (sameAddress input.handoff.tx.sender input.handoff.recipient) = true then
            sourceOp_consumeStateGas input
          else { succeeded := true, gas := input.handoff.gasAvailable }) = input.gas.stateCharge := by
        simp [charge, hComputed]
      have hReferenceCharged :
          (if Reference.chargeGuard (mapInput input)
              ((mapInput input).sender == (mapInput input).recipient) = true then
            Reference.chargedState (mapInput input)
          else { succeeded := true, gas := Reference.gasState (mapInput input) }) =
            mapStateCharge input.gas.stateCharge := by
        simp [hReferenceChargeMapSelf, charged_state_bridge]
      have hOog :
          (!(if chargeApplies input (sameAddress input.handoff.tx.sender input.handoff.recipient) = true then
            sourceOp_consumeStateGas input
          else { succeeded := true, gas := input.handoff.gasAvailable }).succeeded) = true := by
        simp [hCharged, hInputChargeSucceeded]
      have hPreRefund := adapter_pre_refund_coherence input h
      have hPreRefundGenerated :
          Generated.preRefundGas input.handoff.tx.gasLimit
              (if (!(if chargeApplies input
                    (sameAddress input.handoff.tx.sender input.handoff.recipient) = true then
                  sourceOp_consumeStateGas input
                else { succeeded := true, gas := input.handoff.gasAvailable }).succeeded) = true then
                clearExecutionGas
                  (if chargeApplies input
                      (sameAddress input.handoff.tx.sender input.handoff.recipient) = true then
                    sourceOp_consumeStateGas input
                  else { succeeded := true, gas := input.handoff.gasAvailable }).gas
              else
                (if chargeApplies input
                    (sameAddress input.handoff.tx.sender input.handoff.recipient) = true then
                  sourceOp_consumeStateGas input
                else { succeeded := true, gas := input.handoff.gasAvailable }).gas) =
            input.gas.preRefundGas := by
        simpa [charge, hCharged, hInputChargeSucceeded, chargeSucceeded, hChargeEq, hComputed] using hPreRefund.symm
      have hPreRefundState :
          Generated.preRefundGas input.handoff.tx.gasLimit
              (Generated.clearExecutionGas input.gas.stateCharge.gas) =
            input.gas.preRefundGas := by
        simpa [charge, chargeSucceeded, hChargeEq] using hPreRefund.symm
      have hPreRefundReference :
          Reference.preRefundFor (mapInput input)
              (Reference.clearExecution (mapStateCharge input.gas.stateCharge).gas).execution
              (Reference.clearExecution (mapStateCharge input.gas.stateCharge).gas).reservoir =
            input.gas.preRefundGas := by
        calc
          Reference.preRefundFor (mapInput input)
              (Reference.clearExecution (mapStateCharge input.gas.stateCharge).gas).execution
              (Reference.clearExecution (mapStateCharge input.gas.stateCharge).gas).reservoir =
              Generated.preRefundGas input.handoff.tx.gasLimit
                (Generated.clearExecutionGas input.gas.stateCharge.gas) := by
                  simpa [mapStateCharge, Generated.clearExecutionGas, Reference.clearExecution] using
                    (pre_refund_bridge input (Generated.clearExecutionGas input.gas.stateCharge.gas)).symm
          _ = input.gas.preRefundGas := hPreRefundState
      have hStateUsed :
          (if input.handoff.spec.eip8037Enabled then
              stateGasUsed
                (if (!(if chargeApplies input
                    (sameAddress input.handoff.tx.sender input.handoff.recipient) = true then
                  sourceOp_consumeStateGas input
                else { succeeded := true, gas := input.handoff.gasAvailable }).succeeded) = true then
                  clearExecutionGas
                    (if chargeApplies input
                        (sameAddress input.handoff.tx.sender input.handoff.recipient) = true then
                      sourceOp_consumeStateGas input
                    else { succeeded := true, gas := input.handoff.gasAvailable }).gas
                else
                  (if chargeApplies input
                      (sameAddress input.handoff.tx.sender input.handoff.recipient) = true then
                    sourceOp_consumeStateGas input
                  else { succeeded := true, gas := input.handoff.gasAvailable }).gas)
            else 0) =
            (if (mapInput input).eip8037 then
                (if (!(if Reference.chargeGuard (mapInput input)
                    ((mapInput input).sender == (mapInput input).recipient) = true then
                  Reference.chargedState (mapInput input)
                else { succeeded := true, gas := Reference.gasState (mapInput input) }).succeeded) = true then
                  (Reference.clearExecution
                    (if Reference.chargeGuard (mapInput input)
                        ((mapInput input).sender == (mapInput input).recipient) = true then
                      Reference.chargedState (mapInput input)
                    else { succeeded := true, gas := Reference.gasState (mapInput input) }).gas).stateUsed
                else
                  (if Reference.chargeGuard (mapInput input)
                      ((mapInput input).sender == (mapInput input).recipient) = true then
                    Reference.chargedState (mapInput input)
                  else { succeeded := true, gas := Reference.gasState (mapInput input) }).gas.stateUsed)
              else 0) := by
        have hChargedStateExpanded := charged_state_bridge input
        simp only [mapInput] at hChargedStateExpanded
        simp [hInputChargeSucceeded] at hChargedStateExpanded
        rw [hComputed, hReferenceChargeMapSelf]
        simp [charge, hReferenceChargeSelf, hChargedStateExpanded, hInputChargeSucceeded,
          chargeSucceeded, mapStateCharge, mapInput, stateGasUsed,
          Generated.clearExecutionGas, Reference.clearExecution]
      have hSubstate := substate_bridge input
        (sameAddress input.handoff.tx.sender input.handoff.recipient) true
      have hSubstateSelf :
          mapSubstate
              (sourceOp_substateConstruction
                (if logApplies input
                    (sameAddress input.handoff.tx.sender input.handoff.recipient) true = true then
                  [sourceOp_transferLogRequest input] else []) true) =
            { output := []
              refund := 0
              logs :=
                if Reference.logsGuard (mapInput input)
                    (sameAddress input.handoff.tx.sender input.handoff.recipient) true = true then
                  [Reference.expectedTransferLog (mapInput input)] else []
              shouldRevert := false
              isError := false
              error := none
              exception := if true then Reference.EvmException.outOfGas else Reference.EvmException.none } := by
        exact hSubstate
      have hSubstateMap :
          mapSubstate
              (sourceOp_substateConstruction
                (if logApplies input
                    (sameAddress input.handoff.tx.sender input.handoff.recipient) true = true then
                  [sourceOp_transferLogRequest input] else []) true) =
            { output := []
              refund := 0
              logs :=
                if Reference.logsGuard (mapInput input)
                    ((mapInput input).sender == (mapInput input).recipient) true = true then
                  [Reference.expectedTransferLog (mapInput input)] else []
              shouldRevert := false
              isError := false
              error := none
              exception := Reference.EvmException.outOfGas } := by
        change mapSubstate
              (sourceOp_substateConstruction
                (if logApplies input
                    (sameAddress input.handoff.tx.sender input.handoff.recipient) true = true then
                  [sourceOp_transferLogRequest input] else []) true) =
            { output := []
              refund := 0
              logs :=
                if Reference.logsGuard (mapInput input)
                    (sameAddress input.handoff.tx.sender input.handoff.recipient) true = true then
                  [Reference.expectedTransferLog (mapInput input)] else []
              shouldRevert := false
              isError := false
              error := none
              exception := if true then Reference.EvmException.outOfGas else Reference.EvmException.none }
        exact hSubstateSelf
      have hPreRefundReferenceSimple :
          Reference.preRefundFor (mapInput input)
              (Reference.clearExecution (mapStateCharge input.gas.stateCharge).gas).execution
              (Reference.clearExecution (mapStateCharge input.gas.stateCharge).gas).reservoir =
            input.gas.preRefundGas := hPreRefundReference
      have hPreRefundReferenceZero :
          Reference.preRefundFor (mapInput input) 0 input.gas.stateCharge.gas.stateReservoir =
            input.gas.preRefundGas := by
        simpa [mapStateCharge, Reference.clearExecution] using hPreRefundReferenceSimple
      have hStateUsedSimple :
          (if input.handoff.spec.eip8037Enabled = true then
              stateGasUsed (Generated.clearExecutionGas input.gas.stateCharge.gas) else 0) =
            (if (mapInput input).eip8037 = true then
              (Reference.clearExecution (mapStateCharge input.gas.stateCharge).gas).stateUsed else 0) := by
        rfl
      have hStateUsedReference :
          (if (mapInput input).eip8037 = true then
              (Reference.clearExecution (mapStateCharge input.gas.stateCharge).gas).stateUsed else 0) =
            (if input.handoff.spec.eip8037Enabled = true then
              input.gas.stateCharge.gas.stateGasUsed else 0) := by
        rfl
      have hWarmupBridge :
          Generated.isWarmup input.handoff.options =
            Reference.optionIsWarmup input.handoff.options.raw := by
        rfl
      have hEffectiveSimple :
          Generated.effectiveBlockGas
              (sourceOp_refundRequest
                (settlementInputOf input
                  (sourceOp_substateConstruction
                    (if logApplies input
                        (sameAddress input.handoff.tx.sender input.handoff.recipient) true = true then
                      [sourceOp_transferLogRequest input] else []) true)
                  input.gas.preRefundGas
                  (if input.handoff.spec.eip8037Enabled = true then
                    stateGasUsed (Generated.clearExecutionGas input.gas.stateCharge.gas) else 0))) =
            Reference.effectiveBlockGas
              (Reference.gasView
                (Reference.settlement
                  (Reference.settlementInputOf (mapInput input)
                    (mapSubstate
                      (sourceOp_substateConstruction
                        (if logApplies input
                            (sameAddress input.handoff.tx.sender input.handoff.recipient) true = true then
                          [sourceOp_transferLogRequest input] else []) true))
                    input.gas.preRefundGas
                    (if (mapInput input).eip8037 = true then
                      (Reference.clearExecution (mapStateCharge input.gas.stateCharge).gas).stateUsed else 0)))) := by
        simpa [hStateUsedSimple] using
          (effective_block_bridge input
            (sourceOp_substateConstruction
              (if logApplies input
                  (sameAddress input.handoff.tx.sender input.handoff.recipient) true = true then
                [sourceOp_transferLogRequest input] else []) true)
            input.gas.preRefundGas
            (if (mapInput input).eip8037 = true then
              (Reference.clearExecution (mapStateCharge input.gas.stateCharge).gas).stateUsed else 0))
      apply result_ext
      · simp only [mapComplete, Generated.run, Reference.run]
        rw [hCharged]
        rw [hReferenceCharged]
        simp [hInputChargeSucceeded, mapStateCharge]
      · simp only [mapComplete, Generated.run, Reference.run]
        simp only [hCharged, hReferenceCharged, hOog, hPreRefundGenerated,
          hPreRefundReference, hSubstate]
        simp [hInputChargeSucceeded, mapStateCharge, Reference.clearExecution, Bool.not_true]
        try simp only [sourceOp_clearExecutionGas]
        try rw [hPreRefundState]
        try rw [hPreRefundReferenceZero]
        try rw [← hSubstateMap]
        exact settlement_spent_bridge input
          (sourceOp_substateConstruction
            (if logApplies input
                (sameAddress input.handoff.tx.sender input.handoff.recipient) true = true then
              [sourceOp_transferLogRequest input] else []) true)
          input.gas.preRefundGas
          (if input.handoff.spec.eip8037Enabled = true then
            stateGasUsed (Generated.clearExecutionGas input.gas.stateCharge.gas) else 0)
      · simp only [mapComplete, Generated.run, Reference.run]
        simp only [hCharged, hReferenceCharged, hOog, hPreRefundGenerated,
          hPreRefundReference]
        simpa [hInputChargeSucceeded, mapStateCharge, Reference.clearExecution,
           Generated.clearExecutionGas, sourceOp_clearExecutionGas] using
          hPreRefundState.trans hPreRefundReferenceZero.symm
      · simp only [mapComplete, Generated.run, Reference.run]
        simp only [hCharged, hReferenceCharged, hOog, hPreRefundGenerated,
          hPreRefundReference]
        simp [hInputChargeSucceeded, mapStateCharge, Reference.clearExecution]
        try simp only [sourceOp_clearExecutionGas]
        try rw [hPreRefundState]
        try rw [hPreRefundReferenceZero]
        try rw [← hSubstateMap]
        try simp only [sourceOp_clearExecutionGas]
        refine Eq.trans ?_ (header_result_bridge input
          (sameAddress input.handoff.tx.sender input.handoff.recipient) true
          (sourceOp_substateConstruction
            (if logApplies input
                (sameAddress input.handoff.tx.sender input.handoff.recipient) true = true then
              [sourceOp_transferLogRequest input] else []) true)
          input.gas.preRefundGas
          (if input.handoff.spec.eip8037Enabled = true then
            stateGasUsed (Generated.clearExecutionGas input.gas.stateCharge.gas) else 0))
        simp [Generated.sourceOp_substateConstruction, Reference.settlementInputOf,
          mapSubstate, mapInput, Generated.combineBlockGas]
      · simp only [mapComplete, Generated.run, Reference.run]
        simp only [hCharged, hReferenceCharged, hOog, hPreRefundGenerated,
          hPreRefundReference, hStateUsed]
        simp [hInputChargeSucceeded, mapStateCharge, Reference.clearExecution]
        try simp only [sourceOp_clearExecutionGas]
        try rw [hPreRefundState]
        try rw [hPreRefundReferenceZero]
        try rw [← hSubstateMap]
        simp only [Generated.sourceOp_finalizeRequest]
        rw [hWarmupBridge]
        split <;> rename_i warmupCase
        · simp [warmupCase, mapInput, Reference.settlementInputOf,
            Reference.effectiveBlockGas, Reference.gasView, mapSubstate,
            Generated.sourceOp_substateConstruction, hStateUsedSimple]
        · simpa [warmupCase, hPreRefundReferenceZero, hSubstateMap, mapInput, Reference.settlementInputOf,
            Reference.effectiveBlockGas, Reference.gasView, Reference.gasState, mapSubstate,
            Generated.sourceOp_substateConstruction, sourceOp_clearExecutionGas,
            Generated.clearExecutionGas, Reference.clearExecution, mapStateCharge,
            hStateUsedReference] using
            hEffectiveSimple
      · simp only [mapComplete, Generated.run, Reference.run]
        simp only [hCharged, hReferenceCharged, hOog, hPreRefundGenerated,
          hPreRefundReference, hStateUsed]
        simp [hInputChargeSucceeded, mapStateCharge, Reference.clearExecution]
        try simp only [sourceOp_clearExecutionGas]
        try rw [hPreRefundState]
        try rw [hPreRefundReferenceZero]
        try rw [← hSubstateMap]
        simp only [Generated.sourceOp_finalizeRequest]
        rw [hWarmupBridge]
        simp only [mapInput]
        split <;> rename_i warmupCase
        · simp [warmupCase, Reference.settlementInputOf,
            Reference.effectiveBlockGas, Reference.gasView, mapSubstate,
            Generated.sourceOp_substateConstruction, hStateUsedSimple]
        · rw [settlement_spent_bridge input
            (sourceOp_substateConstruction
              (if logApplies input
                  (sameAddress input.handoff.tx.sender input.handoff.recipient) true = true then
                [sourceOp_transferLogRequest input] else []) true)
            input.gas.preRefundGas
            (if input.handoff.spec.eip8037Enabled = true then
              stateGasUsed (Generated.clearExecutionGas input.gas.stateCharge.gas) else 0)]
          <;> simp [warmupCase, Reference.settlementInputOf, Reference.gasView,
            mapSubstate, Generated.sourceOp_substateConstruction, mapInput,
            hStateUsedSimple, Reference.clearExecution] <;> rfl
      · simp only [mapComplete, Generated.run, Reference.run]
        simp only [hCharged, hReferenceCharged, hOog, hPreRefundGenerated,
          hPreRefundReference, hStateUsed]
        simp [hInputChargeSucceeded, mapStateCharge, Reference.clearExecution, Bool.not_true]
        try simp only [sourceOp_clearExecutionGas]
        try rw [hPreRefundState]
        try rw [hPreRefundReferenceZero]
        try rw [← hSubstateMap]
        rw [post_execution_gas_bridge input
            (sameAddress input.handoff.tx.sender input.handoff.recipient) true
            (sourceOp_substateConstruction
              (if logApplies input
                  (sameAddress input.handoff.tx.sender input.handoff.recipient) true = true then
                [sourceOp_transferLogRequest input] else []) true)
            input.gas.preRefundGas
            (if input.handoff.spec.eip8037Enabled = true then
              stateGasUsed (Generated.clearExecutionGas input.gas.stateCharge.gas) else 0)]
          <;> simp [mapInput, hSubstateSelf]
          <;> rfl
      · simp only [mapComplete, Generated.run, Reference.run]
        simp only [hCharged, hReferenceCharged, hOog, hPreRefundGenerated,
          hPreRefundReference, hStateUsed]
        simp [hInputChargeSucceeded, mapStateCharge, Reference.clearExecution, Bool.not_true]
        try simp only [sourceOp_clearExecutionGas]
        try rw [hPreRefundState]
        try rw [hPreRefundReferenceZero]
        try rw [← hSubstateMap]
        rw [post_state_gas_bridge input
            (sameAddress input.handoff.tx.sender input.handoff.recipient) true
            (sourceOp_substateConstruction
              (if logApplies input
                  (sameAddress input.handoff.tx.sender input.handoff.recipient) true = true then
                [sourceOp_transferLogRequest input] else []) true)
            input.gas.preRefundGas
            (if input.handoff.spec.eip8037Enabled = true then
              stateGasUsed (Generated.clearExecutionGas input.gas.stateCharge.gas) else 0)]
          <;> simp [mapInput, hSubstateSelf]
          <;> rfl
      · simp only [mapComplete, Generated.run, Reference.run]
        simp only [hCharged, hReferenceCharged, hOog, hPreRefundGenerated,
          hPreRefundReference, hSubstate]
        simp [hInputChargeSucceeded, mapStateCharge, Reference.clearExecution, Bool.not_true]
        try simp only [sourceOp_clearExecutionGas]
        try rw [hPreRefundState]
        try rw [hPreRefundReferenceZero]
        try rw [← hSubstateMap]
        simp only [mapInput]
        rw [receipt_settlement_bridge input
            (sourceOp_substateConstruction
              (if logApplies input
                  (sameAddress input.handoff.tx.sender input.handoff.recipient) true = true then
                [sourceOp_transferLogRequest input] else []) true)
            input.gas.preRefundGas
            (if input.handoff.spec.eip8037Enabled = true then
              stateGasUsed (Generated.clearExecutionGas input.gas.stateCharge.gas) else 0)
            true]
        rw [hSubstateSelf]
        rfl
      · simp only [mapComplete, Generated.run, Reference.run]
        simp only [hCharged, hReferenceCharged, hOog, hPreRefundGenerated,
          hPreRefundReference]
        have hWriteSelf :
            writeApplies input (sameAddress input.handoff.tx.sender input.handoff.recipient) true =
              Reference.writesGuard (mapInput input)
                ((mapInput input).sender == (mapInput input).recipient) true := by
          simpa [mapInput, Generated.sameAddress] using
            (write_guard_bridge input
              (sameAddress input.handoff.tx.sender input.handoff.recipient) true)
        have hLogSelf :
            logApplies input (sameAddress input.handoff.tx.sender input.handoff.recipient) true =
              Reference.logsGuard (mapInput input)
                ((mapInput input).sender == (mapInput input).recipient) true := by
          simpa [mapInput, Generated.sameAddress] using
            (log_guard_bridge input
              (sameAddress input.handoff.tx.sender input.handoff.recipient) true)
        have hActionsSelf :
            evalSourcePredicate sourceActionsPredicate input
                (sameAddress input.handoff.tx.sender input.handoff.recipient) true =
              (mapInput input).tracingActions :=
          actions_guard_bridge input
            (sameAddress input.handoff.tx.sender input.handoff.recipient) true
        have hCountersSelf :
            evalSourcePredicate sourceCountersPredicate input
                (sameAddress input.handoff.tx.sender input.handoff.recipient) true =
              (!((mapInput input).skipValidation) && !((mapInput input).parallel)) :=
          counters_guard_bridge input
            (sameAddress input.handoff.tx.sender input.handoff.recipient) true
        have hReceiptSelf :
            evalSourcePredicate sourceReceiptPredicate input
                (sameAddress input.handoff.tx.sender input.handoff.recipient) true =
              (mapInput input).tracingReceipt :=
          receipt_guard_bridge input
            (sameAddress input.handoff.tx.sender input.handoff.recipient) true
        have hWritePredicate :
            evalSourcePredicate sourceRecipientPredicate input
                (sameAddress input.handoff.tx.sender input.handoff.recipient) true =
              Reference.writesGuard (mapInput input)
                ((mapInput input).sender == (mapInput input).recipient) true := by
          simpa [Generated.writeApplies] using hWriteSelf
        have hLogPredicate :
            evalSourcePredicate sourceLogPredicate input
                (sameAddress input.handoff.tx.sender input.handoff.recipient) true =
              Reference.logsGuard (mapInput input)
                ((mapInput input).sender == (mapInput input).recipient) true := by
          simpa [Generated.logApplies] using hLogSelf
        simp only [hInputChargeSucceeded, Bool.not_true, Bool.not_false,
          hStateUsed]
        simp only [List.map_append]
        rw [map_if_gas_charge_prop]
        rw [map_pay_value_effect input
          (writeApplies input (sameAddress input.handoff.tx.sender input.handoff.recipient) true)]
        rw [map_add_recipient_effect input
          (writeApplies input (sameAddress input.handoff.tx.sender input.handoff.recipient) true)]
        rw [map_action_start_effect input
          (sameAddress input.handoff.tx.sender input.handoff.recipient) false
          input.gas.stateCharge.gas]
        simp only [Generated.gasRemaining]
        rw [map_action_terminal_prop]
        rw [map_if_clear_prop]
        rw [map_if_log_prop]
        rw [map_if_action_prop]
        rw [map_substate_effect]
        try rw [← hSubstateMap]
        rw [map_settlement_effect]
        rw [map_if_world_prop_effect]
        rw [map_access_effect_ordered input]
        rw [map_if_header_prop]
        rw [map_fee_effect]
        rw [map_if_transaction_prop]
        rw [map_final_world_effect]
        rw [map_root_effect]
        rw [map_receipt_effect]
        simp only [Generated.sourceOp_incrementEmptyCalls, List.map_cons, List.map_nil]
        simp only [charge_condition_bridge input,
          write_condition_bridge input true, log_condition_bridge input true,
          actions_condition_bridge input true, counters_condition_bridge input true,
          receipt_condition_bridge input true, access_tracing_bridge input,
          fees_tracing_bridge input, warmup_condition_bridge input]
        simp only [hWriteSelf, hLogSelf, Bool.false_eq_true]
        try simp only [if_bool_eq_true]
        apply journal_layout
      · simp only [mapComplete, Generated.run, Reference.run]
        simp only [hCharged, hReferenceCharged, hOog, hPreRefundGenerated,
          hPreRefundReference]
        simp only [hInputChargeSucceeded, Bool.not_true, Bool.not_false]
        simpa only [hStateUsed] using
          (post_execution_gas_bridge input
            (sameAddress input.handoff.tx.sender input.handoff.recipient) true
            (sourceOp_substateConstruction
              (if logApplies input
                  (sameAddress input.handoff.tx.sender input.handoff.recipient) true = true then
                [sourceOp_transferLogRequest input] else []) true)
            input.gas.preRefundGas
            (if input.handoff.spec.eip8037Enabled = true then
              stateGasUsed (Generated.clearExecutionGas input.gas.stateCharge.gas) else 0))
      · simp only [mapComplete, Generated.run, Reference.run]
        simp only [hCharged, hReferenceCharged, hOog, hPreRefundGenerated,
          hPreRefundReference]
        simp only [hInputChargeSucceeded, Bool.not_true, Bool.not_false]
        simpa only [hStateUsed] using
          (post_state_gas_bridge input
            (sameAddress input.handoff.tx.sender input.handoff.recipient) true
            (sourceOp_substateConstruction
              (if logApplies input
                  (sameAddress input.handoff.tx.sender input.handoff.recipient) true = true then
                [sourceOp_transferLogRequest input] else []) true)
            input.gas.preRefundGas
            (if input.handoff.spec.eip8037Enabled = true then
              stateGasUsed (Generated.clearExecutionGas input.gas.stateCharge.gas) else 0))
      · simp only [mapComplete, Generated.run, Reference.run]
        simp only [hCharged, hReferenceCharged, hOog, hPreRefundGenerated,
          hPreRefundReference]
        simp only [hInputChargeSucceeded, Bool.not_true, Bool.not_false]
        simpa only [hStateUsed] using
          (receipt_settlement_bridge input
            (sourceOp_substateConstruction
              (if logApplies input
                  (sameAddress input.handoff.tx.sender input.handoff.recipient) true = true then
                [sourceOp_transferLogRequest input] else []) true)
            input.gas.preRefundGas
            (if input.handoff.spec.eip8037Enabled = true then
              stateGasUsed (Generated.clearExecutionGas input.gas.stateCharge.gas) else 0)
            true)
      · have hWriteSelf :
            writeApplies input (sameAddress input.handoff.tx.sender input.handoff.recipient) true =
              Reference.writesGuard (mapInput input)
                ((mapInput input).sender == (mapInput input).recipient) true := by
          simpa [mapInput, Generated.sameAddress] using
            (write_guard_bridge input
              (sameAddress input.handoff.tx.sender input.handoff.recipient) true)
        have hLogSelf :
            logApplies input (sameAddress input.handoff.tx.sender input.handoff.recipient) true =
              Reference.logsGuard (mapInput input)
                ((mapInput input).sender == (mapInput input).recipient) true := by
          simpa [mapInput, Generated.sameAddress] using
            (log_guard_bridge input
              (sameAddress input.handoff.tx.sender input.handoff.recipient) true)
        have hActionsSelf :
            evalSourcePredicate sourceActionsPredicate input
                (sameAddress input.handoff.tx.sender input.handoff.recipient) true =
              (mapInput input).tracingActions :=
          actions_guard_bridge input
            (sameAddress input.handoff.tx.sender input.handoff.recipient) true
        have hCountersSelf :
            evalSourcePredicate sourceCountersPredicate input
                (sameAddress input.handoff.tx.sender input.handoff.recipient) true =
              (!((mapInput input).skipValidation) && !((mapInput input).parallel)) :=
          counters_guard_bridge input
            (sameAddress input.handoff.tx.sender input.handoff.recipient) true
        have hReceiptSelf :
            evalSourcePredicate sourceReceiptPredicate input
                (sameAddress input.handoff.tx.sender input.handoff.recipient) true =
              (mapInput input).tracingReceipt :=
          receipt_guard_bridge input
            (sameAddress input.handoff.tx.sender input.handoff.recipient) true
        have hWritePredicate :
            evalSourcePredicate sourceRecipientPredicate input
                (sameAddress input.handoff.tx.sender input.handoff.recipient) true =
              Reference.writesGuard (mapInput input)
                ((mapInput input).sender == (mapInput input).recipient) true := by
          simpa [Generated.writeApplies] using hWriteSelf
        have hLogPredicate :
            evalSourcePredicate sourceLogPredicate input
                (sameAddress input.handoff.tx.sender input.handoff.recipient) true =
              Reference.logsGuard (mapInput input)
                ((mapInput input).sender == (mapInput input).recipient) true := by
          simpa [Generated.logApplies] using hLogSelf
        simp only [mapComplete, Generated.run, Reference.run]
        simp only [hCharged, hReferenceCharged, hOog, hPreRefundGenerated,
          hPreRefundReference, hStateUsed]
        simp only [hInputChargeSucceeded, Bool.not_true, Bool.not_false]
        simp only [List.map_append]
        rw [map_if_gas_charge_prop]
        rw [map_pay_value_effect input
          (writeApplies input (sameAddress input.handoff.tx.sender input.handoff.recipient) true)]
        rw [map_add_recipient_effect input
          (writeApplies input (sameAddress input.handoff.tx.sender input.handoff.recipient) true)]
        rw [map_action_start_effect input
          (sameAddress input.handoff.tx.sender input.handoff.recipient) false
          input.gas.stateCharge.gas]
        simp only [Generated.gasRemaining]
        rw [map_action_terminal_prop]
        rw [map_if_clear_prop]
        rw [map_if_log_prop]
        rw [map_if_action_prop]
        rw [map_substate_effect]
        try rw [← hSubstateMap]
        rw [map_settlement_effect]
        rw [map_if_world_prop_effect]
        rw [map_access_effect_ordered input]
        rw [map_if_header_prop]
        rw [map_fee_effect]
        rw [map_if_transaction_prop]
        rw [map_final_world_effect]
        rw [map_root_effect]
        rw [map_receipt_effect]
        simp only [Generated.sourceOp_incrementEmptyCalls, List.map_cons, List.map_nil]
        simp only [charge_condition_bridge input,
          write_condition_bridge input true, log_condition_bridge input true,
          actions_condition_bridge input true, counters_condition_bridge input true,
          receipt_condition_bridge input true, access_tracing_bridge input,
          fees_tracing_bridge input, warmup_condition_bridge input]
        simp only [hWriteSelf, hLogSelf, Bool.false_eq_true]
        try rw [header_result_bridge input
          (sameAddress input.handoff.tx.sender input.handoff.recipient) true
          (sourceOp_substateConstruction
            (if logApplies input
                (sameAddress input.handoff.tx.sender input.handoff.recipient) true = true then
              [sourceOp_transferLogRequest input] else []) true)
          input.gas.preRefundGas
          (if input.handoff.spec.eip8037Enabled = true then
            stateGasUsed (Generated.clearExecutionGas input.gas.stateCharge.gas) else 0)]
        try rw [hEffectiveSimple]
        try rw [settlement_spent_bridge input
          (sourceOp_substateConstruction
            (if logApplies input
                (sameAddress input.handoff.tx.sender input.handoff.recipient) true = true then
              [sourceOp_transferLogRequest input] else []) true)
          input.gas.preRefundGas
          (if input.handoff.spec.eip8037Enabled = true then
            stateGasUsed (Generated.clearExecutionGas input.gas.stateCharge.gas) else 0)]
        apply journal_layout
  · have hReferenceNotCharge :
        ¬ Reference.chargeGuard (mapInput input)
            (Generated.sameAddress input.handoff.tx.sender input.handoff.recipient) = true := by
      intro referenceCharge
      apply charge
      rw [charge_guard_bridge input]
      exact referenceCharge
    have hReferenceNotChargeExpanded := hReferenceNotCharge
    simp only [mapInput] at hReferenceNotChargeExpanded
    have hReferenceNotChargeMapSelf :
        ¬ Reference.chargeGuard (mapInput input)
            ((mapInput input).sender == (mapInput input).recipient) = true := by
      simpa [mapInput, Generated.sameAddress] using hReferenceNotCharge
    have hReferenceNotChargeSelf := hReferenceNotCharge
    simp only [mapInput, Generated.sameAddress] at hReferenceNotChargeSelf
    have hCharged :
        (if chargeApplies input (sameAddress input.handoff.tx.sender input.handoff.recipient) = true then
          sourceOp_consumeStateGas input
        else { succeeded := true, gas := input.handoff.gasAvailable }) =
          { succeeded := true, gas := input.handoff.gasAvailable } := by
      simp [charge]
    have hReferenceCharged :
        (if Reference.chargeGuard (mapInput input)
            ((mapInput input).sender == (mapInput input).recipient) = true then
          Reference.chargedState (mapInput input)
        else { succeeded := true, gas := Reference.gasState (mapInput input) }) =
          { succeeded := true, gas := Reference.gasState (mapInput input) } := by
      simp [hReferenceNotChargeMapSelf]
    have hOog :
        (!(if chargeApplies input (sameAddress input.handoff.tx.sender input.handoff.recipient) = true then
          sourceOp_consumeStateGas input
        else { succeeded := true, gas := input.handoff.gasAvailable }).succeeded) = false := by
      simp [hCharged]
    have hPreRefund := adapter_pre_refund_coherence input h
    have hPreRefundGenerated :
        Generated.preRefundGas input.handoff.tx.gasLimit
            (if (!(if chargeApplies input
                  (sameAddress input.handoff.tx.sender input.handoff.recipient) = true then
                sourceOp_consumeStateGas input
              else { succeeded := true, gas := input.handoff.gasAvailable }).succeeded) = true then
              clearExecutionGas
                (if chargeApplies input
                    (sameAddress input.handoff.tx.sender input.handoff.recipient) = true then
                  sourceOp_consumeStateGas input
                else { succeeded := true, gas := input.handoff.gasAvailable }).gas
            else
              (if chargeApplies input
                  (sameAddress input.handoff.tx.sender input.handoff.recipient) = true then
                sourceOp_consumeStateGas input
              else { succeeded := true, gas := input.handoff.gasAvailable }).gas) =
          input.gas.preRefundGas := by
      simpa [charge] using hPreRefund.symm
    have hPreRefundState :
        Generated.preRefundGas input.handoff.tx.gasLimit input.handoff.gasAvailable =
          input.gas.preRefundGas := by
      simpa [charge] using hPreRefund.symm
    have hPreRefundReference :
        Reference.preRefundFor (mapInput input)
            input.handoff.gasAvailable.execution input.handoff.gasAvailable.stateReservoir =
          input.gas.preRefundGas := by
      calc
        Reference.preRefundFor (mapInput input)
            input.handoff.gasAvailable.execution input.handoff.gasAvailable.stateReservoir =
            Generated.preRefundGas input.handoff.tx.gasLimit input.handoff.gasAvailable := by
              simpa using (pre_refund_bridge input input.handoff.gasAvailable).symm
        _ = input.gas.preRefundGas := hPreRefundState
    have hSubstate := substate_bridge input
      (sameAddress input.handoff.tx.sender input.handoff.recipient) false
    have hSubstateSelf :
        mapSubstate
            (sourceOp_substateConstruction
              (if logApplies input
                  (sameAddress input.handoff.tx.sender input.handoff.recipient) false = true then
                [sourceOp_transferLogRequest input] else []) false) =
          { output := []
            refund := 0
            logs :=
              if Reference.logsGuard (mapInput input)
                  (sameAddress input.handoff.tx.sender input.handoff.recipient) false = true then
                [Reference.expectedTransferLog (mapInput input)] else []
            shouldRevert := false
            isError := false
            error := none
            exception := Reference.EvmException.none } := by
      exact hSubstate
    have hSubstateMap :
        mapSubstate
            (sourceOp_substateConstruction
              (if logApplies input
                  (sameAddress input.handoff.tx.sender input.handoff.recipient) false = true then
                [sourceOp_transferLogRequest input] else []) false) =
          { output := []
            refund := 0
            logs :=
              if Reference.logsGuard (mapInput input)
                  ((mapInput input).sender == (mapInput input).recipient) false = true then
                [Reference.expectedTransferLog (mapInput input)] else []
            shouldRevert := false
            isError := false
            error := none
            exception := Reference.EvmException.none } := by
      change mapSubstate
            (sourceOp_substateConstruction
              (if logApplies input
                  (sameAddress input.handoff.tx.sender input.handoff.recipient) false = true then
                [sourceOp_transferLogRequest input] else []) false) =
          { output := []
            refund := 0
            logs :=
              if Reference.logsGuard (mapInput input)
                  (sameAddress input.handoff.tx.sender input.handoff.recipient) false = true then
                [Reference.expectedTransferLog (mapInput input)] else []
            shouldRevert := false
            isError := false
            error := none
            exception := if false then Reference.EvmException.outOfGas else Reference.EvmException.none }
      exact hSubstateSelf
    have hPreRefundReferenceSimple :
        Reference.preRefundFor (mapInput input)
            input.handoff.gasAvailable.execution input.handoff.gasAvailable.stateReservoir =
          input.gas.preRefundGas := hPreRefundReference
    have hPreRefundReferenceState :
        Reference.preRefundFor (mapInput input)
            (Reference.gasState (mapInput input)).execution
            (Reference.gasState (mapInput input)).reservoir =
          input.gas.preRefundGas := by
      simpa [Reference.gasState, mapInput] using hPreRefundReferenceSimple
    have hStateUsedSimple :
        (if input.handoff.spec.eip8037Enabled = true then
            stateGasUsed input.handoff.gasAvailable else 0) =
          (if (mapInput input).eip8037 = true then
            (Reference.gasState (mapInput input)).stateUsed else 0) := by
      rfl
    have hWarmupBridge :
        Generated.isWarmup input.handoff.options =
          Reference.optionIsWarmup input.handoff.options.raw := by
      rfl
    have hEffectiveSimple :
        Generated.effectiveBlockGas
            (sourceOp_refundRequest
              (settlementInputOf input
                (sourceOp_substateConstruction
                  (if logApplies input
                      (sameAddress input.handoff.tx.sender input.handoff.recipient) false = true then
                    [sourceOp_transferLogRequest input] else []) false)
                input.gas.preRefundGas
                (if input.handoff.spec.eip8037Enabled = true then
                  stateGasUsed input.handoff.gasAvailable else 0))) =
          Reference.effectiveBlockGas
            (Reference.gasView
              (Reference.settlement
                (Reference.settlementInputOf (mapInput input)
                  (mapSubstate
                    (sourceOp_substateConstruction
                      (if logApplies input
                          (sameAddress input.handoff.tx.sender input.handoff.recipient) false = true then
                        [sourceOp_transferLogRequest input] else []) false))
                  input.gas.preRefundGas
                  (if input.handoff.spec.eip8037Enabled = true then
                    stateGasUsed input.handoff.gasAvailable else 0)))) := by
      simpa [hStateUsedSimple] using
        (effective_block_bridge input
          (sourceOp_substateConstruction
            (if logApplies input
                (sameAddress input.handoff.tx.sender input.handoff.recipient) false = true then
              [sourceOp_transferLogRequest input] else []) false)
          input.gas.preRefundGas
          (if input.handoff.spec.eip8037Enabled = true then
            stateGasUsed input.handoff.gasAvailable else 0))
    have hWriteSelf :
        writeApplies input (sameAddress input.handoff.tx.sender input.handoff.recipient) false =
          Reference.writesGuard (mapInput input)
            ((mapInput input).sender == (mapInput input).recipient) false := by
      simpa [mapInput, Generated.sameAddress] using
        (write_guard_bridge input
          (sameAddress input.handoff.tx.sender input.handoff.recipient) false)
    have hLogSelf :
        logApplies input (sameAddress input.handoff.tx.sender input.handoff.recipient) false =
          Reference.logsGuard (mapInput input)
            ((mapInput input).sender == (mapInput input).recipient) false := by
      simpa [mapInput, Generated.sameAddress] using
        (log_guard_bridge input
          (sameAddress input.handoff.tx.sender input.handoff.recipient) false)
    have hActionsSelf :
        evalSourcePredicate sourceActionsPredicate input
            (sameAddress input.handoff.tx.sender input.handoff.recipient) false =
          (mapInput input).tracingActions :=
      actions_guard_bridge input
        (sameAddress input.handoff.tx.sender input.handoff.recipient) false
    have hCountersSelf :
        evalSourcePredicate sourceCountersPredicate input
            (sameAddress input.handoff.tx.sender input.handoff.recipient) false =
          (!((mapInput input).skipValidation) && !((mapInput input).parallel)) :=
      counters_guard_bridge input
        (sameAddress input.handoff.tx.sender input.handoff.recipient) false
    have hReceiptSelf :
        evalSourcePredicate sourceReceiptPredicate input
            (sameAddress input.handoff.tx.sender input.handoff.recipient) false =
          (mapInput input).tracingReceipt :=
      receipt_guard_bridge input
        (sameAddress input.handoff.tx.sender input.handoff.recipient) false
    have hWritePredicate :
        evalSourcePredicate sourceRecipientPredicate input
            (sameAddress input.handoff.tx.sender input.handoff.recipient) false =
          Reference.writesGuard (mapInput input)
            ((mapInput input).sender == (mapInput input).recipient) false := by
      simpa [Generated.writeApplies] using hWriteSelf
    have hLogPredicate :
        evalSourcePredicate sourceLogPredicate input
            (sameAddress input.handoff.tx.sender input.handoff.recipient) false =
          Reference.logsGuard (mapInput input)
            ((mapInput input).sender == (mapInput input).recipient) false := by
      simpa [Generated.logApplies] using hLogSelf
    apply result_ext
    · simp only [mapComplete, Generated.run, Reference.run]
      rw [hCharged, hReferenceCharged]
      simp [hOog, mapGasState]
    · simp only [mapComplete, Generated.run, Reference.run]
      simp only [hCharged, hReferenceCharged, hOog, hPreRefundGenerated,
        hPreRefundReference, hSubstate]
      try rw [hPreRefundReferenceState]
      simp [mapStateCharge]
      try simp only [sourceOp_clearExecutionGas]
      try rw [hPreRefundState]
      try rw [hPreRefundReferenceSimple]
      try rw [← hSubstateMap]
      calc
        _ = _ := settlement_spent_bridge input
          (sourceOp_substateConstruction
            (if logApplies input
                (sameAddress input.handoff.tx.sender input.handoff.recipient) false = true then
              [sourceOp_transferLogRequest input] else []) false)
          input.gas.preRefundGas
          (if input.handoff.spec.eip8037Enabled = true then
            stateGasUsed input.handoff.gasAvailable else 0)
        _ = _ := by
          simpa [Reference.gasState, mapInput] using hPreRefundReferenceSimple
    · simp only [mapComplete, Generated.run, Reference.run]
      simp only [hCharged, hReferenceCharged, hOog, hPreRefundGenerated,
        hPreRefundReference]
      try rw [hPreRefundReferenceState]
      simp [mapStateCharge]
      exact
        hPreRefundState.trans hPreRefundReferenceSimple.symm
    · simp only [mapComplete, Generated.run, Reference.run]
      simp only [hCharged, hReferenceCharged, hOog, hPreRefundGenerated,
        hPreRefundReference]
      try rw [hPreRefundReferenceState]
      simp [mapStateCharge]
      try simp only [sourceOp_clearExecutionGas]
      try rw [hPreRefundState]
      try rw [hPreRefundReferenceSimple]
      try rw [← hSubstateMap]
      refine Eq.trans ?_ (header_result_bridge input
        (sameAddress input.handoff.tx.sender input.handoff.recipient) false
        (sourceOp_substateConstruction
          (if logApplies input
              (sameAddress input.handoff.tx.sender input.handoff.recipient) false = true then
            [sourceOp_transferLogRequest input] else []) false)
        (Reference.preRefundFor (mapInput input)
          input.handoff.gasAvailable.execution input.handoff.gasAvailable.stateReservoir)
        (if input.handoff.spec.eip8037Enabled = true then
          stateGasUsed input.handoff.gasAvailable else 0))
       simp [hPreRefundReferenceSimple, hPreRefundReferenceState, hStateUsedSimple, Reference.gasState,
        Generated.sourceOp_substateConstruction, Reference.settlementInputOf,
        mapSubstate, mapInput, Generated.combineBlockGas]
    · simp only [mapComplete, Generated.run, Reference.run]
      simp only [hCharged, hReferenceCharged, hOog, hPreRefundGenerated,
        hPreRefundReference]
      try rw [hPreRefundReferenceState]
      try simp only [if_false]
      try rw [hPreRefundState]
      try rw [hPreRefundReferenceSimple]
      try rw [← hSubstateSelf]
      simp only [Generated.sourceOp_finalizeRequest]
      rw [hWarmupBridge]
      split <;> rename_i warmupCase
      · simp [warmupCase, mapInput, Reference.settlementInputOf,
          Reference.effectiveBlockGas, Reference.gasView, Reference.gasState, mapSubstate,
          Generated.sourceOp_substateConstruction, Generated.clearExecutionGas,
          Reference.clearExecution, mapStateCharge, hStateUsedSimple]
      · simpa [warmupCase, hPreRefundReferenceSimple, hPreRefundReferenceState, hStateUsedSimple,
          mapInput, Reference.settlementInputOf, Reference.effectiveBlockGas,
          Reference.gasView, Reference.gasState, mapSubstate,
          Generated.sourceOp_substateConstruction, Generated.clearExecutionGas,
          Reference.clearExecution, mapStateCharge]
          using hEffectiveSimple
    · simp only [mapComplete, Generated.run, Reference.run]
      simp only [hCharged, hReferenceCharged, hOog, hPreRefundGenerated,
        hPreRefundReference]
      try rw [hPreRefundReferenceState]
      try simp only [if_false]
      try rw [hPreRefundState]
      try rw [hPreRefundReferenceSimple]
      try rw [← hSubstateSelf]
      simp only [Generated.sourceOp_finalizeRequest]
      rw [hWarmupBridge]
      simp only [mapInput]
      split <;> rename_i warmupCase
      · simp [warmupCase, Reference.settlementInputOf,
          Reference.effectiveBlockGas, Reference.gasView, Reference.gasState, mapSubstate,
          Generated.sourceOp_substateConstruction, Generated.clearExecutionGas,
          Reference.clearExecution, mapStateCharge, hStateUsedSimple]
      · rw [settlement_spent_bridge input
          (sourceOp_substateConstruction
            (if logApplies input
                (sameAddress input.handoff.tx.sender input.handoff.recipient) false = true then
              [sourceOp_transferLogRequest input] else []) false)
          input.gas.preRefundGas
          (if input.handoff.spec.eip8037Enabled = true then
            stateGasUsed input.handoff.gasAvailable else 0)]
        simp [warmupCase, hPreRefundReferenceSimple, hPreRefundReferenceState, hStateUsedSimple,
          Reference.settlementInputOf, Reference.effectiveBlockGas, Reference.gasView,
          Reference.gasState, mapSubstate, Generated.sourceOp_substateConstruction,
          Generated.clearExecutionGas, Reference.clearExecution, mapStateCharge]
    · simp only [mapComplete, Generated.run, Reference.run]
      simp only [hCharged, hReferenceCharged, hOog, hPreRefundGenerated,
        hPreRefundReference]
      try rw [hPreRefundReferenceState]
      simpa only [hStateUsedSimple] using
        (post_execution_gas_bridge input
          (sameAddress input.handoff.tx.sender input.handoff.recipient) false
          (sourceOp_substateConstruction
            (if logApplies input
                (sameAddress input.handoff.tx.sender input.handoff.recipient) false = true then
              [sourceOp_transferLogRequest input] else []) false)
          input.gas.preRefundGas
          (if input.handoff.spec.eip8037Enabled = true then
            stateGasUsed input.handoff.gasAvailable else 0))
    · simp only [mapComplete, Generated.run, Reference.run]
      simp only [hCharged, hReferenceCharged, hOog, hPreRefundGenerated,
        hPreRefundReference]
      try rw [hPreRefundReferenceState]
      simpa only [hStateUsedSimple] using
        (post_state_gas_bridge input
          (sameAddress input.handoff.tx.sender input.handoff.recipient) false
          (sourceOp_substateConstruction
            (if logApplies input
                (sameAddress input.handoff.tx.sender input.handoff.recipient) false = true then
              [sourceOp_transferLogRequest input] else []) false)
          input.gas.preRefundGas
          (if input.handoff.spec.eip8037Enabled = true then
            stateGasUsed input.handoff.gasAvailable else 0))
    · simp only [mapComplete, Generated.run, Reference.run]
      simp only [hCharged, hReferenceCharged, hOog, hPreRefundGenerated,
        hPreRefundReference]
      try rw [hPreRefundReferenceState]
      simpa only [hStateUsedSimple] using
        (receipt_settlement_bridge input
          (sourceOp_substateConstruction
            (if logApplies input
               (sameAddress input.handoff.tx.sender input.handoff.recipient) false = true then
             [sourceOp_transferLogRequest input] else []) false)
          input.gas.preRefundGas
          (if input.handoff.spec.eip8037Enabled = true then
            stateGasUsed input.handoff.gasAvailable else 0)
          false)
    · simp only [mapComplete, Generated.run, Reference.run]
      simp only [hCharged, hReferenceCharged, hOog, hPreRefundGenerated,
        hPreRefundReference]
      try rw [hPreRefundReferenceState]
      try simp only [Bool.not_true]
      simp only [List.map_append]
      rw [map_if_gas_charge_prop]
      rw [map_pay_value_effect input
        (writeApplies input (sameAddress input.handoff.tx.sender input.handoff.recipient) false)]
      rw [map_add_recipient_effect input
        (writeApplies input (sameAddress input.handoff.tx.sender input.handoff.recipient) false)]
      rw [map_action_start_effect input
        (sameAddress input.handoff.tx.sender input.handoff.recipient) false
        input.handoff.gasAvailable]
      simp only [Generated.gasRemaining]
      rw [map_action_terminal_prop]
      rw [map_if_clear_prop]
      rw [map_if_log_prop]
      rw [map_if_action_prop]
      rw [map_substate_effect]
      rw [hSubstateMap]
      rw [map_settlement_effect]
      rw [map_if_world_prop_effect]
      rw [map_access_effect_ordered input]
      rw [map_if_header_prop]
      rw [map_fee_effect]
      rw [map_if_transaction_prop]
      rw [map_final_world_effect]
      rw [map_root_effect]
      rw [map_receipt_effect]
      simp only [Generated.sourceOp_incrementEmptyCalls, List.map_cons, List.map_nil]
      simp only [charge_condition_bridge input,
        write_condition_bridge input false, log_condition_bridge input false,
        actions_condition_bridge input false, counters_condition_bridge input false,
        receipt_condition_bridge input false, access_tracing_bridge input,
        fees_tracing_bridge input, warmup_condition_bridge input]
      simp only [hWriteSelf, hLogSelf, Bool.not_false]
      try rw [header_result_bridge input
        (sameAddress input.handoff.tx.sender input.handoff.recipient) false
        (sourceOp_substateConstruction
          (if logApplies input
              (sameAddress input.handoff.tx.sender input.handoff.recipient) false = true then
            [sourceOp_transferLogRequest input] else []) false)
        input.gas.preRefundGas
        (if input.handoff.spec.eip8037Enabled = true then
          stateGasUsed input.handoff.gasAvailable else 0)]
      try rw [hEffectiveSimple]
      try rw [settlement_spent_bridge input
        (sourceOp_substateConstruction
          (if logApplies input
              (sameAddress input.handoff.tx.sender input.handoff.recipient) false = true then
            [sourceOp_transferLogRequest input] else []) false)
        input.gas.preRefundGas
        (if input.handoff.spec.eip8037Enabled = true then
          stateGasUsed input.handoff.gasAvailable else 0)]
      apply journal_layout
        (charge := Reference.chargeGuard (mapInput input)
            ((mapInput input).sender == (mapInput input).recipient))
          (write := Reference.writesGuard (mapInput input)
            ((mapInput input).sender == (mapInput input).recipient) false)
          (logs := Reference.logsGuard (mapInput input)
            ((mapInput input).sender == (mapInput input).recipient) false)
          (actions := (mapInput input).tracingActions)
          (code := (mapInput input).tracingCode)
          (oog := false)
          (warmup := Reference.optionIsWarmup input.handoff.options.raw)
          (receipt := (mapInput input).tracingReceipt)
          (tracingLogs := (mapInput input).tracingLogs)
          (value := (mapInput input).value)
          (gasCharge := _)
          (subtract := _)
          (add := _)
          (actionStart := _)
          (actionCode := _)
          (clear := _)
          (logEvent := _)
          (logTrace := _)
          (substate := _)
          (terminalError := _)
          (terminalEnd := _)
          (settlement := _)
          (refundEvents := _)
          (accessEvents := _)
          (headerEvents := _)
          (feeEvents := _)
          (transaction := _)
          (finalWorld := _)
          (rootEvents := _)
          (receiptFailure := _)
          (receiptSuccess := _)

theorem receipt_continuation_preserved (input : CompletionInput) (_h : AdapterCoherent input)
    (_hNormalReturn : normalReturnDomain input) :
    (mapComplete (Generated.run input)).receipt =
      mapReceipt (Generated.run input).receiptContinuation := by
  simp [mapComplete]

end SimpleTransferCompletionExtractor.Refinement
