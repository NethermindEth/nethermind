-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.Gas
import Eip803x.AccountPricing
import Eip803x.Evm.MemoryStackControl
import Lean.Elab.Tactic.Omega

namespace Eip803x
namespace Evm
namespace CallCreateFrame

open GasMachine
open AccountPricing
open MemoryStackControl
open Word

local instance : DecidableEq Stack := fun left right =>
  match decEq left.words right.words with
  | isTrue h => isTrue (by cases left; cases right; simp_all)
  | isFalse h => isFalse (fun equal => h (congrArg Stack.words equal))

/-!
  A bounded, executable reference for the Amsterdam CALL-family and CREATE-family
  handler boundary.  The model deliberately stops at the boundary where Nethermind
  hands facts to the VM loop.  Address derivation, code loading, memory expansion,
  world-state facts, and child execution are therefore explicit inputs, not hidden
  claims about CLR code.

  The order is the important part of this leaf.  CALL charges its value/base/access
  work before the NEW_ACCOUNT state charge, then applies EIP-150 only to execution
  gas.  CREATE charges CREATE_ACCESS/initcode/hash and memory before checking depth,
  balance, and nonce; only after those checks does it derive and inspect the target.
  A `FrameEntry` carries the independent state reservoir through the child, while
  `finishFrame` supplies the success/revert/exception journal boundary.
-/

def maxCallDepth : Nat := 1024

inductive CallKind where
  | call
  | callcode
  | delegatecall
  | staticcall
  deriving DecidableEq, Repr

namespace CallKind

def hasExplicitValue : CallKind → Bool
  | .call | .callcode => true
  | .delegatecall | .staticcall => false

def hasTransfer (kind : CallKind) (value : Nat) : Bool :=
  (kind == .call || kind == .callcode) && value != 0

def hasWriteCharge : CallKind → Bool
  | .call | .callcode => true
  | .delegatecall | .staticcall => false

def isStatic : CallKind → Bool
  | .staticcall => true
  | _ => false

end CallKind

inductive CreateKind where
  | create
  | create2
  deriving DecidableEq, Repr

namespace CreateKind

def hashWords : CreateKind → Nat → AccountGasSchedule → Nat
  | .create, _, _ => 0
  | .create2, words, schedule => words * schedule.create2HashWordCost

end CreateKind

abbrev AccountStatus := AccountPricing.AccountStatus

inductive CollisionKind where
  | none
  | nonEmptyCode
  | nonZeroNonce
  | storageOnly
  deriving DecidableEq, Repr

def CollisionKind.isCollision : CollisionKind → Bool
  | .none => false
  | .nonEmptyCode | .nonZeroNonce | .storageOnly => true

inductive RuntimeCodeKind where
  | empty
  | fresh
  | duplicate
  | childRevert
  | invalid
  | depositOutOfGas
  deriving DecidableEq, Repr

inductive ChildExit where
  | success
  | revert
  | exceptional
  deriving DecidableEq, Repr

/-- Abstract gas events that a child VM may expose to this boundary leaf.  The
trace is deliberately explicit: this reference does not infer an arbitrary
child gas state from its final fields. -/
inductive FrameGasStep where
  | execution (amount : Nat)
  | state (amount : Nat)
  | refill (amount : Nat)
  /-- A signed SSTORE/refund-counter delta supplied by the child execution trace. -/
  | refund (delta : Int)
  deriving DecidableEq, Repr

def frameGasStep (step : FrameGasStep) (frame : FrameGasState) : Option FrameGasState :=
  match step with
  | .execution amount =>
      match chargeExecution amount frame.gas with
      | .ok gas => some { frame with gas := gas }
      | .error _ => none
  | .state amount =>
      match chargeState amount frame.gas with
      | .ok gas => some { frame with gas := gas }
      | .error _ => none
  | .refill amount =>
      if amount <= frame.gas.stateUsed then
        some { frame with gas := refillState amount frame.gas }
      else
        none
  | .refund delta =>
      some { frame with gas :=
        { frame.gas with refundCounter := frame.gas.refundCounter + delta } }

def runFrameGasSteps (frame : FrameGasState) : List FrameGasStep → Option FrameGasState
  | [] => some frame
  | step :: steps =>
      match frameGasStep step frame with
      | none => none
      | some next => runFrameGasSteps next steps

def FrameGasReachable (start : FrameGasState) (steps : List FrameGasStep)
    (child : FrameGasState) : Prop :=
  runFrameGasSteps start steps = some child

instance frameGasReachableDecidable (start : FrameGasState) (steps : List FrameGasStep)
    (child : FrameGasState) : Decidable (FrameGasReachable start steps child) := by
  unfold FrameGasReachable
  infer_instance

theorem frameGasStep_preserves_baseline {step : FrameGasStep} {frame next : FrameGasState}
    (h : frameGasStep step frame = some next) :
    next.stateGasBaseline = frame.stateGasBaseline ∧
      next.stateUsedBaseline = frame.stateUsedBaseline ∧
      next.refundCounterBaseline = frame.refundCounterBaseline := by
  cases step with
  | execution amount =>
      simp [frameGasStep] at h
      split at h
      · cases h
        exact ⟨rfl, rfl, rfl⟩
      · simp at h
  | state amount =>
      simp [frameGasStep] at h
      split at h
      · cases h
        exact ⟨rfl, rfl, rfl⟩
      · simp at h
  | refill amount =>
      simp [frameGasStep] at h
      cases h with
      | intro hAffordable hNext =>
          cases hNext
          exact ⟨rfl, rfl, rfl⟩
  | refund delta =>
      simp [frameGasStep] at h
      cases h
      exact ⟨rfl, rfl, rfl⟩

theorem frameGasStep_preserves_invariant {step : FrameGasStep}
    {frame next : FrameGasState} (hFrame : FrameInvariant frame)
    (h : frameGasStep step frame = some next) :
    FrameInvariant next := by
  cases step with
  | execution amount =>
      by_cases hAmount : amount <= frame.gas.gasLeft
      · simp [frameGasStep, chargeExecution, hAmount] at h
        cases h
        simpa [FrameInvariant] using hFrame
      · simp [frameGasStep, chargeExecution, hAmount] at h
  | state amount =>
      by_cases hAffordable : stateChargeFromGasLeft amount frame.gas <= frame.gas.gasLeft
      · simp [frameGasStep, chargeState, hAffordable] at h
        cases h
        exact chargedState_preserves_invariant amount frame hFrame
      · simp [frameGasStep, chargeState, hAffordable] at h
  | refill amount =>
      by_cases hAffordable : amount <= frame.gas.stateUsed
      · simp [frameGasStep, hAffordable] at h
        cases h
        exact refillState_preserves_invariant amount frame hFrame
      · simp [frameGasStep, hAffordable] at h
  | refund delta =>
      simp [frameGasStep] at h
      cases h
      simpa [FrameInvariant] using hFrame

theorem frameGasStep_refund_is_signed (delta : Int) (frame : FrameGasState) :
    frameGasStep (.refund delta) frame =
      some { frame with gas :=
        { frame.gas with refundCounter := frame.gas.refundCounter + delta } } := by
  rfl

