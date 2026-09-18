-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.Production
import Eip803x.Evm.MemoryStackControl
import Lean.Elab.Tactic.Omega

namespace Eip803x
namespace Precompiles
namespace Wrapper

open Evm.MemoryStackControl

/-- The largest value accepted by the production `ulong` gas carrier. -/
def uint64Max : Nat := 2 ^ 64 - 1

/-- The two independently computed `ulong` costs returned by an `IPrecompile`. -/
structure Cost where
  base : Nat
  data : Nat
  deriving DecidableEq, Repr

/-- The wrapper can distinguish the overflow guard from an ordinary insufficient-gas result. -/
inductive PricingFailure where
  | baseDataOverflow
  | outOfGas
  deriving DecidableEq, Repr

inductive PricingResult where
  | success (gas : ProductionGasState) (charged : Nat)
  | failure (reason : PricingFailure) (gas : ProductionGasState)
  deriving DecidableEq, Repr

/--
The `baseGasCost <= ulong.MaxValue - dataGasCost` guard from
`IGasPolicy.TryConsumePrecompileGas`. The extra data bound is the explicit Lean-side
representation obligation: production has already obtained both inputs as `ulong`.
-/
def totalCost? (cost : Cost) : Option Nat :=
  if cost.data <= uint64Max && cost.base <= uint64Max - cost.data then
    some (cost.base + cost.data)
  else
    none

/-- Overflow preserves gas; an affordable-width but insufficient charge zeros execution gas. -/
def tryConsumePrecompileGas (cost : Cost) (gas : ProductionGasState) : PricingResult :=
  match totalCost? cost with
  | none => .failure .baseDataOverflow gas
  | some total =>
    if total <= gas.gasLeft then
      .success { gas with gasLeft := gas.gasLeft - total } total
    else
      .failure .outOfGas { gas with gasLeft := 0 }

def clearExecutionGas (gas : ProductionGasState) : ProductionGasState :=
  { gas with gasLeft := 0 }

/-- The outstanding part of a child's state-gas spill. -/
def unrefundedSpill (gas : ProductionGasState) : Int :=
  max (gas.stateGasSpill - gas.stateGasSpillRefunded) 0

/-- Exact field-level transcription of `EthereumGasPolicy.Refund`. -/
def refundChildGas (parent child : ProductionGasState) : ProductionGasState :=
  { gasLeft := parent.gasLeft + child.gasLeft
    stateReservoir := parent.stateReservoir + child.stateReservoir
    stateGasUsed := parent.stateGasUsed + child.stateGasUsed
    stateGasSpill := parent.stateGasSpill + child.stateGasSpill
    stateGasSpillRefunded := parent.stateGasSpillRefunded + child.stateGasSpillRefunded }

/-- Exact successful-frame spill repayment performed after the full child commits. -/
def repayStateGasSpill (gas : ProductionGasState) : ProductionGasState :=
  let repayment := min gas.stateReservoir (unrefundedSpill gas)
  if repayment <= 0 then
    gas
  else
    { gas with
      gasLeft := gas.gasLeft + Int.toNat repayment
      stateReservoir := gas.stateReservoir - repayment
      stateGasSpillRefunded := gas.stateGasSpillRefunded + repayment }

/-- Exact field-level result of `RestoreChildStateGas` after the child execution gas was cleared. -/
def restoreChildStateGas (parent child : ProductionGasState) : ProductionGasState :=
  let spill := unrefundedSpill child
  { gasLeft := parent.gasLeft + Int.toNat spill
    stateReservoir := parent.stateReservoir + child.stateReservoir + child.stateGasUsed - spill
    stateGasUsed := parent.stateGasUsed
    stateGasSpill := parent.stateGasSpill
    stateGasSpillRefunded := parent.stateGasSpillRefunded }

/-- Exact field-level transcription of `RestoreChildStateGasOnHalt`. -/
def restoreChildStateGasOnHalt (parent child : ProductionGasState) : ProductionGasState :=
  let spill := unrefundedSpill child
  { gasLeft := parent.gasLeft
    stateReservoir := parent.stateReservoir + child.stateReservoir + child.stateGasUsed - spill
    stateGasUsed := parent.stateGasUsed
    stateGasSpill := parent.stateGasSpill
    stateGasSpillRefunded := parent.stateGasSpillRefunded }

