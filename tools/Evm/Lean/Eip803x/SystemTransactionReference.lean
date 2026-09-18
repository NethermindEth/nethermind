-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.TransactionGas

namespace Eip803x
namespace SystemTransactionReference

/-!
Handwritten reference for the standard-mainnet route from
`TransactionProcessorBase.Process` to `SystemTransactionProcessor`.

The relation makes the processor overrides and the block-internal call-site
contract executable. EVM execution, ordinary intrinsic-gas calculation, and
settlement arithmetic are explicit observations. This is not an extraction or
a refinement theorem for the production C#.
-/

structure Options where
  commit : Bool := false
  restore : Bool := false
  skipValidation : Bool := false
  warmup : Bool := false
  buildUp : Bool := false
  deriving DecidableEq, Repr

def Options.execute : Options := { commit := true }

def Options.callAndRestore : Options :=
  { commit := true, restore := true, skipValidation := true }

def Options.build : Options := { buildUp := true }

def Options.trace : Options := { commit := true, skipValidation := true }

def Options.warm : Options := { skipValidation := true, warmup := true }

def Options.onlySkipValidation : Options := { skipValidation := true }

def Options.isBuildUpExactly (o : Options) : Bool := o == Options.build

def Options.isSkipValidationExactly (o : Options) : Bool :=
  o == Options.onlySkipValidation

def Options.core (o : Options) : Options := { o with warmup := false }

def Options.payOriginalValue (o : Options) : Bool :=
  let core := o.core
  !core.skipValidation && !(core.commit && core.skipValidation)

def Options.forSystemCore (o : Options) : Options :=
  if o.payOriginalValue then
    { o with commit := true, skipValidation := true }
  else
    o

theorem pay_original_value_iff_validation_not_skipped (o : Options) :
    o.payOriginalValue = !o.skipValidation := by
  cases o with
  | mk commit restore skipValidation warmup buildUp =>
      cases skipValidation <;> simp [Options.payOriginalValue, Options.core]

theorem validating_system_route_forces_commit_and_skip (o : Options)
    (h : o.skipValidation = false) :
    o.forSystemCore.commit = true ∧ o.forSystemCore.skipValidation = true := by
  cases o
  simp_all [Options.forSystemCore, Options.payOriginalValue, Options.core]

theorem system_core_always_skips_validation (o : Options) :
    o.forSystemCore.skipValidation = true := by
  cases o with
  | mk commit restore skipValidation warmup buildUp =>
      cases commit <;> cases skipValidation <;>
        simp [Options.forSystemCore, Options.payOriginalValue, Options.core]

inductive Engine where
  | standardMainnet
  | aura
  | optimism
  | other
  deriving DecidableEq, Repr

inductive TransactionKind where
  | plain
  | systemTransaction
  | systemCall
  deriving DecidableEq, Repr

inductive Sender where
  | missing
  | systemUser
  | other (address : String)
  deriving DecidableEq, Repr

def systemUserAddress : String := "0xfffffffffffffffffffffffffffffffffffffffe"

def Sender.address : Sender → Option String
  | .missing => none
  | .systemUser => some systemUserAddress
  | .other address => some address

structure AccessList where
  addresses : List String := []
  storageKeys : List (String × Nat) := []
  deriving DecidableEq, Repr

structure Transaction where
  kind : TransactionKind
  sender : Sender
  destination : Option String := none
  data : List Nat := []
  gasPrice : Nat := 0
  accessList : Option AccessList := none
  isOpSystemTransaction : Bool := false
  supportsAuthorizationList : Bool := false
  authorizationHasRecipient : Bool := true
  authorizationListNonempty : Bool := true
  nonceIsMax : Bool := false
  initCodeWithinLimit : Bool := true
  gasLimit : Nat
  value : Nat := 0
  deriving DecidableEq, Repr

def Transaction.isSystem (tx : Transaction) : Bool :=
  tx.kind == .systemTransaction || tx.sender == .systemUser || tx.isOpSystemTransaction

def Transaction.routesToSystemProcessor (tx : Transaction) (o : Options) : Bool :=
  tx.isSystem || o.isSkipValidationExactly

def Transaction.isContractCreation (tx : Transaction) : Bool := tx.destination.isNone

def Transaction.senderIsRecipient (tx : Transaction) : Bool :=
  tx.sender.address == tx.destination

theorem contract_creation_is_derived_from_destination (tx : Transaction) :
    tx.isContractCreation = tx.destination.isNone := by
  rfl

theorem sender_recipient_equality_is_derived (tx : Transaction) :
    tx.senderIsRecipient = (tx.sender.address == tx.destination) := by
  rfl

inductive SpecRepresentation where
  | releaseSpec
  | decoratorFallback
  deriving DecidableEq, Repr

structure Spec where
  representation : SpecRepresentation := .releaseSpec
  isAmsterdam : Bool := true
  eip158 : Bool := true
  eip658 : Bool := true
  eip2780 : Bool := true
  eip7708 : Bool := true
  eip7778 : Bool := true
  eip7843 : Bool := true
  eip7928 : Bool := true
  eip7954 : Bool := true
  eip7976 : Bool := true
  eip7981 : Bool := true
  eip8024 : Bool := true
  eip8037 : Bool := true
  eip8038 : Bool := true
  eip8246 : Bool := true
  eip8282 : Bool := true
  isGenesis : Bool := false
  deriving DecidableEq, Repr

def Spec.pinnedAmsterdam : Spec where
  representation := .releaseSpec
  isAmsterdam := true
  eip158 := true
  eip658 := true
  eip2780 := true
  eip7708 := true
  eip7778 := true
  eip7843 := true
  eip7928 := true
  eip7954 := true
  eip7976 := true
  eip7981 := true
  eip8024 := true
  eip8037 := true
  eip8038 := true
  eip8246 := true
  eip8282 := true
  isGenesis := false

def Spec.systemEip158 (spec : Spec) : Bool :=
  match spec.representation with
  | .releaseSpec => false
  | .decoratorFallback => if !spec.eip158 then false else spec.isGenesis

theorem release_spec_system_execution_disables_eip158 (spec : Spec)
    (h : spec.representation = .releaseSpec) : spec.systemEip158 = false := by
  simp [Spec.systemEip158, h]

structure Schedule where
  transactionExecutionCap : Nat
  systemCallStateReservoir : Nat
  deriving DecidableEq, Repr

def Schedule.pinnedAmsterdam : Schedule where
  transactionExecutionCap := 30_000_000
  systemCallStateReservoir := 1_566_720

def int64Max : Nat := 9_223_372_036_854_775_807

theorem pinned_system_call_gas_fits_int64 : 31_566_720 ≤ int64Max := by
  native_decide

