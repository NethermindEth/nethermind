-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.Gas
import Eip803x.SStore
import Eip803x.Evm.MemoryStackControl
import Lean.Elab.Tactic.Grind
import Lean.Elab.Tactic.Omega

namespace Eip803x
namespace Evm
namespace PersistentStorage

open GasMachine
open MemoryStackControl

/-- Handler-local constants around the shared, fork-configurable EIP-8037/8038 schedule. -/
structure Schedule where
  gas : GasSchedule
  sloadBase : Nat
  sstoreStipend : Nat
  deriving DecidableEq, Repr

namespace Schedule

/-- Amsterdam has EIP-2929 access charging, so the separate SLOAD base is zero. -/
def amsterdam : Schedule where
  gas := GasSchedule.amsterdam
  sloadBase := 0
  sstoreStipend := 2300

end Schedule

/-- Persistent storage is keyed by the currently executing account and a 256-bit index. -/
structure Cell where
  address : UInt256
  key : UInt256
  deriving DecidableEq, Repr

/-- The transaction-start value is kept separately from the current journaled value. -/
structure Slot where
  original : UInt256
  current : UInt256
  deriving DecidableEq, Repr

/-- An extensional handler-local view of persistent storage; the first matching cell wins. -/
abbrev Store := List (Cell × Slot)

def zeroSlot : Slot :=
  { original := Word.zero, current := Word.zero }

def readSlot : Store → Cell → Slot
  | [], _ => zeroSlot
  | (candidate, value) :: tail, cell =>
      if candidate = cell then value else readSlot tail cell

def readOriginal (store : Store) (cell : Cell) : UInt256 :=
  (readSlot store cell).original

def readCurrent (store : Store) (cell : Cell) : UInt256 :=
  (readSlot store cell).current

/-- Writes only the current value. A previously absent cell has transaction-start value zero. -/
def writeCurrent : Store → Cell → UInt256 → Store
  | [], cell, value => [(cell, { original := Word.zero, current := value })]
  | (candidate, slot) :: tail, cell, value =>
      if candidate = cell then
        (candidate, { slot with current := value }) :: tail
      else
        (candidate, slot) :: writeCurrent tail cell value

def accessStatus (warmCells : List Cell) (cell : Cell) : AccessStatus :=
  if cell ∈ warmCells then .warm else .cold

def accessCost (schedule : Schedule) : AccessStatus → Nat
  | .cold => schedule.gas.coldStorageAccess
  | .warm => schedule.gas.warmAccess

def warmCell (warmCells : List Cell) (cell : Cell) : List Cell :=
  if cell ∈ warmCells then warmCells else cell :: warmCells

/-- A proof-only chronological audit of the semantic effects around production helper calls. -/
inductive Event where
  | sloadBaseCharged (amount : Nat)
  | keyPopped (key : UInt256)
  | valuePopped (value : UInt256)
  | accessCharged (cell : Cell) (status : AccessStatus) (amount : Nat)
  | currentRead (cell : Cell) (value : UInt256)
  | originalRead (cell : Cell) (value : UInt256)
  | netMeteredNoOp
  | executionWriteCharged (amount : Nat)
  | stateCharged (amount : Nat)
  | refundAdjusted (amount : Int)
  | stateRefundCredited (amount advanced : Nat)
  | storageWritten (cell : Cell) (value : UInt256)
  | valuePushed (value : UInt256)
  deriving DecidableEq, Repr

/-- The state visible at the boundary of one SLOAD or SSTORE handler invocation. -/
structure MachineState where
  gas : GasState
  stateGasFloor : Nat
  stateGasRefundAdvanced : Nat
  stack : Stack
  storage : Store
  warmCells : List Cell
  executingAccount : UInt256
  events : List Event
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

def record (state : MachineState) (event : Event) : MachineState :=
  { state with events := state.events ++ [event] }

def exhaustExecutionGas (state : MachineState) : MachineState :=
  { state with gas := { state.gas with gasLeft := 0 } }

/-- Credits ancestor-created state without prematurely decrementing this frame's state-used floor. -/
def addAdvancedStateRefund (amount : Nat) (gas : GasState) : GasState :=
  let toGasLeft := min amount gas.stateFromGasLeft
  { gas with
    gasLeft := gas.gasLeft + toGasLeft
    stateReservoir := gas.stateReservoir + (amount - toGasLeft)
    stateFromGasLeft := gas.stateFromGasLeft - toGasLeft }

structure StateGasCredit where
  gas : GasState
  advanced : Nat
  deriving DecidableEq, Repr

/--
The exact handler-level abstraction of `VirtualMachine.CreditStateGasRefund`.
The portion above this frame's `InitialStateGasUsed` floor is refilled normally;
the rest remains spendable but is tracked for propagation to the ancestor frame.
-/
def creditStateGasRefund (amount stateGasFloor : Nat) (gas : GasState) : StateGasCredit :=
  let refundableHere := gas.stateUsed - stateGasFloor
  let appliedHere := min amount refundableHere
  let afterLocal := refillState appliedHere gas
  let advanced := amount - appliedHere
  { gas := addAdvancedStateRefund advanced afterLocal, advanced }

