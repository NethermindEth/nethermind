-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Lean.Elab.Tactic.Omega
import Eip803x.Gas
import Eip803x.Evm.MemoryStackControl
import Eip803x.Evm.PureStack

/-!
  The accepted handwritten execution reference for the Amsterdam pure-word opcode slice.

  `PureStack` deliberately models only word-to-word stack transformations.
  This file adds the production ordering around those transformations: dispatch
  advances the program counter, the fixed gas charge precedes the depth check,
  and `EXP` removes its two operands before charging its exponent-byte gas.
  Production extraction and whole-dispatch refinement remain separate obligations.
-/

namespace Eip803x
namespace Evm
namespace PureStackExecution

open Word
open MemoryStackControl
open PureStack

/-- Costs selected by the active hard-fork and supplied by the caller. -/
structure Schedule where
  veryLow : Nat
  low : Nat
  mid : Nat
  expBase : Nat
  expByte : Nat
  deriving DecidableEq, Repr

namespace Schedule

/-- Amsterdam selects the EIP-160 `EXP` byte price. -/
def amsterdam : Schedule :=
  { veryLow := 3
    low := 5
    mid := 8
    expBase := 10
    expByte := 50 }

end Schedule

/-- The fixed charge made before an opcode's stack-depth check. -/
def staticCost (schedule : Schedule) : PureStack.Opcode → Nat
  | .add | .sub | .lt | .gt | .slt | .sgt | .eq | .iszero
  | .and | .or | .xor | .not | .byte | .shl | .shr | .sar => schedule.veryLow
  | .mul | .div | .sdiv | .mod | .smod | .signextend | .clz => schedule.low
  | .addmod | .mulmod => schedule.mid
  | .exp => schedule.expBase

def veryLowOpcodes : List PureStack.Opcode :=
  [ .add, .sub, .lt, .gt, .slt, .sgt, .eq, .iszero
  , .and, .or, .xor, .not, .byte, .shl, .shr, .sar ]

def lowOpcodes : List PureStack.Opcode :=
  [ .mul, .div, .sdiv, .mod, .smod, .signextend, .clz ]

def midOpcodes : List PureStack.Opcode := [.addmod, .mulmod]

def expOpcodes : List PureStack.Opcode := [.exp]

theorem staticCost_veryLow (schedule : Schedule) {opcode : PureStack.Opcode}
    (h : opcode ∈ veryLowOpcodes) : staticCost schedule opcode = schedule.veryLow := by
  cases opcode <;> simp [veryLowOpcodes, staticCost] at h ⊢

theorem staticCost_low (schedule : Schedule) {opcode : PureStack.Opcode}
    (h : opcode ∈ lowOpcodes) : staticCost schedule opcode = schedule.low := by
  cases opcode <;> simp [lowOpcodes, staticCost] at h ⊢

theorem staticCost_mid (schedule : Schedule) {opcode : PureStack.Opcode}
    (h : opcode ∈ midOpcodes) : staticCost schedule opcode = schedule.mid := by
  cases opcode <;> simp [midOpcodes, staticCost] at h ⊢

theorem staticCost_exp (schedule : Schedule) {opcode : PureStack.Opcode}
    (h : opcode ∈ expOpcodes) : staticCost schedule opcode = schedule.expBase := by
  cases opcode <;> simp [expOpcodes, staticCost] at h ⊢

/-- Every one of the 26 modeled opcodes belongs to exactly one fixed-cost family. -/
theorem staticCost_exhaustive (_schedule : Schedule) (opcode : PureStack.Opcode) :
    opcode ∈ veryLowOpcodes ∨ opcode ∈ lowOpcodes ∨ opcode ∈ midOpcodes ∨ opcode ∈ expOpcodes := by
  cases opcode <;> simp [veryLowOpcodes, lowOpcodes, midOpcodes, expOpcodes]

/-- The order is `PureStack.allOpcodes`; this is an executable 26-opcode cost audit. -/
theorem allOpcodeStaticCosts (schedule : Schedule) :
    PureStack.allOpcodes.map (staticCost schedule) =
      [ schedule.veryLow, schedule.low, schedule.veryLow, schedule.low, schedule.low
      , schedule.low, schedule.low, schedule.mid, schedule.mid, schedule.expBase
      , schedule.low, schedule.veryLow, schedule.veryLow, schedule.veryLow
      , schedule.veryLow, schedule.veryLow, schedule.veryLow, schedule.veryLow
      , schedule.veryLow, schedule.veryLow, schedule.veryLow, schedule.veryLow
      , schedule.veryLow, schedule.veryLow, schedule.veryLow, schedule.low ] := by
  rfl

