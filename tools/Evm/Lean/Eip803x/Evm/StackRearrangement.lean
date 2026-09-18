-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Lean.Elab.Tactic.Omega
import Eip803x.Gas
import Eip803x.Evm.MemoryStackControl
import Eip803x.Evm.Word

/-!
  A small handwritten operational reference for POP, DUP1..DUP16, and
  SWAP1..SWAP16.  It deliberately shares only the bounded word-stack
  foundations with `MemoryStackControl`; the gas debit, PC convention, and
  fault ordering are specified here independently of the generated metadata.
-/

namespace Eip803x
namespace Evm
namespace StackRearrangement

open Word
open MemoryStackControl

abbrev OperandStack := MemoryStackControl.Stack

inductive Operation where
  | pop
  | dup (depth : Nat)
  | swap (depth : Nat)
  deriving DecidableEq, Repr

structure Plan where
  fixedGas : Nat
  stackInputs : Nat
  stackGrowth : Nat
  stackLimit : Nat
  pcBeforeGas : Bool
  gasExhaustsOnFailure : Bool
  faultsAfterGas : Bool
  deriving DecidableEq, Repr

structure State where
  stack : OperandStack
  gas : GasState
  pc : Nat
  deriving Repr

inductive Error where
  | outOfGas
  | stackUnderflow
  | stackOverflow
  deriving DecidableEq, Repr

inductive Outcome where
  | success (state : State)
  | error (reason : Error) (state : State)
  deriving Repr

namespace Outcome

def state : Outcome → State
  | .success state => state
  | .error _ state => state

def error? : Outcome → Option Error
  | .success _ => none
  | .error reason _ => some reason

end Outcome

namespace State

def withGasLeft (state : State) (gasLeft : Nat) : State :=
  { state with gas := { state.gas with gasLeft } }

def advancePc (state : State) : State :=
  { state with pc := state.pc + 1 }

end State

inductive Debit where
  | paid (state : State)
  | outOfGas (state : State)
  deriving Repr

def debit (plan : Plan) (state : State) : Debit :=
  if plan.fixedGas ≤ state.gas.gasLeft then
    .paid (state.withGasLeft (state.gas.gasLeft - plan.fixedGas))
  else
    .outOfGas (if plan.gasExhaustsOnFailure = true then state.withGasLeft 0 else state)

def operationPlan : Operation → Plan
  | .pop =>
    { fixedGas := 2
      stackInputs := 1
      stackGrowth := 0
      stackLimit := MemoryStackControl.stackLimit
      pcBeforeGas := true
      gasExhaustsOnFailure := true
      faultsAfterGas := true }
  | .dup depth =>
    { fixedGas := 3
      stackInputs := depth
      stackGrowth := 1
      stackLimit := MemoryStackControl.stackLimit
      pcBeforeGas := true
      gasExhaustsOnFailure := true
      faultsAfterGas := true }
  | .swap depth =>
    { fixedGas := 3
      stackInputs := depth + 1
      stackGrowth := 0
      stackLimit := MemoryStackControl.stackLimit
      pcBeforeGas := true
      gasExhaustsOnFailure := true
      faultsAfterGas := true }

def stackError (state : State) : MemoryStackControl.StackFault → Outcome
  | .underflow => .error .stackUnderflow state
  | .overflow => .error .stackOverflow state

def applyOperation : Operation → OperandStack → Except Error OperandStack
  | .pop, stack =>
    match stack.pop with
    | none => .error .stackUnderflow
    | some (_, result) => .ok result
  | .dup depth, stack =>
    match stack.duplicate depth with
    | .ok result => .ok result
    | .error .underflow => .error .stackUnderflow
    | .error .overflow => .error .stackOverflow
  | .swap depth, stack =>
    match stack.swap depth with
    | none => .error .stackUnderflow
    | some result => .ok result