def creditMachineStateRefund (amount : Nat) (state : MachineState) : MachineState :=
  if amount = 0 then
    state
  else
    let credit := creditStateGasRefund amount state.stateGasFloor state.gas
    record
      { state with
        gas := credit.gas
        stateGasRefundAdvanced := state.stateGasRefundAdvanced + credit.advanced }
      (.stateRefundCredited amount credit.advanced)

def adjustRefund (amount : Int) (state : MachineState) : MachineState :=
  if amount = 0 then
    state
  else
    record
      { state with gas := { state.gas with refundCounter := state.gas.refundCounter + amount } }
      (.refundAdjusted amount)

def clearRefundAdjustment (schedule : Schedule) (input : StorageSituation) : Int :=
  if SStore.clearsOriginal input then schedule.gas.storageClearRefund else 0

def clearRefundReversal (schedule : Schedule) (input : StorageSituation) : Int :=
  if SStore.reversesClear input then -(schedule.gas.storageClearRefund : Int) else 0

def restoreOriginalRefund (schedule : Schedule) (input : StorageSituation) : Int :=
  if SStore.restoresOriginal input then schedule.gas.storageWrite else 0

def postAccessExecutionCharge (schedule : Schedule) (input : StorageSituation) : Nat :=
  (SStore.price schedule.gas input).executionCharge - accessCost schedule input.access

/-- Applies the already-afforded SSTORE refund, refill, and durable-write effects in order. -/
def commitSStore (schedule : Schedule) (input : StorageSituation) (cell : Cell)
    (newValue : UInt256) (state : MachineState) : MachineState :=
  let effect := SStore.price schedule.gas input
  let afterClear := adjustRefund (clearRefundAdjustment schedule input) state
  let afterReversal := adjustRefund (clearRefundReversal schedule input) afterClear
  let afterStateRefund := creditMachineStateRefund effect.stateRefill afterReversal
  let afterRestore := adjustRefund (restoreOriginalRefund schedule input) afterStateRefund
  let afterWrite := { afterRestore with storage := writeCurrent afterRestore.storage cell newValue }
  record afterWrite (.storageWritten cell newValue)

/--
Pinned production SLOAD ordering: base charge, key pop, access charge/warming,
current-value read, then replacement push. Failed execution charges burn gas.
-/
def executeSLoad (schedule : Schedule) (state : MachineState) : Outcome :=
  match chargeExecution schedule.sloadBase state.gas with
  | .error _ => { status := .outOfGas, state := exhaustExecutionGas state }
  | .ok gasAfterBase =>
    let afterBase := record { state with gas := gasAfterBase }
      (.sloadBaseCharged schedule.sloadBase)
    match afterBase.stack.pop with
    | none => { status := .stackUnderflow, state := afterBase }
    | some (key, tail) =>
      let cell := { address := state.executingAccount, key }
      let afterPop := record { afterBase with stack := tail } (.keyPopped key)
      let access := accessStatus afterPop.warmCells cell
      let cost := accessCost schedule access
      match chargeExecution cost afterPop.gas with
      | .error _ => { status := .outOfGas, state := exhaustExecutionGas afterPop }
      | .ok gasAfterAccess =>
        let afterAccess := record
          { afterPop with gas := gasAfterAccess
                          warmCells := warmCell afterPop.warmCells cell }
          (.accessCharged cell access cost)
        let value := readCurrent afterAccess.storage cell
        let afterRead := record afterAccess (.currentRead cell value)
        match afterRead.stack.push value with
        | none => { status := .stackOverflow, state := afterRead }
        | some stackAfter =>
          { status := .ok
            state := record { afterRead with stack := stackAfter } (.valuePushed value) }