theorem frameGasSteps_preserve_invariant (start : FrameGasState)
    (steps : List FrameGasStep) (child : FrameGasState)
    (hStart : FrameInvariant start) (h : FrameGasReachable start steps child) :
    FrameInvariant child := by
  induction steps generalizing start child with
  | nil =>
      simp [FrameGasReachable, runFrameGasSteps] at h
      subst child
      exact hStart
  | cons step steps ih =>
      simp only [FrameGasReachable, runFrameGasSteps] at h
      cases hStep : frameGasStep step start with
      | none => simp [hStep] at h
      | some next =>
          simp [hStep] at h
          have hStepInvariant := frameGasStep_preserves_invariant hStart hStep
          exact ih next child hStepInvariant h

theorem frameGasSteps_preserve_baseline (start : FrameGasState)
    (steps : List FrameGasStep) (child : FrameGasState)
    (h : FrameGasReachable start steps child) :
    child.stateGasBaseline = start.stateGasBaseline ∧
      child.stateUsedBaseline = start.stateUsedBaseline ∧
      child.refundCounterBaseline = start.refundCounterBaseline := by
  induction steps generalizing start child with
  | nil =>
      simp [FrameGasReachable, runFrameGasSteps] at h
      subst child
      exact ⟨rfl, rfl, rfl⟩
  | cons step steps ih =>
      simp only [FrameGasReachable, runFrameGasSteps] at h
      cases hStep : frameGasStep step start with
      | none => simp [hStep] at h
      | some next =>
          simp [hStep] at h
          have hStepBaseline := frameGasStep_preserves_baseline hStep
          have hTail := ih next child h
          exact ⟨hTail.1.trans hStepBaseline.1, hTail.2.1.trans hStepBaseline.2.1,
            hTail.2.2.trans hStepBaseline.2.2⟩

inductive PrepStatus where
  | entered
  | stackUnderflow
  | staticViolation
  | outOfGas
  | depthOrBalanceFailure
  | nonceFailure
  | collision
  | oracleFailure
  deriving DecidableEq, Repr

structure CallOperands where
  gasLimit : UInt256
  codeSource : UInt256
  value : UInt256
  dataOffset : UInt256
  dataLength : UInt256
  outputOffset : UInt256
  outputLength : UInt256
  deriving DecidableEq, Repr

private def pop (stack : Stack) : Option (UInt256 × Stack) := Stack.pop stack

private def popCallTail (stack : Stack) (value : UInt256) (gasLimit codeSource : UInt256) :
    Option (CallOperands × Stack) := do
  let (dataOffset, afterDataOffset) ← pop stack
  let (dataLength, afterDataLength) ← pop afterDataOffset
  let (outputOffset, afterOutputOffset) ← pop afterDataLength
  let (outputLength, tail) ← pop afterOutputOffset
  pure ({ gasLimit, codeSource, value, dataOffset, dataLength, outputOffset, outputLength }, tail)

def popCall (kind : CallKind) (environmentValue : UInt256) (stack : Stack) :
    Option (CallOperands × Stack) := do
  let (gasLimit, afterGas) ← pop stack
  let (codeSource, afterCodeSource) ← pop afterGas
  if kind.hasExplicitValue = true then
    let (value, afterValue) ← pop afterCodeSource
    popCallTail afterValue value gasLimit codeSource
  else
    popCallTail afterCodeSource
      (if kind = .delegatecall then environmentValue else zero) gasLimit codeSource

structure WorldFrame where
  checkpoint : Nat
  current : Nat
  deriving DecidableEq, Repr

def WorldFrame.enter (parent : WorldFrame) : WorldFrame :=
  { checkpoint := parent.current, current := parent.current }

def WorldFrame.mergeSuccess (parent child : WorldFrame) : WorldFrame :=
  { parent with current := child.current }

def WorldFrame.restore (parent child : WorldFrame) : WorldFrame :=
  { parent with current := child.checkpoint }

private def mergeGasWithPausedParent (parent : GasState) (child : FrameGasState) : GasState :=
  repayStateFromGasLeft
    { child.gas with
      gasLeft := parent.gasLeft + child.gas.gasLeft
      stateReservoir := parent.stateReservoir + child.gas.stateReservoir
      stateFromGasLeft := parent.stateFromGasLeft + child.gas.stateFromGasLeft }

/-- A child result accepted by the prepared-frame adapter must retain the frame
baseline, stay within the execution gas admitted at entry, and satisfy the
gas-machine invariant.  Reachability is checked separately with an explicit
`FrameGasStep` trace. -/
def FrameChildWellFormed (entry : FrameEntry) (child : FrameGasState) : Prop :=
  child.stateGasBaseline = entry.child.stateGasBaseline ∧
    child.stateUsedBaseline = entry.child.stateUsedBaseline ∧
    child.refundCounterBaseline = entry.child.refundCounterBaseline ∧
    child.gas.gasLeft <= entry.child.gas.gasLeft ∧
    child.gas.stateFromGasLeft <= child.gas.stateUsed ∧
    FrameInvariant child

instance frameChildWellFormedDecidable (entry : FrameEntry) (child : FrameGasState) :
    Decidable (FrameChildWellFormed entry child) := by
  unfold FrameChildWellFormed FrameInvariant
  infer_instance

def childWorldMatchesParent (parent : WorldFrame) (childWorld : WorldFrame) : Prop :=
  childWorld.checkpoint = parent.current

instance childWorldMatchesParentDecidable (parent childWorld : WorldFrame) :
    Decidable (childWorldMatchesParent parent childWorld) := by
  unfold childWorldMatchesParent
  infer_instance

structure CallWorldOracle where
  callerBalance : Nat
  executingAccount : AccountStatus
  target : AccountStatus
  targetDerived : Bool
  codeLoaded : Bool
  delegatedTargetAccess : Option AccessStatus
  deriving DecidableEq, Repr

structure CallRequest where
  kind : CallKind
  stack : Stack
  environmentValue : UInt256
  staticContext : Bool
  callDepth : Nat
  /-- Input-memory expansion charge, after the ordinary CALL base charge. -/
  inputMemoryExecutionCharge : Nat
  /-- Output-memory expansion charge, after input-memory expansion. -/
  outputMemoryExecutionCharge : Nat
  gas : GasState
  access : AccessStatus
  world : CallWorldOracle
  deriving Repr

structure CallPreparation where
  status : PrepStatus
  gas : GasState
  operands : Option CallOperands
  remainingStack : Option Stack
  entry : Option FrameEntry
  inputMemoryExpanded : Bool
  outputMemoryExpanded : Bool
  targetAccessed : Bool
  targetDerived : Bool
  accessWarmed : Bool
  delegatedAccessed : Bool
  executionGasCleared : Bool
  newAccountStateCharged : Bool
  newAccountStateCharge : Nat
  deriving DecidableEq, Repr

/-- Amsterdam's EIP-2929 CALL base is zero; access is charged separately. -/
def callBaseExecutionCharge : Nat := 0

