-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.Evm.StackRearrangement
import Eip803x.Generated.StackRearrangementOpcodeKernel

/-!
  Refinement boundary for the generated Amsterdam stack-rearrangement kernel.
  The generated file contains metadata and a theorem-free executable machine;
  this file is the proof-bearing layer.  It checks the complete 33 × 4 root
  coverage, then relates every generated opcode to the handwritten operation
  reference through explicit state and error observations.
-/

namespace Eip803x
namespace Evm
namespace Refinement
namespace StackRearrangementOpcode

open StackRearrangement

abbrev GeneratedOpcode := Eip803x.Generated.StackRearrangementOpcodeKernel.Opcode
abbrev GeneratedTable := Eip803x.Generated.StackRearrangementOpcodeKernel.DispatchTable

def rootCoverage : List (GeneratedOpcode × GeneratedTable) :=
  Generated.StackRearrangementOpcodeKernel.specializations.map fun specialization =>
    (specialization.opcode, specialization.dispatchTable)

def expectedDispatchTables : List GeneratedTable :=
  [.noTrace, .noTraceCancelable, .traced, .tracedCancelable]

theorem exact_opcode_count : Generated.StackRearrangementOpcodeKernel.allOpcodes.length = 33 := by
  native_decide

theorem exact_root_count : Generated.StackRearrangementOpcodeKernel.specializations.length = 132 := by native_decide

theorem exact_root_coverage :
  rootCoverage.length = 132 ∧
      (rootCoverage.eraseDups).length = 132 := by
  native_decide

theorem exact_root_cartesian :
    rootCoverage =
      Generated.StackRearrangementOpcodeKernel.allOpcodes.flatMap fun opcode =>
        expectedDispatchTables.map fun table => (opcode, table) := by
  native_decide

def word (value : Nat) : UInt256 := Word.ofNat value

def gas (value : Nat) : GasState :=
  { gasLeft := value
    stateReservoir := 0
    stateFromGasLeft := 0
    stateUsed := 0
    refundCounter := 0 }

def state (words : List UInt256) (gasLeft : Nat) : StackRearrangement.State :=
  { stack := MemoryStackControl.Stack.fromWords words
    gas := gas gasLeft
    pc := 11 }

structure Snapshot where
  words : List UInt256
  gasLeft : Nat
  pc : Nat
  error : Option StackRearrangement.Error
  deriving DecidableEq, Repr

def observe (outcome : StackRearrangement.Outcome) : Snapshot :=
  { words := (Outcome.state outcome).stack.words
    gasLeft := (Outcome.state outcome).gas.gasLeft
    pc := (Outcome.state outcome).pc
    error := Outcome.error? outcome }

def toGeneratedState (state : StackRearrangement.State) :
    Eip803x.Generated.StackRearrangementOpcodeKernel.State :=
  { stack := state.stack
    gas := state.gas
    pc := state.pc }

def generatedErrorToReference
    (reason : Eip803x.Generated.StackRearrangementOpcodeKernel.Error) : StackRearrangement.Error :=
  match reason with
  | .outOfGas => .outOfGas
  | .stackUnderflow => .stackUnderflow
  | .stackOverflow => .stackOverflow

def toReferenceState
    (state : Eip803x.Generated.StackRearrangementOpcodeKernel.State) : StackRearrangement.State :=
  { stack := state.stack
    gas := state.gas
    pc := state.pc }

def toReferenceOutcome
    (outcome : Eip803x.Generated.StackRearrangementOpcodeKernel.Outcome) :
    StackRearrangement.Outcome :=
  match outcome with
  | .success state => .success (toReferenceState state)
  | .error reason state => .error (generatedErrorToReference reason) (toReferenceState state)

def observeGenerated
    (outcome : Eip803x.Generated.StackRearrangementOpcodeKernel.Outcome) : Snapshot :=
  observe (toReferenceOutcome outcome)

def generatedOperationToReference
    : Eip803x.Generated.StackRearrangementOpcodeKernel.Operation → StackRearrangement.Operation
  | .pop => .pop
  | .dup depth => .dup depth
  | .swap depth => .swap depth

def referencePop (stack : MemoryStackControl.Stack) :
    Except Eip803x.Generated.StackRearrangementOpcodeKernel.Error MemoryStackControl.Stack :=
  match stack.pop with
  | none => .error .stackUnderflow
  | some (_, result) => .ok result