/--
Pinned joint EIP-8037/8038 SSTORE handler composition. Static and stipend checks
precede stack access. Access is charged and warmed before the current read; an
original read happens only for a change. Execution write gas precedes state gas,
all refunds precede the durable write, and any failure leaves storage untouched.
-/
def executeSStore (schedule : Schedule) (isStatic : Bool) (state : MachineState) : Outcome :=
  if isStatic then
    { status := .staticCallViolation, state }
  else if state.gas.gasLeft ≤ schedule.sstoreStipend then
    { status := .outOfGas, state }
  else
    match state.stack.pop with
    | none => { status := .stackUnderflow, state }
    | some (key, afterKeyStack) =>
      let afterKey := record { state with stack := afterKeyStack } (.keyPopped key)
      match afterKey.stack.pop with
      | none => { status := .stackUnderflow, state := afterKey }
      | some (newValue, afterValueStack) =>
        let afterValue := record { afterKey with stack := afterValueStack }
          (.valuePopped newValue)
        let cell := { address := state.executingAccount, key }
        let access := accessStatus afterValue.warmCells cell
        let cost := accessCost schedule access
        match chargeExecution cost afterValue.gas with
        | .error _ => { status := .outOfGas, state := exhaustExecutionGas afterValue }
        | .ok gasAfterAccess =>
          let afterAccess := record
            { afterValue with gas := gasAfterAccess
                              warmCells := warmCell afterValue.warmCells cell }
            (.accessCharged cell access cost)
          let current := readCurrent afterAccess.storage cell
          let afterCurrent := record afterAccess (.currentRead cell current)
          if newValue = current then
            { status := .ok, state := record afterCurrent .netMeteredNoOp }
          else
            let original := readOriginal afterCurrent.storage cell
            let afterOriginal := record afterCurrent (.originalRead cell original)
            let input : StorageSituation := { original, current, newValue, access }
            let effect := SStore.price schedule.gas input
            let executionWrite := postAccessExecutionCharge schedule input
            match chargeExecution executionWrite afterOriginal.gas with
            | .error _ =>
              { status := .outOfGas, state := exhaustExecutionGas afterOriginal }
            | .ok gasAfterExecution =>
              let afterExecution := record { afterOriginal with gas := gasAfterExecution }
                (.executionWriteCharged executionWrite)
              match chargeState effect.stateCharge afterExecution.gas with
              | .error _ => { status := .outOfGas, state := afterExecution }
              | .ok gasAfterState =>
                let afterState := record { afterExecution with gas := gasAfterState }
                  (.stateCharged effect.stateCharge)
                { status := .ok, state := commitSStore schedule input cell newValue afterState }

theorem readSlot_write_same (before : Store) (cell : Cell) (value : UInt256) :
    readSlot (writeCurrent before cell value) cell =
      { original := readOriginal before cell, current := value } := by
  induction before with
  | nil => simp [writeCurrent, readSlot, readOriginal, zeroSlot]
  | cons head tail ih =>
      rcases head with ⟨candidate, slot⟩
      by_cases h : candidate = cell
      · subst candidate
        simp [writeCurrent, readSlot, readOriginal]
      · simp [writeCurrent, readSlot, readOriginal, h, ih]

theorem readSlot_write_other (before : Store) (written queried : Cell) (value : UInt256)
    (hDifferent : written ≠ queried) :
    readSlot (writeCurrent before written value) queried = readSlot before queried := by
  induction before with
  | nil => simp [writeCurrent, readSlot, hDifferent]
  | cons head tail ih =>
      rcases head with ⟨candidate, slot⟩
      by_cases hWritten : candidate = written
      · subst candidate
        simp [writeCurrent, readSlot, hDifferent]
      · by_cases hQueried : candidate = queried
        · subst candidate
          simp [writeCurrent, readSlot, hWritten]
        · simp [writeCurrent, readSlot, hWritten, hQueried, ih]

theorem writeCurrent_preserves_original (before : Store) (cell : Cell) (value : UInt256) :
    readOriginal (writeCurrent before cell value) cell = readOriginal before cell := by
  simp [readOriginal, readSlot_write_same]

theorem writeCurrent_sets_current (before : Store) (cell : Cell) (value : UInt256) :
    readCurrent (writeCurrent before cell value) cell = value := by
  simp [readCurrent, readSlot_write_same]

theorem warmCell_contains (warmCells : List Cell) (cell : Cell) :
    cell ∈ warmCell warmCells cell := by
  by_cases h : cell ∈ warmCells
  · simp [warmCell, h]
  · simp [warmCell, h]

theorem accessCost_agrees_with_sstore (schedule : Schedule) (access : AccessStatus) :
    accessCost schedule access = SStore.accessCharge schedule.gas access := by
  cases access <;> rfl

theorem refund_adjustments_agree_with_price (schedule : Schedule)
    (input : StorageSituation) :
    clearRefundAdjustment schedule input + clearRefundReversal schedule input +
        restoreOriginalRefund schedule input =
      (SStore.price schedule.gas input).executionRefund := by
  unfold clearRefundAdjustment clearRefundReversal restoreOriginalRefund SStore.price
  split <;> split <;> split <;> simp [Int.sub_eq_add_neg] <;> omega

theorem postAccessExecutionCharge_eq (schedule : Schedule) (input : StorageSituation) :
    postAccessExecutionCharge schedule input =
      if SStore.isFirstChange input then schedule.gas.storageWrite else 0 := by
  rcases input with ⟨original, current, newValue, access⟩
  cases access <;>
    simp [postAccessExecutionCharge, SStore.price, accessCost, SStore.accessCharge] <;>
    omega

