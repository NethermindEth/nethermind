-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import PersistentStorageOpcodeExtractor.Generated.PersistentStorageOpcodeKernel
import Eip803x.Evm.PersistentStorage
import Lean.Elab.Tactic.Grind
import Lean.Elab.Tactic.Omega

namespace PersistentStorageOpcodeExtractor.Refinement.PersistentStorageOpcode

namespace Generated
export Eip803x.Generated.PersistentStorageOpcodeKernel
  (Schedule amsterdamSchedule consensusIsTracingAccess executionModeApplicability
   openExtractionObligations Opcode allOpcodes opcodeByte DispatchSpecialization
   dispatchSpecializations AccessStatus StorageSituation StorageGasEffect accessCost
   isFirstChange clearsOriginal reversesClear restoresOriginal createsSlot removesCreatedSlot
   price Cell Slot Store zeroSlot readSlot readOriginal readCurrent writeCurrent accessStatus
   warmCell Event MachineState Status Outcome record exhaustExecutionGas advancePc
   addAdvancedStateRefund StateGasCredit creditStateGasRefund creditMachineStateRefund
   adjustRefund clearRefundAdjustment clearRefundReversal restoreOriginalRefund
   postAccessExecutionCharge commitSStore executeSLoad executeSStore execute)
end Generated

namespace Reference
export Eip803x.Evm.PersistentStorage
  (Schedule Cell Slot Store zeroSlot readSlot readOriginal readCurrent writeCurrent accessStatus
   accessCost warmCell Event MachineState Status Outcome record exhaustExecutionGas
   addAdvancedStateRefund StateGasCredit creditStateGasRefund creditMachineStateRefund
   adjustRefund clearRefundAdjustment clearRefundReversal restoreOriginalRefund
   postAccessExecutionCharge commitSStore executeSLoad executeSStore)
end Reference

open Eip803x
open Eip803x.Evm.MemoryStackControl

def toReferenceAccess : Generated.AccessStatus → AccessStatus
  | .cold => .cold
  | .warm => .warm

def toReferenceSituation (input : Generated.StorageSituation) : StorageSituation :=
  { original := input.original
    current := input.current
    newValue := input.newValue
    access := toReferenceAccess input.access }

def toReferenceEffect (effect : Generated.StorageGasEffect) : StorageGasEffect :=
  { executionCharge := effect.executionCharge
    executionRefund := effect.executionRefund
    stateCharge := effect.stateCharge
    stateRefill := effect.stateRefill }

def toReferenceCell (cell : Generated.Cell) : Reference.Cell :=
  ⟨cell.address, cell.key⟩

def toReferenceSlot (slot : Generated.Slot) : Reference.Slot :=
  ⟨slot.original, slot.current⟩

def toReferenceStore : Generated.Store → Reference.Store
  | [] => []
  | (cell, slot) :: tail => (toReferenceCell cell, toReferenceSlot slot) :: toReferenceStore tail

def toReferenceEvent : Generated.Event → Reference.Event
  | .sloadBaseCharged amount => .sloadBaseCharged amount
  | .keyPopped key => .keyPopped key
  | .valuePopped value => .valuePopped value
  | .accessCharged cell status amount => .accessCharged (toReferenceCell cell) (toReferenceAccess status) amount
  | .currentRead cell value => .currentRead (toReferenceCell cell) value
  | .originalRead cell value => .originalRead (toReferenceCell cell) value
  | .netMeteredNoOp => .netMeteredNoOp
  | .executionWriteCharged amount => .executionWriteCharged amount
  | .stateCharged amount => .stateCharged amount
  | .refundAdjusted amount => .refundAdjusted amount
  | .stateRefundCredited amount advanced => .stateRefundCredited amount advanced
  | .storageWritten cell value => .storageWritten (toReferenceCell cell) value
  | .valuePushed value => .valuePushed value

def toReferenceState (state : Generated.MachineState) : Reference.MachineState :=
  { gas := state.gas
    stateGasFloor := state.stateGasFloor
    stateGasRefundAdvanced := state.stateGasRefundAdvanced
    stack := state.stack
    storage := toReferenceStore state.storage
    warmCells := state.warmCells.map toReferenceCell
    executingAccount := state.executingAccount
    events := state.events.map toReferenceEvent }

@[simp] theorem toReferenceState_mk (gas : GasState) (floor advanced : Nat)
    (stack : Stack) (storage : Generated.Store) (warmCells : List Generated.Cell)
    (executingAccount : UInt256) (events : List Generated.Event) (pc : Nat) :
    toReferenceState
        ({ gas
           stateGasFloor := floor
           stateGasRefundAdvanced := advanced
           stack
           storage
           warmCells
           executingAccount
           events
           pc } : Generated.MachineState) =
      ({ gas
         stateGasFloor := floor
         stateGasRefundAdvanced := advanced
         stack
         storage := toReferenceStore storage
         warmCells := warmCells.map toReferenceCell
         executingAccount
         events := events.map toReferenceEvent } : Reference.MachineState) := by
  rfl

attribute [simp] toReferenceAccess toReferenceEvent

@[simp] theorem toReferenceState_gas (state : Generated.MachineState) :
    (toReferenceState state).gas = state.gas := by rfl

@[simp] theorem toReferenceState_floor (state : Generated.MachineState) :
    (toReferenceState state).stateGasFloor = state.stateGasFloor := by rfl

@[simp] theorem toReferenceState_advanced (state : Generated.MachineState) :
    (toReferenceState state).stateGasRefundAdvanced = state.stateGasRefundAdvanced := by rfl

@[simp] theorem toReferenceState_stack (state : Generated.MachineState) :
    (toReferenceState state).stack = state.stack := by rfl

@[simp] theorem toReferenceState_storage (state : Generated.MachineState) :
    (toReferenceState state).storage = toReferenceStore state.storage := by rfl

@[simp] theorem toReferenceState_warmCells (state : Generated.MachineState) :
    (toReferenceState state).warmCells = state.warmCells.map toReferenceCell := by rfl

@[simp] theorem toReferenceState_executingAccount (state : Generated.MachineState) :
    (toReferenceState state).executingAccount = state.executingAccount := by rfl

@[simp] theorem toReferenceState_events (state : Generated.MachineState) :
    (toReferenceState state).events = state.events.map toReferenceEvent := by rfl

theorem toReferenceStore_state_storage (state : Generated.MachineState) :
    toReferenceStore state.storage = (toReferenceState state).storage := by
  rfl

def toReferenceStatus : Generated.Status → Option Reference.Status
  | .ok => some .ok
  | .outOfGas => some .outOfGas
  | .stackUnderflow => some .stackUnderflow
  | .stackOverflow => some .stackOverflow
  | .staticCallViolation => some .staticCallViolation
  | .badInstruction => none

attribute [simp] toReferenceStatus

def toReferenceOutcome (outcome : Generated.Outcome) : Option Reference.Outcome := do
  let status ← toReferenceStatus outcome.status
  pure { status, state := toReferenceState outcome.state }