def referenceDuplicate (depth : Nat) (stack : MemoryStackControl.Stack) :
    Except Eip803x.Generated.StackRearrangementOpcodeKernel.Error MemoryStackControl.Stack :=
  match stack.duplicate depth with
  | .ok result => .ok result
  | .error .underflow => .error .stackUnderflow
  | .error .overflow => .error .stackOverflow

def referenceSwap (depth : Nat) (stack : MemoryStackControl.Stack) :
    Except Eip803x.Generated.StackRearrangementOpcodeKernel.Error MemoryStackControl.Stack :=
  match stack.swap depth with
  | none => .error .stackUnderflow
  | some result => .ok result

def referenceOperation (operation : StackRearrangement.Operation) (stack : MemoryStackControl.Stack) :
    Except Eip803x.Generated.StackRearrangementOpcodeKernel.Error MemoryStackControl.Stack :=
  match operation with
  | .pop => referencePop stack
  | .dup depth => referenceDuplicate depth stack
  | .swap depth => referenceSwap depth stack

def generatedResultToReference
    : Except Eip803x.Generated.StackRearrangementOpcodeKernel.Error MemoryStackControl.Stack →
      Except StackRearrangement.Error MemoryStackControl.Stack
  | .ok result => .ok result
  | .error reason => .error (generatedErrorToReference reason)

theorem referenceOperation_maps_to_handwritten
    (operation : StackRearrangement.Operation) (stack : MemoryStackControl.Stack) :
    generatedResultToReference (referenceOperation operation stack) =
      StackRearrangement.applyOperation operation stack := by
  cases operation with
  | pop =>
    cases hPop : stack.pop with
    | none =>
      simp [referenceOperation, referencePop, generatedResultToReference,
        generatedErrorToReference, StackRearrangement.applyOperation, hPop]
    | some result =>
      rcases result with ⟨value, result⟩
      simp [referenceOperation, referencePop, generatedResultToReference,
        StackRearrangement.applyOperation, hPop]
  | dup depth =>
    cases hDuplicate : stack.duplicate depth with
    | ok result =>
      simp [referenceOperation, referenceDuplicate, generatedResultToReference,
        StackRearrangement.applyOperation, hDuplicate]
    | error reason =>
      cases reason <;>
        simp [referenceOperation, referenceDuplicate, generatedResultToReference,
          generatedErrorToReference, StackRearrangement.applyOperation, hDuplicate]
  | swap depth =>
    cases hSwap : stack.swap depth <;>
      simp [referenceOperation, referenceSwap, generatedResultToReference,
        generatedErrorToReference, StackRearrangement.applyOperation, hSwap]

theorem generated_readAt_matches_list_get {α : Type} (index : Nat) (words : List α) :
    Eip803x.Generated.StackRearrangementOpcodeKernel.readAt? index words = words[index]? := by
  induction index generalizing words with
  | zero => cases words <;> rfl
  | succ index ih =>
    cases words with
    | nil => rfl
    | cons head tail => exact ih tail

theorem generated_replaceAt_matches_reference {α : Type} (index : Nat) (value : α)
    (words : List α) :
    Eip803x.Generated.StackRearrangementOpcodeKernel.replaceAt index value words =
      MemoryStackControl.Stack.replaceAt index value words := by
  induction index generalizing words with
  | zero => cases words <;> rfl
  | succ index ih =>
    cases words with
    | nil => rfl
    | cons head tail =>
      simp only [Eip803x.Generated.StackRearrangementOpcodeKernel.replaceAt,
        MemoryStackControl.Stack.replaceAt]
      rw [ih]

theorem fromWords_of_length_le (words : List UInt256)
    (hBound : words.length ≤ MemoryStackControl.stackLimit) :
    MemoryStackControl.Stack.fromWords words = ⟨words, hBound⟩ := by
  simp [MemoryStackControl.Stack.fromWords, List.take_of_length_le hBound]

theorem stack_push_is_fromWords
    (stack : MemoryStackControl.Stack) (value : UInt256) :
    MemoryStackControl.Stack.push stack value =
      if stack.words.length < MemoryStackControl.stackLimit then
        some (MemoryStackControl.Stack.fromWords (value :: stack.words))
      else
        none := by
  rcases stack with ⟨words, hBound⟩
  by_cases hRoom : words.length < MemoryStackControl.stackLimit
  · have hResult : (value :: words).length ≤ MemoryStackControl.stackLimit := by
      simp only [List.length_cons]
      omega
    simp only [MemoryStackControl.Stack.push, dif_pos hRoom]
    rw [fromWords_of_length_le (value :: words) hResult]
    simp only [hRoom, if_pos]
  · simp [MemoryStackControl.Stack.push, hRoom]