theorem sstore_execution_charge_split (schedule : Schedule) (input : StorageSituation) :
    accessCost schedule input.access + postAccessExecutionCharge schedule input =
      (SStore.price schedule.gas input).executionCharge := by
  rcases input with ⟨original, current, newValue, access⟩
  cases access <;>
    simp [accessCost, postAccessExecutionCharge, SStore.price, SStore.accessCharge] <;>
    omega

theorem adjustRefund_gas (amount : Int) (state : MachineState) :
    (adjustRefund amount state).gas =
      { state.gas with refundCounter := state.gas.refundCounter + amount } := by
  by_cases h : amount = 0 <;> simp [adjustRefund, h, record]

theorem adjustRefund_storage (amount : Int) (state : MachineState) :
    (adjustRefund amount state).storage = state.storage := by
  by_cases h : amount = 0 <;> simp [adjustRefund, h, record]

theorem adjustRefund_advanced (amount : Int) (state : MachineState) :
    (adjustRefund amount state).stateGasRefundAdvanced = state.stateGasRefundAdvanced := by
  by_cases h : amount = 0 <;> simp [adjustRefund, h, record]

theorem adjustRefund_floor (amount : Int) (state : MachineState) :
    (adjustRefund amount state).stateGasFloor = state.stateGasFloor := by
  by_cases h : amount = 0 <;> simp [adjustRefund, h, record]

theorem adjustRefund_stack (amount : Int) (state : MachineState) :
    (adjustRefund amount state).stack = state.stack := by
  by_cases h : amount = 0 <;> simp [adjustRefund, h, record]

theorem adjustRefund_warmCells (amount : Int) (state : MachineState) :
    (adjustRefund amount state).warmCells = state.warmCells := by
  by_cases h : amount = 0 <;> simp [adjustRefund, h, record]

theorem addAdvancedStateRefund_total (amount : Nat) (gas : GasState) :
    totalRemaining (addAdvancedStateRefund amount gas) = totalRemaining gas + amount := by
  simp [totalRemaining, addAdvancedStateRefund]
  omega

theorem addAdvancedStateRefund_preserves_stateUsed (amount : Nat) (gas : GasState) :
    (addAdvancedStateRefund amount gas).stateUsed = gas.stateUsed := by
  rfl

theorem creditStateGasRefund_advanced (amount floor : Nat) (gas : GasState) :
    (creditStateGasRefund amount floor gas).advanced =
      amount - min amount (gas.stateUsed - floor) := by
  rfl

theorem creditStateGasRefund_total (amount floor : Nat) (gas : GasState) :
    totalRemaining (creditStateGasRefund amount floor gas).gas =
      totalRemaining gas + amount := by
  simp only [creditStateGasRefund]
  rw [addAdvancedStateRefund_total, refillState_total]
  omega

theorem creditStateGasRefund_preserves_refundCounter (amount floor : Nat) (gas : GasState) :
    (creditStateGasRefund amount floor gas).gas.refundCounter = gas.refundCounter := by
  simp [creditStateGasRefund, refillState, addAdvancedStateRefund]

theorem creditStateGasRefund_split_fields (amount floor : Nat) (gas : GasState) :
    let appliedHere := min amount (gas.stateUsed - floor)
    let advanced := amount - appliedHere
    let localToGasLeft := min appliedHere gas.stateFromGasLeft
    let afterLocalFromGasLeft := gas.stateFromGasLeft - localToGasLeft
    let advancedToGasLeft := min advanced afterLocalFromGasLeft
    let credit := creditStateGasRefund amount floor gas
    credit.advanced = advanced ∧
      credit.gas.gasLeft = gas.gasLeft + localToGasLeft + advancedToGasLeft ∧
      credit.gas.stateReservoir = gas.stateReservoir + (appliedHere - localToGasLeft) +
        (advanced - advancedToGasLeft) ∧
      credit.gas.stateFromGasLeft = gas.stateFromGasLeft - localToGasLeft -
        advancedToGasLeft ∧
      credit.gas.stateUsed = gas.stateUsed - appliedHere ∧
      credit.gas.refundCounter = gas.refundCounter := by
  simp [creditStateGasRefund, refillState, appliedRefill, refillToGasLeft,
    addAdvancedStateRefund]

theorem creditMachineStateRefund_fields (amount : Nat) (state : MachineState) :
    let credit := creditStateGasRefund amount state.stateGasFloor state.gas
    (creditMachineStateRefund amount state).gas = credit.gas ∧
      (creditMachineStateRefund amount state).stateGasRefundAdvanced =
        state.stateGasRefundAdvanced + credit.advanced ∧
      (creditMachineStateRefund amount state).storage = state.storage := by
  by_cases h : amount = 0
  · simp [creditMachineStateRefund, creditStateGasRefund, h, refillState,
      appliedRefill, refillToGasLeft, addAdvancedStateRefund]
  · simp [creditMachineStateRefund, h, record]