/-- The number of big-endian bytes that production charges for an `EXP` exponent. -/
def exponentByteLength (exponent : UInt256) : Nat :=
  if exponent.val = 0 then 0 else exponent.val.log2 / 8 + 1

/-- The post-pop, exponent-dependent `EXP` charge. -/
def expDynamicCost (schedule : Schedule) (exponent : UInt256) : Nat :=
  schedule.expByte * exponentByteLength exponent

def expTotalCost (schedule : Schedule) (exponent : UInt256) : Nat :=
  schedule.expBase + expDynamicCost schedule exponent

@[simp] theorem exponentByteLength_zero : exponentByteLength Word.zero = 0 := by
  native_decide

@[simp] theorem expDynamicCost_zero (schedule : Schedule) :
    expDynamicCost schedule Word.zero = 0 := by
  simp [expDynamicCost]

/-- A bounded stack and the execution-gas portion of the EIP-8037 gas state. -/
structure State where
  stack : Stack
  gas : GasState
  pc : Nat
  deriving Repr

namespace State

def withGasLeft (state : State) (gasLeft : Nat) : State :=
  { state with gas := { state.gas with gasLeft } }

def advancePc (state : State) : State :=
  { state with pc := state.pc + 1 }

end State

inductive Error where
  | outOfGas
  | stackUnderflow
  deriving DecidableEq, Repr

/-- An explicit completed state, including the state observed at an exceptional exit. -/
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

/-- Production's `UpdateGas`: on an unaffordable execution charge, execution gas becomes zero. -/
inductive Debit where
  | paid (state : State)
  | outOfGas (state : State)
  deriving Repr

namespace Debit

def state : Debit → State
  | .paid state => state
  | .outOfGas state => state

end Debit

def debitExecution (cost : Nat) (state : State) : Debit :=
  if cost ≤ state.gas.gasLeft then
    .paid (state.withGasLeft (state.gas.gasLeft - cost))
  else
    .outOfGas (state.withGasLeft 0)

theorem debitExecution_paid (cost : Nat) (state : State) (h : cost ≤ state.gas.gasLeft) :
    debitExecution cost state = .paid (state.withGasLeft (state.gas.gasLeft - cost)) := by
  simp [debitExecution, h]

theorem debitExecution_outOfGas (cost : Nat) (state : State) (h : state.gas.gasLeft < cost) :
    debitExecution cost state = .outOfGas (state.withGasLeft 0) := by
  simp [debitExecution, Nat.not_le_of_gt h]

theorem debitExecution_preserves_stateGas (cost : Nat) (state : State) :
    (debitExecution cost state).state.gas.stateReservoir = state.gas.stateReservoir ∧
    (debitExecution cost state).state.gas.stateFromGasLeft = state.gas.stateFromGasLeft ∧
    (debitExecution cost state).state.gas.stateUsed = state.gas.stateUsed ∧
    (debitExecution cost state).state.gas.refundCounter = state.gas.refundCounter := by
  by_cases h : cost ≤ state.gas.gasLeft
  · simp [debitExecution, Debit.state, State.withGasLeft, h]
  · simp [debitExecution, Debit.state, State.withGasLeft, h]

/-- Bounded unary execution, with the EVM top-of-stack as the first argument. -/
def unary (operation : UInt256 → UInt256) (stack : Stack) : Option Stack :=
  match hWords : stack.words with
  | [] => none
  | value :: rest =>
    some ⟨operation value :: rest, by
      have hBound := stack.bounded
      rw [hWords] at hBound
      simpa only [List.length_cons] using hBound⟩

/-- Bounded binary execution, with the EVM top-of-stack as the first argument. -/
def binary (operation : UInt256 → UInt256 → UInt256) (stack : Stack) : Option Stack :=
  match hWords : stack.words with
  | top :: next :: rest =>
    some ⟨operation top next :: rest, by
      have hBound := stack.bounded
      rw [hWords] at hBound
      simp only [List.length_cons] at hBound ⊢
      omega⟩
  | _ => none

/-- Bounded ternary execution, with the EVM top-of-stack as the first argument. -/
def ternary (operation : UInt256 → UInt256 → UInt256 → UInt256) (stack : Stack) : Option Stack :=
  match hWords : stack.words with
  | top :: next :: third :: rest =>
    some ⟨operation top next third :: rest, by
      have hBound := stack.bounded
      rw [hWords] at hBound
      simp only [List.length_cons] at hBound ⊢
      omega⟩
  | _ => none