def executeAfterGas (operation : Operation) (plan : Plan) (afterGas : State) : Outcome :=
  if plan.faultsAfterGas = false then
    match applyOperation operation afterGas.stack with
    | .ok result => .success { afterGas with stack := result }
    | .error reason => .error reason afterGas
  else if afterGas.stack.words.length < plan.stackInputs then
    .error .stackUnderflow afterGas
  else if plan.stackGrowth > 0 ∧
      afterGas.stack.words.length + plan.stackGrowth > plan.stackLimit then
    .error .stackOverflow afterGas
  else
    match applyOperation operation afterGas.stack with
    | .ok result => .success { afterGas with stack := result }
    | .error reason => .error reason afterGas

theorem executeAfterGas_state_pc (operation : Operation) (plan : Plan) (afterGas : State) :
    (executeAfterGas operation plan afterGas).state.pc = afterGas.pc := by
  unfold executeAfterGas
  split
  · next hFaults =>
    cases hApply : applyOperation operation afterGas.stack with
    | ok result => rfl
    | error reason => rfl
  · next hFaults =>
    split
    · next hUnderflow => rfl
    · next hUnderflow =>
      split
      · next hOverflow => rfl
      · next hOverflow =>
        cases hApply : applyOperation operation afterGas.stack with
        | ok result => rfl
        | error reason => rfl

theorem executeAfterGas_underflow (operation : Operation) (plan : Plan) (afterGas : State)
    (hFaults : plan.faultsAfterGas = true)
    (hDepth : afterGas.stack.words.length < plan.stackInputs) :
    executeAfterGas operation plan afterGas = .error .stackUnderflow afterGas := by
  simp [executeAfterGas, hFaults, hDepth]

theorem executeAfterGas_overflow (operation : Operation) (plan : Plan) (afterGas : State)
    (hFaults : plan.faultsAfterGas = true)
    (hDepth : ¬afterGas.stack.words.length < plan.stackInputs)
    (hGrowth : plan.stackGrowth > 0 ∧
      afterGas.stack.words.length + plan.stackGrowth > plan.stackLimit) :
    executeAfterGas operation plan afterGas = .error .stackOverflow afterGas := by
  simp [executeAfterGas, hFaults, hDepth, hGrowth]

def executePlanned (operation : Operation) (plan : Plan) (state : State) : Outcome :=
  let dispatched := if plan.pcBeforeGas = true then state.advancePc else state
  match debit plan dispatched with
  | .outOfGas afterOutOfGas => .error .outOfGas afterOutOfGas
  | .paid afterGas => executeAfterGas operation plan afterGas

def execute (operation : Operation) (state : State) : Outcome :=
  executePlanned operation (operationPlan operation) state

def gasCost : Operation → Nat
  | .pop => 2
  | .dup _ | .swap _ => 3

def stackInputs : Operation → Nat
  | .pop => 1
  | .dup depth => depth
  | .swap depth => depth + 1

def stackGrowth : Operation → Nat
  | .pop | .swap _ => 0
  | .dup _ => 1

theorem operation_plan_matches_metadata (operation : Operation) :
    let plan := operationPlan operation
    plan.fixedGas = gasCost operation ∧
      plan.stackInputs = stackInputs operation ∧
      plan.stackGrowth = stackGrowth operation ∧
      plan.stackLimit = MemoryStackControl.stackLimit ∧
      plan.pcBeforeGas = true ∧
      plan.gasExhaustsOnFailure = true ∧
      plan.faultsAfterGas = true := by
  cases operation <;> simp [operationPlan, gasCost, stackInputs, stackGrowth]