theorem creditMachineStateRefund_stack_warmCells (amount : Nat) (state : MachineState) :
    (creditMachineStateRefund amount state).stack = state.stack ∧
      (creditMachineStateRefund amount state).warmCells = state.warmCells := by
  by_cases h : amount = 0 <;> simp [creditMachineStateRefund, h, record]

/-- The exact local/ancestor credit state reached between the reversal and restore refunds. -/
def commitCredit (schedule : Schedule) (input : StorageSituation)
    (floor : Nat) (gas : GasState) : StateGasCredit :=
  creditStateGasRefund (SStore.price schedule.gas input).stateRefill floor
    { gas with
      refundCounter := gas.refundCounter + clearRefundAdjustment schedule input +
        clearRefundReversal schedule input }

theorem commitSStore_gas_and_advanced (schedule : Schedule) (input : StorageSituation)
    (cell : Cell) (newValue : UInt256) (state : MachineState) :
    let credit := commitCredit schedule input state.stateGasFloor state.gas
    (commitSStore schedule input cell newValue state).gas =
      { credit.gas with
        refundCounter := credit.gas.refundCounter + restoreOriginalRefund schedule input } ∧
      (commitSStore schedule input cell newValue state).stateGasRefundAdvanced =
        state.stateGasRefundAdvanced + credit.advanced := by
  simp only [commitSStore, record, adjustRefund_gas, adjustRefund_floor,
    adjustRefund_advanced, creditMachineStateRefund_fields, commitCredit]
  simp

theorem commitSStore_storage (schedule : Schedule) (input : StorageSituation)
    (cell : Cell) (newValue : UInt256) (state : MachineState) :
    (commitSStore schedule input cell newValue state).storage =
      writeCurrent state.storage cell newValue := by
  simp [commitSStore, adjustRefund_storage, creditMachineStateRefund_fields, record]

theorem commitSStore_stack_warmCells (schedule : Schedule) (input : StorageSituation)
    (cell : Cell) (newValue : UInt256) (state : MachineState) :
    (commitSStore schedule input cell newValue state).stack.words = state.stack.words ∧
      (commitSStore schedule input cell newValue state).warmCells = state.warmCells := by
  simp only [commitSStore, record, adjustRefund_stack, adjustRefund_warmCells,
    creditMachineStateRefund_stack_warmCells]
  simp

theorem commitSStore_refundCounter (schedule : Schedule) (input : StorageSituation)
    (cell : Cell) (newValue : UInt256) (state : MachineState) :
    (commitSStore schedule input cell newValue state).gas.refundCounter =
      state.gas.refundCounter + (SStore.price schedule.gas input).executionRefund := by
  simp only [commitSStore, record, adjustRefund_gas, adjustRefund_floor,
    creditMachineStateRefund_fields, creditStateGasRefund_preserves_refundCounter]
  rw [← refund_adjustments_agree_with_price schedule input]
  omega

theorem credit_above_frame_floor_is_advanced {amount floor : Nat} {gas : GasState}
    (hFloor : gas.stateUsed ≤ floor) :
    (creditStateGasRefund amount floor gas).advanced = amount ∧
      (creditStateGasRefund amount floor gas).gas.stateUsed = gas.stateUsed ∧
      totalRemaining (creditStateGasRefund amount floor gas).gas =
        totalRemaining gas + amount := by
  have hZero : gas.stateUsed - floor = 0 := Nat.sub_eq_zero_of_le hFloor
  simp [creditStateGasRefund, hZero, addAdvancedStateRefund,
    refillState, appliedRefill, refillToGasLeft, totalRemaining]
  omega

theorem sload_out_of_base_gas_preserves_stack_and_storage (schedule : Schedule)
    (state : MachineState) (h : state.gas.gasLeft < schedule.sloadBase) :
    let outcome := executeSLoad schedule state
    outcome.status = .outOfGas ∧ outcome.state.stack.words = state.stack.words ∧
      outcome.state.storage = state.storage := by
  simp [executeSLoad, chargeExecution, Nat.not_le_of_lt h, exhaustExecutionGas]

theorem sstore_static_preserves_state (schedule : Schedule) (state : MachineState) :
    executeSStore schedule true state = { status := .staticCallViolation, state } := by
  rfl

theorem sstore_stipend_preserves_handler_state (schedule : Schedule) (state : MachineState)
    (h : state.gas.gasLeft ≤ schedule.sstoreStipend) :
    executeSStore schedule false state = { status := .outOfGas, state } := by
  simp [executeSStore, h]