/--
`CreateChildFrameGas` is called after `TryReserveChildGas` has already removed the
forwarded execution gas from the parent. It moves the complete reservoir to the child.
-/
structure ChildEntry where
  parent : ProductionGasState
  child : ProductionGasState
  deriving DecidableEq, Repr

def createChildFrameGas (postReservationParent : ProductionGasState)
    (forwarded : Nat) : ChildEntry :=
  { parent := { postReservationParent with stateReservoir := 0 }
    child :=
      { gasLeft := forwarded
        stateReservoir := postReservationParent.stateReservoir
        stateGasUsed := 0
        stateGasSpill := 0
        stateGasSpillRefunded := 0 } }

/-- The `IPrecompile.Run` result is an explicit oracle at this wrapper boundary. -/
inductive LeafResult where
  | success (output : List Byte)
  | failure (renderedError : String)
  | managedException
  deriving DecidableEq, Repr

inductive ExceptionKind where
  | none
  | outOfGas
  | precompileFailure
  deriving DecidableEq, Repr

/-- The four fields constructed by `RunPrecompile` / `ExecutePrecompileCall`. -/
structure CallFlags where
  output : List Byte
  precompileSuccess : Bool
  shouldRevert : Bool
  exception : ExceptionKind
  substateError : Option String
  deriving DecidableEq, Repr

def pricingFailureFlags : CallFlags :=
  { output := []
    precompileSuccess := false
    shouldRevert := true
    exception := .outOfGas
    substateError := none }

def flagsForLeaf : LeafResult -> CallFlags
  | .success output =>
    { output
      precompileSuccess := true
      shouldRevert := false
      exception := .none
      substateError := none }
  | .failure renderedError =>
    { output := []
      precompileSuccess := false
      shouldRevert := true
      exception := .precompileFailure
      substateError := some renderedError }
  | .managedException =>
    { output := []
      precompileSuccess := false
      shouldRevert := true
      exception := .none
      substateError := none }

/-- Facts supplied by the world-state adapter around the balance-credit operation. -/
structure AccountFacts where
  priorRestorePending : Bool
  wasCreated : Bool
  transferValueIsZero : Bool
  eip158Active : Bool
  isRipemd160 : Bool
  deadAfterCredit : Bool
  existsAfterSnapshotRestore : Bool
  deriving DecidableEq, Repr

/-- Whether this invocation newly meets the historical EIP-161/Parity condition. -/
def qualifiesRipemdTouch (facts : AccountFacts) : Bool :=
  !facts.wasCreated && facts.transferValueIsZero && facts.eip158Active &&
    facts.isRipemd160 && facts.deadAfterCredit

/-- `_shouldRestoreRipemdTouch` is transaction-sticky once any invocation qualifies. -/
def shouldRestoreRipemdTouch (facts : AccountFacts) : Bool :=
  facts.priorRestorePending || qualifiesRipemdTouch facts

structure WorldEffects where
  accountTouchOrCreditDurable : Bool
  ripemdDirtyTouchDurable : Bool
  deriving DecidableEq, Repr

def successfulWorld (facts : AccountFacts) : WorldEffects :=
  { accountTouchOrCreditDurable := true
    ripemdDirtyTouchDurable := shouldRestoreRipemdTouch facts }

def restoredWorld (facts : AccountFacts) : WorldEffects :=
  { accountTouchOrCreditDurable := false
    ripemdDirtyTouchDurable :=
      shouldRestoreRipemdTouch facts && facts.existsAfterSnapshotRestore }

/-- A chronological audit of wrapper control flow; opaque logging is named but not interpreted. -/
inductive Event where
  | transferLogOracle
  | accountTouchedOrCreated
  | ripemdRestoreMarked
  | pricingChecked (base data : Nat)
  | pricingRejected (reason : PricingFailure)
  | executionGasDebited (amount : Nat)
  | localExecutionGasCleared
  | precompileLeafInvoked
  | childExecutionCleared
  | childExecutionDiscarded
  | childRefunded
  | childCommitted
  | stateSpillRepaid
  | childStateRestoredOnRevert
  | childStateRestoredOnHalt
  | snapshotRestored
  | ripemdTouchRestored
  | directPathDeclined
  | inputMemoryRejected
  | returnDataSet (data : List Byte)
  | outputCopied (data : List Byte)
  | stackResultPushed (success : Bool)
  deriving DecidableEq, Repr

