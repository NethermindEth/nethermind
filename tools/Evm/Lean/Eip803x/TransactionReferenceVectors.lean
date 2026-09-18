-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.TransactionReference

namespace Eip803x.TransactionReferenceVectors

open Eip803x.TransactionReference

private def noFailures : ValidationFailure → Bool := fun _ => false

private def oneFailure (failure : ValidationFailure) : ValidationFailure → Bool :=
  fun candidate => decide (candidate = failure)

private def baseGas : GasTransition :=
  { gasLeft := 80
    stateGasReservoir := 0
    stateGasFromGasLeft := 0
    refundCounter := 0
    evmStateGasUsed := 0 }

private def baseFees : FeeSchedule :=
  { effectiveGasPrice := 1
    premiumPerGas := 1
    baseFeePerGas := 0
    blobBaseFee := 0
    feeCollectorEnabled := false
    overflow := false }

private def baseInput : Input :=
  { loadNonceFromState := false
    entryKind := .messageCall
    mode := .execute
    tracerIsTracingState := false
    forcedRestoreFailure := none
    deleteCallerAccount := false
    hasAuthorizationList := false
    initialization :=
      { txGas := 100
        intrinsicGas := 20
        txMaxGasLimit := 100 }
    initialRefundCounter := 0
    initialStateGasUsed := 0
    calldataFloorGasCost := 0
    value := 5
    validation := noFailures
    senderValid := true
    gasPurchaseSucceeds := true
    nonceDecision := none
    authorization := .none
    authorizationGas := baseGas
    recipientStateCharge := 0
    createDestination :=
      { accountExists := false
        balance := 0
        nonce := 0
        codeEmpty := true
        storageNonEmpty := false
        collision := .none }
    newAccountStateGas := 0
    executionGas := baseGas
    environmentFailure := none
    execution := .success
    destroyList := []
    destroyRefund := 0
    codeInsertExecutionRefund := 0
    refundQuotient := 5
    fees := baseFees
    priorCumulativeReceiptGas := 0
    priorBlockExecutionGas := 0
    priorBlockStateGas := 0
    postStateRoot := none
    eip8037Enabled := true
    eip7778Enabled := true }

def successfulEvm : Input := baseInput

def revertedEvm : Input :=
  { baseInput with
    execution := .revert
    executionGas := { baseGas with gasLeft := 65, refundCounter := 20 }
    calldataFloorGasCost := 50
    priorCumulativeReceiptGas := 7 }

def exceptionalEvm : Input :=
  { baseInput with
    execution := .exception .stackUnderflow
    executionGas := { baseGas with gasLeft := 0 } }

def topFrameOutOfGasEvm : Input :=
  { baseInput with
    execution := .topFrameOutOfGas
    executionGas := { baseGas with gasLeft := 0 } }

def collisionEvm : Input :=
  { baseInput with
    entryKind := .contractCreation
    createDestination :=
      { accountExists := false
        balance := 0
        nonce := 0
        codeEmpty := true
        storageNonEmpty := true
        collision := .storageOnly }
    newAccountStateGas := 7
    execution := .collision
    executionGas := { baseGas with gasLeft := 70 } }

def createStateOutOfGasEvm : Input :=
  { baseInput with
    entryKind := .contractCreation
    newAccountStateGas := 81
    execution := .createStateOutOfGas
    executionGas := { baseGas with gasLeft := 0 } }

def depositInvalidCodeEvm : Input :=
  { baseInput with
    entryKind := .contractCreation
    execution := .depositInvalidCode
    executionGas := { baseGas with gasLeft := 40 } }

def depositOutOfGasEvm : Input :=
  { baseInput with
    entryKind := .contractCreation
    execution := .depositOutOfGas
    executionGas := { baseGas with gasLeft := 40 } }

def successfulCreation : Input :=
  { baseInput with
    entryKind := .contractCreation
    newAccountStateGas := 7
    executionGas :=
      { baseGas with gasLeft := 70, stateGasFromGasLeft := 7, evmStateGasUsed := 7 } }

def revertedCreationRefillsStateGas : Input :=
  { successfulCreation with
    execution := .revert
    executionGas :=
      { baseGas with gasLeft := 60, stateGasFromGasLeft := 7, evmStateGasUsed := 7 } }

def authorizationOutOfGas : Input :=
  { baseInput with
    hasAuthorizationList := true
    authorization := .outOfGas 1
    authorizationGas := { baseGas with gasLeft := 50, evmStateGasUsed := 10 }
    executionGas := { baseGas with gasLeft := 0 } }

def delegatedTargetOutOfGas : Input :=
  { baseInput with
    hasAuthorizationList := true
    authorization := .applied 1
    authorizationGas := { baseGas with gasLeft := 79, evmStateGasUsed := 1 }
    environmentFailure := some .delegatedTargetOutOfGas
    executionGas :=
      { baseGas with
        gasLeft := 7
        stateGasReservoir := 5
        stateGasFromGasLeft := 6
        evmStateGasUsed := 9 } }

def appliedAuthorizations : Input :=
  { baseInput with
    hasAuthorizationList := true
    authorization := .applied 2
    authorizationGas := { baseGas with gasLeft := 78, evmStateGasUsed := 2 }
    executionGas := { baseGas with gasLeft := 70, evmStateGasUsed := 4 } }

def skippedAuthorizations : Input :=
  { baseInput with
    hasAuthorizationList := true
    authorization := .skippedInvalid 2 }

def simpleTransfer : Input :=
  { baseInput with entryKind := .simpleTransfer }

def simpleTransferWithStateCharge : Input :=
  { simpleTransfer with recipientStateCharge := 7 }

def stateTracedSimpleTransfer : Input :=
  { simpleTransfer with tracerIsTracingState := true }

def simpleTransferOutOfGas : Input :=
  { baseInput with
    entryKind := .simpleTransfer
    recipientStateCharge := 81
    executionGas := { baseGas with gasLeft := 0 } }

def simpleTransferOutOfGasWithReservoir : Input :=
  { simpleTransferOutOfGas with
    initialization := { txGas := 100, intrinsicGas := 20, txMaxGasLimit := 98 }
    executionGas := { baseGas with gasLeft := 61 } }

private def simpleTransferRevert : Input :=
  { simpleTransfer with execution := .revert }

def staticValidationFailure : Input :=
  { baseInput with validation := oneFailure .malformedTransaction }

def skipValidationGates : Input :=
  { baseInput with
    mode := .trace
    validation := oneFailure .nonceOverflow
    nonceDecision := some .tooHigh }

def skipValidationUnconditionalFailure : Input :=
  { baseInput with
    mode := .trace
    validation := oneFailure .malformedTransaction }

def senderValidationFailure : Input :=
  { baseInput with senderValid := false }

def gasPurchaseFailure : Input :=
  { baseInput with gasPurchaseSucceeds := false }

def maxFeeFailure : Input :=
  { baseInput with validation := oneFailure .maxFeePerGasBelowBaseFee }

def balanceFailure : Input :=
  { baseInput with validation := oneFailure .insufficientSenderBalance }

def gasPurchaseFailureCallAndRestore : Input :=
  { gasPurchaseFailure with mode := .callAndRestore }

def senderFailureCallAndRestore : Input :=
  { baseInput with
    mode := .callAndRestore
    forcedRestoreFailure := some (.sender .senderHasDeployedCode) }

def maxFeeFailureCallAndRestore : Input :=
  { baseInput with
    mode := .callAndRestore
    forcedRestoreFailure := some .maxFee }

def balanceFailureCallAndRestore : Input :=
  { baseInput with
    mode := .callAndRestore
    forcedRestoreFailure := some .balance }

def nonceFailureCallAndRestore : Input :=
  { baseInput with
    mode := .callAndRestore
    forcedRestoreFailure := some (.nonce .tooHigh) }

def feeOverflowCallAndRestore : Input :=
  { baseInput with
    mode := .callAndRestore
    forcedRestoreFailure := some .feeOverflow }

def nonceFailure : Input :=
  { baseInput with nonceDecision := some .tooHigh }

def environmentFailure : Input :=
  { baseInput with environmentFailure := some .recipientNotResolved }

def environmentFailureCallAndRestore : Input :=
  { environmentFailure with mode := .callAndRestore }

def systemTransaction : Input :=
  { baseInput with entryKind := .systemCall }

def skipValidationSystemRoute : Input :=
  { baseInput with mode := .systemSkipValidation }

def buildUp : Input :=
  { baseInput with mode := .buildUp }

def warmup : Input :=
  { baseInput with mode := .warmup }

def traceMode : Input :=
  { skipValidationGates with validation := noFailures, nonceDecision := none }

def buildUpStaticFailure : Input :=
  { staticValidationFailure with mode := .buildUp }

def buildUpGasFailure : Input :=
  { gasPurchaseFailure with mode := .buildUp }

def warmupGasFailure : Input :=
  { gasPurchaseFailure with mode := .warmup }

def traceGasFailure : Input :=
  { gasPurchaseFailure with mode := .trace }

def feeSchedule : Input :=
  { baseInput with
    fees :=
      { effectiveGasPrice := 2
        premiumPerGas := 1
        baseFeePerGas := 3
        blobBaseFee := 4
        feeCollectorEnabled := true
        overflow := false }
    priorCumulativeReceiptGas := 5
    postStateRoot := some 77 }

def blockStateBottleneck : Input :=
  { baseInput with
    executionGas := { baseGas with gasLeft := 60, evmStateGasUsed := 40 }
    priorBlockExecutionGas := 100
    priorBlockStateGas := 110 }

def invalidSettlementDomainInput : Input :=
  { baseInput with
    executionGas := { baseGas with gasLeft := 80, evmStateGasUsed := 40 } }

def feeArithmeticOverflow : Input :=
  { baseInput with fees := { baseFees with overflow := true } }

def destroyListSuccess : Input :=
  { baseInput with
    destroyList := [9, 10]
    destroyRefund := 2 }

def unmodeledExecution : Input :=
  { baseInput with execution := .unmodeled }

structure Observation where
  outcome : LifecycleOutcome
  trace : List Event
  durableWorld : Nat
  reversibleWorld : Nat
  senderNonce : Nat
  senderBalance : Nat
  recipientBalance : Nat
  beneficiaryBalance : Nat
  feeCollectorBalance : Nat
  reservedGas : Nat
  gasLeft : Nat
  stateGasReservoir : Nat
  stateGasFromGasLeft : Nat
  refundCounter : Nat
  stateGasUsed : Nat
  flags : SubstateFlags
  authorization : Option AuthorizationDecision
  path : Option ExecutionPath
  topFrameOutOfGas : Bool
  pendingPreparationRestore : Bool
  preparationSnapshot : Bool
  topLevelSnapshot : Bool
  pendingRollback : Bool
  settlementPaidGas : Option Nat
  scalarSpentGas : Option Nat
  scalarGasRefund : Option Nat
  headerGasUsed : Nat
  blockCumulativeExecutionGas : Nat
  blockCumulativeStateGas : Nat
  blockExecutionGas : Nat
  blockStateGas : Nat
  cumulativeReceiptGas : Nat
  logs : List Nat
  destroyList : List Nat
  destroyListFinalized : Bool
  receiptOpen : Bool
  receiptClosed : Bool
  receipt : Option ReceiptObservation
  commitBeforeExecution : Bool
  committed : Bool
  transientReset : Bool
  cleanupRequired : Bool
  buildUpSnapshot : Bool
  nonceLoaded : Bool
  lastTransition : Option TransitionKind
  deriving DecidableEq, Repr

def observe (result : Run) : Observation :=
  { outcome := result.outcome
    trace := result.trace
    durableWorld := result.state.durableWorld
    reversibleWorld := result.state.reversibleWorld
    senderNonce := result.state.senderNonce
    senderBalance := result.state.senderBalance
    recipientBalance := result.state.recipientBalance
    beneficiaryBalance := result.state.beneficiaryBalance
    feeCollectorBalance := result.state.feeCollectorBalance
    reservedGas := result.state.reservedGas
    gasLeft := result.state.gas.gasLeft
    stateGasReservoir := result.state.gas.stateGasReservoir
    stateGasFromGasLeft := result.state.stateGasFromGasLeft
    refundCounter := result.state.gas.refundCounter
    stateGasUsed := result.state.gas.evmStateGasUsed
    flags := result.state.substateFlags
    authorization := result.state.authorization
    path := result.state.path
    topFrameOutOfGas := result.state.topFrameOutOfGas
    pendingPreparationRestore := result.state.pendingPreparationRestore
    preparationSnapshot := result.state.preparationSnapshot.isSome
    topLevelSnapshot := result.state.topLevelSnapshot.isSome
    pendingRollback := result.state.pendingRollback.isSome
    settlementPaidGas := result.state.settlement.map TransactionGas.Settlement.paidGas
    scalarSpentGas := result.state.scalarSettlement.map TransactionSettlement.Result.spentGas
    scalarGasRefund := result.state.scalarSettlement.map TransactionSettlement.Result.gasRefund
    headerGasUsed := result.state.headerGasUsed
    blockCumulativeExecutionGas := result.state.blockCumulativeExecutionGas
    blockCumulativeStateGas := result.state.blockCumulativeStateGas
    blockExecutionGas := result.state.settlement.map TransactionGas.Settlement.executionGas |>.getD 0
    blockStateGas := result.state.settlement.map TransactionGas.Settlement.stateGas |>.getD 0
    cumulativeReceiptGas := result.state.cumulativeReceiptGas
    logs := result.state.logs
    destroyList := result.state.destroyList
    destroyListFinalized := result.state.destroyListFinalized
    receiptOpen := result.state.receiptOpen
    receiptClosed := result.state.receiptClosed
    receipt := result.state.receipt
    commitBeforeExecution := result.state.commitBeforeExecution
    committed := result.state.committed
    transientReset := result.state.transientReset
    cleanupRequired := result.state.cleanupRequired
    buildUpSnapshot := result.state.buildUpSnapshot.isSome
    nonceLoaded := result.state.nonceLoaded
    lastTransition := result.state.lastTransition }

private def ordinaryPrefix (loadNonce buildUp commit : Bool) : List Event :=
  (if loadNonce then [.loadNonceFromState] else []) ++
    [.startNewTxTrace, .process] ++
    (if buildUp then [.buildUpSnapshot] else []) ++
    [.executeCoreOrdinary
    , .recoverSenderBeforeIntrinsicGas
    , .calculateIntrinsicGas
    , .validateStatic
    , .calculateEffectiveGasPrice
    , .recoverSenderIfNeeded
    , .validateSender
    , .buyGas
    , .incrementNonce
    , .prepareSimpleTransferFastPath] ++
    (if commit then [.commitBeforeExecution] else []) ++
    [.calculateAvailableGas]

private def evmSuccessTrace : List Event :=
  ordinaryPrefix false false true ++
    [.buildExecutionEnvironment, .recipientStateCharge, .topExecutionSnapshot
    , .payValue, .vmExecution, .refund, .headerGas, .payFees
    , .commit, .receiptStart, .receiptObserve, .endTxTrace]

private def evmRevertTrace : List Event :=
  ordinaryPrefix false false true ++
    [.buildExecutionEnvironment, .recipientStateCharge, .topExecutionSnapshot
    , .payValue, .vmExecution, .executionRollback, .refund, .headerGas, .payFees
    , .commit, .receiptStart, .receiptObserve, .endTxTrace]

private def evmExceptionTrace : List Event := evmRevertTrace

private def evmTopOutOfGasTrace : List Event :=
  ordinaryPrefix false false true ++
    [.buildExecutionEnvironment, .recipientStateCharge, .topExecutionSnapshot
    , .topFrameOutOfGas, .refund, .headerGas, .payFees
    , .commit, .receiptStart, .receiptObserve, .endTxTrace]

private def creationCollisionTrace : List Event :=
  ordinaryPrefix false false true ++
    [.buildExecutionEnvironment, .topExecutionSnapshot
    , .createDestinationRead, .createStateCharge, .collisionCheck, .executionRollback, .refund, .headerGas, .payFees
    , .commit, .receiptStart, .receiptObserve, .endTxTrace]

private def creationStateOogTrace : List Event :=
  ordinaryPrefix false false true ++
    [.buildExecutionEnvironment, .topExecutionSnapshot
    , .createDestinationRead, .createStateCharge, .topFrameOutOfGas, .executionRollback, .refund, .headerGas, .payFees
    , .commit, .receiptStart, .receiptObserve, .endTxTrace]

private def creationDeploymentFailureTrace : List Event :=
  ordinaryPrefix false false true ++
    [.buildExecutionEnvironment, .topExecutionSnapshot
    , .createDestinationRead, .collisionCheck, .payValue, .vmExecution, .deployment, .executionRollback
    , .refund, .headerGas, .payFees, .commit, .receiptStart, .receiptObserve
    , .endTxTrace]

private def creationSuccessTrace : List Event :=
  ordinaryPrefix false false true ++
    [.buildExecutionEnvironment, .topExecutionSnapshot
    , .createDestinationRead, .createStateCharge, .collisionCheck, .payValue, .vmExecution, .deployment, .refund, .headerGas, .payFees
    , .commit, .receiptStart, .receiptObserve, .endTxTrace]