theorem pinned_beacon_call_gas_fits_int64 : 31_566_720 ≤ int64Max := by
  native_decide

structure IntrinsicGas where
  execution : Nat
  state : Nat
  floor : Nat
  deriving DecidableEq, Repr

structure AmsterdamIntrinsicComponents where
  transactionBase : Nat
  calldata : Nat
  accessList : Nat
  create : Nat
  initcode : Nat
  recipient : Nat
  floor : Nat
  deriving DecidableEq, Repr

def calldataExecutionCost (data : List Nat) : Nat :=
  data.foldl (fun total byte => total + if byte == 0 then 4 else 16) 0

def accessListCounts (accessList : Option AccessList) : Nat × Nat :=
  match accessList with
  | none => (0, 0)
  | some list => (list.addresses.length, list.storageKeys.length)

def accessListFloorTokens (accessList : Option AccessList) : Nat :=
  let counts := accessListCounts accessList
  (counts.1 * 20 + counts.2 * 32) * 4

def accessListExecutionCost (accessList : Option AccessList) : Nat :=
  let counts := accessListCounts accessList
  counts.1 * 2_900 + counts.2 * 2_000 + accessListFloorTokens accessList * 16

def calculateAmsterdamIntrinsicComponents (tx : Transaction) : AmsterdamIntrinsicComponents :=
  let create := if tx.isContractCreation then 12_000 else 0
  let initcode := if tx.isContractCreation then ((tx.data.length + 31) / 32) * 2 else 0
  let recipient := if tx.isContractCreation || tx.senderIsRecipient then 0
    else 3_000 + if tx.value == 0 then 0 else 6_000
  let floorBase := 12_000 + create + recipient
  { transactionBase := 12_000
  , calldata := calldataExecutionCost tx.data
  , accessList := accessListExecutionCost tx.accessList
  , create := create
  , initcode := initcode
  , recipient := recipient
  , floor := floorBase + (tx.data.length * 4 + accessListFloorTokens tx.accessList) * 16 }

def calculateAmsterdamIntrinsic (tx : Transaction) : IntrinsicGas :=
  let components := calculateAmsterdamIntrinsicComponents tx
  { execution := components.transactionBase + components.calldata +
      components.accessList + components.create + components.initcode + components.recipient
  , state := 0
  , floor := components.floor }

def systemCallIntrinsic (schedule : Schedule) : IntrinsicGas where
  execution := 0
  state := schedule.systemCallStateReservoir
  floor := 0

structure AvailableGas where
  executionLeft : Nat
  stateReservoir : Nat
  stateUsed : Nat
  stateFromGasLeft : Nat
  stateFromGasLeftRefunded : Nat
  deriving DecidableEq, Repr

def createSystemCallAvailable (schedule : Schedule) (gasLimit : Nat) : AvailableGas :=
  let reservoir := min gasLimit schedule.systemCallStateReservoir
  { executionLeft := gasLimit - reservoir
  , stateReservoir := reservoir
  , stateUsed := 0
  , stateFromGasLeft := 0
  , stateFromGasLeftRefunded := 0 }

def createOrdinaryAvailable (schedule : Schedule) (intrinsic : IntrinsicGas)
    (gasLimit : Nat) : Option AvailableGas :=
  let intrinsicTotal := intrinsic.execution + intrinsic.state
  if gasLimit < intrinsicTotal then
    none
  else
    let available := gasLimit - intrinsicTotal
    let maxExecution := schedule.transactionExecutionCap - intrinsic.execution
    let executionLeft := min available maxExecution
    some
      { executionLeft := executionLeft
      , stateReservoir := available - executionLeft
      , stateUsed := intrinsic.state
      , stateFromGasLeft := 0
      , stateFromGasLeftRefunded := 0 }

theorem system_call_available_partitions_limit (schedule : Schedule) (gasLimit : Nat) :
    (createSystemCallAvailable schedule gasLimit).executionLeft +
      (createSystemCallAvailable schedule gasLimit).stateReservoir = gasLimit := by
  simp [createSystemCallAvailable, Nat.sub_add_cancel (Nat.min_le_left gasLimit schedule.systemCallStateReservoir)]

theorem pinned_system_call_split :
    createSystemCallAvailable Schedule.pinnedAmsterdam 31_566_720 =
      { executionLeft := 30_000_000
      , stateReservoir := 1_566_720
      , stateUsed := 0
      , stateFromGasLeft := 0
      , stateFromGasLeftRefunded := 0 } := by
  native_decide

inductive StaticFailure where
  | senderNotSpecified
  | nonceOverflow
  | transactionSizeOverMaxInitCodeSize
  | authorizationContractCreation
  | emptyAuthorizationList
  | intrinsicExecutionExceedsCap
  | intrinsicFloorExceedsCap
  | gasLimitBelowIntrinsicExecution
  | gasLimitBelowFloor
  deriving DecidableEq, Repr

def validateStatic (tx : Transaction) (o : Options) (spec : Spec)
    (schedule : Schedule) (intrinsic : IntrinsicGas) : Option StaticFailure :=
  if tx.sender == .missing then
    some .senderNotSpecified
  else if !o.skipValidation && tx.nonceIsMax then
    some .nonceOverflow
  else if !tx.initCodeWithinLimit then
    some .transactionSizeOverMaxInitCodeSize
  else if tx.supportsAuthorizationList && !tx.authorizationHasRecipient then
    some .authorizationContractCreation
  else if tx.supportsAuthorizationList && !tx.authorizationListNonempty then
    some .emptyAuthorizationList
  else if spec.eip8037 && schedule.transactionExecutionCap < intrinsic.execution then
    some .intrinsicExecutionExceedsCap
  else if spec.eip8037 && schedule.transactionExecutionCap < intrinsic.floor then
    some .intrinsicFloorExceedsCap
  else if tx.gasLimit < intrinsic.execution then
    some .gasLimitBelowIntrinsicExecution
  else if tx.gasLimit < intrinsic.floor then
    some .gasLimitBelowFloor
  else
    none

structure World where
  token : Nat
  senderBalance : Nat
  recipientBalance : Nat
  senderNonce : Nat
  deriving DecidableEq, Repr

def World.debitSender (world : World) (value : Nat) : Option World :=
  if value ≤ world.senderBalance then
    some { world with senderBalance := world.senderBalance - value }
  else
    none

inductive ReceiptStatus where
  | success
  | failure
  deriving DecidableEq, Repr

inductive TxResult where
  | ok
  | rejected (reason : StaticFailure)
  | evmException (kind : Nat)
  | unmodeled (reason : Nat)
  deriving DecidableEq, Repr