abbrev referenceAmsterdam : Reference.Schedule :=
  Eip803x.Evm.PersistentStorage.Schedule.amsterdam

@[simp] theorem referenceAmsterdam_gas : referenceAmsterdam.gas = GasSchedule.amsterdam := by
  rfl

@[simp] theorem referenceAmsterdam_sloadBase : referenceAmsterdam.sloadBase = 0 := by
  rfl

@[simp] theorem referenceAmsterdam_sstoreStipend : referenceAmsterdam.sstoreStipend = 2300 := by
  rfl

@[simp] theorem generatedAmsterdam_sloadBase : Generated.amsterdamSchedule.sloadBase = 0 := by
  rfl

@[simp] theorem generatedAmsterdam_sstoreStipend :
    Generated.amsterdamSchedule.sstoreStipend = 2300 := by
  rfl

@[simp] theorem consensus_access_tracing_disabled :
    Generated.consensusIsTracingAccess = false := by
  rfl

theorem execution_mode_scope_exact :
    Generated.executionModeApplicability =
      "Normal consensus execution only; VM.IsTracingAccess must be false because access-list tracing uses the distinct warm-cost path." := by
  rfl

theorem open_extraction_obligations_exact :
    Generated.openExtractionObligations =
      ["The theorem covers the immediate standard-mainnet Amsterdam SLOAD/SSTORE handler abstraction selected by the eight admitted dispatch roots; C#, Roslyn, CLR/JIT, unsafe function-pointer execution, and hardware remain trusted.",
       "UInt256, Address, EvmStack, byte-array normalization, zero canonicalization, span lifetime, and provider-value representation remain adapter premises.",
       "IWorldState Get/GetOriginal/Set correctness, database persistence, storage journaling, snapshots, frame rollback/merge, and decorators remain separate obligations.",
       "StackAccessTracker collection implementation and BAL behavior are abstracted as an extensional warm-cell set; its admitted charge-before-warm path is covered for VM.IsTracingAccess=false, while access-list tracing, provider allocation, and concurrency remain open.",
       "Tracing callbacks, callback failures, metrics, cancellation, opcode counter overflow, transaction refund cap, receipt accounting, and block accounting are outside this handler theorem.",
       "Fixed-width ulong/long overflow is excluded by the refinement's explicit Amsterdam schedule and well-formed production-gas representation premises."] := by
  rfl

theorem toReferenceCell_injective : Function.Injective toReferenceCell := by
  intro left right equality
  cases left
  cases right
  cases equality
  rfl

@[simp] theorem toReferenceCell_eq (left right : Generated.Cell) :
    toReferenceCell left = toReferenceCell right ↔ left = right :=
  toReferenceCell_injective.eq_iff

@[simp] theorem readSlot_refines (store : Generated.Store) (cell : Generated.Cell) :
    toReferenceSlot (Generated.readSlot store cell) =
      Reference.readSlot (toReferenceStore store) (toReferenceCell cell) := by
  induction store with
  | nil => rfl
  | cons head tail inductionHypothesis =>
      rcases head with ⟨candidate, slot⟩
      by_cases same : candidate = cell
      · simp [Generated.readSlot, Reference.readSlot, toReferenceStore, same]
      · simp [Generated.readSlot, Reference.readSlot, toReferenceStore, same, inductionHypothesis]

theorem readCurrent_refines (store : Generated.Store) (cell : Generated.Cell) :
    Generated.readCurrent store cell =
      Reference.readCurrent (toReferenceStore store) (toReferenceCell cell) := by
  simpa [Generated.readCurrent, Reference.readCurrent, toReferenceSlot] using
    congrArg (fun slot : Reference.Slot => slot.current) (readSlot_refines store cell)

theorem readOriginal_refines (store : Generated.Store) (cell : Generated.Cell) :
    Generated.readOriginal store cell =
      Reference.readOriginal (toReferenceStore store) (toReferenceCell cell) := by
  simpa [Generated.readOriginal, Reference.readOriginal, toReferenceSlot] using
    congrArg (fun slot : Reference.Slot => slot.original) (readSlot_refines store cell)

@[simp] theorem referenceReadCurrent_refines (store : Generated.Store)
    (cell : Generated.Cell) :
    Reference.readCurrent (toReferenceStore store) (toReferenceCell cell) =
      Generated.readCurrent store cell := by
  exact (readCurrent_refines store cell).symm

@[simp] theorem referenceReadOriginal_refines (store : Generated.Store)
    (cell : Generated.Cell) :
    Reference.readOriginal (toReferenceStore store) (toReferenceCell cell) =
      Generated.readOriginal store cell := by
  exact (readOriginal_refines store cell).symm

@[simp] theorem writeCurrent_refines (store : Generated.Store) (cell : Generated.Cell)
    (value : UInt256) :
    toReferenceStore (Generated.writeCurrent store cell value) =
      Reference.writeCurrent (toReferenceStore store) (toReferenceCell cell) value := by
  induction store with
  | nil => rfl
  | cons head tail inductionHypothesis =>
      rcases head with ⟨candidate, slot⟩
      by_cases same : candidate = cell
      · simp [Generated.writeCurrent, Reference.writeCurrent, toReferenceStore, toReferenceSlot, same]
      · simp [Generated.writeCurrent, Reference.writeCurrent, toReferenceStore, toReferenceSlot,
          same, inductionHypothesis]

@[simp] theorem accessStatus_refines (warmCells : List Generated.Cell) (cell : Generated.Cell) :
    toReferenceAccess (Generated.accessStatus warmCells cell) =
      Reference.accessStatus (warmCells.map toReferenceCell) (toReferenceCell cell) := by
  by_cases warm : cell ∈ warmCells <;>
    simp [Generated.accessStatus, Reference.accessStatus, toReferenceAccess, warm]

@[simp] theorem warmCell_refines (warmCells : List Generated.Cell) (cell : Generated.Cell) :
    (Generated.warmCell warmCells cell).map toReferenceCell =
      Reference.warmCell (warmCells.map toReferenceCell) (toReferenceCell cell) := by
  by_cases warm : cell ∈ warmCells <;>
    simp [Generated.warmCell, Reference.warmCell, warm]

theorem price_refines (input : Generated.StorageSituation) :
    toReferenceEffect (Generated.price Generated.amsterdamSchedule input) =
      SStore.price GasSchedule.amsterdam (toReferenceSituation input) := by
  rcases input with ⟨original, current, newValue, access⟩
  cases access <;> rfl

theorem price_executionCharge_refines (input : Generated.StorageSituation) :
    (Generated.price Generated.amsterdamSchedule input).executionCharge =
      (SStore.price GasSchedule.amsterdam (toReferenceSituation input)).executionCharge := by
  exact congrArg StorageGasEffect.executionCharge (price_refines input)

theorem price_stateCharge_refines (input : Generated.StorageSituation) :
    (Generated.price Generated.amsterdamSchedule input).stateCharge =
      (SStore.price GasSchedule.amsterdam (toReferenceSituation input)).stateCharge := by
  exact congrArg StorageGasEffect.stateCharge (price_refines input)

