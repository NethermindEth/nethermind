-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import TransientStorageOpcodeExtractor.Generated.TransientStorageOpcodeKernel
import Eip803x.Evm.TransientStorageStack

namespace TransientStorageOpcodeExtractor.Refinement.TransientStorageOpcode

namespace Generated
export Eip803x.Generated.TransientStorageOpcodeKernel
  (Schedule amsterdamSchedule Opcode allOpcodes opcodeByte DispatchSpecialization
   dispatchSpecializations fixedGas Cell Store read write Frame load store MachineState Status
   Outcome withGas exhaustExecutionGas advancePc executeTLoad executeTStore execute)
end Generated

namespace ReferenceStore
export Eip803x.Evm.TransientStorage (Cell Store read write Frame load store)
end ReferenceStore

namespace Reference
export Eip803x.Evm.TransientStorageStack
  (MachineState Status Outcome withGas exhaustExecutionGas executeTLoad executeTStore)
end Reference

open Eip803x
open Eip803x.Evm.MemoryStackControl

def toReferenceCell (cell : Generated.Cell) : ReferenceStore.Cell :=
  ⟨cell.address, cell.key⟩

def toReferenceStore : Generated.Store → ReferenceStore.Store
  | [] => []
  | (cell, value) :: tail => (toReferenceCell cell, value) :: toReferenceStore tail

def toReferenceFrame (frame : Generated.Frame) : ReferenceStore.Frame :=
  { checkpoint := toReferenceStore frame.checkpoint
    current := toReferenceStore frame.current }

def toReferenceState (state : Generated.MachineState) : Reference.MachineState :=
  { gas := state.gas
    stack := state.stack
    transient := toReferenceFrame state.transient }

def toReferenceStatus : Generated.Status → Option Reference.Status
  | .ok => some .ok
  | .outOfGas => some .outOfGas
  | .stackUnderflow => some .stackUnderflow
  | .stackOverflow => some .stackOverflow
  | .staticCallViolation => some .staticCallViolation
  | .badInstruction => none

def toReferenceOutcome (outcome : Generated.Outcome) : Option Reference.Outcome := do
  let status ← toReferenceStatus outcome.status
  pure { status, state := toReferenceState outcome.state }

theorem toReferenceCell_injective : Function.Injective toReferenceCell := by
  intro left right equality
  cases left
  cases right
  cases equality
  rfl

@[simp] theorem toReferenceCell_eq (left right : Generated.Cell) :
    toReferenceCell left = toReferenceCell right ↔ left = right :=
  toReferenceCell_injective.eq_iff

theorem read_refines (store : Generated.Store) (cell : Generated.Cell) :
    Generated.read store cell = ReferenceStore.read (toReferenceStore store) (toReferenceCell cell) := by
  induction store with
  | nil => rfl
  | cons head tail inductionHypothesis =>
      rcases head with ⟨candidate, value⟩
      by_cases same : candidate = cell
      · simp [Generated.read, ReferenceStore.read, toReferenceStore, same]
      · simp [Generated.read, ReferenceStore.read, toReferenceStore, same, inductionHypothesis]

theorem write_refines (store : Generated.Store) (cell : Generated.Cell) (value : UInt256) :
    toReferenceStore (Generated.write store cell value) =
      ReferenceStore.write (toReferenceStore store) (toReferenceCell cell) value := by
  induction store with
  | nil => rfl
  | cons head tail inductionHypothesis =>
      rcases head with ⟨candidate, current⟩
      by_cases same : candidate = cell
      · simp [Generated.write, ReferenceStore.write, toReferenceStore, same]
      · simp [Generated.write, ReferenceStore.write, toReferenceStore, same, inductionHypothesis]

theorem load_refines (frame : Generated.Frame) (address key : UInt256) :
    Generated.load frame address key = ReferenceStore.load (toReferenceFrame frame) address key := by
  exact read_refines frame.current ⟨address, key⟩

theorem store_refines (frame : Generated.Frame) (address key value : UInt256) :
    toReferenceFrame (Generated.store frame address key value) =
      ReferenceStore.store (toReferenceFrame frame) address key value := by
  simp [Generated.store, ReferenceStore.store, toReferenceFrame, toReferenceCell, write_refines]