theorem execute_pc_before_gas (operation : Operation) (state : State) :
    (execute operation state).state.pc = state.pc + 1 := by
  cases operation with
  | pop =>
    unfold execute
    simp only [operationPlan]
    by_cases hGas : 2 ≤ state.gas.gasLeft
    · simp only [executePlanned, State.advancePc, debit, State.withGasLeft, hGas, ↓reduceIte]
      exact executeAfterGas_state_pc .pop _ _
    · simp only [executePlanned, State.advancePc, debit, State.withGasLeft, hGas, ↓reduceIte]
      rfl
  | dup depth =>
    unfold execute
    simp only [operationPlan]
    by_cases hGas : 3 ≤ state.gas.gasLeft
    · simp only [executePlanned, State.advancePc, debit, State.withGasLeft, hGas, ↓reduceIte]
      exact executeAfterGas_state_pc (.dup depth) _ _
    · simp only [executePlanned, State.advancePc, debit, State.withGasLeft, hGas, ↓reduceIte]
      rfl
  | swap depth =>
    unfold execute
    simp only [operationPlan]
    by_cases hGas : 3 ≤ state.gas.gasLeft
    · simp only [executePlanned, State.advancePc, debit, State.withGasLeft, hGas, ↓reduceIte]
      exact executeAfterGas_state_pc (.swap depth) _ _
    · simp only [executePlanned, State.advancePc, debit, State.withGasLeft, hGas, ↓reduceIte]
      rfl

theorem execute_out_of_gas_exhausts (operation : Operation) (state : State)
    (h : state.gas.gasLeft < gasCost operation) :
    (execute operation state).error? = some .outOfGas ∧
      (execute operation state).state.gas.gasLeft = 0 := by
  cases operation with
  | pop =>
    have hGas : ¬ 2 ≤ state.gas.gasLeft := Nat.not_le_of_lt h
    simp [execute, executePlanned, operationPlan, debit, Outcome.state, Outcome.error?,
      State.advancePc, State.withGasLeft, hGas]
  | dup depth =>
    have hGas : ¬ 3 ≤ state.gas.gasLeft := Nat.not_le_of_lt h
    simp [execute, executePlanned, operationPlan, debit, Outcome.state, Outcome.error?,
      State.advancePc, State.withGasLeft, hGas]
  | swap depth =>
    have hGas : ¬ 3 ≤ state.gas.gasLeft := Nat.not_le_of_lt h
    simp [execute, executePlanned, operationPlan, debit, Outcome.state, Outcome.error?,
      State.advancePc, State.withGasLeft, hGas]