theorem price_stateRefill_refines (input : Generated.StorageSituation) :
    (Generated.price Generated.amsterdamSchedule input).stateRefill =
      (SStore.price GasSchedule.amsterdam (toReferenceSituation input)).stateRefill := by
  exact congrArg StorageGasEffect.stateRefill (price_refines input)

theorem accessCost_refines (access : Generated.AccessStatus) :
    Generated.accessCost Generated.amsterdamSchedule access =
      Reference.accessCost referenceAmsterdam (toReferenceAccess access) := by
  cases access <;> rfl

@[simp] theorem clearRefundAdjustment_refines (input : Generated.StorageSituation) :
    Generated.clearRefundAdjustment Generated.amsterdamSchedule input =
      Reference.clearRefundAdjustment referenceAmsterdam (toReferenceSituation input) := by
  rcases input with ⟨original, current, newValue, access⟩
  rfl

@[simp] theorem clearRefundReversal_refines (input : Generated.StorageSituation) :
    Generated.clearRefundReversal Generated.amsterdamSchedule input =
      Reference.clearRefundReversal referenceAmsterdam (toReferenceSituation input) := by
  rcases input with ⟨original, current, newValue, access⟩
  rfl

@[simp] theorem restoreOriginalRefund_refines (input : Generated.StorageSituation) :
    Generated.restoreOriginalRefund Generated.amsterdamSchedule input =
      Reference.restoreOriginalRefund referenceAmsterdam (toReferenceSituation input) := by
  rcases input with ⟨original, current, newValue, access⟩
  rfl

theorem postAccessExecutionCharge_refines (input : Generated.StorageSituation) :
    Generated.postAccessExecutionCharge Generated.amsterdamSchedule input =
      Reference.postAccessExecutionCharge referenceAmsterdam (toReferenceSituation input) := by
  rcases input with ⟨original, current, newValue, access⟩
  cases access <;> rfl

@[simp] theorem referenceStateCharge_refines (input : Generated.StorageSituation) :
    (SStore.price GasSchedule.amsterdam (toReferenceSituation input)).stateCharge =
      (Generated.price Generated.amsterdamSchedule input).stateCharge := by
  exact (price_stateCharge_refines input).symm

@[simp] theorem referencePostAccessExecutionCharge_refines
    (input : Generated.StorageSituation) :
    Reference.postAccessExecutionCharge referenceAmsterdam (toReferenceSituation input) =
      Generated.postAccessExecutionCharge Generated.amsterdamSchedule input := by
  exact (postAccessExecutionCharge_refines input).symm

@[simp] theorem record_refines (state : Generated.MachineState) (event : Generated.Event) :
    toReferenceState (Generated.record state event) =
      Reference.record (toReferenceState state) (toReferenceEvent event) := by
  simp [Generated.record, Reference.record, toReferenceState]

@[simp] theorem storage_update_refines (state : Generated.MachineState)
    (storage : Generated.Store) :
    toReferenceState { state with storage := storage } =
      { toReferenceState state with storage := toReferenceStore storage } := by
  rfl

@[simp] theorem gas_update_refines (state : Generated.MachineState) (gas : GasState) :
    toReferenceState { state with gas := gas } =
      { toReferenceState state with gas := gas } := by
  rfl

@[simp] theorem stack_update_refines (state : Generated.MachineState) (stack : Stack) :
    toReferenceState { state with stack := stack } =
      { toReferenceState state with stack := stack } := by
  rfl

@[simp] theorem gas_warm_update_refines (state : Generated.MachineState) (gas : GasState)
    (warmCells : List Generated.Cell) :
    toReferenceState { state with gas := gas, warmCells := warmCells } =
      { toReferenceState state with gas := gas, warmCells := warmCells.map toReferenceCell } := by
  rfl

@[simp] theorem pc_update_erased (state : Generated.MachineState) (pc : Nat) :
    toReferenceState { state with pc := pc } = toReferenceState state := by
  rfl

@[simp] theorem accessedStorageCost_refines (warmCells : List Generated.Cell)
    (cell : Generated.Cell) :
    Reference.accessCost referenceAmsterdam
        (Reference.accessStatus (warmCells.map toReferenceCell) (toReferenceCell cell)) =
      Generated.accessCost Generated.amsterdamSchedule
        (Generated.accessStatus warmCells cell) := by
  rw [← accessStatus_refines]
  exact (accessCost_refines _).symm

@[simp] theorem exhaustExecutionGas_refines (state : Generated.MachineState) :
    toReferenceState (Generated.exhaustExecutionGas state) =
      Reference.exhaustExecutionGas (toReferenceState state) := by
  rfl

@[simp] theorem advancePc_erased (state : Generated.MachineState) :
    toReferenceState (Generated.advancePc state) = toReferenceState state := by
  rfl

@[simp] theorem addAdvancedStateRefund_refines (amount : Nat) (gas : GasState) :
    Generated.addAdvancedStateRefund amount gas = Reference.addAdvancedStateRefund amount gas := by
  rfl

@[simp] theorem creditStateGasRefund_gas_refines (amount floor : Nat) (gas : GasState) :
    (Generated.creditStateGasRefund amount floor gas).gas =
      (Reference.creditStateGasRefund amount floor gas).gas := by
  rfl

@[simp] theorem creditStateGasRefund_advanced_refines (amount floor : Nat) (gas : GasState) :
    (Generated.creditStateGasRefund amount floor gas).advanced =
      (Reference.creditStateGasRefund amount floor gas).advanced := by
  rfl

@[simp] theorem creditMachineStateRefund_refines (amount : Nat) (state : Generated.MachineState) :
    toReferenceState (Generated.creditMachineStateRefund amount state) =
      Reference.creditMachineStateRefund amount (toReferenceState state) := by
  by_cases zero : amount = 0 <;>
    simp [Generated.creditMachineStateRefund, Reference.creditMachineStateRefund,
      Generated.creditStateGasRefund, Reference.creditStateGasRefund, zero,
      Generated.addAdvancedStateRefund, Reference.addAdvancedStateRefund,
      Generated.record, Reference.record, toReferenceState, toReferenceEvent]

@[simp] theorem adjustRefund_refines (amount : Int) (state : Generated.MachineState) :
    toReferenceState (Generated.adjustRefund amount state) =
      Reference.adjustRefund amount (toReferenceState state) := by
  by_cases zero : amount = 0 <;>
    simp [Generated.adjustRefund, Reference.adjustRefund, zero,
      Generated.record, Reference.record, toReferenceState, toReferenceEvent]

@[simp] theorem creditMachineStateRefund_storage_refines (amount : Nat)
    (state : Generated.MachineState) :
    toReferenceStore (Generated.creditMachineStateRefund amount state).storage =
      (Reference.creditMachineStateRefund amount (toReferenceState state)).storage := by
  change (toReferenceState (Generated.creditMachineStateRefund amount state)).storage = _
  rw [creditMachineStateRefund_refines]