structure SettlementObservation where
  spentGas : Nat
  operationGas : Nat
  effectiveBlockGas : Nat
  blockStateGas : Nat
  maxUsedGas : Nat
  gasRefund : Nat
  remainingGas : Nat
  remainingStateReservoir : Nat
  refundCounter : Int
  deriving DecidableEq, Repr

structure DestroyedAccount where
  address : Nat
  balance : Nat
  deriving DecidableEq, Repr

structure SuccessObservation where
  expectedInputWorld : World
  worldAfter : World
  executingAddress : Option String := none
  returndata : List Nat := []
  logs : List Nat := []
  destroyList : List DestroyedAccount := []
  settlement : SettlementObservation
  deriving DecidableEq, Repr

structure FailureObservation where
  expectedInputWorld : World
  discardedWorld : World
  returndata : List Nat := []
  exceptionKind : Option Nat := none
  settlement : SettlementObservation
  deriving DecidableEq, Repr

inductive EvmObservation where
  | success (observation : SuccessObservation)
  | revert (observation : FailureObservation)
  | exceptionalHalt (observation : FailureObservation)
  | topFrameOutOfGas (settlement : SettlementObservation)
  | transactionCollision (observation : FailureObservation)
  | createStateOutOfGas (observation : FailureObservation)
  | deploymentFailure (observation : FailureObservation)
  | unsupportedNoCode
  deriving DecidableEq, Repr

structure SimpleObservation where
  expectedInputWorld : World
  worldAfter : World
  stateGasOutOfGas : Bool := false
  settlement : SettlementObservation
  deriving DecidableEq, Repr

structure TransferLog where
  emitter : String
  signature : String
  fromAddress : String
  toAddress : String
  value : Nat
  deriving DecidableEq, Repr

def transferSignature : String :=
  "0xddf252ad1be2c89b69c2b068fc378daa952ba7f163c4a11628f55a4df523b3ef"

def expectedEip7708TransferLogs (spec : Spec) (tx : Transaction)
    (executingAddress : Option String) : List TransferLog :=
  if spec.eip7708 && tx.value != 0 && !tx.senderIsRecipient then
    let recipient := match tx.destination with
      | some destination => some destination
      | none => executingAddress
    match tx.sender.address, recipient with
    | some sender, some recipient =>
        [{ emitter := systemUserAddress
         , signature := transferSignature
         , fromAddress := sender
         , toAddress := recipient
         , value := tx.value }]
    | _, _ => []
  else
    []

inductive BodyObservation where
  | simpleTransfer (observation : SimpleObservation)
  | evm (observation : EvmObservation)
  deriving DecidableEq, Repr

def BodyObservation.matchesTargetCode : BodyObservation → Bool → Bool
  | .simpleTransfer _, false => true
  | .evm _, true => true
  | _, _ => false

structure Oracle where
  ordinaryIntrinsic : IntrinsicGas
  body : BodyObservation
  deriving DecidableEq, Repr

inductive Event where
  | outerBuildUpSnapshot
  | routeToSystemProcessor
  | acquireSystemProcessor
  | beginSystemAccountReadSuppression
  | beforeSystemTransactionHook
  | deriveSystemOptions
  | getHeader
  | selectSystemSpec
  | recoverSenderBeforeIntrinsic
  | calculateIntrinsicGas
  | validateStatic
  | calculateEffectiveGasPrice
  | updateMetrics
  | recoverSenderIfNeeded
  | validateSenderSkipped
  | buyGas
  | incrementNonce
  | selectExecutionPath
  | commitBeforeExecution
  | calculateAvailableGas
  | setTransactionExecutionContext
  | buildExecutionEnvironment
  | takeExecutionSnapshot
  | payOriginalValue
  | executeSimpleTransfer
  | executeEvm
  | restoreExecutionSnapshot
  | calculateRefundAndSettlement
  | refundPaymentBypassed
  | skipNormalBlockGasCounters
  | payFeesBypassed
  | finalizeDestroyList
  | writeTransactionGasFields
  | finalReset
  | finalCommit
  | finalTransientReset
  | markReceiptTrace
  | disposeSystemAccountReadSuppression
  | returnResult
  deriving DecidableEq, Repr

def Event.canonical : List Event :=
  [ .outerBuildUpSnapshot
  , .routeToSystemProcessor
  , .acquireSystemProcessor
  , .beginSystemAccountReadSuppression
  , .beforeSystemTransactionHook
  , .deriveSystemOptions
  , .getHeader
  , .selectSystemSpec
  , .recoverSenderBeforeIntrinsic
  , .calculateIntrinsicGas
  , .validateStatic
  , .calculateEffectiveGasPrice
  , .updateMetrics
  , .recoverSenderIfNeeded
  , .validateSenderSkipped
  , .buyGas
  , .incrementNonce
  , .selectExecutionPath
  , .commitBeforeExecution
  , .calculateAvailableGas
  , .setTransactionExecutionContext
  , .buildExecutionEnvironment
  , .takeExecutionSnapshot
  , .payOriginalValue
  , .executeSimpleTransfer
  , .executeEvm
  , .restoreExecutionSnapshot
  , .calculateRefundAndSettlement
  , .refundPaymentBypassed
  , .skipNormalBlockGasCounters
  , .payFeesBypassed
  , .finalizeDestroyList
  , .writeTransactionGasFields
  , .finalReset
  , .finalCommit
  , .finalTransientReset
  , .markReceiptTrace
  , .disposeSystemAccountReadSuppression
  , .returnResult ]

theorem canonical_event_count : Event.canonical.length = 39 := by native_decide

structure RuntimeState where
  world : World
  committedWorld : World
  events : List Event := []
  outerBuildUpSnapshotTaken : Bool := false
  commitCount : Nat := 0
  resetCount : Nat := 0
  transientResetCount : Nat := 0
  receiptTraceMarks : Nat := 0
  txSpentGas : Option Nat := none
  txBlockGasUsed : Option Nat := none
  destroyedAccounts : List Nat := []
  preservedDestroyedBalances : List (Nat × Nat) := []
  destroyBurnLogs : List (Nat × Nat) := []
  deriving DecidableEq, Repr

def RuntimeState.emit (state : RuntimeState) (event : Event) : RuntimeState :=
  { state with events := state.events ++ [event] }

def RuntimeState.emitMany (state : RuntimeState) (events : List Event) : RuntimeState :=
  { state with events := state.events ++ events }

def RuntimeState.commitState (state : RuntimeState) : RuntimeState :=
  { state with committedWorld := state.world, commitCount := state.commitCount + 1 }

def RuntimeState.reset (state : RuntimeState) : RuntimeState :=
  { state with world := state.committedWorld, resetCount := state.resetCount + 1 }

def RuntimeState.resetTransient (state : RuntimeState) : RuntimeState :=
  { state with transientResetCount := state.transientResetCount + 1 }

