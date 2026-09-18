-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.Evm.PersistentStorage

namespace Eip803x
namespace Evm
namespace PersistentStorageVectors

open GasMachine
open MemoryStackControl
open PersistentStorage
open Word

def word (value : Nat) : UInt256 := Word.ofNat value

def address : UInt256 := word 0xaa
def otherAddress : UInt256 := word 0xbb
def key : UInt256 := word 1
def otherKey : UInt256 := word 2
def zero : UInt256 := Word.zero
def five : UInt256 := word 5
def seven : UInt256 := word 7
def nine : UInt256 := word 9

def cell : Cell := { address, key }
def otherCell : Cell := { address := otherAddress, key := otherKey }

def schedule : Schedule := Schedule.amsterdam
def stateCost : Nat := schedule.gas.storageSetGas

def stackOf (words : List UInt256) (h : words.length ≤ stackLimit := by native_decide) : Stack :=
  ⟨words, h⟩

def gas (left reservoir fromLeft used : Nat) (refund : Int := 0) : GasState :=
  { gasLeft := left
    stateReservoir := reservoir
    stateFromGasLeft := fromLeft
    stateUsed := used
    refundCounter := refund }

def store (original current : UInt256) : Store :=
  [(cell, { original, current }),
    (otherCell, { original := seven, current := nine })]

def initial (left : Nat) (words : List UInt256) (original current : UInt256)
    (reservoir : Nat := 0) (fromLeft : Nat := 0) (used : Nat := 0)
    (floor : Nat := 0) (refund : Int := 0) (warmCells : List Cell := [])
    (h : words.length ≤ stackLimit := by native_decide) : MachineState :=
  { gas := gas left reservoir fromLeft used refund
    stateGasFloor := floor
    stateGasRefundAdvanced := 0
    stack := stackOf words h
    storage := store original current
    warmCells
    executingAccount := address
    events := [] }

example : schedule.gas.coldStorageAccess = 2100 := by native_decide
example : schedule.gas.warmAccess = 100 := by native_decide
example : schedule.gas.storageWrite = 10000 := by native_decide
example : schedule.gas.storageClearRefund = 11616 := by native_decide
example : stateCost = 97920 := by native_decide
example : schedule.sstoreStipend = 2300 := by native_decide

/-! SLOAD boundaries and order. -/

example :
    let outcome := executeSLoad schedule (initial 2099 [key] zero five)
    outcome.status = .outOfGas ∧ outcome.state.gas.gasLeft = 0 ∧
      outcome.state.stack.words = [] ∧ outcome.state.warmCells = [] ∧
      outcome.state.storage = store zero five ∧
      outcome.state.events = [.sloadBaseCharged 0, .keyPopped key] := by
  native_decide

example :
    let outcome := executeSLoad schedule (initial 2100 [key] zero five)
    outcome.status = .ok ∧ outcome.state.gas.gasLeft = 0 ∧
      outcome.state.stack.words = [five] ∧ cell ∈ outcome.state.warmCells ∧
      outcome.state.events =
        [.sloadBaseCharged 0, .keyPopped key, .accessCharged cell .cold 2100,
          .currentRead cell five, .valuePushed five] := by
  native_decide

example :
    let outcome := executeSLoad schedule (initial 100 [key] zero five (warmCells := [cell]))
    outcome.status = .ok ∧ outcome.state.gas.gasLeft = 0 ∧
      outcome.state.stack.words = [five] ∧ outcome.state.warmCells = [cell] ∧
      outcome.state.events =
        [.sloadBaseCharged 0, .keyPopped key, .accessCharged cell .warm 100,
          .currentRead cell five, .valuePushed five] := by
  native_decide

/-- A warm access one gas short consumes the key, burns gas, and performs no read or push. -/
example :
    let before := initial 99 [key] zero five (warmCells := [cell])
    let outcome := executeSLoad schedule before
    outcome.status = .outOfGas ∧ outcome.state.gas.gasLeft = 0 ∧
      outcome.state.stack.words = [] ∧ outcome.state.warmCells = [cell] ∧
      outcome.state.storage = before.storage ∧
      outcome.state.events = [.sloadBaseCharged 0, .keyPopped key] := by
  native_decide

example :
    let outcome := executeSLoad schedule (initial 55 [] zero five)
    outcome.status = .stackUnderflow ∧ outcome.state.gas.gasLeft = 55 ∧
      outcome.state.events = [.sloadBaseCharged 0] := by
  native_decide

