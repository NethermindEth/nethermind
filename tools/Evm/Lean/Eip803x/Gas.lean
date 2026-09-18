-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.Schedule
import Lean.Elab.Tactic.Omega

namespace Eip803x

inductive OutOfGas where
  | outOfGas
  deriving DecidableEq, Repr

/-- The common EIP-8037 gas machine. Natural numbers rule out arithmetic wraparound. -/
structure GasState where
  gasLeft : Nat
  stateReservoir : Nat
  stateFromGasLeft : Nat
  stateUsed : Nat
  refundCounter : Int
  deriving DecidableEq, Repr

/-- A running frame together with the state-gas values restored by rollback. -/
structure FrameGasState where
  gas : GasState
  stateGasBaseline : Nat
  stateUsedBaseline : Nat
  refundCounterBaseline : Int
  deriving DecidableEq, Repr

/-- The paused parent and running child produced by a frame entry. -/
structure FrameEntry where
  pausedParent : GasState
  child : FrameGasState
  deriving DecidableEq, Repr

namespace GasMachine

def totalRemaining (state : GasState) : Nat :=
  state.gasLeft + state.stateReservoir

def chargeExecution (amount : Nat) (state : GasState) : Except OutOfGas GasState :=
  if amount <= state.gasLeft then
    .ok { state with gasLeft := state.gasLeft - amount }
  else
    .error .outOfGas

def stateChargeFromReservoir (amount : Nat) (state : GasState) : Nat :=
  min amount state.stateReservoir

def stateChargeFromGasLeft (amount : Nat) (state : GasState) : Nat :=
  amount - stateChargeFromReservoir amount state

/-- The successor used only after `chargeState` establishes that the spill is affordable. -/
private def chargedState (amount : Nat) (state : GasState) : GasState :=
  let fromReservoir := stateChargeFromReservoir amount state
  let fromGasLeft := stateChargeFromGasLeft amount state
  { state with
    gasLeft := state.gasLeft - fromGasLeft
    stateReservoir := state.stateReservoir - fromReservoir
    stateFromGasLeft := state.stateFromGasLeft + fromGasLeft
    stateUsed := state.stateUsed + amount }

def chargeState (amount : Nat) (state : GasState) : Except OutOfGas GasState :=
  if stateChargeFromGasLeft amount state <= state.gasLeft then
    .ok (chargedState amount state)
  else
    .error .outOfGas

def appliedRefill (amount : Nat) (state : GasState) : Nat :=
  min amount state.stateUsed

/-- Protocol-valid refills cannot remove more state gas than has been charged. -/
def CanRefill (amount : Nat) (state : GasState) : Prop :=
  amount <= state.stateUsed

def refillToGasLeft (amount : Nat) (state : GasState) : Nat :=
  min (appliedRefill amount state) state.stateFromGasLeft

/--
Refills the most recently charged pool first. An excessive request is capped by
`stateUsed`, making the total operation safe. `CanRefill` identifies the protocol-valid
domain on which the full requested amount is credited.
-/
def refillState (amount : Nat) (state : GasState) : GasState :=
  let applied := appliedRefill amount state
  let toGasLeft := refillToGasLeft amount state
  { state with
    gasLeft := state.gasLeft + toGasLeft
    stateReservoir := state.stateReservoir + (applied - toGasLeft)
    stateFromGasLeft := state.stateFromGasLeft - toGasLeft
    stateUsed := state.stateUsed - applied }

def gasOpcode (state : GasState) : Nat :=
  state.gasLeft

def eip150Cap (state : GasState) : Nat :=
  state.gasLeft - state.gasLeft / 64

def forwardGas (requested : Nat) (state : GasState) : Nat :=
  min requested (eip150Cap state)

/--
Pauses a parent and starts a child. Only execution gas is subject to EIP-150;
the reservoir moves to the child in full and the paused parent retains none.
-/
def enterFrame (requested : Nat) (state : GasState) : FrameEntry :=
  let forwarded := forwardGas requested state
  { pausedParent :=
      { state with
        gasLeft := state.gasLeft - forwarded
        stateReservoir := 0 }
    child :=
      { gas :=
          { state with
            gasLeft := forwarded
            stateFromGasLeft := 0 }
        stateGasBaseline := state.stateReservoir
        stateUsedBaseline := state.stateUsed
        refundCounterBaseline := state.refundCounter } }

