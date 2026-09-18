-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import EvmTransactionPreparationExtractor.Reference.EvmTransactionPreparationReference
import OrdinaryTransactionRefundAdapterExtractor.Specification.OrdinaryTransactionRefund
import ReceiptTerminalFoldExtractor.Specification.ReceiptTerminalFold

namespace OrdinaryEvmCompletionExtractor.Specification.OrdinaryEvmCompletion

namespace P
export EvmTransactionPreparationExtractor.Reference
  (Input Result VmInput Gas Snapshot AccessObservation run isCreateTx)
end P
namespace F
export OrdinaryTransactionRefundAdapterExtractor.Specification.OrdinaryTransactionRefund
  (GasState Constants Input Result evaluate uint256Modulus FitsUInt256)
end F
namespace R
export ReceiptTerminalFoldExtractor.Specification.ReceiptTerminalFold
  (BytesOracle LogOracle ErrorOracle EvmExceptionOracle HashOracle AddressOracle PriceOracle
   Status TransactionResult Gas TransactionInput BlockInput GasTotals State FinalizeInput
   FinalizationObservation Event previousTotals effectiveBlockGas finalizeTransaction emptyBytes)
end R

inductive HaltReason where
  | badInstruction | stackOverflow | stackUnderflow | outOfGas | invalidJumpDestination
  | accessViolation | staticCallViolation | precompileFailure | transactionCollision
  | notEnoughBalance | other | invalidCode
  deriving DecidableEq, Repr

/-- A post-ExecuteTransaction observation: REVERT state-gas repayment has already happened. -/
inductive VmTerminal where
  | success (output : R.BytesOracle) (refund : Int) (logs : List R.LogOracle)
  | reverted (output : R.BytesOracle) (refund : Int) (error : R.ErrorOracle)
  | exceptional (reason : HaltReason) (error : R.ErrorOracle)
      (detail : Option R.ErrorOracle)
  deriving DecidableEq, Repr

structure VmObservation where
  frameEntry : P.VmInput
  postGas : F.GasState
  terminal : VmTerminal
  destroyed : List R.AddressOracle
  accessAfter : P.AccessObservation
  shouldRestoreRipemdTouch : Bool
  opCodeCount : Int
  deriving DecidableEq, Repr

structure TailSpec where
  eip3529 : Bool
  eip7778 : Bool
  eip658 : Bool
  eip1559 : Bool
  eip4844FeeCollector : Bool
  destroyRefund : Nat
  feeCollector : Option String

structure TailTransaction where
  gasLimit : Nat
  maxFeePerGas : Nat
  maxPriorityFeePerGas : Nat
  isFree : Bool
  supportsBlobs : Bool
  txType : Nat
  hash : Option R.HashOracle

structure ProcessorCounters where
  executionGas : Nat
  stateGas : Nat
  headerGasUsed : Nat
  deriving DecidableEq, Repr

structure TailInput where
  vm : VmObservation
  intrinsicFloorGas : F.GasState
  spec : TailSpec
  transaction : TailTransaction
  block : R.BlockInput
  gasBeneficiary : String
  premiumPerGas : Nat
  blobBaseFee : Nat
  options : Nat
  processorParallel : Bool
  processorBefore : ProcessorCounters
  receiptBefore : R.State
  address : String → R.AddressOracle
  diagnosticEffectivePrice : R.PriceOracle
  revertException : R.EvmExceptionOracle
  haltException : HaltReason → R.EvmExceptionOracle
  recalculatedStateRoot : R.HashOracle
  tracingState : Bool
  tracingFees : Bool
  nestedReceiptTracer : Bool
  currentTracerIsTracingReceipt : Bool

structure Input where
  preparation : P.Input
  tail : TailInput

def refundConstants : F.Constants :=
  { executionCap := 16777216, createStateCost := 183600, newAccountCost := 25000,
    perAuthorizationCost := 12500, legacyRefundQuotient := 2, eip3529RefundQuotient := 5 }

def prepared (i : Input) : P.Result := P.run i.preparation

def preparationGas (gas : P.Gas) : F.GasState :=
  { value := gas.value.toNat, stateReservoir := gas.stateReservoir,
    stateGasUsed := gas.stateGasUsed, stateGasSpill := gas.stateGasSpill,
    stateGasSpillRefunded := gas.stateGasSpillRefunded }

def terminalStatus : VmTerminal → R.Status
  | .success .. => .success
  | .reverted .. | .exceptional .. => .failure

def shouldRevert : VmTerminal → Bool
  | .reverted .. => true
  | _ => false