def ripemdMarkEvents (facts : AccountFacts) : List Event :=
  if qualifiesRipemdTouch facts then [.ripemdRestoreMarked] else []

def ripemdRestoreEvents (facts : AccountFacts) : List Event :=
  if shouldRestoreRipemdTouch facts && facts.existsAfterSnapshotRestore then
    [.ripemdTouchRestored]
  else
    []

def pricingRejectionEvents : PricingFailure -> List Event
  | .baseDataOverflow => [.pricingRejected .baseDataOverflow]
  | .outOfGas => [.localExecutionGasCleared, .pricingRejected .outOfGas]

structure FullInput where
  cost : Cost
  gas : ProductionGasState
  account : AccountFacts
  leaf : LeafResult
  deriving DecidableEq, Repr

inductive RawKind where
  | success
  | precompileFailure
  | managedException
  | pricingOutOfGas (reason : PricingFailure)
  deriving DecidableEq, Repr

structure RawOutcome where
  kind : RawKind
  restorePending : Bool
  /-- Gas installed on the VM state; failed full-frame pricing deliberately keeps the prior value. -/
  gas : ProductionGasState
  /-- The local gas value after `TryConsumePrecompileGas`, before the success-only assignment. -/
  localPricingGas : ProductionGasState
  call : CallFlags
  events : List Event
  deriving DecidableEq, Repr

def fullPrelude (input : FullInput) : List Event :=
  [.transferLogOracle, .accountTouchedOrCreated] ++ ripemdMarkEvents input.account ++
    [.pricingChecked input.cost.base input.cost.data]

/--
Handwritten `RunPrecompile<Eip158>` plus `ExecutePrecompileCall`, before the outer
top-level/nested-frame dispatch interprets the returned flags.
-/
def runFullRaw (input : FullInput) : RawOutcome :=
  let preludeEvents := fullPrelude input
  match tryConsumePrecompileGas input.cost input.gas with
  | .failure reason localGas =>
    { kind := .pricingOutOfGas reason
      restorePending := shouldRestoreRipemdTouch input.account
      gas := input.gas
      localPricingGas := localGas
      call := pricingFailureFlags
      events := preludeEvents ++ pricingRejectionEvents reason }
  | .success gas charged =>
    let events := preludeEvents ++ [.executionGasDebited charged, .precompileLeafInvoked]
    match input.leaf with
    | .success output =>
      { kind := .success, restorePending := shouldRestoreRipemdTouch input.account,
        gas, localPricingGas := gas,
        call := flagsForLeaf (.success output), events }
    | .failure error =>
      { kind := .precompileFailure, restorePending := shouldRestoreRipemdTouch input.account,
        gas, localPricingGas := gas,
        call := flagsForLeaf (.failure error), events }
    | .managedException =>
      { kind := .managedException, restorePending := shouldRestoreRipemdTouch input.account,
        gas, localPricingGas := gas,
        call := flagsForLeaf .managedException, events }

inductive TopStatus where
  | success
  | exceptional (exception : ExceptionKind)
  deriving DecidableEq, Repr

structure TopOutcome where
  status : TopStatus
  restorePending : Bool
  gas : ProductionGasState
  rawCall : CallFlags
  world : WorldEffects
  events : List Event
  deriving DecidableEq, Repr

/--
At the VM boundary, a returned precompile failure and pricing OOG become `OutOfGas`;
a non-fatal managed exception becomes `PrecompileFailure`. Transaction settlement later
performs the top-level halt gas reset and is outside this slice.
-/
def executeTop (input : FullInput) : TopOutcome :=
  let raw := runFullRaw input
  match raw.kind with
  | .success =>
    { status := .success
      restorePending := raw.restorePending
      gas := raw.gas
      rawCall := raw.call
      world := successfulWorld input.account
      events := raw.events }
  | .pricingOutOfGas _ | .precompileFailure =>
    { status := .exceptional .outOfGas
      restorePending := raw.restorePending
      gas := raw.gas
      rawCall := raw.call
      world := restoredWorld input.account
      events := raw.events ++ [.snapshotRestored] ++ ripemdRestoreEvents input.account }
  | .managedException =>
    { status := .exceptional .precompileFailure
      restorePending := raw.restorePending
      gas := raw.gas
      rawCall := raw.call
      world := restoredWorld input.account
      events := raw.events ++ [.snapshotRestored] ++ ripemdRestoreEvents input.account }