private def callAccountEffect (schedule : AccountGasSchedule) (request : CallRequest)
    (operands : CallOperands) : AccountGasEffect :=
  let value := operands.value.val
  match request.kind with
  | .call =>
      priceCall schedule
        { codeAccess := request.access
          delegatedTargetAccess := request.world.delegatedTargetAccess
          stateTarget := request.world.target
          value }
  | .callcode =>
      priceCallCode schedule
        { codeAccess := request.access
          delegatedTargetAccess := request.world.delegatedTargetAccess
          executingAccount := request.world.executingAccount
          value }
  | .delegatecall =>
      priceDelegateCall schedule
        { codeAccess := request.access
          delegatedTargetAccess := request.world.delegatedTargetAccess
          stateTarget := request.world.target
          value }
  | .staticcall =>
      priceStaticCall schedule
        { codeAccess := request.access
          delegatedTargetAccess := request.world.delegatedTargetAccess
          stateTarget := request.world.target
          value := 0 }

private def callValue (kind : CallKind) (operands : CallOperands) : Nat :=
  if kind = .delegatecall then 0 else operands.value.val

private def callHasValue (kind : CallKind) (operands : CallOperands) : Bool :=
  CallKind.hasTransfer kind (callValue kind operands)

private def callFailure (_request : CallRequest) (status : PrepStatus)
    (gas : GasState) (operands : Option CallOperands := none)
    (tail : Option Stack := none) (accessed derived charged : Bool := false)
    (inputExpanded outputExpanded warmed delegated executionCleared : Bool := false) : CallPreparation :=
  { status, gas, operands, remainingStack := tail, entry := none
    inputMemoryExpanded := inputExpanded, outputMemoryExpanded := outputExpanded
    targetAccessed := accessed, targetDerived := derived, accessWarmed := warmed
    delegatedAccessed := delegated, executionGasCleared := executionCleared
    newAccountStateCharged := charged, newAccountStateCharge := 0 }

private def exhaustExecutionGas (state : GasState) : GasState :=
  { state with gasLeft := 0 }

private def chargeExecutionStep (amount : Nat) (state : GasState) : Option GasState :=
  if amount <= state.gasLeft then
    some { state with gasLeft := state.gasLeft - amount }
  else
    none

def prepareCall (schedule : AccountGasSchedule) (request : CallRequest) : CallPreparation :=
  match popCall request.kind request.environmentValue request.stack with
  | none => callFailure request .stackUnderflow request.gas
  | some (operands, tail) =>
      let hasValue := callHasValue request.kind operands
      if request.staticContext = true ∧ hasValue = true ∧ request.kind ≠ .callcode then
        callFailure request .staticViolation request.gas (some operands) (some tail)
      else
        let effect := callAccountEffect schedule request operands
        let valueExecutionCharge :=
          if hasValue then effect.callStipendCharge + effect.accountWriteCharge else 0
        match chargeExecutionStep valueExecutionCharge request.gas with
        | none =>
            callFailure request .outOfGas (exhaustExecutionGas request.gas)
              (some operands) (some tail) (executionCleared := true)
        | some afterValue =>
            match chargeExecutionStep callBaseExecutionCharge afterValue with
            | none =>
                callFailure request .outOfGas (exhaustExecutionGas afterValue)
                  (some operands) (some tail) (executionCleared := true)
            | some afterBase =>
                match chargeExecutionStep request.inputMemoryExecutionCharge afterBase with
                | none =>
                    callFailure request .outOfGas (exhaustExecutionGas afterBase)
                      (some operands) (some tail) (executionCleared := true)
                | some afterInputMemory =>
                    match chargeExecutionStep request.outputMemoryExecutionCharge afterInputMemory with
                    | none =>
                        callFailure request .outOfGas (exhaustExecutionGas afterInputMemory)
                          (some operands) (some tail)
                          (inputExpanded := request.inputMemoryExecutionCharge ≠ 0)
                          (executionCleared := true)
                    | some afterMemory =>
                        match chargeExecutionStep effect.accessCharge afterMemory with
                        | none =>
                            callFailure request .outOfGas (exhaustExecutionGas afterMemory)
                              (some operands) (some tail)
                              (inputExpanded := request.inputMemoryExecutionCharge ≠ 0)
                              (outputExpanded := request.outputMemoryExecutionCharge ≠ 0)
                              (accessed := true) (warmed := true) (executionCleared := true)
                        | some afterAccess =>
                            if request.world.targetDerived = false || request.world.codeLoaded = false then
                              callFailure request .oracleFailure afterAccess (some operands) (some tail)
                                (accessed := true) (derived := request.world.targetDerived)
                                (inputExpanded := request.inputMemoryExecutionCharge ≠ 0)
                                (outputExpanded := request.outputMemoryExecutionCharge ≠ 0)
                                (warmed := true)
                            else
                              let delegated := request.world.delegatedTargetAccess.isSome
                              match chargeExecutionStep effect.delegatedTargetAccessCharge afterAccess with
                              | none =>
                                  callFailure request .outOfGas (exhaustExecutionGas afterAccess)
                                    (some operands) (some tail)
                                    (inputExpanded := request.inputMemoryExecutionCharge ≠ 0)
                                    (outputExpanded := request.outputMemoryExecutionCharge ≠ 0)
                                    (accessed := true) (derived := true) (warmed := true)
                                    (delegated := delegated) (executionCleared := true)
                              | some afterDelegated =>
                                  match chargeState effect.stateCharge afterDelegated with
                                  | .error _ =>
                                      callFailure request .outOfGas (exhaustExecutionGas afterDelegated)
                                        (some operands) (some tail)
                                        (inputExpanded := request.inputMemoryExecutionCharge ≠ 0)
                                        (outputExpanded := request.outputMemoryExecutionCharge ≠ 0)
                                        (accessed := true) (derived := true) (warmed := true)
                                        (delegated := delegated) (charged := false)
                                        (executionCleared := true)
                                  | .ok afterState =>
                                      let requested := operands.gasLimit.val
                                      let entry := enterFrame requested afterState
                                      let childWithStipend :=
                                        { entry.child with gas :=
                                            { entry.child.gas with
                                              gasLeft := entry.child.gas.gasLeft + effect.callStipendCharge } }
                                      let entryWithStipend := { entry with child := childWithStipend }
                                      let blocked : Prop :=
                                        request.callDepth >= maxCallDepth ∨
                                          (hasValue = true ∧ request.world.callerBalance < callValue request.kind operands)
                                       if blocked then
                                         { status := .depthOrBalanceFailure
                                           gas := refillState effect.stateCharge
                                             (mergeRevert entryWithStipend childWithStipend)
                                           operands := some operands
                                           remainingStack := some tail
                                           entry := none
                                           inputMemoryExpanded := request.inputMemoryExecutionCharge ≠ 0
                                           outputMemoryExpanded := request.outputMemoryExecutionCharge ≠ 0
                                           targetAccessed := true
                                           targetDerived := true
                                           accessWarmed := true
                                           delegatedAccessed := delegated
                                           executionGasCleared := false
                                           newAccountStateCharged := effect.stateCharge ≠ 0
                                           newAccountStateCharge := effect.stateCharge }
                                       else
                                         { status := .entered
                                           gas := entryWithStipend.pausedParent
                                           operands := some operands
                                           remainingStack := some tail
                                           entry := some entryWithStipend
                                           inputMemoryExpanded := request.inputMemoryExecutionCharge ≠ 0
                                           outputMemoryExpanded := request.outputMemoryExecutionCharge ≠ 0
                                           targetAccessed := true
                                           targetDerived := true
                                           accessWarmed := true
                                           delegatedAccessed := delegated
                                           executionGasCleared := false
                                           newAccountStateCharged := effect.stateCharge ≠ 0
                                           newAccountStateCharge := effect.stateCharge }

