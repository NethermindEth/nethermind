-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.Evm.TransientStorageStack

namespace Eip803x
namespace Evm
namespace TransientStorageStackVectors

open Word
open MemoryStackControl
open TransientStorage
open TransientStorageStack

def gas (left : Nat) : GasState :=
  { gasLeft := left
    stateReservoir := 17
    stateFromGasLeft := 3
    stateUsed := 4
    refundCounter := 5 }

def stackOf (words : List UInt256) (h : words.length ≤ stackLimit := by native_decide) : Stack :=
  ⟨words, h⟩

def address : UInt256 := Word.ofNat 0xaa
def key : UInt256 := Word.ofNat 1
def value : UInt256 := Word.ofNat 8
def schedule : GasSchedule := GasSchedule.amsterdam
def exactCost : Nat := schedule.warmAccess
def oneShort : Nat := exactCost - 1

def initial (left : Nat) (words : List UInt256)
    (h : words.length ≤ stackLimit := by native_decide) : MachineState :=
  { gas := gas left
    stack := stackOf words h
    transient := enterFrame [] }

example : executeTLoad schedule address (initial oneShort [key]) =
    { status := .outOfGas, state := exhaustExecutionGas (initial oneShort [key]) } := by
  exact tload_out_of_gas_zeros_execution_gas schedule address
    (initial oneShort [key]) (by native_decide)

example :
    let outcome := executeTLoad schedule address (initial exactCost [])
    outcome.status = .stackUnderflow ∧ outcome.state.gas.gasLeft = 0 := by
  native_decide

example :
    let seeded :=
      { initial exactCost [key] with
        transient := TransientStorage.store (enterFrame []) address key value }
    let outcome := executeTLoad schedule address seeded
    outcome.status = .ok ∧ outcome.state.stack.words = [value] ∧
      outcome.state.gas.gasLeft = 0 := by
  native_decide

example :
    executeTStore schedule true address (initial 0 [key, value]) =
      { status := .staticCallViolation, state := initial 0 [key, value] } := by
  exact tstore_static_preserves_state schedule address (initial 0 [key, value])

example :
    executeTStore schedule false address (initial oneShort [key, value]) =
      { status := .outOfGas,
        state := exhaustExecutionGas (initial oneShort [key, value]) } := by
  exact tstore_out_of_gas_zeros_execution_gas schedule address
    (initial oneShort [key, value]) (by native_decide)

example :
    let outcome := executeTStore schedule false address (initial exactCost [])
    outcome.status = .stackUnderflow ∧ outcome.state.gas.gasLeft = 0 ∧
      outcome.state.stack.words = [] := by
  native_decide

example :
    let outcome := executeTStore schedule false address (initial exactCost [key])
    outcome.status = .stackUnderflow ∧ outcome.state.gas.gasLeft = 0 ∧
      outcome.state.stack.words = [] := by
  native_decide

example :
    let outcome := executeTStore schedule false address (initial exactCost [key, value])
    outcome.status = .ok ∧ outcome.state.gas.gasLeft = 0 ∧
      outcome.state.stack.words = [] ∧
      TransientStorage.load outcome.state.transient address key = value := by
  native_decide

/-- Mutation sentinel: charging before the static check would report OOG here. -/
def mutatedTStoreChargeFirst (schedule : GasSchedule) (isStatic : Bool) (_address : UInt256)
    (state : MachineState) : TransientStorageStack.Status :=
  match GasMachine.chargeExecution schedule.warmAccess state.gas with
  | .error _ => .outOfGas
  | .ok _ => if isStatic then .staticCallViolation else .ok

example :
    mutatedTStoreChargeFirst schedule true address (initial 0 [key, value]) ≠
      (executeTStore schedule true address (initial 0 [key, value])).status := by
  native_decide

/-- Mutation sentinel: value/key reversal writes a different cell and value. -/
example :
    let outcome := executeTStore schedule false address (initial exactCost [key, value])
    let reversed := TransientStorage.store (enterFrame []) address value key
    TransientStorage.load outcome.state.transient address key ≠
      TransientStorage.load reversed address key := by
  native_decide

end TransientStorageStackVectors
end Evm
end Eip803x
