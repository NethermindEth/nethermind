-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.TransactionGas
import Eip803x.TransactionSettlement
import Eip803x.Gas

namespace Eip803x.TransactionReference

/-!
This is a handwritten executable reference for the ordinary outer transaction
lifecycle at the pinned Amsterdam mainnet surface.  It is not a production
refinement theorem.  Recovery, validation, world-state, VM, root, fee, receipt,
and commit/reset behavior are explicit adapter operations in `Oracle`.

The trace contains runtime events, not one entry for every method declaration
in the source inventory.  Optional events are omitted on paths that do not
execute them, and `endTxTrace` closes every adapter invocation, including an
early rejection.
-/

inductive Phase where
  | validation
  | senderAuthorityRecovery
  | preExecution
  | simpleTransferOrVm
  | snapshotsRollback
  | feeRefundReceiptSettlement
  deriving DecidableEq, Repr

inductive Event where
  | loadNonceFromState
  | startNewTxTrace
  | process
  | buildUpSnapshot
  | executeCoreOrdinary
  | executeCoreSystem
  | recoverSenderBeforeIntrinsicGas
  | calculateIntrinsicGas
  | validateStatic
  | calculateEffectiveGasPrice
  | recoverSenderIfNeeded
  | validateSender
  | buyGas
  | incrementNonce
  | prepareSimpleTransferFastPath
  | commitBeforeExecution
  | calculateAvailableGas
  | authorizationSnapshot
  | processDelegations
  | buildExecutionEnvironment
  | recipientStateCharge
  | simpleTransferExecution
  | authorizationRestore
  | topExecutionSnapshot
  | payValue
  | createDestinationRead
  | createStateCharge
  | collisionCheck
  | topFrameOutOfGas
  | vmExecution
  | deployment
  | executionRollback
  | refund
  | headerGas
  | payFees
  | destroyListFinalize
  | restore
  | commit
  | resetTransient
  | receiptStart
  | receiptObserve
  | endTxTrace
  deriving DecidableEq, Repr

def Event.phase : Event → Phase
  | .loadNonceFromState | .startNewTxTrace | .process | .buildUpSnapshot
  | .executeCoreOrdinary | .executeCoreSystem
  | .recoverSenderBeforeIntrinsicGas | .calculateIntrinsicGas | .validateStatic =>
      .validation
  | .calculateEffectiveGasPrice | .recoverSenderIfNeeded | .validateSender
  | .buyGas | .incrementNonce => .senderAuthorityRecovery
  | .prepareSimpleTransferFastPath | .commitBeforeExecution | .simpleTransferExecution
  | .calculateAvailableGas | .authorizationSnapshot | .processDelegations
  | .buildExecutionEnvironment | .recipientStateCharge | .authorizationRestore =>
       .preExecution
  | .deployment | .executionRollback =>
      .snapshotsRollback
  | .topExecutionSnapshot | .createDestinationRead | .createStateCharge | .collisionCheck
  | .payValue
  | .topFrameOutOfGas | .vmExecution => .simpleTransferOrVm
  | .refund | .headerGas | .payFees | .destroyListFinalize | .restore | .commit | .resetTransient
  | .receiptStart
  | .receiptObserve | .endTxTrace => .feeRefundReceiptSettlement

def eventRank : Event → Nat
  | .loadNonceFromState => 0
  | .startNewTxTrace => 1
  | .process => 2
  | .buildUpSnapshot => 3
  | .executeCoreOrdinary | .executeCoreSystem => 4
  | .recoverSenderBeforeIntrinsicGas => 5
  | .calculateIntrinsicGas => 6
  | .validateStatic => 7
  | .calculateEffectiveGasPrice => 8
  | .recoverSenderIfNeeded => 9
  | .validateSender => 10
  | .buyGas => 11
  | .incrementNonce => 12
  | .prepareSimpleTransferFastPath => 13
  | .commitBeforeExecution => 14
  | .calculateAvailableGas => 15
  | .simpleTransferExecution | .authorizationSnapshot => 16
  | .processDelegations => 17
  | .buildExecutionEnvironment => 18
  | .recipientStateCharge => 19
  | .authorizationRestore => 20
  | .topExecutionSnapshot => 21
  | .createDestinationRead => 22
  | .createStateCharge => 23
  | .collisionCheck => 24
  | .payValue => 25
  | .topFrameOutOfGas | .vmExecution => 26
  | .deployment => 27
  | .executionRollback => 28
  | .refund => 29
  | .headerGas => 30
  | .payFees => 31
  | .destroyListFinalize => 32
  | .restore | .commit | .resetTransient => 33
  | .receiptStart => 34
  | .receiptObserve => 35
  | .endTxTrace => 35

def traceOrdered : List Event → Bool
  | [] => true
  | [_] => true
  | first :: second :: rest =>
      decide (eventRank first ≤ eventRank second) && traceOrdered (second :: rest)

def phaseTrace (trace : List Event) : List Phase := trace.map Event.phase

def phaseRank : Phase → Nat
  | .validation => 0
  | .senderAuthorityRecovery => 1
  | .preExecution => 2
  | .simpleTransferOrVm => 3
  | .snapshotsRollback => 4
  | .feeRefundReceiptSettlement => 5

def phaseTraceOrdered : List Phase → Bool
  | [] => true
  | [_] => true
  | first :: second :: rest =>
      decide (phaseRank first ≤ phaseRank second) && phaseTraceOrdered (second :: rest)

inductive ExecutionPath where
  | simpleTransfer
  | evm
  deriving DecidableEq, Repr

inductive EntryKind where
  | messageCall
  | contractCreation
  | simpleTransfer
  | systemCall
  deriving DecidableEq, Repr

inductive ExecutionMode where
  | execute
  | callAndRestore
  | buildUp
  | trace
  | warmup
  | systemSkipValidation
  deriving DecidableEq, Repr

def ExecutionMode.skipsValidation : ExecutionMode → Bool
  | .execute | .buildUp => false
  | .callAndRestore | .trace | .warmup | .systemSkipValidation => true

def ExecutionMode.restores : ExecutionMode → Bool
  | .callAndRestore => true
  | _ => false

def ExecutionMode.commits : ExecutionMode → Bool
  | .execute | .callAndRestore | .trace => true
  | .buildUp | .warmup | .systemSkipValidation => false

def ExecutionMode.updatesHeader : ExecutionMode → Bool
  | .execute | .buildUp => true
  | .callAndRestore | .trace | .warmup | .systemSkipValidation => false

def EntryKind.isSystem : EntryKind → Bool
  | .systemCall => true
  | _ => false

def EntryKind.path : EntryKind → ExecutionPath
  | .simpleTransfer => .simpleTransfer
  | .messageCall | .contractCreation => .evm
  | .systemCall => .evm

inductive ValidationFailure where
  | senderNotSpecified
  | nonceOverflow
  | malformedTransaction
  | gasLimitBelowIntrinsic
  | gasLimitBelowFloor
  | blockGasLimitExceeded
  | maxFeePerGasBelowBaseFee
  | insufficientSenderBalance
  | senderHasDeployedCode
  | transactionSizeOverMaxInitCodeSize
  | nonceTooHigh
  | nonceTooLow
  deriving DecidableEq, Repr

inductive SenderFailure where
  | senderNotSpecified
  | senderHasDeployedCode
  | senderAccountDoesNotExist
  deriving DecidableEq, Repr

inductive NonceFailure where
  | overflow
  | tooHigh
  | tooLow
  deriving DecidableEq, Repr

inductive PreparationFailure where
  | recipientNotResolved
  | delegatedTargetOutOfGas
  | intrinsicGasUnavailable
  | contractCollision
  | invalidCodeDeposit
  deriving DecidableEq, Repr

inductive UnmodeledCase where
  | systemTransactionProcessor
  | unsupportedProductionAdapter
  | buildEnvironmentException
  | invalidCreateDestination
  | inconsistentCreateAdmissionDirective
  | invalidExecutionRoute
  | inconsistentOracleEffectResult
  | inconsistentAuthorizationResult
  | inconsistentPreparationResult
  | inconsistentEnvironmentResult
  | inconsistentEvmExecutionResult
  | invalidEvmExecutionDirective
  | missingExecution
  | missingSettlement
  | invalidSettlementDomain
  | inconsistentSettlementResult
  | missingReceipt
  | missingPreparationSnapshot
  | feeArithmeticOverflow
  deriving DecidableEq, Repr

inductive EarlyFailure where
  | validation (failure : ValidationFailure)
  | sender (failure : SenderFailure)
  | nonce (failure : NonceFailure)
  | gasPurchase
  | unmodeled (case : UnmodeledCase)
  deriving DecidableEq, Repr

inductive RestoreFailure where
  | sender (failure : SenderFailure)
  | maxFee
  | balance
  | nonce (failure : NonceFailure)
  | feeOverflow
  deriving DecidableEq, Repr

inductive AuthorizationDecision where
  | none
  | applied (count : Nat)
  | skippedInvalid (count : Nat)
  | outOfGas (appliedBeforeFailure : Nat)
  deriving DecidableEq, Repr

inductive EvmException where
  | outOfGas
  | invalidCode
  | collision
  | stackUnderflow
  | stackOverflow
  | invalidJump
  | staticViolation
  deriving DecidableEq, Repr

inductive ExecutionDirective where
  | success
  | revert
  | exception (kind : EvmException)
  | topFrameOutOfGas
  /-- A simple-transfer NEW_ACCOUNT charge failed before value movement. -/
  | simpleTransferStateOutOfGas
  | collision
  | createStateOutOfGas
  | depositInvalidCode
  | depositOutOfGas
  | preparationOutOfGas
  | unmodeled
  deriving DecidableEq, Repr

structure SubstateFlags where
  isError : Bool
  shouldRevert : Bool
  deriving DecidableEq, Repr

def flagsOf : ExecutionDirective → SubstateFlags
  | .success => { isError := false, shouldRevert := false }
  | .revert => { isError := false, shouldRevert := true }
  | .simpleTransferStateOutOfGas => { isError := false, shouldRevert := false }
  | .exception _ | .unmodeled => { isError := true, shouldRevert := false }
  | .topFrameOutOfGas | .collision | .createStateOutOfGas | .preparationOutOfGas =>
      { isError := true, shouldRevert := false }
  | .depositInvalidCode | .depositOutOfGas =>
      { isError := false, shouldRevert := false }

theorem revert_substate_flags : flagsOf .revert =
    { isError := false, shouldRevert := true } := by
  rfl

def executionNeedsRollback : ExecutionDirective → Bool
  | .success | .topFrameOutOfGas | .simpleTransferStateOutOfGas | .preparationOutOfGas
  | .unmodeled => false
  | .revert | .exception _ | .collision | .createStateOutOfGas
  | .depositInvalidCode | .depositOutOfGas => true

inductive TransitionKind where
  | success
  | revert
  | exception
  | topFrameOutOfGas
  | collision
  | createStateOutOfGas
  | depositInvalidCode
  | depositOutOfGas
  | preparationOutOfGas
  | authorizationOutOfGas
  | simpleTransfer
  deriving DecidableEq, Repr

def transitionOf : ExecutionDirective → TransitionKind
  | .success => .success
  | .revert => .revert
  | .exception _ => .exception
  | .topFrameOutOfGas => .topFrameOutOfGas
  | .simpleTransferStateOutOfGas => .simpleTransfer
  | .collision => .collision
  | .createStateOutOfGas => .createStateOutOfGas
  | .depositInvalidCode => .depositInvalidCode
  | .depositOutOfGas => .depositOutOfGas
  | .preparationOutOfGas => .preparationOutOfGas
  | .unmodeled => .exception

inductive ReceiptStatus where
  | success
  | failure
  deriving DecidableEq, Repr

inductive LifecycleOutcome where
  | executed (status : ReceiptStatus) (exception : Option EvmException)
  | rejected (failure : EarlyFailure)
  | systemBypass
  | unmodeled (case : UnmodeledCase)
  | escapedException (case : UnmodeledCase)
  deriving DecidableEq, Repr

/-!
### CREATE destination admission

The typed CREATE boundary consumes one adapter-supplied destination
classification. Account-trie existence is deliberately independent of a
storage collision, including one whose destination is balance-bearing.
-/