def nonzeroSloadBase : Schedule := { schedule with sloadBase := 3 }

/-- Mutation sentinel: the base charge fails before the key can be popped. -/
example :
    let before := initial 2 [key] zero five
    let outcome := executeSLoad nonzeroSloadBase before
    outcome.status = .outOfGas ∧ outcome.state.gas.gasLeft = 0 ∧
      outcome.state.stack.words = [key] ∧ outcome.state.events = [] := by
  native_decide

/-! SSTORE preconditions, partial stack mutation, and access ordering. -/

example :
    executeSStore schedule true (initial 0 [key, five] zero zero) =
      { status := .staticCallViolation, state := initial 0 [key, five] zero zero } := by
  exact sstore_static_preserves_state schedule _

/-- The strict sentry is `gas_left > 2300`; rejection does not debit handler-local gas. -/
example :
    let before := initial 2300 [key, five] zero zero
    let outcome := executeSStore schedule false before
    outcome.status = .outOfGas ∧ outcome.state.gas.gasLeft = 2300 ∧
      outcome.state.stack.words = [key, five] ∧ outcome.state.events = [] ∧
      outcome.state.storage = before.storage := by
  native_decide

example :
    let outcome := executeSStore schedule false (initial 2301 [] zero zero)
    outcome.status = .stackUnderflow ∧ outcome.state.gas.gasLeft = 2301 ∧
      outcome.state.stack.words = [] ∧ outcome.state.events = [] := by
  native_decide

/-- The two production pops are individually atomic: second-pop underflow keeps the key consumed. -/
example :
    let outcome := executeSStore schedule false (initial 2301 [key] zero zero)
    outcome.status = .stackUnderflow ∧ outcome.state.gas.gasLeft = 2301 ∧
      outcome.state.stack.words = [] ∧ outcome.state.events = [.keyPopped key] := by
  native_decide

def expensiveColdSchedule : Schedule :=
  { schedule with gas := { schedule.gas with coldStorageAccess := 3000 } }

/-- Access OOG occurs after both pops but before warming or the first current-value read. -/
example :
    let before := initial 2999 [key, five] zero zero
    let outcome := executeSStore expensiveColdSchedule false before
    outcome.status = .outOfGas ∧ outcome.state.gas.gasLeft = 0 ∧
      outcome.state.stack.words = [] ∧ outcome.state.warmCells = [] ∧
      outcome.state.storage = before.storage ∧
      outcome.state.events = [.keyPopped key, .valuePopped five] := by
  native_decide

example :
    let outcome := executeSStore expensiveColdSchedule false
      (initial 3000 [key, five] zero five)
    outcome.status = .ok ∧ outcome.state.gas.gasLeft = 0 ∧
      cell ∈ outcome.state.warmCells ∧ outcome.state.storage = store zero five ∧
      outcome.state.events =
        [.keyPopped key, .valuePopped five, .accessCharged cell .cold 3000,
          .currentRead cell five, .netMeteredNoOp] := by
  native_decide

/-! Complete post-access charge/refund/state cases at the handler boundary. -/

/-- Cold zero-to-nonzero creation charges access, execution write, then state gas. -/
example :
    let outcome := executeSStore schedule false
      (initial 12100 [key, five] zero zero stateCost)
    outcome.status = .ok ∧ outcome.state.gas.gasLeft = 0 ∧
      outcome.state.gas.stateReservoir = 0 ∧ outcome.state.gas.stateUsed = stateCost ∧
      outcome.state.gas.refundCounter = 0 ∧
      readOriginal outcome.state.storage cell = zero ∧
      readCurrent outcome.state.storage cell = five ∧
      readCurrent outcome.state.storage otherCell = nine ∧
      outcome.state.events =
        [.keyPopped key, .valuePopped five, .accessCharged cell .cold 2100,
          .currentRead cell zero, .originalRead cell zero,
          .executionWriteCharged 10000, .stateCharged stateCost,
          .storageWritten cell five] := by
  native_decide

/-- One short of the execution write burns gas after the slot is warm/read, without writing. -/
example :
    let before := initial 12099 [key, five] zero zero stateCost
    let outcome := executeSStore schedule false before
    outcome.status = .outOfGas ∧ outcome.state.gas.gasLeft = 0 ∧
      outcome.state.gas.stateReservoir = stateCost ∧ outcome.state.gas.stateUsed = 0 ∧
      outcome.state.gas.refundCounter = 0 ∧ cell ∈ outcome.state.warmCells ∧
      outcome.state.storage = before.storage ∧
      outcome.state.events =
        [.keyPopped key, .valuePopped five, .accessCharged cell .cold 2100,
          .currentRead cell zero, .originalRead cell zero] := by
  native_decide