def isError : VmTerminal → Bool
  | .exceptional .. => true
  | _ => false

def refundCounter : VmTerminal → Int
  | .success _ refund _ | .reverted _ refund _ => refund
  | .exceptional .. => 0

def rollbackRequired : VmTerminal → Bool
  | .success .. => false
  | _ => true

def refundInput (i : Input) : F.Input :=
  { entry := .ordinaryRefund,
    transactionGasLimit := i.tail.transaction.gasLimit,
    gasPrice := i.preparation.handoff.context.opcodeGasPrice,
    maxFeePerGas := i.tail.transaction.maxFeePerGas,
    maxPriorityFeePerGas := i.tail.transaction.maxPriorityFeePerGas,
    skipValidation := false, isContractCreation := false,
    isEip8037Enabled := i.preparation.handoff.spec.eip8037,
    isEip3529Enabled := i.tail.spec.eip3529, isEip7778Enabled := i.tail.spec.eip7778,
    isError := isError i.tail.vm.terminal, shouldRevert := shouldRevert i.tail.vm.terminal,
    refundCounter := refundCounter i.tail.vm.terminal, destroyCount := 0,
    destroyRefund := i.tail.spec.destroyRefund,
    codeInsertRefundCount := (prepared i).delegationRefunds,
    incomingGas := i.tail.vm.postGas,
    intrinsicStandard := preparationGas (prepared i).executionIntrinsicGasStandard,
    floorGas := i.tail.intrinsicFloorGas,
    postIntrinsicStateReservoir := (prepared i).postIntrinsicStateReservoir,
    topLevelCreateStateGasCharged := false }

def refundResult (i : Input) : F.Result := F.evaluate refundConstants (refundInput i)

def consumedGas (i : Input) : R.Gas :=
  let gas := (refundResult i).consumed
  { spentGas := gas.spentGas, operationGas := gas.operationGas, blockGas := gas.blockGas,
    blockStateGas := gas.blockStateGas, maxUsedGas := gas.maxUsedGas, gasRefund := gas.gasRefund }

def processorAfter (i : Input) : ProcessorCounters :=
  let gas := consumedGas i
  let before := i.tail.processorBefore
  if i.preparation.handoff.spec.eip8037 then
    let executionGas := before.executionGas + R.effectiveBlockGas gas
    let stateGas := before.stateGas + gas.blockStateGas
    { executionGas, stateGas, headerGasUsed := max executionGas stateGas }
  else { before with headerGasUsed := before.headerGasUsed + R.effectiveBlockGas gas }

structure Fees where
  beneficiaryAmount : Nat
  baseFeeAmount : Nat
  collectorAmount : Nat
  collectorCredit : Option (R.AddressOracle × Nat)
  reportedBurntAmount : Nat
  deriving DecidableEq, Repr

def fees (i : Input) : Fees :=
  let spent := (consumedGas i).spentGas
  let effectiveBase := min i.tail.block.baseFeePerGas i.preparation.handoff.context.opcodeGasPrice
  let base := if i.tail.transaction.isFree then 0 else
    (effectiveBase * spent) % F.uint256Modulus
  let collected := ((if i.tail.spec.eip1559 then base else 0) +
    (if i.tail.transaction.supportsBlobs && i.tail.spec.eip4844FeeCollector then
      i.tail.blobBaseFee else 0)) % F.uint256Modulus
  { beneficiaryAmount := (i.tail.premiumPerGas * spent) % F.uint256Modulus,
    baseFeeAmount := base, collectorAmount := collected,
    collectorCredit := i.tail.spec.feeCollector.bind fun collector =>
      if collected == 0 then none else some (i.tail.address collector, collected),
    reportedBurntAmount := (base + i.tail.blobBaseFee) % F.uint256Modulus }

def receiptTransaction (i : Input) : R.TransactionInput :=
  { txType := i.tail.transaction.txType,
    to := i.preparation.handoff.tx.recipient.map i.tail.address,
    sender := some (i.tail.address i.preparation.handoff.tx.sender),
    txHash := i.tail.transaction.hash, effectiveGasPrice := i.tail.diagnosticEffectivePrice }

def recipient (i : Input) : R.AddressOracle :=
  i.tail.address (prepared i).environment.executingAccount

def stateRoot (i : Input) : Option R.HashOracle :=
  if i.tail.spec.eip658 then none else some i.tail.recalculatedStateRoot