theorem generated_pop_stack_refines (stack : MemoryStackControl.Stack) :
    Eip803x.Generated.StackRearrangementOpcodeKernel.applyOperation .pop stack =
      referencePop stack := by
  rcases stack with ⟨words, hBound⟩
  cases words with
  | nil => rfl
  | cons value tail =>
    have hTail : tail.length ≤ MemoryStackControl.stackLimit := by
      simp only [List.length_cons] at hBound
      omega
    simp only [Eip803x.Generated.StackRearrangementOpcodeKernel.applyOperation,
      Eip803x.Generated.StackRearrangementOpcodeKernel.toStackResult,
      Eip803x.Generated.StackRearrangementOpcodeKernel.popWords,
      referencePop, MemoryStackControl.Stack.pop]
    rw [fromWords_of_length_le tail hTail]

theorem generated_duplicate_stack_refines (depth : Nat) (stack : MemoryStackControl.Stack) :
    Eip803x.Generated.StackRearrangementOpcodeKernel.applyOperation (.dup depth) stack =
      referenceDuplicate depth stack := by
  rcases stack with ⟨words, hBound⟩
  simp only [Eip803x.Generated.StackRearrangementOpcodeKernel.applyOperation,
    Eip803x.Generated.StackRearrangementOpcodeKernel.duplicateWords,
    Eip803x.Generated.StackRearrangementOpcodeKernel.toStackResult,
    referenceDuplicate, MemoryStackControl.Stack.duplicate]
  rw [generated_readAt_matches_list_get]
  by_cases hDepth : 0 < depth ∧ depth ≤ words.length
  · rw [if_pos hDepth, dif_pos hDepth]
    cases hRead : words[depth - 1]? with
    | none => rfl
    | some value =>
      simp only
      rw [stack_push_is_fromWords]
      by_cases hRoom : words.length < MemoryStackControl.stackLimit
      · have hGeneratedRoom : words.length <
            Eip803x.Generated.StackRearrangementOpcodeKernel.stackLimit := by
          simpa [Eip803x.Generated.StackRearrangementOpcodeKernel.stackLimit,
            MemoryStackControl.stackLimit] using hRoom
        simp [hRoom, hGeneratedRoom]
      · have hGeneratedFull : ¬words.length <
            Eip803x.Generated.StackRearrangementOpcodeKernel.stackLimit := by
          simpa [Eip803x.Generated.StackRearrangementOpcodeKernel.stackLimit,
            MemoryStackControl.stackLimit] using hRoom
        simp [hRoom, hGeneratedFull]
  · rw [if_neg hDepth, dif_neg hDepth]

theorem generated_swap_stack_refines (depth : Nat) (stack : MemoryStackControl.Stack) :
    Eip803x.Generated.StackRearrangementOpcodeKernel.applyOperation (.swap depth) stack =
      referenceSwap depth stack := by
  rcases stack with ⟨words, hBound⟩
  simp only [Eip803x.Generated.StackRearrangementOpcodeKernel.applyOperation,
    Eip803x.Generated.StackRearrangementOpcodeKernel.swapWords,
    Eip803x.Generated.StackRearrangementOpcodeKernel.toStackResult,
    referenceSwap, MemoryStackControl.Stack.swap]
  cases depth with
  | zero => rfl
  | succ depth =>
    cases words with
    | nil => rfl
    | cons top tail =>
      simp only [Nat.add_sub_cancel]
      rw [generated_readAt_matches_list_get]
      rw [generated_replaceAt_matches_reference]
      cases hRead : tail[depth]? with
      | none => rfl
      | some other =>
        simp only
        cases hReplace : MemoryStackControl.Stack.replaceAt depth top tail with
        | none => rfl
        | some changedTail => rfl

