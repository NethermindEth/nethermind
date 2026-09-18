-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import SimpleTransferCompletionExtractor.Generated.SimpleTransferCompletion
import SimpleTransferCompletionExtractor.Refinement.SimpleTransferCompletionRefinement

namespace SimpleTransferCompletionExtractor.Vectors

open SimpleTransferCompletionExtractor.Generated

def sender : Address := { id := 1 }
def recipient : Address := { id := 2 }
def beneficiary : Address := { id := 3 }
def accessAddress : Address := { id := 4 }
def storageKey : Nat := 9
def duplicateKey : Nat := 10
-- This is an independently written expected value for the generated TransferLog.CreateTransfer
-- projection: the ERC-20 Transfer topic, Address.SystemUser, two bounded address projections,
-- and UInt256.ToBigEndian(value).
def transferSignature : Nat := 0xddf252ad1be2c89b69c2b068fc378daa952ba7f163c4a11628f55a4df523b3ef
def expectedTransferLogVector : TransferLog :=
  { address := { id := 0xfffffffffffffffffffffffffffffffffffffffe }
    topics := [transferSignature, 1, 2]
    data := [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
      0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 7]
    fromAddress := sender
    toAddress := recipient
    amount := 7 }

def baseTransaction : Transaction :=
  { sender
    recipient
    value := 7
    data := []
    accessList := []
    gasLimit := 100000
    isFree := false
    supportsBlobs := false
    maxFeePerGas := 10
    maxPriorityFeePerGas := 2
    blockGasUsed := 0
    spentGas := 0 }

def baseHeader : Header :=
  { gasUsed := 100
    baseFeePerGas := 3
    gasBeneficiary := beneficiary
    stateRoot := some 77 }

def baseSpec : Spec :=
  { eip8037Enabled := true
    eip7708Enabled := false
    eip658Enabled := true
    eip3529Enabled := true
    eip7778Enabled := true
    eip1559Enabled := true
    eip4844FeeCollectorEnabled := false
    useHotAndColdStorage := true
    useTxAccessLists := true
    addCoinbaseToTxAccessList := true
    feeCollector := none
    destroyRefund := 24000
    refundQuotient := 5 }

def baseTracer : Tracer :=
  { isTracingActions := true
    isTracingCode := true
    isTracingLogs := true
    isTracingAccess := true
    isTracingFees := true
    isTracingReceipt := true
    isTracingState := true }

def baseHandoff : Handoff :=
  { tx := baseTransaction
    header := baseHeader
    spec := baseSpec
    tracer := baseTracer
    options := { raw := executionOptionCommit }
    restore := false
    commit := true
    deleteCallerAccount := false
    recipient
    intrinsic := { floorGas := 21000, standardGas := 21000 }
    gasAvailable := { execution := 50000, stateReservoir := 200000, stateGasUsed := 0, stateGasSpill := 0, stateGasSpillRefunded := 0 }
    opcodeGasPrice := 3
    premiumPerGas := 2
    senderReservedGasPayment := 0
    blobBaseFee := 0
    blockCumulativeExecutionGas := 100
    blockCumulativeStateGas := 5
    parallel := false }

def baseInput : CompletionInput :=
  { handoff := baseHandoff
    gas := { newAccountStateCost := sourceNewAccountStateCost, stateCharge := { succeeded := true, gas := { execution := 50000, stateReservoir := 16400, stateGasUsed := sourceNewAccountStateCost, stateGasSpill := 0, stateGasSpillRefunded := 0 } }, preRefundGas := 33600 }
    world := { recipientIsDead := true, senderBalance := 100, stateRootBeforeRecalculate := some 77, stateRootAfterRecalculate := some 88 }
    tracer := { normalReturn := true } }

-- Ordinary successful value transfer to a dead recipient; this exercises the EIP-8037 charge,
-- value movement, fees, block counters, final commit, and all enabled trace observations.
def successfulDeadRecipient : SimpleComplete := Generated.run baseInput