def restoreRevert (frame : FrameGasState) : GasState :=
  { frame.gas with
    gasLeft := frame.gas.gasLeft + frame.gas.stateFromGasLeft
    stateReservoir := frame.stateGasBaseline
    stateFromGasLeft := 0
    stateUsed := frame.stateUsedBaseline
    refundCounter := frame.refundCounterBaseline }

def restoreException (frame : FrameGasState) : GasState :=
  { restoreRevert frame with gasLeft := 0 }

def repayStateFromGasLeft (state : GasState) : GasState :=
  let repayment := min state.stateReservoir state.stateFromGasLeft
  { state with
    gasLeft := state.gasLeft + repayment
    stateReservoir := state.stateReservoir - repayment
    stateFromGasLeft := state.stateFromGasLeft - repayment }

/--
Merges a successful child into its paused parent. The parent owns the unforwarded
execution gas; the child owns the fully forwarded reservoir and the authoritative
transaction-wide state/refund counters.
-/
private def mergeWithParent (parent : GasState) (child : FrameGasState) : GasState :=
  repayStateFromGasLeft
    { child.gas with
      gasLeft := parent.gasLeft + child.gas.gasLeft
      stateReservoir := parent.stateReservoir + child.gas.stateReservoir
      stateFromGasLeft := parent.stateFromGasLeft + child.gas.stateFromGasLeft }

def mergeSuccess (entry : FrameEntry) (child : FrameGasState) : GasState :=
  mergeWithParent entry.pausedParent child

def mergeRevert (entry : FrameEntry) (child : FrameGasState) : GasState :=
  mergeSuccess entry { child with gas := restoreRevert child }

def mergeException (entry : FrameEntry) (child : FrameGasState) : GasState :=
  mergeSuccess entry { child with gas := restoreException child }

/-- State creation accounted by this frame is the signed change from its entry snapshot. -/
def committedStateGas (frame : FrameGasState) : Int :=
  (frame.gas.stateUsed : Int) - frame.stateUsedBaseline

/-- Conservation law maintained by state charges and refills inside a frame. -/
def FrameInvariant (frame : FrameGasState) : Prop :=
  frame.gas.stateUsed + frame.gas.stateReservoir =
    frame.stateUsedBaseline + frame.stateGasBaseline + frame.gas.stateFromGasLeft

theorem chargeExecution_of_le {amount : Nat} {state : GasState}
    (h : amount <= state.gasLeft) :
    chargeExecution amount state = .ok { state with gasLeft := state.gasLeft - amount } := by
  simp [chargeExecution, h]

theorem chargeExecution_of_lt {amount : Nat} {state : GasState}
    (h : state.gasLeft < amount) :
    chargeExecution amount state = .error .outOfGas := by
  simp [chargeExecution, Nat.not_le_of_lt h]

theorem chargeExecution_changes_only_gasLeft {amount : Nat} {before after : GasState}
    (h : chargeExecution amount before = .ok after) :
    after.stateReservoir = before.stateReservoir ∧
    after.stateFromGasLeft = before.stateFromGasLeft ∧
    after.stateUsed = before.stateUsed ∧
    after.refundCounter = before.refundCounter := by
  simp only [chargeExecution] at h
  split at h
  case isTrue =>
    simp only [Except.ok.injEq] at h
    subst after
    simp
  case isFalse => simp at h

theorem chargeState_of_affordable {amount : Nat} {state : GasState}
    (h : stateChargeFromGasLeft amount state <= state.gasLeft) :
    chargeState amount state = .ok (chargedState amount state) := by
  simp [chargeState, h]

theorem chargeState_of_insufficient {amount : Nat} {state : GasState}
    (h : state.gasLeft < stateChargeFromGasLeft amount state) :
    chargeState amount state = .error .outOfGas := by
  simp [chargeState, Nat.not_le_of_lt h]

theorem chargedState_fields (amount : Nat) (state : GasState) :
    let fromReservoir := min amount state.stateReservoir
    let fromGasLeft := amount - fromReservoir
    (chargedState amount state).stateReservoir = state.stateReservoir - fromReservoir ∧
    (chargedState amount state).gasLeft = state.gasLeft - fromGasLeft ∧
    (chargedState amount state).stateFromGasLeft = state.stateFromGasLeft + fromGasLeft ∧
    (chargedState amount state).stateUsed = state.stateUsed + amount ∧
    (chargedState amount state).refundCounter = state.refundCounter := by
  simp [chargedState, stateChargeFromReservoir, stateChargeFromGasLeft]

