-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import FrameJournalExtractor.Generated.FrameJournalKernel
import FrameJournalExtractor.Specification.FrameJournal
import WorldJournalExtractor.Refinement.WorldJournal
import PersistentStorageOpcodeExtractor.Refinement.PersistentStorageOpcode
import TransientStorageOpcodeExtractor.Refinement.TransientStorageOpcode
import LogOpcodeExtractor.Refinement.LogOpcode
import Eip803x.Evm.CallCreateFrame
import Eip803x.Evm.SelfDestruct

namespace FrameJournalExtractor.Refinement.FrameJournal

open FrameJournalExtractor
open WorldJournalExtractor

namespace Generated
export FrameJournalExtractor.Generated.FrameJournalKernel
  (admittedOperationNames admittedDependencyTheorems markCreated enter retainAndPop
    reapplyRipemdTouch restoreAndPop transition executeTrace)
end Generated

namespace Spec
export FrameJournalExtractor.Specification
  (markCreated enter retainAndPop reapplyRipemdTouch restoreAndPop step run)
end Spec

#check @WorldJournalExtractor.Refinement.WorldJournal.transition_refines
#check @WorldJournalExtractor.Refinement.WorldJournal.finite_trace_refines
#check @PersistentStorageOpcodeExtractor.Refinement.PersistentStorageOpcode.sload_byte_refines
#check @PersistentStorageOpcodeExtractor.Refinement.PersistentStorageOpcode.sstore_byte_refines
#check @TransientStorageOpcodeExtractor.Refinement.TransientStorageOpcode.tload_byte_refines
#check @TransientStorageOpcodeExtractor.Refinement.TransientStorageOpcode.tstore_byte_refines
#check @LogOpcodeExtractor.Refinement.generated_execute_refines_reference
#check @Eip803x.Evm.CallCreateFrame.call_failure_constructor_has_no_child
#check @Eip803x.Evm.CallCreateFrame.create_failure_constructor_has_no_state_charge
#check @Eip803x.Evm.CallCreateFrame.revert_returns_exact_execution_and_restores_world
#check @Eip803x.Evm.CallCreateFrame.completion_preserves_enclosing_checkpoint
#check @Eip803x.Evm.SelfDestruct.destroy_marked_exactly_for_same_transaction_creation
#check @Eip803x.Evm.SelfDestruct.successful_other_target_moves_exact_balance

theorem admitted_operation_names_exact : Generated.admittedOperationNames =
    ["applyWorld", "enterCall", "enterCreate", "preFrameCallFailure",
      "preFrameCreateFailure", "latchRipemdTouch", "exitSuccess", "exitRevert",
      "exitException"] := by
  rfl

theorem dependency_theorem_count : Generated.admittedDependencyTheorems.length = 13 := by
  native_decide

theorem mark_created_refines (world : WorldMachine) (address : Address) :
    Generated.markCreated world address = Spec.markCreated world address := by
  rfl

theorem enter_refines (kind : FrameKind) (machine : Machine) :
    Generated.enter kind machine = Spec.enter kind machine := by
  cases kind <;>
    simp [Generated.enter, Spec.enter, mark_created_refines,
      WorldJournalExtractor.Refinement.WorldJournal.transition_refines] <;>
    rfl

theorem retain_and_pop_refines (machine : Machine) :
    Generated.retainAndPop machine = Spec.retainAndPop machine := by
  rfl

theorem reapply_ripemd_touch_refines (machine : Machine) :
    Generated.reapplyRipemdTouch machine = Spec.reapplyRipemdTouch machine := by
  simp [Generated.reapplyRipemdTouch, Spec.reapplyRipemdTouch,
    WorldJournalExtractor.Refinement.WorldJournal.transition_refines]
  rfl

theorem restore_and_pop_refines (machine : Machine) :
    Generated.restoreAndPop machine = Spec.restoreAndPop machine := by
  unfold Generated.restoreAndPop Spec.restoreAndPop
  cases machine.frames with
  | nil => rfl
  | cons kind frames =>
      rw [WorldJournalExtractor.Refinement.WorldJournal.transition_refines]
      cases restored : WorldJournalExtractor.Specification.step
          .restoreSnapshot machine.world with
      | none => rfl
      | some world => exact reapply_ripemd_touch_refines { machine with world, frames }