inductive ChildExit where
  | success
  | reverted
  | exceptionalHalt
  deriving DecidableEq, Repr

structure NestedOutcome where
  exit : ChildExit
  vmException : ExceptionKind
  restorePending : Bool
  parentGas : ProductionGasState
  childGasAtExit : ProductionGasState
  rawCall : CallFlags
  returnData : List Byte
  copiedOutput : List Byte
  stackResult : Bool
  world : WorldEffects
  events : List Event
  deriving DecidableEq, Repr

/-- Full child-frame handling, including the later resumed CALL result and clipped output copy. -/
def executeNested (pausedParent : ProductionGasState) (outputLength : Nat)
    (input : FullInput) : NestedOutcome :=
  let raw := runFullRaw input
  match raw.kind with
  | .success =>
    let copied := raw.call.output.take outputLength
    { exit := .success
      vmException := .none
      restorePending := raw.restorePending
      parentGas := repayStateGasSpill (refundChildGas pausedParent raw.gas)
      childGasAtExit := raw.gas
      rawCall := raw.call
      returnData := raw.call.output
      copiedOutput := copied
      stackResult := true
      world := successfulWorld input.account
      events := raw.events ++ [.childRefunded, .returnDataSet raw.call.output,
        .childCommitted, .stateSpillRepaid, .stackResultPushed true, .outputCopied copied] }
  | .managedException =>
    let cleared := clearExecutionGas raw.gas
    { exit := .reverted
      vmException := .none
      restorePending := raw.restorePending
      parentGas := restoreChildStateGas pausedParent cleared
      childGasAtExit := cleared
      rawCall := raw.call
      returnData := []
      copiedOutput := []
      stackResult := false
      world := restoredWorld input.account
      events := raw.events ++ [.childExecutionCleared, .childStateRestoredOnRevert,
        .snapshotRestored] ++ ripemdRestoreEvents input.account ++
        [.returnDataSet [], .stackResultPushed false, .outputCopied []] }
  | .pricingOutOfGas _ | .precompileFailure =>
    { exit := .exceptionalHalt
      vmException := .outOfGas
      restorePending := raw.restorePending
      parentGas := restoreChildStateGasOnHalt pausedParent raw.gas
      childGasAtExit := raw.gas
      rawCall := raw.call
      returnData := []
      copiedOutput := []
      stackResult := false
      world := restoredWorld input.account
      events := raw.events ++ [.snapshotRestored] ++ ripemdRestoreEvents input.account ++
        [.returnDataSet [], .childStateRestoredOnHalt, .childExecutionDiscarded,
          .stackResultPushed false, .outputCopied []] }

structure DirectInput where
  traceInstructions : Bool
  traceActions : Bool
  isRipemdCodeSource : Bool
  inputMemoryValid : Bool
  postReservationParent : ProductionGasState
  forwardedGas : Nat
  cost : Cost
  leaf : LeafResult
  outputLength : Nat
  priorReturnData : List Byte
  deriving DecidableEq, Repr

def directEligible (input : DirectInput) : Bool :=
  !input.traceInstructions && !input.traceActions && !input.isRipemdCodeSource

inductive DirectStatus where
  | declined
  | inputMemoryOutOfGas
  | handledSuccess
  | handledFailure
  deriving DecidableEq, Repr

structure DirectOutcome where
  status : DirectStatus
  parentGas : ProductionGasState
  childGasAtExit : Option ProductionGasState
  opcodeException : ExceptionKind
  returnData : List Byte
  copiedOutput : List Byte
  stackResult : Option Bool
  accountTouchDurable : Bool
  events : List Event
  deriving DecidableEq, Repr