theorem chargedState_total {amount : Nat} {state : GasState}
    (h : stateChargeFromGasLeft amount state <= state.gasLeft) :
    totalRemaining (chargedState amount state) + amount = totalRemaining state := by
  by_cases hReservoir : amount <= state.stateReservoir
  · simp [totalRemaining, chargedState, stateChargeFromGasLeft,
      stateChargeFromReservoir, Nat.min_eq_left hReservoir] at h ⊢
    omega
  · have hReservoirLt : state.stateReservoir < amount := Nat.lt_of_not_ge hReservoir
    simp [totalRemaining, chargedState, stateChargeFromGasLeft,
      stateChargeFromReservoir, Nat.min_eq_right (Nat.le_of_lt hReservoirLt)] at h ⊢
    omega

theorem refillState_lifo (amount : Nat) (state : GasState) :
    let applied := min amount state.stateUsed
    let toGasLeft := min applied state.stateFromGasLeft
    (refillState amount state).gasLeft = state.gasLeft + toGasLeft ∧
    (refillState amount state).stateFromGasLeft = state.stateFromGasLeft - toGasLeft ∧
    (refillState amount state).stateReservoir = state.stateReservoir + (applied - toGasLeft) ∧
    (refillState amount state).stateUsed = state.stateUsed - applied ∧
    (refillState amount state).refundCounter = state.refundCounter := by
  simp [refillState, appliedRefill, refillToGasLeft]

theorem refillState_total (amount : Nat) (state : GasState) :
    totalRemaining (refillState amount state) =
      totalRemaining state + min amount state.stateUsed := by
  simp only [totalRemaining, refillState, appliedRefill, refillToGasLeft]
  omega

theorem appliedRefill_eq_requested {amount : Nat} {state : GasState}
    (h : CanRefill amount state) :
    appliedRefill amount state = amount := by
  simp [appliedRefill, Nat.min_eq_left h]

theorem refillState_valid_total {amount : Nat} {state : GasState}
    (h : CanRefill amount state) :
    totalRemaining (refillState amount state) = totalRemaining state + amount := by
  rw [refillState_total, Nat.min_eq_left h]

theorem refillState_valid_lifo {amount : Nat} {state : GasState}
    (h : CanRefill amount state) :
    let toGasLeft := min amount state.stateFromGasLeft
    (refillState amount state).gasLeft = state.gasLeft + toGasLeft ∧
    (refillState amount state).stateFromGasLeft = state.stateFromGasLeft - toGasLeft ∧
    (refillState amount state).stateReservoir = state.stateReservoir + (amount - toGasLeft) ∧
    (refillState amount state).stateUsed = state.stateUsed - amount := by
  simp [refillState, appliedRefill, refillToGasLeft, Nat.min_eq_left h]

theorem enterFrame_invariant (requested : Nat) (state : GasState) :
    FrameInvariant (enterFrame requested state).child := by
  simp [FrameInvariant, enterFrame]

theorem chargedState_preserves_invariant (amount : Nat) (frame : FrameGasState)
    (h : FrameInvariant frame) :
    FrameInvariant { frame with gas := chargedState amount frame.gas } := by
  simp only [FrameInvariant, chargedState, stateChargeFromGasLeft,
    stateChargeFromReservoir] at h ⊢
  omega

theorem refillState_preserves_invariant (amount : Nat) (frame : FrameGasState)
    (h : FrameInvariant frame) :
    FrameInvariant { frame with gas := refillState amount frame.gas } := by
  simp only [FrameInvariant, refillState, appliedRefill, refillToGasLeft] at h ⊢
  omega

theorem gasOpcode_observes_only_gasLeft (state : GasState) :
    gasOpcode state = state.gasLeft := rfl

theorem forwardGas_uses_only_gasLeft {left right : GasState}
    (h : left.gasLeft = right.gasLeft) (requested : Nat) :
    forwardGas requested left = forwardGas requested right := by
  simp [forwardGas, eip150Cap, h]

theorem forwardGas_le_gasLeft (requested : Nat) (state : GasState) :
    forwardGas requested state <= state.gasLeft := by
  simp only [forwardGas, eip150Cap]
  omega