theorem transition_refines (operation : Operation) (machine : Machine) :
    Generated.transition operation machine = Spec.step operation machine := by
  cases operation with
  | applyWorld worldOperation =>
      cases worldOperation <;>
        simp [Generated.transition, Spec.step, isBodyOperation,
          WorldJournalExtractor.Refinement.WorldJournal.transition_refines]
  | enterCall => simp [Generated.transition, Spec.step, enter_refines]
  | enterCreate address => simp [Generated.transition, Spec.step, enter_refines]
  | preFrameCallFailure => rfl
  | preFrameCreateFailure => rfl
  | latchRipemdTouch => rfl
  | exitSuccess => simp [Generated.transition, Spec.step, retain_and_pop_refines]
  | exitRevert => simp [Generated.transition, Spec.step, restore_and_pop_refines]
  | exitException => simp [Generated.transition, Spec.step, restore_and_pop_refines]

theorem finite_trace_refines (operations : List Operation) (machine : Machine) :
    Generated.executeTrace operations machine = Spec.run operations machine := by
  induction operations generalizing machine with
  | nil => rfl
  | cons operation tail inductionHypothesis =>
      simp only [Generated.executeTrace, Spec.run, transition_refines]
      cases result : Spec.step operation machine with
      | none => rfl
      | some next => exact inductionHypothesis next

private theorem body_world_transition_preserves_snapshots
    (operation : WorldOperation) (world next : WorldMachine)
    (body : isBodyOperation operation = true)
    (result : WorldJournalExtractor.Generated.WorldJournalKernel.transition
      operation world = some next) :
    next.snapshots = world.snapshots := by
  cases operation <;>
    simp_all [isBodyOperation,
      WorldJournalExtractor.Generated.WorldJournalKernel.transition,
      WorldJournalExtractor.Generated.WorldJournalKernel.record] <;>
    cases result <;> rfl

private theorem read_account_preserves_transaction_wide (world next : WorldMachine)
    (result : WorldJournalExtractor.Generated.WorldJournalKernel.transition
      (.readAccount ripemd160Address) world = some next) :
    next.journal.persistentOriginals = world.journal.persistentOriginals ∧
      next.journal.createdThisTx = world.journal.createdThisTx := by
  unfold WorldJournalExtractor.Generated.WorldJournalKernel.transition at result
  unfold WorldJournalExtractor.Generated.WorldJournalKernel.readAccountState at result
  cases head : WorldJournalExtractor.Generated.WorldJournalKernel.findAccountHead
      world.journal.accounts.changes ripemd160Address with
  | some change =>
      simp [head, WorldJournalExtractor.Generated.WorldJournalKernel.record] at result
      cases result
      exact ⟨rfl, rfl⟩
  | none =>
      cases backing : FiniteMap.lookup world.journal.accounts.initial ripemd160Address with
      | none =>
          simp [head, backing, WorldJournalExtractor.Generated.WorldJournalKernel.record] at result
          cases result
          exact ⟨rfl, rfl⟩
      | some account =>
          simp [head, backing, WorldJournalExtractor.Generated.WorldJournalKernel.record] at result
          cases result
          exact ⟨rfl, rfl⟩

private theorem update_account_preserves_transaction_wide (world next : WorldMachine)
    (account : AccountView)
    (result : WorldJournalExtractor.Generated.WorldJournalKernel.transition
      (.updateAccount ripemd160Address account) world = some next) :
    next.journal.persistentOriginals = world.journal.persistentOriginals ∧
      next.journal.createdThisTx = world.journal.createdThisTx := by
  simp [WorldJournalExtractor.Generated.WorldJournalKernel.transition,
    WorldJournalExtractor.Generated.WorldJournalKernel.replaceAccount] at result
  cases result
  exact ⟨rfl, rfl⟩