inductive CreateDestinationStatus where
  | dead
  | existent
  deriving DecidableEq, Repr

inductive CreateCollisionKind where
  | none
  | storageOnly
  | nonZeroNonce
  | nonEmptyCode
  deriving DecidableEq, Repr

def CreateCollisionKind.isCollision : CreateCollisionKind → Bool
  | .none => false
  | .storageOnly | .nonZeroNonce | .nonEmptyCode => true

structure CreateDestinationOracle where
  /-- Logical EIP-161 account existence, independent of physical storage occupancy. -/
  accountExists : Bool
  /-- Balance observed by the single destination classification. -/
  balance : Nat
  /-- Nonce observed by the single destination classification. -/
  nonce : Nat
  /-- Whether the classified destination has empty code. -/
  codeEmpty : Bool
  /-- Whether the classified destination has nonempty storage. -/
  storageNonEmpty : Bool
  /-- The single classification read's collision fact, independent of trie existence. -/
  collision : CreateCollisionKind
  deriving DecidableEq, Repr

def CreateDestinationOracleConsistent (destination : CreateDestinationOracle) : Prop :=
  ((destination.accountExists = true) ↔
      (destination.balance ≠ 0 ∨ destination.nonce ≠ 0 ∨ destination.codeEmpty = false)) ∧
    (destination.nonce ≠ 0 → destination.collision.isCollision = true) ∧
    (destination.codeEmpty = false → destination.collision.isCollision = true) ∧
    (destination.storageNonEmpty = true → destination.collision.isCollision = true) ∧
    (destination.collision = .storageOnly → destination.storageNonEmpty = true) ∧
    (destination.collision = .nonZeroNonce → destination.nonce ≠ 0) ∧
    (destination.collision = .nonEmptyCode → destination.codeEmpty = false) ∧
    ((destination.collision = .nonZeroNonce ∨
        destination.collision = .nonEmptyCode) → destination.accountExists = true)

instance createDestinationOracleConsistentDecidable (destination : CreateDestinationOracle) :
    Decidable (CreateDestinationOracleConsistent destination) := by
  unfold CreateDestinationOracleConsistent
  infer_instance

structure CreateDestinationClassification where
  oracle : CreateDestinationOracle
  status : CreateDestinationStatus
  /-- This boundary performs exactly one destination classification/read. -/
  destinationReadCount : Nat
  deriving DecidableEq, Repr

def classifyCreateDestination (destination : CreateDestinationOracle) :
    CreateDestinationClassification :=
  { oracle := destination
    status := if destination.accountExists then .existent else .dead
    destinationReadCount := 1 }

def createStateCharge (newAccountStateGas : Nat)
    (classification : CreateDestinationClassification) : Nat :=
  if classification.status = .dead then newAccountStateGas else 0

inductive CreateAdmissionStatus where
  | invalidDestination
  | outOfGas
  | collision
  | entered
  deriving DecidableEq, Repr

structure CreateAdmissionResult where
  status : CreateAdmissionStatus
  classification : CreateDestinationClassification
  gas : GasState
  /-- Required NEW_ACCOUNT state charge after the one destination read. -/
  stateCharge : Nat
  /-- True only when the state-charge operation returned success. -/
  stateChargeApplied : Bool
  /-- Amount returned to state gas on a collision; normally zero or stateCharge. -/
  stateChargeRefilled : Nat
  /-- Exceptional CREATE admission clears execution gas. -/
  executionGasCleared : Bool
  /-- A child is entered only on the non-collision success path. -/
  childEntered : Bool
  deriving DecidableEq, Repr

def clearCreateExecutionGas (gas : GasState) : GasState :=
  { gas with gasLeft := 0 }

/-!
`admitCreate` checks source-fact consistency after constructing the single
classification, then charges state gas, and only then branches on collision.
The collision branch refills before clearing execution gas so a state charge
that spilled from `gasLeft` is rolled back without making that execution gas
available after the exceptional halt.
-/
def admitCreate (gas : GasState) (newAccountStateGas : Nat)
    (destination : CreateDestinationOracle) : CreateAdmissionResult :=
  let classification := classifyCreateDestination destination
  let stateCharge := createStateCharge newAccountStateGas classification
  if _h : ¬ CreateDestinationOracleConsistent destination then
    { status := .invalidDestination
      classification
      gas
      stateCharge := 0
      stateChargeApplied := false
      stateChargeRefilled := 0
      executionGasCleared := false
      childEntered := false }
  else
    match GasMachine.chargeState stateCharge gas with
    | .error _ =>
        { status := .outOfGas
          classification
          gas := clearCreateExecutionGas gas
          stateCharge
          stateChargeApplied := false
          stateChargeRefilled := 0
          executionGasCleared := true
          childEntered := false }
    | .ok chargedGas =>
        if destination.collision.isCollision then
          let refilledGas := GasMachine.refillState stateCharge chargedGas
          { status := .collision
            classification
            gas := clearCreateExecutionGas refilledGas
            stateCharge
            stateChargeApplied := classification.status = .dead
            stateChargeRefilled := stateCharge
            executionGasCleared := true
            childEntered := false }
        else
          { status := .entered
            classification
            gas := chargedGas
            stateCharge
            stateChargeApplied := classification.status = .dead
            stateChargeRefilled := 0
            executionGasCleared := false
            childEntered := true }

abbrev CreateAdmissionKernel :=
  GasState → Nat → CreateDestinationOracle → CreateAdmissionResult

structure GasTransition where
  gasLeft : Nat
  stateGasReservoir : Nat
  stateGasFromGasLeft : Nat
  refundCounter : Nat
  evmStateGasUsed : Nat
  deriving DecidableEq, Repr

structure FeeSchedule where
  effectiveGasPrice : Nat
  premiumPerGas : Nat
  baseFeePerGas : Nat
  blobBaseFee : Nat
  feeCollectorEnabled : Bool
  overflow : Bool
  deriving DecidableEq, Repr

structure Input where
  loadNonceFromState : Bool
  entryKind : EntryKind
  mode : ExecutionMode
  tracerIsTracingState : Bool
  forcedRestoreFailure : Option RestoreFailure
  /-- Result of production sender recovery: CallAndRestore deletes the
  temporary sender instead of committing after restoring the baseline. -/
  deleteCallerAccount : Bool
  hasAuthorizationList : Bool
  initialization : TransactionGas.InitializationInput
  initialRefundCounter : Nat
  initialStateGasUsed : Nat
  calldataFloorGasCost : Nat
  value : Nat
  validation : ValidationFailure → Bool
  senderValid : Bool
  gasPurchaseSucceeds : Bool
  nonceDecision : Option NonceFailure
  authorization : AuthorizationDecision
  authorizationGas : GasTransition
  /-- Adapter-observed state-gas amount for a non-CREATE recipient charge. -/
  recipientStateCharge : Nat
  createDestination : CreateDestinationOracle
  newAccountStateGas : Nat
  executionGas : GasTransition
  environmentFailure : Option PreparationFailure
  execution : ExecutionDirective
  destroyList : List Nat
  destroyRefund : Nat
  codeInsertExecutionRefund : Nat
  refundQuotient : Nat
  fees : FeeSchedule
  priorCumulativeReceiptGas : Nat
  priorBlockExecutionGas : Nat
  priorBlockStateGas : Nat
  postStateRoot : Option Nat
  eip8037Enabled : Bool
  eip7778Enabled : Bool

structure Baseline where
  durableWorld : Nat
  reversibleWorld : Nat
  senderNonce : Nat
  senderBalance : Nat
  recipientBalance : Nat
  beneficiaryBalance : Nat
  feeCollectorBalance : Nat
  reservedGas : Nat
  logs : List Nat
  destroyList : List Nat
  deriving DecidableEq, Repr

structure PreparationSnapshot where
  durableWorld : Nat
  reversibleWorld : Nat
  senderNonce : Nat
  senderBalance : Nat
  recipientBalance : Nat
  beneficiaryBalance : Nat
  feeCollectorBalance : Nat
  gas : TransactionGas.SettlementInput
  stateGasFromGasLeft : Nat
  logs : List Nat
  destroyList : List Nat
  destroyListFinalized : Bool
  deriving DecidableEq, Repr

structure ExecutionSnapshot where
  durableWorld : Nat
  reversibleWorld : Nat
  senderNonce : Nat
  senderBalance : Nat
  recipientBalance : Nat
  beneficiaryBalance : Nat
  feeCollectorBalance : Nat
  /-- Gas state at the top-level execution boundary. Revert restores only
  the state-gas delta charged by that execution, preserving execution gas. -/
  gas : GasState
  logs : List Nat
  destroyList : List Nat
  destroyListFinalized : Bool
  deriving DecidableEq, Repr

structure ReceiptObservation where
  status : ReceiptStatus
  gasUsed : Nat
  cumulativeGasUsed : Nat
  logs : List Nat
  stateRoot : Option Nat
  deriving DecidableEq, Repr

/-- The full gas shape captured before EIP-8037 preparation can mutate it. -/
structure PrePreparationGasSnapshot where
  gas : TransactionGas.SettlementInput
  stateGasFromGasLeft : Nat
  deriving DecidableEq, Repr

/-- The state-gas portion restored by `EthereumGasPolicy.ResetForHalt`. -/
structure Eip8037HaltBaseline where
  stateGasReservoir : Nat
  stateGasFromGasLeft : Nat
  stateGasUsed : Nat
  deriving DecidableEq, Repr

structure State where
  baseline : Baseline
  durableWorld : Nat
  reversibleWorld : Nat
  senderNonce : Nat
  senderBalance : Nat
  recipientBalance : Nat
  beneficiaryBalance : Nat
  feeCollectorBalance : Nat
  reservedGas : Nat
  gas : TransactionGas.SettlementInput
  stateGasFromGasLeft : Nat
  authorization : Option AuthorizationDecision
  path : Option ExecutionPath
  topFrameOutOfGas : Bool
  pendingPreparationRestore : Bool
  preparationSnapshot : Option PreparationSnapshot
  prePreparationGas : Option PrePreparationGasSnapshot
  preparationGasRestore : Option PrePreparationGasSnapshot
  eip8037HaltBaseline : Option Eip8037HaltBaseline
  topLevelSnapshot : Option ExecutionSnapshot
  topExecutionGasSnapshot : Option PrePreparationGasSnapshot
  pendingRollback : Option ExecutionSnapshot
  execution : Option ExecutionDirective
  createAdmission : Option CreateAdmissionResult
  substateFlags : SubstateFlags
  lastTransition : Option TransitionKind
  settlement : Option TransactionGas.Settlement
  scalarSettlement : Option TransactionSettlement.Result
  headerGasUsed : Nat
  blockCumulativeExecutionGas : Nat
  blockCumulativeStateGas : Nat
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
  buildUpSnapshot : Option Baseline
  nonceLoaded : Bool
  deriving DecidableEq, Repr

def initialGas (input : Input) : TransactionGas.SettlementInput :=
  let initial := TransactionGas.initializeTransactionGas input.initialization
  { txGas := input.initialization.txGas
    gasLeft := initial.gasLeft
    stateGasReservoir := initial.stateGasReservoir
    refundCounter := input.initialRefundCounter
    evmStateGasUsed := input.initialStateGasUsed
    calldataFloorGasCost := input.calldataFloorGasCost }

def gasTransitionOf (transition : GasTransition) (state : State) : State :=
  { state with
    gas := { state.gas with
      gasLeft := transition.gasLeft
      stateGasReservoir := transition.stateGasReservoir
      refundCounter := transition.refundCounter
      evmStateGasUsed := transition.evmStateGasUsed }
    stateGasFromGasLeft := transition.stateGasFromGasLeft }

def prePreparationGasSnapshotOf (state : State) : PrePreparationGasSnapshot :=
  { gas := state.gas
    stateGasFromGasLeft := state.stateGasFromGasLeft }

def eip8037HaltBaselineOf (state : State) : Eip8037HaltBaseline :=
  { stateGasReservoir := state.gas.stateGasReservoir
    stateGasFromGasLeft := state.stateGasFromGasLeft
    stateGasUsed := state.gas.evmStateGasUsed }