def finalizeInput (i : Input) : R.FinalizeInput :=
  match i.tail.vm.terminal with
  | .success output _ logs =>
    { status := .success, shouldRevert := false, output, substateError := none,
      vmError := { id := 0 }, evmExceptionType := none, substateResultError := none,
      logs, stateRoot := stateRoot i }
  | .reverted output _ error =>
    { status := .failure, shouldRevert := true, output, substateError := some error,
      vmError := error, evmExceptionType := some i.tail.revertException,
      substateResultError := none, logs := [], stateRoot := stateRoot i }
  | .exceptional reason error detail =>
    { status := .failure, shouldRevert := false, output := R.emptyBytes,
      substateError := some error, vmError := error, evmExceptionType := some (i.tail.haltException reason),
      substateResultError := detail, logs := [], stateRoot := stateRoot i }

def receiptEntry (i : Input) : R.State :=
  { i.tail.receiptBefore with headerGasUsed := (processorAfter i).headerGasUsed }

def receiptResult (i : Input) : R.FinalizationObservation :=
  R.finalizeTransaction (receiptEntry i) i.tail.block (receiptTransaction i) (recipient i)
    (consumedGas i) (finalizeInput i) i.tail.nestedReceiptTracer i.tail.currentTracerIsTracingReceipt

structure GasCopies where
  outerCaller : F.GasState
  framePostVm : F.GasState
  callLocal : F.GasState
  refundWorking : F.GasState
  deriving DecidableEq, Repr

inductive Event where
  | vmReturned | opCodeMetrics (count : Nat) | flushMetrics
  | accessReport (access : P.AccessObservation)
  | restore (snapshot : P.Snapshot) | restoreRipemdTouch (requested : Bool)
  | frameDisposed | refundCall
  | senderCredit (recipient : R.AddressOracle) (amount : Nat)
  | processorHeaderWrite (gas : Nat)
  | beneficiaryCredit (recipient : R.AddressOracle) (amount : Nat)
  | collectorCredit (recipient : R.AddressOracle) (amount : Nat)
  | reportFees (premium burnt : Nat)
  | transactionGasWrite (blockGas spentGas : Nat)
  | commit (roots tracingState : Bool) | recalculateStateRoot
  | receipt (event : R.Event)
  | environmentDisposed | accessTrackerDisposed | returned
  deriving DecidableEq, Repr

def events (i : Input) : List Event :=
  [.vmReturned, .opCodeMetrics i.tail.vm.opCodeCount.toNat, .flushMetrics] ++
  (if i.tail.vm.accessAfter.tracing then [.accessReport i.tail.vm.accessAfter] else []) ++
  (if rollbackRequired i.tail.vm.terminal then
    [.restore i.tail.vm.frameEntry.snapshot, .restoreRipemdTouch i.tail.vm.shouldRestoreRipemdTouch] else []) ++
  [.frameDisposed, .refundCall] ++
  ((refundResult i).senderCredit.toList.map fun amount =>
    .senderCredit (i.tail.address i.preparation.handoff.tx.sender) amount) ++
  [.processorHeaderWrite (processorAfter i).headerGasUsed,
   .beneficiaryCredit (i.tail.address i.tail.gasBeneficiary) (fees i).beneficiaryAmount] ++
  ((fees i).collectorCredit.toList.map fun credit => .collectorCredit credit.1 credit.2) ++
  (if i.tail.tracingFees then [.reportFees (fees i).beneficiaryAmount (fees i).reportedBurntAmount] else []) ++
  [.transactionGasWrite (R.effectiveBlockGas (consumedGas i)) (consumedGas i).spentGas,
   .commit (!i.tail.spec.eip658) i.tail.tracingState] ++
  (if i.tail.spec.eip658 then [] else [.recalculateStateRoot]) ++
  ((receiptResult i).trace.events.map Event.receipt) ++
  [.environmentDisposed, .accessTrackerDisposed, .returned]

structure Result where
  preparation : P.Result
  status : R.Status
  transactionExecuted : Bool
  rollbackSnapshot : Option P.Snapshot
  gasCopies : GasCopies
  refund : F.Result
  processor : ProcessorCounters
  feeObservation : Fees
  receipt : R.FinalizationObservation
  events : List Event

def evaluate (i : Input) : Result :=
  { preparation := prepared i, status := terminalStatus i.tail.vm.terminal,
    transactionExecuted := true,
    rollbackSnapshot := if rollbackRequired i.tail.vm.terminal then some i.tail.vm.frameEntry.snapshot else none,
    gasCopies :=
      { outerCaller := preparationGas (prepared i).gas
        framePostVm := i.tail.vm.postGas
        callLocal := i.tail.vm.postGas
        refundWorking := (refundResult i).workingGas },
    refund := refundResult i, processor := processorAfter i, feeObservation := fees i,
    receipt := receiptResult i, events := events i }