/-- The bounded counterpart of `PureStack.execute`, without gas or program-counter effects. -/
def stackExecute : PureStack.Opcode → Stack → Option Stack
  | .add => binary Word.add
  | .mul => binary Word.mul
  | .sub => binary Word.sub
  | .div => binary Word.udiv
  | .sdiv => binary Word.sdiv
  | .mod => binary Word.umod
  | .smod => binary Word.smod
  | .addmod => ternary Word.addmod
  | .mulmod => ternary Word.mulmod
  | .exp => binary Word.exp
  | .signextend => binary Word.signextend
  | .lt => binary Word.unsignedLt
  | .gt => binary Word.unsignedGt
  | .slt => binary Word.signedLt
  | .sgt => binary Word.signedGt
  | .eq => binary Word.equal
  | .iszero => unary Word.isZero
  | .and => binary Word.bitwiseAnd
  | .or => binary Word.bitwiseOr
  | .xor => binary Word.bitwiseXor
  | .not => unary Word.bitwiseNot
  | .byte => binary Word.byte
  | .shl => binary Word.shl
  | .shr => binary Word.shr
  | .sar => binary Word.sar
  | .clz => unary Word.clz

theorem unary_refines (operation : UInt256 → UInt256) (stack result : Stack)
    (h : unary operation stack = some result) :
    PureStack.unary operation stack.words = some result.words := by
  rcases stack with ⟨words, hBound⟩
  cases words with
  | nil => simp [unary] at h
  | cons value rest =>
    simp only [unary, PureStack.unary] at h ⊢
    cases h
    rfl

theorem binary_refines (operation : UInt256 → UInt256 → UInt256) (stack result : Stack)
    (h : binary operation stack = some result) :
    PureStack.binary operation stack.words = some result.words := by
  rcases stack with ⟨words, hBound⟩
  cases words with
  | nil => simp [binary] at h
  | cons top rest =>
    cases rest with
    | nil => simp [binary] at h
    | cons next tail =>
      simp only [binary, PureStack.binary] at h ⊢
      cases h
      rfl

theorem ternary_refines (operation : UInt256 → UInt256 → UInt256 → UInt256)
    (stack result : Stack) (h : ternary operation stack = some result) :
    PureStack.ternary operation stack.words = some result.words := by
  rcases stack with ⟨words, hBound⟩
  cases words with
  | nil => simp [ternary] at h
  | cons top rest =>
    cases rest with
    | nil => simp [ternary] at h
    | cons next tail =>
      cases tail with
      | nil => simp [ternary] at h
      | cons third rest =>
        simp only [ternary, PureStack.ternary] at h ⊢
        cases h
        rfl

/-- Every successful bounded word step agrees with the existing pure-stack model. -/
theorem stackExecute_refines (opcode : PureStack.Opcode) (stack result : Stack)
    (h : stackExecute opcode stack = some result) :
    PureStack.execute opcode stack.words = some result.words := by
  cases opcode <;> simp only [stackExecute, PureStack.execute] at h ⊢
  all_goals
    first
    | exact unary_refines _ _ _ h
    | exact binary_refines _ _ _ h
    | exact ternary_refines _ _ _ h

theorem stackExecute_output_bounded (opcode : PureStack.Opcode) (stack result : Stack)
    (_h : stackExecute opcode stack = some result) : result.words.length ≤ stackLimit :=
  result.bounded

/-- `EXP` after its fixed charge: atomically remove operands, then charge exponent bytes. -/
def executeExpAfterStatic (schedule : Schedule) (state : State) : Outcome :=
  match hWords : state.stack.words with
  | base :: exponent :: tail =>
    let hBound : (base :: exponent :: tail).length ≤ stackLimit := by
      have hBound := state.stack.bounded
      rw [hWords] at hBound
      exact hBound
    let popped : State :=
      { state with
        stack := ⟨tail, by
          simp only [List.length_cons] at hBound
          omega⟩ }
    match debitExecution (expDynamicCost schedule exponent) popped with
    | .outOfGas afterOutOfGas => .error .outOfGas afterOutOfGas
    | .paid afterDynamic =>
      .success
        { afterDynamic with
          stack := ⟨Word.exp base exponent :: tail, by
            simp only [List.length_cons] at hBound ⊢
            omega⟩ }
  | _ => .error .stackUnderflow state

/--
  Executes one already-selected pure-word opcode.  The program counter is
  advanced before every error path, just as the production dispatcher does.