structure CreateOperands where
  value : UInt256
  memoryOffset : UInt256
  initCodeLength : UInt256
  salt : UInt256
  deriving DecidableEq, Repr

def popCreate (kind : CreateKind) (stack : Stack) : Option (CreateOperands × Stack) := do
  let (value, afterValue) ← pop stack
  let (memoryOffset, afterOffset) ← pop afterValue
  let (initCodeLength, afterLength) ← pop afterOffset
  if kind = .create2 then
    let (salt, tail) ← pop afterLength
    pure ({ value, memoryOffset, initCodeLength, salt }, tail)
  else
    pure ({ value, memoryOffset, initCodeLength, salt := zero }, afterLength)

structure CreateDestinationOracle where
  status : AccountStatus
  collision : CollisionKind
  physicalLeafExists : Bool
  logicalAccountExists : Bool
  destinationDerived : Bool
  warmed : Bool
  deriving DecidableEq, Repr

/-- Consistency obligations on facts collected by the CREATE destination
adapter.  They are checked before any CREATE admission or gas charge. -/
def CreateDestinationOracleConsistent (destination : CreateDestinationOracle) : Prop :=
  (destination.status = .existent ↔ destination.logicalAccountExists = true) ∧
    (destination.logicalAccountExists = true → destination.physicalLeafExists = true) ∧
    (destination.collision.isCollision = true → destination.physicalLeafExists = true) ∧
    ((destination.collision = .nonEmptyCode ∨
        destination.collision = .nonZeroNonce) →
      destination.logicalAccountExists = true)

instance createDestinationOracleConsistentDecidable (destination : CreateDestinationOracle) :
    Decidable (CreateDestinationOracleConsistent destination) := by
  unfold CreateDestinationOracleConsistent
  infer_instance

structure CreateRequest where
  kind : CreateKind
  stack : Stack
  staticContext : Bool
  callDepth : Nat
  balance : Nat
  nonce : Nat
  maxInitCodeSize : Nat
  /-- Whether the production 32-byte ceiling computation overflowed. -/
  initCodeWordCountOverflow : Bool
  memoryExecutionCharge : Nat
  initCodeReadable : Bool
  destination : CreateDestinationOracle
  gas : GasState
  schedule : AccountGasSchedule
  deriving Repr

structure CreatePreparation where
  status : PrepStatus
  gas : GasState
  operands : Option CreateOperands
  remainingStack : Option Stack
  entry : Option FrameEntry
  entryExecutionCharged : Bool
  memoryExpanded : Bool
  executionGasCleared : Bool
  destinationAccessed : Bool
  destinationPhysicalLeafExists : Bool
  destinationLogicalAccountExists : Bool
  destinationStorageCleared : Bool
  /-- Gas forwarded to a CREATE destination that collided before child entry. -/
  collisionForwardedExecutionGas : Nat
  /-- The collision path burns the forwarded child execution gas. -/
  collisionExecutionGasBurned : Bool
  stateCharged : Bool
  stateCharge : Nat
  collision : CollisionKind
  deriving DecidableEq, Repr

private def createEntryEffect (request : CreateRequest) (operands : CreateOperands) :
    CreateEntryGasEffect :=
  let situation : CreateSituation :=
    { destination := request.destination.status
      collision := false
      preChecksPassed := true
      initCodeLength := operands.initCodeLength.val
      runtimeCodeLength := 0
      childSucceeded := false
      runtimeCodeValid := false
      depositSucceeded := false }
  match request.kind with
  | .create => (priceCreate request.schedule situation).entry
  | .create2 => (priceCreate2 request.schedule situation).entry

private def createFailure (_request : CreateRequest) (status : PrepStatus) (gas : GasState)
    (operands : Option CreateOperands := none) (tail : Option Stack := none)
    (accessed charged : Bool := false) (collision : CollisionKind := .none)
    (entryCharged memoryExpanded executionCleared physicalLeafExists logicalAccountExists storageCleared : Bool := false)
    (stateCharge collisionForwardedExecutionGas : Nat := 0)
    (collisionExecutionGasBurned : Bool := false) :
  CreatePreparation :=
  { status, gas, operands, remainingStack := tail, entry := none
    entryExecutionCharged := entryCharged, memoryExpanded, executionGasCleared := executionCleared
    destinationAccessed := accessed, destinationPhysicalLeafExists := physicalLeafExists
    destinationLogicalAccountExists := logicalAccountExists
    destinationStorageCleared := storageCleared
    collisionForwardedExecutionGas, collisionExecutionGasBurned
    stateCharged := charged, stateCharge, collision }