theorem execute_tload_refines (schedule : Generated.Schedule) (referenceSchedule : GasSchedule)
    (sameGas : schedule.warmAccess = referenceSchedule.warmAccess)
    (address : UInt256) (state : Generated.MachineState) :
    toReferenceOutcome (Generated.executeTLoad schedule address state) =
      some (Reference.executeTLoad referenceSchedule address (toReferenceState state)) := by
  simp only [Generated.executeTLoad, Reference.executeTLoad]
  rw [sameGas]
  simp only [Generated.advancePc]
  cases charge : Eip803x.GasMachine.chargeExecution referenceSchedule.warmAccess state.gas with
  | error fault => simp [charge, Generated.exhaustExecutionGas,
      Reference.exhaustExecutionGas, toReferenceOutcome, toReferenceStatus, toReferenceState,
      toReferenceFrame]
  | ok gasAfter =>
      cases popped : state.stack.pop with
      | none => simp [charge, popped, Generated.withGas,
          Reference.withGas, toReferenceOutcome, toReferenceStatus, toReferenceState,
          toReferenceFrame]
      | some pair =>
          rcases pair with ⟨key, tail⟩
          simp only [charge, popped, toReferenceState]
          rw [load_refines state.transient address key]
          cases pushed : tail.push (ReferenceStore.load (toReferenceFrame state.transient) address key) <;>
            simp [Generated.withGas, Reference.withGas,
              toReferenceOutcome, toReferenceStatus, toReferenceState, toReferenceFrame]

theorem execute_tstore_refines (schedule : Generated.Schedule) (referenceSchedule : GasSchedule)
    (sameGas : schedule.warmAccess = referenceSchedule.warmAccess)
    (isStatic : Bool) (address : UInt256) (state : Generated.MachineState) :
    toReferenceOutcome (Generated.executeTStore schedule isStatic address state) =
      some (Reference.executeTStore referenceSchedule isStatic address (toReferenceState state)) := by
  cases isStatic with
  | true => simp [Generated.executeTStore, Reference.executeTStore, Generated.advancePc,
      toReferenceOutcome, toReferenceStatus, toReferenceState, toReferenceFrame]
  | false =>
      simp only [Generated.executeTStore, Reference.executeTStore, Bool.false_eq_true, if_false]
      rw [sameGas]
      cases charge : Eip803x.GasMachine.chargeExecution referenceSchedule.warmAccess state.gas with
      | error fault => simp [charge, Generated.advancePc, Generated.exhaustExecutionGas,
          Reference.exhaustExecutionGas, toReferenceOutcome, toReferenceStatus, toReferenceState,
          toReferenceFrame]
      | ok gasAfter =>
          cases keyPop : state.stack.pop with
          | none => simp [charge, keyPop, Generated.advancePc, Generated.withGas,
              Reference.withGas, toReferenceOutcome, toReferenceStatus, toReferenceState,
              toReferenceFrame]
          | some keyPair =>
              rcases keyPair with ⟨key, afterKey⟩
              cases valuePop : afterKey.pop with
              | none => simp [charge, keyPop, valuePop, Generated.advancePc, Generated.withGas,
                  Reference.withGas, toReferenceOutcome, toReferenceStatus, toReferenceState,
                  toReferenceFrame]
              | some valuePair =>
                  rcases valuePair with ⟨value, stackAfter⟩
                  simp [charge, keyPop, valuePop, Generated.advancePc, Generated.withGas,
                    Reference.withGas, toReferenceOutcome, toReferenceStatus, toReferenceState,
                    toReferenceFrame]
                  exact store_refines state.transient address key value

theorem amsterdam_tload_refines (address : UInt256) (state : Generated.MachineState) :
    toReferenceOutcome (Generated.executeTLoad Generated.amsterdamSchedule address state) =
      some (Reference.executeTLoad GasSchedule.amsterdam address (toReferenceState state)) := by
  apply execute_tload_refines
  rfl

theorem amsterdam_tstore_refines (isStatic : Bool) (address : UInt256)
    (state : Generated.MachineState) :
    toReferenceOutcome (Generated.executeTStore Generated.amsterdamSchedule isStatic address state) =
      some (Reference.executeTStore GasSchedule.amsterdam isStatic address (toReferenceState state)) := by
  apply execute_tstore_refines
  rfl

theorem opcode_bytes_injective : Function.Injective Generated.opcodeByte := by
  intro left right equality
  cases left <;> cases right <;> simp_all [Generated.opcodeByte]