private theorem reapply_ripemd_touch_preserves_control (machine next : Machine)
    (result : Generated.reapplyRipemdTouch machine = some next) :
    next.frames = machine.frames ∧
      next.world.snapshots = machine.world.snapshots ∧
      next.world.journal.persistentOriginals = machine.world.journal.persistentOriginals ∧
      next.world.journal.createdThisTx = machine.world.journal.createdThisTx ∧
      next.ripemdTouchLatched = machine.ripemdTouchLatched := by
  unfold Generated.reapplyRipemdTouch at result
  cases latched : machine.ripemdTouchLatched with
  | false =>
      simp [latched] at result
      cases result
      exact ⟨rfl, rfl, rfl, rfl, latched⟩
  | true =>
      simp only [latched, if_true] at result
      cases readResult : WorldJournalExtractor.Generated.WorldJournalKernel.transition
          (.readAccount ripemd160Address) machine.world with
      | none => simp [readResult] at result
      | some readWorld =>
          have readPreserves := body_world_transition_preserves_snapshots
            (.readAccount ripemd160Address) machine.world readWorld rfl readResult
          have readTransactionWide := read_account_preserves_transaction_wide
            machine.world readWorld readResult
          simp only [readResult] at result
          cases accountResult : FiniteMap.lookup readWorld.journal.accounts.current
              ripemd160Address with
          | none =>
              simp [accountResult] at result
              cases result
              exact ⟨rfl, readPreserves, readTransactionWide.1,
                readTransactionWide.2, rfl⟩
          | some account =>
              simp only [accountResult] at result
              cases emptyResult : isEmptyAccount account with
              | false =>
                  simp [emptyResult] at result
                  cases result
                  exact ⟨rfl, readPreserves, readTransactionWide.1,
                    readTransactionWide.2, rfl⟩
              | true =>
                  cases updateResult : WorldJournalExtractor.Generated.WorldJournalKernel.transition
                      (.updateAccount ripemd160Address account) readWorld with
                  | none => simp [emptyResult, updateResult] at result
                  | some updatedWorld =>
                      have updatePreserves := body_world_transition_preserves_snapshots
                        (.updateAccount ripemd160Address account) readWorld updatedWorld rfl
                        updateResult
                      have updateTransactionWide := update_account_preserves_transaction_wide
                        readWorld updatedWorld account updateResult
                      simp [emptyResult, updateResult] at result
                      cases result
                      exact ⟨rfl, updatePreserves.trans readPreserves,
                        updateTransactionWide.1.trans readTransactionWide.1,
                        updateTransactionWide.2.trans readTransactionWide.2, rfl⟩

private theorem enter_preserves_control (kind : FrameKind) (machine next : Machine)
    (result : Generated.enter kind machine = some next) :
    next.frames = kind :: machine.frames ∧
      next.world.snapshots.length = machine.world.snapshots.length + 1 ∧
      ∃ snapshot, next.world.snapshots = snapshot :: machine.world.snapshots := by
  cases kind <;>
    simp [Generated.enter, Generated.markCreated,
      WorldJournalExtractor.Generated.WorldJournalKernel.transition,
      WorldJournalExtractor.Generated.WorldJournalKernel.snapshotOf,
      WorldJournalExtractor.Generated.WorldJournalKernel.record] at result <;>
    cases result <;> exact ⟨rfl, rfl, ⟨_, rfl⟩⟩

private theorem world_read_succeeds (world : WorldMachine) (address : Address) :
    ∃ next, WorldJournalExtractor.Generated.WorldJournalKernel.transition
      (.readAccount address) world = some next := by
  unfold WorldJournalExtractor.Generated.WorldJournalKernel.transition
  exact ⟨_, rfl⟩

private theorem world_update_succeeds (world : WorldMachine) (address : Address)
    (account : AccountView) :
    ∃ next, WorldJournalExtractor.Generated.WorldJournalKernel.transition
      (.updateAccount address account) world = some next := by
  unfold WorldJournalExtractor.Generated.WorldJournalKernel.transition
  exact ⟨_, rfl⟩

private theorem reapply_ripemd_touch_succeeds (machine : Machine) :
    ∃ next, Generated.reapplyRipemdTouch machine = some next := by
  cases latched : machine.ripemdTouchLatched with
  | false => exact ⟨machine, by simp [Generated.reapplyRipemdTouch, latched]⟩
  | true =>
      obtain ⟨readWorld, readResult⟩ := world_read_succeeds machine.world ripemd160Address
      cases accountResult : FiniteMap.lookup readWorld.journal.accounts.current
          ripemd160Address with
      | none =>
          exact ⟨{ machine with world := readWorld }, by
            simp [Generated.reapplyRipemdTouch, latched, readResult, accountResult]⟩
      | some account =>
          cases emptyResult : isEmptyAccount account with
          | false =>
              exact ⟨{ machine with world := readWorld }, by
                simp [Generated.reapplyRipemdTouch, latched, readResult, accountResult,
                  emptyResult]⟩
          | true =>
              obtain ⟨updatedWorld, updateResult⟩ :=
                world_update_succeeds readWorld ripemd160Address account
              exact ⟨{ machine with world := updatedWorld }, by
                simp [Generated.reapplyRipemdTouch, latched, readResult, accountResult,
                  emptyResult, updateResult]⟩