def prepareCreate (request : CreateRequest) : CreatePreparation :=
  if request.staticContext then
    createFailure request .staticViolation request.gas
  else
    match popCreate request.kind request.stack with
    | none => createFailure request .stackUnderflow request.gas
    | some (operands, tail) =>
        if _h : ¬ CreateDestinationOracleConsistent request.destination then
          createFailure request .oracleFailure request.gas (some operands) (some tail)
        else if operands.initCodeLength.val > request.maxInitCodeSize then
          createFailure request .outOfGas (exhaustExecutionGas request.gas)
            (some operands) (some tail) (executionCleared := true)
        else if request.initCodeWordCountOverflow then
          createFailure request .outOfGas (exhaustExecutionGas request.gas)
            (some operands) (some tail) (executionCleared := true)
        else
          let entryEffect := createEntryEffect request operands
          match chargeExecutionStep entryEffect.executionCharge request.gas with
          | none =>
              createFailure request .outOfGas (exhaustExecutionGas request.gas)
                (some operands) (some tail) (executionCleared := true)
          | some afterEntryExecution =>
              match chargeExecutionStep request.memoryExecutionCharge afterEntryExecution with
              | none =>
                  createFailure request .outOfGas (exhaustExecutionGas afterEntryExecution)
                    (some operands) (some tail) (entryCharged := true) (executionCleared := true)
              | some afterMemory =>
                  let memoryExpanded := request.memoryExecutionCharge ≠ 0
                  if request.callDepth >= maxCallDepth then
                    createFailure request .depthOrBalanceFailure afterMemory (some operands) (some tail)
                      (entryCharged := true) (memoryExpanded := memoryExpanded)
                  else if request.initCodeReadable = false then
                    createFailure request .outOfGas (exhaustExecutionGas afterMemory)
                      (some operands) (some tail)
                      (entryCharged := true) (memoryExpanded := memoryExpanded)
                      (executionCleared := true)
                  else if operands.value.val > request.balance then
                    createFailure request .depthOrBalanceFailure afterMemory (some operands) (some tail)
                      (entryCharged := true) (memoryExpanded := memoryExpanded)
                  else if request.nonce >= (2 ^ 64) - 1 then
                    createFailure request .nonceFailure afterMemory (some operands) (some tail)
                      (entryCharged := true) (memoryExpanded := memoryExpanded)
                  else if request.destination.destinationDerived = false ∨
                      request.destination.warmed = false then
                            createFailure request .oracleFailure afterMemory (some operands) (some tail)
                      (accessed := request.destination.destinationDerived)
                      (charged := false) (collision := request.destination.collision)
                      (entryCharged := true) (memoryExpanded := memoryExpanded)
                      (physicalLeafExists :=
                        if request.destination.destinationDerived then
                          request.destination.physicalLeafExists
                        else false)
                      (logicalAccountExists :=
                        if request.destination.destinationDerived then
                          request.destination.logicalAccountExists
                        else false)
                  else
                    let stateCharge :=
                      if request.destination.status = .dead then request.schedule.base.newAccountGas else 0
                    match chargeState stateCharge afterMemory with
                    | .error _ =>
                        createFailure request .outOfGas (exhaustExecutionGas afterMemory)
                          (some operands) (some tail)
                          (accessed := true) (charged := false)
                          (collision := request.destination.collision)
                          (entryCharged := true) (memoryExpanded := memoryExpanded)
                          (physicalLeafExists := request.destination.physicalLeafExists)
                          (logicalAccountExists := request.destination.logicalAccountExists)
                          (executionCleared := true)
                    | .ok afterState =>
                        let entry := enterFrame afterState.gasLeft afterState
                        if request.destination.collision.isCollision = true then
                          -- CREATE burns the reserved child execution gas on collision, then
                          -- returns the state charge (if one was charged).
                          let collisionEntry := enterFrame afterState.gasLeft afterState
                          createFailure request .collision
                            (refillState stateCharge (mergeException collisionEntry collisionEntry.child))
                            (some operands) (some tail) (accessed := true)
                            (charged := stateCharge ≠ 0)
                            (collision := request.destination.collision)
                            (entryCharged := true) (memoryExpanded := memoryExpanded)
                            (physicalLeafExists := request.destination.physicalLeafExists)
                            (logicalAccountExists := request.destination.logicalAccountExists)
                            (stateCharge := stateCharge)
                            (collisionForwardedExecutionGas := collisionEntry.child.gas.gasLeft)
                            (collisionExecutionGasBurned := true)
                           else
                             { status := .entered
                               gas := entry.pausedParent
                               operands := some operands
                               remainingStack := some tail
                               entry := some entry
                               entryExecutionCharged := true
                               memoryExpanded
                               executionGasCleared := false
                               destinationAccessed := true
                               destinationPhysicalLeafExists := request.destination.physicalLeafExists
                               destinationLogicalAccountExists := request.destination.logicalAccountExists
                               destinationStorageCleared := false
                               collisionForwardedExecutionGas := 0
                               collisionExecutionGasBurned := false
                               stateCharged := stateCharge ≠ 0
                               stateCharge
                               collision := .none }

structure FrameOutcome where
  exit : ChildExit
  gas : GasState
  world : WorldFrame
  childGas : GasState
  childWorld : WorldFrame
  deriving DecidableEq, Repr

structure CallCompletion where
  outcome : FrameOutcome
  newAccountStateRefilled : Bool
  deriving DecidableEq, Repr

def finishFrame (parent : WorldFrame) (entry : FrameEntry) (childWorld : WorldFrame)
    (child : FrameGasState) (exit : ChildExit) : FrameOutcome :=
  match exit with
  | .success =>
      { exit, gas := mergeSuccess entry child
        world := WorldFrame.mergeSuccess parent childWorld
        childGas := child.gas, childWorld }
  | .revert =>
      { exit, gas := mergeRevert entry child
        world := WorldFrame.restore parent childWorld
        childGas := child.gas, childWorld }
  | .exceptional =>
      { exit, gas := mergeException entry child
        world := WorldFrame.restore parent childWorld
        childGas := child.gas, childWorld }

/-- Complete a CALL-family child after the handler has charged a new-account state
charge.  The charge is part of the frame baseline while the child runs, so a
REVERT or exceptional halt must explicitly refund it after the VM rollback.
The child execution-gas rule remains the one supplied by `finishFrame`:
REVERT returns execution gas while an exceptional halt burns it. -/
def completeCall (parent : WorldFrame) (entry : FrameEntry) (childWorld : WorldFrame)
    (child : FrameGasState) (stateCharge : Nat) (exit : ChildExit) : CallCompletion :=
  let finished := finishFrame parent entry childWorld child exit
  match exit with
  | .success =>
      { outcome := finished, newAccountStateRefilled := false }
  | .revert | .exceptional =>
      { outcome := { finished with gas := refillState stateCharge finished.gas }
        newAccountStateRefilled := stateCharge ≠ 0 }

/-- Completion checks every preparation field against the supplied request before
accepting a reachable child; preparation metadata cannot change the refund. -/
def completePreparedCall (schedule : AccountGasSchedule) (request : CallRequest)
    (parent : WorldFrame) (prepared : CallPreparation)
    (childWorld : WorldFrame) (child : FrameGasState) (steps : List FrameGasStep)
    (exit : ChildExit) :
    Option CallCompletion :=
  match prepared.entry with
  | none => none
  | some entry =>
      if prepared = prepareCall schedule request ∧
          childWorldMatchesParent parent childWorld ∧
          FrameChildWellFormed entry child ∧
          FrameGasReachable entry.child steps child then
        some (completeCall parent entry childWorld child prepared.newAccountStateCharge exit)
      else
        none

structure CreateCompletion where
  /-- Present only on the provenance-checked prepared-frame completion path. -/
  destinationPhysicalLeafExists : Option Bool := none
  destinationLogicalAccountExists : Option Bool := none
  outcome : FrameOutcome
  stateChargeRefilled : Bool
  codeCommitted : Bool
  depositExecutionCharged : Nat
  depositStateCharged : Nat
  deriving DecidableEq, Repr

private def depositExecutionCharge (schedule : AccountGasSchedule) (runtimeLength : Nat) : Nat :=
  runtimeCodeWords runtimeLength * schedule.codeDepositExecutionWordCost

private def depositStateCharge (schedule : AccountGasSchedule) (runtimeLength : Nat) : Nat :=
  runtimeLength * schedule.base.cpsb