/-- Entry identities and restrictions, never a completed-output premise. -/
structure Boundary (i : Input) : Prop where
  finalPreparation : (prepared i).status = .vmBoundary
  frame : (prepared i).vmInput = some i.tail.vm.frameEntry
  nonCreate : P.isCreateTx i.preparation = false
  transactionFrame : i.tail.vm.frameEntry.executionType = "TRANSACTION"
  presentCode : i.tail.vm.frameEntry.environment.code.isNull = false
  gasIdentity : i.tail.vm.frameEntry.gas = (prepared i).gas
  environmentIdentity : i.tail.vm.frameEntry.environment = (prepared i).environment
  accessIdentity : i.tail.vm.frameEntry.access = (prepared i).access
  snapshotIdentity : i.tail.vm.frameEntry.snapshot = (prepared i).topLevelSnapshot
  eip8037 : i.preparation.handoff.spec.eip8037 = true
  eip8038 : i.preparation.handoff.spec.eip8038 = true
  eip3529 : i.tail.spec.eip3529 = true
  eip7778 : i.tail.spec.eip7778 = true
  eip658 : i.tail.spec.eip658 = true
  eip1559 : i.tail.spec.eip1559 = true
  commitOnly : i.tail.options = 1
  noWarmup : i.preparation.handoff.options.warmup = false
  noSkipValidation : i.preparation.handoff.options.skipValidation = false
  sequentialProcessor : i.tail.processorParallel = false
  sequentialReceipts : i.tail.receiptBefore.parallel = false
  emptyDestroy : i.tail.vm.destroyed = []
  accessTracing : i.tail.vm.accessAfter.tracing = i.tail.vm.frameEntry.access.tracing
  receiptHistory : i.tail.receiptBefore.receipts.length = i.tail.receiptBefore.gasHistory.length
  receiptIndex : i.tail.receiptBefore.currentIndex = i.tail.receiptBefore.receipts.length

/-- Production provenance and hook contracts are not discharged by the pure definitions. -/
structure ExternalPredicates where
  preparationProvenance : P.Input → Prop
  actualVmReturnAfterTopLevelRevertRefund : P.VmInput → VmObservation → Prop
  topLevelFrameInitialization : (frame : P.VmInput) → (initialStateGasUsed : Int) →
    (isTopLevel isStatic newAccountCharged isCreateStateGasCharged : Bool) → Prop
  exceptionEnumProjection : (HaltReason → R.EvmExceptionOracle) → R.EvmExceptionOracle → Prop
  originalIntrinsicFloorProjection : Input → Prop
  executionCallerFlags : (input : Input) → (restore commit : Bool) → Prop
  sameTransactionSpecHeaderTracer : Input → Prop
  baseReceiptTracerWithReceiptTracingEnabled : Input → Prop
  normalNonReentrantHooksAndCleanup : Input → Prop
  observationNoninterference : Input → Prop
  addressBytesErrorHashAndPriceRepresentation : Input → Prop
  uint256PrimitiveSemantics : Input → Prop

structure ExternalAssumptions (predicates : ExternalPredicates) (i : Input) : Prop where
  preparation : predicates.preparationProvenance i.preparation
  vm : predicates.actualVmReturnAfterTopLevelRevertRefund i.tail.vm.frameEntry i.tail.vm
  frameInitialization : predicates.topLevelFrameInitialization i.tail.vm.frameEntry
    i.tail.vm.frameEntry.gas.stateGasUsed true false false false
  exceptions : predicates.exceptionEnumProjection i.tail.haltException i.tail.revertException
  intrinsicFloor : predicates.originalIntrinsicFloorProjection i
  callerFlags : predicates.executionCallerFlags i false true
  identities : predicates.sameTransactionSpecHeaderTracer i
  receiptTracer : predicates.baseReceiptTracerWithReceiptTracingEnabled i
  normality : predicates.normalNonReentrantHooksAndCleanup i
  noninterference : predicates.observationNoninterference i
  representation : predicates.addressBytesErrorHashAndPriceRepresentation i
  uint256 : predicates.uint256PrimitiveSemantics i

def NonnegativeGas (gas : F.GasState) : Prop :=
  0 ≤ gas.stateReservoir ∧ 0 ≤ gas.stateGasUsed ∧
    0 ≤ gas.stateGasSpill ∧ 0 ≤ gas.stateGasSpillRefunded

def PreparationGasRepresentable (gas : P.Gas) : Prop :=
  0 ≤ gas.value ∧ (preparationGas gas).Valid ∧ NonnegativeGas (preparationGas gas)