/-- State OOG keeps the successful access/write debit rather than exhausting its five gas. -/
example :
    let before := initial 12105 [key, five] zero zero (stateCost - 6)
    let outcome := executeSStore schedule false before
    outcome.status = .outOfGas ∧ outcome.state.gas.gasLeft = 5 ∧
      outcome.state.gas.stateReservoir = stateCost - 6 ∧
      outcome.state.gas.stateUsed = 0 ∧ outcome.state.storage = before.storage ∧
      cell ∈ outcome.state.warmCells ∧
      outcome.state.events.getLast? = some (.executionWriteCharged 10000) := by
  native_decide

/-- A no-op reads only current state and pays only its cold access. -/
example :
    let before := initial 2301 [key, seven] nine seven
    let outcome := executeSStore schedule false before
    outcome.status = .ok ∧ outcome.state.gas.gasLeft = 201 ∧
      outcome.state.storage = before.storage ∧ outcome.state.gas.refundCounter = 0 ∧
      outcome.state.events =
        [.keyPopped key, .valuePopped seven, .accessCharged cell .cold 2100,
          .currentRead cell seven, .netMeteredNoOp] := by
  native_decide

/-- A warm first update charges exactly WARM_ACCESS + STORAGE_WRITE. -/
example :
    let outcome := executeSStore schedule false
      (initial 10100 [key, nine] seven seven (warmCells := [cell]))
    outcome.status = .ok ∧ outcome.state.gas.gasLeft = 0 ∧
      readCurrent outcome.state.storage cell = nine ∧
      outcome.state.gas.stateUsed = 0 ∧ outcome.state.gas.refundCounter = 0 := by
  native_decide

/-- Clearing a transaction-start nonzero slot grants the signed clear refund. -/
example :
    let outcome := executeSStore schedule false
      (initial 10100 [key, zero] seven seven (refund := 3) (warmCells := [cell]))
    outcome.status = .ok ∧ outcome.state.gas.gasLeft = 0 ∧
      readCurrent outcome.state.storage cell = zero ∧
      outcome.state.gas.refundCounter = 11619 ∧
      outcome.state.events.getLast? = some (.storageWritten cell zero) := by
  native_decide

/-- Clearing a dirty nonzero slot grants only the clear refund and no write charge. -/
example :
    let outcome := executeSStore schedule false
      (initial 2400 [key, zero] seven nine (warmCells := [cell]))
    outcome.status = .ok ∧ outcome.state.gas.gasLeft = 2300 ∧
      outcome.state.gas.refundCounter = 11616 ∧
      readCurrent outcome.state.storage cell = zero := by
  native_decide

/-- Restoring a dirty slot grants STORAGE_WRITE. -/
example :
    let outcome := executeSStore schedule false
      (initial 2400 [key, seven] seven nine (warmCells := [cell]))
    outcome.status = .ok ∧ outcome.state.gas.gasLeft = 2300 ∧
      outcome.state.gas.refundCounter = 10000 ∧
      readCurrent outcome.state.storage cell = seven := by
  native_decide

/-- Restoring a cleared nonzero slot applies -CLEAR then +STORAGE_WRITE, in that order. -/
example :
    let outcome := executeSStore schedule false
      (initial 2400 [key, seven] seven zero (warmCells := [cell]))
    outcome.status = .ok ∧ outcome.state.gas.refundCounter = -1616 ∧
      outcome.state.events =
        [.keyPopped key, .valuePopped seven, .accessCharged cell .warm 100,
          .currentRead cell zero, .originalRead cell seven,
          .executionWriteCharged 0, .stateCharged 0,
          .refundAdjusted (-11616), .refundAdjusted 10000,
          .storageWritten cell seven] := by
  native_decide

/-- Rewriting a cleared slot reverses the earlier clear refund without a restore refund. -/
example :
    let outcome := executeSStore schedule false
      (initial 2400 [key, nine] seven zero (warmCells := [cell]))
    outcome.status = .ok ∧ outcome.state.gas.refundCounter = -11616 ∧
      readCurrent outcome.state.storage cell = nine := by
  native_decide