-- Self-send must not issue either PayValue or recipient balance requests.
def selfSend : SimpleComplete :=
  Generated.run { baseInput with
    handoff := { baseHandoff with tx := { baseTransaction with recipient := sender }, recipient := sender }
    gas := { baseInput.gas with preRefundGas := 100000 } }

-- Failed new-account state charge still reports action start before clearing execution gas, then
-- carries OutOfGas through substate, settlement, fee payment, and the receipt continuation.
def newAccountOutOfGas : SimpleComplete :=
  Generated.run { baseInput with
    handoff := { baseHandoff with gasAvailable := { execution := 1, stateReservoir := 1000, stateGasUsed := 0, stateGasSpill := 0, stateGasSpillRefunded := 0 } }
    gas := { (baseInput.gas) with stateCharge := { succeeded := false, gas := { execution := 1, stateReservoir := 1000, stateGasUsed := 0, stateGasSpill := 0, stateGasSpillRefunded := 0 } }, preRefundGas := 99000 } }

-- EIP-7708 transfer log and receipt callback path, with build-up finalization and no state commit.
def transferLogAndBuildUp : SimpleComplete :=
  Generated.run { baseInput with
    handoff := { baseHandoff with
      spec := { baseSpec with eip7708Enabled := true }
      options := { raw := executionOptionBuildUp }
      commit := false }
    gas := { baseInput.gas with preRefundGas := 100000 }
    world := { baseInput.world with recipientIsDead := false } }

-- BuildUp reaping is exact enum equality, so a composite flag must not reap.
def compositeBuildUpFlags : SimpleComplete :=
  Generated.run { baseInput with
    handoff := { baseHandoff with options := { raw := executionOptionBuildUp + executionOptionCommit }, commit := false }
    gas := { baseInput.gas with preRefundGas := 100000 }
    world := { baseInput.world with recipientIsDead := false } }

-- Legacy accounting must write EffectiveBlockGas (SpentGas when both block components are zero),
-- while EIP-7778 keeps its pre-refund block component.
def legacyPre7778EffectiveBlock : SimpleComplete :=
  Generated.run { baseInput with
    handoff := { baseHandoff with
      spec := { baseSpec with eip8037Enabled := false, eip7778Enabled := false }
      gasAvailable := { execution := 50000, stateReservoir := 0, stateGasUsed := 0, stateGasSpill := 0, stateGasSpillRefunded := 0 } }
    gas := { baseInput.gas with newAccountStateCost := sourceNewAccountStateCost, preRefundGas := 50000 }
    world := { baseInput.world with recipientIsDead := false } }

def eip7778EffectiveBlock : SimpleComplete :=
  Generated.run { baseInput with
    handoff := { baseHandoff with spec := { baseSpec with eip8037Enabled := false, eip7778Enabled := true } }
    gas := { baseInput.gas with newAccountStateCost := sourceNewAccountStateCost, preRefundGas := 100000 }
    world := { baseInput.world with recipientIsDead := false } }

-- Access warming follows UseHotAndColdStorage, then access-list entries, coinbase, recipient,
-- and sender. The generated fold removes duplicate addresses and storage cells.
def accessDisabledStorage : SimpleComplete :=
  Generated.run { baseInput with
    handoff := { baseHandoff with
      spec := { baseSpec with useHotAndColdStorage := false }
      tx := { baseTransaction with accessList := [{ address := accessAddress, storageKeys := [storageKey] }] } } }

def accessListOnly : SimpleComplete :=
  Generated.run { baseInput with
    handoff := { baseHandoff with
      spec := { baseSpec with addCoinbaseToTxAccessList := false }
      tx := { baseTransaction with accessList :=
        [{ address := accessAddress, storageKeys := [storageKey, duplicateKey] },
         { address := accessAddress, storageKeys := [storageKey] }] } } }

def accessCoinbaseOnly : SimpleComplete :=
  Generated.run { baseInput with
    handoff := { baseHandoff with
      spec := { baseSpec with useTxAccessLists := false }
      tx := { baseTransaction with accessList := [{ address := accessAddress, storageKeys := [storageKey] }] } } }