theorem generated_stack_operation_refines
    (operation : Eip803x.Generated.StackRearrangementOpcodeKernel.Operation)
    (stack : MemoryStackControl.Stack) :
    match operation with
    | .pop =>
      Eip803x.Generated.StackRearrangementOpcodeKernel.applyOperation operation stack =
        referencePop stack
    | .dup depth =>
      Eip803x.Generated.StackRearrangementOpcodeKernel.applyOperation operation stack =
        referenceDuplicate depth stack
    | .swap depth =>
      Eip803x.Generated.StackRearrangementOpcodeKernel.applyOperation operation stack =
        referenceSwap depth stack := by
  cases operation with
  | pop => exact generated_pop_stack_refines stack
  | dup depth => exact generated_duplicate_stack_refines depth stack
  | swap depth => exact generated_swap_stack_refines depth stack

theorem generated_operation_refines_handwritten
    (operation : Eip803x.Generated.StackRearrangementOpcodeKernel.Operation)
    (stack : MemoryStackControl.Stack) :
    generatedResultToReference
        (Eip803x.Generated.StackRearrangementOpcodeKernel.applyOperation operation stack) =
      StackRearrangement.applyOperation
        (generatedOperationToReference operation) stack := by
  cases operation with
  | pop =>
    rw [generated_pop_stack_refines]
    exact referenceOperation_maps_to_handwritten .pop stack
  | dup depth =>
    rw [generated_duplicate_stack_refines]
    exact referenceOperation_maps_to_handwritten (.dup depth) stack
  | swap depth =>
    rw [generated_swap_stack_refines]
    exact referenceOperation_maps_to_handwritten (.swap depth) stack

def PlansAgree
    (generated : Eip803x.Generated.StackRearrangementOpcodeKernel.Plan)
    (reference : StackRearrangement.Plan) : Prop :=
  generated.fixedGas = reference.fixedGas ∧
    generated.stackInputs = reference.stackInputs ∧
    generated.stackGrowth = reference.stackGrowth ∧
    generated.stackLimit = reference.stackLimit ∧
    generated.pcBeforeGas = reference.pcBeforeGas ∧
    generated.gasExhaustsOnFailure = reference.gasExhaustsOnFailure ∧
    generated.faultsAfterGas = reference.faultsAfterGas

def toReferencePlan
    (plan : Eip803x.Generated.StackRearrangementOpcodeKernel.Plan) : StackRearrangement.Plan :=
  { fixedGas := plan.fixedGas
    stackInputs := plan.stackInputs
    stackGrowth := plan.stackGrowth
    stackLimit := plan.stackLimit
    pcBeforeGas := plan.pcBeforeGas
    gasExhaustsOnFailure := plan.gasExhaustsOnFailure
    faultsAfterGas := plan.faultsAfterGas }

def toReferenceDebit
    (debit : Eip803x.Generated.StackRearrangementOpcodeKernel.Debit) :
    StackRearrangement.Debit :=
  match debit with
  | .paid state => .paid (toReferenceState state)
  | .outOfGas state => .outOfGas (toReferenceState state)

theorem generated_debit_refines
    (plan : Eip803x.Generated.StackRearrangementOpcodeKernel.Plan)
    (state : Eip803x.Generated.StackRearrangementOpcodeKernel.State) :
    toReferenceDebit (Eip803x.Generated.StackRearrangementOpcodeKernel.debit plan state) =
      StackRearrangement.debit (toReferencePlan plan) (toReferenceState state) := by
  by_cases hGas : plan.fixedGas ≤ state.gas.gasLeft
  · simp [Eip803x.Generated.StackRearrangementOpcodeKernel.debit,
      Eip803x.Generated.StackRearrangementOpcodeKernel.State.withGasLeft,
      StackRearrangement.debit, StackRearrangement.State.withGasLeft,
      toReferenceDebit, toReferencePlan, toReferenceState, hGas]
  · by_cases hExhausts : plan.gasExhaustsOnFailure = true <;>
      simp [Eip803x.Generated.StackRearrangementOpcodeKernel.debit,
        Eip803x.Generated.StackRearrangementOpcodeKernel.State.withGasLeft,
        StackRearrangement.debit, StackRearrangement.State.withGasLeft,
        toReferenceDebit, toReferencePlan, toReferenceState, hGas, hExhausts]