private theorem restore_and_pop_succeeds (machine : Machine)
    (frames : machine.frames ≠ []) (snapshots : machine.world.snapshots ≠ []) :
    ∃ next, Generated.restoreAndPop machine = some next := by
  cases frameResult : machine.frames with
  | nil => exact False.elim (frames frameResult)
  | cons kind tail =>
      cases snapshotResult : machine.world.snapshots with
      | nil => exact False.elim (snapshots snapshotResult)
      | cons snapshot snapshots =>
          let restoredWorld := WorldJournalExtractor.Generated.WorldJournalKernel.record
            { machine.world with
              journal := WorldJournalExtractor.Generated.WorldJournalKernel.rewindState
                machine.world.journal snapshot
              snapshots := snapshots }
            (.snapshotRestored snapshot)
          simpa [Generated.restoreAndPop, frameResult, snapshotResult,
            WorldJournalExtractor.Generated.WorldJournalKernel.transition, restoredWorld] using
            reapply_ripemd_touch_succeeds
              { machine with world := restoredWorld, frames := tail }

private theorem restore_and_pop_preserves_control (machine next : Machine)
    (result : Generated.restoreAndPop machine = some next) :
    next.frames = machine.frames.tail ∧
      next.world.snapshots = machine.world.snapshots.tail := by
  unfold Generated.restoreAndPop at result
  cases frames : machine.frames with
  | nil => simp [frames] at result
  | cons kind tail =>
      cases snapshots : machine.world.snapshots with
      | nil =>
          simp [frames, snapshots,
            WorldJournalExtractor.Generated.WorldJournalKernel.transition] at result
      | cons snapshot snapshots =>
          simp only [frames, snapshots,
            WorldJournalExtractor.Generated.WorldJournalKernel.transition] at result
          have control := reapply_ripemd_touch_preserves_control _ next result
          exact ⟨by simpa [frames] using control.1,
            by simpa [snapshots,
              WorldJournalExtractor.Generated.WorldJournalKernel.rewindState,
              WorldJournalExtractor.Generated.WorldJournalKernel.record] using control.2.1⟩