/--
The standard-build inline STATICCALL path. The caller has already reserved execution gas;
the helper either declines without effects or owns the child reservoir until refund/halt.
-/
def executeDirect (input : DirectInput) : DirectOutcome :=
  if !directEligible input then
    { status := .declined
      parentGas := input.postReservationParent
      childGasAtExit := none
      opcodeException := .none
      returnData := input.priorReturnData
      copiedOutput := []
      stackResult := none
      accountTouchDurable := false
      events := [.directPathDeclined] }
  else if !input.inputMemoryValid then
    { status := .inputMemoryOutOfGas
      parentGas := input.postReservationParent
      childGasAtExit := none
      opcodeException := .outOfGas
      returnData := input.priorReturnData
      copiedOutput := []
      stackResult := none
      accountTouchDurable := false
      events := [.inputMemoryRejected] }
  else
    let entry := createChildFrameGas input.postReservationParent input.forwardedGas
    let pricingEvents := [.pricingChecked input.cost.base input.cost.data]
    match tryConsumePrecompileGas input.cost entry.child with
    | .failure reason childAfterAttempt =>
      { status := .handledFailure
        parentGas := restoreChildStateGasOnHalt entry.parent childAfterAttempt
        childGasAtExit := some childAfterAttempt
        opcodeException := .none
        returnData := []
        copiedOutput := []
        stackResult := some false
        accountTouchDurable := false
        events := pricingEvents ++ pricingRejectionEvents reason ++
          [.childStateRestoredOnHalt, .returnDataSet [], .stackResultPushed false,
            .childExecutionDiscarded] }
    | .success child charged =>
      let events := pricingEvents ++ [.executionGasDebited charged, .precompileLeafInvoked]
      match input.leaf with
      | .success output =>
        let copied := output.take input.outputLength
        { status := .handledSuccess
          parentGas := refundChildGas entry.parent child
          childGasAtExit := some child
          opcodeException := .none
          returnData := output
          copiedOutput := copied
          stackResult := some true
          accountTouchDurable := true
          events := events ++ [.accountTouchedOrCreated, .childRefunded,
            .returnDataSet output, .outputCopied copied, .stackResultPushed true] }
      | .failure _ =>
        let cleared := clearExecutionGas child
        { status := .handledFailure
          parentGas := restoreChildStateGasOnHalt entry.parent cleared
          childGasAtExit := some cleared
          opcodeException := .none
          returnData := []
          copiedOutput := []
          stackResult := some false
          accountTouchDurable := false
          events := events ++ [.childExecutionCleared, .childStateRestoredOnHalt,
            .returnDataSet [], .stackResultPushed false] }
      | .managedException =>
        let cleared := clearExecutionGas child
        { status := .handledFailure
          parentGas := restoreChildStateGasOnHalt entry.parent cleared
          childGasAtExit := some cleared
          opcodeException := .none
          returnData := []
          copiedOutput := []
          stackResult := some false
          accountTouchDurable := false
          events := events ++ [.childExecutionCleared, .childStateRestoredOnHalt,
            .returnDataSet [], .stackResultPushed false] }

theorem totalCost_overflow {cost : Cost} (hData : cost.data <= uint64Max)
    (hOverflow : uint64Max - cost.data < cost.base) : totalCost? cost = none := by
  simp [totalCost?, hData, Nat.not_le_of_lt hOverflow]

theorem pricing_overflow_preserves_raw_gas {input : FullInput}
    (h : totalCost? input.cost = none) :
    (runFullRaw input).kind = .pricingOutOfGas .baseDataOverflow /\
      (runFullRaw input).gas = input.gas /\
      (runFullRaw input).localPricingGas = input.gas /\
      (runFullRaw input).call = pricingFailureFlags := by
  simp [runFullRaw, tryConsumePrecompileGas, h]

theorem pricing_one_short_discards_zeroed_local_gas {input : FullInput} {total : Nat}
    (hCost : totalCost? input.cost = some total) (hGas : input.gas.gasLeft < total) :
    (runFullRaw input).kind = .pricingOutOfGas .outOfGas /\
      (runFullRaw input).gas = input.gas /\
      (runFullRaw input).localPricingGas = clearExecutionGas input.gas /\
      (runFullRaw input).call = pricingFailureFlags := by
  simp [runFullRaw, tryConsumePrecompileGas, hCost, Nat.not_le_of_lt hGas,
    clearExecutionGas]

theorem pricing_success_debits_only_execution {cost : Cost} {gas : ProductionGasState}
    {total : Nat} (hCost : totalCost? cost = some total) (hGas : total <= gas.gasLeft) :
    tryConsumePrecompileGas cost gas =
      .success { gas with gasLeft := gas.gasLeft - total } total := by
  simp [tryConsumePrecompileGas, hCost, hGas]