private def creationRevertTrace : List Event :=
  ordinaryPrefix false false true ++
    [.buildExecutionEnvironment, .topExecutionSnapshot
    , .createDestinationRead, .createStateCharge, .collisionCheck, .payValue, .vmExecution
    , .executionRollback, .refund, .headerGas, .payFees, .commit, .receiptStart
    , .receiptObserve, .endTxTrace]

private def authSuccessTrace : List Event :=
  ordinaryPrefix false false true ++
    [.authorizationSnapshot, .processDelegations, .buildExecutionEnvironment
    , .recipientStateCharge, .topExecutionSnapshot, .payValue, .vmExecution
    , .refund, .headerGas, .payFees, .commit, .receiptStart, .receiptObserve
    , .endTxTrace]

private def authOutOfGasTrace : List Event :=
  ordinaryPrefix false false true ++
    [.authorizationSnapshot, .processDelegations, .buildExecutionEnvironment
    , .authorizationRestore, .topExecutionSnapshot, .topFrameOutOfGas
    , .refund, .headerGas, .payFees, .commit, .receiptStart, .receiptObserve
    , .endTxTrace]

private def simpleSuccessTrace : List Event :=
  ordinaryPrefix false false false ++
    [.simpleTransferExecution, .recipientStateCharge, .payValue, .refund
    , .headerGas, .payFees, .commit, .receiptStart, .receiptObserve, .endTxTrace]

private def stateTracedSimpleSuccessTrace : List Event :=
  ordinaryPrefix false false true ++
    [.simpleTransferExecution, .recipientStateCharge, .payValue, .refund
    , .headerGas, .payFees, .commit, .receiptStart, .receiptObserve, .endTxTrace]

private def simpleOogTrace : List Event :=
  ordinaryPrefix false false false ++
    [.simpleTransferExecution, .recipientStateCharge, .topFrameOutOfGas, .refund
    , .headerGas, .payFees, .commit, .receiptStart, .receiptObserve, .endTxTrace]

private def authAppliedTrace : List Event := authSuccessTrace

private def authSkippedTrace : List Event := authSuccessTrace

private def callAndRestoreCommitTrace : List Event :=
  ordinaryPrefix false false true ++
    [.buildExecutionEnvironment, .recipientStateCharge, .topExecutionSnapshot
    , .payValue, .vmExecution, .refund, .headerGas, .payFees, .restore
    , .commit
    , .receiptStart, .receiptObserve, .endTxTrace]

private def callAndRestoreDeleteTrace : List Event :=
  ordinaryPrefix false false true ++
    [.buildExecutionEnvironment, .recipientStateCharge, .topExecutionSnapshot
    , .payValue, .vmExecution, .refund, .headerGas, .payFees, .restore
    , .receiptStart, .receiptObserve, .endTxTrace]

private def buildUpTrace : List Event :=
  ordinaryPrefix false true false ++
    [.buildExecutionEnvironment, .recipientStateCharge, .topExecutionSnapshot
    , .payValue, .vmExecution, .refund, .headerGas, .payFees, .resetTransient
    , .receiptStart, .receiptObserve, .endTxTrace]

private def destroyTrace : List Event :=
  ordinaryPrefix false false true ++
    [.buildExecutionEnvironment, .recipientStateCharge, .topExecutionSnapshot
    , .payValue, .vmExecution, .refund, .headerGas, .payFees, .destroyListFinalize
    , .commit, .receiptStart, .receiptObserve, .endTxTrace]

private def traceModeTrace : List Event :=
  ordinaryPrefix false false true ++
    [.buildExecutionEnvironment, .recipientStateCharge, .topExecutionSnapshot
    , .payValue, .vmExecution, .refund, .headerGas, .payFees, .commit
    , .receiptStart, .receiptObserve, .endTxTrace]

private def loadTrace : List Event :=
  ordinaryPrefix true false true ++
    [.buildExecutionEnvironment, .recipientStateCharge, .topExecutionSnapshot
    , .payValue, .vmExecution, .refund, .headerGas, .payFees, .commit
    , .receiptStart, .receiptObserve, .endTxTrace]

private def invalidSettlementTrace : List Event :=
  ordinaryPrefix false false true ++
    [.buildExecutionEnvironment, .recipientStateCharge, .topExecutionSnapshot
    , .payValue, .vmExecution, .endTxTrace]

private def rejectedBase (failure : EarlyFailure) (trace : List Event) : Observation :=
  { outcome := .rejected failure
    trace
    durableWorld := 0
    reversibleWorld := 0
    senderNonce := 0
    senderBalance := 1_000_000
    recipientBalance := 0
    beneficiaryBalance := 0
    feeCollectorBalance := 0
    reservedGas := 0
    gasLeft := 80
    stateGasReservoir := 0
    stateGasFromGasLeft := 0
    refundCounter := 0
    stateGasUsed := 0
    flags := { isError := false, shouldRevert := false }
    authorization := none
    path := none
    topFrameOutOfGas := false
    pendingPreparationRestore := false
    preparationSnapshot := false
    topLevelSnapshot := false
    pendingRollback := false
    settlementPaidGas := none
    scalarSpentGas := none
    scalarGasRefund := none
    headerGasUsed := 0
    blockCumulativeExecutionGas := 0
    blockCumulativeStateGas := 0
    blockExecutionGas := 0
    blockStateGas := 0
    cumulativeReceiptGas := 0
    logs := []
    destroyList := []
    destroyListFinalized := false
    receiptOpen := false
    receiptClosed := true
    receipt := none
    commitBeforeExecution := false
    committed := false
    transientReset := false
    cleanupRequired := false
    buildUpSnapshot := false
    nonceLoaded := false
    lastTransition := none }

private def rejectedAfterBuy (failure : EarlyFailure) (trace : List Event) : Observation :=
  { rejectedBase failure trace with
    senderBalance := 999_900
    reservedGas := 100
    cleanupRequired := true }

private def rejectedAfterBuyRestored (failure : EarlyFailure) (trace : List Event) : Observation :=
  rejectedBase failure trace

private def rejectedBuildUp (failure : EarlyFailure) (trace : List Event) : Observation :=
  { rejectedBase failure trace with buildUpSnapshot := true }

private def escapedAfterEnvironment (trace : List Event) : Observation :=
  { rejectedBase (.unmodeled .buildEnvironmentException) trace with
    outcome := .escapedException .buildEnvironmentException
    senderNonce := 1
    senderBalance := 999_900
    reservedGas := 100
    path := some .evm
    receiptClosed := false
    commitBeforeExecution := trace.contains .commitBeforeExecution
    cleanupRequired := true }

private def invalidSettlementObservation (trace : List Event) : Observation :=
  { rejectedBase (.unmodeled .invalidSettlementDomain) trace with
    outcome := .unmodeled .invalidSettlementDomain
    durableWorld := 0
    reversibleWorld := 1
    senderNonce := 1
    senderBalance := 999_895
    recipientBalance := 5
    reservedGas := 100
    gasLeft := 80
    stateGasUsed := 40
    path := some .evm
    topLevelSnapshot := true
    logs := [5]
    receiptClosed := true
    commitBeforeExecution := trace.contains .commitBeforeExecution
    cleanupRequired := true
    lastTransition := some .success }

private def literalCompleted
    (outcome : LifecycleOutcome) (flags : SubstateFlags)
    (receiptStatus : ReceiptStatus) (lastTransition : Option TransitionKind)
    (trace : List Event)
    (path : ExecutionPath) (gas : GasTransition)
    (durableWorld reversibleWorld senderBalance recipientBalance beneficiaryBalance feeCollectorBalance : Nat)
    (authorization : Option AuthorizationDecision) (preparationSnapshot : Bool)
    (scalarSpent scalarRefund cumulative paidGas : Nat) (logs destroyList : List Nat)
    (destroyFinalized topFrameOutOfGas committed transientReset buildUpSnapshot nonceLoaded : Bool)
    (topLevelSnapshot : Bool) (stateRoot : Option Nat) : Observation :=
  { outcome
    trace
    durableWorld
    reversibleWorld
    senderNonce := 1
    senderBalance
    recipientBalance
    beneficiaryBalance
    feeCollectorBalance
    reservedGas := 100
    gasLeft := gas.gasLeft
    stateGasReservoir := gas.stateGasReservoir
    stateGasFromGasLeft := gas.stateGasFromGasLeft
    refundCounter := gas.refundCounter
    stateGasUsed := gas.evmStateGasUsed
    flags
    authorization
    path := some path
    topFrameOutOfGas
    pendingPreparationRestore := false
    preparationSnapshot
    topLevelSnapshot
    pendingRollback := false
    settlementPaidGas := some paidGas
    scalarSpentGas := some scalarSpent
    scalarGasRefund := some scalarRefund
    headerGasUsed := scalarSpent
    blockCumulativeExecutionGas := scalarSpent
    blockCumulativeStateGas := 0
    blockExecutionGas := scalarSpent
    blockStateGas := 0
    cumulativeReceiptGas := cumulative
    logs
    destroyList
    destroyListFinalized := destroyFinalized
    receiptOpen := false
    receiptClosed := true
    receipt := some
      { status := receiptStatus
        gasUsed := scalarSpent
        cumulativeGasUsed := cumulative
        logs
        stateRoot }
    commitBeforeExecution := trace.contains .commitBeforeExecution
    committed
    transientReset
    cleanupRequired := false
    buildUpSnapshot
    nonceLoaded
    lastTransition }

private def withBlockAccounting (observation : Observation)
    (header cumulativeExecution cumulativeState execution stateGas : Nat) : Observation :=
  { observation with
    headerGasUsed := header
    blockCumulativeExecutionGas := cumulativeExecution
    blockCumulativeStateGas := cumulativeState
    blockExecutionGas := execution
    blockStateGas := stateGas }

private def systemExpected (trace : List Event) : Observation :=
  { rejectedBase (.unmodeled .systemTransactionProcessor) trace with outcome := .systemBypass }

structure Vector where
  name : String
  input : Input
  expected : Observation