def eip8037HaltBaselineOfPrePreparationGas
    (snapshot : PrePreparationGasSnapshot) : Eip8037HaltBaseline :=
  { stateGasReservoir := snapshot.gas.stateGasReservoir
    stateGasFromGasLeft := snapshot.stateGasFromGasLeft
    stateGasUsed := snapshot.gas.evmStateGasUsed }

def restorePrePreparationGas
    (snapshot : PrePreparationGasSnapshot) (state : State) : State :=
  { state with
    gas := snapshot.gas
    stateGasFromGasLeft := snapshot.stateGasFromGasLeft
    preparationGasRestore := some snapshot
    eip8037HaltBaseline := some (eip8037HaltBaselineOfPrePreparationGas snapshot) }

def gasStateOf (state : State) : GasState :=
  { gasLeft := state.gas.gasLeft
    stateReservoir := state.gas.stateGasReservoir
    stateFromGasLeft := state.stateGasFromGasLeft
    stateUsed := state.gas.evmStateGasUsed
    refundCounter := Int.ofNat state.gas.refundCounter }

def withGasState (gas : GasState) (state : State) : State :=
  { state with
    gas := { state.gas with
      gasLeft := gas.gasLeft
      stateGasReservoir := gas.stateReservoir
      refundCounter := gas.refundCounter.toNat
      evmStateGasUsed := gas.stateUsed }
    stateGasFromGasLeft := gas.stateFromGasLeft }

def clearExecutionGas (transition : GasTransition) : GasTransition :=
  { transition with gasLeft := 0 }

def initialState (input : Input) : State :=
  let gas := initialGas input
  let baseline : Baseline :=
    { durableWorld := 0
      reversibleWorld := 0
      senderNonce := 0
      senderBalance := 1_000_000
      recipientBalance := 0
      beneficiaryBalance := 0
      feeCollectorBalance := 0
      reservedGas := 0
      logs := []
      destroyList := [] }
  { baseline
    durableWorld := baseline.durableWorld
    reversibleWorld := baseline.reversibleWorld
    senderNonce := baseline.senderNonce
    senderBalance := baseline.senderBalance
    recipientBalance := baseline.recipientBalance
    beneficiaryBalance := baseline.beneficiaryBalance
    feeCollectorBalance := baseline.feeCollectorBalance
    reservedGas := baseline.reservedGas
    gas
    stateGasFromGasLeft := 0
    authorization := none
    path := none
    topFrameOutOfGas := false
    pendingPreparationRestore := false
    preparationSnapshot := none
    prePreparationGas := none
    preparationGasRestore := none
    eip8037HaltBaseline := none
    topLevelSnapshot := none
    topExecutionGasSnapshot := none
    pendingRollback := none
    execution := none
    createAdmission := none
    substateFlags := { isError := false, shouldRevert := false }
    lastTransition := none
    settlement := none
    scalarSettlement := none
    headerGasUsed := 0
    blockCumulativeExecutionGas := input.priorBlockExecutionGas
    blockCumulativeStateGas := input.priorBlockStateGas
    cumulativeReceiptGas := input.priorCumulativeReceiptGas
    logs := baseline.logs
    destroyList := baseline.destroyList
    destroyListFinalized := false
    receiptOpen := false
    receiptClosed := false
    receipt := none
    commitBeforeExecution := false
    committed := false
    transientReset := false
    cleanupRequired := false
    buildUpSnapshot := none
    nonceLoaded := false }

def executionStatus : ExecutionDirective → ReceiptStatus
  | .success => .success
  | _ => .failure

def classifyDirective : ExecutionDirective → LifecycleOutcome
  | .success => .executed .success none
  | .revert => .executed .failure none
  | .simpleTransferStateOutOfGas => .executed .failure (some .outOfGas)
  | .exception kind => .executed .failure (some kind)
  | .topFrameOutOfGas | .createStateOutOfGas | .preparationOutOfGas =>
      .executed .failure (some .outOfGas)
  | .collision => .executed .failure (some .collision)
  | .depositInvalidCode => .executed .failure (some .invalidCode)
  | .depositOutOfGas => .executed .failure (some .outOfGas)
  | .unmodeled => .unmodeled .unsupportedProductionAdapter

def classify (state : State) : LifecycleOutcome :=
  match state.execution with
  | some execution => classifyDirective execution
  | none => .unmodeled .missingExecution

def feeReservePayment (input : Input) : Nat :=
  input.initialization.txGas * input.fees.effectiveGasPrice + input.fees.blobBaseFee

def preparationSnapshotOf (state : State) : PreparationSnapshot :=
  { durableWorld := state.durableWorld
    reversibleWorld := state.reversibleWorld
    senderNonce := state.senderNonce
    senderBalance := state.senderBalance
    recipientBalance := state.recipientBalance
    beneficiaryBalance := state.beneficiaryBalance
    feeCollectorBalance := state.feeCollectorBalance
    gas := state.gas
    stateGasFromGasLeft := state.stateGasFromGasLeft
    logs := state.logs
    destroyList := state.destroyList
    destroyListFinalized := state.destroyListFinalized }

def executionSnapshotOf (state : State) : ExecutionSnapshot :=
  { durableWorld := state.durableWorld
    reversibleWorld := state.reversibleWorld
    senderNonce := state.senderNonce
    senderBalance := state.senderBalance
    recipientBalance := state.recipientBalance
    beneficiaryBalance := state.beneficiaryBalance
    feeCollectorBalance := state.feeCollectorBalance
    gas := gasStateOf state
    logs := state.logs
    destroyList := state.destroyList
    destroyListFinalized := state.destroyListFinalized }

def restorePreparationSnapshot (snapshot : PreparationSnapshot) (state : State) : State :=
  { state with
    durableWorld := snapshot.durableWorld
    reversibleWorld := snapshot.reversibleWorld
    senderNonce := snapshot.senderNonce
    senderBalance := snapshot.senderBalance
    recipientBalance := snapshot.recipientBalance
    beneficiaryBalance := snapshot.beneficiaryBalance
    feeCollectorBalance := snapshot.feeCollectorBalance
    gas := snapshot.gas
    stateGasFromGasLeft := snapshot.stateGasFromGasLeft
    logs := snapshot.logs
    destroyList := snapshot.destroyList
    destroyListFinalized := snapshot.destroyListFinalized
    pendingPreparationRestore := false }

def restoredRevertGas (snapshot : ExecutionSnapshot) (state : State) : GasState :=
  if state.substateFlags.shouldRevert then
    GasMachine.refillState
      ((gasStateOf state).stateUsed - snapshot.gas.stateUsed) (gasStateOf state)
  else
    gasStateOf state

def restoreExecutionSnapshot (snapshot : ExecutionSnapshot) (state : State) : State :=
  let gas := restoredRevertGas snapshot state
  { state with
    durableWorld := snapshot.durableWorld
    reversibleWorld := snapshot.reversibleWorld
    senderNonce := snapshot.senderNonce
    senderBalance := snapshot.senderBalance
    recipientBalance := snapshot.recipientBalance
    beneficiaryBalance := snapshot.beneficiaryBalance
    feeCollectorBalance := snapshot.feeCollectorBalance
    gas := { state.gas with
      gasLeft := gas.gasLeft
      stateGasReservoir := gas.stateReservoir
      refundCounter := snapshot.gas.refundCounter.toNat
      evmStateGasUsed := gas.stateUsed }
    stateGasFromGasLeft := gas.stateFromGasLeft
    logs := snapshot.logs
    destroyList := snapshot.destroyList
    destroyListFinalized := snapshot.destroyListFinalized
    pendingRollback := none }

def restoreBaselinePreservingLogs (state : State) : State :=
  { state with
    durableWorld := state.baseline.durableWorld
    reversibleWorld := state.baseline.reversibleWorld
    senderNonce := state.baseline.senderNonce
    senderBalance := state.baseline.senderBalance
    recipientBalance := state.baseline.recipientBalance
    beneficiaryBalance := state.baseline.beneficiaryBalance
    feeCollectorBalance := state.baseline.feeCollectorBalance
    reservedGas := state.baseline.reservedGas
    destroyList := state.baseline.destroyList
    destroyListFinalized := false
    committed := false
    transientReset := false
    cleanupRequired := false }

theorem restore_preparation_snapshot_fields
    (snapshot : PreparationSnapshot) (state : State) :
    let restored := restorePreparationSnapshot snapshot state
    restored.durableWorld = snapshot.durableWorld ∧
      restored.reversibleWorld = snapshot.reversibleWorld ∧
      restored.senderNonce = snapshot.senderNonce ∧
      restored.senderBalance = snapshot.senderBalance ∧
      restored.recipientBalance = snapshot.recipientBalance ∧
      restored.beneficiaryBalance = snapshot.beneficiaryBalance ∧
      restored.feeCollectorBalance = snapshot.feeCollectorBalance ∧
      restored.gas = snapshot.gas ∧
      restored.stateGasFromGasLeft = snapshot.stateGasFromGasLeft ∧
      restored.logs = snapshot.logs ∧
      restored.destroyList = snapshot.destroyList ∧
      restored.destroyListFinalized = snapshot.destroyListFinalized := by
  simp [restorePreparationSnapshot]

theorem restore_preparation_snapshot_preserves_external_accounting
    (snapshot : PreparationSnapshot) (state : State) :
    let restored := restorePreparationSnapshot snapshot state
    restored.baseline = state.baseline ∧ restored.reservedGas = state.reservedGas ∧
      restored.authorization = state.authorization := by
  simp [restorePreparationSnapshot]

theorem restore_execution_snapshot_fields
    (snapshot : ExecutionSnapshot) (state : State) :
    let restored := restoreExecutionSnapshot snapshot state
    restored.durableWorld = snapshot.durableWorld ∧
      restored.reversibleWorld = snapshot.reversibleWorld ∧
      restored.senderNonce = snapshot.senderNonce ∧
      restored.senderBalance = snapshot.senderBalance ∧
      restored.recipientBalance = snapshot.recipientBalance ∧
      restored.beneficiaryBalance = snapshot.beneficiaryBalance ∧
      restored.feeCollectorBalance = snapshot.feeCollectorBalance ∧
      restored.gas.gasLeft = (restoredRevertGas snapshot state).gasLeft ∧
      restored.gas.stateGasReservoir = (restoredRevertGas snapshot state).stateReservoir ∧
      restored.stateGasFromGasLeft = (restoredRevertGas snapshot state).stateFromGasLeft ∧
      restored.gas.refundCounter = snapshot.gas.refundCounter.toNat ∧
      restored.gas.evmStateGasUsed = (restoredRevertGas snapshot state).stateUsed ∧
      restored.logs = snapshot.logs ∧
      restored.destroyList = snapshot.destroyList ∧
      restored.destroyListFinalized = snapshot.destroyListFinalized := by
  simp [restoreExecutionSnapshot, restoredRevertGas]

theorem restore_execution_snapshot_preserves_external_accounting
    (snapshot : ExecutionSnapshot) (state : State) :
    let restored := restoreExecutionSnapshot snapshot state
    restored.baseline = state.baseline ∧ restored.authorization = state.authorization ∧
      restored.reservedGas = state.reservedGas ∧ restored.gas.txGas = state.gas.txGas ∧
      restored.gas.calldataFloorGasCost = state.gas.calldataFloorGasCost := by
  simp [restoreExecutionSnapshot]

theorem restore_baseline_preserves_gas_observation (state : State) :
    (restoreBaselinePreservingLogs state).gas = state.gas := by
  rfl

inductive OracleEffect where
  | continue (state : State)
  | stop (failure : EarlyFailure) (state : State)
  | unmodeled (case : UnmodeledCase) (state : State)

structure AuthorizationResult where
  state : State
  decision : AuthorizationDecision

structure PreparationResult where
  state : State
  path : ExecutionPath
  topFrameOutOfGas : Bool

structure EnvironmentResult where
  state : State
  path : ExecutionPath
  topFrameOutOfGas : Bool
  failure : Option PreparationFailure

structure StateChargeResult where
  state : State
  outOfGas : Bool

structure ExecutionResult where
  state : State
  outcome : ExecutionDirective