/-- Bounds apply to computed preparation outputs as well as raw inputs. -/
structure Representation (i : Input) : Prop where
  outerGas : PreparationGasRepresentable (prepared i).gas
  frameGas : PreparationGasRepresentable i.tail.vm.frameEntry.gas
  intrinsicStandard : PreparationGasRepresentable (prepared i).executionIntrinsicGasStandard
  refundInput : (refundInput i).Valid
  postVmGas : NonnegativeGas i.tail.vm.postGas
  floorGas : NonnegativeGas i.tail.intrinsicFloorGas
  postIntrinsicReservoir : 0 ≤ (prepared i).postIntrinsicStateReservoir
  premium : F.FitsUInt256 i.tail.premiumPerGas
  blobBaseFee : F.FitsUInt256 i.tail.blobBaseFee
  baseFee : F.FitsUInt256 i.tail.block.baseFeePerGas
  opCodeCount : 0 ≤ i.tail.vm.opCodeCount ∧ i.tail.vm.opCodeCount ≤ 2147483647

def CounterRanges (i : Input) : Prop :=
  let gas := consumedGas i
  let previous := R.previousTotals i.tail.receiptBefore.gasHistory
  let bound := Eip803x.TransactionSettlement.uint64Max
  i.tail.processorBefore.executionGas ≤ bound ∧ i.tail.processorBefore.stateGas ≤ bound ∧
  i.tail.processorBefore.headerGasUsed ≤ bound ∧
  i.tail.processorBefore.executionGas + R.effectiveBlockGas gas ≤ bound ∧
  i.tail.processorBefore.stateGas + gas.blockStateGas ≤ bound ∧
  i.tail.processorBefore.headerGasUsed + R.effectiveBlockGas gas ≤ bound ∧
  previous.1 + R.effectiveBlockGas gas ≤ bound ∧ previous.2 + gas.blockStateGas ≤ bound ∧
  i.tail.receiptBefore.cumulativeReceiptGas + gas.spentGas ≤ bound

theorem counter_ranges_receipt_sums (i : Input) (ranges : CounterRanges i) :
    (R.previousTotals i.tail.receiptBefore.gasHistory).1 + R.effectiveBlockGas (consumedGas i) ≤
        Eip803x.TransactionSettlement.uint64Max ∧
    (R.previousTotals i.tail.receiptBefore.gasHistory).2 + (consumedGas i).blockStateGas ≤
        Eip803x.TransactionSettlement.uint64Max ∧
    i.tail.receiptBefore.cumulativeReceiptGas + (consumedGas i).spentGas ≤
        Eip803x.TransactionSettlement.uint64Max := by
  rcases ranges with ⟨_, _, _, _, _, _, execution, state, paid⟩
  exact ⟨execution, state, paid⟩

theorem status_is_derived (i : Input) :
    (evaluate i).status = terminalStatus i.tail.vm.terminal := rfl

theorem transaction_executed (i : Input) : (evaluate i).transactionExecuted = true := rfl

theorem refund_is_computed (i : Input) :
    (evaluate i).refund = F.evaluate refundConstants (refundInput i) := rfl

theorem receipt_is_computed (i : Input) : (evaluate i).receipt = receiptResult i := rfl

theorem post_vm_gas_not_refunded_twice (i : Input) :
    (refundInput i).incomingGas = i.tail.vm.postGas := rfl

theorem refund_uses_original_floor (i : Input) :
    (refundInput i).floorGas = i.tail.intrinsicFloorGas := rfl

theorem non_create_refund_flags (i : Input) :
    (refundInput i).entry = .ordinaryRefund ∧ (refundInput i).isContractCreation = false ∧
    (refundInput i).topLevelCreateStateGasCharged = false := ⟨rfl, rfl, rfl⟩

theorem gas_views_remain_distinct (i : Input) :
    (evaluate i).gasCopies.outerCaller = preparationGas (prepared i).gas ∧
    (evaluate i).gasCopies.framePostVm = i.tail.vm.postGas ∧
    (evaluate i).gasCopies.callLocal = i.tail.vm.postGas ∧
    (evaluate i).gasCopies.refundWorking = (refundResult i).workingGas := ⟨rfl, rfl, rfl, rfl⟩

theorem successful_finalize_has_no_exception (i : Input)
    (success : (finalizeInput i).status = .success) :
    (finalizeInput i).evmExceptionType = none := by
  cases terminal : i.tail.vm.terminal <;> simp_all [finalizeInput]

end OrdinaryEvmCompletionExtractor.Specification.OrdinaryEvmCompletion
