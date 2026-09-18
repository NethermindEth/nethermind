-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.Gas
import Eip803x.Evm.MemoryStackControl
import Eip803x.Evm.TransientStorage

namespace Eip803x
namespace Evm
namespace TransientStorageStack

open GasMachine
open MemoryStackControl
open TransientStorage

structure MachineState where
  gas : GasState
  stack : Stack
  transient : Frame
  deriving Repr

inductive Status where
  | ok
  | outOfGas
  | stackUnderflow
  | stackOverflow
  | staticCallViolation
  deriving DecidableEq, Repr

structure Outcome where
  status : Status
  state : MachineState
  deriving Repr

def withGas (state : MachineState) (gas : GasState) : MachineState :=
  { state with gas }

/-- Production's failed `UpdateGas` burns the remaining execution gas. -/
def exhaustExecutionGas (state : MachineState) : MachineState :=
  { state with gas := { state.gas with gasLeft := 0 } }

/--
TLOAD charges its fixed execution cost before reading the key, then replaces
that key with the value for `(executing account, key)`.
-/
def executeTLoad (schedule : GasSchedule) (address : UInt256) (state : MachineState) : Outcome :=
  match chargeExecution schedule.warmAccess state.gas with
  | .error _ => { status := .outOfGas, state := exhaustExecutionGas state }
  | .ok gasAfter =>
    let charged := withGas state gasAfter
    match state.stack.pop with
    | none => { status := .stackUnderflow, state := charged }
    | some (key, tail) =>
      let value := TransientStorage.load state.transient address key
      match tail.push value with
      | none => { status := .stackOverflow, state := { charged with stack := tail } }
      | some stackAfter => { status := .ok, state := { charged with stack := stackAfter } }

/--
TSTORE rejects static context before charging gas. Otherwise it charges first,
pops key then value, and only then mutates transient storage.
-/
def executeTStore (schedule : GasSchedule) (isStatic : Bool) (address : UInt256)
    (state : MachineState) : Outcome :=
  if isStatic then
    { status := .staticCallViolation, state }
  else
    match chargeExecution schedule.warmAccess state.gas with
    | .error _ => { status := .outOfGas, state := exhaustExecutionGas state }
    | .ok gasAfter =>
      let charged := withGas state gasAfter
      match state.stack.pop with
      | none => { status := .stackUnderflow, state := charged }
      | some (key, afterKey) =>
        match afterKey.pop with
        | none => { status := .stackUnderflow, state := { charged with stack := afterKey } }
        | some (value, stackAfter) =>
          { status := .ok
            state :=
              { charged with
                stack := stackAfter
                transient := TransientStorage.store state.transient address key value } }

theorem tload_out_of_gas_zeros_execution_gas (schedule : GasSchedule) (address : UInt256)
    (state : MachineState) (h : state.gas.gasLeft < schedule.warmAccess) :
    executeTLoad schedule address state =
      { status := .outOfGas, state := exhaustExecutionGas state } := by
  simp [executeTLoad, chargeExecution, Nat.not_le_of_lt h]

theorem tstore_static_preserves_state (schedule : GasSchedule) (address : UInt256)
    (state : MachineState) :
    executeTStore schedule true address state = { status := .staticCallViolation, state } := by
  rfl

theorem tstore_out_of_gas_zeros_execution_gas (schedule : GasSchedule) (address : UInt256)
    (state : MachineState) (h : state.gas.gasLeft < schedule.warmAccess) :
    executeTStore schedule false address state =
      { status := .outOfGas, state := exhaustExecutionGas state } := by
  simp [executeTStore, chargeExecution, Nat.not_le_of_lt h]

theorem exhaust_preserves_nonexecution_gas (state : MachineState) :
    (exhaustExecutionGas state).gas.stateReservoir = state.gas.stateReservoir ∧
      (exhaustExecutionGas state).gas.stateFromGasLeft = state.gas.stateFromGasLeft ∧
      (exhaustExecutionGas state).gas.stateUsed = state.gas.stateUsed ∧
      (exhaustExecutionGas state).gas.refundCounter = state.gas.refundCounter := by
  simp [exhaustExecutionGas]