theorem transition_preserves_wellformed (operation : Operation) (machine next : Machine)
    (wellFormed : machine.WellFormed)
    (result : Generated.transition operation machine = some next) :
    next.WellFormed := by
  cases operation with
  | applyWorld worldOperation =>
      cases body : isBodyOperation worldOperation with
      | false => simp [Generated.transition, body] at result
      | true =>
          cases worldResult : WorldJournalExtractor.Generated.WorldJournalKernel.transition
              worldOperation machine.world with
          | none => simp [Generated.transition, body, worldResult] at result
          | some world =>
              simp [Generated.transition, body, worldResult] at result
              cases result
              unfold Machine.WellFormed at wellFormed ⊢
              rw [body_world_transition_preserves_snapshots
                worldOperation machine.world world body worldResult]
              exact wellFormed
  | enterCall =>
      simp [Generated.transition, Generated.enter, Machine.WellFormed,
        WorldJournalExtractor.Generated.WorldJournalKernel.transition,
        WorldJournalExtractor.Generated.WorldJournalKernel.snapshotOf,
        WorldJournalExtractor.Generated.WorldJournalKernel.record] at result ⊢
      cases result
      simpa [Machine.WellFormed] using congrArg Nat.succ wellFormed
  | enterCreate address =>
      simp [Generated.transition, Generated.enter, Generated.markCreated, Machine.WellFormed,
        WorldJournalExtractor.Generated.WorldJournalKernel.transition,
        WorldJournalExtractor.Generated.WorldJournalKernel.snapshotOf,
        WorldJournalExtractor.Generated.WorldJournalKernel.record] at result ⊢
      cases result
      simpa [Machine.WellFormed] using congrArg Nat.succ wellFormed
  | preFrameCallFailure =>
      simp [Generated.transition] at result
      cases result
      exact wellFormed
  | preFrameCreateFailure =>
      simp [Generated.transition] at result
      cases result
      exact wellFormed
  | latchRipemdTouch =>
      simp [Generated.transition] at result
      cases result
      exact wellFormed
  | exitSuccess =>
      unfold Generated.transition Generated.retainAndPop at result
      cases frames : machine.frames <;> cases snapshots : machine.world.snapshots <;>
        simp_all [Machine.WellFormed] <;>
        cases result <;> exact wellFormed
  | exitRevert =>
      unfold Generated.transition Generated.restoreAndPop at result
      cases frames : machine.frames with
      | nil => simp [frames] at result
      | cons kind tail =>
          cases snapshots : machine.world.snapshots with
          | nil => simp [frames, snapshots, Machine.WellFormed] at wellFormed
          | cons snapshot snapshots =>
              simp only [frames,
                WorldJournalExtractor.Generated.WorldJournalKernel.transition,
                snapshots] at result
              have control := reapply_ripemd_touch_preserves_control _ next result
              unfold Machine.WellFormed at wellFormed ⊢
              simp [frames, snapshots] at wellFormed
              simpa [WorldJournalExtractor.Generated.WorldJournalKernel.record] using
                Eq.trans (congrArg List.length control.1) (Eq.trans wellFormed
                  (congrArg List.length control.2.1).symm)
  | exitException =>
      unfold Generated.transition Generated.restoreAndPop at result
      cases frames : machine.frames with
      | nil => simp [frames] at result
      | cons kind tail =>
          cases snapshots : machine.world.snapshots with
          | nil => simp [frames, snapshots, Machine.WellFormed] at wellFormed
          | cons snapshot snapshots =>
              simp only [frames,
                WorldJournalExtractor.Generated.WorldJournalKernel.transition,
                snapshots] at result
              have control := reapply_ripemd_touch_preserves_control _ next result
              unfold Machine.WellFormed at wellFormed ⊢
              simp [frames, snapshots] at wellFormed
              simpa [WorldJournalExtractor.Generated.WorldJournalKernel.record] using
                Eq.trans (congrArg List.length control.1) (Eq.trans wellFormed
                  (congrArg List.length control.2.1).symm)

theorem finite_trace_preserves_wellformed (operations : List Operation) (machine next : Machine)
    (wellFormed : machine.WellFormed)
    (result : Generated.executeTrace operations machine = some next) :
    next.WellFormed := by
  induction operations generalizing machine with
  | nil =>
      simp [Generated.executeTrace] at result
      cases result
      exact wellFormed
  | cons operation tail inductionHypothesis =>
      simp only [Generated.executeTrace] at result
      cases stepResult : Generated.transition operation machine with
      | none => simp [stepResult] at result
      | some intermediate =>
          rw [stepResult] at result
          exact inductionHypothesis intermediate
            (transition_preserves_wellformed operation machine intermediate wellFormed stepResult)
            result

theorem pre_frame_call_failure_stutters (machine : Machine) :
    Generated.transition .preFrameCallFailure machine = some machine := by
  rfl

theorem pre_frame_create_failure_stutters (machine : Machine) :
    Generated.transition .preFrameCreateFailure machine = some machine := by
  rfl

theorem success_retains_journal (machine next : Machine)
    (result : Generated.transition .exitSuccess machine = some next) :
    next.world.journal = machine.world.journal := by
  unfold Generated.transition Generated.retainAndPop at result
  cases frames : machine.frames with
  | nil => simp [frames] at result
  | cons kind tail =>
      cases snapshots : machine.world.snapshots with
      | nil => simp [frames, snapshots] at result
      | cons snapshot snapshots =>
          simp [frames, snapshots] at result
          cases result
          rfl

theorem revert_and_exception_have_same_journal_result (machine : Machine) :
    (Generated.transition .exitRevert machine).map (fun next => next.world.journal) =
      (Generated.transition .exitException machine).map (fun next => next.world.journal) := by
  rfl

theorem revert_delegates_exact_world_restore (machine : Machine) (kind : FrameKind)
    (frames : List FrameKind) (hasFrame : machine.frames = kind :: frames) :
    Generated.transition .exitRevert machine =
      (WorldJournalExtractor.Generated.WorldJournalKernel.transition
        .restoreSnapshot machine.world).bind fun world =>
          Generated.reapplyRipemdTouch { machine with world, frames } := by
  simp only [Generated.transition, Generated.restoreAndPop, hasFrame]
  cases WorldJournalExtractor.Generated.WorldJournalKernel.transition
      .restoreSnapshot machine.world <;> rfl