def accessDedup : SimpleComplete :=
  Generated.run { baseInput with
    handoff := { baseHandoff with tx := { baseTransaction with accessList :=
      [{ address := beneficiary, storageKeys := [storageKey, storageKey] },
       { address := recipient, storageKeys := [duplicateKey] },
       { address := beneficiary, storageKeys := [storageKey] }] } } }

def receiptRootAfterRecalculate : SimpleComplete :=
  Generated.run { baseInput with
    handoff := { baseHandoff with spec := { baseSpec with eip658Enabled := false }, commit := false, options := { raw := 0 } } }

def receiptRootHiddenWithoutTracing : SimpleComplete :=
  Generated.run { baseInput with
    handoff := { baseHandoff with
      spec := { baseSpec with eip658Enabled := false }
      tracer := { baseTracer with isTracingReceipt := false }
      commit := false
      options := { raw := 0 } } }

def combineStateDominant : Nat := Generated.combineBlockGas 10 20
def combineExecutionDominant : Nat := Generated.combineBlockGas 30 20
def combineEqual : Nat := Generated.combineBlockGas 30 30
def sumVsMaxMutationSentinel : Bool :=
  combineStateDominant == 20 && combineExecutionDominant == 30 && combineEqual == 30

example : combineStateDominant = 20 := by decide
example : combineExecutionDominant = 30 := by decide
example : combineEqual = 30 := by decide
example : sourceNewAccountStateCost = 183600 := by decide
example : newAccountOutOfGas.preRefundGas != 0 := by decide
example : receiptRootAfterRecalculate.receiptContinuation.stateRoot = some 88 := by decide
example : receiptRootHiddenWithoutTracing.receiptContinuation.stateRoot = none := by decide
example : receiptRootAfterRecalculate.effects.contains (.world (.recalculateStateRoot (some 77) (some 88))) := by decide
example : compositeBuildUpFlags.effects.contains (.world .reapEmptyAccounts) = false := by decide
example : transferLogAndBuildUp.effects.contains (.logCreated expectedTransferLogVector) = true := by decide
example : (Generated.accessObservation { baseInput with handoff := { baseHandoff with spec := { baseSpec with useHotAndColdStorage := false } } }).addresses = [] := by decide
example : (Generated.accessObservation { baseInput with handoff := { baseHandoff with tx := { baseTransaction with accessList := [{ address := accessAddress, storageKeys := [storageKey] }] } } }).storageCells.length = 1 := by decide
example : successfulDeadRecipient.preRefundGas = 33600 := by decide
example : legacyPre7778EffectiveBlock.tx.blockGasUsed = legacyPre7778EffectiveBlock.spentGas.spentGas := by decide
example : legacyPre7778EffectiveBlock.tx.blockGasUsed = 50000 := by decide
example : legacyPre7778EffectiveBlock.blockCumulativeExecutionGas = baseHandoff.blockCumulativeExecutionGas := by decide
example : legacyPre7778EffectiveBlock.blockCumulativeStateGas = baseHandoff.blockCumulativeStateGas := by decide
example : eip7778EffectiveBlock.tx.blockGasUsed = eip7778EffectiveBlock.spentGas.blockGas := by decide
example : (Generated.accessObservation { baseInput with handoff := { baseHandoff with spec := { baseSpec with addCoinbaseToTxAccessList := false } } }).addresses = [recipient, sender] := by decide
example : (Generated.accessObservation { baseInput with handoff := { baseHandoff with spec := { baseSpec with addCoinbaseToTxAccessList := false }, tx := { baseTransaction with accessList :=
  [{ address := accessAddress, storageKeys := [storageKey, duplicateKey] }, { address := accessAddress, storageKeys := [storageKey] }] } } }).storageCells =
  [{ address := accessAddress, key := storageKey }, { address := accessAddress, key := duplicateKey }] := by decide