theorem execute_underflow_after_charge (operation : Operation) (state : State)
    (hGas : gasCost operation ≤ state.gas.gasLeft)
    (hDepth : state.stack.words.length < stackInputs operation) :
    (execute operation state).error? = some .stackUnderflow ∧
      (execute operation state).state.pc = state.pc + 1 ∧
      (execute operation state).state.gas.gasLeft = state.gas.gasLeft - gasCost operation := by
  cases operation with
  | pop =>
    have hCharge : 2 ≤ state.gas.gasLeft := by simpa [gasCost] using hGas
    have hInputs : state.stack.words.length < 1 := by simpa [stackInputs] using hDepth
    have hEmpty : state.stack.words = [] := by
      apply List.eq_nil_of_length_eq_zero
      omega
    have hAfter : executeAfterGas .pop
        { fixedGas := 2
          stackInputs := 1
          stackGrowth := 0
          stackLimit := MemoryStackControl.stackLimit
          pcBeforeGas := true
          gasExhaustsOnFailure := true
          faultsAfterGas := true }
        { stack := state.stack
          gas := { state.gas with gasLeft := state.gas.gasLeft - 2 }
          pc := state.pc + 1 } =
        .error .stackUnderflow
          { stack := state.stack
            gas := { state.gas with gasLeft := state.gas.gasLeft - 2 }
            pc := state.pc + 1 } := by
      apply executeAfterGas_underflow
      · rfl
      · simpa [operationPlan] using hInputs
    have hExec : execute .pop state =
        .error .stackUnderflow
          { stack := state.stack
            gas := { state.gas with gasLeft := state.gas.gasLeft - 2 }
            pc := state.pc + 1 } := by
      unfold execute executePlanned
      simp only [operationPlan]
      dsimp [State.advancePc]
      unfold debit
      by_cases hAffordable : 2 ≤ state.gas.gasLeft
      · simp only [hAffordable, if_pos]
        simpa [State.withGasLeft, operationPlan] using hAfter
      · simp only [hAffordable]
        exact (hAffordable hCharge).elim
    rw [hExec]
    simp [Outcome.error?, Outcome.state, gasCost]
  | dup depth =>
    have hCharge : 3 ≤ state.gas.gasLeft := by simpa [gasCost] using hGas
    have hInputs : state.stack.words.length < depth := by simpa [stackInputs] using hDepth
    have hAfter : executeAfterGas (.dup depth)
        { fixedGas := 3
          stackInputs := depth
          stackGrowth := 1
          stackLimit := MemoryStackControl.stackLimit
          pcBeforeGas := true
          gasExhaustsOnFailure := true
          faultsAfterGas := true }
        { stack := state.stack
          gas := { state.gas with gasLeft := state.gas.gasLeft - 3 }
          pc := state.pc + 1 } =
        .error .stackUnderflow
          { stack := state.stack
            gas := { state.gas with gasLeft := state.gas.gasLeft - 3 }
            pc := state.pc + 1 } := by
      apply executeAfterGas_underflow
      · rfl
      · simpa [operationPlan] using hInputs
    have hExec : execute (.dup depth) state =
        .error .stackUnderflow
          { stack := state.stack
            gas := { state.gas with gasLeft := state.gas.gasLeft - 3 }
            pc := state.pc + 1 } := by
      unfold execute executePlanned
      simp only [operationPlan]
      dsimp [State.advancePc]
      unfold debit
      by_cases hAffordable : 3 ≤ state.gas.gasLeft
      · simp only [hAffordable, if_pos]
        simpa [State.withGasLeft] using hAfter
      · simp only [hAffordable]
        exact (hAffordable hCharge).elim
    rw [hExec]
    simp [Outcome.error?, Outcome.state, gasCost]
  | swap depth =>
    have hCharge : 3 ≤ state.gas.gasLeft := by simpa [gasCost] using hGas
    have hInputs : state.stack.words.length < depth + 1 := by simpa [stackInputs] using hDepth
    have hAfter : executeAfterGas (.swap depth)
        { fixedGas := 3
          stackInputs := depth + 1
          stackGrowth := 0
          stackLimit := MemoryStackControl.stackLimit
          pcBeforeGas := true
          gasExhaustsOnFailure := true
          faultsAfterGas := true }
        { stack := state.stack
          gas := { state.gas with gasLeft := state.gas.gasLeft - 3 }
          pc := state.pc + 1 } =
        .error .stackUnderflow
          { stack := state.stack
            gas := { state.gas with gasLeft := state.gas.gasLeft - 3 }
            pc := state.pc + 1 } := by
      apply executeAfterGas_underflow
      · rfl
      · simpa [operationPlan] using hInputs
    have hExec : execute (.swap depth) state =
        .error .stackUnderflow
          { stack := state.stack
            gas := { state.gas with gasLeft := state.gas.gasLeft - 3 }
            pc := state.pc + 1 } := by
      unfold execute executePlanned
      simp only [operationPlan]
      dsimp [State.advancePc]
      unfold debit
      by_cases hAffordable : 3 ≤ state.gas.gasLeft
      · simp only [hAffordable, if_pos]
        simpa [State.withGasLeft] using hAfter
      · simp only [hAffordable]
        exact (hAffordable hCharge).elim
    rw [hExec]
    simp [Outcome.error?, Outcome.state, gasCost]