theorem tstore_success (schedule : GasSchedule) (address key value : UInt256)
    (state : MachineState) (afterKey stackAfter : Stack)
    (hGas : schedule.warmAccess ≤ state.gas.gasLeft)
    (hKey : state.stack.pop = some (key, afterKey))
    (hValue : afterKey.pop = some (value, stackAfter)) :
    executeTStore schedule false address state =
      { status := .ok
        state :=
          { state with
            gas := { state.gas with gasLeft := state.gas.gasLeft - schedule.warmAccess }
            stack := stackAfter
            transient := TransientStorage.store state.transient address key value } } := by
  simp [executeTStore, chargeExecution, hGas, hKey, hValue, withGas]

theorem tload_success (schedule : GasSchedule) (address key value : UInt256)
    (state : MachineState) (tail : Stack)
    (hGas : schedule.warmAccess ≤ state.gas.gasLeft)
    (hKey : state.stack.pop = some (key, tail))
    (hValue : TransientStorage.load state.transient address key = value) :
    let outcome := executeTLoad schedule address state
    outcome.status = .ok ∧
      outcome.state.gas.gasLeft = state.gas.gasLeft - schedule.warmAccess ∧
      outcome.state.stack.words = value :: tail.words ∧
      outcome.state.transient = state.transient := by
  have hRoom := Stack.popped_tail_has_room state.stack tail key hKey
  simp [executeTLoad, chargeExecution, hGas, hKey, hValue, Stack.push, hRoom, withGas]

theorem tload_cannot_overflow (schedule : GasSchedule) (address : UInt256)
    (state : MachineState) :
    (executeTLoad schedule address state).status ≠ .stackOverflow := by
  unfold executeTLoad
  cases hCharge : chargeExecution schedule.warmAccess state.gas with
  | error fault => simp
  | ok gasAfter =>
      cases hPop : state.stack.pop with
      | none => simp
      | some popped =>
          rcases popped with ⟨key, tail⟩
          have hRoom := Stack.popped_tail_has_room state.stack tail key hPop
          simp [Stack.push, hRoom]

theorem tstore_successful_value_is_readable (schedule : GasSchedule) (address key value : UInt256)
    (state : MachineState) (afterKey stackAfter : Stack)
    (hGas : schedule.warmAccess ≤ state.gas.gasLeft)
    (hKey : state.stack.pop = some (key, afterKey))
    (hValue : afterKey.pop = some (value, stackAfter)) :
    let outcome := executeTStore schedule false address state
    TransientStorage.load outcome.state.transient address key = value := by
  rw [tstore_success schedule address key value state afterKey stackAfter hGas hKey hValue]
  exact TransientStorage.load_after_store state.transient address key value

theorem tload_underflow_charges_before_stack_check (schedule : GasSchedule) (address : UInt256)
    (state : MachineState) (hGas : schedule.warmAccess ≤ state.gas.gasLeft)
    (hEmpty : state.stack.words = []) :
    (executeTLoad schedule address state).status = .stackUnderflow ∧
      (executeTLoad schedule address state).state.gas.gasLeft =
        state.gas.gasLeft - schedule.warmAccess := by
  rcases state with ⟨gas, stack, transient⟩
  rcases stack with ⟨words, bounded⟩
  simp only at hEmpty
  subst words
  simp [executeTLoad, chargeExecution, hGas, Stack.pop, withGas]

theorem tstore_static_precedes_out_of_gas (schedule : GasSchedule) (address : UInt256)
    (state : MachineState) (_h : state.gas.gasLeft < schedule.warmAccess) :
    (executeTStore schedule true address state).status = .staticCallViolation := by
  rfl

end TransientStorageStack
end Evm
end Eip803x