theorem sstore_failure_preserves_persistent_effects (schedule : Schedule) (isStatic : Bool)
    (state : MachineState) (hFailure : (executeSStore schedule isStatic state).status ≠ .ok) :
    let outcome := executeSStore schedule isStatic state
    outcome.state.storage = state.storage ∧
      outcome.state.gas.refundCounter = state.gas.refundCounter ∧
      outcome.state.stateGasRefundAdvanced = state.stateGasRefundAdvanced := by
  dsimp
  by_cases hStatic : isStatic
  · simp [executeSStore, hStatic]
  · by_cases hStipend : state.gas.gasLeft ≤ schedule.sstoreStipend
    · simp [executeSStore, hStatic, hStipend]
    · cases hKey : state.stack.pop with
      | none => simp [executeSStore, hStatic, hStipend, hKey]
      | some keyResult =>
        rcases keyResult with ⟨key, afterKeyStack⟩
        cases hValue : afterKeyStack.pop with
        | none => simp [executeSStore, hStatic, hStipend, hKey, hValue, record]
        | some valueResult =>
          rcases valueResult with ⟨newValue, afterValueStack⟩
          let cell : Cell := { address := state.executingAccount, key }
          let access := accessStatus state.warmCells cell
          let cost := accessCost schedule access
          cases hAccess : chargeExecution cost state.gas with
          | error error =>
            simp [executeSStore, hStatic, hStipend, hKey, hValue, cell, access, cost,
              hAccess, record, exhaustExecutionGas]
          | ok gasAfterAccess =>
            have hAccessPreserves := chargeExecution_changes_only_gasLeft hAccess
            by_cases hNoOp : newValue = readCurrent state.storage cell
            · simp [executeSStore, hStatic, hStipend, hKey, hValue, cell, access, cost,
                hAccess, hNoOp, record] at hFailure
            · let input : StorageSituation :=
                { original := readOriginal state.storage cell
                  current := readCurrent state.storage cell
                  newValue
                  access }
              let executionWrite := postAccessExecutionCharge schedule input
              cases hExecution : chargeExecution executionWrite gasAfterAccess with
              | error error =>
                simp [executeSStore, hStatic, hStipend, hKey, hValue, cell, access,
                  cost, hAccess, hNoOp, input, executionWrite, hExecution,
                  record, exhaustExecutionGas]
                exact hAccessPreserves.2.2.2
              | ok gasAfterExecution =>
                have hExecutionPreserves := chargeExecution_changes_only_gasLeft hExecution
                let stateCharge := (SStore.price schedule.gas input).stateCharge
                cases hState : chargeState stateCharge gasAfterExecution with
                | error error =>
                  have hStateRefund := hExecutionPreserves.2.2.2.trans
                    hAccessPreserves.2.2.2
                  simp [executeSStore, hStatic, hStipend, hKey, hValue, cell, access,
                    cost, hAccess, hNoOp, input, executionWrite, hExecution, stateCharge,
                    hState, record]
                  exact hStateRefund
                | ok gasAfterState =>
                  simp [executeSStore, hStatic, hStipend, hKey, hValue, cell, access,
                    cost, hAccess, hNoOp, input, executionWrite, hExecution, stateCharge,
                    hState, record] at hFailure