theorem execute_dup_overflow_after_charge (depth : Nat) (state : State)
    (hDepth : depth ≤ state.stack.words.length)
    (hGas : 3 ≤ state.gas.gasLeft)
    (hFull : state.stack.words.length = MemoryStackControl.stackLimit) :
    (execute (.dup depth) state).error? = some .stackOverflow ∧
      (execute (.dup depth) state).state.pc = state.pc + 1 ∧
      (execute (.dup depth) state).state.gas.gasLeft = state.gas.gasLeft - 3 := by
  have hDepth' : ¬ state.stack.words.length < depth := Nat.not_lt_of_ge hDepth
  have hGrowth : 0 < 1 ∧
      state.stack.words.length + 1 > MemoryStackControl.stackLimit := by
    constructor
    · omega
    · rw [hFull]
      omega
  have hAfter : executeAfterGas (.dup depth)
      { fixedGas := 3
        stackInputs := depth
        stackGrowth := 1
        stackLimit := MemoryStackControl.stackLimit
        pcBeforeGas := true
        gasExhaustsOnFailure := true
        faultsAfterGas := true }
      { stack := state.stack
        gas := { state.gas with gasLeft := state.gas.gasLeft - 3 }
        pc := state.pc + 1 } =
      .error .stackOverflow
        { stack := state.stack
          gas := { state.gas with gasLeft := state.gas.gasLeft - 3 }
          pc := state.pc + 1 } := by
    apply executeAfterGas_overflow
    · rfl
    · exact hDepth'
    · exact hGrowth
  have hExec : execute (.dup depth) state =
      .error .stackOverflow
        { stack := state.stack
          gas := { state.gas with gasLeft := state.gas.gasLeft - 3 }
          pc := state.pc + 1 } := by
    unfold execute executePlanned
    simp only [operationPlan]
    dsimp [State.advancePc]
    unfold debit
    by_cases hAffordable : 3 ≤ state.gas.gasLeft
    · simp only [hAffordable, if_pos]
      simpa [State.withGasLeft] using hAfter
    · exact (hAffordable hGas).elim
  rw [hExec]
  simp [Outcome.error?, Outcome.state]

theorem pop_exact (value : UInt256) (tail : List UInt256) (gas : GasState) (pc : Nat)
    (hBound : (value :: tail).length ≤ MemoryStackControl.stackLimit)
    (hGas : 2 ≤ gas.gasLeft) :
    (execute .pop
        { stack := ⟨value :: tail, hBound⟩, gas := gas, pc := pc }).state.stack.words = tail := by
  have hTailBound : tail.length ≤ MemoryStackControl.stackLimit := by
    simp only [List.length_cons] at hBound
    omega
  have hAfter : executeAfterGas .pop
      { fixedGas := 2
        stackInputs := 1
        stackGrowth := 0
        stackLimit := MemoryStackControl.stackLimit
        pcBeforeGas := true
        gasExhaustsOnFailure := true
        faultsAfterGas := true }
      { stack := ⟨value :: tail, hBound⟩
        gas := { gas with gasLeft := gas.gasLeft - 2 }
        pc := pc + 1 } =
      .success
        { stack := ⟨tail, hTailBound⟩
          gas := { gas with gasLeft := gas.gasLeft - 2 }
          pc := pc + 1 } := by
    simp [executeAfterGas, applyOperation, MemoryStackControl.Stack.pop]
  have hExec : execute .pop
      { stack := ⟨value :: tail, hBound⟩, gas := gas, pc := pc } =
      .success
        { stack := ⟨tail, hTailBound⟩
          gas := { gas with gasLeft := gas.gasLeft - 2 }
          pc := pc + 1 } := by
    unfold execute executePlanned
    simp only [operationPlan]
    dsimp [State.advancePc]
    unfold debit
    by_cases hAffordable : 2 ≤ gas.gasLeft
    · simp only [hAffordable, if_pos]
      simpa [State.withGasLeft] using hAfter
    · exact (hAffordable hGas).elim
  rw [hExec]
  rfl

