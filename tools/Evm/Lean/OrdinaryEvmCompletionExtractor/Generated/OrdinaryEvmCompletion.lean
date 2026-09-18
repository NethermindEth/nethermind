-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import OrdinaryEvmCompletionExtractor.Specification.OrdinaryEvmCompletion

namespace OrdinaryEvmCompletionExtractor.Generated.OrdinaryEvmCompletion

open OrdinaryEvmCompletionExtractor.Specification.OrdinaryEvmCompletion

/-- Conditional restricted-source continuation; upstream preparation remains a model stage. -/
def sourceClosure : String := "50d54c1db38d1045465503a3fc40faee3642f0fbd390f68339843f864b4ad148"
def sourceIr : String := "7b8b6b3ae5964c1e4c0740550e7d18756473529834cdaf60027fcb3c7de81601"
def compilerAssembly : String := "Nethermind.Evm"
def compilerConfiguration : String := "Release"
def compilerEpoch : String := "1789035784"

def refundConstants : F.Constants :=
  { executionCap := 16777216, createStateCost := 183600, newAccountCost := 25000,
    perAuthorizationCost := 12500, legacyRefundQuotient := 2, eip3529RefundQuotient := 5 }

def prepared (i : Input) : P.Result := P.run i.preparation

def preparationGas (gas : P.Gas) : F.GasState :=
  { value := gas.value.toNat, stateReservoir := gas.stateReservoir,
    stateGasUsed := gas.stateGasUsed, stateGasSpill := gas.stateGasSpill,
    stateGasSpillRefunded := gas.stateGasSpillRefunded }


def shouldRevert : VmTerminal → Bool
  | .reverted .. => true
  | _ => false

def isError : VmTerminal → Bool
  | .exceptional .. => true
  | _ => false

def refundCounter : VmTerminal → Int
  | .success _ refund _ | .reverted _ refund _ => refund
  | .exceptional .. => 0

def rollbackRequired (terminal : VmTerminal) : Bool :=
  (shouldRevert terminal) || (isError terminal)

def terminalStatus (terminal : VmTerminal) : R.Status :=
  if rollbackRequired terminal then .failure else .success


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

def fees (i : Input) : Fees :=
  let spent := (consumedGas i).spentGas
  let effectiveBase := min (i.tail.block.baseFeePerGas) (i.preparation.handoff.context.opcodeGasPrice)
  let base := if i.tail.transaction.isFree then 0 else
    (effectiveBase * spent) % F.uint256Modulus
  let collected := ((if i.tail.spec.eip1559 then base else 0) +
    (if i.tail.transaction.supportsBlobs && i.tail.spec.eip4844FeeCollector then
      i.tail.blobBaseFee else 0)) % F.uint256Modulus
  { beneficiaryAmount := ((i.tail.premiumPerGas) * (spent)) % F.uint256Modulus,
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

end OrdinaryEvmCompletionExtractor.Generated.OrdinaryEvmCompletion