def completeCreate (schedule : AccountGasSchedule) (parent : WorldFrame)
    (entry : FrameEntry) (childWorld : WorldFrame) (child : FrameGasState)
    (createStateCharge runtimeLength : Nat) (code : RuntimeCodeKind) : CreateCompletion :=
  match code with
  | .empty =>
      { outcome := finishFrame parent entry childWorld child .success
        stateChargeRefilled := false
        codeCommitted := true
        depositExecutionCharged := 0
        depositStateCharged := 0 }
  | .childRevert =>
      let failed := finishFrame parent entry childWorld child .revert
      { outcome := { failed with gas := refillState createStateCharge failed.gas }
        stateChargeRefilled := createStateCharge ≠ 0
        codeCommitted := false
        depositExecutionCharged := 0
        depositStateCharged := 0 }
  | .invalid | .depositOutOfGas =>
      let failed := finishFrame parent entry childWorld child .exceptional
      { outcome := { failed with gas := refillState createStateCharge failed.gas }
        stateChargeRefilled := createStateCharge ≠ 0
        codeCommitted := false
        depositExecutionCharged := 0
        depositStateCharged := 0 }
  | .duplicate | .fresh =>
      let executionCharge := depositExecutionCharge schedule runtimeLength
      let stateCharge := depositStateCharge schedule runtimeLength
      match chargeExecution executionCharge child.gas with
      | .error _ =>
          let failed := finishFrame parent entry childWorld child .exceptional
          { outcome := { failed with gas := refillState createStateCharge failed.gas }
            stateChargeRefilled := createStateCharge ≠ 0
            codeCommitted := false
            depositExecutionCharged := 0
            depositStateCharged := 0 }
      | .ok afterExecution =>
          match chargeState stateCharge afterExecution with
          | .error _ =>
              let chargedChild := { child with gas := afterExecution }
              let failed := finishFrame parent entry childWorld chargedChild .exceptional
              { outcome := { failed with gas := refillState createStateCharge failed.gas }
                stateChargeRefilled := createStateCharge ≠ 0
                codeCommitted := false
                depositExecutionCharged := executionCharge
                depositStateCharged := 0 }
          | .ok afterState =>
              let committedChild := { child with gas := afterState }
              { outcome := finishFrame parent entry childWorld committedChild .success
                stateChargeRefilled := false
                codeCommitted := true
                depositExecutionCharged := executionCharge
                depositStateCharged := stateCharge }

/-- Complete only a recomputed CREATE preparation and retain both destination
facts needed by the production frame adapter. -/
def completePreparedCreate (request : CreateRequest) (parent : WorldFrame)
    (prepared : CreatePreparation) (childWorld : WorldFrame) (child : FrameGasState)
    (steps : List FrameGasStep) (runtimeLength : Nat) (code : RuntimeCodeKind) :
    Option CreateCompletion :=
  match prepared.entry with
  | none => none
  | some entry =>
      if prepared = prepareCreate request ∧
          childWorldMatchesParent parent childWorld ∧
          FrameChildWellFormed entry child ∧
          FrameGasReachable entry.child steps child then
        some { completeCreate request.schedule parent entry childWorld child
          prepared.stateCharge runtimeLength code with
            destinationPhysicalLeafExists := some prepared.destinationPhysicalLeafExists
            destinationLogicalAccountExists := some prepared.destinationLogicalAccountExists }
      else
        none

theorem popCall_underflow_is_fail_closed (kind : CallKind) (environmentValue : UInt256) :
    popCall kind environmentValue Stack.empty = none := by
  cases kind <;> simp [popCall, pop, Stack.pop, Stack.empty]

theorem popCreate_underflow_is_fail_closed (kind : CreateKind) :
    popCreate kind Stack.empty = none := by
  cases kind <;> simp [popCreate, pop, Stack.pop, Stack.empty]

theorem call_kind_pricing_distinguishes_writes (schedule : AccountGasSchedule)
    (access : AccessStatus) (target : AccountStatus) (value : Nat) :
    let call := priceCall schedule
      { codeAccess := access, delegatedTargetAccess := none, stateTarget := target, value }
    let callcode := priceCallCode schedule
      { codeAccess := access, delegatedTargetAccess := none, executingAccount := .existent, value }
    let delegate := priceDelegateCall schedule
      { codeAccess := access, delegatedTargetAccess := none, stateTarget := target, value }
    let static := priceStaticCall schedule
      { codeAccess := access, delegatedTargetAccess := none, stateTarget := target, value }
    delegate.accountWriteCharge = 0 ∧ static.accountWriteCharge = 0 ∧
      callcode.accountWriteCharge = call.accountWriteCharge := by
  simp [priceCall, priceCallCode, priceDelegateCall, priceStaticCall, accountEffect,
    callAccountWrite]

theorem call_eip150_uses_execution_only (requested : Nat) (state : GasState) :
    forwardGas requested { state with stateReservoir := state.stateReservoir + 100 } =
      forwardGas requested state := by
  exact forwardGas_uses_only_gasLeft rfl requested

theorem call_failure_constructor_has_no_child
    (request : CallRequest) (status : PrepStatus) (gas : GasState) :
    (callFailure request status gas).entry = none ∧
      (callFailure request status gas).targetAccessed = false ∧
      (callFailure request status gas).targetDerived = false := by
  constructor
  · rfl
  · constructor <;> rfl

theorem prepareCall_depth_or_balance_failure_is_post_access
    (schedule : AccountGasSchedule) (request : CallRequest)
    (h : (prepareCall schedule request).status = .depthOrBalanceFailure) :
    let result := prepareCall schedule request
    result.entry = none ∧
      result.targetAccessed = true ∧ result.targetDerived = true ∧
      result.accessWarmed = true ∧ result.executionGasCleared = false := by
  dsimp
  unfold prepareCall at h ⊢
  cases hPop : popCall request.kind request.environmentValue request.stack with
  | none => simp [hPop] at h; cases h
  | some pair =>
      cases pair with
      | mk operands tail =>
          simp [hPop] at h ⊢
          by_cases hStatic :
              request.staticContext = true ∧ callHasValue request.kind operands = true ∧
                request.kind ≠ .callcode
          · simp [hStatic] at h; cases h
          · simp [hStatic] at h ⊢
            cases hValue : chargeExecutionStep
                (if callHasValue request.kind operands = true then
                  (callAccountEffect schedule request operands).callStipendCharge +
                    (callAccountEffect schedule request operands).accountWriteCharge
                else 0) request.gas with
            | none => simp [hValue] at h; cases h
            | some afterValue =>
                simp [hValue] at h ⊢
                cases hBase : chargeExecutionStep callBaseExecutionCharge afterValue with
                | none => simp [hBase] at h; cases h
                | some afterBase =>
                    simp [hBase] at h ⊢
                    cases hInput : chargeExecutionStep request.inputMemoryExecutionCharge afterBase with
                    | none => simp [hInput] at h; cases h
                    | some afterInputMemory =>
                        simp [hInput] at h ⊢
                        cases hOutput : chargeExecutionStep
                            request.outputMemoryExecutionCharge afterInputMemory with
                        | none => simp [hOutput] at h; cases h
                        | some afterMemory =>
                            simp [hOutput] at h ⊢
                            cases hAccess : chargeExecutionStep
                                (callAccountEffect schedule request operands).accessCharge afterMemory with
                            | none => simp [hAccess] at h; cases h
                            | some afterAccess =>
                                simp [hAccess] at h ⊢
                                by_cases hOracle :
                                    request.world.targetDerived = false ∨
                                      request.world.codeLoaded = false
                                · simp [hOracle] at h; cases h
                                · simp [hOracle] at h ⊢
                                  cases hDelegated : chargeExecutionStep
                                      (callAccountEffect schedule request operands).delegatedTargetAccessCharge
                                      afterAccess with
                                  | none => simp [hDelegated] at h; cases h
                                  | some afterDelegated =>
                                      simp [hDelegated] at h ⊢
                                      cases hState : chargeState
                                          (callAccountEffect schedule request operands).stateCharge afterDelegated with
                                      | error err => simp [hState] at h; cases h
                                      | ok afterState =>
                                          simp [hState] at h ⊢
                                          by_cases hBlocked :
                                              request.callDepth >= maxCallDepth ∨
                                                callHasValue request.kind operands = true ∧
                                                  request.world.callerBalance <
                                                    callValue request.kind operands
                                          · simp [hBlocked] at h ⊢
                                          · simp [hBlocked] at h