/--
Lifecycle-control fields which adapter results may not replace except at the
explicit transition which owns that field.

World, balance, gas, log, and destroy-list contents remain adapter outputs;
route, snapshot, rollback, receipt, settlement, and cleanup ownership stays
in the lifecycle driver.
-/
structure ExecutionControlBoundary where
  baseline : Baseline
  reservedGas : Nat
  authorization : Option AuthorizationDecision
  path : Option ExecutionPath
  topFrameOutOfGas : Bool
  pendingPreparationRestore : Bool
  preparationSnapshot : Option PreparationSnapshot
  prePreparationGas : Option PrePreparationGasSnapshot
  preparationGasRestore : Option PrePreparationGasSnapshot
  eip8037HaltBaseline : Option Eip8037HaltBaseline
  topLevelSnapshot : Option ExecutionSnapshot
  topExecutionGasSnapshot : Option PrePreparationGasSnapshot
  pendingRollback : Option ExecutionSnapshot
  execution : Option ExecutionDirective
  createAdmission : Option CreateAdmissionResult
  substateFlags : SubstateFlags
  lastTransition : Option TransitionKind
  settlement : Option TransactionGas.Settlement
  scalarSettlement : Option TransactionSettlement.Result
  headerGasUsed : Nat
  blockCumulativeExecutionGas : Nat
  blockCumulativeStateGas : Nat
  cumulativeReceiptGas : Nat
  destroyListFinalized : Bool
  receiptOpen : Bool
  receiptClosed : Bool
  receipt : Option ReceiptObservation
  commitBeforeExecution : Bool
  committed : Bool
  transientReset : Bool
  cleanupRequired : Bool
  buildUpSnapshot : Option Baseline
  nonceLoaded : Bool
  deriving DecidableEq

def executionControlOf (state : State) : ExecutionControlBoundary :=
  { baseline := state.baseline
    reservedGas := state.reservedGas
    authorization := state.authorization
    path := state.path
    topFrameOutOfGas := state.topFrameOutOfGas
    pendingPreparationRestore := state.pendingPreparationRestore
    preparationSnapshot := state.preparationSnapshot
    prePreparationGas := state.prePreparationGas
    preparationGasRestore := state.preparationGasRestore
    eip8037HaltBaseline := state.eip8037HaltBaseline
    topLevelSnapshot := state.topLevelSnapshot
    topExecutionGasSnapshot := state.topExecutionGasSnapshot
    pendingRollback := state.pendingRollback
    execution := state.execution
    createAdmission := state.createAdmission
    substateFlags := state.substateFlags
    lastTransition := state.lastTransition
    settlement := state.settlement
    scalarSettlement := state.scalarSettlement
    headerGasUsed := state.headerGasUsed
    blockCumulativeExecutionGas := state.blockCumulativeExecutionGas
    blockCumulativeStateGas := state.blockCumulativeStateGas
    cumulativeReceiptGas := state.cumulativeReceiptGas
    destroyListFinalized := state.destroyListFinalized
    receiptOpen := state.receiptOpen
    receiptClosed := state.receiptClosed
    receipt := state.receipt
    commitBeforeExecution := state.commitBeforeExecution
    committed := state.committed
    transientReset := state.transientReset
    cleanupRequired := state.cleanupRequired
    buildUpSnapshot := state.buildUpSnapshot
    nonceLoaded := state.nonceLoaded }

def preservesLifecycleControl (before after : State) : Bool :=
  decide (executionControlOf after = executionControlOf before)

def preservesExecutionControl (before after : State) : Bool :=
  preservesLifecycleControl before after

structure Oracle where
  recoverSenderBeforeIntrinsicGas : Input → State → OracleEffect
  calculateIntrinsicGas : Input → State → OracleEffect
  validateStatic : Input → State → OracleEffect
  calculateEffectiveGasPrice : Input → State → OracleEffect
  recoverSenderIfNeeded : Input → State → OracleEffect
  validateSender : Input → State → OracleEffect
  buyGas : Input → State → OracleEffect
  incrementNonce : Input → State → OracleEffect
  processDelegations : Input → State → AuthorizationResult
  prepareSimpleTransferFastPath : Input → State → PreparationResult
  commitBeforeExecution : Input → State → OracleEffect
  calculateAvailableGas : Input → State → OracleEffect
  buildExecutionEnvironment : Input → State → EnvironmentResult
  executeEvmCall : Input → State → ExecutionResult
  payRefund : Input → State → OracleEffect
  payFees : Input → State → OracleEffect
  finalizeDestroyList : Input → State → OracleEffect
  restore : Input → State → OracleEffect
  commit : Input → State → OracleEffect
  resetTransient : Input → State → OracleEffect
  computeStateRoot : Input → State → Nat
  makeReceipt : Input → State → ReceiptObservation

def validationEffect (input : Input) (state : State) : OracleEffect :=
  if input.validation .senderNotSpecified then
    .stop (.validation .senderNotSpecified) state
  else if input.validation .nonceOverflow && !input.mode.skipsValidation then
    .stop (.validation .nonceOverflow) state
  else if input.validation .malformedTransaction then
    .stop (.validation .malformedTransaction) state
  else if input.validation .gasLimitBelowIntrinsic then
    .stop (.validation .gasLimitBelowIntrinsic) state
  else if input.validation .gasLimitBelowFloor then
    .stop (.validation .gasLimitBelowFloor) state
  else if input.validation .blockGasLimitExceeded && !input.mode.skipsValidation then
    .stop (.validation .blockGasLimitExceeded) state
  else if input.validation .transactionSizeOverMaxInitCodeSize then
    .stop (.validation .transactionSizeOverMaxInitCodeSize) state
  else
    .continue state

def effectiveGasPriceEffect (_input : Input) (state : State) : OracleEffect :=
  .continue state

def senderEffect (input : Input) (state : State) : OracleEffect :=
  match input.forcedRestoreFailure with
  | some (.sender failure) => .stop (.sender failure) state
  | _ =>
      if input.mode.skipsValidation || input.senderValid then .continue state
      else .stop (.sender .senderHasDeployedCode) state

def buyGasEffect (input : Input) (state : State) : OracleEffect :=
  match input.forcedRestoreFailure with
  | some .maxFee => .stop (.validation .maxFeePerGasBelowBaseFee) state
  | some .balance => .stop (.validation .insufficientSenderBalance) state
  | some .feeOverflow => .unmodeled .feeArithmeticOverflow state
  | _ =>
      if input.validation .maxFeePerGasBelowBaseFee then
        .stop (.validation .maxFeePerGasBelowBaseFee) state
      else if input.validation .insufficientSenderBalance then
        .stop (.validation .insufficientSenderBalance) state
      else if input.fees.overflow then
        .unmodeled .feeArithmeticOverflow state
      else if !input.gasPurchaseSucceeds then
        .stop .gasPurchase state
      else
        .continue
          { state with
            reservedGas := state.reservedGas + state.gas.txGas
            senderBalance := state.senderBalance - feeReservePayment input
            cleanupRequired := true }

def incrementNonceEffect (input : Input) (state : State) : OracleEffect :=
  match input.forcedRestoreFailure with
  | some (.nonce failure) => .stop (.nonce failure) state
  | _ =>
      if input.mode.skipsValidation then
        .continue { state with senderNonce := state.senderNonce + 1, cleanupRequired := true }
      else
        match input.nonceDecision with
        | none => .continue { state with senderNonce := state.senderNonce + 1, cleanupRequired := true }
        | some .overflow => .stop (.nonce .overflow) state
        | some .tooHigh => .stop (.nonce .tooHigh) state
        | some .tooLow => .stop (.nonce .tooLow) state

def processDelegationsEffect (input : Input) (state : State) : AuthorizationResult :=
  let withGas := gasTransitionOf input.authorizationGas state
  match input.authorization with
  | .none =>
      { state := withGas, decision := .none }
  | .applied count =>
      { state := { withGas with durableWorld := withGas.durableWorld + count }
        decision := .applied count }
  | .skippedInvalid count =>
      { state := withGas, decision := .skippedInvalid count }
  | .outOfGas appliedBeforeFailure =>
      { state := { withGas with
          durableWorld := withGas.durableWorld + appliedBeforeFailure
          reversibleWorld := withGas.reversibleWorld + appliedBeforeFailure
          logs := withGas.logs ++ [appliedBeforeFailure] }
        decision := .outOfGas appliedBeforeFailure }

def prepareEffect (input : Input) (state : State) : PreparationResult :=
  { state
    path := input.entryKind.path
    topFrameOutOfGas := false }

def environmentEffect (input : Input) (state : State) : EnvironmentResult :=
  { state
    path := input.entryKind.path
    topFrameOutOfGas := false
    failure := input.environmentFailure }

def applyValueMutation (input : Input) (state : State) : State :=
  { state with
    reversibleWorld := state.reversibleWorld + 1
    senderBalance := state.senderBalance - input.value
    recipientBalance := state.recipientBalance + input.value
    logs := state.logs ++ [input.value]
    destroyList := input.destroyList }

def executionEffect (input : Input) (state : State) : ExecutionResult :=
  if state.topFrameOutOfGas && input.execution != .createStateOutOfGas &&
      input.execution != .preparationOutOfGas then
    { state := gasTransitionOf (clearExecutionGas input.executionGas) state
      outcome := .topFrameOutOfGas }
  else
    match input.execution with
    | .success =>
        { state := applyValueMutation input (gasTransitionOf input.executionGas state)
          outcome := .success }
    | .revert =>
        { state := applyValueMutation input (gasTransitionOf input.executionGas state)
          outcome := .revert }
    | .exception kind =>
        { state := applyValueMutation input (gasTransitionOf input.executionGas state)
          outcome := .exception kind }
    | .topFrameOutOfGas =>
        { state := gasTransitionOf (clearExecutionGas input.executionGas) state
          outcome := .topFrameOutOfGas }
    | .simpleTransferStateOutOfGas =>
        { state := gasTransitionOf (clearExecutionGas input.executionGas) state
          outcome := .simpleTransferStateOutOfGas }
    | .collision =>
        { state := applyValueMutation input (gasTransitionOf (clearExecutionGas input.executionGas) state)
          outcome := .collision }
    | .createStateOutOfGas =>
        { state := applyValueMutation input (gasTransitionOf (clearExecutionGas input.executionGas) state)
          outcome := .createStateOutOfGas }
    | .depositInvalidCode =>
        { state := applyValueMutation input (gasTransitionOf (clearExecutionGas input.executionGas) state)
          outcome := .depositInvalidCode }
    | .depositOutOfGas =>
        { state := applyValueMutation input (gasTransitionOf (clearExecutionGas input.executionGas) state)
          outcome := .depositOutOfGas }
    | .preparationOutOfGas =>
        { state := gasTransitionOf (clearExecutionGas input.executionGas) state
          outcome := .preparationOutOfGas }
    | .unmodeled =>
        { state := gasTransitionOf input.executionGas state
          outcome := .unmodeled }

def simpleTransferSuccessEffect (input : Input) (state : State) : ExecutionResult :=
  { state := applyValueMutation input state
    outcome := .success }

theorem simple_transfer_success_preserves_charged_gas (input : Input) (state : State) :
    (simpleTransferSuccessEffect input state).state.gas = state.gas ∧
      (simpleTransferSuccessEffect input state).state.stateGasFromGasLeft = state.stateGasFromGasLeft := by
  simp [simpleTransferSuccessEffect, applyValueMutation]

def routeDirectiveAdmissible (input : Input) (state : State) (path : ExecutionPath) : Bool :=
  match input.entryKind, path with
  | .simpleTransfer, .simpleTransfer =>
      decide (input.execution = .success ∧ state.topFrameOutOfGas = false)
  | .messageCall, .evm =>
      decide (input.execution ≠ .simpleTransferStateOutOfGas ∧
        input.execution ≠ .collision ∧ input.execution ≠ .createStateOutOfGas ∧
        input.execution ≠ .depositInvalidCode ∧ input.execution ≠ .depositOutOfGas)
  | .contractCreation, .evm => decide (input.execution ≠ .simpleTransferStateOutOfGas)
  | _, _ => false