theorem exception_delegates_exact_world_restore (machine : Machine) (kind : FrameKind)
    (frames : List FrameKind) (hasFrame : machine.frames = kind :: frames) :
    Generated.transition .exitException machine =
      (WorldJournalExtractor.Generated.WorldJournalKernel.transition
        .restoreSnapshot machine.world).bind fun world =>
          Generated.reapplyRipemdTouch { machine with world, frames } := by
  simp only [Generated.transition, Generated.restoreAndPop, hasFrame]
  cases WorldJournalExtractor.Generated.WorldJournalKernel.transition
      .restoreSnapshot machine.world <;> rfl

theorem nested_revert_pops_only_inner_frame (machine : Machine) (address : Address) :
    (Generated.executeTrace [.enterCall, .enterCreate address, .exitRevert] machine).map
      (fun next => next.frames) = some (.call :: machine.frames) := by
  simp only [Generated.executeTrace]
  cases callResult : Generated.transition .enterCall machine with
  | none =>
      simp [Generated.transition, Generated.enter,
        WorldJournalExtractor.Generated.WorldJournalKernel.transition] at callResult
  | some afterCall =>
      cases createResult : Generated.transition (.enterCreate address) afterCall with
      | none =>
          simp [Generated.transition, Generated.enter,
            WorldJournalExtractor.Generated.WorldJournalKernel.transition] at createResult
      | some afterCreate =>
          have callControl := enter_preserves_control .call machine afterCall
            (by simpa [Generated.transition] using callResult)
          have createControl := enter_preserves_control (.create address) afterCall afterCreate
            (by simpa [Generated.transition] using createResult)
          cases exitResult : Generated.transition .exitRevert afterCreate with
          | none =>
              have frameNonempty : afterCreate.frames ≠ [] := by
                rw [createControl.1]
                simp
              obtain ⟨snapshot, snapshotResult⟩ := createControl.2.2
              have snapshotNonempty : afterCreate.world.snapshots ≠ [] := by
                rw [snapshotResult]
                simp
              obtain ⟨restored, restoreResult⟩ :=
                restore_and_pop_succeeds afterCreate frameNonempty snapshotNonempty
              have successfulExit : Generated.transition .exitRevert afterCreate = some restored := by
                simpa [Generated.transition] using restoreResult
              rw [successfulExit] at exitResult
              contradiction
          | some next =>
              have exitControl := restore_and_pop_preserves_control afterCreate next
                (by simpa [Generated.transition] using exitResult)
              simp [createResult, exitResult, exitControl.1, createControl.1, callControl.1]

theorem nested_revert_pops_only_inner_snapshot (machine : Machine) (address : Address) :
    (Generated.executeTrace [.enterCall, .enterCreate address, .exitRevert] machine).map
      (fun next => next.world.snapshots.length) = some (machine.world.snapshots.length + 1) := by
  simp only [Generated.executeTrace]
  cases callResult : Generated.transition .enterCall machine with
  | none =>
      simp [Generated.transition, Generated.enter,
        WorldJournalExtractor.Generated.WorldJournalKernel.transition] at callResult
  | some afterCall =>
      cases createResult : Generated.transition (.enterCreate address) afterCall with
      | none =>
          simp [Generated.transition, Generated.enter,
            WorldJournalExtractor.Generated.WorldJournalKernel.transition] at createResult
      | some afterCreate =>
          have callControl := enter_preserves_control .call machine afterCall
            (by simpa [Generated.transition] using callResult)
          have createControl := enter_preserves_control (.create address) afterCall afterCreate
            (by simpa [Generated.transition] using createResult)
          cases exitResult : Generated.transition .exitRevert afterCreate with
          | none =>
              have frameNonempty : afterCreate.frames ≠ [] := by
                rw [createControl.1]
                simp
              obtain ⟨snapshot, snapshotResult⟩ := createControl.2.2
              have snapshotNonempty : afterCreate.world.snapshots ≠ [] := by
                rw [snapshotResult]
                simp
              obtain ⟨restored, restoreResult⟩ :=
                restore_and_pop_succeeds afterCreate frameNonempty snapshotNonempty
              have successfulExit : Generated.transition .exitRevert afterCreate = some restored := by
                simpa [Generated.transition] using restoreResult
              rw [successfulExit] at exitResult
              contradiction
          | some next =>
              have exitControl := restore_and_pop_preserves_control afterCreate next
                (by simpa [Generated.transition] using exitResult)
              simp [createResult, exitResult, exitControl.2, createControl.2.1,
                callControl.2.1]