def vectors : List Vector :=
  [ { name := "successful EVM transaction",
      input := successfulEvm,
      expected := literalCompleted (.executed .success none)
        { isError := false, shouldRevert := false } .success (some .success)
        evmSuccessTrace .evm baseGas
        0 1 999_975 5 20 0 none false 20 0 20 20 [5] [] false false true false false false true none }
  , { name := "REVERT returns unused gas and suppresses state refunds",
      input := revertedEvm,
      expected := withBlockAccounting (literalCompleted (.executed .failure none)
        { isError := false, shouldRevert := true } .failure (some .revert)
        evmRevertTrace .evm { baseGas with gasLeft := 65 }
        0 0 999_950 0 50 0 none false 50 0 57 50 [] [] false false true false false false true none)
        50 50 0 50 0 }
  , { name := "top-level CREATE REVERT refills its new-account state gas",
      input := revertedCreationRefillsStateGas,
      expected := literalCompleted (.executed .failure none)
        { isError := false, shouldRevert := true } .failure (some .revert)
        creationRevertTrace .evm { baseGas with gasLeft := 67 }
        0 0 999_967 0 33 0 none false 33 0 33 33 [] [] false false true false false false true none }
  , { name := "zero-reservoir exception burns gas",
      input := exceptionalEvm,
      expected := literalCompleted (.executed .failure (some .stackUnderflow))
        { isError := true, shouldRevert := false } .failure (some .exception)
        evmExceptionTrace .evm { baseGas with gasLeft := 0 }
        0 0 999_900 0 100 0 none false 100 0 100 100 [] [] false false true false false false true none }
  , { name := "explicit top-frame out of gas",
      input := topFrameOutOfGasEvm,
      expected := literalCompleted (.executed .failure (some .outOfGas))
        { isError := true, shouldRevert := false } .failure (some .topFrameOutOfGas)
        evmTopOutOfGasTrace .evm { baseGas with gasLeft := 0 }
        0 0 999_900 0 100 0 none false 100 0 100 100 [] [] false true true false false false true none }
  , { name := "contract collision clears execution gas and burns full gas",
      input := collisionEvm,
      expected := literalCompleted (.executed .failure (some .collision))
        { isError := true, shouldRevert := false } .failure (some .collision)
        creationCollisionTrace .evm { baseGas with gasLeft := 0 }
        0 0 999_900 0 100 0 none false 100 0 100 100 [] [] false false true false false false true none }
  , { name := "CREATE state-gas out of gas is an error",
      input := createStateOutOfGasEvm,
      expected := literalCompleted (.executed .failure (some .outOfGas))
        { isError := true, shouldRevert := false } .failure (some .createStateOutOfGas)
        creationStateOogTrace .evm { baseGas with gasLeft := 0 }
        0 0 999_900 0 100 0 none false 100 0 100 100 [] [] false true true false false false true none }
  , { name := "invalid code deposit clears execution gas",
      input := depositInvalidCodeEvm,
      expected := literalCompleted (.executed .failure (some .invalidCode))
        { isError := false, shouldRevert := false } .failure (some .depositInvalidCode)
        creationDeploymentFailureTrace .evm { baseGas with gasLeft := 0 }
        0 0 999_900 0 100 0 none false 100 0 100 100 [] [] false false true false false false true none }
  , { name := "out-of-gas code deposit is distinct",
      input := depositOutOfGasEvm,
      expected := literalCompleted (.executed .failure (some .outOfGas))
        { isError := false, shouldRevert := false } .failure (some .depositOutOfGas)
        creationDeploymentFailureTrace .evm { baseGas with gasLeft := 0 }
        0 0 999_900 0 100 0 none false 100 0 100 100 [] [] false false true false false false true none }
  , { name := "successful contract deployment",
      input := successfulCreation,
      expected := withBlockAccounting (literalCompleted (.executed .success none)
        { isError := false, shouldRevert := false } .success (some .success)
        creationSuccessTrace .evm
          { baseGas with gasLeft := 70, stateGasFromGasLeft := 7, evmStateGasUsed := 7 }
        0 1 999_965 5 30 0 none false 30 0 30 30 [5] [] false false true false false false true none)
        23 23 7 23 7 }
  , { name := "authorization OOG restores partial mutation before halt",
      input := authorizationOutOfGas,
      expected := literalCompleted (.executed .failure (some .outOfGas))
        { isError := true, shouldRevert := false } .failure (some .topFrameOutOfGas)
        authOutOfGasTrace .evm { baseGas with gasLeft := 0 }
        0 0 999_900 0 100 0 (some (.outOfGas 1)) true 100 0 100 100 [] [] false true true false false false true none }
  , { name := "delegated-target OOG restores authorization preparation before halt",
      input := delegatedTargetOutOfGas,
      expected := literalCompleted (.executed .failure (some .outOfGas))
        { isError := true, shouldRevert := false } .failure (some .topFrameOutOfGas)
        authOutOfGasTrace .evm { baseGas with gasLeft := 0 }
        0 0 999_900 0 100 0 (some (.applied 1)) true 100 0 100 100 [] [] false true true false false false true none }
  , { name := "applied EIP-7702 authorizations and state charges",
      input := appliedAuthorizations,
      expected := withBlockAccounting (literalCompleted (.executed .success none)
        { isError := false, shouldRevert := false } .success (some .success)
        authAppliedTrace .evm { baseGas with gasLeft := 70, evmStateGasUsed := 4 }
        2 1 999_965 5 30 0 (some (.applied 2)) true 30 0 30 30 [5] [] false false true false false false true none)
        26 26 4 26 4 }
  , { name := "skipped invalid authorizations",
      input := skippedAuthorizations,
      expected := literalCompleted (.executed .success none)
        { isError := false, shouldRevert := false } .success (some .success)
        authSkippedTrace .evm baseGas
        0 1 999_975 5 20 0 (some (.skippedInvalid 2)) true 20 0 20 20 [5] [] false false true false false false true none }
  , { name := "simple transfer fast path",
      input := simpleTransfer,
      expected := literalCompleted (.executed .success none)
        { isError := false, shouldRevert := false } .success (some .success)
        simpleSuccessTrace .simpleTransfer baseGas
        0 1 999_975 5 20 0 none false 20 0 20 20 [5] [] false false true false false false false none }
  , { name := "simple transfer retains charged recipient state gas",
      input := simpleTransferWithStateCharge,
      expected := withBlockAccounting (literalCompleted (.executed .success none)
        { isError := false, shouldRevert := false } .success (some .success)
        simpleSuccessTrace .simpleTransfer
          { baseGas with gasLeft := 73, stateGasFromGasLeft := 7, evmStateGasUsed := 7 }
        0 1 999_968 5 27 0 none false 27 0 27 27 [5] [] false false true false false false false none)
        20 20 7 20 7 }
  , { name := "state-traced simple transfer commits before execution",
      input := stateTracedSimpleTransfer,
      expected := literalCompleted (.executed .success none)
        { isError := false, shouldRevert := false } .success (some .success)
        stateTracedSimpleSuccessTrace .simpleTransfer baseGas
        0 1 999_975 5 20 0 none false 20 0 20 20 [5] [] false false true false false false false none }
  , { name := "simple transfer state-gas out of gas",
      input := simpleTransferOutOfGas,
      expected := literalCompleted (.executed .failure (some .outOfGas))
        { isError := false, shouldRevert := false } .failure (some .simpleTransfer)
        simpleOogTrace .simpleTransfer { baseGas with gasLeft := 0 }
        0 0 999_900 0 100 0 none false 100 0 100 100 [] [] false true true false false false false none }
  , { name := "simple transfer state-gas OOG returns its reservoir",
      input := simpleTransferOutOfGasWithReservoir,
      expected := literalCompleted (.executed .failure (some .outOfGas))
        { isError := false, shouldRevert := false } .failure (some .simpleTransfer)
        simpleOogTrace .simpleTransfer { baseGas with gasLeft := 0, stateGasReservoir := 2 }
        0 0 999_902 0 98 0 none false 98 0 98 98 [] [] false true true false false false false none }
  , { name := "static validation rejection",
      input := staticValidationFailure,
      expected := rejectedBase (.validation .malformedTransaction)
        [.startNewTxTrace, .process, .executeCoreOrdinary
        , .recoverSenderBeforeIntrinsicGas, .calculateIntrinsicGas, .validateStatic
        , .endTxTrace] }
  , { name := "sender validation rejection",
      input := senderValidationFailure,
      expected := rejectedBase (.sender .senderHasDeployedCode)
        ((ordinaryPrefix false false false).take 9 ++ [.endTxTrace]) }
  , { name := "max-fee failure is anchored at BuyGas",
      input := maxFeeFailure,
      expected := rejectedBase (.validation .maxFeePerGasBelowBaseFee)
        ((ordinaryPrefix false false false).take 10 ++ [.endTxTrace]) }
  , { name := "balance failure is anchored at BuyGas",
      input := balanceFailure,
      expected := rejectedBase (.validation .insufficientSenderBalance)
        ((ordinaryPrefix false false false).take 10 ++ [.endTxTrace]) }
  , { name := "gas purchase rejection",
      input := gasPurchaseFailure,
      expected := rejectedBase .gasPurchase
        ((ordinaryPrefix false false false).take 10 ++ [.endTxTrace]) }
  , { name := "nonce rejection leaves reservation in execute mode",
      input := nonceFailure,
      expected := rejectedAfterBuy (.nonce .tooHigh)
        ((ordinaryPrefix false false false).take 11 ++ [.endTxTrace]) }
  , { name := "gas failure restores in call-and-restore",
      input := gasPurchaseFailureCallAndRestore,
      expected := rejectedBase .gasPurchase
        ((ordinaryPrefix false false false).take 10 ++ [.restore, .endTxTrace]) }
  , { name := "sender failure restores in call-and-restore",
      input := senderFailureCallAndRestore,
      expected := rejectedBase (.sender .senderHasDeployedCode)
        ((ordinaryPrefix false false false).take 9 ++ [.restore, .endTxTrace]) }
  , { name := "max-fee failure restores in call-and-restore",
      input := maxFeeFailureCallAndRestore,
      expected := rejectedBase (.validation .maxFeePerGasBelowBaseFee)
        ((ordinaryPrefix false false false).take 10 ++ [.restore, .endTxTrace]) }
  , { name := "balance failure restores in call-and-restore",
      input := balanceFailureCallAndRestore,
      expected := rejectedBase (.validation .insufficientSenderBalance)
        ((ordinaryPrefix false false false).take 10 ++ [.restore, .endTxTrace]) }
  , { name := "nonce failure restores reservation in call-and-restore",
      input := nonceFailureCallAndRestore,
      expected := rejectedBase (.nonce .tooHigh)
        ((ordinaryPrefix false false false).take 11 ++ [.restore, .endTxTrace]) }
  , { name := "fee overflow restores in call-and-restore",
      input := feeOverflowCallAndRestore,
      expected := { (rejectedBase (.unmodeled .feeArithmeticOverflow)
        ((ordinaryPrefix false false false).take 10 ++ [.restore, .endTxTrace])) with
        outcome := .unmodeled .feeArithmeticOverflow } }
  , { name := "environment exception escapes the transaction result",
      input := environmentFailure,
      expected := escapedAfterEnvironment
        (ordinaryPrefix false false true ++ [.buildExecutionEnvironment]) }
  , { name := "environment exception escapes call-and-restore without reset",
      input := environmentFailureCallAndRestore,
      expected := escapedAfterEnvironment
        (ordinaryPrefix false false true ++ [.buildExecutionEnvironment]) }
  , { name := "system transaction route",
      input := systemTransaction,
      expected := systemExpected [.startNewTxTrace, .process, .executeCoreSystem, .endTxTrace] }
  , { name := "skip-validation system route",
      input := skipValidationSystemRoute,
      expected := systemExpected [.startNewTxTrace, .process, .executeCoreSystem, .endTxTrace] }
  , { name := "BuildUp entry snapshot and reset",
      input := buildUp,
      expected := literalCompleted (.executed .success none)
        { isError := false, shouldRevert := false } .success (some .success)
        buildUpTrace .evm baseGas
        0 1 999_975 5 20 0 none false 20 0 20 20 [5] [] false false false true true false true none }
  , { name := "BuildUp static failure retains entry snapshot",
      input := buildUpStaticFailure,
      expected := rejectedBuildUp (.validation .malformedTransaction)
        ((ordinaryPrefix false true false).take 7 ++ [.endTxTrace]) }
  , { name := "BuildUp gas failure retains entry snapshot",
      input := buildUpGasFailure,
      expected := rejectedBuildUp .gasPurchase
        ((ordinaryPrefix false true false).take 11 ++ [.endTxTrace]) }
  , { name := "warmup reset",
      input := warmup,
      expected := withBlockAccounting (literalCompleted (.executed .success none)
        { isError := false, shouldRevert := false } .success (some .success)
        (ordinaryPrefix false false false ++
          [.buildExecutionEnvironment, .recipientStateCharge, .topExecutionSnapshot
          , .payValue, .vmExecution, .refund, .headerGas, .payFees, .resetTransient
          , .receiptStart, .receiptObserve, .endTxTrace]) .evm baseGas
        0 1 999_975 5 20 0 none false 20 0 20 20 [5] [] false false false true false false true none)
        0 0 0 20 0 }
  , { name := "warmup gas failure",
      input := warmupGasFailure,
      expected := rejectedBase .gasPurchase
        ((ordinaryPrefix false false false).take 10 ++ [.endTxTrace]) }
  , { name := "trace mode runs static action and commits",
      input := traceMode,
      expected := withBlockAccounting (literalCompleted (.executed .success none)
        { isError := false, shouldRevert := false } .success (some .success)
        traceModeTrace .evm baseGas
        0 1 999_975 5 20 0 none false 20 0 20 20 [5] [] false false true false false false true none)
        0 0 0 20 0 }
  , { name := "SkipValidation ignores gated static and nonce checks",
      input := skipValidationGates,
      expected := withBlockAccounting (literalCompleted (.executed .success none)
        { isError := false, shouldRevert := false } .success (some .success)
        traceModeTrace .evm baseGas
        0 1 999_975 5 20 0 none false 20 0 20 20 [5] [] false false true false false false true none)
        0 0 0 20 0 }
  , { name := "SkipValidation still rejects unconditional static failure",
      input := skipValidationUnconditionalFailure,
      expected := rejectedBase (.validation .malformedTransaction)
        ((ordinaryPrefix false false false).take 6 ++ [.endTxTrace]) }
  , { name := "trace gas failure",
      input := traceGasFailure,
      expected := rejectedBase .gasPurchase
        ((ordinaryPrefix false false false).take 10 ++ [.endTxTrace]) }
  , { name := "load nonce event precedes tracing",
      input := { baseInput with loadNonceFromState := true },
      expected := literalCompleted (.executed .success none)
        { isError := false, shouldRevert := false } .success (some .success)
        loadTrace .evm baseGas
        0 1 999_975 5 20 0 none false 20 0 20 20 [5] [] false false true false false true true none }
  , { name := "explicit effective price and fee collector cap",
      input := feeSchedule,
      expected := literalCompleted (.executed .success none)
        { isError := false, shouldRevert := false } .success (some .success)
        (ordinaryPrefix false false true ++
          [.buildExecutionEnvironment, .recipientStateCharge, .topExecutionSnapshot
          , .payValue, .vmExecution, .refund, .headerGas, .payFees, .commit
          , .receiptStart, .receiptObserve, .endTxTrace]) .evm baseGas
        0 1 999_951 5 20 44 none false 20 0 25 20 [5] [] false false true false false false true (some 77) }
  , { name := "fee arithmetic overflow is explicit at BuyGas",
      input := feeArithmeticOverflow,
      expected := { rejectedBase (.unmodeled .feeArithmeticOverflow)
          ([.startNewTxTrace, .process, .executeCoreOrdinary
          , .recoverSenderBeforeIntrinsicGas, .calculateIntrinsicGas, .validateStatic
          , .calculateEffectiveGasPrice, .recoverSenderIfNeeded, .validateSender
          , .buyGas, .endTxTrace]) with
          outcome := .unmodeled .feeArithmeticOverflow } }
  , { name := "post-fee destroy-list finalization",
      input := destroyListSuccess,
      expected := withBlockAccounting (literalCompleted (.executed .success none)
        { isError := false, shouldRevert := false } .success (some .success)
        destroyTrace .evm baseGas
        2 1 999_979 5 16 0 none false 16 4 16 20 [5, 9, 10] [] true false true false false false true none)
        20 20 0 20 0 }
  , { name := "unmodeled VM outcome remains observable",
      input := unmodeledExecution,
      expected := withBlockAccounting (literalCompleted (.unmodeled .unsupportedProductionAdapter)
        { isError := true, shouldRevert := false } .failure (some .exception)
        (ordinaryPrefix false false true ++
          [.buildExecutionEnvironment, .recipientStateCharge, .topExecutionSnapshot
          , .payValue, .vmExecution, .refund, .headerGas, .payFees, .commit
          , .receiptStart, .receiptObserve, .endTxTrace]) .evm baseGas
        0 0 999_900 0 100 0 none false 100 0 100 20 [] [] false false true false false false true none)
        20 20 0 20 0 }
  , { name := "call-and-restore commits restored ordinary sender state",
      input := { baseInput with mode := .callAndRestore },
      expected := { (withBlockAccounting (literalCompleted (.executed .success none)
        { isError := false, shouldRevert := false } .success (some .success)
        callAndRestoreCommitTrace .evm baseGas
        0 0 1_000_000 0 0 0 none false 20 0 20 20 [5] [] false false true false false false true none)
        0 0 0 20 0) with senderNonce := 0, reservedGas := 0 } }
  , { name := "call-and-restore deletes temporary sender without commit",
      input := { baseInput with mode := .callAndRestore, deleteCallerAccount := true },
      expected := { (withBlockAccounting (literalCompleted (.executed .success none)
        { isError := false, shouldRevert := false } .success (some .success)
        callAndRestoreDeleteTrace .evm baseGas
        0 0 1_000_000 0 0 0 none false 20 0 20 20 [5] [] false false false false false false true none)
        0 0 0 20 0) with senderNonce := 0, reservedGas := 0 } }
  , { name := "state-gas bottleneck controls block header gas",
      input := blockStateBottleneck,
      expected := withBlockAccounting (literalCompleted (.executed .success none)
        { isError := false, shouldRevert := false } .success (some .success)
        evmSuccessTrace .evm { baseGas with gasLeft := 60, evmStateGasUsed := 40 }
        0 1 999_955 5 40 0 none false 40 0 40 40 [5] [] false false true false false false true none)
        150 100 150 0 40 }
  , { name := "invalid settlement domain fails closed through run",
      input := invalidSettlementDomainInput,
      expected := invalidSettlementObservation invalidSettlementTrace }
  ]

private def passes (vector : Vector) : Bool :=
  let actual := run nonsemanticFixtureOracle vector.input
  traceOrdered actual.trace && observe actual == vector.expected

theorem all_vectors_pass : vectors.all passes = true := by
  native_decide

theorem all_vector_traces_are_ordered :
    vectors.all (fun vector => traceOrdered (run nonsemanticFixtureOracle vector.input).trace) = true := by
  native_decide

theorem vector_count : vectors.length = 53 := by
  native_decide

theorem call_and_restore_finalization_matches_sender_recovery :
    let ordinary := run nonsemanticFixtureOracle { baseInput with mode := .callAndRestore }
    let temporary := run nonsemanticFixtureOracle
      { baseInput with mode := .callAndRestore, deleteCallerAccount := true }
    ordinary.trace.contains .restore = true ∧
      ordinary.trace.contains .commit = true ∧
      ordinary.state.committed = true ∧
      temporary.trace.contains .restore = true ∧
      temporary.trace.contains .commit = false ∧
      temporary.state.committed = false := by
  native_decide

theorem invalid_settlement_domain_reaches_run :
    let result := run nonsemanticFixtureOracle invalidSettlementDomainInput
    result.outcome = .unmodeled .invalidSettlementDomain ∧
      result.state.settlement = none ∧
      result.state.scalarSettlement = none ∧
      result.state.receipt = none ∧
      result.state.receiptClosed = true := by
  native_decide

theorem state_traced_simple_transfer_commits_before_execution :
    let result := run nonsemanticFixtureOracle stateTracedSimpleTransfer
    result.state.commitBeforeExecution = true ∧
      result.state.committed = true ∧
      result.trace.contains .commitBeforeExecution = true := by
  native_decide

theorem environment_exception_escapes_without_epilogue :
    let result := run nonsemanticFixtureOracle environmentFailureCallAndRestore
    result.outcome = .escapedException .buildEnvironmentException ∧
      result.trace.contains .restore = false ∧
      result.trace.contains .endTxTrace = false ∧
      result.state.receiptClosed = false := by
  native_decide

private def misroutedSimpleTransferOracle : Oracle :=
  { nonsemanticFixtureOracle with
    prepareSimpleTransferFastPath := fun _ state =>
      { state, path := .simpleTransfer, topFrameOutOfGas := false } }

private def prehaltedSimpleTransferOracle : Oracle :=
  { nonsemanticFixtureOracle with
    prepareSimpleTransferFastPath := fun _ state =>
      { state, path := .simpleTransfer, topFrameOutOfGas := true } }

theorem simple_transfer_revert_fails_closed_before_value_transfer :
    let result := run nonsemanticFixtureOracle simpleTransferRevert
    result.outcome = .unmodeled .invalidExecutionRoute ∧
      result.trace.contains .simpleTransferExecution = false ∧
      result.trace.contains .payValue = false ∧
      result.state.execution = none ∧ result.state.recipientBalance = 0 := by
  native_decide

theorem forged_simple_transfer_route_fails_closed_before_value_transfer :
    let result := run misroutedSimpleTransferOracle revertedEvm
    result.outcome = .unmodeled .invalidExecutionRoute ∧
      result.trace.contains .simpleTransferExecution = false ∧
      result.trace.contains .payValue = false ∧
      result.state.execution = none ∧ result.state.recipientBalance = 0 := by
  native_decide

theorem prehalted_simple_transfer_fails_closed_before_value_transfer :
    let result := run prehaltedSimpleTransferOracle simpleTransfer
    result.outcome = .unmodeled .invalidExecutionRoute ∧
      result.trace.contains .simpleTransferExecution = false ∧
      result.trace.contains .payValue = false ∧
      result.state.execution = none ∧ result.state.recipientBalance = 0 := by
  native_decide

private def environmentMutatesRouteOracle : Oracle :=
  { nonsemanticFixtureOracle with
    buildExecutionEnvironment := fun _ state =>
      { state := { state with path := some .simpleTransfer }
        path := .evm
        topFrameOutOfGas := false
        failure := none } }

private def environmentAdvertisesSimpleTransferRouteOracle : Oracle :=
  { nonsemanticFixtureOracle with
    buildExecutionEnvironment := fun _ state =>
      { state
        path := .simpleTransfer
        topFrameOutOfGas := false
        failure := none } }

private def evmReturnsSimpleTransferOogOracle : Oracle :=
  { nonsemanticFixtureOracle with
    executeEvmCall := fun _ state =>
      { state, outcome := .simpleTransferStateOutOfGas } }

private def evmReturnsMismatchedRevertOracle : Oracle :=
  { nonsemanticFixtureOracle with
    executeEvmCall := fun _ state =>
      { state, outcome := .revert } }

private def evmMutatesControlOracle : Oracle :=
  { nonsemanticFixtureOracle with
    executeEvmCall := fun _ state =>
      { state := { state with path := some .simpleTransfer }
        outcome := .success } }

private def baselineSwapCallAndRestoreInput : Input :=
  { successfulEvm with mode := .callAndRestore }

private def baselineSwappingEnvironmentOracle : Oracle :=
  { nonsemanticFixtureOracle with
    buildExecutionEnvironment := fun _ state =>
      { state := { state with baseline := { state.baseline with senderBalance := 17 } }
        path := .evm
        topFrameOutOfGas := false
        failure := none } }