theorem dup_exact (depth : Nat) (value : UInt256) (tail : List UInt256) (gas : GasState) (pc : Nat)
    (hDepth : 0 < depth ∧ depth ≤ (value :: tail).length)
    (hRoom : (value :: tail).length < MemoryStackControl.stackLimit)
    (hGas : 3 ≤ gas.gasLeft)
    (hValue : (value :: tail)[depth - 1]? = some value) :
    (execute (.dup depth)
      { stack := ⟨value :: tail, by omega⟩, gas := gas, pc := pc }).state.stack.words =
      value :: (value :: tail) := by
  have hDepth' : ¬ (value :: tail).length < depth := Nat.not_lt_of_ge hDepth.2
  have hResultBound : (value :: value :: tail).length ≤ MemoryStackControl.stackLimit := by
    simp only [List.length_cons] at hRoom ⊢
    omega
  have hGrowth : ¬ (value :: tail).length + 1 > MemoryStackControl.stackLimit := by
    simp only [List.length_cons] at hRoom ⊢
    omega
  have hDepthLength : depth ≤ tail.length + 1 := by
    simpa only [List.length_cons] using hDepth.2
  have hRoomLength : tail.length + 1 < MemoryStackControl.stackLimit := by
    simpa only [List.length_cons] using hRoom
  have hDepthLength' : ¬tail.length + 1 < depth := by omega
  have hGrowth' : ¬MemoryStackControl.stackLimit < tail.length + 1 + 1 := by omega
  have hDuplicate : MemoryStackControl.Stack.duplicate depth
      ⟨value :: tail, by omega⟩ =
      .ok ⟨value :: value :: tail, hResultBound⟩ := by
    simp [MemoryStackControl.Stack.duplicate, MemoryStackControl.Stack.push,
      hDepth.1, hDepthLength, hRoomLength, hValue]
  have hAfter : executeAfterGas (.dup depth)
      { fixedGas := 3
        stackInputs := depth
        stackGrowth := 1
        stackLimit := MemoryStackControl.stackLimit
        pcBeforeGas := true
        gasExhaustsOnFailure := true
        faultsAfterGas := true }
      { stack := ⟨value :: tail, by omega⟩
        gas := { gas with gasLeft := gas.gasLeft - 3 }
        pc := pc + 1 } =
      .success
        { stack := ⟨value :: value :: tail, hResultBound⟩
          gas := { gas with gasLeft := gas.gasLeft - 3 }
          pc := pc + 1 } := by
    simp [executeAfterGas, applyOperation, hDepthLength', hGrowth', hDuplicate]
  have hExec : execute (.dup depth)
      { stack := ⟨value :: tail, by omega⟩, gas := gas, pc := pc } =
      .success
        { stack := ⟨value :: value :: tail, hResultBound⟩
          gas := { gas with gasLeft := gas.gasLeft - 3 }
          pc := pc + 1 } := by
    unfold execute executePlanned
    simp only [operationPlan]
    dsimp [State.advancePc]
    unfold debit
    by_cases hAffordable : 3 ≤ gas.gasLeft
    · simp only [hAffordable, if_pos]
      simpa [State.withGasLeft] using hAfter
    · exact (hAffordable hGas).elim
  rw [hExec]
  rfl