theorem full_account_action_precedes_pricing (input : FullInput) :
    exists tail, (runFullRaw input).events = fullPrelude input ++ tail := by
  unfold runFullRaw
  cases hPrice : tryConsumePrecompileGas input.cost input.gas with
  | failure reason => simp
  | success gas charged =>
    cases input.leaf <;> simp

theorem full_pricing_rejection_never_invokes_leaf {input : FullInput}
    {reason : PricingFailure} {localGas : ProductionGasState}
    (h : tryConsumePrecompileGas input.cost input.gas = .failure reason localGas) :
    (runFullRaw input).events = fullPrelude input ++ pricingRejectionEvents reason := by
  simp [runFullRaw, h]

theorem ripemd_exception_condition_exact (facts : AccountFacts) :
    qualifiesRipemdTouch facts =
      (!facts.wasCreated && facts.transferValueIsZero && facts.eip158Active &&
        facts.isRipemd160 && facts.deadAfterCredit) := rfl

theorem ripemd_restore_pending_is_sticky (facts : AccountFacts) :
    shouldRestoreRipemdTouch facts =
      (facts.priorRestorePending || qualifiesRipemdTouch facts) := rfl

theorem full_raw_propagates_sticky_ripemd_flag (input : FullInput) :
    (runFullRaw input).restorePending =
      (input.account.priorRestorePending || qualifiesRipemdTouch input.account) := by
  unfold runFullRaw shouldRestoreRipemdTouch
  cases hPrice : tryConsumePrecompileGas input.cost input.gas with
  | failure reason localGas => rfl
  | success gas charged => cases input.leaf <;> rfl

theorem top_failure_restores_account_effect {input : FullInput}
    (h : (runFullRaw input).kind ≠ .success) :
    (executeTop input).world = restoredWorld input.account := by
  unfold executeTop
  cases hRaw : runFullRaw input with
  | mk kind restorePending gas localPricingGas call events =>
    cases kind <;> simp_all

theorem top_pricing_failure_is_exceptional_oog {input : FullInput}
    {reason : PricingFailure}
    (h : (runFullRaw input).kind = .pricingOutOfGas reason) :
    (executeTop input).status = .exceptional .outOfGas := by
  simp [executeTop, h]

theorem top_returned_precompile_failure_is_exceptional_oog {input : FullInput}
    (h : (runFullRaw input).kind = .precompileFailure) :
    (executeTop input).status = .exceptional .outOfGas := by
  simp [executeTop, h]

theorem top_managed_exception_is_precompile_exception {input : FullInput}
    (h : (runFullRaw input).kind = .managedException) :
    (executeTop input).status = .exceptional .precompileFailure := by
  simp [executeTop, h]

theorem nested_success_refunds_child {parent : ProductionGasState} {outputLength : Nat}
    {input : FullInput} (h : (runFullRaw input).kind = .success) :
    let raw := runFullRaw input
    (executeNested parent outputLength input).exit = .success /\
      (executeNested parent outputLength input).parentGas =
        repayStateGasSpill (refundChildGas parent raw.gas) /\
      (executeNested parent outputLength input).returnData = raw.call.output /\
      (executeNested parent outputLength input).copiedOutput = raw.call.output.take outputLength /\
      (executeNested parent outputLength input).stackResult = true := by
  simp [executeNested, h]

theorem nested_managed_exception_clears_execution_and_restores_state
    {parent : ProductionGasState}
    {outputLength : Nat} {input : FullInput}
    (h : (runFullRaw input).kind = .managedException) :
    let raw := runFullRaw input
    (executeNested parent outputLength input).exit = .reverted /\
      (executeNested parent outputLength input).childGasAtExit.gasLeft = 0 /\
      (executeNested parent outputLength input).parentGas =
        restoreChildStateGas parent (clearExecutionGas raw.gas) /\
      (executeNested parent outputLength input).returnData = [] /\
      (executeNested parent outputLength input).stackResult = false := by
  simp [executeNested, h, clearExecutionGas]

