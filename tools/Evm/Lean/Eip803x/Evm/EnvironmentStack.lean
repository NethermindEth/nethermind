-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.Evm.Environment
import Eip803x.Evm.MemoryStackControl

namespace Eip803x
namespace Evm
namespace EnvironmentStack

open Word
open MemoryStackControl

inductive Status where
  | ok
  | stackUnderflow
  | stackOverflow
  | badInstruction
  deriving DecidableEq, Repr

structure Outcome where
  status : Status
  stack : Stack
  deriving Repr

def pushWord (stack : Stack) (value : UInt256) : Outcome :=
  match stack.push value with
  | some result => { status := .ok, stack := result }
  | none => { status := .stackOverflow, stack }

def applyValueResult (stack : Stack) : Environment.Result → Outcome
  | .word value => pushWord stack value
  | .stackUnderflow => { status := .stackUnderflow, stack }
  | .badInstruction => { status := .badInstruction, stack }

def executeIndexed (context : Environment.ValidContext) (opcode : Environment.Opcode)
    (stack : Stack) : Outcome :=
  match stack.pop with
  | none => { status := .stackUnderflow, stack }
  | some (argument, tail) =>
    applyValueResult tail (Environment.execute context opcode (some argument))

/--
Composes context-value selection with the bounded top-first stack. BLOCKHASH and
BLOBHASH consume their query before pushing the answer; every other covered
opcode pushes one word. Gas, PC, fork dispatch, and production representation
remain separate refinement obligations.
-/
def execute (context : Environment.ValidContext) (opcode : Environment.Opcode)
    (stack : Stack) : Outcome :=
  match opcode with
  | .blockhash | .blobhash => executeIndexed context opcode stack
  | _ => applyValueResult stack (Environment.execute context opcode)

theorem outcome_stack_is_bounded (outcome : Outcome) :
    outcome.stack.words.length ≤ stackLimit := outcome.stack.bounded

theorem indexed_empty_underflows (context : Environment.ValidContext)
    (opcode : Environment.Opcode) :
    executeIndexed context opcode Stack.empty =
      { status := .stackUnderflow, stack := Stack.empty } := by
  rfl

theorem popped_tail_has_room (stack tail : Stack) (argument : UInt256)
    (hPop : stack.pop = some (argument, tail)) :
    tail.words.length < stackLimit :=
  Stack.popped_tail_has_room stack tail argument hPop

theorem indexed_word_replaces_argument
    (context : Environment.ValidContext) (opcode : Environment.Opcode)
    (stack tail : Stack) (argument value : UInt256)
    (hPop : stack.pop = some (argument, tail))
    (hValue : Environment.execute context opcode (some argument) = .word value) :
    let outcome := executeIndexed context opcode stack
    outcome.status = .ok ∧ outcome.stack.words = value :: tail.words := by
  have hRoom := popped_tail_has_room stack tail argument hPop
  simp [executeIndexed, hPop, applyValueResult, hValue, pushWord, Stack.push, hRoom]

theorem bad_instruction_preserves_stack (stack : Stack) :
    applyValueResult stack .badInstruction =
      { status := .badInstruction, stack } := by
  rfl

theorem push_to_full_stack_overflows (value : UInt256) :
    pushWord Stack.full value = { status := .stackOverflow, stack := Stack.full } := by
  have h : ¬ Stack.full.words.length < stackLimit := by native_decide
  simp [pushWord, Stack.push, h]

/-!
The definitions below are the independent operational projection used by the
environment-opcode refinement.  They deliberately model only the production
state visible at the opcode boundary: gas, PC, stack, and terminal status.
Dispatch tracing/cancellation callbacks and the implementation of context
providers remain composition obligations outside this projection.
-/

inductive DispatchTable where
  | noTrace
  | noTraceCancelable
  | traced
  | tracedCancelable
  deriving DecidableEq, Repr

def fixedGas : Environment.Opcode → Nat
  | .blockhash => 20
  | .selfbalance => 5
  | .blobhash => 3
  | _ => 2

def semanticPops : Environment.Opcode → Nat
  | .blockhash | .blobhash => 1
  | _ => 0

def contextAvailable (context : Environment.Context) : Environment.Opcode → Bool
  | .blobbasefee => context.excessBlobGas.isSome
  | .slotnum => context.slotNumber.isSome
  | _ => true

structure MachineState where
  gasLeft : Nat
  pc : Nat
  stack : Stack
  deriving Repr

inductive MachineStatus where
  | ok
  | outOfGas
  | stackUnderflow
  | stackOverflow
  | badInstruction
  deriving DecidableEq, Repr

structure MachineOutcome where
  status : MachineStatus
  state : MachineState
  deriving Repr

def withGas (state : MachineState) (gas : Nat) : MachineState :=
  { state with gasLeft := gas }

def advancePc (state : MachineState) : MachineState :=
  { state with pc := state.pc + 1 }

def applyValueResultToState (state : MachineState) : Environment.Result → MachineOutcome
  | .stackUnderflow => { status := .stackUnderflow, state }
  | .badInstruction => { status := .badInstruction, state }
  | .word value =>
    match state.stack.push value with
    | some stack => { status := .ok, state := { state with stack } }
    | none => { status := .stackOverflow, state }

def executePaidState (context : Environment.ValidContext) (opcode : Environment.Opcode)
    (state : MachineState) : MachineOutcome :=
  if semanticPops opcode = 1 then
    match state.stack.pop with
    | none => { status := .stackUnderflow, state }
    | some (argument, stack) =>
      applyValueResultToState { state with stack }
        (Environment.execute context opcode (some argument))
  else
    applyValueResultToState state (Environment.execute context opcode)

def executeState (context : Environment.ValidContext) (opcode : Environment.Opcode)
    (state : MachineState) : MachineOutcome :=
  let advanced := advancePc state
  if !contextAvailable context.value opcode then
    { status := .badInstruction, state := advanced }
  else if fixedGas opcode ≤ advanced.gasLeft then
    executePaidState context opcode (withGas advanced (advanced.gasLeft - fixedGas opcode))
  else
    { status := .outOfGas, state := withGas advanced 0 }

/--
The four Amsterdam tables select different trace/cancellation wrappers around
the same environment instruction transition.  Callback effects are excluded
from this operational projection and are not erased from the production-route
claim made by the extractor.
-/
def executeAmsterdam (table : DispatchTable) (context : Environment.ValidContext)
    (opcode : Environment.Opcode) (state : MachineState) : MachineOutcome :=
  match table with
  | .noTrace => executeState context opcode state
  | .noTraceCancelable => executeState context opcode state
  | .traced => executeState context opcode state
  | .tracedCancelable => executeState context opcode state

end EnvironmentStack
end Evm
end Eip803x