def RuntimeState.finalizeDestroyAccounts (state : RuntimeState) (spec : Spec)
    (accounts : List DestroyedAccount) : RuntimeState :=
  let nonzero := accounts.filter (fun account => account.balance != 0)
  { state with
    destroyedAccounts := accounts.map (·.address)
    preservedDestroyedBalances := if spec.eip8246 then
        nonzero.map (fun account => (account.address, account.balance))
      else []
    destroyBurnLogs := if spec.eip8246 then []
      else nonzero.map (fun account => (account.address, account.balance)) }

inductive TracerKind where
  | null
  | callOutput
  | other
  deriving DecidableEq, Repr

structure Input where
  engine : Engine := .standardMainnet
  tx : Transaction
  options : Options := .execute
  spec : Spec := Spec.pinnedAmsterdam
  schedule : Schedule := Schedule.pinnedAmsterdam
  initialWorld : World
  initialCommittedWorld : World
  tracingState : Bool := false
  tracingReceipt : Bool := false
  tracerKind : TracerKind := .null
  refundHookCalled : Bool := false
  oracle : Oracle
  deriving DecidableEq, Repr

def Input.pinnedDomain (input : Input) : Bool :=
  input.engine == .standardMainnet &&
    input.spec == Spec.pinnedAmsterdam &&
    input.schedule == Schedule.pinnedAmsterdam

def Input.tracingFactsConsistent (input : Input) : Bool :=
  !input.tracingState &&
    match input.tracerKind with
    | .null => !input.tracingReceipt
    | .callOutput => input.tracingReceipt
    | .other => false

inductive Disposition where
  | completed
  | rejected
  | notRouted
  | unmodeled
  deriving DecidableEq, Repr

inductive ReceiptOwnership where
  | blockAdapterObligation
  deriving DecidableEq, Repr

structure CallOutputTrace where
  status : ReceiptStatus
  returnValue : List Nat
  gasSpent : Nat
  operationGas : Nat
  deriving DecidableEq, Repr

def CallOutputTrace.initial : CallOutputTrace :=
  { status := .failure, returnValue := [], gasSpent := 0, operationGas := 0 }

inductive GasPurchaseOutput where
  | notRun
  | bypassed
  | charged (premiumPerGas senderReservedGasPayment blobBaseFee : Nat)
  deriving DecidableEq, Repr

structure GasPurchaseStep where
  output : GasPurchaseOutput
  world : World
  continues : Bool
  deriving DecidableEq, Repr

def buyGasOverride (world : World) : GasPurchaseStep :=
  { output := .bypassed, world := world, continues := true }

def GasPurchaseOutput.premiumPerGas : GasPurchaseOutput → Nat
  | .charged premium _ _ => premium
  | _ => 0

def GasPurchaseOutput.senderReservedGasPayment : GasPurchaseOutput → Nat
  | .charged _ reserved _ => reserved
  | _ => 0

def GasPurchaseOutput.blobBaseFee : GasPurchaseOutput → Nat
  | .charged _ _ blob => blob
  | _ => 0

inductive NonceOutput where
  | notRun
  | bypassed (nonce : Nat)
  | incremented (before after : Nat)
  deriving DecidableEq, Repr

structure NonceStep where
  output : NonceOutput
  world : World
  continues : Bool
  deriving DecidableEq, Repr

def incrementNonceOverride (world : World) : NonceStep :=
  { output := .bypassed world.senderNonce, world := world, continues := true }

def NonceOutput.before : NonceOutput → Option Nat
  | .notRun => none
  | .bypassed nonce => some nonce
  | .incremented before _ => some before

def NonceOutput.after : NonceOutput → Option Nat
  | .notRun => none
  | .bypassed nonce => some nonce
  | .incremented _ after => some after

def NonceOutput.delta : NonceOutput → Nat
  | .incremented before after => after - before
  | _ => 0

structure OverrideKernel where
  buyGas : World → GasPurchaseStep
  incrementNonce : World → NonceStep

def productionOverrides : OverrideKernel where
  buyGas := buyGasOverride
  incrementNonce := incrementNonceOverride

@[simp] theorem production_buy_gas (world : World) :
    productionOverrides.buyGas world = buyGasOverride world := rfl

@[simp] theorem production_increment_nonce (world : World) :
    productionOverrides.incrementNonce world = incrementNonceOverride world := rfl

structure Result where
  disposition : Disposition
  state : RuntimeState
  txResult : TxResult
  receiptStatus : Option ReceiptStatus := none
  returndata : List Nat := []
  logs : List Nat := []
  transferLogs : List TransferLog := []
  settlement : Option SettlementObservation := none
  initialAvailableGas : Option AvailableGas := none
  effectiveOptions : Options
  payOriginalValue : Bool
  systemSpecEip158 : Bool
  normalReceiptOwnership : ReceiptOwnership := .blockAdapterObligation
  gasPurchaseOutput : GasPurchaseOutput := .notRun
  nonceOutput : NonceOutput := .notRun
  overridesReached : Bool := false
  callOutputTrace : Option CallOutputTrace := none
  deriving DecidableEq, Repr

def Result.premiumPerGas (result : Result) : Nat := result.gasPurchaseOutput.premiumPerGas

def Result.senderReservedGasPayment (result : Result) : Nat :=
  result.gasPurchaseOutput.senderReservedGasPayment

def Result.blobBaseFee (result : Result) : Nat := result.gasPurchaseOutput.blobBaseFee

def Result.nonceIncrementDelta (result : Result) : Nat :=
  result.nonceOutput.delta

def normalBlockCounterUpdate (options : Options) (executionGas stateGas : Nat) :
    Nat × Nat × Nat :=
  if options.skipValidation then
    (0, 0, 0)
  else
    (max executionGas stateGas, executionGas, stateGas)

def Result.normalCounterDeltas (result : Result) : Nat × Nat × Nat :=
  match result.settlement with
  | some settlement => normalBlockCounterUpdate result.effectiveOptions
      settlement.effectiveBlockGas settlement.blockStateGas
  | none => (0, 0, 0)

def Result.normalHeaderGasUsedDelta (result : Result) : Nat :=
  result.normalCounterDeltas.1

def Result.normalBlockExecutionGasDelta (result : Result) : Nat :=
  result.normalCounterDeltas.2.1

def Result.normalBlockStateGasDelta (result : Result) : Nat :=
  result.normalCounterDeltas.2.2

def Result.emit (result : Result) (event : Event) : Result :=
  { result with state := result.state.emit event }

def Result.closeSuppression (suppressed : Bool) (result : Result) : Result :=
  let result := if suppressed then result.emit .disposeSystemAccountReadSuppression else result
  result.emit .returnResult

def Result.returnDirect (result : Result) : Result := result.emit .returnResult