private def baselineSwappingPreparationOracle : Oracle :=
  { nonsemanticFixtureOracle with
    prepareSimpleTransferFastPath := fun _ state =>
      { state := { state with baseline := { state.baseline with senderBalance := 17 } }
        path := .evm
        topFrameOutOfGas := false } }

private def destroyFinalizingAuthorizationOracle : Oracle :=
  { nonsemanticFixtureOracle with
    processDelegations := fun input state =>
      { state := { state with destroyListFinalized := true }
        decision := input.authorization } }

private def baselineSwappingSettlementKernel (input : Input) (state : State) : Option State :=
  match settleState input state with
  | none => none
  | some settled =>
      some { settled with baseline := { settled.baseline with senderBalance := 17 } }

private def oracleEffectHookEvents : List Event :=
  [ .recoverSenderBeforeIntrinsicGas
  , .calculateIntrinsicGas
  , .validateStatic
  , .calculateEffectiveGasPrice
  , .recoverSenderIfNeeded
  , .validateSender
  , .buyGas
  , .incrementNonce
  , .commitBeforeExecution
  , .calculateAvailableGas
  , .refund
  , .payFees
  , .destroyListFinalize
  , .restore
  , .commit
  , .resetTransient ]

private def oracleEffectHookRejectsBaselineReplacement (event : Event) : Bool :=
  let before := initialState successfulEvm
  let cursor : Cursor := { state := before, trace := [] }
  match effectAt event
      (.continue { before with baseline := { before.baseline with senderBalance := 17 } }) cursor with
  | .unmodeled .inconsistentOracleEffectResult rejected =>
      rejected.trace == [event] && rejected.state == before
  | _ => false

private def earlyEffectControlMutations : List State :=
  let before := initialState successfulEvm
  [ { before with baseline := { before.baseline with senderBalance := 17 } }
  , { before with cleanupRequired := true }
  , { before with preparationSnapshot := some (preparationSnapshotOf before) }
  , { before with path := some .simpleTransfer }
  , { before with receiptClosed := true }
  , { before with headerGasUsed := 1 } ]

private def calculationHookRejectsControlMutation (mutated : State) : Bool :=
  let before := initialState successfulEvm
  let cursor : Cursor := { state := before, trace := [] }
  match effectAt .calculateAvailableGas (.continue mutated) cursor with
  | .unmodeled .inconsistentOracleEffectResult rejected =>
      rejected.trace == [.calculateAvailableGas] && rejected.state == before
  | _ => false

private def stoppedValidationHookRejectsControlMutation : Bool :=
  let before := initialState successfulEvm
  let cursor : Cursor := { state := before, trace := [] }
  match effectAt .validateStatic
      (.stop (.validation .malformedTransaction) { before with cleanupRequired := true }) cursor with
  | .unmodeled .inconsistentOracleEffectResult rejected =>
      rejected.trace == [.validateStatic] && rejected.state == before
  | _ => false

theorem environment_route_mutation_fails_closed_before_value_transfer :
    let result := run environmentMutatesRouteOracle exceptionalEvm
    result.outcome = .unmodeled .inconsistentEnvironmentResult ∧
      result.trace.contains .recipientStateCharge = false ∧
      result.trace.contains .payValue = false ∧ result.trace.contains .vmExecution = false ∧
      result.state.path = some .evm ∧ result.state.recipientBalance = 0 := by
  native_decide

theorem environment_advertised_route_switch_fails_closed_before_value_transfer :
    let result := run environmentAdvertisesSimpleTransferRouteOracle exceptionalEvm
    result.outcome = .unmodeled .invalidExecutionRoute ∧
      result.trace.contains .recipientStateCharge = false ∧
      result.trace.contains .payValue = false ∧ result.trace.contains .vmExecution = false ∧
      result.state.path = some .evm ∧ result.state.recipientBalance = 0 := by
  native_decide

theorem evm_simple_transfer_directive_fails_closed_without_state_adoption :
    let result := run evmReturnsSimpleTransferOogOracle successfulEvm
    result.outcome = .unmodeled .invalidEvmExecutionDirective ∧
      result.trace.contains .payValue = true ∧ result.trace.contains .vmExecution = true ∧
      result.state.execution = none ∧ result.state.recipientBalance = 0 ∧
      result.state.settlement = none ∧ result.state.scalarSettlement = none := by
  native_decide

theorem evm_directive_mismatch_fails_closed_without_state_adoption :
    let result := run evmReturnsMismatchedRevertOracle successfulEvm
    result.outcome = .unmodeled .invalidEvmExecutionDirective ∧
      result.state.execution = none ∧ result.state.recipientBalance = 0 ∧
      result.state.settlement = none := by
  native_decide

theorem evm_control_mutation_fails_closed_without_state_adoption :
    let result := run evmMutatesControlOracle successfulEvm
    result.outcome = .unmodeled .inconsistentEvmExecutionResult ∧
      result.state.path = some .evm ∧ result.state.execution = none ∧
      result.state.recipientBalance = 0 ∧ result.state.settlement = none := by
  native_decide

theorem baseline_substitution_fails_closed_before_call_and_restore_cleanup :
    let result := run baselineSwappingEnvironmentOracle baselineSwapCallAndRestoreInput
    result.outcome = .unmodeled .inconsistentEnvironmentResult ∧
      result.trace.contains .restore = true ∧ result.state.baseline.senderBalance = 1_000_000 ∧
      result.state.senderBalance = 1_000_000 := by
  native_decide

theorem preparation_baseline_substitution_fails_closed_before_call_and_restore_cleanup :
    let result := run baselineSwappingPreparationOracle baselineSwapCallAndRestoreInput
    result.outcome = .unmodeled .inconsistentPreparationResult ∧
      result.trace.contains .restore = true ∧ result.trace.contains .calculateAvailableGas = false ∧
      result.state.baseline.senderBalance = 1_000_000 ∧ result.state.senderBalance = 1_000_000 := by
  native_decide

theorem authorization_destroy_finalization_mutation_fails_closed_without_state_adoption :
    let input := { successfulEvm with hasAuthorizationList := true, authorization := .applied 1 }
    let result := run destroyFinalizingAuthorizationOracle input
    result.outcome = .unmodeled .inconsistentAuthorizationResult ∧
      result.trace.contains .processDelegations = true ∧
      result.trace.contains .buildExecutionEnvironment = false ∧
      result.state.destroyListFinalized = false ∧ result.state.authorization = none := by
  native_decide

theorem settlement_baseline_substitution_fails_closed_without_state_adoption :
    let result := runWithTransactionKernels admitCreate requiresEvmRecipientStateCharge
      baselineSwappingSettlementKernel nonsemanticFixtureOracle successfulEvm
    result.outcome = .unmodeled .inconsistentSettlementResult ∧
      result.state.baseline.senderBalance = 1_000_000 ∧ result.state.settlement = none ∧
      result.state.scalarSettlement = none ∧ result.state.receipt = none := by
  native_decide

theorem all_oracle_effect_hooks_reject_baseline_replacement :
    oracleEffectHookEvents.all oracleEffectHookRejectsBaselineReplacement = true := by
  native_decide

theorem oracle_effect_hook_count : oracleEffectHookEvents.length = 16 := by
  native_decide

theorem early_effect_control_mutations_fail_closed_without_state_adoption :
    earlyEffectControlMutations.all calculationHookRejectsControlMutation = true ∧
      stoppedValidationHookRejectsControlMutation = true := by
  native_decide

theorem early_effect_control_mutation_count : earlyEffectControlMutations.length = 6 := by
  native_decide

theorem authorization_oog_partial_mutation_then_restore :
    let before := initialState authorizationOutOfGas
    let partialState := (processDelegationsEffect authorizationOutOfGas before).state
    let restored := restorePreparationSnapshot (preparationSnapshotOf before) partialState
    partialState.durableWorld = 1 ∧
      partialState.reversibleWorld = 1 ∧
      partialState.logs = [1] ∧
      partialState.gas.gasLeft = 50 ∧
      restored.durableWorld = before.durableWorld ∧
      restored.reversibleWorld = before.reversibleWorld ∧
      restored.senderNonce = before.senderNonce ∧
      restored.beneficiaryBalance = before.beneficiaryBalance ∧
      restored.feeCollectorBalance = before.feeCollectorBalance ∧
      restored.logs = before.logs ∧
      restored.destroyList = before.destroyList ∧
      restored.destroyListFinalized = before.destroyListFinalized ∧
      restored.gas.gasLeft = before.gas.gasLeft := by
  native_decide

theorem all_vector_phase_traces_are_ordered :
    vectors.all (fun vector =>
      phaseTraceOrdered (phaseTrace (run nonsemanticFixtureOracle vector.input).trace)) = true := by
  native_decide

/-!
### CREATE destination admission vectors

These vectors exercise the typed transaction-processor boundary in
`TransactionReference`, independently of the broad lifecycle fixture above.
Expected classifications and gas records are literal values: they do not
call `classifyCreateDestination`, `createStateCharge`, or `admitCreate`.
-/

private def admissionGas (gasLeft stateReservoir stateFromGasLeft stateUsed : Nat) : GasState :=
  { gasLeft, stateReservoir, stateFromGasLeft, stateUsed, refundCounter := 0 }

private def freshDestination : CreateDestinationOracle :=
  { accountExists := false
    balance := 0
    nonce := 0
    codeEmpty := true
    storageNonEmpty := false
    collision := .none }

private def storageOnlyDestination : CreateDestinationOracle :=
  { accountExists := false
    balance := 0
    nonce := 0
    codeEmpty := true
    storageNonEmpty := true
    collision := .storageOnly }

private def balanceOnlyDestination : CreateDestinationOracle :=
  { accountExists := true
    balance := 1
    nonce := 0
    codeEmpty := true
    storageNonEmpty := false
    collision := .none }

private def nonceCollisionDestination : CreateDestinationOracle :=
  { accountExists := true
    balance := 0
    nonce := 1
    codeEmpty := true
    storageNonEmpty := false
    collision := .nonZeroNonce }

private def codeCollisionDestination : CreateDestinationOracle :=
  { accountExists := true
    balance := 0
    nonce := 0
    codeEmpty := false
    storageNonEmpty := false
    collision := .nonEmptyCode }

private def balanceBearingStorageDestination : CreateDestinationOracle :=
  { accountExists := true
    balance := 1
    nonce := 0
    codeEmpty := true
    storageNonEmpty := true
    collision := .storageOnly }

private def balanceBearingStorageExpectedDestination : CreateDestinationOracle :=
  { accountExists := true
    balance := 1
    nonce := 0
    codeEmpty := true
    storageNonEmpty := true
    collision := .storageOnly }

private def invalidNonceCollisionDestination : CreateDestinationOracle :=
  { accountExists := false
    balance := 0
    nonce := 1
    codeEmpty := true
    storageNonEmpty := false
    collision := .nonZeroNonce }

private def invalidNonceWithoutCollisionDestination : CreateDestinationOracle :=
  { accountExists := true
    balance := 0
    nonce := 1
    codeEmpty := true
    storageNonEmpty := false
    collision := .none }

private def invalidCodeWithoutCollisionDestination : CreateDestinationOracle :=
  { accountExists := true
    balance := 0
    nonce := 0
    codeEmpty := false
    storageNonEmpty := false
    collision := .none }

private def invalidStorageWithoutCollisionDestination : CreateDestinationOracle :=
  { accountExists := false
    balance := 0
    nonce := 0
    codeEmpty := true
    storageNonEmpty := true
    collision := .none }

private def invalidLogicallyEmptyExistentDestination : CreateDestinationOracle :=
  { accountExists := true
    balance := 0
    nonce := 0
    codeEmpty := true
    storageNonEmpty := false
    collision := .none }

private def invalidStorageOnlyExistentDestination : CreateDestinationOracle :=
  { accountExists := true
    balance := 0
    nonce := 0
    codeEmpty := true
    storageNonEmpty := true
    collision := .storageOnly }

private def literalClassification (destination : CreateDestinationOracle)
    (status : CreateDestinationStatus) : CreateDestinationClassification :=
  { oracle := destination
    status
    destinationReadCount := 1 }

private def literalAdmission (status : CreateAdmissionStatus)
    (destination : CreateDestinationOracle)
    (classificationStatus : CreateDestinationStatus) (gas : GasState)
    (stateCharge : Nat) (stateChargeApplied : Bool) (stateChargeRefilled : Nat)
    (executionGasCleared childEntered : Bool) : CreateAdmissionResult :=
  { status
    classification := literalClassification destination classificationStatus
    gas
    stateCharge
    stateChargeApplied
    stateChargeRefilled
    executionGasCleared
    childEntered }

structure CreateAdmissionVector where
  name : String
  gas : GasState
  newAccountStateGas : Nat
  destination : CreateDestinationOracle
  expected : CreateAdmissionResult
  deriving DecidableEq, Repr

def createAdmissionVectors : List CreateAdmissionVector :=
  [ { name := "fresh target charges reservoir before child entry"
      gas := admissionGas 9 7 0 0
      newAccountStateGas := 7
      destination := freshDestination
      expected := literalAdmission .entered freshDestination .dead
        (admissionGas 9 0 0 7) 7 true 0 false true }
  , { name := "fresh target spills state charge into execution gas"
      gas := admissionGas 9 2 0 0
      newAccountStateGas := 7
      destination := freshDestination
      expected := literalAdmission .entered freshDestination .dead
        (admissionGas 4 0 5 7) 7 true 0 false true }
  , { name := "one-short fresh state charge is exceptional OOG"
      gas := admissionGas 0 6 0 0
      newAccountStateGas := 7
      destination := freshDestination
      expected := literalAdmission .outOfGas freshDestination .dead
        (admissionGas 0 6 0 0) 7 false 0 true false }
  , { name := "storage-only collision charges then refills exact reservoir"
      gas := admissionGas 13 7 0 0
      newAccountStateGas := 7
      destination := storageOnlyDestination
      expected := literalAdmission .collision storageOnlyDestination .dead
        (admissionGas 0 7 0 0) 7 true 7 true false }
  , { name := "storage-only collision refills a gas-left spill before burn"
      gas := admissionGas 13 2 0 0
      newAccountStateGas := 7
      destination := storageOnlyDestination
      expected := literalAdmission .collision storageOnlyDestination .dead
        (admissionGas 0 2 0 0) 7 true 7 true false }
  , { name := "existing balance-only target has no new-account charge"
      gas := admissionGas 5 3 0 11
      newAccountStateGas := 7
      destination := balanceOnlyDestination
      expected := literalAdmission .entered balanceOnlyDestination .existent
        (admissionGas 5 3 0 11) 0 false 0 false true }
  , { name := "existing nonce collision has no new-account charge"
      gas := admissionGas 5 3 0 11
      newAccountStateGas := 7
      destination := nonceCollisionDestination
      expected := literalAdmission .collision nonceCollisionDestination .existent
        (admissionGas 0 3 0 11) 0 false 0 true false }
  , { name := "existing code collision has no new-account charge"
      gas := admissionGas 5 3 0 11
      newAccountStateGas := 7
      destination := codeCollisionDestination
      expected := literalAdmission .collision codeCollisionDestination .existent
        (admissionGas 0 3 0 11) 0 false 0 true false }
  , { name := "balance-bearing storage collision is existent and has no new-account charge"
      gas := admissionGas 5 3 0 11
      newAccountStateGas := 7
      destination := balanceBearingStorageDestination
      expected := literalAdmission .collision balanceBearingStorageExpectedDestination .existent
        (admissionGas 0 3 0 11) 0 false 0 true false }
  , { name := "storage fact without a collision fails closed"
      gas := admissionGas 5 3 0 11
      newAccountStateGas := 7
      destination := invalidStorageWithoutCollisionDestination
      expected := literalAdmission .invalidDestination invalidStorageWithoutCollisionDestination .dead
        (admissionGas 5 3 0 11) 0 false 0 false false }
  , { name := "logically empty existent account fact fails closed"
      gas := admissionGas 5 3 0 11
      newAccountStateGas := 7
      destination := invalidLogicallyEmptyExistentDestination
      expected := literalAdmission .invalidDestination invalidLogicallyEmptyExistentDestination .existent
        (admissionGas 5 3 0 11) 0 false 0 false false }
  , { name := "storage alone does not establish logical account existence"
      gas := admissionGas 5 3 0 11
      newAccountStateGas := 7
      destination := invalidStorageOnlyExistentDestination
      expected := literalAdmission .invalidDestination invalidStorageOnlyExistentDestination .existent
        (admissionGas 5 3 0 11) 0 false 0 false false }
  , { name := "inconsistent nonce collision account fact fails closed"
      gas := admissionGas 5 3 0 11
      newAccountStateGas := 7
      destination := invalidNonceCollisionDestination
      expected := literalAdmission .invalidDestination invalidNonceCollisionDestination .dead
        (admissionGas 5 3 0 11) 0 false 0 false false }
  , { name := "nonzero nonce without collision fails closed"
      gas := admissionGas 5 3 0 11
      newAccountStateGas := 7
      destination := invalidNonceWithoutCollisionDestination
      expected := literalAdmission .invalidDestination invalidNonceWithoutCollisionDestination .existent
        (admissionGas 5 3 0 11) 0 false 0 false false }
  , { name := "nonempty code without collision fails closed"
      gas := admissionGas 5 3 0 11
      newAccountStateGas := 7
      destination := invalidCodeWithoutCollisionDestination
      expected := literalAdmission .invalidDestination invalidCodeWithoutCollisionDestination .existent
        (admissionGas 5 3 0 11) 0 false 0 false false }
  , { name := "fresh target with zero configurable charge still enters"
      gas := admissionGas 5 0 0 0
      newAccountStateGas := 0
      destination := freshDestination
      expected := literalAdmission .entered freshDestination .dead
        (admissionGas 5 0 0 0) 0 true 0 false true }
  , { name := "storage-only zero-charge collision still clears execution gas"
      gas := admissionGas 5 0 0 0
      newAccountStateGas := 0
      destination := storageOnlyDestination
      expected := literalAdmission .collision storageOnlyDestination .dead
        (admissionGas 0 0 0 0) 0 true 0 true false } ]