@[simp] theorem adjustRefund_storage_refines (amount : Int)
    (state : Generated.MachineState) :
    toReferenceStore (Generated.adjustRefund amount state).storage =
      (Reference.adjustRefund amount (toReferenceState state)).storage := by
  change (toReferenceState (Generated.adjustRefund amount state)).storage = _
  rw [adjustRefund_refines]

theorem commitSStore_refines (input : Generated.StorageSituation) (cell : Generated.Cell)
    (newValue : UInt256) (state : Generated.MachineState) :
    toReferenceState
        (Generated.commitSStore Generated.amsterdamSchedule input cell newValue state) =
      Reference.commitSStore referenceAmsterdam (toReferenceSituation input)
        (toReferenceCell cell) newValue (toReferenceState state) := by
  unfold Generated.commitSStore Reference.commitSStore
  simp only [price_stateRefill_refines, clearRefundAdjustment_refines,
    clearRefundReversal_refines, restoreOriginalRefund_refines, record_refines,
    storage_update_refines, writeCurrent_refines, adjustRefund_refines,
    creditMachineStateRefund_refines]
  simp [referenceAmsterdam, Eip803x.Evm.PersistentStorage.Schedule.amsterdam]

@[simp] theorem commitSStore_preserves_pc (schedule : Generated.Schedule)
    (input : Generated.StorageSituation) (cell : Generated.Cell) (newValue : UInt256)
    (state : Generated.MachineState) :
    (Generated.commitSStore schedule input cell newValue state).pc = state.pc := by
  have adjustRefundPc (amount : Int) (current : Generated.MachineState) :
      (Generated.adjustRefund amount current).pc = current.pc := by
    by_cases zero : amount = 0 <;>
      simp [Generated.adjustRefund, zero, Generated.record]
  have creditRefundPc (amount : Nat) (current : Generated.MachineState) :
      (Generated.creditMachineStateRefund amount current).pc = current.pc := by
    by_cases zero : amount = 0 <;>
      simp [Generated.creditMachineStateRefund, zero, Generated.record]
  unfold Generated.commitSStore
  simp only [Generated.record, adjustRefundPc, creditRefundPc]

theorem execute_sload_refines (isTracingAccess : Bool)
    (_consensusMode : isTracingAccess = Generated.consensusIsTracingAccess)
    (state : Generated.MachineState) :
    toReferenceOutcome (Generated.executeSLoad Generated.amsterdamSchedule state) =
      some (Reference.executeSLoad referenceAmsterdam (toReferenceState state)) := by
  subst isTracingAccess
  unfold Generated.executeSLoad Reference.executeSLoad
  simp only [generatedAmsterdam_sloadBase, referenceAmsterdam_sloadBase,
    Generated.advancePc, Generated.record, Reference.record]
  cases baseCharge : GasMachine.chargeExecution 0 state.gas with
  | error fault => simp [baseCharge, toReferenceOutcome, toReferenceStatus, toReferenceState,
      Generated.exhaustExecutionGas, Reference.exhaustExecutionGas]
  | ok gasAfterBase =>
      cases keyPop : state.stack.pop with
      | none => simp [baseCharge, keyPop, toReferenceOutcome,
          toReferenceStatus, toReferenceEvent]
      | some keyPair =>
          rcases keyPair with ⟨key, tail⟩
          let cell : Generated.Cell := ⟨state.executingAccount, key⟩
          have accessCostEquality := accessedStorageCost_refines state.warmCells cell
          have accessStatusEquality :
              toReferenceAccess (Generated.accessStatus state.warmCells cell) =
                Reference.accessStatus (state.warmCells.map toReferenceCell)
                  ⟨state.executingAccount, key⟩ := by
            simpa [cell, toReferenceCell] using accessStatus_refines state.warmCells cell
          have warmCellsEquality :
              (Generated.warmCell state.warmCells cell).map toReferenceCell =
                Reference.warmCell (state.warmCells.map toReferenceCell)
                  ⟨state.executingAccount, key⟩ := by
            simp [cell, toReferenceCell]
          have readEquality : Generated.readCurrent state.storage cell =
              Reference.readCurrent (toReferenceStore state.storage)
                ⟨state.executingAccount, key⟩ := by
            simpa [cell, toReferenceCell] using readCurrent_refines state.storage cell
          simp only [cell, toReferenceCell] at accessCostEquality accessStatusEquality warmCellsEquality readEquality
          cases accessCharge : GasMachine.chargeExecution
              (Generated.accessCost Generated.amsterdamSchedule
                (Generated.accessStatus state.warmCells cell)) gasAfterBase with
          | error fault =>
              have referenceAccessCharge : GasMachine.chargeExecution
                  (Reference.accessCost referenceAmsterdam
                    (Reference.accessStatus (state.warmCells.map toReferenceCell)
                      ⟨state.executingAccount, key⟩)) gasAfterBase = .error fault := by
                rw [accessCostEquality]
                exact accessCharge
              simp [baseCharge, keyPop, accessCharge, referenceAccessCharge, cell,
                toReferenceOutcome, toReferenceStatus, toReferenceEvent,
                Generated.exhaustExecutionGas, Reference.exhaustExecutionGas]
          | ok gasAfterAccess =>
              have referenceAccessCharge : GasMachine.chargeExecution
                  (Reference.accessCost referenceAmsterdam
                    (Reference.accessStatus (state.warmCells.map toReferenceCell)
                      ⟨state.executingAccount, key⟩)) gasAfterBase = .ok gasAfterAccess := by
                rw [accessCostEquality]
                exact accessCharge
              cases push : tail.push (Generated.readCurrent state.storage cell) with
              | none =>
                  have referencePush : tail.push
                      (Reference.readCurrent (toReferenceStore state.storage)
                        ⟨state.executingAccount, key⟩) = none := by
                    change tail.push (Reference.readCurrent (toReferenceStore state.storage)
                      (toReferenceCell cell)) = none
                    rw [← readCurrent_refines]
                    exact push
                  simp [baseCharge, keyPop, accessCharge, referenceAccessCharge, cell,
                    referencePush, warmCellsEquality, readEquality, toReferenceCell,
                    toReferenceOutcome, toReferenceStatus, toReferenceEvent] <;>
                    exact ⟨accessStatusEquality, accessCostEquality.symm⟩
              | some stackAfter =>
                  have referencePush : tail.push
                      (Reference.readCurrent (toReferenceStore state.storage)
                        ⟨state.executingAccount, key⟩) = some stackAfter := by
                    change tail.push (Reference.readCurrent (toReferenceStore state.storage)
                      (toReferenceCell cell)) = some stackAfter
                    rw [← readCurrent_refines]
                    exact push
                  simp [baseCharge, keyPop, accessCharge, referenceAccessCharge, cell,
                    referencePush, warmCellsEquality, readEquality, toReferenceCell,
                    toReferenceOutcome, toReferenceStatus, toReferenceEvent] <;>
                    exact ⟨accessStatusEquality, accessCostEquality.symm⟩