def unsupportedResult (state : RuntimeState) (options : Options) (reason : Nat) : Result :=
  { disposition := .unmodeled
  , state := state
  , txResult := .unmodeled reason
  , effectiveOptions := options
  , payOriginalValue := false
  , systemSpecEip158 := false }

def rejectedResult (state : RuntimeState) (effectiveOptions : Options)
    (payOriginal : Bool) (systemSpecEip158 : Bool) (reason : StaticFailure) : Result :=
  { disposition := .rejected
  , state := state
  , txResult := .rejected reason
  , effectiveOptions := effectiveOptions
  , payOriginalValue := payOriginal
  , systemSpecEip158 := systemSpecEip158 }

def finishState (options : Options) (state : RuntimeState) : RuntimeState :=
  if options.restore then
    let state := (state.reset.emit .finalReset).commitState
    state.emit .finalCommit
  else if options.commit then
    (state.commitState).emit .finalCommit
  else
    (state.resetTransient).emit .finalTransientReset

def finishCompleted (input : Input) (suppressed : Bool) (effectiveOptions : Options)
    (payOriginal : Bool) (state : RuntimeState) (status : ReceiptStatus)
    (txResult : TxResult) (returndata logs : List Nat)
    (transferLogs : List TransferLog)
    (destroyList : List DestroyedAccount)
    (available : AvailableGas) (settlement : SettlementObservation) : Result :=
  let state := state.emit .calculateRefundAndSettlement
  let state := if input.refundHookCalled then state.emit .refundPaymentBypassed else state
  let state := state.emitMany [.skipNormalBlockGasCounters, .payFeesBypassed]
  let state := if input.spec.eip8037 && input.spec.eip7708 && status == .success then
      (state.finalizeDestroyAccounts input.spec destroyList).emit .finalizeDestroyList
    else state
  let state := if effectiveOptions.warmup then state else
    { (state.emit .writeTransactionGasFields) with
      txSpentGas := some settlement.spentGas
      txBlockGasUsed := some settlement.effectiveBlockGas }
  let state := finishState effectiveOptions state
  let state := if input.tracingReceipt then
      { (state.emit .markReceiptTrace) with receiptTraceMarks := state.receiptTraceMarks + 1 }
    else state
  ({ disposition := .completed
   , state := state
   , txResult := txResult
   , receiptStatus := some status
   , returndata := returndata
   , logs := logs
   , transferLogs := transferLogs
   , settlement := some settlement
   , initialAvailableGas := some available
   , effectiveOptions := effectiveOptions
   , payOriginalValue := payOriginal
   , systemSpecEip158 := input.spec.systemEip158
   , callOutputTrace := if input.tracerKind == .callOutput then some
       { status := status
       , returnValue := returndata
       , gasSpent := settlement.spentGas
       , operationGas := settlement.operationGas }
     else none } : Result).closeSuppression suppressed

def executeSimple (input : Input) (suppressed : Bool) (effectiveOptions : Options)
    (payOriginal : Bool) (state : RuntimeState) (available : AvailableGas)
    (observation : SimpleObservation) : Result :=
  if observation.stateGasOutOfGas then
    let state := state.emit .executeSimpleTransfer
    if observation.expectedInputWorld != state.world then
      (unsupportedResult state effectiveOptions 20).closeSuppression suppressed
    else
      finishCompleted input suppressed effectiveOptions payOriginal state .failure (.evmException 1)
        [] [] [] [] available observation.settlement
  else
    match if payOriginal && !input.tx.senderIsRecipient then
        state.world.debitSender input.tx.value
      else some state.world with
    | none => (unsupportedResult state effectiveOptions 21).closeSuppression suppressed
    | some paidWorld =>
        let state := if payOriginal && !input.tx.senderIsRecipient && input.tx.value != 0 then
            { (state.emit .payOriginalValue) with world := paidWorld }
          else { state with world := paidWorld }
        let state := state.emit .executeSimpleTransfer
        if observation.expectedInputWorld != state.world ||
            observation.worldAfter.senderNonce != state.world.senderNonce then
          (unsupportedResult state effectiveOptions 22).closeSuppression suppressed
        else
          let state := { state with world := observation.worldAfter }
          finishCompleted input suppressed effectiveOptions payOriginal state .success .ok
            [] [] (expectedEip7708TransferLogs input.spec input.tx none) []
              available observation.settlement