private def passesCreateAdmission (vector : CreateAdmissionVector) : Bool :=
  admitCreate vector.gas vector.newAccountStateGas vector.destination = vector.expected

theorem all_create_admission_vectors_pass :
    createAdmissionVectors.all passesCreateAdmission = true := by
  native_decide

theorem create_admission_vector_count : createAdmissionVectors.length = 17 := by
  native_decide

theorem create_admission_boundary_theorems_hold :
    (admitCreate (admissionGas 0 6 0 0) 7 freshDestination).status = .outOfGas ∧
      (admitCreate (admissionGas 13 7 0 0) 7 storageOnlyDestination).stateChargeRefilled = 7 ∧
      (admitCreate (admissionGas 5 3 0 11) 7 balanceOnlyDestination).stateCharge = 0 ∧
      (admitCreate (admissionGas 5 3 0 11) 7 nonceCollisionDestination).stateCharge = 0 ∧
      CreateDestinationOracleConsistent storageOnlyDestination ∧
      CreateDestinationOracleConsistent balanceBearingStorageDestination ∧
      ¬ CreateDestinationOracleConsistent invalidStorageWithoutCollisionDestination ∧
      ¬ CreateDestinationOracleConsistent invalidLogicallyEmptyExistentDestination ∧
      ¬ CreateDestinationOracleConsistent invalidStorageOnlyExistentDestination ∧
      ¬ CreateDestinationOracleConsistent invalidNonceWithoutCollisionDestination ∧
      ¬ CreateDestinationOracleConsistent invalidCodeWithoutCollisionDestination ∧
      (admitCreate (admissionGas 5 3 0 11) 7 invalidStorageWithoutCollisionDestination).status =
        .invalidDestination ∧
      (admitCreate (admissionGas 5 3 0 11) 7 invalidLogicallyEmptyExistentDestination).status =
        .invalidDestination ∧
      (admitCreate (admissionGas 5 3 0 11) 7 invalidStorageOnlyExistentDestination).status =
        .invalidDestination ∧
      (admitCreate (admissionGas 5 3 0 11) 7 invalidNonceWithoutCollisionDestination).status =
        .invalidDestination ∧
      (admitCreate (admissionGas 5 3 0 11) 7 invalidCodeWithoutCollisionDestination).status =
        .invalidDestination ∧
      (admitCreate (admissionGas 5 3 0 11) 7 balanceBearingStorageDestination).stateCharge = 0 := by
  native_decide

/-!
### CREATE admission through the full transaction lifecycle

These vectors use `run`, not the detached admission helper.  Their expected
records are literal and include the saved single-read classification, so both
trace order and propagated gas state must agree.
-/

structure CreateAdmissionRunObservation where
  outcome : LifecycleOutcome
  trace : List Event
  gasLeft : Nat
  stateGasReservoir : Nat
  stateGasFromGasLeft : Nat
  stateGasUsed : Nat
  topFrameOutOfGas : Bool
  execution : Option ExecutionDirective
  admission : Option CreateAdmissionResult
  settled : Bool
  receiptObserved : Bool
  dimensionalSettlement : Option TransactionGas.Settlement
  scalarSettlement : Option TransactionSettlement.Result
  senderRefund : Nat
  senderBalance : Nat
  tipPaid : Nat
  burnedFees : Nat
  headerGasUsed : Nat
  blockCumulativeExecutionGas : Nat
  blockCumulativeStateGas : Nat
  cumulativeReceiptGas : Nat
  receipt : Option ReceiptObservation
  deriving DecidableEq, Repr

def observeCreateAdmissionRun (result : Run) : CreateAdmissionRunObservation :=
  { outcome := result.outcome
    trace := result.trace
    gasLeft := result.state.gas.gasLeft
    stateGasReservoir := result.state.gas.stateGasReservoir
    stateGasFromGasLeft := result.state.stateGasFromGasLeft
    stateGasUsed := result.state.gas.evmStateGasUsed
    topFrameOutOfGas := result.state.topFrameOutOfGas
    execution := result.state.execution
    admission := result.state.createAdmission
    settled := result.state.settlement.isSome
    receiptObserved := result.state.receipt.isSome
    dimensionalSettlement := result.state.settlement
    scalarSettlement := result.state.scalarSettlement
    senderRefund := match result.state.scalarSettlement with
      | some settlement => result.state.gas.txGas - settlement.spentGas
      | none => 0
    senderBalance := result.state.senderBalance
    tipPaid := result.state.beneficiaryBalance - result.state.baseline.beneficiaryBalance
    burnedFees := result.state.feeCollectorBalance - result.state.baseline.feeCollectorBalance
    headerGasUsed := result.state.headerGasUsed
    blockCumulativeExecutionGas := result.state.blockCumulativeExecutionGas
    blockCumulativeStateGas := result.state.blockCumulativeStateGas
    cumulativeReceiptGas := result.state.cumulativeReceiptGas
    receipt := result.state.receipt }

/--
The halt matrix additionally observes the production-shaped state reset that
happens before scalar and dimensional settlement consume the transaction.
-/
structure Eip8037HaltRunObservation where
  lifecycle : CreateAdmissionRunObservation
  prePreparationGasRestore : Option PrePreparationGasSnapshot
  preparationSnapshotPresent : Bool
  topExecutionGasSnapshot : Option PrePreparationGasSnapshot
  haltBaseline : Option Eip8037HaltBaseline
  deriving DecidableEq, Repr

def observeEip8037HaltRun (result : Run) : Eip8037HaltRunObservation :=
  { lifecycle := observeCreateAdmissionRun result
    prePreparationGasRestore := result.state.preparationGasRestore
    preparationSnapshotPresent := result.state.preparationSnapshot.isSome
    topExecutionGasSnapshot := result.state.topExecutionGasSnapshot
    haltBaseline := result.state.eip8037HaltBaseline }

private def dimensionalSettlement
    (beforeRefund gasRefund afterRefund paidGas stateGas executionGas : Nat) :
    TransactionGas.Settlement :=
  { gasUsedBeforeRefund := beforeRefund
    gasRefund
    gasUsedAfterRefund := afterRefund
    paidGas
    stateGas
    executionGas }

private def scalarSettlement
    (spentGas operationGas blockGas blockStateGas maxUsedGas gasRefund : Nat) :
    TransactionSettlement.Result :=
  { spentGas
    operationGas
    blockGas
    blockStateGas
    maxUsedGas
    gasRefund }

private def runReceipt (status : ReceiptStatus) (gasUsed cumulativeGasUsed : Nat)
    (logs : List Nat) : ReceiptObservation :=
  { status
    gasUsed
    cumulativeGasUsed
    logs
    stateRoot := none }

private def creationRunPrefix : List Event :=
  ordinaryPrefix false false true ++
    [.buildExecutionEnvironment, .topExecutionSnapshot
    , .createDestinationRead]

private def creationRunSettlement : List Event :=
  [.refund, .headerGas, .payFees, .commit, .receiptStart, .receiptObserve, .endTxTrace]

private def reservoirHaltInput : Input :=
  { baseInput with
    initialization :=
      { txGas := 35
        intrinsicGas := 20
        txMaxGasLimit := 33 }
    executionGas := { baseGas with gasLeft := 0, stateGasReservoir := 2 }
    fees :=
      { effectiveGasPrice := 3
        premiumPerGas := 1
        baseFeePerGas := 2
        blobBaseFee := 0
        feeCollectorEnabled := true
        overflow := false } }

private def reservoirPrePreparationGas : PrePreparationGasSnapshot :=
  { gas :=
      { txGas := 35
        gasLeft := 13
        stateGasReservoir := 2
        refundCounter := 0
        evmStateGasUsed := 0
        calldataFloorGasCost := 0 }
    stateGasFromGasLeft := 0 }

private def basePrePreparationGas : PrePreparationGasSnapshot :=
  { gas :=
      { txGas := 100
        gasLeft := 80
        stateGasReservoir := 0
        refundCounter := 0
        evmStateGasUsed := 0
        calldataFloorGasCost := 0 }
    stateGasFromGasLeft := 0 }

private def reservoirHaltBaseline : Eip8037HaltBaseline :=
  { stateGasReservoir := 2
    stateGasFromGasLeft := 0
    stateGasUsed := 0 }

private def reservoirHaltExpected
    (outcome : LifecycleOutcome) (trace : List Event) (topFrameOutOfGas : Bool)
    (execution : Option ExecutionDirective) (admission : Option CreateAdmissionResult) :
    CreateAdmissionRunObservation :=
  { outcome
    trace
    gasLeft := 0
    stateGasReservoir := 2
    stateGasFromGasLeft := 0
    stateGasUsed := 0
    topFrameOutOfGas
    execution
    admission
    settled := true
    receiptObserved := true
    dimensionalSettlement := some (dimensionalSettlement 33 0 33 33 0 33)
    scalarSettlement := some (scalarSettlement 33 33 33 0 33 0)
    senderRefund := 2
    senderBalance := 999_901
    tipPaid := 33
    burnedFees := 66
    headerGasUsed := 33
    blockCumulativeExecutionGas := 33
    blockCumulativeStateGas := 0
    cumulativeReceiptGas := 33
    receipt := some (runReceipt .failure 33 33 []) }

private def haltRunExpected (lifecycle : CreateAdmissionRunObservation)
    (prePreparationGasRestore : Option PrePreparationGasSnapshot)
    (preparationSnapshotPresent : Bool) : Eip8037HaltRunObservation :=
  { lifecycle
    prePreparationGasRestore
    preparationSnapshotPresent
    topExecutionGasSnapshot := some reservoirPrePreparationGas
    haltBaseline := some reservoirHaltBaseline }

private def freshRunInput : Input :=
  { baseInput with
    entryKind := .contractCreation
    createDestination := freshDestination
    newAccountStateGas := 7
    executionGas :=
      { baseGas with gasLeft := 73, stateGasFromGasLeft := 7, evmStateGasUsed := 7 } }

private def storageOnlyRunInput : Input :=
  { reservoirHaltInput with
    entryKind := .contractCreation
    createDestination := storageOnlyDestination
    newAccountStateGas := 7
    execution := .collision }

private def balanceBearingStorageRunInput : Input :=
  { reservoirHaltInput with
    entryKind := .contractCreation
    createDestination := balanceBearingStorageDestination
    newAccountStateGas := 7
    execution := .collision }

private def oneShortRunInput : Input :=
  { reservoirHaltInput with
    entryKind := .contractCreation
    createDestination := freshDestination
    newAccountStateGas := 16
    execution := .createStateOutOfGas }

private def ordinaryExceptionHaltRunInput : Input :=
  { reservoirHaltInput with
    execution := .exception .stackUnderflow
    executionGas :=
      { baseGas with
        gasLeft := 9
        stateGasReservoir := 1
        stateGasFromGasLeft := 6
        refundCounter := 8
        evmStateGasUsed := 7 } }

private def topFrameHaltRunInput : Input :=
  { reservoirHaltInput with execution := .topFrameOutOfGas }

private def initialTopFrameHaltRunInput : Input :=
  { reservoirHaltInput with
    recipientStateCharge := 16 }

private def preparationHaltRunInput : Input :=
  { reservoirHaltInput with
    execution := .preparationOutOfGas
    executionGas :=
      { baseGas with
        gasLeft := 9
        stateGasReservoir := 1
        stateGasFromGasLeft := 5
        evmStateGasUsed := 7 } }

private def createTopFrameHaltRunInput : Input :=
  { reservoirHaltInput with
    entryKind := .contractCreation
    execution := .topFrameOutOfGas }

private def depositInvalidCodeHaltRunInput : Input :=
  { reservoirHaltInput with
    entryKind := .contractCreation
    execution := .depositInvalidCode
    codeInsertExecutionRefund := 7
    executionGas :=
      { baseGas with
        gasLeft := 9
        stateGasReservoir := 1
        stateGasFromGasLeft := 6
        refundCounter := 8
        evmStateGasUsed := 7 } }

private def depositOutOfGasHaltRunInput : Input :=
  { depositInvalidCodeHaltRunInput with execution := .depositOutOfGas }

private def ordinaryExceptionHaltTrace : List Event :=
  ordinaryPrefix false false true ++
    [.buildExecutionEnvironment, .recipientStateCharge, .topExecutionSnapshot
    , .payValue, .vmExecution, .executionRollback] ++ creationRunSettlement

private def ordinaryTopFrameHaltTrace : List Event :=
  ordinaryPrefix false false true ++
    [.buildExecutionEnvironment, .recipientStateCharge, .topExecutionSnapshot
    , .topFrameOutOfGas] ++ creationRunSettlement

private def createTopFrameHaltTrace : List Event :=
  ordinaryPrefix false false true ++
    [.buildExecutionEnvironment, .topExecutionSnapshot, .topFrameOutOfGas] ++
      creationRunSettlement

private def codeDepositHaltTrace : List Event :=
  creationRunPrefix ++
    [.collisionCheck, .payValue, .vmExecution, .deployment, .executionRollback] ++
      creationRunSettlement

private def balanceOnlyRunInput : Input :=
  { baseInput with
    entryKind := .contractCreation
    createDestination := balanceOnlyDestination
    newAccountStateGas := 7 }

private def nonceCollisionRunInput : Input :=
  { baseInput with
    entryKind := .contractCreation
    createDestination := nonceCollisionDestination
    newAccountStateGas := 7
    execution := .collision }

private def codeCollisionRunInput : Input :=
  { baseInput with
    entryKind := .contractCreation
    createDestination := codeCollisionDestination
    newAccountStateGas := 7
    execution := .collision }

private def invalidDestinationRunInput : Input :=
  { baseInput with
    entryKind := .contractCreation
    createDestination := invalidNonceCollisionDestination
    newAccountStateGas := 7 }

private def invalidStorageWithoutCollisionRunInput : Input :=
  { baseInput with
    entryKind := .contractCreation
    createDestination := invalidStorageWithoutCollisionDestination
    newAccountStateGas := 7 }

private def invalidLogicallyEmptyExistentRunInput : Input :=
  { baseInput with
    entryKind := .contractCreation
    createDestination := invalidLogicallyEmptyExistentDestination
    newAccountStateGas := 7 }

private def invalidStorageOnlyExistentRunInput : Input :=
  { baseInput with
    entryKind := .contractCreation
    createDestination := invalidStorageOnlyExistentDestination
    newAccountStateGas := 7 }

private def invalidNonceWithoutCollisionRunInput : Input :=
  { baseInput with
    entryKind := .contractCreation
    createDestination := invalidNonceWithoutCollisionDestination
    newAccountStateGas := 7 }

private def invalidCodeWithoutCollisionRunInput : Input :=
  { baseInput with
    entryKind := .contractCreation
    createDestination := invalidCodeWithoutCollisionDestination
    newAccountStateGas := 7 }

private def inconsistentAdmissionDirectiveRunInput : Input :=
  { baseInput with
    entryKind := .contractCreation
    createDestination := storageOnlyDestination
    newAccountStateGas := 7 }

private def freshRunExpected : CreateAdmissionRunObservation :=
  { outcome := .executed .success none
    trace := creationRunPrefix ++
      [.createStateCharge, .collisionCheck, .payValue, .vmExecution, .deployment] ++
        creationRunSettlement
    gasLeft := 73
    stateGasReservoir := 0
    stateGasFromGasLeft := 7
    stateGasUsed := 7
    topFrameOutOfGas := false
    execution := some .success
    admission := some (literalAdmission .entered freshDestination .dead
      (admissionGas 73 0 7 7) 7 true 0 false true)
    settled := true
    receiptObserved := true
    dimensionalSettlement := some (dimensionalSettlement 27 0 27 27 7 20)
    scalarSettlement := some (scalarSettlement 27 27 20 7 27 0)
    senderRefund := 73
    senderBalance := 999_968
    tipPaid := 27
    burnedFees := 0
    headerGasUsed := 20
    blockCumulativeExecutionGas := 20
    blockCumulativeStateGas := 7
    cumulativeReceiptGas := 27
    receipt := some (runReceipt .success 27 27 [5]) }