theorem enterFrame_moves_reservoir (requested : Nat) (state : GasState) :
    (enterFrame requested state).pausedParent.stateReservoir = 0 ∧
    (enterFrame requested state).child.gas.stateReservoir = state.stateReservoir := by
  simp [enterFrame]

theorem enterFrame_conserves_total (requested : Nat) (state : GasState) :
    totalRemaining (enterFrame requested state).pausedParent +
      totalRemaining (enterFrame requested state).child.gas = totalRemaining state := by
  simp only [enterFrame, totalRemaining]
  have h := forwardGas_le_gasLeft requested state
  omega

theorem restoreRevert_baseline (frame : FrameGasState) :
    (restoreRevert frame).stateReservoir = frame.stateGasBaseline ∧
    (restoreRevert frame).stateUsed = frame.stateUsedBaseline ∧
    (restoreRevert frame).stateFromGasLeft = 0 ∧
    (restoreRevert frame).refundCounter = frame.refundCounterBaseline := by
  simp [restoreRevert]

theorem restoreRevert_returns_spilled_state_gas (frame : FrameGasState) :
    (restoreRevert frame).gasLeft = frame.gas.gasLeft + frame.gas.stateFromGasLeft := by
  rfl

theorem restoreException_consumes_execution_gas (frame : FrameGasState) :
    (restoreException frame).gasLeft = 0 := by
  rfl

theorem restoreException_baseline (frame : FrameGasState) :
    (restoreException frame).stateReservoir = frame.stateGasBaseline ∧
    (restoreException frame).stateUsed = frame.stateUsedBaseline ∧
    (restoreException frame).stateFromGasLeft = 0 ∧
    (restoreException frame).refundCounter = frame.refundCounterBaseline := by
  simp [restoreException, restoreRevert]

theorem repayStateFromGasLeft_conserves_total (state : GasState) :
    totalRemaining (repayStateFromGasLeft state) = totalRemaining state := by
  simp only [totalRemaining, repayStateFromGasLeft]
  omega

theorem mergeSuccess_conserves_total (entry : FrameEntry) (child : FrameGasState) :
    totalRemaining (mergeSuccess entry child) =
      totalRemaining entry.pausedParent + totalRemaining child.gas := by
  rw [mergeSuccess, mergeWithParent, repayStateFromGasLeft_conserves_total]
  simp only [totalRemaining]
  omega

theorem enterFrame_mergeSuccess_conserves_total (requested : Nat) (state : GasState) :
    let entry := enterFrame requested state
    totalRemaining (mergeSuccess entry entry.child) = totalRemaining state := by
  simp only
  rw [mergeSuccess_conserves_total, enterFrame_conserves_total]

theorem mergeRevert_restores_child_baseline (entry : FrameEntry) (child : FrameGasState) :
    let merged := mergeRevert entry child
    merged.stateUsed = child.stateUsedBaseline ∧
      merged.refundCounter = child.refundCounterBaseline := by
  simp [mergeRevert, mergeSuccess, mergeWithParent, repayStateFromGasLeft, restoreRevert]

theorem mergeException_consumes_child_execution_gas (entry : FrameEntry)
    (child : FrameGasState) :
    (mergeException entry child).gasLeft <= entry.pausedParent.gasLeft +
      entry.pausedParent.stateFromGasLeft := by
  simp only [mergeException, mergeSuccess, restoreException, restoreRevert,
    mergeWithParent, repayStateFromGasLeft]
  omega

theorem mergeException_ignores_child_gasLeft (entry : FrameEntry)
    (child : FrameGasState) :
    mergeException entry child =
      mergeException entry { child with gas := { child.gas with gasLeft := 0 } } := by
  rfl

theorem rollback_has_no_committed_state_gas (frame : FrameGasState) :
    committedStateGas { frame with gas := restoreRevert frame } = 0 := by
  simp [committedStateGas, restoreRevert]

theorem committedStateGas_agrees_with_pool_delta (frame : FrameGasState)
    (h : FrameInvariant frame) :
    committedStateGas frame =
      (frame.stateGasBaseline : Int) - frame.gas.stateReservoir +
        frame.gas.stateFromGasLeft := by
  simp only [committedStateGas, FrameInvariant] at h ⊢
  omega

end GasMachine
end Eip803x