def executeEvm (input : Input) (suppressed : Bool) (effectiveOptions : Options)
    (payOriginal : Bool) (state : RuntimeState) (available : AvailableGas)
    (observation : EvmObservation) : Result :=
  let state := state.emitMany
    [.setTransactionExecutionContext, .buildExecutionEnvironment, .takeExecutionSnapshot]
  let snapshot := state.world
  match observation with
  | .unsupportedNoCode =>
      (unsupportedResult state effectiveOptions 21).closeSuppression suppressed
  | .topFrameOutOfGas settlement =>
      let state := state.emit .executeEvm
      finishCompleted input suppressed effectiveOptions payOriginal state .failure (.evmException 1)
        [] [] [] [] available settlement
  | .success outcome =>
      if input.tx.isContractCreation && input.tx.value != 0 &&
          (outcome.executingAddress.isNone || outcome.executingAddress == some "") then
        (unsupportedResult state effectiveOptions 40).closeSuppression suppressed
      else match if payOriginal then state.world.debitSender input.tx.value else some state.world with
      | none => (unsupportedResult state effectiveOptions 22).closeSuppression suppressed
      | some paidWorld =>
          let state := if payOriginal && input.tx.value != 0 then
              { (state.emit .payOriginalValue) with world := paidWorld }
            else { state with world := paidWorld }
          let state := state.emit .executeEvm
          if outcome.expectedInputWorld != state.world ||
              outcome.worldAfter.senderNonce != state.world.senderNonce then
            (unsupportedResult state effectiveOptions 23).closeSuppression suppressed
          else
            let state := { state with world := outcome.worldAfter }
            finishCompleted input suppressed effectiveOptions payOriginal state .success .ok
              outcome.returndata outcome.logs
                (expectedEip7708TransferLogs input.spec input.tx outcome.executingAddress)
                outcome.destroyList available outcome.settlement
  | .revert outcome =>
      match if payOriginal then state.world.debitSender input.tx.value else some state.world with
      | none => (unsupportedResult state effectiveOptions 24).closeSuppression suppressed
      | some paidWorld =>
          let state := if payOriginal && input.tx.value != 0 then
              { (state.emit .payOriginalValue) with world := paidWorld }
            else { state with world := paidWorld }
          let state := state.emit .executeEvm
          if outcome.expectedInputWorld != state.world then
            (unsupportedResult state effectiveOptions 25).closeSuppression suppressed
          else
            let state := { state with world := outcome.discardedWorld }
            let state := { (state.emit .restoreExecutionSnapshot) with world := snapshot }
            finishCompleted input suppressed effectiveOptions payOriginal state .failure .ok
              outcome.returndata [] [] [] available outcome.settlement
  | .exceptionalHalt outcome =>
      match if payOriginal then state.world.debitSender input.tx.value else some state.world with
      | none => (unsupportedResult state effectiveOptions 26).closeSuppression suppressed
      | some paidWorld =>
          let state := if payOriginal && input.tx.value != 0 then
              { (state.emit .payOriginalValue) with world := paidWorld }
            else { state with world := paidWorld }
          let state := state.emit .executeEvm
          if outcome.expectedInputWorld != state.world then
            (unsupportedResult state effectiveOptions 27).closeSuppression suppressed
          else
            let state := { state with world := outcome.discardedWorld }
            let state := { (state.emit .restoreExecutionSnapshot) with world := snapshot }
            finishCompleted input suppressed effectiveOptions payOriginal state .failure
              (.evmException (outcome.exceptionKind.getD 1)) [] [] [] [] available outcome.settlement
  | .transactionCollision outcome =>
      if !input.tx.isContractCreation then
        (unsupportedResult state effectiveOptions 28).closeSuppression suppressed
      else
        match if payOriginal then state.world.debitSender input.tx.value else some state.world with
        | none => (unsupportedResult state effectiveOptions 29).closeSuppression suppressed
        | some paidWorld =>
            let state := if payOriginal && input.tx.value != 0 then
                { (state.emit .payOriginalValue) with world := paidWorld }
              else { state with world := paidWorld }
            let state := state.emit .executeEvm
            if outcome.expectedInputWorld != state.world then
              (unsupportedResult state effectiveOptions 30).closeSuppression suppressed
            else
              let state := { state with world := outcome.discardedWorld }
              let state := { (state.emit .restoreExecutionSnapshot) with world := snapshot }
              finishCompleted input suppressed effectiveOptions payOriginal state .failure
                (.evmException 2) [] [] [] [] available outcome.settlement
  | .createStateOutOfGas outcome =>
      if !input.tx.isContractCreation then
        (unsupportedResult state effectiveOptions 31).closeSuppression suppressed
      else
        match if payOriginal then state.world.debitSender input.tx.value else some state.world with
        | none => (unsupportedResult state effectiveOptions 32).closeSuppression suppressed
        | some paidWorld =>
            let state := if payOriginal && input.tx.value != 0 then
                { (state.emit .payOriginalValue) with world := paidWorld }
              else { state with world := paidWorld }
            let state := state.emit .executeEvm
            if outcome.expectedInputWorld != state.world then
              (unsupportedResult state effectiveOptions 33).closeSuppression suppressed
            else
              let state := { state with world := outcome.discardedWorld }
              let state := { (state.emit .restoreExecutionSnapshot) with world := snapshot }
              finishCompleted input suppressed effectiveOptions payOriginal state .failure
                (.evmException 1) [] [] [] [] available outcome.settlement
  | .deploymentFailure outcome =>
      if !input.tx.isContractCreation then
        (unsupportedResult state effectiveOptions 34).closeSuppression suppressed
      else
        match if payOriginal then state.world.debitSender input.tx.value else some state.world with
        | none => (unsupportedResult state effectiveOptions 35).closeSuppression suppressed
        | some paidWorld =>
            let state := if payOriginal && input.tx.value != 0 then
                { (state.emit .payOriginalValue) with world := paidWorld }
              else { state with world := paidWorld }
            let state := state.emit .executeEvm
            if outcome.expectedInputWorld != state.world then
              (unsupportedResult state effectiveOptions 36).closeSuppression suppressed
            else
              let state := { state with world := outcome.discardedWorld }
              let state := { (state.emit .restoreExecutionSnapshot) with world := snapshot }
              finishCompleted input suppressed effectiveOptions payOriginal state .failure
                (.evmException (outcome.exceptionKind.getD 1)) [] [] [] [] available outcome.settlement

def runCoreWithOverrides (overrides : OverrideKernel) (input : Input) : Result :=
  let initial : RuntimeState :=
    { world := input.initialWorld, committedWorld := input.initialCommittedWorld }
  let initial := if input.options.isBuildUpExactly then
      { (initial.emit .outerBuildUpSnapshot) with outerBuildUpSnapshotTaken := true }
    else initial
  if !input.pinnedDomain then
    (unsupportedResult initial input.options 2).returnDirect
  else if !input.tracingFactsConsistent then
    (unsupportedResult initial input.options 37).returnDirect
  else if input.tx.isOpSystemTransaction then
    (unsupportedResult initial input.options 3).returnDirect
  else if input.tx.gasLimit > int64Max then
    (unsupportedResult initial input.options 6).returnDirect
  else if !input.tx.routesToSystemProcessor input.options then
    ({ (unsupportedResult initial input.options 4) with disposition := .notRouted }).returnDirect
  else
    let suppressed := input.tx.sender == .systemUser
    let state := initial.emitMany [.routeToSystemProcessor, .acquireSystemProcessor]
    let state := if suppressed then state.emit .beginSystemAccountReadSuppression else state
    let state := state.emitMany
      [.beforeSystemTransactionHook, .deriveSystemOptions, .getHeader, .selectSystemSpec,
       .recoverSenderBeforeIntrinsic, .calculateIntrinsicGas]
    let payOriginal := input.options.payOriginalValue
    let effectiveOptions := input.options.forSystemCore
    let intrinsic := if input.tx.kind == .systemCall then
        systemCallIntrinsic input.schedule
      else calculateAmsterdamIntrinsic input.tx
    if input.tx.supportsAuthorizationList then
      (unsupportedResult state effectiveOptions 7).closeSuppression suppressed
    else if input.tx.kind != .systemCall && input.oracle.ordinaryIntrinsic != intrinsic then
      (unsupportedResult state effectiveOptions 8).closeSuppression suppressed
    else if input.tx.kind != .systemCall && intrinsic.state != 0 then
      (unsupportedResult state effectiveOptions 5).closeSuppression suppressed
    else
      let state := state.emit .validateStatic
      match validateStatic input.tx effectiveOptions input.spec input.schedule intrinsic with
      | some reason =>
          (rejectedResult state effectiveOptions payOriginal input.spec.systemEip158 reason).closeSuppression suppressed
      | none =>
          let state := state.emitMany
            [.calculateEffectiveGasPrice, .updateMetrics, .recoverSenderIfNeeded,
             .validateSenderSkipped]
          let state := state.emit .buyGas
          let purchase := overrides.buyGas state.world
          let state := { state with world := purchase.world }
          if !purchase.continues then
            ({ (unsupportedResult state effectiveOptions 38) with
                gasPurchaseOutput := purchase.output }).closeSuppression suppressed
          else
            let state := state.emit .incrementNonce
            let nonce := overrides.incrementNonce state.world
            let state := { state with world := nonce.world }
            if !nonce.continues then
              ({ (unsupportedResult state effectiveOptions 39) with
                  gasPurchaseOutput := purchase.output
                  nonceOutput := nonce.output }).closeSuppression suppressed
            else
              let state := state.emit .selectExecutionPath
              let commitBefore := effectiveOptions.commit &&
                (match input.oracle.body with
                 | .evm _ => true
                 | .simpleTransfer _ => effectiveOptions.restore || input.tracingState)
              let state := if commitBefore then
                  (state.commitState).emit .commitBeforeExecution
                else state
              let state := state.emit .calculateAvailableGas
              let available := if input.tx.kind == .systemCall then
                  some (createSystemCallAvailable input.schedule input.tx.gasLimit)
                else createOrdinaryAvailable input.schedule intrinsic input.tx.gasLimit
              let result := match available with
                | none =>
                    (rejectedResult state effectiveOptions payOriginal input.spec.systemEip158
                      .gasLimitBelowIntrinsicExecution).closeSuppression suppressed
                | some gas =>
                    match input.oracle.body with
                    | .simpleTransfer observation =>
                        executeSimple input suppressed effectiveOptions payOriginal state gas observation
                    | .evm observation =>
                        executeEvm input suppressed effectiveOptions payOriginal state gas observation
              { result with
                gasPurchaseOutput := purchase.output
                nonceOutput := nonce.output
                overridesReached := true }