theorem successful_frame_merge_conserves_execution_and_state
    (parent : WorldFrame) (entry : FrameEntry) (child : FrameGasState) (world : WorldFrame) :
    totalRemaining (mergeSuccess entry child) =
      totalRemaining entry.pausedParent + totalRemaining child.gas ∧
      (finishFrame parent entry world child .success).world.current = world.current := by
  constructor
  · exact mergeSuccess_conserves_total entry child
  · rfl

theorem revert_returns_execution_and_restores_world
    (parent : WorldFrame) (entry : FrameEntry) (child : FrameGasState) (world : WorldFrame) :
    (finishFrame parent entry world child .revert).gas.stateUsed = child.stateUsedBaseline ∧
      (finishFrame parent entry world child .revert).gas.refundCounter =
        child.refundCounterBaseline ∧
      (finishFrame parent entry world child .revert).world.current = world.checkpoint := by
  simp [finishFrame, WorldFrame.restore, mergeRevert_restores_child_baseline]

theorem revert_returns_exact_execution_and_restores_world
    (parent : WorldFrame) (entry : FrameEntry) (child : FrameGasState)
    (world : WorldFrame) :
    let repayment := min (entry.pausedParent.stateReservoir + child.stateGasBaseline)
      entry.pausedParent.stateFromGasLeft
    let result := finishFrame parent entry world child .revert
    result.gas.gasLeft = entry.pausedParent.gasLeft + child.gas.gasLeft +
        child.gas.stateFromGasLeft + repayment ∧
      result.gas.stateReservoir =
        entry.pausedParent.stateReservoir + child.stateGasBaseline - repayment ∧
      result.gas.stateFromGasLeft = entry.pausedParent.stateFromGasLeft - repayment ∧
      result.gas.stateUsed = child.stateUsedBaseline ∧
      result.gas.refundCounter = child.refundCounterBaseline ∧
      result.world.current = world.checkpoint := by
  simp only [finishFrame]
  have hMerge : mergeRevert entry child =
      mergeGasWithPausedParent entry.pausedParent
        { child with gas := restoreRevert child } := by
    rfl
  rw [hMerge]
  simp only [mergeGasWithPausedParent, restoreRevert, repayStateFromGasLeft,
    WorldFrame.restore]
  simp [Nat.add_assoc]

theorem exceptional_halt_burns_child_execution
    (parent : WorldFrame) (entry : FrameEntry) (child : FrameGasState) (world : WorldFrame) :
    (finishFrame parent entry world child .exceptional).world.current = world.checkpoint ∧
      (finishFrame parent entry world child .exceptional).gas.gasLeft =
        entry.pausedParent.gasLeft +
          min (entry.pausedParent.stateReservoir + child.stateGasBaseline)
            entry.pausedParent.stateFromGasLeft ∧
      (finishFrame parent entry world child .exceptional).gas =
        (finishFrame parent entry world
          { child with gas := { child.gas with gasLeft := 0 } } .exceptional).gas ∧
      (finishFrame parent entry world child .exceptional).gas.stateUsed =
        child.stateUsedBaseline ∧
      (finishFrame parent entry world child .exceptional).gas.refundCounter =
        child.refundCounterBaseline := by
  constructor
  · rfl
  constructor
  · simp only [finishFrame]
    have hMerge : mergeException entry child =
        mergeGasWithPausedParent entry.pausedParent
          { child with gas := restoreException child } := by
      rfl
    rw [hMerge]
    simp only [mergeGasWithPausedParent, restoreException, restoreRevert,
      repayStateFromGasLeft]
    simp
  constructor
  · simp only [finishFrame]
    exact mergeException_ignores_child_gasLeft entry child
  · simp only [finishFrame]
    have hMerge : mergeException entry child =
        mergeGasWithPausedParent entry.pausedParent
          { child with gas := restoreException child } := by
      rfl
    rw [hMerge]
    simp only [mergeGasWithPausedParent, restoreException, restoreRevert,
      repayStateFromGasLeft]
    simp

theorem failed_create_refills_state_charge
    (schedule : AccountGasSchedule) (parent : WorldFrame) (entry : FrameEntry)
    (childWorld : WorldFrame) (child : FrameGasState) (charge : Nat) (runtime : Nat) :
    (completeCreate schedule parent entry childWorld child charge runtime .invalid).stateChargeRefilled =
      (charge ≠ 0) := by
  simp [completeCreate]

theorem reverted_create_refills_state_charge
    (schedule : AccountGasSchedule) (parent : WorldFrame) (entry : FrameEntry)
    (childWorld : WorldFrame) (child : FrameGasState) (charge : Nat) (runtime : Nat) :
    (completeCreate schedule parent entry childWorld child charge runtime .childRevert).stateChargeRefilled =
      (charge ≠ 0) := by
  simp [completeCreate]

theorem failed_call_refills_state_charge
    (parent : WorldFrame) (entry : FrameEntry) (childWorld : WorldFrame)
    (child : FrameGasState) (charge : Nat) (exit : ChildExit)
    (hExit : exit = .revert ∨ exit = .exceptional)
    (hCharge : CanRefill charge (finishFrame parent entry childWorld child exit).gas) :
    let finished := finishFrame parent entry childWorld child exit
    let result := completeCall parent entry childWorld child charge exit
    result.newAccountStateRefilled = (charge ≠ 0) ∧
      result.outcome.gas = refillState charge finished.gas ∧
      result.outcome.gas.gasLeft = finished.gas.gasLeft +
        min charge finished.gas.stateFromGasLeft ∧
      result.outcome.gas.stateReservoir = finished.gas.stateReservoir +
        (charge - min charge finished.gas.stateFromGasLeft) ∧
      result.outcome.gas.stateFromGasLeft = finished.gas.stateFromGasLeft -
        min charge finished.gas.stateFromGasLeft ∧
      result.outcome.gas.stateUsed = finished.gas.stateUsed - charge := by
  rcases hExit with rfl | rfl
  · simp only [completeCall]
    have hLifo := refillState_valid_lifo hCharge
    simp only [hLifo.1, hLifo.2.1, hLifo.2.2.1, hLifo.2.2.2]
    simp
  · simp only [completeCall]
    have hLifo := refillState_valid_lifo hCharge
    simp only [hLifo.1, hLifo.2.1, hLifo.2.2.1, hLifo.2.2.2]
    simp