theorem execute_sstore_refines (isTracingAccess isStatic : Bool)
    (_consensusMode : isTracingAccess = Generated.consensusIsTracingAccess)
    (state : Generated.MachineState) :
    toReferenceOutcome (Generated.executeSStore Generated.amsterdamSchedule isStatic state) =
      some (Reference.executeSStore referenceAmsterdam isStatic (toReferenceState state)) := by
  subst isTracingAccess
  cases isStatic with
  | true => simp [Generated.executeSStore, Reference.executeSStore, Generated.advancePc,
      toReferenceOutcome, toReferenceStatus, toReferenceState]
  | false =>
      simp only [Generated.executeSStore, Reference.executeSStore, Bool.false_eq_true, if_false]
      simp only [generatedAmsterdam_sstoreStipend, referenceAmsterdam_sstoreStipend,
        Generated.advancePc, Generated.record, Reference.record]
      by_cases stipend : state.gas.gasLeft ≤ 2300
      · simp [stipend, toReferenceOutcome, toReferenceStatus, toReferenceState]
      · simp only [stipend, if_false]
        have aboveStipend : 2300 < state.gas.gasLeft := by omega
        have notStipend : ¬state.gas.gasLeft ≤ 2300 := by omega
        cases keyPop : state.stack.pop with
        | none => simp [keyPop, aboveStipend, toReferenceOutcome,
            toReferenceStatus, toReferenceState]
        | some keyPair =>
            rcases keyPair with ⟨key, afterKey⟩
            cases valuePop : afterKey.pop with
            | none => simp [keyPop, valuePop, aboveStipend, toReferenceOutcome,
                toReferenceStatus, toReferenceState]
            | some valuePair =>
                rcases valuePair with ⟨newValue, stackAfter⟩
                let cell : Generated.Cell := ⟨state.executingAccount, key⟩
                let access := Generated.accessStatus state.warmCells cell
                have accessCostEquality :
                    Reference.accessCost referenceAmsterdam
                        (Reference.accessStatus (state.warmCells.map toReferenceCell)
                          ⟨state.executingAccount, key⟩) =
                      Generated.accessCost Generated.amsterdamSchedule access := by
                  simpa [access, cell, toReferenceCell] using
                    accessedStorageCost_refines state.warmCells cell
                have accessStatusEquality :
                    toReferenceAccess access =
                      Reference.accessStatus (state.warmCells.map toReferenceCell)
                        ⟨state.executingAccount, key⟩ := by
                  simpa [access, cell, toReferenceCell] using
                    accessStatus_refines state.warmCells cell
                have warmCellsEquality :
                    (Generated.warmCell state.warmCells cell).map toReferenceCell =
                      Reference.warmCell (state.warmCells.map toReferenceCell)
                        ⟨state.executingAccount, key⟩ := by
                  simp [cell, toReferenceCell]
                have currentEquality : Generated.readCurrent state.storage cell =
                    Reference.readCurrent (toReferenceStore state.storage)
                      ⟨state.executingAccount, key⟩ := by
                  simpa [cell, toReferenceCell] using readCurrent_refines state.storage cell
                have originalEquality : Generated.readOriginal state.storage cell =
                    Reference.readOriginal (toReferenceStore state.storage)
                      ⟨state.executingAccount, key⟩ := by
                  simpa [cell, toReferenceCell] using readOriginal_refines state.storage cell
                have accessStatusAtCell :
                    toReferenceAccess
                        (Generated.accessStatus state.warmCells
                          ⟨state.executingAccount, key⟩) =
                      Reference.accessStatus (state.warmCells.map toReferenceCell)
                        ⟨state.executingAccount, key⟩ := by
                  simpa [access, cell] using accessStatusEquality
                have accessStatusAtCellExpanded :
                    (match Generated.accessStatus state.warmCells
                        ⟨state.executingAccount, key⟩ with
                      | .cold => AccessStatus.cold
                      | .warm => AccessStatus.warm) =
                      Reference.accessStatus (state.warmCells.map toReferenceCell)
                        ⟨state.executingAccount, key⟩ := by
                  simpa only [toReferenceAccess] using accessStatusAtCell
                cases accessCharge : GasMachine.chargeExecution
                    (Generated.accessCost Generated.amsterdamSchedule access) state.gas with
                | error fault =>
                    have referenceAccessCharge : GasMachine.chargeExecution
                        (Reference.accessCost referenceAmsterdam
                          (Reference.accessStatus (state.warmCells.map toReferenceCell)
                            ⟨state.executingAccount, key⟩)) state.gas = .error fault := by
                      rw [accessCostEquality]
                      exact accessCharge
                    simp [keyPop, valuePop, accessCharge, referenceAccessCharge, stipend,
                      cell, access,
                      toReferenceOutcome, toReferenceStatus, exhaustExecutionGas_refines] <;>
                      simp_all [toReferenceCell]
                | ok gasAfterAccess =>
                    have accessChargeAtCell : GasMachine.chargeExecution
                        (Generated.accessCost Generated.amsterdamSchedule
                          (Generated.accessStatus state.warmCells
                            ⟨state.executingAccount, key⟩)) state.gas = .ok gasAfterAccess := by
                      simpa [access, cell] using accessCharge
                    have referenceAccessCharge : GasMachine.chargeExecution
                        (Reference.accessCost referenceAmsterdam
                          (Reference.accessStatus (state.warmCells.map toReferenceCell)
                            ⟨state.executingAccount, key⟩)) state.gas = .ok gasAfterAccess := by
                      rw [accessCostEquality]
                      exact accessCharge
                    let current := Generated.readCurrent state.storage cell
                    by_cases noChange : newValue = current
                    · simp [keyPop, valuePop, accessCharge, stipend,
                        noChange, cell, access, current, accessCostEquality,
                        warmCellsEquality, currentEquality, toReferenceCell,
                        toReferenceOutcome, toReferenceStatus,
                        toReferenceState] <;>
                        exact accessStatusAtCell
                    · have noChangeAtCell : ¬newValue =
                          Generated.readCurrent state.storage
                            ⟨state.executingAccount, key⟩ := by
                        simpa [current, cell] using noChange
                      let original := Generated.readOriginal state.storage cell
                      let input : Generated.StorageSituation :=
                        { original, current, newValue, access }
                      let executionWrite :=
                        Generated.postAccessExecutionCharge Generated.amsterdamSchedule input
                      let referenceInput : StorageSituation :=
                        { original := Reference.readOriginal (toReferenceStore state.storage)
                            ⟨state.executingAccount, key⟩
                          current := Reference.readCurrent (toReferenceStore state.storage)
                            ⟨state.executingAccount, key⟩
                          newValue
                          access := Reference.accessStatus (state.warmCells.map toReferenceCell)
                            ⟨state.executingAccount, key⟩ }
                      have inputEquality : toReferenceSituation input = referenceInput := by
                        simp only [input, referenceInput, original, current, toReferenceSituation]
                        rw [currentEquality, originalEquality, accessStatusEquality]
                      let mappedInput : Generated.StorageSituation :=
                        { original := Reference.readOriginal (toReferenceStore state.storage)
                            ⟨state.executingAccount, key⟩
                          current := Reference.readCurrent (toReferenceStore state.storage)
                            ⟨state.executingAccount, key⟩
                          newValue
                          access := Generated.accessStatus state.warmCells
                            ⟨state.executingAccount, key⟩ }
                      have mappedSituation : toReferenceSituation mappedInput = referenceInput := by
                        simp [mappedInput, referenceInput, toReferenceSituation,
                          accessStatusAtCellExpanded]
                      have mappedExecutionWriteEquality :
                          Generated.postAccessExecutionCharge Generated.amsterdamSchedule mappedInput =
                            Reference.postAccessExecutionCharge referenceAmsterdam referenceInput := by
                        rw [postAccessExecutionCharge_refines, mappedSituation]
                      have mappedStateChargeEquality :
                          (Generated.price Generated.amsterdamSchedule mappedInput).stateCharge =
                            (SStore.price GasSchedule.amsterdam referenceInput).stateCharge := by
                        rw [price_stateCharge_refines, mappedSituation]
                      have mappedSituationAtCell :
                          toReferenceSituation
                              { original := Reference.readOriginal (toReferenceStore state.storage)
                                  ⟨state.executingAccount, key⟩
                                current := Reference.readCurrent (toReferenceStore state.storage)
                                  ⟨state.executingAccount, key⟩
                                newValue
                                access := Generated.accessStatus state.warmCells
                                  ⟨state.executingAccount, key⟩ } =
                            { original := Reference.readOriginal (toReferenceStore state.storage)
                                ⟨state.executingAccount, key⟩
                              current := Reference.readCurrent (toReferenceStore state.storage)
                                ⟨state.executingAccount, key⟩
                              newValue
                              access := Reference.accessStatus
                                (state.warmCells.map toReferenceCell)
                                ⟨state.executingAccount, key⟩ } := by
                        simpa [mappedInput, referenceInput] using mappedSituation
                      have mappedExecutionWriteAtCell :
                          Generated.postAccessExecutionCharge Generated.amsterdamSchedule
                              { original := Reference.readOriginal (toReferenceStore state.storage)
                                  ⟨state.executingAccount, key⟩
                                current := Reference.readCurrent (toReferenceStore state.storage)
                                  ⟨state.executingAccount, key⟩
                                newValue
                                access := Generated.accessStatus state.warmCells
                                  ⟨state.executingAccount, key⟩ } =
                            Reference.postAccessExecutionCharge referenceAmsterdam
                              { original := Reference.readOriginal (toReferenceStore state.storage)
                                  ⟨state.executingAccount, key⟩
                                current := Reference.readCurrent (toReferenceStore state.storage)
                                  ⟨state.executingAccount, key⟩
                                newValue
                                access := Reference.accessStatus
                                  (state.warmCells.map toReferenceCell)
                                  ⟨state.executingAccount, key⟩ } := by
                        simpa [mappedInput, referenceInput] using mappedExecutionWriteEquality
                      have mappedStateChargeAtCell :
                          (Generated.price Generated.amsterdamSchedule
                              { original := Reference.readOriginal (toReferenceStore state.storage)
                                  ⟨state.executingAccount, key⟩
                                current := Reference.readCurrent (toReferenceStore state.storage)
                                  ⟨state.executingAccount, key⟩
                                newValue
                                access := Generated.accessStatus state.warmCells
                                  ⟨state.executingAccount, key⟩ }).stateCharge =
                            (SStore.price GasSchedule.amsterdam
                              { original := Reference.readOriginal (toReferenceStore state.storage)
                                  ⟨state.executingAccount, key⟩
                                current := Reference.readCurrent (toReferenceStore state.storage)
                                  ⟨state.executingAccount, key⟩
                                newValue
                                access := Reference.accessStatus
                                  (state.warmCells.map toReferenceCell)
                                  ⟨state.executingAccount, key⟩ }).stateCharge := by
                        simpa [mappedInput, referenceInput] using mappedStateChargeEquality
                      have referenceChange : ¬newValue =
                          Reference.readCurrent (toReferenceStore state.storage)
                            ⟨state.executingAccount, key⟩ := by
                        intro same
                        apply noChange
                        dsimp [current]
                        rw [currentEquality]
                        exact same
                      have executionWriteEquality :
                          Reference.postAccessExecutionCharge referenceAmsterdam referenceInput =
                            executionWrite := by
                        rw [← inputEquality]
                        exact referencePostAccessExecutionCharge_refines input
                      cases executionCharge : GasMachine.chargeExecution executionWrite gasAfterAccess with
                      | error fault =>
                          have executionChargeAtCell : GasMachine.chargeExecution
                              (Generated.postAccessExecutionCharge Generated.amsterdamSchedule
                                { original := Generated.readOriginal state.storage
                                    ⟨state.executingAccount, key⟩
                                  current := Generated.readCurrent state.storage
                                    ⟨state.executingAccount, key⟩
                                  newValue
                                  access := Generated.accessStatus state.warmCells
                                    ⟨state.executingAccount, key⟩ })
                              gasAfterAccess = .error fault := by
                            simpa [executionWrite, input, original, current, access, cell] using
                              executionCharge
                          have referenceExecutionCharge : GasMachine.chargeExecution
                              (Reference.postAccessExecutionCharge referenceAmsterdam referenceInput)
                              gasAfterAccess = .error fault := by
                            rw [executionWriteEquality]
                            exact executionCharge
                          have referenceExecutionChargeAtCell : GasMachine.chargeExecution
                              (Reference.postAccessExecutionCharge referenceAmsterdam
                                { original := Reference.readOriginal
                                    (toReferenceStore state.storage) ⟨state.executingAccount, key⟩
                                  current := Reference.readCurrent
                                    (toReferenceStore state.storage) ⟨state.executingAccount, key⟩
                                  newValue
                                  access := Reference.accessStatus
                                    (state.warmCells.map toReferenceCell)
                                    ⟨state.executingAccount, key⟩ })
                              gasAfterAccess = .error fault := by
                            simpa [referenceInput] using referenceExecutionCharge
                          simp only [if_false, valuePop, accessChargeAtCell, noChangeAtCell,
                            executionChargeAtCell]
                          simp [notStipend, keyPop, valuePop, accessChargeAtCell, referenceChange,
                            referenceExecutionChargeAtCell, cell, access, accessCostEquality,
                            warmCellsEquality, currentEquality, originalEquality,
                            toReferenceOutcome, toReferenceStatus, exhaustExecutionGas_refines,
                            toReferenceCell] <;>
                            rw [accessStatusAtCellExpanded]
                      | ok gasAfterExecution =>
                          have executionChargeAtCell : GasMachine.chargeExecution
                              (Generated.postAccessExecutionCharge Generated.amsterdamSchedule
                                { original := Generated.readOriginal state.storage
                                    ⟨state.executingAccount, key⟩
                                  current := Generated.readCurrent state.storage
                                    ⟨state.executingAccount, key⟩
                                  newValue
                                  access := Generated.accessStatus state.warmCells
                                    ⟨state.executingAccount, key⟩ })
                              gasAfterAccess = .ok gasAfterExecution := by
                            simpa [executionWrite, input, original, current, access, cell] using
                              executionCharge
                          have referenceExecutionCharge : GasMachine.chargeExecution
                              (Reference.postAccessExecutionCharge referenceAmsterdam referenceInput)
                              gasAfterAccess = .ok gasAfterExecution := by
                            rw [executionWriteEquality]
                            exact executionCharge
                          have referenceExecutionChargeAtCell : GasMachine.chargeExecution
                              (Reference.postAccessExecutionCharge referenceAmsterdam
                                { original := Reference.readOriginal
                                    (toReferenceStore state.storage) ⟨state.executingAccount, key⟩
                                  current := Reference.readCurrent
                                    (toReferenceStore state.storage) ⟨state.executingAccount, key⟩
                                  newValue
                                  access := Reference.accessStatus
                                    (state.warmCells.map toReferenceCell)
                                    ⟨state.executingAccount, key⟩ })
                              gasAfterAccess = .ok gasAfterExecution := by
                            simpa [referenceInput] using referenceExecutionCharge
                          let stateCharge :=
                            (Generated.price Generated.amsterdamSchedule input).stateCharge
                          let referenceStateCharge :=
                            (SStore.price GasSchedule.amsterdam referenceInput).stateCharge
                          have stateChargeEquality : referenceStateCharge = stateCharge := by
                            simp only [referenceStateCharge, stateCharge]
                            rw [← inputEquality]
                            exact referenceStateCharge_refines input
                          cases stateGasCharge : GasMachine.chargeState stateCharge gasAfterExecution with
                          | error fault =>
                              have stateGasChargeAtCell : GasMachine.chargeState
                                  (Generated.price Generated.amsterdamSchedule
                                    { original := Generated.readOriginal state.storage
                                        ⟨state.executingAccount, key⟩
                                      current := Generated.readCurrent state.storage
                                        ⟨state.executingAccount, key⟩
                                      newValue
                                      access := Generated.accessStatus state.warmCells
                                        ⟨state.executingAccount, key⟩ }).stateCharge
                                  gasAfterExecution = .error fault := by
                                simpa [stateCharge, input, original, current, access, cell] using
                                  stateGasCharge
                              have referenceStateGasCharge : GasMachine.chargeState
                                  referenceStateCharge gasAfterExecution = .error fault := by
                                rw [stateChargeEquality]
                                exact stateGasCharge
                              have referenceStateGasChargeAtCell : GasMachine.chargeState
                                  (SStore.price GasSchedule.amsterdam
                                    { original := Reference.readOriginal
                                        (toReferenceStore state.storage) ⟨state.executingAccount, key⟩
                                      current := Reference.readCurrent
                                        (toReferenceStore state.storage) ⟨state.executingAccount, key⟩
                                      newValue
                                      access := Reference.accessStatus
                                        (state.warmCells.map toReferenceCell)
                                        ⟨state.executingAccount, key⟩ }).stateCharge
                                  gasAfterExecution = .error fault := by
                                simpa [referenceStateCharge, referenceInput] using
                                  referenceStateGasCharge
                              simp only [if_false, valuePop, accessChargeAtCell, noChangeAtCell,
                                executionChargeAtCell, stateGasChargeAtCell]
                              simp [notStipend, keyPop, valuePop, accessChargeAtCell,
                                referenceChange, referenceExecutionChargeAtCell,
                                referenceStateGasChargeAtCell, cell, access, accessCostEquality,
                                warmCellsEquality, currentEquality, originalEquality,
                                toReferenceOutcome, toReferenceStatus, toReferenceState,
                                toReferenceCell]
                              exact ⟨accessStatusAtCellExpanded, by simpa [mappedInput, referenceInput] using
                                mappedExecutionWriteEquality⟩
                          | ok gasAfterState =>
                              have stateGasChargeAtCell : GasMachine.chargeState
                                  (Generated.price Generated.amsterdamSchedule
                                    { original := Generated.readOriginal state.storage
                                        ⟨state.executingAccount, key⟩
                                      current := Generated.readCurrent state.storage
                                        ⟨state.executingAccount, key⟩
                                      newValue
                                      access := Generated.accessStatus state.warmCells
                                        ⟨state.executingAccount, key⟩ }).stateCharge
                                  gasAfterExecution = .ok gasAfterState := by
                                simpa [stateCharge, input, original, current, access, cell] using
                                  stateGasCharge
                              have referenceStateGasCharge : GasMachine.chargeState
                                  referenceStateCharge gasAfterExecution = .ok gasAfterState := by
                                rw [stateChargeEquality]
                                exact stateGasCharge
                              have referenceStateGasChargeAtCell : GasMachine.chargeState
                                  (SStore.price GasSchedule.amsterdam
                                    { original := Reference.readOriginal
                                        (toReferenceStore state.storage) ⟨state.executingAccount, key⟩
                                      current := Reference.readCurrent
                                        (toReferenceStore state.storage) ⟨state.executingAccount, key⟩
                                      newValue
                                      access := Reference.accessStatus
                                        (state.warmCells.map toReferenceCell)
                                        ⟨state.executingAccount, key⟩ }).stateCharge
                                  gasAfterExecution = .ok gasAfterState := by
                                simpa [referenceStateCharge, referenceInput] using
                                  referenceStateGasCharge
                              simp only [if_false, valuePop, accessChargeAtCell, noChangeAtCell,
                                executionChargeAtCell, stateGasChargeAtCell]
                              simp [notStipend, keyPop, valuePop, accessChargeAtCell,
                                referenceChange, referenceExecutionChargeAtCell,
                                referenceStateGasChargeAtCell, cell, access,
                                mappedSituationAtCell, mappedExecutionWriteAtCell,
                                mappedStateChargeAtCell, accessCostEquality, warmCellsEquality,
                                currentEquality, originalEquality, toReferenceOutcome,
                                toReferenceStatus, commitSStore_refines,
                                toReferenceCell] <;>
                                rw [accessStatusAtCellExpanded]