def runCore (input : Input) : Result :=
  runCoreWithOverrides
    { buyGas := fun world =>
        { output := .bypassed, world := world, continues := true }
    , incrementNonce := fun world =>
        { output := .bypassed world.senderNonce, world := world, continues := true } }
    input

@[simp] theorem parameterized_production_core_eq (input : Input) :
    runCoreWithOverrides productionOverrides input = runCore input := by
  rfl

def Result.overrideStageReached (result : Result) : Bool :=
  result.overridesReached

def attachCallOutputTrace (input : Input) (result : Result) : Result :=
  if input.tracingFactsConsistent && input.tracerKind == .callOutput &&
      result.callOutputTrace.isNone then
      { result with callOutputTrace := some CallOutputTrace.initial }
  else result

@[simp] theorem attachCallOutputTrace_gasPurchaseOutput (input : Input) (result : Result) :
    (attachCallOutputTrace input result).gasPurchaseOutput = result.gasPurchaseOutput := by
  unfold attachCallOutputTrace
  split <;> rfl

@[simp] theorem attachCallOutputTrace_nonceOutput (input : Input) (result : Result) :
    (attachCallOutputTrace input result).nonceOutput = result.nonceOutput := by
  unfold attachCallOutputTrace
  split <;> rfl

@[simp] theorem attachCallOutputTrace_disposition (input : Input) (result : Result) :
    (attachCallOutputTrace input result).disposition = result.disposition := by
  unfold attachCallOutputTrace
  split <;> rfl

@[simp] theorem attachCallOutputTrace_overridesReached (input : Input) (result : Result) :
    (attachCallOutputTrace input result).overridesReached = result.overridesReached := by
  unfold attachCallOutputTrace
  split <;> rfl

def runWithOverrides (overrides : OverrideKernel) (input : Input) : Result :=
  attachCallOutputTrace input (runCoreWithOverrides overrides input)

def run (input : Input) : Result := runWithOverrides productionOverrides input

theorem runCore_production_override_outputs (input : Input) :
    ((runCore input).gasPurchaseOutput = .notRun ∧
      (runCore input).nonceOutput = .notRun ∧
      (runCore input).overridesReached = false ∧
      (runCore input).disposition ≠ .completed) ∨
    ((runCore input).gasPurchaseOutput = .bypassed ∧
      (runCore input).nonceOutput = .bypassed input.initialWorld.senderNonce ∧
      (runCore input).overridesReached = true) := by
  grind (splits := 64) [runCore, runCoreWithOverrides, unsupportedResult,
    rejectedResult, Result.returnDirect, Result.closeSuppression, Result.emit,
    RuntimeState.emit, RuntimeState.emitMany]

theorem completed_or_reached_execution_uses_bypassed_overrides (input : Input)
    (h : (runCore input).disposition = .completed ∨
      (runCore input).overrideStageReached = true) :
    (run input).gasPurchaseOutput = .bypassed ∧
      (run input).nonceOutput = .bypassed input.initialWorld.senderNonce := by
  rcases runCore_production_override_outputs input with early | reached
  · exfalso
    rcases h with completed | execution
    · exact early.2.2.2 completed
    · simp [Result.overrideStageReached, early.2.2.1] at execution
  · simp [run, runWithOverrides, reached.1, reached.2.1]

theorem system_processor_never_charges_normal_block_counters
    (options : Options) (executionGas stateGas : Nat) :
    normalBlockCounterUpdate options.forSystemCore executionGas stateGas = (0, 0, 0) := by
  simp [normalBlockCounterUpdate, system_core_always_skips_validation]

theorem system_processor_gas_purchase_result_is_zero (input : Input) :
    let result := run input
    result.premiumPerGas = 0 ∧ result.senderReservedGasPayment = 0 ∧
      result.blobBaseFee = 0 := by
  rcases runCore_production_override_outputs input with outputs | outputs <;>
    simp [run, runWithOverrides, outputs.1, Result.premiumPerGas,
      Result.senderReservedGasPayment, Result.blobBaseFee,
      GasPurchaseOutput.premiumPerGas, GasPurchaseOutput.senderReservedGasPayment,
      GasPurchaseOutput.blobBaseFee]

theorem system_processor_nonce_result_is_noop (input : Input) :
    (run input).nonceIncrementDelta = 0 := by
  rcases runCore_production_override_outputs input with outputs | outputs <;>
    simp [run, runWithOverrides, outputs.2, Result.nonceIncrementDelta,
      NonceOutput.delta]

inductive BlockCallSite where
  | beaconRoot
  | withdrawalRequests
  | consolidationRequests
  | builderDepositRequests
  | builderExitRequests
  | historicalBlockhash
  deriving DecidableEq, Repr

def beaconRootsAddress : String := "0x000F3df6D732807Ef1319fB7B8bB8522d0Beac02"

def withdrawalRequestsAddress : String := "0x00000961Ef480Eb55e80D19ad83579A64c007002"

def consolidationRequestsAddress : String := "0x0000BBdDc7CE488642fb579F8B00f3a590007251"