theorem nested_returned_precompile_failure_is_exceptional_halt
    {parent : ProductionGasState} {outputLength : Nat} {input : FullInput}
    (h : (runFullRaw input).kind = .precompileFailure) :
    let raw := runFullRaw input
    (executeNested parent outputLength input).exit = .exceptionalHalt /\
      (executeNested parent outputLength input).vmException = .outOfGas /\
      (executeNested parent outputLength input).parentGas =
        restoreChildStateGasOnHalt parent raw.gas /\
      (executeNested parent outputLength input).childGasAtExit = raw.gas /\
      (executeNested parent outputLength input).parentGas.gasLeft = parent.gasLeft /\
      (executeNested parent outputLength input).returnData = [] /\
      (executeNested parent outputLength input).stackResult = false := by
  simp [executeNested, h, restoreChildStateGasOnHalt]

theorem nested_pricing_halt_discards_execution_and_restores_state
    {parent : ProductionGasState} {outputLength : Nat} {input : FullInput}
    {reason : PricingFailure} (h : (runFullRaw input).kind = .pricingOutOfGas reason) :
    let raw := runFullRaw input
    (executeNested parent outputLength input).exit = .exceptionalHalt /\
      (executeNested parent outputLength input).vmException = .outOfGas /\
      (executeNested parent outputLength input).parentGas =
        restoreChildStateGasOnHalt parent raw.gas /\
      (executeNested parent outputLength input).parentGas.gasLeft = parent.gasLeft /\
      (executeNested parent outputLength input).returnData = [] /\
      (executeNested parent outputLength input).stackResult = false := by
  simp [executeNested, h, restoreChildStateGasOnHalt]

theorem restored_ripemd_touch_is_the_only_retained_failed_world_effect
    (facts : AccountFacts) :
    (restoredWorld facts).accountTouchOrCreditDurable = false /\
      (restoredWorld facts).ripemdDirtyTouchDurable =
        (shouldRestoreRipemdTouch facts && facts.existsAfterSnapshotRestore) := by
  exact ⟨rfl, rfl⟩

theorem direct_decline_is_effect_free {input : DirectInput}
    (h : directEligible input = false) :
    executeDirect input =
      { status := .declined
        parentGas := input.postReservationParent
        childGasAtExit := none
        opcodeException := .none
        returnData := input.priorReturnData
        copiedOutput := []
        stackResult := none
        accountTouchDurable := false
        events := [.directPathDeclined] } := by
  simp [executeDirect, h]

theorem direct_ripemd_is_never_eligible (input : DirectInput)
    (h : input.isRipemdCodeSource = true) : directEligible input = false := by
  simp [directEligible, h]

theorem direct_input_memory_failure_precedes_child_creation {input : DirectInput}
    (hEligible : directEligible input = true) (hMemory : input.inputMemoryValid = false) :
    (executeDirect input).status = .inputMemoryOutOfGas /\
      (executeDirect input).parentGas = input.postReservationParent /\
      (executeDirect input).childGasAtExit = none /\
      (executeDirect input).opcodeException = .outOfGas := by
  simp [executeDirect, hEligible, hMemory]

theorem direct_pricing_halt_restores_reservoir_without_execution_refund
    {input : DirectInput} {reason : PricingFailure} {childAfterAttempt : ProductionGasState}
    (hEligible : directEligible input = true) (hMemory : input.inputMemoryValid = true)
    (hPrice : tryConsumePrecompileGas input.cost
      (createChildFrameGas input.postReservationParent input.forwardedGas).child =
        .failure reason childAfterAttempt) :
    let entry := createChildFrameGas input.postReservationParent input.forwardedGas
    (executeDirect input).status = .handledFailure /\
      (executeDirect input).parentGas =
        restoreChildStateGasOnHalt entry.parent childAfterAttempt /\
      (executeDirect input).childGasAtExit = some childAfterAttempt /\
      (executeDirect input).parentGas.gasLeft = entry.parent.gasLeft /\
      (executeDirect input).returnData = [] /\
      (executeDirect input).stackResult = some false /\
      (executeDirect input).accountTouchDurable = false := by
  simp [executeDirect, hEligible, hMemory, hPrice, restoreChildStateGasOnHalt]

theorem direct_leaf_failure_clears_child_execution {input : DirectInput}
    {child : ProductionGasState} {charged : Nat} {error : String}
    (hEligible : directEligible input = true) (hMemory : input.inputMemoryValid = true)
    (hPrice : tryConsumePrecompileGas input.cost
      (createChildFrameGas input.postReservationParent input.forwardedGas).child =
        .success child charged)
    (hLeaf : input.leaf = .failure error) :
    (executeDirect input).status = .handledFailure /\
      (executeDirect input).childGasAtExit = some (clearExecutionGas child) /\
      (executeDirect input).returnData = [] /\
      (executeDirect input).stackResult = some false /\
      (executeDirect input).accountTouchDurable = false := by
  simp [executeDirect, hEligible, hMemory, hPrice, hLeaf]