-/
def execute (schedule : Schedule) (opcode : PureStack.Opcode) (state : State) : Outcome :=
  let dispatched := state.advancePc
  match debitExecution (staticCost schedule opcode) dispatched with
  | .outOfGas afterOutOfGas => .error .outOfGas afterOutOfGas
  | .paid afterStatic =>
    match opcode with
    | .exp => executeExpAfterStatic schedule afterStatic
    | _ =>
      match stackExecute opcode afterStatic.stack with
      | none => .error .stackUnderflow afterStatic
      | some finalStack => .success { afterStatic with stack := finalStack }

theorem execute_outcome_stack_bounded (schedule : Schedule) (opcode : PureStack.Opcode) (state : State) :
    (execute schedule opcode state).state.stack.words.length ≤ stackLimit :=
  (execute schedule opcode state).state.stack.bounded

theorem execute_static_outOfGas (schedule : Schedule) (opcode : PureStack.Opcode) (state : State)
    (h : state.gas.gasLeft < staticCost schedule opcode) :
    (execute schedule opcode state).error? = some .outOfGas ∧
    (execute schedule opcode state).state.gas.gasLeft = 0 ∧
    (execute schedule opcode state).state.stack.words = state.stack.words ∧
    (execute schedule opcode state).state.pc = state.pc + 1 := by
  simp [execute, State.advancePc, State.withGasLeft, debitExecution, Nat.not_le_of_gt h,
    Outcome.error?, Outcome.state]

theorem execute_exp_underflow (schedule : Schedule) (state : State)
    (hGas : schedule.expBase ≤ state.gas.gasLeft)
    (hStack : state.stack.words.length < 2) :
    (execute schedule .exp state).error? = some .stackUnderflow ∧
    (execute schedule .exp state).state.gas.gasLeft = state.gas.gasLeft - schedule.expBase ∧
    (execute schedule .exp state).state.stack.words = state.stack.words ∧
    (execute schedule .exp state).state.pc = state.pc + 1 := by
  rcases state with ⟨stack, gas, pc⟩
  rcases stack with ⟨words, hBound⟩
  cases words with
  | nil =>
    simp [execute, executeExpAfterStatic, State.advancePc, State.withGasLeft,
      debitExecution, staticCost, hGas, Outcome.error?, Outcome.state]
  | cons top tail =>
    cases tail with
    | nil =>
      simp [execute, executeExpAfterStatic, State.advancePc, State.withGasLeft,
        debitExecution, staticCost, hGas, Outcome.error?, Outcome.state]
    | cons next rest =>
      simp only [List.length_cons] at hStack
      omega

theorem execute_exp_dynamic_outOfGas (schedule : Schedule) (base exponent : UInt256)
    (tail : List UInt256) (hBound : (base :: exponent :: tail).length ≤ stackLimit)
    (gas : GasState) (pc : Nat)
    (hStatic : schedule.expBase ≤ gas.gasLeft)
    (hDynamic : gas.gasLeft - schedule.expBase < expDynamicCost schedule exponent) :
    let state : State := ⟨⟨base :: exponent :: tail, hBound⟩, gas, pc⟩
    (execute schedule .exp state).error? = some .outOfGas ∧
    (execute schedule .exp state).state.gas.gasLeft = 0 ∧
    (execute schedule .exp state).state.stack.words = tail ∧
    (execute schedule .exp state).state.pc = pc + 1 := by
  simp [execute, executeExpAfterStatic, State.advancePc, State.withGasLeft,
    debitExecution, staticCost, hStatic, Nat.not_le_of_gt hDynamic, Outcome.error?, Outcome.state]

/-- A deliberately wrong OOG debit, retained only for mutation tests. -/
def mutatedPreserveGasOnOutOfGas (cost : Nat) (state : State) : Debit :=
  if cost ≤ state.gas.gasLeft then
    .paid (state.withGasLeft (state.gas.gasLeft - cost))
  else
    .outOfGas state

/-- A deliberately wrong `EXP` order: dynamic gas before atomic operand removal. -/
def mutatedExpChargeBeforePop (schedule : Schedule) (state : State) : Outcome :=
  match hWords : state.stack.words with
  | base :: exponent :: tail =>
    match debitExecution (expDynamicCost schedule exponent) state with
    | .outOfGas afterOutOfGas => .error .outOfGas afterOutOfGas
    | .paid afterDynamic =>
      let hBound : (base :: exponent :: tail).length ≤ stackLimit := by
        have hBound := state.stack.bounded
        rw [hWords] at hBound
        exact hBound
      .success
        { afterDynamic with
          stack := ⟨Word.exp base exponent :: tail, by
            simp only [List.length_cons] at hBound ⊢
            omega⟩ }
  | _ => .error .stackUnderflow state

end PureStackExecution
end Evm
end Eip803x