theorem restore_preserves_originals (machine next : Machine)
    (result : Generated.transition .exitRevert machine = some next) :
    next.world.journal.persistentOriginals = machine.world.journal.persistentOriginals := by
  unfold Generated.transition Generated.restoreAndPop at result
  cases frames : machine.frames with
  | nil => simp [frames] at result
  | cons kind tail =>
      cases snapshots : machine.world.snapshots with
      | nil => simp [frames, snapshots, WorldJournalExtractor.Generated.WorldJournalKernel.transition] at result
      | cons snapshot snapshots =>
          simp only [frames, snapshots,
            WorldJournalExtractor.Generated.WorldJournalKernel.transition] at result
          have control := reapply_ripemd_touch_preserves_control _ next result
          simpa [WorldJournalExtractor.Generated.WorldJournalKernel.rewindState,
            WorldJournalExtractor.Generated.WorldJournalKernel.record] using control.2.2.1

theorem restore_preserves_created_this_transaction (machine next : Machine)
    (result : Generated.transition .exitException machine = some next) :
    next.world.journal.createdThisTx = machine.world.journal.createdThisTx := by
  unfold Generated.transition Generated.restoreAndPop at result
  cases frames : machine.frames with
  | nil => simp [frames] at result
  | cons kind tail =>
      cases snapshots : machine.world.snapshots with
      | nil => simp [frames, snapshots, WorldJournalExtractor.Generated.WorldJournalKernel.transition] at result
      | cons snapshot snapshots =>
          simp only [frames, snapshots,
            WorldJournalExtractor.Generated.WorldJournalKernel.transition] at result
          have control := reapply_ripemd_touch_preserves_control _ next result
          simpa [WorldJournalExtractor.Generated.WorldJournalKernel.rewindState,
            WorldJournalExtractor.Generated.WorldJournalKernel.record] using control.2.2.2.1

theorem restore_preserves_ripemd_touch_latch (machine next : Machine)
    (result : Generated.transition .exitRevert machine = some next) :
    next.ripemdTouchLatched = machine.ripemdTouchLatched := by
  unfold Generated.transition Generated.restoreAndPop at result
  cases frames : machine.frames with
  | nil => simp [frames] at result
  | cons kind tail =>
      cases snapshots : machine.world.snapshots with
      | nil => simp [frames, snapshots,
          WorldJournalExtractor.Generated.WorldJournalKernel.transition] at result
      | cons snapshot snapshots =>
          simp only [frames, snapshots,
            WorldJournalExtractor.Generated.WorldJournalKernel.transition] at result
          have control := reapply_ripemd_touch_preserves_control _ next result
          simpa [WorldJournalExtractor.Generated.WorldJournalKernel.rewindState,
            WorldJournalExtractor.Generated.WorldJournalKernel.record] using control.2.2.2.2

theorem ripemd_touch_latch_is_transaction_wide (machine : Machine) :
    (Generated.transition .latchRipemdTouch machine).map
      (fun next => next.ripemdTouchLatched) = some true := by
  rfl

theorem create_entry_marks_transaction_wide_set (machine next : Machine) (address : Address)
    (result : Generated.transition (.enterCreate address) machine = some next) :
    next.world.journal.createdThisTx =
      FiniteSet.insert machine.world.journal.createdThisTx address := by
  simp [Generated.transition, Generated.enter, Generated.markCreated,
    WorldJournalExtractor.Generated.WorldJournalKernel.transition,
    WorldJournalExtractor.Generated.WorldJournalKernel.snapshotOf,
    WorldJournalExtractor.Generated.WorldJournalKernel.record] at result
  cases result
  rfl

theorem body_snapshot_operations_fail_closed (machine : Machine) :
    Generated.transition (.applyWorld .takeSnapshot) machine = none ∧
      Generated.transition (.applyWorld .restoreSnapshot) machine = none := by
  exact ⟨rfl, rfl⟩

end FrameJournalExtractor.Refinement.FrameJournal