/--
Consumes the adapter-observed non-CREATE recipient state charge. The failed
branch is intentionally atomic, matching `TryConsumeStateGas`.
-/
def tryConsumeRecipientStateGas (input : Input) (state : State) : StateChargeResult :=
  match GasMachine.chargeState input.recipientStateCharge (gasStateOf state) with
  | .ok gas => { state := withGasState gas state, outOfGas := false }
  | .error _ => { state, outOfGas := true }

theorem failed_recipient_state_charge_is_atomic (input : Input) (state : State)
    (h : GasMachine.chargeState input.recipientStateCharge (gasStateOf state) = .error .outOfGas) :
    (tryConsumeRecipientStateGas input state).outOfGas = true ∧
      (tryConsumeRecipientStateGas input state).state = state := by
  simp [tryConsumeRecipientStateGas, h]

def standardComputeStateRoot (_input : Input) (state : State) : Nat :=
  state.durableWorld + state.reversibleWorld + state.senderNonce +
    state.senderBalance + state.recipientBalance + state.beneficiaryBalance +
    state.feeCollectorBalance

def scalarSettlementInput (input : Input) (state : State) : TransactionSettlement.Input :=
  { transactionGasLimit := state.gas.txGas
    preRefundGas := state.gas.txGas - state.gas.gasLeft - state.gas.stateGasReservoir
    refundCounter := state.gas.refundCounter
    destroyCount := Int.ofNat state.destroyList.length
    destroyRefund := input.destroyRefund
    codeInsertExecutionRefund := input.codeInsertExecutionRefund
    calldataFloorGas := state.gas.calldataFloorGasCost
    stateGasUsed := state.gas.evmStateGasUsed
    refundQuotient := input.refundQuotient
    isError := state.substateFlags.isError
    shouldRevert := state.substateFlags.shouldRevert
    isEip8037Enabled := input.eip8037Enabled
    isEip7778Enabled := input.eip7778Enabled }

/--
The EIP-8037 exceptional-halt path enters scalar settlement after halt
preparation has restored the state-gas baseline, cleared execution gas, and
preserved the state reservoir. It avoids the normal error rule that substitutes
the full transaction gas limit; EIP-8037 supplies no code-insertion execution
refund on this path.
-/
def eip8037HaltSettlementInput (input : Input) (state : State) :
    TransactionSettlement.Input :=
  { scalarSettlementInput input state with
    preRefundGas := state.gas.txGas - state.gas.stateGasReservoir
    refundCounter := 0
    destroyCount := 0
    destroyRefund := 0
    codeInsertExecutionRefund := 0
    isError := false
    shouldRevert := true }

def routesThroughEip8037Halt : ExecutionDirective → Bool
  | .exception _ | .topFrameOutOfGas | .collision | .createStateOutOfGas
  | .depositInvalidCode | .depositOutOfGas | .preparationOutOfGas => true
  | _ => false

/--
`CompleteEip8037Halt` is an EVM-frame helper. The simple-transfer fast path
has its separate `Refund` route and must not select this normalization.
-/
def usesEip8037HaltSettlement (input : Input) (state : State) : Bool :=
  input.eip8037Enabled && state.path == some .evm &&
    match state.execution with
    | some execution => routesThroughEip8037Halt execution
    | none => false

def scalarSettlementInputForState (input : Input) (state : State) :
    TransactionSettlement.Input :=
  if usesEip8037HaltSettlement input state then
    eip8037HaltSettlementInput input state
  else
    scalarSettlementInput input state

/--
Models `CompleteEip8037Halt`: restore the state-gas baseline, clear all
exposed execution gas, then expose the result to both settlement dimensions.
-/
def completeEip8037HaltState (state : State) : Option State :=
  match state.eip8037HaltBaseline with
  | none => none
  | some baseline =>
      some
        { state with
          gas := { state.gas with
            gasLeft := 0
            stateGasReservoir := baseline.stateGasReservoir
            refundCounter := 0
            evmStateGasUsed := baseline.stateGasUsed }
          stateGasFromGasLeft := baseline.stateGasFromGasLeft }

def settlementStateForExecution (input : Input) (state : State) : Option State :=
  if usesEip8037HaltSettlement input state then
    completeEip8037HaltState state
  else
    some state

def dimensionalSettlementInput (state : State) : TransactionGas.SettlementInput :=
  { state.gas with
    refundCounter :=
      if state.substateFlags.isError || state.substateFlags.shouldRevert then 0
      else state.gas.refundCounter }

def standardPayRefund (input : Input) (state : State) : OracleEffect :=
  match state.scalarSettlement with
  | some settlement =>
      let refundGas := state.gas.txGas - settlement.spentGas
      .continue { state with
        senderBalance := state.senderBalance + refundGas * input.fees.effectiveGasPrice }
  | none => .unmodeled .missingSettlement state

def standardPayFees (input : Input) (state : State) : OracleEffect :=
  match state.scalarSettlement with
  | some settlement =>
      let beneficiaryPayment := settlement.spentGas * input.fees.premiumPerGas
      let effectiveBaseFee := min input.fees.baseFeePerGas input.fees.effectiveGasPrice
      let basePayment := settlement.spentGas * effectiveBaseFee + input.fees.blobBaseFee
      .continue { state with
        beneficiaryBalance := state.beneficiaryBalance + beneficiaryPayment
        feeCollectorBalance :=
          if input.fees.feeCollectorEnabled then state.feeCollectorBalance + basePayment
          else state.feeCollectorBalance }
  | none => .unmodeled .missingSettlement state

def standardFinalizeDestroyList (_input : Input) (state : State) : OracleEffect :=
  .continue { state with
    durableWorld := state.durableWorld + state.destroyList.length
    logs := state.logs ++ state.destroyList
    destroyList := []
    destroyListFinalized := true }

def standardMakeReceipt (input : Input) (state : State) : ReceiptObservation :=
  let gasUsed := match state.scalarSettlement with
    | some settlement => settlement.spentGas
    | none => 0
  { status := match state.execution with
      | some .success => .success
      | _ => .failure
    gasUsed
    cumulativeGasUsed := state.cumulativeReceiptGas
    logs := state.logs
    stateRoot := input.postStateRoot }

def nonsemanticFixtureOracle : Oracle :=
  { recoverSenderBeforeIntrinsicGas := fun _ state => .continue state
    calculateIntrinsicGas := fun _ state => .continue state
    validateStatic := validationEffect
    calculateEffectiveGasPrice := effectiveGasPriceEffect
    recoverSenderIfNeeded := fun _ state => .continue state
    validateSender := senderEffect
    buyGas := buyGasEffect
    incrementNonce := incrementNonceEffect
    processDelegations := processDelegationsEffect
    prepareSimpleTransferFastPath := prepareEffect
    commitBeforeExecution := fun _ state =>
      .continue { state with commitBeforeExecution := true }
    calculateAvailableGas := fun input state =>
      if input.initialization.intrinsicGas ≤ input.initialization.txGas then .continue state
      else .stop (.validation .gasLimitBelowIntrinsic) state
    buildExecutionEnvironment := environmentEffect
    executeEvmCall := executionEffect
    payRefund := standardPayRefund
    payFees := standardPayFees
    finalizeDestroyList := standardFinalizeDestroyList
    restore := fun _ state => .continue (restoreBaselinePreservingLogs state)
    commit := fun _ state => .continue { state with committed := true, cleanupRequired := false }
    resetTransient := fun _ state => .continue { state with transientReset := true, cleanupRequired := false }
    computeStateRoot := standardComputeStateRoot
    makeReceipt := standardMakeReceipt }

structure Cursor where
  state : State
  trace : List Event

inductive Journey where
  | active (cursor : Cursor)
  | rejected (failure : EarlyFailure) (cursor : Cursor)
  | unmodeled (case : UnmodeledCase) (cursor : Cursor)
  | escapedException (case : UnmodeledCase) (cursor : Cursor)

def Journey.bind (journey : Journey) (next : Cursor → Journey) : Journey :=
  match journey with
  | .active cursor => next cursor
  | .rejected failure cursor => .rejected failure cursor
  | .unmodeled case cursor => .unmodeled case cursor
  | .escapedException case cursor => .escapedException case cursor

def emit (event : Event) (cursor : Cursor) : Cursor :=
  { cursor with trace := cursor.trace ++ [event] }

def preservesLifecycleControlExcept (before after : State)
    (allowReservedGas allowDestroyListFinalized allowCommitBeforeExecution
      allowCommitted allowTransientReset allowCleanupRequired : Bool) : Bool :=
  let normalized :=
    { after with
      reservedGas := if allowReservedGas then before.reservedGas else after.reservedGas
      destroyListFinalized :=
        if allowDestroyListFinalized then before.destroyListFinalized else after.destroyListFinalized
      commitBeforeExecution :=
        if allowCommitBeforeExecution then before.commitBeforeExecution else after.commitBeforeExecution
      committed := if allowCommitted then before.committed else after.committed
      transientReset := if allowTransientReset then before.transientReset else after.transientReset
      cleanupRequired := if allowCleanupRequired then before.cleanupRequired else after.cleanupRequired }
  preservesLifecycleControl before normalized

def continuedOracleEffectControlAdmissible (event : Event) (before after : State) : Bool :=
  match event with
  | .buyGas =>
      preservesLifecycleControlExcept before after true false false false false true &&
        decide (after.reservedGas = before.reservedGas + before.gas.txGas) &&
        decide (after.cleanupRequired = true)
  | .incrementNonce =>
      preservesLifecycleControlExcept before after false false false false false true &&
        decide (after.senderNonce = before.senderNonce + 1) &&
        decide (after.cleanupRequired = true)
  | .commitBeforeExecution =>
      preservesLifecycleControlExcept before after false false true false false false &&
        decide (after.commitBeforeExecution = true)
  | .destroyListFinalize =>
      preservesLifecycleControlExcept before after false true false false false false &&
        decide (after.destroyListFinalized = true)
  | .restore =>
      preservesLifecycleControlExcept before after true true false true true true &&
        decide (after.reservedGas = after.baseline.reservedGas) &&
        decide (after.destroyListFinalized = false) &&
        decide (after.committed = false) &&
        decide (after.transientReset = false) &&
        decide (after.cleanupRequired = false)
  | .commit =>
      preservesLifecycleControlExcept before after false false false true false true &&
        decide (after.committed = true) &&
        decide (after.cleanupRequired = false)
  | .resetTransient =>
      preservesLifecycleControlExcept before after false false false false true true &&
        decide (after.transientReset = true) &&
        decide (after.cleanupRequired = false)
  | _ => preservesLifecycleControl before after

def oracleEffectControlAdmissible (event : Event) (before : State) (effect : OracleEffect) : Bool :=
  match effect with
  | .continue after => continuedOracleEffectControlAdmissible event before after
  | .stop _ after | .unmodeled _ after => preservesLifecycleControl before after