theorem generated_executePlanned_refines
    (operation : Eip803x.Generated.StackRearrangementOpcodeKernel.Operation)
    (plan : Eip803x.Generated.StackRearrangementOpcodeKernel.Plan)
    (state : StackRearrangement.State) :
    toReferenceOutcome
        (Eip803x.Generated.StackRearrangementOpcodeKernel.executePlanned
          operation plan (toGeneratedState state)) =
      StackRearrangement.executePlanned
        (generatedOperationToReference operation) (toReferencePlan plan) state := by
  rcases state with ⟨stack, gas, pc⟩
  unfold Eip803x.Generated.StackRearrangementOpcodeKernel.executePlanned
    StackRearrangement.executePlanned
  dsimp only [toGeneratedState, toReferenceState]
  simp only [Eip803x.Generated.StackRearrangementOpcodeKernel.State.advancePc,
    StackRearrangement.State.advancePc]
  have hDispatched :
      toReferenceState
          (if plan.pcBeforeGas = true then
            { stack := stack, gas := gas, pc := pc + 1 }
          else
            { stack := stack, gas := gas, pc := pc }) =
        (if (toReferencePlan plan).pcBeforeGas = true then
          { stack := stack, gas := gas, pc := pc + 1 }
        else
          { stack := stack, gas := gas, pc := pc }) := by
    by_cases hPc : plan.pcBeforeGas = true <;>
      simp [toReferencePlan, toReferenceState, hPc]
  generalize hDebit :
    Eip803x.Generated.StackRearrangementOpcodeKernel.debit plan
      (if plan.pcBeforeGas = true then
        { stack := stack, gas := gas, pc := pc + 1 }
      else
        { stack := stack, gas := gas, pc := pc }) = debit
  have hDebitRefines := generated_debit_refines plan
    (if plan.pcBeforeGas = true then
      { stack := stack, gas := gas, pc := pc + 1 }
    else
      { stack := stack, gas := gas, pc := pc })
  rw [hDebit] at hDebitRefines
  rw [← hDispatched]
  rw [← hDebitRefines]
  cases debit with
  | outOfGas afterOutOfGas =>
    rfl
  | paid afterGas =>
    simp only [toReferenceDebit]
    unfold StackRearrangement.executeAfterGas
    by_cases hFaults : plan.faultsAfterGas = false
    · have hReferenceFaults : (toReferencePlan plan).faultsAfterGas = false := by
        simpa [toReferencePlan] using hFaults
      simp only [if_pos hFaults, if_pos hReferenceFaults]
      dsimp only [toReferenceState]
      generalize hResult :
        Eip803x.Generated.StackRearrangementOpcodeKernel.applyOperation operation afterGas.stack = result
      have hResultRefines := generated_operation_refines_handwritten operation afterGas.stack
      rw [hResult] at hResultRefines
      cases result with
      | ok result =>
        simp only [generatedResultToReference] at hResultRefines
        rw [← hResultRefines]
        rfl
      | error reason =>
        cases reason <;>
          simp only [generatedResultToReference] at hResultRefines <;>
          rw [← hResultRefines] <;> rfl
    · have hReferenceFaults : ¬(toReferencePlan plan).faultsAfterGas = false := by
        simpa [toReferencePlan] using hFaults
      simp only [if_neg hFaults, if_neg hReferenceFaults]
      by_cases hUnderflow : afterGas.stack.words.length < plan.stackInputs
      · have hReferenceUnderflow :
            (toReferenceState afterGas).stack.words.length <
              (toReferencePlan plan).stackInputs := by
          simpa [toReferencePlan, toReferenceState] using hUnderflow
        simp only [if_pos hUnderflow, if_pos hReferenceUnderflow]
        dsimp only [toReferenceOutcome, generatedErrorToReference, toReferenceState]
      · have hReferenceUnderflow :
            ¬(toReferenceState afterGas).stack.words.length <
              (toReferencePlan plan).stackInputs := by
          simpa [toReferencePlan, toReferenceState] using hUnderflow
        simp only [if_neg hUnderflow, if_neg hReferenceUnderflow]
        by_cases hOverflow : plan.stackGrowth > 0 ∧
            afterGas.stack.words.length + plan.stackGrowth > plan.stackLimit
        · have hReferenceOverflow :
              (toReferencePlan plan).stackGrowth > 0 ∧
                (toReferenceState afterGas).stack.words.length +
                    (toReferencePlan plan).stackGrowth > (toReferencePlan plan).stackLimit := by
            simpa [toReferencePlan, toReferenceState] using hOverflow
          simp only [if_pos hOverflow, if_pos hReferenceOverflow]
          dsimp only [toReferenceOutcome, generatedErrorToReference, toReferenceState]
        · have hReferenceOverflow :
              ¬((toReferencePlan plan).stackGrowth > 0 ∧
                (toReferenceState afterGas).stack.words.length +
                    (toReferencePlan plan).stackGrowth > (toReferencePlan plan).stackLimit) := by
            simpa [toReferencePlan, toReferenceState] using hOverflow
          simp only [if_neg hOverflow, if_neg hReferenceOverflow]
          dsimp only [toReferenceState]
          generalize hResult :
            Eip803x.Generated.StackRearrangementOpcodeKernel.applyOperation operation afterGas.stack = result
          have hResultRefines := generated_operation_refines_handwritten operation afterGas.stack
          rw [hResult] at hResultRefines
          cases result with
          | ok result =>
            simp only [generatedResultToReference] at hResultRefines
            rw [← hResultRefines]
            rfl
          | error reason =>
            cases reason <;>
              simp only [generatedResultToReference] at hResultRefines <;>
              rw [← hResultRefines] <;> rfl