private def storageOnlyRunExpected : CreateAdmissionRunObservation :=
  { outcome := .executed .failure (some .collision)
    trace := creationRunPrefix ++
      [.createStateCharge, .collisionCheck, .executionRollback] ++ creationRunSettlement
    gasLeft := 0
    stateGasReservoir := 2
    stateGasFromGasLeft := 0
    stateGasUsed := 0
    topFrameOutOfGas := false
    execution := some .collision
    admission := some (literalAdmission .collision storageOnlyDestination .dead
      (admissionGas 0 2 0 0) 7 true 7 true false)
    settled := true
    receiptObserved := true
    dimensionalSettlement := some (dimensionalSettlement 33 0 33 33 0 33)
    scalarSettlement := some (scalarSettlement 33 33 33 0 33 0)
    senderRefund := 2
    senderBalance := 999_901
    tipPaid := 33
    burnedFees := 66
    headerGasUsed := 33
    blockCumulativeExecutionGas := 33
    blockCumulativeStateGas := 0
    cumulativeReceiptGas := 33
    receipt := some (runReceipt .failure 33 33 []) }

private def balanceBearingStorageRunExpected : CreateAdmissionRunObservation :=
  { outcome := .executed .failure (some .collision)
    trace := creationRunPrefix ++ [.collisionCheck, .executionRollback] ++ creationRunSettlement
    gasLeft := 0
    stateGasReservoir := 2
    stateGasFromGasLeft := 0
    stateGasUsed := 0
    topFrameOutOfGas := false
    execution := some .collision
    admission := some (literalAdmission .collision balanceBearingStorageExpectedDestination .existent
      (admissionGas 0 2 0 0) 0 false 0 true false)
    settled := true
    receiptObserved := true
    dimensionalSettlement := some (dimensionalSettlement 33 0 33 33 0 33)
    scalarSettlement := some (scalarSettlement 33 33 33 0 33 0)
    senderRefund := 2
    senderBalance := 999_901
    tipPaid := 33
    burnedFees := 66
    headerGasUsed := 33
    blockCumulativeExecutionGas := 33
    blockCumulativeStateGas := 0
    cumulativeReceiptGas := 33
    receipt := some (runReceipt .failure 33 33 []) }

private def oneShortRunExpected : CreateAdmissionRunObservation :=
  reservoirHaltExpected (.executed .failure (some .outOfGas))
    (creationRunPrefix ++
      [.createStateCharge, .topFrameOutOfGas, .executionRollback] ++ creationRunSettlement)
    true (some .createStateOutOfGas)
    (some (literalAdmission .outOfGas freshDestination .dead
      (admissionGas 0 2 0 0) 16 false 0 true false))

private def ordinaryExceptionHaltRunExpected : CreateAdmissionRunObservation :=
  reservoirHaltExpected (.executed .failure (some .stackUnderflow))
    ordinaryExceptionHaltTrace false (some (.exception .stackUnderflow)) none

private def topFrameHaltRunExpected : CreateAdmissionRunObservation :=
  reservoirHaltExpected (.executed .failure (some .outOfGas))
    ordinaryTopFrameHaltTrace true (some .topFrameOutOfGas) none

private def initialTopFrameHaltRunExpected : CreateAdmissionRunObservation :=
  reservoirHaltExpected (.executed .failure (some .outOfGas))
    ordinaryTopFrameHaltTrace true (some .topFrameOutOfGas) none

private def preparationHaltRunExpected : CreateAdmissionRunObservation :=
  reservoirHaltExpected (.executed .failure (some .outOfGas))
    ordinaryTopFrameHaltTrace true (some .preparationOutOfGas) none

private def createTopFrameHaltRunExpected : CreateAdmissionRunObservation :=
  reservoirHaltExpected (.executed .failure (some .outOfGas))
    createTopFrameHaltTrace true (some .topFrameOutOfGas) none

private def depositInvalidCodeHaltRunExpected : CreateAdmissionRunObservation :=
  reservoirHaltExpected (.executed .failure (some .invalidCode))
    codeDepositHaltTrace false (some .depositInvalidCode)
    (some (literalAdmission .entered freshDestination .dead
      (admissionGas 13 2 0 0) 0 true 0 false true))

private def depositOutOfGasHaltRunExpected : CreateAdmissionRunObservation :=
  reservoirHaltExpected (.executed .failure (some .outOfGas))
    codeDepositHaltTrace false (some .depositOutOfGas)
    (some (literalAdmission .entered freshDestination .dead
      (admissionGas 13 2 0 0) 0 true 0 false true))

private def balanceOnlyRunExpected : CreateAdmissionRunObservation :=
  { outcome := .executed .success none
    trace := creationRunPrefix ++
      [.collisionCheck, .payValue, .vmExecution, .deployment] ++ creationRunSettlement
    gasLeft := 80
    stateGasReservoir := 0
    stateGasFromGasLeft := 0
    stateGasUsed := 0
    topFrameOutOfGas := false
    execution := some .success
    admission := some (literalAdmission .entered balanceOnlyDestination .existent
      (admissionGas 80 0 0 0) 0 false 0 false true)
    settled := true
    receiptObserved := true
    dimensionalSettlement := some (dimensionalSettlement 20 0 20 20 0 20)
    scalarSettlement := some (scalarSettlement 20 20 20 0 20 0)
    senderRefund := 80
    senderBalance := 999_975
    tipPaid := 20
    burnedFees := 0
    headerGasUsed := 20
    blockCumulativeExecutionGas := 20
    blockCumulativeStateGas := 0
    cumulativeReceiptGas := 20
    receipt := some (runReceipt .success 20 20 [5]) }

private def nonceCollisionRunExpected : CreateAdmissionRunObservation :=
  { outcome := .executed .failure (some .collision)
    trace := creationRunPrefix ++ [.collisionCheck, .executionRollback] ++ creationRunSettlement
    gasLeft := 0
    stateGasReservoir := 0
    stateGasFromGasLeft := 0
    stateGasUsed := 0
    topFrameOutOfGas := false
    execution := some .collision
    admission := some (literalAdmission .collision nonceCollisionDestination .existent
      (admissionGas 0 0 0 0) 0 false 0 true false)
    settled := true
    receiptObserved := true
    dimensionalSettlement := some (dimensionalSettlement 100 0 100 100 0 100)
    scalarSettlement := some (scalarSettlement 100 100 100 0 100 0)
    senderRefund := 0
    senderBalance := 999_900
    tipPaid := 100
    burnedFees := 0
    headerGasUsed := 100
    blockCumulativeExecutionGas := 100
    blockCumulativeStateGas := 0
    cumulativeReceiptGas := 100
    receipt := some (runReceipt .failure 100 100 []) }

private def codeCollisionRunExpected : CreateAdmissionRunObservation :=
  { outcome := .executed .failure (some .collision)
    trace := creationRunPrefix ++ [.collisionCheck, .executionRollback] ++ creationRunSettlement
    gasLeft := 0
    stateGasReservoir := 0
    stateGasFromGasLeft := 0
    stateGasUsed := 0
    topFrameOutOfGas := false
    execution := some .collision
    admission := some (literalAdmission .collision codeCollisionDestination .existent
      (admissionGas 0 0 0 0) 0 false 0 true false)
    settled := true
    receiptObserved := true
    dimensionalSettlement := some (dimensionalSettlement 100 0 100 100 0 100)
    scalarSettlement := some (scalarSettlement 100 100 100 0 100 0)
    senderRefund := 0
    senderBalance := 999_900
    tipPaid := 100
    burnedFees := 0
    headerGasUsed := 100
    blockCumulativeExecutionGas := 100
    blockCumulativeStateGas := 0
    cumulativeReceiptGas := 100
    receipt := some (runReceipt .failure 100 100 []) }

private def invalidDestinationRunExpected : CreateAdmissionRunObservation :=
  { outcome := .unmodeled .invalidCreateDestination
    trace := creationRunPrefix ++ [.endTxTrace]
    gasLeft := 80
    stateGasReservoir := 0
    stateGasFromGasLeft := 0
    stateGasUsed := 0
    topFrameOutOfGas := false
    execution := none
    admission := some (literalAdmission .invalidDestination invalidNonceCollisionDestination .dead
      (admissionGas 80 0 0 0) 0 false 0 false false)
    settled := false
    receiptObserved := false
    dimensionalSettlement := none
    scalarSettlement := none
    senderRefund := 0
    senderBalance := 999_900
    tipPaid := 0
    burnedFees := 0
    headerGasUsed := 0
    blockCumulativeExecutionGas := 0
    blockCumulativeStateGas := 0
    cumulativeReceiptGas := 0
    receipt := none }

private def invalidStorageWithoutCollisionRunExpected : CreateAdmissionRunObservation :=
  { outcome := .unmodeled .invalidCreateDestination
    trace := creationRunPrefix ++ [.endTxTrace]
    gasLeft := 80
    stateGasReservoir := 0
    stateGasFromGasLeft := 0
    stateGasUsed := 0
    topFrameOutOfGas := false
    execution := none
    admission := some (literalAdmission .invalidDestination invalidStorageWithoutCollisionDestination .dead
      (admissionGas 80 0 0 0) 0 false 0 false false)
    settled := false
    receiptObserved := false
    dimensionalSettlement := none
    scalarSettlement := none
    senderRefund := 0
    senderBalance := 999_900
    tipPaid := 0
    burnedFees := 0
    headerGasUsed := 0
    blockCumulativeExecutionGas := 0
    blockCumulativeStateGas := 0
    cumulativeReceiptGas := 0
    receipt := none }

private def invalidExistingDestinationRunExpected
    (destination : CreateDestinationOracle) : CreateAdmissionRunObservation :=
  { outcome := .unmodeled .invalidCreateDestination
    trace := creationRunPrefix ++ [.endTxTrace]
    gasLeft := 80
    stateGasReservoir := 0
    stateGasFromGasLeft := 0
    stateGasUsed := 0
    topFrameOutOfGas := false
    execution := none
    admission := some (literalAdmission .invalidDestination destination .existent
      (admissionGas 80 0 0 0) 0 false 0 false false)
    settled := false
    receiptObserved := false
    dimensionalSettlement := none
    scalarSettlement := none
    senderRefund := 0
    senderBalance := 999_900
    tipPaid := 0
    burnedFees := 0
    headerGasUsed := 0
    blockCumulativeExecutionGas := 0
    blockCumulativeStateGas := 0
    cumulativeReceiptGas := 0
    receipt := none }

private def inconsistentAdmissionDirectiveRunExpected : CreateAdmissionRunObservation :=
  { outcome := .unmodeled .inconsistentCreateAdmissionDirective
    trace := creationRunPrefix ++ [.createStateCharge, .endTxTrace]
    gasLeft := 0
    stateGasReservoir := 0
    stateGasFromGasLeft := 0
    stateGasUsed := 0
    topFrameOutOfGas := false
    execution := none
    admission := some (literalAdmission .collision storageOnlyDestination .dead
      (admissionGas 0 0 0 0) 7 true 7 true false)
    settled := false
    receiptObserved := false
    dimensionalSettlement := none
    scalarSettlement := none
    senderRefund := 0
    senderBalance := 999_900
    tipPaid := 0
    burnedFees := 0
    headerGasUsed := 0
    blockCumulativeExecutionGas := 0
    blockCumulativeStateGas := 0
    cumulativeReceiptGas := 0
    receipt := none }

structure CreateAdmissionRunVector where
  name : String
  input : Input
  expected : CreateAdmissionRunObservation

def createAdmissionRunVectors : List CreateAdmissionRunVector :=
  [ { name := "fresh CREATE charges exactly once before value transfer"
      input := freshRunInput
      expected := freshRunExpected }
  , { name := "storage-only collision refills then preserves reservoir through halt settlement"
      input := storageOnlyRunInput
      expected := storageOnlyRunExpected }
  , { name := "balance-bearing storage collision has no new-account charge before halt settlement"
      input := balanceBearingStorageRunInput
      expected := balanceBearingStorageRunExpected }
  , { name := "one-short CREATE state charge clears execution gas before value transfer"
      input := oneShortRunInput
      expected := oneShortRunExpected }
  , { name := "balance-only existing target has no new-account charge"
      input := balanceOnlyRunInput
      expected := balanceOnlyRunExpected }
  , { name := "nonce collision has no new-account charge"
      input := nonceCollisionRunInput
      expected := nonceCollisionRunExpected }
  , { name := "code collision has no new-account charge"
      input := codeCollisionRunInput
      expected := codeCollisionRunExpected }
  , { name := "inconsistent destination facts fail closed before value transfer"
      input := invalidDestinationRunInput
      expected := invalidDestinationRunExpected }
  , { name := "storage without collision fails closed before value transfer"
      input := invalidStorageWithoutCollisionRunInput
      expected := invalidStorageWithoutCollisionRunExpected }
  , { name := "logically empty existent account fact fails closed before value transfer"
      input := invalidLogicallyEmptyExistentRunInput
      expected := invalidExistingDestinationRunExpected invalidLogicallyEmptyExistentDestination }
  , { name := "storage alone does not establish logical existence before value transfer"
      input := invalidStorageOnlyExistentRunInput
      expected := invalidExistingDestinationRunExpected invalidStorageOnlyExistentDestination }
  , { name := "nonzero nonce without collision fails closed before value transfer"
      input := invalidNonceWithoutCollisionRunInput
      expected := invalidExistingDestinationRunExpected invalidNonceWithoutCollisionDestination }
  , { name := "nonempty code without collision fails closed before value transfer"
      input := invalidCodeWithoutCollisionRunInput
      expected := invalidExistingDestinationRunExpected invalidCodeWithoutCollisionDestination }
  , { name := "inconsistent collision directive fails closed before value transfer"
      input := inconsistentAdmissionDirectiveRunInput
      expected := inconsistentAdmissionDirectiveRunExpected } ]

private def passesCreateAdmissionRun (vector : CreateAdmissionRunVector) : Bool :=
  observeCreateAdmissionRun (run nonsemanticFixtureOracle vector.input) = vector.expected

theorem all_create_admission_run_vectors_pass :
    createAdmissionRunVectors.all passesCreateAdmissionRun = true := by
  native_decide

theorem create_admission_run_vector_count : createAdmissionRunVectors.length = 14 := by
  native_decide

/-!
### Reservoir-preserving EIP-8037 halts through `run`

These terminal vectors keep two gas units in the state reservoir. Their
literal observations require the production-shaped halt settlement to charge
33 of 35 transaction gas in both scalar and dimensional accounting.
-/

structure Eip8037HaltRunVector where
  name : String
  input : Input
  expected : Eip8037HaltRunObservation

def eip8037HaltRunVectors : List Eip8037HaltRunVector :=
  [ { name := "stack-underflow resets positive execution gas before settlement"
      input := ordinaryExceptionHaltRunInput
      expected := haltRunExpected ordinaryExceptionHaltRunExpected none false }
  , { name := "ordinary top-frame OOG preserves the EIP-8037 reservoir"
      input := topFrameHaltRunInput
      expected := haltRunExpected topFrameHaltRunExpected none false }
  , { name := "initial top-frame OOG restores pre-preparation gas without authorization"
      input := initialTopFrameHaltRunInput
      expected := haltRunExpected initialTopFrameHaltRunExpected
        (some reservoirPrePreparationGas) false }
  , { name := "preparation OOG restores adversarial post-preparation gas"
      input := preparationHaltRunInput
      expected := haltRunExpected preparationHaltRunExpected
        (some reservoirPrePreparationGas) false }
  , { name := "CREATE top-frame OOG preserves the EIP-8037 reservoir"
      input := createTopFrameHaltRunInput
      expected := haltRunExpected createTopFrameHaltRunExpected none false }
  , { name := "one-short CREATE state OOG preserves the EIP-8037 reservoir"
      input := oneShortRunInput
      expected := haltRunExpected oneShortRunExpected none false }
  , { name := "invalid code deposit uses the EIP-8037 halt settlement"
      input := depositInvalidCodeHaltRunInput
      expected := haltRunExpected depositInvalidCodeHaltRunExpected none false }
  , { name := "code-deposit OOG uses the EIP-8037 halt settlement"
      input := depositOutOfGasHaltRunInput
      expected := haltRunExpected depositOutOfGasHaltRunExpected none false } ]

private def passesEip8037HaltRun (vector : Eip8037HaltRunVector) : Bool :=
  observeEip8037HaltRun (run nonsemanticFixtureOracle vector.input) = vector.expected

theorem all_eip8037_halt_run_vectors_pass :
    eip8037HaltRunVectors.all passesEip8037HaltRun = true := by
  native_decide

theorem eip8037_halt_run_vector_count : eip8037HaltRunVectors.length = 8 := by
  native_decide

theorem all_eip8037_halt_run_traces_are_ordered :
    eip8037HaltRunVectors.all (fun vector =>
      traceOrdered (run nonsemanticFixtureOracle vector.input).trace) = true := by
  native_decide