theorem dispatch_specializations_exact :
    Generated.dispatchSpecializations =
      [ { opcode := .tload
          table := "NoTrace"
          tracingFlag := "OffFlag"
          cancellationFlag := "OffFlag"
          closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<TLoadOpcode<OffFlag>,OffFlag,OffFlag,OnFlag>" },
        { opcode := .tstore
          table := "NoTrace"
          tracingFlag := "OffFlag"
          cancellationFlag := "OffFlag"
          closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<TStoreOpcode,OffFlag,OffFlag,OnFlag>" },
        { opcode := .tload
          table := "NoTraceCancelable"
          tracingFlag := "OffFlag"
          cancellationFlag := "OnFlag"
          closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<TLoadOpcode<OffFlag>,OffFlag,OnFlag,OnFlag>" },
        { opcode := .tstore
          table := "NoTraceCancelable"
          tracingFlag := "OffFlag"
          cancellationFlag := "OnFlag"
          closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<TStoreOpcode,OffFlag,OnFlag,OnFlag>" },
        { opcode := .tload
          table := "Traced"
          tracingFlag := "OnFlag"
          cancellationFlag := "OffFlag"
          closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<TLoadOpcode<OnFlag>,OnFlag,OffFlag,OnFlag>" },
        { opcode := .tstore
          table := "Traced"
          tracingFlag := "OnFlag"
          cancellationFlag := "OffFlag"
          closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<TStoreOpcode,OnFlag,OffFlag,OnFlag>" },
        { opcode := .tload
          table := "TracedCancelable"
          tracingFlag := "OnFlag"
          cancellationFlag := "OnFlag"
          closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<TLoadOpcode<OnFlag>,OnFlag,OnFlag,OnFlag>" },
        { opcode := .tstore
          table := "TracedCancelable"
          tracingFlag := "OnFlag"
          cancellationFlag := "OnFlag"
          closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<TStoreOpcode,OnFlag,OnFlag,OnFlag>" } ] := by
  rfl

theorem dispatch_specialization_keys_complete :
    Generated.dispatchSpecializations.map (fun specialization =>
      (specialization.opcode, specialization.table)) =
      [ (.tload, "NoTrace"), (.tstore, "NoTrace"),
        (.tload, "NoTraceCancelable"), (.tstore, "NoTraceCancelable"),
        (.tload, "Traced"), (.tstore, "Traced"),
        (.tload, "TracedCancelable"), (.tstore, "TracedCancelable") ] := by
  rfl

theorem dispatch_specialization_keys_unique :
    (Generated.dispatchSpecializations.map (fun specialization =>
      (specialization.opcode, specialization.table))).Nodup := by
  decide

theorem dispatch_roots_have_no_placeholder :
    ∀ specialization ∈ Generated.dispatchSpecializations,
      specialization.closedRoot.contains '/' = false := by
  simp [Generated.dispatchSpecializations]

theorem tload_byte_refines (address : UInt256) (state : Generated.MachineState) :
    toReferenceOutcome
        (Generated.execute Generated.amsterdamSchedule true false address 0x5c state) =
      some (Reference.executeTLoad GasSchedule.amsterdam address (toReferenceState state)) := by
  simpa [Generated.execute, Generated.opcodeByte] using amsterdam_tload_refines address state

theorem tstore_byte_refines (isStatic : Bool) (address : UInt256)
    (state : Generated.MachineState) :
    toReferenceOutcome
        (Generated.execute Generated.amsterdamSchedule true isStatic address 0x5d state) =
      some (Reference.executeTStore GasSchedule.amsterdam isStatic address (toReferenceState state)) := by
  simpa [Generated.execute, Generated.opcodeByte] using amsterdam_tstore_refines isStatic address state

theorem disabled_opcode_is_bad (schedule : Generated.Schedule) (isStatic : Bool)
    (address : UInt256) (opcode : Nat) (state : Generated.MachineState) :
    (Generated.execute schedule false isStatic address opcode state).status = .badInstruction := by
  simp [Generated.execute]

theorem tload_advances_pc (schedule : Generated.Schedule) (address : UInt256)
    (state : Generated.MachineState) :
    (Generated.executeTLoad schedule address state).state.pc = state.pc + 1 := by
  unfold Generated.executeTLoad
  cases charge : Eip803x.GasMachine.chargeExecution schedule.warmAccess state.gas with
  | error fault => simp [charge, Generated.advancePc, Generated.exhaustExecutionGas]
  | ok gasAfter =>
      cases popped : state.stack.pop with
      | none => simp [charge, popped, Generated.advancePc, Generated.withGas]
      | some pair =>
          rcases pair with ⟨key, tail⟩
          cases pushed : tail.push (Generated.load state.transient address key) <;>
            simp [charge, popped, pushed, Generated.advancePc, Generated.withGas]

theorem tstore_advances_pc (schedule : Generated.Schedule) (isStatic : Bool)
    (address : UInt256) (state : Generated.MachineState) :
    (Generated.executeTStore schedule isStatic address state).state.pc = state.pc + 1 := by
  unfold Generated.executeTStore
  cases isStatic with
  | true => simp [Generated.advancePc]
  | false =>
      simp only [Bool.false_eq_true, if_false]
      cases charge : Eip803x.GasMachine.chargeExecution schedule.warmAccess state.gas with
      | error fault => simp [charge, Generated.advancePc, Generated.exhaustExecutionGas]
      | ok gasAfter =>
          cases keyPop : state.stack.pop with
          | none => simp [charge, keyPop, Generated.advancePc, Generated.withGas]
          | some keyPair =>
              rcases keyPair with ⟨key, afterKey⟩
              cases valuePop : afterKey.pop <;>
                simp [charge, keyPop, valuePop, Generated.advancePc, Generated.withGas]

end TransientStorageOpcodeExtractor.Refinement.TransientStorageOpcode