theorem generated_plan_refines_reference (opcode : GeneratedOpcode) :
    PlansAgree
      (Generated.StackRearrangementOpcodeKernel.plan opcode)
      (StackRearrangement.operationPlan
        (generatedOperationToReference (Generated.StackRearrangementOpcodeKernel.toOperation opcode))) := by
  unfold PlansAgree
  cases opcode <;> native_decide

theorem generated_execute_refines_reference
    (opcode : GeneratedOpcode) (state : StackRearrangement.State) :
    observeGenerated
        (Generated.StackRearrangementOpcodeKernel.execute opcode (toGeneratedState state)) =
      observe
        (StackRearrangement.execute
          (generatedOperationToReference (Generated.StackRearrangementOpcodeKernel.toOperation opcode)) state) := by
  have hPlan :
      toReferencePlan (Generated.StackRearrangementOpcodeKernel.plan opcode) =
        StackRearrangement.operationPlan
          (generatedOperationToReference (Generated.StackRearrangementOpcodeKernel.toOperation opcode)) := by
    cases opcode <;> native_decide
  change observe
      (toReferenceOutcome
        (Generated.StackRearrangementOpcodeKernel.executePlanned
          (Generated.StackRearrangementOpcodeKernel.toOperation opcode)
          (Generated.StackRearrangementOpcodeKernel.plan opcode)
          (toGeneratedState state))) =
    observe
      (StackRearrangement.executePlanned
        (generatedOperationToReference (Generated.StackRearrangementOpcodeKernel.toOperation opcode))
        (StackRearrangement.operationPlan
          (generatedOperationToReference (Generated.StackRearrangementOpcodeKernel.toOperation opcode)))
        state)
  rw [generated_executePlanned_refines, hPlan]

structure Vector where
  opcode : GeneratedOpcode
  initial : StackRearrangement.State
  expected : Snapshot
  deriving Repr

def passes (vector : Vector) : Bool :=
  decide (observeGenerated
    (Generated.StackRearrangementOpcodeKernel.execute vector.opcode (toGeneratedState vector.initial)) =
      vector.expected)

def rangeWords (count : Nat) : List UInt256 :=
  (List.range count).map fun value => word (value + 1)

def dupOpcode : Nat → GeneratedOpcode
  | 1 => .dup1
  | 2 => .dup2
  | 3 => .dup3
  | 4 => .dup4
  | 5 => .dup5
  | 6 => .dup6
  | 7 => .dup7
  | 8 => .dup8
  | 9 => .dup9
  | 10 => .dup10
  | 11 => .dup11
  | 12 => .dup12
  | 13 => .dup13
  | 14 => .dup14
  | 15 => .dup15
  | 16 => .dup16
  | _ => .dup1

def swapOpcode : Nat → GeneratedOpcode
  | 1 => .swap1
  | 2 => .swap2
  | 3 => .swap3
  | 4 => .swap4
  | 5 => .swap5
  | 6 => .swap6
  | 7 => .swap7
  | 8 => .swap8
  | 9 => .swap9
  | 10 => .swap10
  | 11 => .swap11
  | 12 => .swap12
  | 13 => .swap13
  | 14 => .swap14
  | 15 => .swap15
  | 16 => .swap16
  | _ => .swap1

def popVector : Vector :=
  { opcode := .pop
    initial := state [word 99] 10
    expected :=
      { words := []
        gasLeft := 8
        pc := 12
        error := none } }