def builderDepositRequestsAddress : String := "0x0000BFF46984E3725691FA540A8C7589300D8282"

def builderExitRequestsAddress : String := "0x000064D678505AD48F8CCB093BC65613800E8282"

def BlockCallSite.destination : BlockCallSite → Option String
  | .beaconRoot => some beaconRootsAddress
  | .withdrawalRequests => some withdrawalRequestsAddress
  | .consolidationRequests => some consolidationRequestsAddress
  | .builderDepositRequests => some builderDepositRequestsAddress
  | .builderExitRequests => some builderExitRequestsAddress
  | .historicalBlockhash => none

def BlockCallSite.requestType : BlockCallSite → Option Nat
  | .withdrawalRequests => some 1
  | .consolidationRequests => some 2
  | .builderDepositRequests => some 3
  | .builderExitRequests => some 4
  | _ => none

def BlockCallSite.isRequest : BlockCallSite → Bool
  | .withdrawalRequests | .consolidationRequests
  | .builderDepositRequests | .builderExitRequests => true
  | _ => false

structure BlockAdapterState where
  normalReceipts : List Nat
  cumulativeReceiptGas : Nat
  requestPayloads : List (List Nat)
  deriving DecidableEq, Repr

inductive BlockCallDisposition where
  | skipped
  | applied
  | invalidBlock
  | unmodeled
  deriving DecidableEq, Repr

inductive BlockIntegrationStatus where
  | leafOnlyUnconnected
  deriving DecidableEq, Repr

def blockIntegrationStatus : BlockIntegrationStatus := .leafOnlyUnconnected

structure BlockCallInput where
  site : BlockCallSite
  enabled : Bool
  targetExists : Bool := true
  targetHasCode : Bool
  beaconRootCalldata : List Nat := []
  requestTypeByte : Nat := 0
  processorInput : Input
  adapterState : BlockAdapterState
  deriving DecidableEq, Repr

structure BlockCallResult where
  disposition : BlockCallDisposition
  adapterState : BlockAdapterState
  processorResult : Option Result := none
  deriving DecidableEq, Repr

def blockCallShapeValid (input : BlockCallInput) : Bool :=
  input.processorInput.tracingFactsConsistent &&
  input.processorInput.options == .execute &&
  input.processorInput.tx.sender == .systemUser &&
  input.processorInput.tx.value == 0 &&
  input.processorInput.tx.gasPrice == 0 &&
  input.processorInput.tx.destination == input.site.destination &&
  input.processorInput.oracle.body.matchesTargetCode input.targetHasCode &&
  match input.site with
    | .beaconRoot =>
        input.processorInput.tx.kind == .systemCall &&
        input.processorInput.tx.gasLimit == 31_566_720 &&
        !input.processorInput.tracingReceipt &&
        input.processorInput.tracerKind == .null &&
        input.processorInput.tx.accessList == some
          { addresses := [beaconRootsAddress], storageKeys := [] } &&
        input.beaconRootCalldata.length == 32 &&
        input.beaconRootCalldata.all (· ≤ 255) &&
        input.processorInput.tx.data == input.beaconRootCalldata
    | .withdrawalRequests | .consolidationRequests
    | .builderDepositRequests | .builderExitRequests =>
        input.processorInput.tx.kind == .systemCall &&
        input.processorInput.tx.gasLimit == 31_566_720 &&
        input.processorInput.tx.data.isEmpty &&
        input.processorInput.tx.accessList.isNone &&
        input.processorInput.tracingReceipt &&
        input.processorInput.tracerKind == .callOutput &&
        input.site.requestType == some input.requestTypeByte &&
        input.requestTypeByte ≤ 255
    | .historicalBlockhash => false

def runBlockCall (input : BlockCallInput) : BlockCallResult :=
  if !input.processorInput.pinnedDomain then
    { disposition := .unmodeled, adapterState := input.adapterState }
  else if input.site == .historicalBlockhash then
    -- The pinned BlockhashStore mutates storage directly; it does not use this
    -- transaction route.
    { disposition := .unmodeled, adapterState := input.adapterState }
  else if !input.enabled then
    { disposition := .skipped, adapterState := input.adapterState }
  else if !blockCallShapeValid input then
    { disposition := .unmodeled, adapterState := input.adapterState }
  else if input.site == .beaconRoot && !input.targetExists then
    { disposition := .skipped, adapterState := input.adapterState }
  else if input.site.isRequest && !input.targetHasCode then
    { disposition := .invalidBlock, adapterState := input.adapterState }
  else
    let processorResult := run input.processorInput
    if processorResult.disposition == .unmodeled then
      { disposition := .unmodeled
      , adapterState := input.adapterState
      , processorResult := some processorResult }
    else if input.site == .beaconRoot then
      -- BeaconBlockRootHandler deliberately ignores TransactionResult.
      { disposition := .applied
      , adapterState := input.adapterState
      , processorResult := some processorResult }
    else
      match processorResult.callOutputTrace with
      | none =>
          { disposition := .unmodeled
          , adapterState := input.adapterState
          , processorResult := some processorResult }
      | some tracer =>
          if tracer.status == .failure then
            { disposition := .invalidBlock
            , adapterState := input.adapterState
            , processorResult := some processorResult }
          else if tracer.returnValue.isEmpty then
            { disposition := .applied
            , adapterState := input.adapterState
            , processorResult := some processorResult }
          else
            { disposition := .applied
            , adapterState :=
                { input.adapterState with
                  requestPayloads := input.adapterState.requestPayloads ++
                    [[input.requestTypeByte] ++ tracer.returnValue] }
            , processorResult := some processorResult }

theorem request_type_is_byte_bounded (site : BlockCallSite) (requestType : Nat)
    (h : site.requestType = some requestType) : requestType ≤ 255 := by
  cases site <;> simp [BlockCallSite.requestType] at h <;> omega

theorem exact_block_call_sites_do_not_append_normal_receipts (input : BlockCallInput) :
    (runBlockCall input).adapterState.normalReceipts = input.adapterState.normalReceipts ∧
      (runBlockCall input).adapterState.cumulativeReceiptGas =
        input.adapterState.cumulativeReceiptGas := by
  grind (splits := 20) [runBlockCall]

theorem unpinned_block_call_fails_before_shape (input : BlockCallInput)
    (h : input.processorInput.pinnedDomain = false) :
    (runBlockCall input).disposition = .unmodeled := by
  simp [runBlockCall, h]

theorem block_call_never_applies_unmodeled_processor (input : BlockCallInput)
    (h : (run input.processorInput).disposition = .unmodeled) :
    (runBlockCall input).disposition != .applied := by
  grind (splits := 20) [runBlockCall]

end SystemTransactionReference
end Eip803x