theorem deposit_failure_directives_route_through_eip8037_halt :
    routesThroughEip8037Halt .depositInvalidCode = true ∧
      routesThroughEip8037Halt .depositOutOfGas = true ∧
      (flagsOf .depositInvalidCode).isError = false ∧
      (flagsOf .depositOutOfGas).isError = false := by
  native_decide

theorem positive_gas_exception_is_normalized_before_settlement :
    let observed := observeEip8037HaltRun
      (run nonsemanticFixtureOracle ordinaryExceptionHaltRunInput)
    observed.lifecycle.gasLeft = 0 ∧
      observed.lifecycle.stateGasReservoir = 2 ∧
      observed.lifecycle.stateGasFromGasLeft = 0 ∧
      observed.lifecycle.stateGasUsed = 0 ∧
      observed.lifecycle.scalarSettlement = some (scalarSettlement 33 33 33 0 33 0) ∧
      observed.lifecycle.dimensionalSettlement = some (dimensionalSettlement 33 0 33 33 0 33) := by
  native_decide

theorem preparation_oog_restores_pre_preparation_gas_without_authorization_snapshot :
    let observed := observeEip8037HaltRun
      (run nonsemanticFixtureOracle preparationHaltRunInput)
    observed.preparationSnapshotPresent = false ∧
      observed.prePreparationGasRestore = some reservoirPrePreparationGas ∧
      observed.topExecutionGasSnapshot = some reservoirPrePreparationGas ∧
      observed.haltBaseline = some reservoirHaltBaseline ∧
      observed.lifecycle.gasLeft = 0 ∧
      observed.lifecycle.stateGasReservoir = 2 ∧
      observed.lifecycle.stateGasFromGasLeft = 0 ∧
      observed.lifecycle.stateGasUsed = 0 := by
  native_decide

theorem code_deposit_halts_preserve_the_reservoir_without_code_insert_refund :
    let invalid := observeEip8037HaltRun
      (run nonsemanticFixtureOracle depositInvalidCodeHaltRunInput)
    let outOfGas := observeEip8037HaltRun
      (run nonsemanticFixtureOracle depositOutOfGasHaltRunInput)
    invalid.lifecycle.scalarSettlement = some (scalarSettlement 33 33 33 0 33 0) ∧
      outOfGas.lifecycle.scalarSettlement = some (scalarSettlement 33 33 33 0 33 0) ∧
      invalid.lifecycle.senderRefund = 2 ∧ outOfGas.lifecycle.senderRefund = 2 := by
  native_decide

theorem simple_transfer_oog_does_not_select_the_evm_halt_normalization :
    let result := run nonsemanticFixtureOracle simpleTransferOutOfGas
    usesEip8037HaltSettlement simpleTransferOutOfGas result.state = false ∧
      result.state.eip8037HaltBaseline = none := by
  native_decide

theorem simple_transfer_state_oog_returns_its_reservoir_without_error :
    let result := run nonsemanticFixtureOracle simpleTransferOutOfGasWithReservoir
    result.state.execution = some .simpleTransferStateOutOfGas ∧
      result.state.substateFlags = { isError := false, shouldRevert := false } ∧
      result.state.gas.gasLeft = 0 ∧ result.state.gas.stateGasReservoir = 2 ∧
      result.state.stateGasFromGasLeft = 0 ∧ result.state.gas.evmStateGasUsed = 0 ∧
      usesEip8037HaltSettlement simpleTransferOutOfGasWithReservoir result.state = false ∧
      result.state.scalarSettlement = some (scalarSettlement 98 98 98 0 98 0) ∧
      result.state.settlement = some (dimensionalSettlement 98 0 98 98 0 98) ∧
      result.state.senderBalance = 999_902 ∧ result.state.beneficiaryBalance = 98 ∧
      result.state.headerGasUsed = 98 ∧ result.state.cumulativeReceiptGas = 98 := by
  native_decide

theorem simple_transfer_state_charge_failure_starts_with_and_preserves_reservoir :
    let before := initialState simpleTransferOutOfGasWithReservoir
    let attempted := tryConsumeRecipientStateGas simpleTransferOutOfGasWithReservoir before
    before.gas.gasLeft = 78 ∧ before.gas.stateGasReservoir = 2 ∧
      attempted.outOfGas = true ∧
      attempted.state.gas.gasLeft = before.gas.gasLeft ∧
      attempted.state.gas.stateGasReservoir = before.gas.stateGasReservoir ∧
      attempted.state.gas.refundCounter = before.gas.refundCounter ∧
      attempted.state.gas.evmStateGasUsed = before.gas.evmStateGasUsed ∧
      attempted.state.stateGasFromGasLeft = before.stateGasFromGasLeft := by
  native_decide

theorem top_level_create_revert_refills_new_account_state_gas :
    let result := run nonsemanticFixtureOracle revertedCreationRefillsStateGas
    result.trace = creationRevertTrace ∧
      result.state.execution = some .revert ∧
      result.state.createAdmission.map (fun admission => admission.stateCharge) = some 7 ∧
      result.state.gas.gasLeft = 67 ∧ result.state.gas.stateGasReservoir = 0 ∧
      result.state.stateGasFromGasLeft = 0 ∧ result.state.gas.evmStateGasUsed = 0 ∧
      result.state.scalarSettlement = some (scalarSettlement 33 33 33 0 33 0) ∧
      result.state.settlement = some (dimensionalSettlement 33 0 33 33 0 33) ∧
      result.state.senderBalance = 999_967 ∧ result.state.beneficiaryBalance = 33 ∧
      result.state.headerGasUsed = 33 ∧ result.state.cumulativeReceiptGas = 33 := by
  native_decide

private def environmentTopOogAfterAuthorizationInput : Input :=
  { baseInput with
    hasAuthorizationList := true
    authorization := .applied 1 }

private def environmentTopOogAfterAuthorizationOracle : Oracle :=
  { nonsemanticFixtureOracle with
    buildExecutionEnvironment := fun _ state =>
      let postEnvironment :=
        { gasTransitionOf
            { baseGas with
              gasLeft := 1
              stateGasReservoir := 0
              stateGasFromGasLeft := 6
              evmStateGasUsed := 7 } state with
          durableWorld := state.durableWorld + 7
          reversibleWorld := state.reversibleWorld + 7
          logs := state.logs ++ [7] }
      { state := postEnvironment
        path := .evm
        topFrameOutOfGas := true
        failure := none } }

theorem environment_reported_top_oog_restores_authorization_preparation :
    let result := run environmentTopOogAfterAuthorizationOracle environmentTopOogAfterAuthorizationInput
    result.trace = authOutOfGasTrace ∧
      result.outcome = .executed .failure (some .outOfGas) ∧
      result.trace.contains .authorizationRestore = true ∧
      result.trace.contains .payValue = false ∧
      result.state.authorization = some (.applied 1) ∧
      result.state.durableWorld = 0 ∧ result.state.reversibleWorld = 0 ∧ result.state.logs = [] ∧
      result.state.preparationGasRestore = some basePrePreparationGas ∧
      result.state.gas.gasLeft = 0 ∧ result.state.gas.stateGasReservoir = 0 ∧
      result.state.stateGasFromGasLeft = 0 ∧ result.state.gas.evmStateGasUsed = 0 := by
  native_decide

private def selfAuthorizationPreparationMutation (state : State) : State :=
  { state with
    durableWorld := state.durableWorld + 3
    reversibleWorld := state.reversibleWorld + 4
    senderNonce := state.senderNonce + 1
    senderBalance := state.senderBalance - 7
    recipientBalance := state.recipientBalance + 7
    beneficiaryBalance := state.beneficiaryBalance + 7
    feeCollectorBalance := state.feeCollectorBalance + 9
    logs := state.logs ++ [7]
    destroyList := [9] }

private def selfAuthorizationPreparationOracle : Oracle :=
  { nonsemanticFixtureOracle with
    processDelegations := fun input state =>
      { state := selfAuthorizationPreparationMutation state
        decision := input.authorization } }

private def selfAuthorizationOutOfGasInput : Input :=
  { authorizationOutOfGas with authorization := .outOfGas 1 }

private def selfAuthorizationEnvironmentTopOogInput : Input :=
  { successfulEvm with hasAuthorizationList := true, authorization := .applied 1 }

private def selfAuthorizationEnvironmentTopOogOracle : Oracle :=
  { selfAuthorizationPreparationOracle with
    buildExecutionEnvironment := fun _ state =>
      { state, path := .evm, topFrameOutOfGas := true, failure := none } }

private def selfAuthorizationRecipientChargeOogInput : Input :=
  { successfulEvm with
    hasAuthorizationList := true
    authorization := .applied 1
    recipientStateCharge := 81 }

theorem self_authorization_oog_restores_complete_preparation_snapshot :
    let result := run selfAuthorizationPreparationOracle selfAuthorizationOutOfGasInput
    result.trace = authOutOfGasTrace ∧
      result.outcome = .executed .failure (some .outOfGas) ∧
      result.state.authorization = some (.outOfGas 1) ∧ result.state.senderNonce = 1 ∧
      result.state.durableWorld = 0 ∧ result.state.reversibleWorld = 0 ∧
      result.state.senderBalance = 999_900 ∧ result.state.recipientBalance = 0 ∧
      result.state.beneficiaryBalance = 100 ∧ result.state.feeCollectorBalance = 0 ∧
      result.state.logs = [] ∧ result.state.destroyList = [] ∧
      result.state.destroyListFinalized = false ∧
      result.state.preparationGasRestore = some basePrePreparationGas ∧
      result.state.senderBalance + result.state.recipientBalance + result.state.beneficiaryBalance +
          result.state.feeCollectorBalance = 1_000_000 := by
  native_decide

theorem self_authorization_environment_top_oog_restores_complete_preparation_snapshot :
    let result := run selfAuthorizationEnvironmentTopOogOracle selfAuthorizationEnvironmentTopOogInput
    result.trace = authOutOfGasTrace ∧
      result.outcome = .executed .failure (some .outOfGas) ∧
      result.state.authorization = some (.applied 1) ∧ result.state.senderNonce = 1 ∧
      result.state.durableWorld = 0 ∧ result.state.reversibleWorld = 0 ∧
      result.state.senderBalance = 999_900 ∧ result.state.recipientBalance = 0 ∧
      result.state.beneficiaryBalance = 100 ∧ result.state.feeCollectorBalance = 0 ∧
      result.state.logs = [] ∧ result.state.destroyList = [] ∧
      result.state.destroyListFinalized = false ∧
      result.state.preparationGasRestore = some basePrePreparationGas ∧
      result.state.senderBalance + result.state.recipientBalance + result.state.beneficiaryBalance +
          result.state.feeCollectorBalance = 1_000_000 := by
  native_decide

theorem other_preparation_oog_shapes_restore_self_authorization_snapshot :
    let delegated := run selfAuthorizationPreparationOracle delegatedTargetOutOfGas
    let recipient := run selfAuthorizationPreparationOracle selfAuthorizationRecipientChargeOogInput
    delegated.outcome = .executed .failure (some .outOfGas) ∧
      recipient.outcome = .executed .failure (some .outOfGas) ∧
      delegated.state.senderNonce = 1 ∧ recipient.state.senderNonce = 1 ∧
      delegated.state.durableWorld = 0 ∧ recipient.state.durableWorld = 0 ∧
      delegated.state.reversibleWorld = 0 ∧ recipient.state.reversibleWorld = 0 ∧
      delegated.state.beneficiaryBalance = 100 ∧ recipient.state.beneficiaryBalance = 100 ∧
      delegated.state.feeCollectorBalance = 0 ∧ recipient.state.feeCollectorBalance = 0 ∧
      delegated.state.destroyListFinalized = false ∧ recipient.state.destroyListFinalized = false := by
  native_decide

private def delegatedCreateRevertSnapshotInput : Input :=
  { successfulCreation with
    hasAuthorizationList := true
    authorization := .applied 1
    newAccountStateGas := 0
    execution := .revert
    executionGas :=
      { gasLeft := 80
        stateGasReservoir := 0
        stateGasFromGasLeft := 0
        refundCounter := 0
        evmStateGasUsed := 0 }
    fees :=
      { effectiveGasPrice := 2
        premiumPerGas := 1
        baseFeePerGas := 1
        blobBaseFee := 0
        feeCollectorEnabled := true
        overflow := false } }

private def delegatedCreateRevertSnapshotOracle : Oracle :=
  { nonsemanticFixtureOracle with
    executeEvmCall := fun _ state =>
      { state := { state with
          senderNonce := state.senderNonce + 1
          senderBalance := state.senderBalance - 5
          recipientBalance := state.recipientBalance + 5
          beneficiaryBalance := state.beneficiaryBalance + 7
          feeCollectorBalance := state.feeCollectorBalance + 9
          durableWorld := state.durableWorld + 11
          reversibleWorld := state.reversibleWorld + 1
          logs := state.logs ++ [5]
          destroyList := [99] }
        outcome := .revert } }

theorem delegated_create_revert_restores_complete_execution_snapshot_before_fees :
    let result := run delegatedCreateRevertSnapshotOracle delegatedCreateRevertSnapshotInput
    result.outcome = .executed .failure none ∧
      result.trace.contains .executionRollback = true ∧ result.state.senderNonce = 1 ∧
      result.state.durableWorld = 1 ∧ result.state.reversibleWorld = 0 ∧
      result.state.senderBalance = 999_960 ∧ result.state.recipientBalance = 0 ∧
      result.state.beneficiaryBalance = 20 ∧ result.state.feeCollectorBalance = 20 ∧
      result.state.logs = [] ∧ result.state.destroyList = [] ∧
      result.state.destroyListFinalized = false ∧
      result.state.senderBalance + result.state.recipientBalance + result.state.beneficiaryBalance +
          result.state.feeCollectorBalance = 1_000_000 := by
  native_decide

private def delegatedTargetOogWithMutatedPreparationOracle : Oracle :=
  { nonsemanticFixtureOracle with
    buildExecutionEnvironment := fun input state =>
      if input.environmentFailure = some .delegatedTargetOutOfGas then
        let postEnvironment :=
          { gasTransitionOf
              { baseGas with
                gasLeft := 1
                stateGasReservoir := 0
                stateGasFromGasLeft := 6
                evmStateGasUsed := 7 } state with
            durableWorld := state.durableWorld + 7
            reversibleWorld := state.reversibleWorld + 7
            logs := state.logs ++ [7] }
        { state := postEnvironment
          path := input.entryKind.path
          topFrameOutOfGas := false
          failure := some .delegatedTargetOutOfGas }
      else
        environmentEffect input state }

theorem delegated_target_oog_restores_preparation_before_halt :
    let result := run nonsemanticFixtureOracle delegatedTargetOutOfGas
    result.trace = authOutOfGasTrace ∧
      result.state.authorization = some (.applied 1) ∧
      result.state.durableWorld = 0 ∧ result.state.reversibleWorld = 0 ∧
      result.state.logs = [] ∧
      result.state.preparationGasRestore = some basePrePreparationGas ∧
      result.state.gas.gasLeft = 0 ∧ result.state.gas.stateGasReservoir = 0 ∧
      result.state.stateGasFromGasLeft = 0 ∧ result.state.gas.evmStateGasUsed = 0 ∧
      result.state.scalarSettlement = some (scalarSettlement 100 100 100 0 100 0) ∧
      result.state.settlement = some (dimensionalSettlement 100 0 100 100 0 100) ∧
      result.state.senderBalance = 999_900 ∧ result.state.beneficiaryBalance = 100 ∧
      result.state.headerGasUsed = 100 ∧ result.state.cumulativeReceiptGas = 100 := by
  native_decide

theorem delegated_target_oog_restores_environment_mutated_preparation :
    let result := run delegatedTargetOogWithMutatedPreparationOracle delegatedTargetOutOfGas
    result.trace = authOutOfGasTrace ∧
      result.state.durableWorld = 0 ∧ result.state.reversibleWorld = 0 ∧
      result.state.logs = [] ∧
      result.state.preparationGasRestore = some basePrePreparationGas ∧
      result.state.eip8037HaltBaseline = some
        { stateGasReservoir := 0, stateGasFromGasLeft := 0, stateGasUsed := 0 } ∧
      result.state.gas.gasLeft = 0 ∧ result.state.gas.stateGasReservoir = 0 ∧
      result.state.stateGasFromGasLeft = 0 ∧ result.state.gas.evmStateGasUsed = 0 ∧
      result.state.scalarSettlement = some (scalarSettlement 100 100 100 0 100 0) ∧
      result.state.settlement = some (dimensionalSettlement 100 0 100 100 0 100) := by
  native_decide

theorem all_create_admission_run_traces_are_ordered :
    createAdmissionRunVectors.all (fun vector =>
      traceOrdered (run nonsemanticFixtureOracle vector.input).trace) = true := by
  native_decide