def dupVector (depth : Nat) : Vector :=
  let values := rangeWords depth
  { opcode := dupOpcode depth
    initial := state values 10
    expected :=
      { words := word depth :: values
        gasLeft := 7
        pc := 12
        error := none } }

def swapVector (depth : Nat) : Vector :=
  let tail := rangeWords depth
  { opcode := swapOpcode depth
    initial := state (word 0 :: tail) 10
    expected :=
      { words := word depth :: tail.dropLast ++ [word 0]
        gasLeft := 7
        pc := 12
        error := none } }

def successVectors : List Vector :=
  [popVector] ++
    (List.range 16).map (fun value => dupVector (value + 1)) ++
    (List.range 16).map (fun value => swapVector (value + 1))

theorem every_opcode_has_exact_stack_result : successVectors.all passes = true := by
  native_decide

def faultVectors : List Vector :=
  [ { opcode := .pop
      initial := state [] 2
      expected :=
        { words := []
          gasLeft := 0
          pc := 12
          error := some .stackUnderflow } }
  , { opcode := .dup1
      initial := state [] 3
      expected :=
        { words := []
          gasLeft := 0
          pc := 12
          error := some .stackUnderflow } }
  , { opcode := .dup16
      initial := state (rangeWords 15) 10
      expected :=
        { words := rangeWords 15
          gasLeft := 7
          pc := 12
          error := some .stackUnderflow } }
  , { opcode := .swap16
      initial := state (rangeWords 16) 10
      expected :=
        { words := rangeWords 16
          gasLeft := 7
          pc := 12
          error := some .stackUnderflow } }
  , { opcode := .dup1
      initial := state (List.replicate MemoryStackControl.stackLimit (word 0)) 10
      expected :=
        { words := List.replicate MemoryStackControl.stackLimit (word 0)
          gasLeft := 7
          pc := 12
          error := some .stackOverflow } }
  , { opcode := .pop
      initial := state [word 99] 1
      expected :=
        { words := [word 99]
          gasLeft := 0
          pc := 12
          error := some .outOfGas } }
  , { opcode := .dup16
      initial := state (rangeWords 16) 2
      expected :=
        { words := rangeWords 16
          gasLeft := 0
          pc := 12
          error := some .outOfGas } } ]

theorem gas_faults_and_stack_bounds : faultVectors.all passes = true := by
  native_decide

def wrongPc (state : StackRearrangement.State) : StackRearrangement.Outcome :=
  StackRearrangement.executePlanned
    .pop
    { fixedGas := 2
      stackInputs := 1
      stackGrowth := 0
      stackLimit := MemoryStackControl.stackLimit
      pcBeforeGas := false
      gasExhaustsOnFailure := true
      faultsAfterGas := true }
    state

def wrongGas (state : StackRearrangement.State) : StackRearrangement.Outcome :=
  StackRearrangement.executePlanned
    .pop
    { fixedGas := 3
      stackInputs := 1
      stackGrowth := 0
      stackLimit := MemoryStackControl.stackLimit
      pcBeforeGas := true
      gasExhaustsOnFailure := true
      faultsAfterGas := true }
    state

def wrongFaultGas (state : StackRearrangement.State) : StackRearrangement.Outcome :=
  StackRearrangement.executePlanned
    .pop
    { fixedGas := 2
      stackInputs := 1
      stackGrowth := 0
      stackLimit := MemoryStackControl.stackLimit
      pcBeforeGas := true
      gasExhaustsOnFailure := false
      faultsAfterGas := true }
    state

def wrongStack (state : StackRearrangement.State) : StackRearrangement.Outcome :=
  StackRearrangement.executePlanned
    .pop
    (StackRearrangement.operationPlan .pop)
    state

theorem semantic_mutation_checks :
    observe (wrongPc (state [word 7] 10)) ≠ observe (StackRearrangement.execute .pop (state [word 7] 10)) ∧
    observe (wrongGas (state [word 7] 10)) ≠ observe (StackRearrangement.execute .pop (state [word 7] 10)) ∧
    observe (wrongFaultGas (state [word 7] 1)) ≠ observe (StackRearrangement.execute .pop (state [word 7] 1)) ∧
    observe (wrongStack (state [word 7, word 8] 10)) ≠
      observe (StackRearrangement.execute (.dup 1) (state [word 7, word 8] 10)) := by
  native_decide

end StackRearrangementOpcode
end Refinement
end Evm
end Eip803x