theorem sstore_changed_success_refines_price (schedule : Schedule) (state : MachineState)
    (key newValue original current : UInt256) (afterKeyStack tail : Stack)
    (gasAfterAccess gasAfterExecution gasAfterState : GasState)
    (hStipend : schedule.sstoreStipend < state.gas.gasLeft)
    (hKey : state.stack.pop = some (key, afterKeyStack))
    (hValue : afterKeyStack.pop = some (newValue, tail))
    (hOriginal : readOriginal state.storage
      { address := state.executingAccount, key } = original)
    (hCurrent : readCurrent state.storage
      { address := state.executingAccount, key } = current)
    (hChange : newValue ≠ current)
    (hAccess : chargeExecution
      (accessCost schedule (accessStatus state.warmCells
        { address := state.executingAccount, key })) state.gas = .ok gasAfterAccess)
    (hExecution : chargeExecution
      (postAccessExecutionCharge schedule
        { original, current, newValue,
          access := accessStatus state.warmCells
            { address := state.executingAccount, key } })
      gasAfterAccess = .ok gasAfterExecution)
    (hState : chargeState
      (SStore.price schedule.gas
        { original, current, newValue,
          access := accessStatus state.warmCells
            { address := state.executingAccount, key } }).stateCharge
      gasAfterExecution = .ok gasAfterState) :
    let cell : Cell := { address := state.executingAccount, key }
    let input : StorageSituation :=
      { original, current, newValue, access := accessStatus state.warmCells cell }
    let credit := commitCredit schedule input state.stateGasFloor gasAfterState
    let outcome := executeSStore schedule false state
    outcome.status = .ok ∧
      accessCost schedule input.access + postAccessExecutionCharge schedule input =
        (SStore.price schedule.gas input).executionCharge ∧
      outcome.state.stack.words = tail.words ∧
      outcome.state.warmCells = warmCell state.warmCells cell ∧
      cell ∈ outcome.state.warmCells ∧
      outcome.state.gas =
        { credit.gas with refundCounter := credit.gas.refundCounter +
            restoreOriginalRefund schedule input } ∧
      outcome.state.gas.refundCounter =
        gasAfterState.refundCounter + (SStore.price schedule.gas input).executionRefund ∧
      outcome.state.stateGasRefundAdvanced =
        state.stateGasRefundAdvanced + credit.advanced ∧
      readOriginal outcome.state.storage cell = original ∧
      readCurrent outcome.state.storage cell = newValue ∧
      (∀ other, other ≠ cell →
        readSlot outcome.state.storage other = readSlot state.storage other) := by
  dsimp
  have hNotStipend : ¬state.gas.gasLeft ≤ schedule.sstoreStipend :=
    Nat.not_le_of_lt hStipend
  simp [executeSStore, hNotStipend, hKey, hValue, hCurrent,
    hChange, hOriginal, hAccess, hExecution, hState, record]
  simp only [commitSStore_stack_warmCells, commitSStore_gas_and_advanced,
    commitSStore_storage]
  simp [commitCredit, writeCurrent_preserves_original,
    writeCurrent_sets_current, hOriginal]
  constructor
  · exact sstore_execution_charge_split schedule
      { original, current, newValue,
        access := accessStatus state.warmCells
          { address := state.executingAccount, key } }
  · constructor
    · exact warmCell_contains state.warmCells
        { address := state.executingAccount, key }
    · constructor
      · rw [creditStateGasRefund_preserves_refundCounter]
        change gasAfterState.refundCounter + clearRefundAdjustment schedule
              { original, current, newValue,
                access := accessStatus state.warmCells
                  { address := state.executingAccount, key } } +
            clearRefundReversal schedule
              { original, current, newValue,
                access := accessStatus state.warmCells
                  { address := state.executingAccount, key } } +
            restoreOriginalRefund schedule
              { original, current, newValue,
                access := accessStatus state.warmCells
                  { address := state.executingAccount, key } } =
            gasAfterState.refundCounter +
              (SStore.price schedule.gas
                { original, current, newValue,
                  access := accessStatus state.warmCells
                    { address := state.executingAccount, key } }).executionRefund
        rw [← refund_adjustments_agree_with_price schedule
          { original, current, newValue,
            access := accessStatus state.warmCells
              { address := state.executingAccount, key } }]
        omega
      · intro other hOther
        exact readSlot_write_other state.storage
          { address := state.executingAccount, key } other newValue (Ne.symm hOther)

theorem sstore_noop_success_refines_price (schedule : Schedule) (state : MachineState)
    (key current : UInt256) (afterKeyStack tail : Stack) (gasAfterAccess : GasState)
    (hStipend : schedule.sstoreStipend < state.gas.gasLeft)
    (hKey : state.stack.pop = some (key, afterKeyStack))
    (hValue : afterKeyStack.pop = some (current, tail))
    (hCurrent : readCurrent state.storage
      { address := state.executingAccount, key } = current)
    (hAccess : chargeExecution
      (accessCost schedule (accessStatus state.warmCells
        { address := state.executingAccount, key })) state.gas = .ok gasAfterAccess) :
    let cell : Cell := { address := state.executingAccount, key }
    let input : StorageSituation :=
      { original := readOriginal state.storage cell, current, newValue := current,
        access := accessStatus state.warmCells cell }
    let outcome := executeSStore schedule false state
    outcome.status = .ok ∧
      outcome.state.stack.words = tail.words ∧
      outcome.state.warmCells = warmCell state.warmCells cell ∧
      cell ∈ outcome.state.warmCells ∧
      outcome.state.gas = gasAfterAccess ∧
      outcome.state.storage = state.storage ∧
      outcome.state.stateGasRefundAdvanced = state.stateGasRefundAdvanced ∧
      SStore.price schedule.gas input =
        { executionCharge := accessCost schedule input.access
          executionRefund := 0
          stateCharge := 0
          stateRefill := 0 } := by
  dsimp
  have hNotStipend : ¬state.gas.gasLeft ≤ schedule.sstoreStipend :=
    Nat.not_le_of_lt hStipend
  simp [executeSStore, hNotStipend, hKey, hValue, hCurrent, hAccess, record,
    warmCell_contains]
  rcases accessStatus state.warmCells
    { address := state.executingAccount, key } <;>
    simp [SStore.price, SStore.accessCharge, accessCost,
      SStore.clearsOriginal, SStore.reversesClear, SStore.restoresOriginal,
      SStore.createsSlot, SStore.removesCreatedSlot]