theorem run_create_admission_reads_once :
    createAdmissionRunVectors.all (fun vector =>
      match (run nonsemanticFixtureOracle vector.input).state.createAdmission with
      | some admission => admission.classification.destinationReadCount == 1
      | none => false) = true := by
  native_decide

theorem terminal_create_admission_never_pays_value :
    (run nonsemanticFixtureOracle storageOnlyRunInput).trace.contains .payValue = false ∧
      (run nonsemanticFixtureOracle balanceBearingStorageRunInput).trace.contains .payValue = false ∧
      (run nonsemanticFixtureOracle oneShortRunInput).trace.contains .payValue = false ∧
      (run nonsemanticFixtureOracle invalidDestinationRunInput).trace.contains .payValue = false ∧
      (run nonsemanticFixtureOracle invalidStorageWithoutCollisionRunInput).trace.contains
        .payValue = false ∧
      (run nonsemanticFixtureOracle invalidLogicallyEmptyExistentRunInput).trace.contains
        .payValue = false ∧
      (run nonsemanticFixtureOracle invalidStorageOnlyExistentRunInput).trace.contains
        .payValue = false ∧
      (run nonsemanticFixtureOracle invalidNonceWithoutCollisionRunInput).trace.contains
        .payValue = false ∧
      (run nonsemanticFixtureOracle invalidCodeWithoutCollisionRunInput).trace.contains
        .payValue = false ∧
      (run nonsemanticFixtureOracle inconsistentAdmissionDirectiveRunInput).trace.contains
        .payValue = false := by
  native_decide

theorem run_existing_destinations_have_no_new_account_charge :
    (run nonsemanticFixtureOracle balanceOnlyRunInput).state.createAdmission.map
        (fun admission => admission.stateCharge) = some 0 ∧
      (run nonsemanticFixtureOracle balanceBearingStorageRunInput).state.createAdmission.map
        (fun admission => admission.stateCharge) = some 0 ∧
      (run nonsemanticFixtureOracle nonceCollisionRunInput).state.createAdmission.map
        (fun admission => admission.stateCharge) = some 0 ∧
      (run nonsemanticFixtureOracle codeCollisionRunInput).state.createAdmission.map
        (fun admission => admission.stateCharge) = some 0 := by
  native_decide

theorem run_storage_only_collision_refills_exact_charge :
    let result := run nonsemanticFixtureOracle storageOnlyRunInput
    result.state.createAdmission.map (fun admission => admission.stateCharge) = some 7 ∧
      result.state.createAdmission.map (fun admission => admission.stateChargeRefilled) = some 7 ∧
      result.state.gas.stateGasReservoir = 2 ∧
      result.state.stateGasFromGasLeft = 0 := by
  native_decide

theorem run_balance_bearing_storage_collision_uses_common_halt_settlement :
    let result := run nonsemanticFixtureOracle balanceBearingStorageRunInput
    result.trace = creationRunPrefix ++ [.collisionCheck, .executionRollback] ++
      creationRunSettlement ∧
      result.state.createAdmission.map (fun admission => admission.stateCharge) = some 0 ∧
      usesEip8037HaltSettlement balanceBearingStorageRunInput result.state = true ∧
      result.state.scalarSettlement = some (scalarSettlement 33 33 33 0 33 0) ∧
      result.state.settlement = some (dimensionalSettlement 33 0 33 33 0 33) ∧
      result.state.gas.gasLeft = 0 ∧ result.state.gas.stateGasReservoir = 2 := by
  native_decide

/-!
The mutation kernels are injected through the complete run pipeline, so their
failure is observed at the same transaction boundary as production ordering
and settlement.
-/

private def mutatedCreateChargeAfterCollisionKernel (gas : GasState)
    (newAccountStateGas : Nat) (destination : CreateDestinationOracle) : CreateAdmissionResult :=
  let result := admitCreate gas newAccountStateGas destination
  if result.status = .collision then
    { result with
      gas := clearCreateExecutionGas gas
      stateCharge := 0
      stateChargeApplied := false
      stateChargeRefilled := 0 }
  else result

private def mutatedTreatsStorageOnlyAsExistingKernel (gas : GasState)
    (newAccountStateGas : Nat) (destination : CreateDestinationOracle) : CreateAdmissionResult :=
  if destination.collision = .storageOnly then
    admitCreate gas newAccountStateGas
      { destination with
        accountExists := true
        balance := 1
        storageNonEmpty := false
        collision := .none }
  else
    admitCreate gas newAccountStateGas destination

private def mutatedLeavesCreateOogGasKernel (gas : GasState)
    (newAccountStateGas : Nat) (destination : CreateDestinationOracle) : CreateAdmissionResult :=
  let result := admitCreate gas newAccountStateGas destination
  if result.status = .outOfGas then
    { result with gas, executionGasCleared := false }
  else result

private def mutatedChargesExistingDestinationKernel (gas : GasState)
    (newAccountStateGas : Nat) (destination : CreateDestinationOracle) : CreateAdmissionResult :=
  if destination.accountExists then
    admitCreate gas newAccountStateGas
      { destination with
        accountExists := false
        balance := 0
        nonce := 0
        codeEmpty := true
        storageNonEmpty := false
        collision := .none }
  else
    admitCreate gas newAccountStateGas destination

private def mutatedChargesExistentStorageDestinationKernel (gas : GasState)
    (newAccountStateGas : Nat) (destination : CreateDestinationOracle) : CreateAdmissionResult :=
  if destination.collision = .storageOnly && destination.accountExists then
    admitCreate gas newAccountStateGas { destination with accountExists := false, balance := 0 }
  else
    admitCreate gas newAccountStateGas destination

private def mutatedRejectsExistentStorageCollisionKernel (gas : GasState)
    (newAccountStateGas : Nat) (destination : CreateDestinationOracle) : CreateAdmissionResult :=
  let result := admitCreate gas newAccountStateGas destination
  if destination.collision = .storageOnly && destination.accountExists then
    { result with
      status := .invalidDestination
      gas
      stateCharge := 0
      stateChargeApplied := false
      stateChargeRefilled := 0
      executionGasCleared := false
      childEntered := false }
  else
    result

private def mutatedAllowsStorageWithoutCollisionKernel (gas : GasState)
    (newAccountStateGas : Nat) (destination : CreateDestinationOracle) : CreateAdmissionResult :=
  if destination.storageNonEmpty && destination.collision = .none then
    admitCreate gas newAccountStateGas { destination with storageNonEmpty := false }
  else
    admitCreate gas newAccountStateGas destination

private def mutatedFullGasBurnSettlement (input : Input) (state : State) : Option State :=
  settleStateWithScalarInput (scalarSettlementInput input state) input state

private def mutatedHaltSkipsStateReset (input : Input) (state : State) : Option State :=
  settleStateWithScalarInput (scalarSettlementInputForState input state) input state

private def mutatedCreateRevertWithoutStateRefundSettlement (input : Input) (state : State) :
    Option State :=
  if input.entryKind = .contractCreation && state.execution = some .revert then
    match state.createAdmission with
    | some admission =>
        match GasMachine.chargeState admission.stateCharge (gasStateOf state) with
        | .ok gas => settleState input (withGasState gas state)
        | .error _ => none
    | none => settleState input state
  else
    settleState input state

private def mutatedSimpleTransferOogBurnsReservoirSettlement (input : Input) (state : State) :
    Option State :=
  if input.entryKind = .simpleTransfer && state.execution = some .simpleTransferStateOutOfGas then
    settleStateWithScalarInput { scalarSettlementInput input state with isError := true } input state
  else
    settleState input state

private def mutatedDelegatedTargetOogAsSuccessOracle : Oracle :=
  { nonsemanticFixtureOracle with
    buildExecutionEnvironment := fun input state =>
      if input.environmentFailure = some .delegatedTargetOutOfGas then
        { state
          path := input.entryKind.path
          topFrameOutOfGas := false
          failure := none }
      else
        environmentEffect input state }

private def mutatedSkipsPrePreparationGasRestore (_input : Input) (cursor : Cursor) : Journey :=
  .active cursor

private def mutatedAlwaysChargesEvmRecipient (_input : Input) : Bool :=
  true

private def snapshotMutationBefore : State :=
  { initialState successfulEvm with
    durableWorld := 1
    reversibleWorld := 2
    senderNonce := 1
    senderBalance := 100
    recipientBalance := 3
    beneficiaryBalance := 4
    feeCollectorBalance := 5
    reservedGas := 100
    logs := [6]
    destroyList := [7]
    destroyListFinalized := false }

private def snapshotMutationAfter : State :=
  { snapshotMutationBefore with
    durableWorld := 11
    reversibleWorld := 12
    senderNonce := 13
    senderBalance := 113
    recipientBalance := 16
    beneficiaryBalance := 14
    feeCollectorBalance := 15
    logs := [16]
    destroyList := [17]
    destroyListFinalized := true }

private def mutatedPreparationRestoreWithoutSenderNonce
    (snapshot : PreparationSnapshot) (state : State) : State :=
  { restorePreparationSnapshot snapshot state with senderNonce := state.senderNonce }

private def mutatedPreparationRestoreWithoutBeneficiaryBalance
    (snapshot : PreparationSnapshot) (state : State) : State :=
  { restorePreparationSnapshot snapshot state with beneficiaryBalance := state.beneficiaryBalance }

private def mutatedPreparationRestoreWithoutFeeCollectorBalance
    (snapshot : PreparationSnapshot) (state : State) : State :=
  { restorePreparationSnapshot snapshot state with feeCollectorBalance := state.feeCollectorBalance }

private def mutatedPreparationRestoreWithoutDestroyListFinalized
    (snapshot : PreparationSnapshot) (state : State) : State :=
  { restorePreparationSnapshot snapshot state with destroyListFinalized := state.destroyListFinalized }

private def mutatedExecutionRestoreWithoutDurableWorld
    (snapshot : ExecutionSnapshot) (state : State) : State :=
  { restoreExecutionSnapshot snapshot state with durableWorld := state.durableWorld }

private def mutatedExecutionRestoreWithoutSenderNonce
    (snapshot : ExecutionSnapshot) (state : State) : State :=
  { restoreExecutionSnapshot snapshot state with senderNonce := state.senderNonce }

private def mutatedExecutionRestoreWithoutBeneficiaryBalance
    (snapshot : ExecutionSnapshot) (state : State) : State :=
  { restoreExecutionSnapshot snapshot state with beneficiaryBalance := state.beneficiaryBalance }

private def mutatedExecutionRestoreWithoutFeeCollectorBalance
    (snapshot : ExecutionSnapshot) (state : State) : State :=
  { restoreExecutionSnapshot snapshot state with feeCollectorBalance := state.feeCollectorBalance }

private def mutatedExecutionRestoreWithoutDestroyListFinalized
    (snapshot : ExecutionSnapshot) (state : State) : State :=
  { restoreExecutionSnapshot snapshot state with destroyListFinalized := state.destroyListFinalized }

theorem snapshot_restore_mutation_sentinels_are_detected :
    let preparation := preparationSnapshotOf snapshotMutationBefore
    let execution := executionSnapshotOf snapshotMutationBefore
    (mutatedPreparationRestoreWithoutSenderNonce preparation snapshotMutationAfter).senderNonce != 1 ∧
      (mutatedPreparationRestoreWithoutBeneficiaryBalance preparation snapshotMutationAfter).beneficiaryBalance !=
        4 ∧
      (mutatedPreparationRestoreWithoutFeeCollectorBalance preparation snapshotMutationAfter).feeCollectorBalance !=
        5 ∧
      (mutatedPreparationRestoreWithoutDestroyListFinalized preparation snapshotMutationAfter).destroyListFinalized !=
        false ∧
      (mutatedExecutionRestoreWithoutDurableWorld execution snapshotMutationAfter).durableWorld != 1 ∧
      (mutatedExecutionRestoreWithoutSenderNonce execution snapshotMutationAfter).senderNonce != 1 ∧
      (mutatedExecutionRestoreWithoutBeneficiaryBalance execution snapshotMutationAfter).beneficiaryBalance !=
        4 ∧
      (mutatedExecutionRestoreWithoutFeeCollectorBalance execution snapshotMutationAfter).feeCollectorBalance !=
        5 ∧
      (mutatedExecutionRestoreWithoutDestroyListFinalized execution snapshotMutationAfter).destroyListFinalized !=
        false := by
  native_decide

theorem run_level_mutation_sentinels_are_detected :
    observeCreateAdmissionRun
        (runWithCreateAdmission mutatedCreateChargeAfterCollisionKernel
          nonsemanticFixtureOracle storageOnlyRunInput) != storageOnlyRunExpected ∧
      observeCreateAdmissionRun
        (runWithCreateAdmission mutatedTreatsStorageOnlyAsExistingKernel
          nonsemanticFixtureOracle storageOnlyRunInput) != storageOnlyRunExpected ∧
      observeCreateAdmissionRun
        (runWithCreateAdmission mutatedLeavesCreateOogGasKernel
          nonsemanticFixtureOracle oneShortRunInput) != oneShortRunExpected ∧
      observeCreateAdmissionRun
        (runWithCreateAdmission mutatedChargesExistingDestinationKernel
          nonsemanticFixtureOracle balanceOnlyRunInput) != balanceOnlyRunExpected ∧
      observeCreateAdmissionRun
        (runWithCreateAdmission mutatedChargesExistentStorageDestinationKernel
          nonsemanticFixtureOracle balanceBearingStorageRunInput) != balanceBearingStorageRunExpected ∧
      observeCreateAdmissionRun
        (runWithCreateAdmission mutatedRejectsExistentStorageCollisionKernel
          nonsemanticFixtureOracle balanceBearingStorageRunInput) != balanceBearingStorageRunExpected ∧
      observeCreateAdmissionRun
        (runWithCreateAdmission mutatedAllowsStorageWithoutCollisionKernel
          nonsemanticFixtureOracle invalidStorageWithoutCollisionRunInput) !=
        invalidStorageWithoutCollisionRunExpected ∧
      observeCreateAdmissionRun
        (runWithTransactionKernels admitCreate requiresEvmRecipientStateCharge
          mutatedFullGasBurnSettlement nonsemanticFixtureOracle storageOnlyRunInput) !=
        storageOnlyRunExpected ∧
      observeCreateAdmissionRun
        (runWithTransactionKernels admitCreate requiresEvmRecipientStateCharge
          mutatedFullGasBurnSettlement nonsemanticFixtureOracle ordinaryExceptionHaltRunInput) !=
        ordinaryExceptionHaltRunExpected ∧
      observeCreateAdmissionRun
        (runWithTransactionKernels admitCreate requiresEvmRecipientStateCharge
          mutatedFullGasBurnSettlement nonsemanticFixtureOracle oneShortRunInput) !=
        oneShortRunExpected ∧
      observeCreateAdmissionRun
        (runWithTransactionKernels admitCreate requiresEvmRecipientStateCharge
          mutatedHaltSkipsStateReset nonsemanticFixtureOracle ordinaryExceptionHaltRunInput) !=
        ordinaryExceptionHaltRunExpected ∧
      observeCreateAdmissionRun
        (runWithTransactionKernels admitCreate requiresEvmRecipientStateCharge
          mutatedHaltSkipsStateReset nonsemanticFixtureOracle depositInvalidCodeHaltRunInput) !=
        depositInvalidCodeHaltRunExpected ∧
      observeCreateAdmissionRun
        (runWithTransactionKernels admitCreate requiresEvmRecipientStateCharge
          mutatedHaltSkipsStateReset nonsemanticFixtureOracle depositOutOfGasHaltRunInput) !=
        depositOutOfGasHaltRunExpected ∧
      observe
        (runWithTransactionKernels admitCreate requiresEvmRecipientStateCharge
          mutatedCreateRevertWithoutStateRefundSettlement nonsemanticFixtureOracle
          revertedCreationRefillsStateGas) !=
        observe (run nonsemanticFixtureOracle revertedCreationRefillsStateGas) ∧
      observe
        (runWithTransactionKernels admitCreate requiresEvmRecipientStateCharge
          mutatedSimpleTransferOogBurnsReservoirSettlement nonsemanticFixtureOracle
          simpleTransferOutOfGasWithReservoir) !=
        observe (run nonsemanticFixtureOracle simpleTransferOutOfGasWithReservoir) ∧
      observe
        (run mutatedDelegatedTargetOogAsSuccessOracle delegatedTargetOutOfGas) !=
        observe (run nonsemanticFixtureOracle delegatedTargetOutOfGas) ∧
      observeEip8037HaltRun
        (runWithTransactionKernelsAndPrePreparationGasRestore admitCreate
          requiresEvmRecipientStateCharge settleState mutatedSkipsPrePreparationGasRestore
          nonsemanticFixtureOracle preparationHaltRunInput) !=
        haltRunExpected preparationHaltRunExpected (some reservoirPrePreparationGas) false ∧
      observeCreateAdmissionRun
        (runWithTransactionKernels admitCreate mutatedAlwaysChargesEvmRecipient settleState
          nonsemanticFixtureOracle storageOnlyRunInput) != storageOnlyRunExpected := by
  native_decide

end Eip803x.TransactionReferenceVectors