example : (Generated.accessObservation { baseInput with handoff := { baseHandoff with spec := { baseSpec with useTxAccessLists := false } } }).addresses = [beneficiary, recipient, sender] := by decide
example : (Generated.sourceOp_transferLogRequest baseInput).topics = [sourceTransferSignature, 1, 2] := by decide
example : (Generated.sourceOp_transferLogRequest
    { baseInput with handoff := { baseHandoff with tx := { baseTransaction with sender := { id := 7 } } } }).topics =
    [sourceTransferSignature, 7, 2] := by decide
example : SimpleTransferCompletionExtractor.Refinement.normalReturnDomain baseInput := by decide
example : ¬ SimpleTransferCompletionExtractor.Refinement.normalReturnDomain
    { baseInput with tracer := { baseInput.tracer with normalReturn := false } } := by decide
example : baseInput.tracer.normalReturn ≠ false :=
  SimpleTransferCompletionExtractor.Refinement.normal_return_domain_excludes_callback_prefix
    baseInput (by decide)
example : SimpleTransferCompletionExtractor.Reference.accessObservationExtensional
    ({ addresses := [1, 2], storageCells := [] } : SimpleTransferCompletionExtractor.Reference.AccessObservation)
    ({ addresses := [2, 1], storageCells := [] } : SimpleTransferCompletionExtractor.Reference.AccessObservation) := by
  simp [SimpleTransferCompletionExtractor.Reference.accessObservationExtensional, or_comm]
example : SimpleTransferCompletionExtractor.Reference.accessObservationExtensional
    ({ addresses := [1, 2], storageCells := [{ address := 4, key := 9 }, { address := 4, key := 10 }] } :
      SimpleTransferCompletionExtractor.Reference.AccessObservation)
    ({ addresses := [2, 1], storageCells := [{ address := 4, key := 10 }, { address := 4, key := 9 }] } :
      SimpleTransferCompletionExtractor.Reference.AccessObservation) := by
  simp [SimpleTransferCompletionExtractor.Reference.accessObservationExtensional, or_comm]
example : SimpleTransferCompletionExtractor.Reference.journalExtensional
    [.trace (.access [1, 2] [])]
    [.trace (.access [2, 1] [])] := by
  simp [SimpleTransferCompletionExtractor.Reference.journalExtensional,
    SimpleTransferCompletionExtractor.Reference.journalEventExtensional,
    SimpleTransferCompletionExtractor.Reference.traceRequestExtensional,
    SimpleTransferCompletionExtractor.Reference.accessObservationExtensional, or_comm]
example : ¬ SimpleTransferCompletionExtractor.Reference.journalExtensional
    [.metric, .trace (.access [1] [])]
    [.trace (.access [1] []), .metric] := by
  simp [SimpleTransferCompletionExtractor.Reference.journalExtensional,
    SimpleTransferCompletionExtractor.Reference.journalEventExtensional]
example : SimpleTransferCompletionExtractor.Refinement.mapTransferLog
    (Generated.sourceOp_transferLogRequest baseInput) =
    SimpleTransferCompletionExtractor.Reference.expectedTransferLog
      (SimpleTransferCompletionExtractor.Refinement.mapInput baseInput) := by
  exact SimpleTransferCompletionExtractor.Refinement.transfer_log_bridge baseInput
example : successfulDeadRecipient.result = .ok := by decide
example : newAccountOutOfGas.result = .evmException .outOfGas := by decide
example : (Generated.run { baseInput with world := { baseInput.world with stateRootAfterRecalculate := some 99 } }).receiptContinuation.stateRoot = some 99 := by decide

def allVectors : List SimpleComplete :=
  [successfulDeadRecipient, selfSend, newAccountOutOfGas, transferLogAndBuildUp,
   compositeBuildUpFlags, legacyPre7778EffectiveBlock, eip7778EffectiveBlock,
   accessDisabledStorage, accessListOnly, accessCoinbaseOnly, accessDedup,
    receiptRootAfterRecalculate, receiptRootHiddenWithoutTracing]

end SimpleTransferCompletionExtractor.Vectors