theorem direct_managed_exception_clears_child_execution {input : DirectInput}
    {child : ProductionGasState} {charged : Nat}
    (hEligible : directEligible input = true) (hMemory : input.inputMemoryValid = true)
    (hPrice : tryConsumePrecompileGas input.cost
      (createChildFrameGas input.postReservationParent input.forwardedGas).child =
        .success child charged)
    (hLeaf : input.leaf = .managedException) :
    (executeDirect input).status = .handledFailure /\
      (executeDirect input).childGasAtExit = some (clearExecutionGas child) /\
      (executeDirect input).stackResult = some false := by
  simp [executeDirect, hEligible, hMemory, hPrice, hLeaf]

theorem direct_returned_failure_and_managed_exception_converge
    (input : DirectInput) (error : String) :
    executeDirect { input with leaf := .failure error } =
      executeDirect { input with leaf := .managedException } := by
  simp only [executeDirect, directEligible]

theorem direct_success_sets_full_returndata_and_clips_copy {input : DirectInput}
    {child : ProductionGasState} {charged : Nat} {output : List Byte}
    (hEligible : directEligible input = true) (hMemory : input.inputMemoryValid = true)
    (hPrice : tryConsumePrecompileGas input.cost
      (createChildFrameGas input.postReservationParent input.forwardedGas).child =
        .success child charged)
    (hLeaf : input.leaf = .success output) :
    let entry := createChildFrameGas input.postReservationParent input.forwardedGas
    (executeDirect input).status = .handledSuccess /\
      (executeDirect input).parentGas = refundChildGas entry.parent child /\
      (executeDirect input).returnData = output /\
      (executeDirect input).copiedOutput = output.take input.outputLength /\
      (executeDirect input).stackResult = some true /\
      (executeDirect input).accountTouchDurable = true := by
  simp [executeDirect, hEligible, hMemory, hPrice, hLeaf]

theorem halt_restoration_never_refunds_child_execution
    (parent child : ProductionGasState) :
    (restoreChildStateGasOnHalt parent child).gasLeft = parent.gasLeft := by
  rfl

theorem refund_returns_child_execution (parent child : ProductionGasState) :
    (refundChildGas parent child).gasLeft = parent.gasLeft + child.gasLeft := by
  rfl

theorem create_child_moves_complete_reservoir_and_resets_child_state_counters
    (parent : ProductionGasState) (forwarded : Nat) :
    (createChildFrameGas parent forwarded).parent.stateReservoir = 0 /\
      (createChildFrameGas parent forwarded).child.gasLeft = forwarded /\
      (createChildFrameGas parent forwarded).child.stateReservoir = parent.stateReservoir /\
      (createChildFrameGas parent forwarded).child.stateGasUsed = 0 /\
      (createChildFrameGas parent forwarded).child.stateGasSpill = 0 /\
      (createChildFrameGas parent forwarded).child.stateGasSpillRefunded = 0 := by
  simp [createChildFrameGas]

theorem halt_restoration_preserves_parent_state_counters
    (parent child : ProductionGasState) :
    (restoreChildStateGasOnHalt parent child).stateGasUsed = parent.stateGasUsed /\
      (restoreChildStateGasOnHalt parent child).stateGasSpill = parent.stateGasSpill /\
      (restoreChildStateGasOnHalt parent child).stateGasSpillRefunded =
        parent.stateGasSpillRefunded := by
  exact ⟨rfl, rfl, rfl⟩

theorem successful_full_merge_repays_outstanding_spill (parent child : ProductionGasState) :
    repayStateGasSpill (refundChildGas parent child) =
      let merged := refundChildGas parent child
      let repayment := min merged.stateReservoir (unrefundedSpill merged)
      if repayment <= 0 then merged else
        { merged with
          gasLeft := merged.gasLeft + Int.toNat repayment
          stateReservoir := merged.stateReservoir - repayment
          stateGasSpillRefunded := merged.stateGasSpillRefunded + repayment } := by
  rfl

end Wrapper
end Precompiles
end Eip803x