/-- Rewriting a created nonzero slot pays only access and leaves state accounting unchanged. -/
example :
    let outcome := executeSStore schedule false
      (initial 2400 [key, nine] zero five (used := stateCost) (warmCells := [cell]))
    outcome.status = .ok ∧ outcome.state.gas.gasLeft = 2300 ∧
      outcome.state.gas.stateUsed = stateCost ∧ outcome.state.gas.refundCounter = 0 ∧
      readCurrent outcome.state.storage cell = nine := by
  native_decide

/-! EIP-8037 local versus ancestor state refills. -/

/-- Locally created state refills gas-left first and decrements state-used. -/
example :
    let outcome := executeSStore schedule false
      (initial 2400 [key, zero] zero five 0 stateCost stateCost 0 0 [cell])
    outcome.status = .ok ∧ outcome.state.gas.gasLeft = 100220 ∧
      outcome.state.gas.stateFromGasLeft = 0 ∧ outcome.state.gas.stateUsed = 0 ∧
      outcome.state.gas.refundCounter = 10000 ∧
      outcome.state.stateGasRefundAdvanced = 0 ∧
      outcome.state.events =
        [.keyPopped key, .valuePopped zero, .accessCharged cell .warm 100,
          .currentRead cell five, .originalRead cell zero,
          .executionWriteCharged 0, .stateCharged 0,
          .stateRefundCredited stateCost 0, .refundAdjusted 10000,
          .storageWritten cell zero] := by
  native_decide

/-- A child can spend the refill while recording the parent-created reduction as advanced. -/
example :
    let outcome := executeSStore schedule false
      (initial 2400 [key, zero] zero five 0 stateCost stateCost stateCost 0 [cell])
    outcome.status = .ok ∧ outcome.state.gas.gasLeft = 100220 ∧
      outcome.state.gas.stateFromGasLeft = 0 ∧
      outcome.state.gas.stateUsed = stateCost ∧
      outcome.state.stateGasRefundAdvanced = stateCost ∧
      outcome.state.events.contains (.stateRefundCredited stateCost stateCost) := by
  native_decide

/-! Mutation-sensitive sequencing sentinels. -/

def mutatedLateStipendStack (state : MachineState) : Stack :=
  match state.stack.pop with
  | none => state.stack
  | some (_, tail) => tail

/-- Moving the stipend check after the first pop would consume the key. -/
example :
    (mutatedLateStipendStack (initial 2300 [key, five] zero zero)).words ≠
      (executeSStore schedule false
        (initial 2300 [key, five] zero zero)).state.stack.words := by
  native_decide

/-- Warming before an unaffordable access would expose a different access-list state. -/
example :
    let before := initial 2999 [key, five] zero zero
    let actual := executeSStore expensiveColdSchedule false before
    warmCell before.warmCells cell ≠ actual.state.warmCells := by
  native_decide

def mutatedStateBeforeExecution (before : GasState) : GasState :=
  match chargeState stateCost before with
  | .error _ => before
  | .ok afterState =>
    match chargeExecution schedule.gas.storageWrite afterState with
    | .error _ => exhaustExecutionGas
        { gas := afterState, stateGasFloor := 0, stateGasRefundAdvanced := 0,
          stack := Stack.empty, storage := [], warmCells := [], executingAccount := address,
          events := [] } |>.gas
    | .ok afterExecution => afterExecution

/-- Charging state before execution leaks state-used into an execution-write OOG result. -/
example :
    let before := gas 9999 stateCost 0 0
    (mutatedStateBeforeExecution before).stateUsed = stateCost ∧
      (executeSStore schedule false
        (initial 12099 [key, five] zero zero stateCost)).state.gas.stateUsed = 0 := by
  native_decide

/-- Key/value reversal would write the value as a key instead of updating the selected slot. -/
example :
    let outcome := executeSStore schedule false
      (initial 12100 [key, five] zero zero stateCost)
    readCurrent outcome.state.storage cell ≠
      readCurrent (writeCurrent (store zero zero) { address, key := five } key) cell := by
  native_decide

/-- Writing before the execution charge would make an OOG change durable. -/
example :
    let before := initial 12099 [key, five] zero zero stateCost
    let outcome := executeSStore schedule false before
    outcome.state.storage ≠ writeCurrent before.storage cell five := by
  native_decide

end PersistentStorageVectors
end Evm
end Eip803x