theorem create_child_failure_burns_execution_and_restores_state
    (schedule : AccountGasSchedule) (parent : WorldFrame) (entry : FrameEntry)
    (childWorld : WorldFrame) (child : FrameGasState) (charge _runtime : Nat)
    (hCharge : CanRefill charge (mergeException entry child)) :
    let result := completeCreate schedule parent entry childWorld child charge runtime .invalid
    result.outcome.world.current = childWorld.checkpoint ∧
      result.outcome.gas = refillState charge (mergeException entry child) ∧
      result.outcome.gas =
        refillState charge
          (mergeException entry { child with gas := { child.gas with gasLeft := 0 } }) ∧
      result.outcome.gas.stateUsed = (mergeException entry child).stateUsed - charge := by
  dsimp
  have hBurn := mergeException_ignores_child_gasLeft entry child
  have hLifo := refillState_valid_lifo hCharge
  simp only [completeCreate, finishFrame, WorldFrame.restore]
  constructor
  · trivial
  constructor
  · trivial
  constructor
  · rw [hBurn]
  · exact hLifo.2.2.2

theorem parent_created_state_can_be_refilled_by_child
    (state : GasState) (amount : Nat) (h : CanRefill amount state) :
    (refillState amount state).stateUsed = state.stateUsed - amount := by
  exact (refillState_valid_lifo h).2.2.2

theorem state_refill_is_lifo_at_frame_boundary (state : GasState) (amount : Nat) :
    (refillState amount state).gasLeft = state.gasLeft +
      min (min amount state.stateUsed) state.stateFromGasLeft := by
  rfl

theorem callcode_targets_executing_account
    (schedule : AccountGasSchedule) (access : AccessStatus) (value : Nat) :
    (priceCallCode schedule
      { codeAccess := access, delegatedTargetAccess := none,
        executingAccount := .existent, value }).stateCharge = 0 := by
  rfl

theorem delegatecall_and_staticcall_have_no_write_charge
    (schedule : AccountGasSchedule) (access : AccessStatus) (target : AccountStatus) (value : Nat) :
    (priceDelegateCall schedule
      { codeAccess := access, delegatedTargetAccess := none, stateTarget := target, value }).accountWriteCharge = 0 ∧
    (priceStaticCall schedule
      { codeAccess := access, delegatedTargetAccess := none, stateTarget := target, value }).accountWriteCharge = 0 := by
  constructor <;> rfl

theorem create_failure_constructor_has_no_state_charge
    (request : CreateRequest) (status : PrepStatus) (gas : GasState) :
    (createFailure request status gas).stateCharged = false := by
  rfl

theorem storage_only_is_a_collision :
    CollisionKind.storageOnly.isCollision = true := by
  rfl

theorem empty_code_does_not_charge_deposit
    (schedule : AccountGasSchedule) (parent : WorldFrame) (entry : FrameEntry)
    (childWorld : WorldFrame) (child : FrameGasState) (charge runtime : Nat) :
    let emptyResult := completeCreate schedule parent entry childWorld child charge runtime .empty
    emptyResult.depositExecutionCharged = 0 ∧ emptyResult.depositStateCharged = 0 := by
  simp [completeCreate]

theorem duplicate_code_uses_normal_deposit_path
    (schedule : AccountGasSchedule) (parent : WorldFrame) (entry : FrameEntry)
    (childWorld : WorldFrame) (child : FrameGasState) (charge runtime : Nat) :
    let duplicateResult := completeCreate schedule parent entry childWorld child charge runtime .duplicate
    let freshResult := completeCreate schedule parent entry childWorld child charge runtime .fresh
    duplicateResult.depositExecutionCharged = freshResult.depositExecutionCharged ∧
    duplicateResult.depositStateCharged = freshResult.depositStateCharged ∧
    duplicateResult.codeCommitted = freshResult.codeCommitted := by
  simp [completeCreate]

theorem call_value_debit_contains_stipend (schedule : AccountGasSchedule)
    (access : AccessStatus) (target : AccountStatus) (value : Nat)
    (hValue : value ≠ 0) :
    let effect := priceCall schedule
      { codeAccess := access, delegatedTargetAccess := none, stateTarget := target, value }
    effect.accountWriteCharge + effect.callStipendCharge =
      schedule.base.accountWrite + schedule.callStipend := by
  simp [priceCall, accountEffect, callAccountWrite, AccountPricing.callStipend, hValue]

theorem completion_preserves_enclosing_checkpoint (parent : WorldFrame)
    (entry : FrameEntry) (childWorld : WorldFrame) (child : FrameGasState)
    (exit : ChildExit) :
    (finishFrame parent entry childWorld child exit).world.checkpoint = parent.checkpoint := by
  cases exit <;> rfl

theorem logical_existence_requires_physical_presence (destination : CreateDestinationOracle)
    (h : CreateDestinationOracleConsistent destination)
    (hLogical : destination.logicalAccountExists = true) :
    destination.physicalLeafExists = true := by
  exact h.2.1 hLogical

theorem completePreparedCall_rejects_changed_preparation (schedule : AccountGasSchedule)
    (request : CallRequest) (parent : WorldFrame) (prepared : CallPreparation)
    (world : WorldFrame) (child : FrameGasState) (steps : List FrameGasStep) (exit : ChildExit)
    (h : prepared ≠ prepareCall schedule request) :
    completePreparedCall schedule request parent prepared world child steps exit = none := by
  cases hEntry : prepared.entry <;> simp [completePreparedCall, hEntry, h]

theorem completePreparedCreate_rejects_changed_preparation (request : CreateRequest)
    (parent : WorldFrame) (prepared : CreatePreparation) (world : WorldFrame)
    (child : FrameGasState) (steps : List FrameGasStep) (runtimeLength : Nat)
    (code : RuntimeCodeKind) (h : prepared ≠ prepareCreate request) :
    completePreparedCreate request parent prepared world child steps runtimeLength code = none := by
  cases hEntry : prepared.entry <;> simp [completePreparedCreate, hEntry, h]

theorem prepared_create_completion_carries_both_facts (request : CreateRequest)
    (parent : WorldFrame) (prepared : CreatePreparation) (world : WorldFrame)
    (child : FrameGasState) (steps : List FrameGasStep) (runtimeLength : Nat)
    (code : RuntimeCodeKind) (result : CreateCompletion)
    (h : completePreparedCreate request parent prepared world child steps runtimeLength code =
      some result) :
    result.destinationPhysicalLeafExists = some prepared.destinationPhysicalLeafExists ∧
      result.destinationLogicalAccountExists = some prepared.destinationLogicalAccountExists := by
  unfold completePreparedCreate at h
  split at h
  · simp at h
  · split at h
    · cases h
      exact ⟨rfl, rfl⟩
    · simp at h

end CallCreateFrame
end Evm
end Eip803x