theorem sstore_post_access_execution_oog (schedule : Schedule) (state : MachineState)
    (key newValue original current : UInt256) (afterKeyStack tail : Stack)
    (gasAfterAccess : GasState)
    (hStipend : schedule.sstoreStipend < state.gas.gasLeft)
    (hKey : state.stack.pop = some (key, afterKeyStack))
    (hValue : afterKeyStack.pop = some (newValue, tail))
    (hOriginal : readOriginal state.storage
      { address := state.executingAccount, key } = original)
    (hCurrent : readCurrent state.storage
      { address := state.executingAccount, key } = current)
    (hChange : newValue ≠ current)
    (hAccess : chargeExecution
      (accessCost schedule (accessStatus state.warmCells
        { address := state.executingAccount, key })) state.gas = .ok gasAfterAccess)
    (hExecution : chargeExecution
      (postAccessExecutionCharge schedule
        { original, current, newValue,
          access := accessStatus state.warmCells
            { address := state.executingAccount, key } })
      gasAfterAccess = .error .outOfGas) :
    let cell : Cell := { address := state.executingAccount, key }
    let outcome := executeSStore schedule false state
    outcome.status = .outOfGas ∧
      outcome.state.stack.words = tail.words ∧
      outcome.state.warmCells = warmCell state.warmCells cell ∧
      cell ∈ outcome.state.warmCells ∧
      outcome.state.storage = state.storage ∧
      outcome.state.gas = { gasAfterAccess with gasLeft := 0 } ∧
      outcome.state.gas.refundCounter = state.gas.refundCounter ∧
      outcome.state.stateGasRefundAdvanced = state.stateGasRefundAdvanced := by
  dsimp
  have hNotStipend : ¬state.gas.gasLeft ≤ schedule.sstoreStipend :=
    Nat.not_le_of_lt hStipend
  have hAccessPreserves := chargeExecution_changes_only_gasLeft hAccess
  simp [executeSStore, hNotStipend, hKey, hValue, hOriginal, hCurrent, hChange,
    hAccess, hExecution, record, exhaustExecutionGas, warmCell_contains,
    hAccessPreserves]

theorem sstore_post_access_state_oog (schedule : Schedule) (state : MachineState)
    (key newValue original current : UInt256) (afterKeyStack tail : Stack)
    (gasAfterAccess gasAfterExecution : GasState)
    (hStipend : schedule.sstoreStipend < state.gas.gasLeft)
    (hKey : state.stack.pop = some (key, afterKeyStack))
    (hValue : afterKeyStack.pop = some (newValue, tail))
    (hOriginal : readOriginal state.storage
      { address := state.executingAccount, key } = original)
    (hCurrent : readCurrent state.storage
      { address := state.executingAccount, key } = current)
    (hChange : newValue ≠ current)
    (hAccess : chargeExecution
      (accessCost schedule (accessStatus state.warmCells
        { address := state.executingAccount, key })) state.gas = .ok gasAfterAccess)
    (hExecution : chargeExecution
      (postAccessExecutionCharge schedule
        { original, current, newValue,
          access := accessStatus state.warmCells
            { address := state.executingAccount, key } })
      gasAfterAccess = .ok gasAfterExecution)
    (hState : chargeState
      (SStore.price schedule.gas
        { original, current, newValue,
          access := accessStatus state.warmCells
            { address := state.executingAccount, key } }).stateCharge
      gasAfterExecution = .error .outOfGas) :
    let cell : Cell := { address := state.executingAccount, key }
    let outcome := executeSStore schedule false state
    outcome.status = .outOfGas ∧
      outcome.state.stack.words = tail.words ∧
      outcome.state.warmCells = warmCell state.warmCells cell ∧
      cell ∈ outcome.state.warmCells ∧
      outcome.state.storage = state.storage ∧
      outcome.state.gas = gasAfterExecution ∧
      outcome.state.stateGasRefundAdvanced = state.stateGasRefundAdvanced := by
  dsimp
  have hNotStipend : ¬state.gas.gasLeft ≤ schedule.sstoreStipend :=
    Nat.not_le_of_lt hStipend
  simp [executeSStore, hNotStipend, hKey, hValue, hOriginal, hCurrent, hChange,
    hAccess, hExecution, hState, record, warmCell_contains]

theorem sload_cannot_overflow (schedule : Schedule) (state : MachineState) :
    (executeSLoad schedule state).status ≠ .stackOverflow := by
  unfold executeSLoad
  cases hBase : chargeExecution schedule.sloadBase state.gas with
  | error fault => simp
  | ok gasAfterBase =>
      cases hPop : state.stack.pop with
      | none => simp [hPop, record]
      | some popped =>
          rcases popped with ⟨key, tail⟩
          cases hAccess : chargeExecution
              (accessCost schedule (accessStatus state.warmCells
                { address := state.executingAccount, key })) gasAfterBase with
          | error fault => simp [hPop, hAccess, record]
          | ok gasAfterAccess =>
              have hRoom := Stack.popped_tail_has_room state.stack tail key hPop
              simp [hPop, hAccess, record, Stack.push, hRoom]

end PersistentStorage
end Evm
end Eip803x