def effectAt (event : Event) (effect : OracleEffect) (cursor : Cursor) : Journey :=
  let cursor' := emit event cursor
  if !oracleEffectControlAdmissible event cursor.state effect then
    .unmodeled .inconsistentOracleEffectResult cursor'
  else
    match effect with
    | .continue state => .active { cursor' with state := state }
    | .stop failure state => .rejected failure { cursor' with state := state }
    | .unmodeled case state => .unmodeled case { cursor' with state := state }

def chargeAt (input : Input) (event : Event)
    (restorePreparationOnOutOfGas : Bool) (cursor : Cursor) : Journey :=
  let result := tryConsumeRecipientStateGas input cursor.state
  let state' :=
    { result.state with
      topFrameOutOfGas := cursor.state.topFrameOutOfGas || result.state.topFrameOutOfGas || result.outOfGas
      pendingPreparationRestore :=
        cursor.state.pendingPreparationRestore || result.state.pendingPreparationRestore ||
          (restorePreparationOnOutOfGas && result.outOfGas && result.state.preparationSnapshot.isSome) }
  .active (emit event { cursor with state := state' })

def capturePrePreparationGasAt (input : Input) (cursor : Cursor) : Cursor :=
  if input.eip8037Enabled then
    { cursor with state :=
        { cursor.state with prePreparationGas := some (prePreparationGasSnapshotOf cursor.state) } }
  else
    cursor

def captureEip8037HaltBaselineAt (input : Input) (cursor : Cursor) : Cursor :=
  if input.eip8037Enabled then
    { cursor with state :=
        { cursor.state with eip8037HaltBaseline := some (eip8037HaltBaselineOf cursor.state) } }
  else
    cursor

def markPreparationOutOfGas (input : Input) (cursor : Cursor) : Cursor :=
  if input.eip8037Enabled && input.execution = .preparationOutOfGas then
    { cursor with state :=
        { cursor.state with
          topFrameOutOfGas := true
          pendingPreparationRestore := cursor.state.pendingPreparationRestore ||
            cursor.state.preparationSnapshot.isSome } }
  else
    cursor

def restorePrePreparationGasAt (input : Input) (cursor : Cursor) : Journey :=
  if !input.eip8037Enabled || !cursor.state.topFrameOutOfGas then
    .active cursor
  else
    match cursor.state.prePreparationGas with
    | none => .unmodeled .missingPreparationSnapshot cursor
    | some snapshot =>
        let restored := restorePrePreparationGas snapshot cursor.state
        .active { cursor with state := { restored with topFrameOutOfGas := true } }

def authorizationAt (oracle : Oracle) (input : Input) (cursor : Cursor) : Journey :=
  if !input.hasAuthorizationList then
    .active cursor
  else
    let snapshotCursor :=
      if input.eip8037Enabled then
        emit .authorizationSnapshot
          { cursor with state :=
              { cursor.state with preparationSnapshot := some (preparationSnapshotOf cursor.state) } }
      else
        cursor
    let result := oracle.processDelegations input snapshotCursor.state
    let processed := emit .processDelegations snapshotCursor
    if !preservesLifecycleControl snapshotCursor.state result.state then
      .unmodeled .inconsistentAuthorizationResult processed
    else
      let state' := { result.state with authorization := some result.decision }
      let state'' := match result.decision with
        | .outOfGas _ =>
            { state' with
              topFrameOutOfGas := true
              pendingPreparationRestore := true
              cleanupRequired := true }
        | _ => state'
      .active { processed with state := state'' }

def preparationRestoreAt (cursor : Cursor) : Journey :=
  if !cursor.state.pendingPreparationRestore then
    .active cursor
  else
    match cursor.state.preparationSnapshot with
    | none => .unmodeled .missingPreparationSnapshot cursor
    | some snapshot =>
        let restored := restorePreparationSnapshot snapshot cursor.state
        .active (emit .authorizationRestore
          { cursor with state := { restored with topFrameOutOfGas := true } })

theorem preparation_restore_at_restores_complete_snapshot
    (snapshot : PreparationSnapshot) (state : State) :
    let cursor : Cursor :=
      { state := { state with
          preparationSnapshot := some snapshot
          pendingPreparationRestore := true }
        trace := [] }
    match preparationRestoreAt cursor with
    | .active restored =>
        restored.trace = [.authorizationRestore] ∧
          restored.state.durableWorld = snapshot.durableWorld ∧
          restored.state.reversibleWorld = snapshot.reversibleWorld ∧
          restored.state.senderNonce = snapshot.senderNonce ∧
          restored.state.senderBalance = snapshot.senderBalance ∧
          restored.state.recipientBalance = snapshot.recipientBalance ∧
          restored.state.beneficiaryBalance = snapshot.beneficiaryBalance ∧
          restored.state.feeCollectorBalance = snapshot.feeCollectorBalance ∧
          restored.state.gas = snapshot.gas ∧
          restored.state.stateGasFromGasLeft = snapshot.stateGasFromGasLeft ∧
          restored.state.logs = snapshot.logs ∧
          restored.state.destroyList = snapshot.destroyList ∧
          restored.state.destroyListFinalized = snapshot.destroyListFinalized ∧
          restored.state.reservedGas = state.reservedGas
    | _ => False := by
  simp [preparationRestoreAt, restorePreparationSnapshot, emit]

def preparationAt (oracle : Oracle) (input : Input) (cursor : Cursor) : Journey :=
  let result := oracle.prepareSimpleTransferFastPath input cursor.state
  let cursor' := emit .prepareSimpleTransferFastPath cursor
  if !preservesLifecycleControl cursor.state result.state then
    .unmodeled .inconsistentPreparationResult cursor'
  else
    .active { cursor' with state :=
      { result.state with
        path := some result.path
        topFrameOutOfGas := cursor.state.topFrameOutOfGas || result.topFrameOutOfGas } }

def environmentSignalsTopFrameOutOfGas (cursor : Cursor) (result : EnvironmentResult) : Bool :=
  cursor.state.topFrameOutOfGas || result.topFrameOutOfGas ||
    (match result.failure with
    | some .delegatedTargetOutOfGas => true
    | _ => false)

def environmentPathAdmissible (cursor : Cursor) (result : EnvironmentResult) : Bool :=
  decide (cursor.state.path = some result.path)

def applyEnvironmentResult (cursor : Cursor) (result : EnvironmentResult) : State :=
  let topFrameOutOfGas := environmentSignalsTopFrameOutOfGas cursor result
  { result.state with
    authorization := cursor.state.authorization
    path := cursor.state.path
    topFrameOutOfGas
    pendingPreparationRestore := cursor.state.pendingPreparationRestore ||
      (topFrameOutOfGas && cursor.state.preparationSnapshot.isSome)
    preparationSnapshot := cursor.state.preparationSnapshot
    prePreparationGas := cursor.state.prePreparationGas
    preparationGasRestore := cursor.state.preparationGasRestore
    eip8037HaltBaseline := cursor.state.eip8037HaltBaseline
    topLevelSnapshot := cursor.state.topLevelSnapshot
    topExecutionGasSnapshot := cursor.state.topExecutionGasSnapshot
    execution := cursor.state.execution
    createAdmission := cursor.state.createAdmission
    pendingRollback := cursor.state.pendingRollback }

def environmentAt (oracle : Oracle) (input : Input) (cursor : Cursor) : Journey :=
  let result := oracle.buildExecutionEnvironment input cursor.state
  let cursor' := emit .buildExecutionEnvironment cursor
  if !environmentPathAdmissible cursor result then
    .unmodeled .invalidExecutionRoute cursor'
  else if !preservesLifecycleControl cursor.state result.state then
    .unmodeled .inconsistentEnvironmentResult cursor'
  else
    let state := applyEnvironmentResult cursor result
    match result.failure with
    | some .delegatedTargetOutOfGas => .active { cursor' with state }
    | some _ => .escapedException .buildEnvironmentException { cursor' with state }
    | none => .active { cursor' with state }

def commitBeforeNeeded (input : Input) (state : State) : Bool :=
  input.mode.commits &&
    (state.path = some .evm || input.mode.restores || input.tracerIsTracingState)

def commitBeforeAt (oracle : Oracle) (input : Input) (cursor : Cursor) : Journey :=
  if commitBeforeNeeded input cursor.state then
    effectAt .commitBeforeExecution
      (oracle.commitBeforeExecution input cursor.state) cursor
  else
    .active cursor

def setExecutionResult (result : ExecutionResult) (cursor : Cursor) : Cursor :=
  let pendingRollback :=
    if executionNeedsRollback result.outcome then cursor.state.topLevelSnapshot else none
  { cursor with state :=
      { result.state with
        execution := some result.outcome
        substateFlags := flagsOf result.outcome
        pendingRollback
        lastTransition := some (transitionOf result.outcome) } }

def evmExecutionDirectiveAdmissible (input : Input) (directive : ExecutionDirective) : Bool :=
  match input.entryKind with
  | .messageCall =>
      match directive with
      | .success | .revert | .exception _ | .unmodeled => true
      | _ => false
  | .contractCreation =>
      match directive with
      | .success | .revert | .exception _ | .depositInvalidCode | .depositOutOfGas
      | .unmodeled => true
      | _ => false
  | .simpleTransfer | .systemCall => false

def forcedTopFrameDirective (directive : ExecutionDirective) : ExecutionDirective :=
  match directive with
  | .createStateOutOfGas | .preparationOutOfGas => directive
  | _ => .topFrameOutOfGas

def executionAt (oracle : Oracle) (input : Input) (cursor : Cursor) : Journey :=
  let result := oracle.executeEvmCall input cursor.state
  if !preservesLifecycleControl cursor.state result.state then
    .unmodeled .inconsistentEvmExecutionResult cursor
  else if !evmExecutionDirectiveAdmissible input result.outcome ||
      result.outcome != input.execution then
    .unmodeled .invalidEvmExecutionDirective cursor
  else
    .active (setExecutionResult result cursor)

def simpleExecutionAt (input : Input) (cursor : Cursor) : Cursor :=
  setExecutionResult (simpleTransferSuccessEffect input cursor.state) cursor

def createAdmissionMatchesDirective (status : CreateAdmissionStatus)
    (directive : ExecutionDirective) : Bool :=
  match status with
  | .invalidDestination => true
  | .outOfGas => decide (directive = .createStateOutOfGas)
  | .collision => decide (directive = .collision)
  | .entered => decide (directive ≠ .collision ∧ directive ≠ .createStateOutOfGas ∧
    directive ≠ .simpleTransferStateOutOfGas)

def createAdmissionAt (admissionKernel : CreateAdmissionKernel) (input : Input)
    (cursor : Cursor) : Journey :=
  let read := emit .createDestinationRead cursor
  let result := admissionKernel (gasStateOf read.state) input.newAccountStateGas input.createDestination
  let admissionState :=
    { withGasState result.gas read.state with createAdmission := some result }
  let admitted := { read with state := admissionState }
  match result.status with
  | .invalidDestination => .unmodeled .invalidCreateDestination admitted
  | .outOfGas | .collision | .entered =>
      let charged :=
        if result.stateCharge = 0 then admitted else emit .createStateCharge admitted
      if !createAdmissionMatchesDirective result.status input.execution then
        .unmodeled .inconsistentCreateAdmissionDirective charged
      else
        match result.status with
        | .outOfGas =>
            let halted := emit .topFrameOutOfGas { charged with state :=
              { charged.state with topFrameOutOfGas := true } }
            .active (setExecutionResult
              { state := halted.state, outcome := .createStateOutOfGas } halted)
        | .collision =>
            let checked := emit .collisionCheck charged
            .active (setExecutionResult { state := checked.state, outcome := .collision } checked)
        | .entered => .active (emit .collisionCheck charged)
        | .invalidDestination => .unmodeled .invalidCreateDestination admitted

def topSnapshotAt (cursor : Cursor) : Cursor :=
  emit .topExecutionSnapshot
    { cursor with state :=
        { cursor.state with
          topLevelSnapshot := some (executionSnapshotOf cursor.state)
          topExecutionGasSnapshot := some (prePreparationGasSnapshotOf cursor.state) } }

def rollbackAt (cursor : Cursor) : Cursor :=
  match cursor.state.pendingRollback with
  | none => cursor
  | some snapshot => emit .executionRollback
      { cursor with state := restoreExecutionSnapshot snapshot cursor.state }

def settlementInputsValidFor (scalarInput : TransactionSettlement.Input) (state : State) : Bool :=
  decide (scalarInput.Valid ∧
    (dimensionalSettlementInput state).Valid)

def settlementInputsValid (input : Input) (state : State) : Bool :=
  match settlementStateForExecution input state with
  | none => false
  | some settlementState =>
      settlementInputsValidFor (scalarSettlementInputForState input settlementState) settlementState

def settleStateWithScalarInput (scalarInput : TransactionSettlement.Input) (input : Input)
    (state : State) : Option State :=
  if !settlementInputsValidFor scalarInput state then
    none
  else
    let scalar := TransactionSettlement.settle scalarInput
    let dimensional := TransactionGas.settle (dimensionalSettlementInput state)
    let blockExecutionGas :=
      if dimensional.executionGas > 0 || dimensional.stateGas > 0 then
        dimensional.executionGas
      else
        dimensional.paidGas
    let blockCumulativeExecutionGas :=
      if input.mode.updatesHeader then
        state.blockCumulativeExecutionGas + blockExecutionGas
      else
        state.blockCumulativeExecutionGas
    let blockCumulativeStateGas :=
      if input.mode.updatesHeader && input.eip8037Enabled then
        state.blockCumulativeStateGas + dimensional.stateGas
      else
        state.blockCumulativeStateGas
    let headerGasUsed :=
      if input.mode.updatesHeader then
        if input.eip8037Enabled then
          max blockCumulativeExecutionGas blockCumulativeStateGas
        else
          state.headerGasUsed + blockExecutionGas
      else
        state.headerGasUsed
    some { state with
      settlement := some dimensional
      scalarSettlement := some scalar
      headerGasUsed
      blockCumulativeExecutionGas
      blockCumulativeStateGas
      cumulativeReceiptGas := state.cumulativeReceiptGas + scalar.spentGas }

def settleState (input : Input) (state : State) : Option State :=
  match settlementStateForExecution input state with
  | none => none
  | some settlementState =>
      settleStateWithScalarInput (scalarSettlementInputForState input settlementState) input
        settlementState

abbrev SettlementKernel := Input → State → Option State

def preservesSettlementControl (before after : State) : Bool :=
  let normalized :=
    { after with
      settlement := before.settlement
      scalarSettlement := before.scalarSettlement
      headerGasUsed := before.headerGasUsed
      blockCumulativeExecutionGas := before.blockCumulativeExecutionGas
      blockCumulativeStateGas := before.blockCumulativeStateGas
      cumulativeReceiptGas := before.cumulativeReceiptGas }
  preservesLifecycleControl before normalized

def finalizationAt (oracle : Oracle) (input : Input) (cursor : Cursor) : Journey :=
  if input.mode.restores then
    let restored := effectAt .restore (oracle.restore input cursor.state) cursor
    if input.deleteCallerAccount then
      restored
    else
      restored.bind fun restoredCursor =>
        effectAt .commit (oracle.commit input restoredCursor.state) restoredCursor
  else if input.mode.commits then
    effectAt .commit (oracle.commit input cursor.state) cursor
  else
    effectAt .resetTransient (oracle.resetTransient input cursor.state) cursor

def receiptAt (oracle : Oracle) (input : Input) (cursor : Cursor) : Cursor :=
  let opened := emit .receiptStart { cursor with state := { cursor.state with receiptOpen := true } }
  let receipt := oracle.makeReceipt input opened.state
  let observed := emit .receiptObserve
    { opened with state := { opened.state with receipt := some receipt } }
  { observed with state := { observed.state with receiptOpen := false, receiptClosed := true } }

def settlementAt (settlementKernel : SettlementKernel) (oracle : Oracle) (input : Input)
    (cursor : Cursor) : Journey :=
  match settlementKernel input cursor.state with
  | none => .unmodeled .invalidSettlementDomain cursor
  | some settledState =>
      if !preservesSettlementControl cursor.state settledState then
        .unmodeled .inconsistentSettlementResult cursor
      else
        let settled := { cursor with state := settledState }
        let refunded := effectAt .refund (oracle.payRefund input settled.state) settled
        refunded.bind fun refundedCursor =>
          let headed := emit .headerGas refundedCursor
          let feesPaid := effectAt .payFees (oracle.payFees input headed.state) headed
          feesPaid.bind fun feesPaidCursor =>
            let destroyed :=
              if feesPaidCursor.state.destroyList.isEmpty then
                .active feesPaidCursor
              else
                effectAt .destroyListFinalize
                  (oracle.finalizeDestroyList input feesPaidCursor.state) feesPaidCursor
            destroyed.bind fun destroyedCursor =>
              let finalized := finalizationAt oracle input destroyedCursor
              finalized.bind fun finalizedCursor =>
                .active (receiptAt oracle input finalizedCursor)

def executeSimpleAt (settlementKernel : SettlementKernel) (oracle : Oracle) (input : Input)
    (cursor : Cursor) : Journey :=
  if !routeDirectiveAdmissible input cursor.state .simpleTransfer then
    .unmodeled .invalidExecutionRoute cursor
  else
    let entered := emit .simpleTransferExecution cursor
    let charged := chargeAt input .recipientStateCharge false entered
    charged.bind fun chargedCursor =>
      let halted := chargedCursor.state.topFrameOutOfGas
      let beforeExecution :=
        if halted then emit .topFrameOutOfGas { chargedCursor with state :=
          { chargedCursor.state with topFrameOutOfGas := true } }
        else emit .payValue chargedCursor
      let afterExecution :=
        if chargedCursor.state.topFrameOutOfGas then
          setExecutionResult
            { state := { beforeExecution.state with
                gas := { beforeExecution.state.gas with gasLeft := 0 } }
              outcome := .simpleTransferStateOutOfGas } beforeExecution
        else
          simpleExecutionAt input beforeExecution
      settlementAt settlementKernel oracle input afterExecution

def requiresEvmRecipientStateCharge (input : Input) : Bool :=
  input.entryKind != .contractCreation

abbrev PrePreparationGasRestoreKernel := Input → Cursor → Journey

def executeEvmAt (admissionKernel : CreateAdmissionKernel)
    (recipientStateChargeRequired : Input → Bool) (settlementKernel : SettlementKernel)
    (prePreparationGasRestore : PrePreparationGasRestoreKernel) (oracle : Oracle)
    (input : Input) (cursor : Cursor) : Journey :=
  let prePreparation := capturePrePreparationGasAt input cursor
  authorizationAt oracle input prePreparation |>.bind fun authorized =>
    let environmentJourney := environmentAt oracle input authorized
    environmentJourney.bind fun environment =>
      let baselined := captureEip8037HaltBaselineAt input environment
      let chargeJourney :=
        if baselined.state.topFrameOutOfGas || !recipientStateChargeRequired input then
          .active baselined
        else
          chargeAt input .recipientStateCharge true baselined
      chargeJourney.bind fun charged =>
        let preparationMarked := markPreparationOutOfGas input charged
        preparationRestoreAt preparationMarked |>.bind fun restored =>
          prePreparationGasRestore input restored |>.bind fun preparationGasRestored =>
            let snapshotted := topSnapshotAt preparationGasRestored
            let forcedTop := snapshotted.state.topFrameOutOfGas ||
              input.execution = .topFrameOutOfGas || input.execution = .preparationOutOfGas
              let afterTopJourney :=
                if forcedTop then
                  let halted := emit .topFrameOutOfGas { snapshotted with state :=
                    { snapshotted.state with topFrameOutOfGas := true } }
                  .active (setExecutionResult
                    { state := halted.state, outcome := forcedTopFrameDirective input.execution } halted)
              else if input.entryKind = .contractCreation then
                createAdmissionAt admissionKernel input snapshotted
              else
                .active (emit .payValue snapshotted)
            afterTopJourney.bind fun admitted =>
              let withValue :=
                if input.entryKind = .contractCreation && !forcedTop &&
                    admitted.state.execution.isNone then
                  emit .payValue admitted
                else admitted
              let vmMarked :=
                if withValue.state.execution.isSome || forcedTop then
                  withValue
                else
                  emit .vmExecution withValue
              let executedJourney :=
                if vmMarked.state.execution.isSome then .active vmMarked else executionAt oracle input vmMarked
              executedJourney.bind fun executed =>
                let deployed :=
                  if input.entryKind = .contractCreation &&
                      (executed.state.execution = some .depositInvalidCode ||
                        executed.state.execution = some .depositOutOfGas ||
                        executed.state.execution = some .success) then
                    emit .deployment executed
                  else executed
                let rolledBack := rollbackAt deployed
                settlementAt settlementKernel oracle input rolledBack

def ordinaryPipeline (admissionKernel : CreateAdmissionKernel)
    (recipientStateChargeRequired : Input → Bool) (settlementKernel : SettlementKernel)
    (prePreparationGasRestore : PrePreparationGasRestoreKernel) (oracle : Oracle)
    (input : Input) (cursor : Cursor) : Journey :=
    (effectAt .recoverSenderBeforeIntrinsicGas
      (oracle.recoverSenderBeforeIntrinsicGas input cursor.state) cursor).bind fun c1 =>
    (effectAt .calculateIntrinsicGas (oracle.calculateIntrinsicGas input c1.state) c1).bind fun c2 =>
      (effectAt .validateStatic (oracle.validateStatic input c2.state) c2).bind fun c3 =>
        (effectAt .calculateEffectiveGasPrice
          (oracle.calculateEffectiveGasPrice input c3.state) c3).bind fun c4 =>
          (effectAt .recoverSenderIfNeeded
            (oracle.recoverSenderIfNeeded input c4.state) c4).bind fun c5 =>
            (effectAt .validateSender (oracle.validateSender input c5.state) c5).bind fun c6 =>
              (effectAt .buyGas (oracle.buyGas input c6.state) c6).bind fun c7 =>
                (effectAt .incrementNonce (oracle.incrementNonce input c7.state) c7).bind fun c8 =>
                  preparationAt oracle input c8 |>.bind fun c9 =>
                    commitBeforeAt oracle input c9 |>.bind fun c10 =>
                      (effectAt .calculateAvailableGas
                        (oracle.calculateAvailableGas input c10.state) c10).bind fun c11 =>
                        match c11.state.path with
                        | some path =>
                            if !routeDirectiveAdmissible input c11.state path then
                              .unmodeled .invalidExecutionRoute c11
                            else
                              match path with
                              | .simpleTransfer => executeSimpleAt settlementKernel oracle input c11
                              | .evm =>
                                  executeEvmAt admissionKernel
                                    recipientStateChargeRequired settlementKernel prePreparationGasRestore oracle
                                    input c11
                        | none => .unmodeled .missingExecution c11

structure Run where
  outcome : LifecycleOutcome
  state : State
  trace : List Event
  deriving DecidableEq, Repr

def closeRun (outcome : LifecycleOutcome) (cursor : Cursor) : Run :=
  { outcome
    state := { cursor.state with receiptOpen := false, receiptClosed := true }
    trace := cursor.trace ++ [.endTxTrace] }

def escapeRun (case : UnmodeledCase) (cursor : Cursor) : Run :=
  { outcome := .escapedException case
    state := cursor.state
    trace := cursor.trace }

def failureNeedsRestore : EarlyFailure → Bool
  | .sender _ | .gasPurchase | .nonce _ => true
  | .validation .maxFeePerGasBelowBaseFee
  | .validation .insufficientSenderBalance => true
  | .unmodeled .feeArithmeticOverflow => true
  | _ => false

def unmodeledNeedsRestore : UnmodeledCase → Bool
  | .feeArithmeticOverflow => true
  | _ => false

def finishJourney (oracle : Oracle) (input : Input) : Journey → Run
  | .active cursor => closeRun (classify cursor.state) cursor
  | .rejected failure cursor =>
      let cleanup :=
        if input.mode.restores && (cursor.state.cleanupRequired || failureNeedsRestore failure) then
          effectAt .restore (oracle.restore input cursor.state) cursor
        else .active cursor
      match cleanup with
      | .active cleaned => closeRun (.rejected failure) cleaned
      | .rejected cleanupFailure cleaned => closeRun (.rejected cleanupFailure) cleaned
      | .unmodeled case cleaned => closeRun (.unmodeled case) cleaned
      | .escapedException case cleaned => escapeRun case cleaned
  | .unmodeled case cursor =>
      let cleanup :=
        if input.mode.restores &&
            (cursor.state.cleanupRequired || unmodeledNeedsRestore case) then
          effectAt .restore (oracle.restore input cursor.state) cursor
        else .active cursor
      match cleanup with
      | .active cleaned => closeRun (.unmodeled case) cleaned
      | .rejected failure cleaned => closeRun (.rejected failure) cleaned
      | .unmodeled cleanupCase cleaned => closeRun (.unmodeled cleanupCase) cleaned
      | .escapedException cleanupCase cleaned => escapeRun cleanupCase cleaned
  | .escapedException case cursor => escapeRun case cursor

def runWithTransactionKernelsAndPrePreparationGasRestore
    (admissionKernel : CreateAdmissionKernel)
    (recipientStateChargeRequired : Input → Bool) (settlementKernel : SettlementKernel)
    (prePreparationGasRestore : PrePreparationGasRestoreKernel) (oracle : Oracle)
    (input : Input) : Run :=
  let initial := initialState input
  let loaded :=
    if input.loadNonceFromState then
      emit .loadNonceFromState { state := { initial with nonceLoaded := true }, trace := [] }
    else
      { state := initial, trace := [] }
  let traced := emit .startNewTxTrace loaded
  let processed := emit .process traced
  let withBuildUp :=
    if input.mode = .buildUp then
      emit .buildUpSnapshot { processed with
        state := { processed.state with buildUpSnapshot := some processed.state.baseline } }
    else processed
  if input.entryKind.isSystem || input.mode = .systemSkipValidation then
    closeRun .systemBypass (emit .executeCoreSystem withBuildUp)
  else
    finishJourney oracle input
      (ordinaryPipeline admissionKernel recipientStateChargeRequired
        settlementKernel prePreparationGasRestore oracle input (emit .executeCoreOrdinary withBuildUp))

def runWithTransactionKernels (admissionKernel : CreateAdmissionKernel)
    (recipientStateChargeRequired : Input → Bool) (settlementKernel : SettlementKernel)
    (oracle : Oracle) (input : Input) : Run :=
  runWithTransactionKernelsAndPrePreparationGasRestore admissionKernel
    recipientStateChargeRequired settlementKernel restorePrePreparationGasAt oracle input

def runWithCreateAdmission (admissionKernel : CreateAdmissionKernel) (oracle : Oracle)
    (input : Input) : Run :=
  runWithTransactionKernels admissionKernel requiresEvmRecipientStateCharge settleState oracle input

def run (oracle : Oracle) (input : Input) : Run :=
  runWithCreateAdmission admitCreate oracle input

theorem run_uses_create_admission (oracle : Oracle) (input : Input) :
    run oracle input = runWithCreateAdmission admitCreate oracle input := rfl

theorem failed_directive_rollback_stage
    (snapshot : ExecutionSnapshot) (state : State) (outcome : ExecutionDirective)
    (hRollback : executionNeedsRollback outcome = true) :
    let cursor : Cursor :=
      { state := { state with topLevelSnapshot := some snapshot }, trace := [] }
    let marked := setExecutionResult { state := state, outcome } cursor
    let rolled := rollbackAt marked
    rolled.trace = [.executionRollback] ∧
      rolled.state.reversibleWorld = snapshot.reversibleWorld ∧
      rolled.state.senderBalance = snapshot.senderBalance ∧
      rolled.state.recipientBalance = snapshot.recipientBalance ∧
      rolled.state.logs = snapshot.logs ∧
      rolled.state.destroyList = snapshot.destroyList ∧
      rolled.state.pendingRollback = none := by
  simp [setExecutionResult, rollbackAt, restoreExecutionSnapshot, emit, hRollback]

def rollbackDirectives : List ExecutionDirective :=
  [.revert, .exception .stackUnderflow, .collision, .createStateOutOfGas
  , .depositInvalidCode, .depositOutOfGas]

theorem rollback_directives_are_staged :
    rollbackDirectives.all executionNeedsRollback = true := by
  native_decide

theorem classify_create_destination_reads_once (destination : CreateDestinationOracle) :
    (classifyCreateDestination destination).destinationReadCount = 1 := by
  rfl

theorem dead_create_destination_requires_new_account_charge
    (newAccountStateGas : Nat) (destination : CreateDestinationOracle)
    (hDead : destination.accountExists = false) :
    createStateCharge newAccountStateGas (classifyCreateDestination destination) =
      newAccountStateGas := by
  simp [createStateCharge, classifyCreateDestination, hDead]

theorem existing_create_destination_requires_no_new_account_charge
    (newAccountStateGas : Nat) (destination : CreateDestinationOracle)
    (hExisting : destination.accountExists = true) :
    createStateCharge newAccountStateGas (classifyCreateDestination destination) = 0 := by
  simp [createStateCharge, classifyCreateDestination, hExisting]

theorem create_admission_rejects_inconsistent_destination
    (gas : GasState) (newAccountStateGas : Nat) (destination : CreateDestinationOracle)
    (hInvalid : ¬ CreateDestinationOracleConsistent destination) :
    let result := admitCreate gas newAccountStateGas destination
    result.status = .invalidDestination ∧
      result.gas = gas ∧ result.classification.destinationReadCount = 1 ∧
      result.childEntered = false := by
  simp [admitCreate, classifyCreateDestination, hInvalid]

theorem create_admission_existing_destination_is_not_state_charged
    (gas : GasState) (newAccountStateGas : Nat) (destination : CreateDestinationOracle)
    (hConsistent : CreateDestinationOracleConsistent destination)
    (hExisting : destination.accountExists = true) :
    let result := admitCreate gas newAccountStateGas destination
    result.stateCharge = 0 ∧ result.stateChargeApplied = false ∧
      result.gas.stateReservoir = gas.stateReservoir ∧
      result.gas.stateUsed = gas.stateUsed := by
  have hChargeZero : GasMachine.chargeState 0 gas = .ok gas := by
    have hAffordable : GasMachine.stateChargeFromGasLeft 0 gas ≤ gas.gasLeft := by
      simp [GasMachine.stateChargeFromGasLeft, GasMachine.stateChargeFromReservoir]
    rw [GasMachine.chargeState_of_affordable hAffordable]
    have hFields := GasMachine.chargedState_fields 0 gas
    simp at hFields
    rcases hFields with ⟨hReservoir, hGasLeft, hFromGasLeft, hUsed, hRefund⟩
    cases gas
    congr 1
  by_cases hCollision : destination.collision.isCollision = true
  · simp [admitCreate, hConsistent, createStateCharge, classifyCreateDestination, hExisting,
      hCollision, hChargeZero, GasMachine.refillState, GasMachine.appliedRefill,
      GasMachine.refillToGasLeft, clearCreateExecutionGas]
  · simp [admitCreate, hConsistent, createStateCharge, classifyCreateDestination, hExisting,
      hCollision, hChargeZero]

theorem charge_state_oog_of_total_insufficient {amount : Nat} {gas : GasState}
    (hInsufficient : gas.gasLeft + gas.stateReservoir < amount) :
    GasMachine.chargeState amount gas = .error .outOfGas := by
  by_cases hReservoir : amount ≤ gas.stateReservoir
  · have hTotal : amount ≤ gas.gasLeft + gas.stateReservoir := by omega
    omega
  · have hReservoirLt : gas.stateReservoir < amount := Nat.lt_of_not_ge hReservoir
    simp [GasMachine.chargeState, GasMachine.stateChargeFromGasLeft,
      GasMachine.stateChargeFromReservoir,
      Nat.min_eq_right (Nat.le_of_lt hReservoirLt)]
    omega

theorem create_admission_one_short_state_charge_is_oog
    (gas : GasState) (newAccountStateGas : Nat) (destination : CreateDestinationOracle)
    (hConsistent : CreateDestinationOracleConsistent destination)
    (hDead : destination.accountExists = false)
    (hInsufficient : gas.gasLeft + gas.stateReservoir < newAccountStateGas) :
    let result := admitCreate gas newAccountStateGas destination
    result.status = .outOfGas ∧
      result.classification.destinationReadCount = 1 ∧
      result.stateCharge = newAccountStateGas ∧
      result.stateChargeApplied = false ∧
      result.stateChargeRefilled = 0 ∧
      result.gas = clearCreateExecutionGas gas ∧
      result.executionGasCleared = true ∧ result.childEntered = false := by
  have hCharge := charge_state_oog_of_total_insufficient hInsufficient
  simp [admitCreate, hConsistent, createStateCharge, classifyCreateDestination, hDead, hCharge]

theorem create_admission_collision_refills_state_charge
    (gas : GasState) (newAccountStateGas : Nat) (destination : CreateDestinationOracle)
    (hConsistent : CreateDestinationOracleConsistent destination)
    (hDead : destination.accountExists = false)
    (hCollision : destination.collision.isCollision = true)
    (hAffordable : GasMachine.stateChargeFromGasLeft newAccountStateGas gas ≤ gas.gasLeft) :
    let result := admitCreate gas newAccountStateGas destination
    result.status = .collision ∧
      result.classification.destinationReadCount = 1 ∧
      result.stateCharge = newAccountStateGas ∧
      result.stateChargeApplied = true ∧
      result.stateChargeRefilled = newAccountStateGas ∧
      result.executionGasCleared = true ∧ result.childEntered = false := by
  simp [admitCreate, hConsistent, createStateCharge, classifyCreateDestination, hDead,
    hCollision, GasMachine.chargeState_of_affordable hAffordable]

theorem create_admission_existing_collision_has_no_state_charge
    (gas : GasState) (newAccountStateGas : Nat) (destination : CreateDestinationOracle)
    (hConsistent : CreateDestinationOracleConsistent destination)
    (hExisting : destination.accountExists = true)
    (hCollision : destination.collision.isCollision = true) :
    let result := admitCreate gas newAccountStateGas destination
    result.status = .collision ∧ result.stateCharge = 0 ∧
      result.stateChargeApplied = false ∧ result.stateChargeRefilled = 0 ∧
      result.gas.stateReservoir = gas.stateReservoir ∧
      result.gas.stateUsed = gas.stateUsed := by
  have hChargeZero : GasMachine.chargeState 0 gas = .ok gas := by
    have hAffordable : GasMachine.stateChargeFromGasLeft 0 gas ≤ gas.gasLeft := by
      simp [GasMachine.stateChargeFromGasLeft, GasMachine.stateChargeFromReservoir]
    rw [GasMachine.chargeState_of_affordable hAffordable]
    have hFields := GasMachine.chargedState_fields 0 gas
    simp at hFields
    rcases hFields with ⟨hReservoir, hGasLeft, hFromGasLeft, hUsed, hRefund⟩
    cases gas
    congr 1
  simp [admitCreate, hConsistent, createStateCharge, classifyCreateDestination, hExisting,
    hCollision, hChargeZero, GasMachine.refillState, GasMachine.appliedRefill,
    GasMachine.refillToGasLeft, clearCreateExecutionGas]

theorem create_admission_collision_restores_state_pool
    (gas : GasState) (newAccountStateGas : Nat) (destination : CreateDestinationOracle)
    (hConsistent : CreateDestinationOracleConsistent destination)
    (hDead : destination.accountExists = false)
    (hCollision : destination.collision.isCollision = true)
    (hSpill : gas.stateFromGasLeft = 0)
    (hAffordable : GasMachine.stateChargeFromGasLeft newAccountStateGas gas ≤ gas.gasLeft) :
    let result := admitCreate gas newAccountStateGas destination
    result.gas.stateReservoir = gas.stateReservoir ∧
      result.gas.stateUsed = gas.stateUsed ∧
      result.gas.stateFromGasLeft = gas.stateFromGasLeft := by
  have hNotInvalid : ¬¬CreateDestinationOracleConsistent destination :=
    fun h => h hConsistent
  unfold admitCreate
  dsimp [classifyCreateDestination]
  simp only [hDead, Bool.false_eq_true, ↓reduceIte, createStateCharge]
  rw [if_neg hNotInvalid]
  rw [GasMachine.chargeState_of_affordable hAffordable]
  simp only [hCollision, ↓reduceIte, clearCreateExecutionGas]
  have hFields := GasMachine.chargedState_fields newAccountStateGas gas
  simp only at hFields
  rcases hFields with ⟨hReservoirFields, hGasLeftFields, hFromGasLeftFields,
    hUsedFields, hRefundFields⟩
  by_cases hReservoir : newAccountStateGas ≤ gas.stateReservoir
  · simp [GasMachine.refillState, GasMachine.appliedRefill, GasMachine.refillToGasLeft,
      hReservoirFields, hGasLeftFields, hFromGasLeftFields, hUsedFields, hRefundFields,
      Nat.min_eq_left hReservoir, hSpill]
    omega
  · have hReservoirLt : gas.stateReservoir < newAccountStateGas :=
      Nat.lt_of_not_ge hReservoir
    simp [GasMachine.refillState, GasMachine.appliedRefill, GasMachine.refillToGasLeft,
      hReservoirFields, hGasLeftFields, hFromGasLeftFields, hUsedFields, hRefundFields,
      Nat.min_eq_right (Nat.le_of_lt hReservoirLt), hSpill]
    omega

end Eip803x.TransactionReference