theorem opcode_bytes_injective : Function.Injective Generated.opcodeByte := by
  intro left right equality
  cases left <;> cases right <;> simp_all [Generated.opcodeByte]

theorem dispatch_specializations_exact :
    Generated.dispatchSpecializations =
      [ { opcode := .sload, table := "NoTrace", tracingFlag := "OffFlag",
          cancellationFlag := "OffFlag",
          closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SLoadOpcode<OffFlag,Eip8038On,OnFlag>,OffFlag,OffFlag,OnFlag>" },
        { opcode := .sstore, table := "NoTrace", tracingFlag := "OffFlag",
          cancellationFlag := "OffFlag",
          closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SStoreMeteredOpcode<OffFlag,OnFlag,OnFlag,Eip8038On,OnFlag>,OffFlag,OffFlag,OnFlag>" },
        { opcode := .sload, table := "NoTraceCancelable", tracingFlag := "OffFlag",
          cancellationFlag := "OnFlag",
          closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SLoadOpcode<OffFlag,Eip8038On,OnFlag>,OffFlag,OnFlag,OnFlag>" },
        { opcode := .sstore, table := "NoTraceCancelable", tracingFlag := "OffFlag",
          cancellationFlag := "OnFlag",
          closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SStoreMeteredOpcode<OffFlag,OnFlag,OnFlag,Eip8038On,OnFlag>,OffFlag,OnFlag,OnFlag>" },
        { opcode := .sload, table := "Traced", tracingFlag := "OnFlag",
          cancellationFlag := "OffFlag",
          closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SLoadOpcode<OnFlag,Eip8038On,OnFlag>,OnFlag,OffFlag,OnFlag>" },
        { opcode := .sstore, table := "Traced", tracingFlag := "OnFlag",
          cancellationFlag := "OffFlag",
          closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SStoreMeteredOpcode<OnFlag,OnFlag,OnFlag,Eip8038On,OnFlag>,OnFlag,OffFlag,OnFlag>" },
        { opcode := .sload, table := "TracedCancelable", tracingFlag := "OnFlag",
          cancellationFlag := "OnFlag",
          closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SLoadOpcode<OnFlag,Eip8038On,OnFlag>,OnFlag,OnFlag,OnFlag>" },
        { opcode := .sstore, table := "TracedCancelable", tracingFlag := "OnFlag",
          cancellationFlag := "OnFlag",
          closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SStoreMeteredOpcode<OnFlag,OnFlag,OnFlag,Eip8038On,OnFlag>,OnFlag,OnFlag,OnFlag>" } ] := by
  rfl

theorem dispatch_specialization_keys_unique :
    (Generated.dispatchSpecializations.map (fun specialization =>
      (specialization.opcode, specialization.table))).Nodup := by
  decide

theorem dispatch_roots_have_no_placeholder :
    ∀ specialization ∈ Generated.dispatchSpecializations,
      specialization.closedRoot.contains '/' = false := by
  simp [Generated.dispatchSpecializations]

theorem sload_byte_refines (state : Generated.MachineState) :
    toReferenceOutcome (Generated.execute Generated.amsterdamSchedule false 0x54 state) =
      some (Reference.executeSLoad referenceAmsterdam (toReferenceState state)) := by
  simpa [Generated.execute, Generated.opcodeByte] using
    execute_sload_refines false rfl state

theorem sstore_byte_refines (isStatic : Bool) (state : Generated.MachineState) :
    toReferenceOutcome (Generated.execute Generated.amsterdamSchedule isStatic 0x55 state) =
      some (Reference.executeSStore referenceAmsterdam isStatic (toReferenceState state)) := by
  simpa [Generated.execute, Generated.opcodeByte] using
    execute_sstore_refines false isStatic rfl state

theorem sload_advances_pc (schedule : Generated.Schedule) (state : Generated.MachineState) :
    (Generated.executeSLoad schedule state).state.pc = state.pc + 1 := by
  unfold Generated.executeSLoad
  cases charge : GasMachine.chargeExecution schedule.sloadBase state.gas with
  | error fault => simp [charge, Generated.advancePc, Generated.exhaustExecutionGas]
  | ok gasAfter =>
      cases keyPop : state.stack.pop with
      | none => simp [charge, keyPop, Generated.advancePc, Generated.record]
      | some pair =>
          rcases pair with ⟨key, tail⟩
          cases accessCharge : GasMachine.chargeExecution
              (Generated.accessCost schedule
                (Generated.accessStatus state.warmCells ⟨state.executingAccount, key⟩)) gasAfter with
          | error fault => simp [charge, keyPop, accessCharge, Generated.advancePc,
              Generated.record, Generated.exhaustExecutionGas]
          | ok gasAfterAccess =>
              cases push : tail.push (Generated.readCurrent state.storage ⟨state.executingAccount, key⟩) <;>
                simp [charge, keyPop, accessCharge, push, Generated.advancePc, Generated.record]

theorem sstore_advances_pc (schedule : Generated.Schedule) (isStatic : Bool)
    (state : Generated.MachineState) :
    (Generated.executeSStore schedule isStatic state).state.pc = state.pc + 1 := by
  unfold Generated.executeSStore
  cases isStatic with
  | true => simp [Generated.advancePc]
  | false =>
      simp only [Bool.false_eq_true, if_false]
      by_cases stipend : state.gas.gasLeft ≤ schedule.sstoreStipend
      · simp [stipend, Generated.advancePc]
      · cases keyPop : state.stack.pop with
        | none => simp [stipend, keyPop, Generated.advancePc]
        | some keyPair =>
            rcases keyPair with ⟨key, afterKey⟩
            cases valuePop : afterKey.pop with
            | none => simp [stipend, keyPop, valuePop, Generated.advancePc, Generated.record]
            | some valuePair =>
                rcases valuePair with ⟨newValue, stackAfter⟩
                let cell : Generated.Cell := ⟨state.executingAccount, key⟩
                let access := Generated.accessStatus state.warmCells cell
                cases accessCharge : GasMachine.chargeExecution
                    (Generated.accessCost schedule access) state.gas with
                | error fault =>
                    simp [stipend, keyPop, valuePop, accessCharge, cell, access,
                      Generated.advancePc, Generated.record, Generated.exhaustExecutionGas]
                | ok gasAfterAccess =>
                    let current := Generated.readCurrent state.storage cell
                    by_cases noChange : newValue = current
                    · simp [stipend, keyPop, valuePop, accessCharge, noChange, cell, access, current,
                        Generated.advancePc, Generated.record]
                    · let original := Generated.readOriginal state.storage cell
                      let input : Generated.StorageSituation :=
                        { original, current, newValue, access }
                      let executionWrite := Generated.postAccessExecutionCharge schedule input
                      cases executionCharge : GasMachine.chargeExecution executionWrite gasAfterAccess with
                      | error fault =>
                          simp [stipend, keyPop, valuePop, accessCharge, noChange, cell, access, current,
                            original, input, executionWrite, executionCharge,
                            Generated.advancePc, Generated.record, Generated.exhaustExecutionGas]
                      | ok gasAfterExecution =>
                          let stateCharge := (Generated.price schedule input).stateCharge
                          cases stateGasCharge : GasMachine.chargeState stateCharge gasAfterExecution with
                          | error fault =>
                              simp [stipend, keyPop, valuePop, accessCharge, noChange, cell, access,
                                current, original, input, executionWrite, executionCharge,
                                stateCharge, stateGasCharge, Generated.advancePc, Generated.record]
                          | ok gasAfterState =>
                              simp only [Generated.advancePc]
                              simp only [stipend, if_false, keyPop]
                              simp only [Generated.record]
                              simp only [valuePop]
                              simp only [cell, access, accessCharge, current, noChange, if_false,
                                original, input, executionWrite, executionCharge, stateCharge,
                                stateGasCharge]
                              exact commitSStore_preserves_pc schedule input cell newValue _

end PersistentStorageOpcodeExtractor.Refinement.PersistentStorageOpcode