theorem swap_exact (depth : Nat) (top : UInt256) (tail : List UInt256) (other : UInt256)
    (gas : GasState) (pc : Nat)
    (hDepth : 0 < depth ∧ depth + 1 ≤ (top :: tail).length)
    (hIndex : tail[depth - 1]? = some other)
    (hBound : (top :: tail).length ≤ MemoryStackControl.stackLimit)
    (hGas : 3 ≤ gas.gasLeft) :
    (execute (.swap depth)
        { stack := ⟨top :: tail, hBound⟩, gas := gas, pc := pc }).state.stack.words =
      other :: (MemoryStackControl.Stack.replaceAt (depth - 1) top tail).getD [] := by
  have hTail : depth ≤ tail.length := by
    simp only [List.length_cons] at hDepth
    omega
  have hIndexLt : depth - 1 < tail.length := by omega
  have hReplaceSome : ∀ (index : Nat) (words : List UInt256),
      index < words.length →
        ∃ changed, MemoryStackControl.Stack.replaceAt index top words = some changed := by
    intro index words
    induction index generalizing words with
    | zero =>
      cases words with
      | nil =>
        intro h
        simp only [List.length_nil] at h
        omega
      | cons head tail =>
        intro h
        exact ⟨top :: tail, by rfl⟩
    | succ index ih =>
      cases words with
      | nil =>
        intro h
        simp only [List.length_nil] at h
        omega
      | cons head tail =>
        intro h
        have hTailIndex : index < tail.length := by
          simp only [List.length_cons] at h
          omega
        obtain ⟨changed, hChanged⟩ := ih tail hTailIndex
        refine ⟨head :: changed, ?_⟩
        simp [MemoryStackControl.Stack.replaceAt, hChanged]
  obtain ⟨changedTail, hChanged⟩ := hReplaceSome (depth - 1) tail hIndexLt
  have hChangedLength : changedTail.length = tail.length :=
    MemoryStackControl.Stack.replaceAt_length (depth - 1) top tail changedTail hChanged
  have hBoundTail : tail.length + 1 ≤ MemoryStackControl.stackLimit := by
    simpa only [List.length_cons] using hBound
  have hResultBound : (other :: changedTail).length ≤ MemoryStackControl.stackLimit := by
    simp only [List.length_cons]
    omega
  have hGetD :
      (MemoryStackControl.Stack.replaceAt (depth - 1) top tail).getD [] = changedTail := by
    rw [hChanged]
    rfl
  have hResultBoundD :
      (other :: (MemoryStackControl.Stack.replaceAt (depth - 1) top tail).getD []).length ≤
        MemoryStackControl.stackLimit := by
    rw [hGetD]
    exact hResultBound
  have hSwap : MemoryStackControl.Stack.swap depth ⟨top :: tail, hBound⟩ =
      some ⟨other :: (MemoryStackControl.Stack.replaceAt (depth - 1) top tail).getD [],
        hResultBoundD⟩ := by
    cases depth with
    | zero => omega
    | succ depth =>
      have hIndexSucc : tail[depth]? = some other := hIndex
      have hChangedSucc : MemoryStackControl.Stack.replaceAt depth top tail = some changedTail := hChanged
      simp [MemoryStackControl.Stack.swap, hIndexSucc, hChangedSucc,
        MemoryStackControl.Stack.fromWords, List.take_of_length_le hResultBound]
  have hAfter : executeAfterGas (.swap depth)
      { fixedGas := 3
        stackInputs := depth + 1
        stackGrowth := 0
        stackLimit := MemoryStackControl.stackLimit
        pcBeforeGas := true
        gasExhaustsOnFailure := true
        faultsAfterGas := true }
      { stack := ⟨top :: tail, hBound⟩
        gas := { gas with gasLeft := gas.gasLeft - 3 }
        pc := pc + 1 } =
      .success
        { stack := ⟨other :: (MemoryStackControl.Stack.replaceAt (depth - 1) top tail).getD [],
            hResultBoundD⟩
          gas := { gas with gasLeft := gas.gasLeft - 3 }
          pc := pc + 1 } := by
    simp [executeAfterGas, applyOperation, hSwap]
    exact hTail
  have hExec : execute (.swap depth)
      { stack := ⟨top :: tail, hBound⟩, gas := gas, pc := pc } =
      .success
        { stack := ⟨other :: (MemoryStackControl.Stack.replaceAt (depth - 1) top tail).getD [],
            hResultBoundD⟩
          gas := { gas with gasLeft := gas.gasLeft - 3 }
          pc := pc + 1 } := by
    unfold execute executePlanned
    simp only [operationPlan]
    dsimp [State.advancePc]
    unfold debit
    by_cases hAffordable : 3 ≤ gas.gasLeft
    · simp only [hAffordable, if_pos]
      simpa [State.withGasLeft] using hAfter
    · exact (hAffordable hGas).elim
  rw [hExec]
  rfl

end StackRearrangement
end Evm
end Eip803x
