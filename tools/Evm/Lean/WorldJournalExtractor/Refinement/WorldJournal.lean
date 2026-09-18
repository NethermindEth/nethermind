-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import WorldJournalExtractor.Generated.WorldJournalKernel
import WorldJournalExtractor.Specification.WorldJournal

namespace WorldJournalExtractor.Refinement.WorldJournal

open WorldJournalExtractor

namespace Generated
export WorldJournalExtractor.Generated.WorldJournalKernel
  (admittedOperationNames record establishOriginal findAccountHead readAccountState
    createdAccountValue replaceAccount removeAccount replacePersistent replaceTransient
    insertWarmAccount insertWarmCell pushLog insertDestroy snapshotOf rewindJournal keepCacheEntry
    rewindAccountJournal rewindState transition executeTrace)
end Generated

namespace Spec
export WorldJournalExtractor.Specification
  (observe captureOriginal readAccount createdAccount writeAccount eraseAccount writePersistent
    writeTransient warmAccount warmCell appendLog addDestroy step run nonNested
    standardTraceWellFormed)
end Spec

theorem admitted_operation_names_exact : Generated.admittedOperationNames =
    [ "readAccount", "createAccount", "updateAccount", "deleteAccount"
    , "readPersistent", "writePersistent", "readTransient", "writeTransient"
    , "warmAccount", "warmCell", "appendLog", "addDestroy"
    , "takeSnapshot", "restoreSnapshot" ] := by
  rfl

def ResultExtEq : Option Machine → Option Machine → Prop
  | none, none => True
  | some left, some right =>
      left.journal.project.ExtEq right.journal.project ∧
      left.observations = right.observations
  | _, _ => False

theorem projection_extensional_reflexive (projection : WorldProjection) :
    projection.ExtEq projection := by
  simp [WorldProjection.ExtEq, FiniteMap.ExtEq, FiniteSet.ExtEq]

theorem journal_restore_own_position (journal : Journal α) :
    journal.restore journal.position = journal := by
  cases journal with
  | mk initial changes => simp [Journal.restore, Journal.position]

theorem journal_restore_push_position (journal : Journal α) (value : α) :
    (journal.push value).restore journal.position = journal := by
  cases journal with
  | mk initial changes => simp [Journal.push, Journal.restore, Journal.position]

theorem journal_restore_arbitrary_suffix (journal : Journal α) (newest : List α) :
    ({ journal with changes := newest ++ journal.changes } : Journal α).restore
        journal.position = journal := by
  cases journal with
  | mk initial changes => simp [Journal.restore, Journal.position]

theorem journal_nested_position_restore (journal : Journal α) (outer inner : α) :
    (((journal.push outer).push inner).restore (journal.push outer).position).restore
        journal.position = journal := by
  rw [journal_restore_push_position, journal_restore_push_position]

theorem journal_nested_arbitrary_position_restore (journal : Journal α)
    (outerChanges innerChanges : List α) :
    let outer : Journal α := { journal with changes := outerChanges ++ journal.changes }
    let inner : Journal α := { outer with changes := innerChanges ++ outer.changes }
    (inner.restore outer.position).restore journal.position = journal := by
  cases journal
  simp [Journal.restore, Journal.position]

theorem account_journal_restore_own_position (journal : AccountJournal) :
    journal.restore journal.position = journal := by
  cases journal
  simp [AccountJournal.restore, AccountJournal.position]

theorem account_journal_restore_semantic_push_position (journal : AccountJournal)
    (address : Address) (value : Option AccountView) :
    (journal.pushSemantic address value).restore journal.position = journal := by
  cases journal with
  | mk initial changes =>
    have notRetained : AccountJournal.retainedCacheEntry
        { kind := .semantic, address, value,
          hadPrevious := (AccountJournal.findHead changes address).isSome } = false := by
      rfl
    simp [AccountJournal.pushSemantic, AccountJournal.restore, AccountJournal.position,
      notRetained]

theorem account_journal_nested_semantic_position_restore (journal : AccountJournal)
    (outerAddress innerAddress : Address) (outerValue innerValue : Option AccountView) :
    (((journal.pushSemantic outerAddress outerValue).pushSemantic innerAddress innerValue).restore
      (journal.pushSemantic outerAddress outerValue).position).restore journal.position = journal := by
  rw [account_journal_restore_semantic_push_position,
    account_journal_restore_semantic_push_position]

theorem account_journal_restore_retains_new_backing_cache (journal : AccountJournal)
    (address : Address) (account : AccountView)
    (fresh : AccountJournal.findHead journal.changes address = none) :
    (journal.pushJustCache address account).restore journal.position =
      journal.pushJustCache address account := by
  cases journal with
  | mk initial changes =>
    have retained : AccountJournal.retainedCacheEntry
        { kind := .justCache, address, value := some account, hadPrevious := false } = true := by
      rfl
    simp [AccountJournal.pushJustCache, AccountJournal.restore, AccountJournal.position,
      fresh, retained]

theorem account_journal_push_cache_projection_stutter (journal : AccountJournal)
    (address : Address) (account : AccountView) :
    (journal.pushJustCache address account).current = journal.current := by
  cases journal
  simp [AccountJournal.pushJustCache, AccountJournal.current, AccountJournal.applyChange,
    List.foldl_append]

theorem world_restore_own_capture (state : JournalState) :
    state.restore state.capture = state := by
  cases state
  simp [JournalState.restore, JournalState.capture, journal_restore_own_position,
    account_journal_restore_own_position]

theorem restore_preserves_persistent_originals (state : JournalState) (snapshot : FrameSnapshot) :
    (state.restore snapshot).persistentOriginals = state.persistentOriginals := by
  rfl

theorem restore_preserves_created_this_transaction (state : JournalState)
    (snapshot : FrameSnapshot) :
    (state.restore snapshot).createdThisTx = state.createdThisTx := by
  rfl

theorem create_account_preserves_created_this_transaction (machine : Machine)
    (address : Address) (balance : Word) (nonce : Nat) :
    Option.map (fun result => result.journal.createdThisTx)
        (Spec.step (.createAccount address balance nonce) machine) =
      some machine.journal.createdThisTx := by
  rfl

theorem restore_separates_persistent_and_transient (state : JournalState)
    (snapshot : FrameSnapshot) :
    (state.restore snapshot).persistent = state.persistent.restore snapshot.persistent ∧
    (state.restore snapshot).transient = state.transient.restore snapshot.transient := by
  exact ⟨rfl, rfl⟩

theorem missing_account_differs_from_physical_empty (address : Address) :
    FiniteMap.lookup ([] : AccountMap) address ≠
      FiniteMap.lookup ([(address, emptyAccount)] : AccountMap) address := by
  simp [FiniteMap.lookup]

theorem establish_original_refines (state : JournalState) (cell : Cell) :
    Generated.establishOriginal state cell = Spec.captureOriginal state cell := by
  unfold Generated.establishOriginal Spec.captureOriginal
  cases original : FiniteMap.lookup state.persistentOriginals cell <;> simp

theorem find_account_head_refines (changes : List AccountChange) (address : Address) :
    Generated.findAccountHead changes address = AccountJournal.findHead changes address := by
  induction changes with
  | nil => rfl
  | cons change tail inductionHypothesis =>
      simp [Generated.findAccountHead, AccountJournal.findHead, inductionHypothesis]

theorem read_account_refines (state : JournalState) (address : Address) :
    Generated.readAccountState state address = Spec.readAccount state address := by
  unfold Generated.readAccountState Spec.readAccount
  rw [find_account_head_refines]
  cases head : AccountJournal.findHead state.accounts.changes address with
  | some => rfl
  | none =>
      cases backing : FiniteMap.lookup state.accounts.initial address with
      | none => rfl
      | some account => simp [AccountJournal.pushJustCache, head]

theorem created_account_refines (balance : Word) (nonce : Nat) :
    Generated.createdAccountValue balance nonce = Spec.createdAccount balance nonce := by
  rfl

theorem replace_account_refines (state : JournalState) (address : Address)
    (account : AccountView) :
    Generated.replaceAccount state address account = Spec.writeAccount state address account := by
  unfold Generated.replaceAccount Spec.writeAccount AccountJournal.pushSemantic
  rw [find_account_head_refines]

theorem remove_account_refines (state : JournalState) (address : Address) :
    Generated.removeAccount state address = Spec.eraseAccount state address := by
  unfold Generated.removeAccount Spec.eraseAccount AccountJournal.pushSemantic
  rw [find_account_head_refines]

theorem replace_persistent_refines (state : JournalState) (cell : Cell) (value : Word) :
    Generated.replacePersistent state cell value = Spec.writePersistent state cell value := by
  rfl

theorem persistent_write_projects_ancillary_effects_to_stutter (state : JournalState)
    (cell : Cell) (value : Word) :
    let written := Spec.writePersistent state cell value
    written.accounts = state.accounts ∧
    written.persistentOriginals = state.persistentOriginals ∧
    written.transient = state.transient ∧
    written.warmAccounts = state.warmAccounts ∧
    written.warmCells = state.warmCells ∧
    written.logs = state.logs ∧
    written.destroySet = state.destroySet ∧
    written.createdThisTx = state.createdThisTx := by
  simp [Spec.writePersistent]

theorem replace_transient_refines (state : JournalState) (cell : Cell) (value : Word) :
    Generated.replaceTransient state cell value = Spec.writeTransient state cell value := by
  rfl

theorem insert_warm_account_refines (state : JournalState) (address : Address) :
    Generated.insertWarmAccount state address = Spec.warmAccount state address := by
  rfl

theorem insert_warm_cell_refines (state : JournalState) (cell : Cell) :
    Generated.insertWarmCell state cell = Spec.warmCell state cell := by
  rfl

theorem push_log_refines (state : JournalState) (entry : LogEntry) :
    Generated.pushLog state entry = Spec.appendLog state entry := by
  rfl

theorem insert_destroy_refines (state : JournalState) (address : Address) :
    Generated.insertDestroy state address = Spec.addDestroy state address := by
  rfl

theorem snapshot_of_refines (state : JournalState) :
    Generated.snapshotOf state = state.capture := by
  cases state
  rfl

theorem rewind_journal_refines (journal : Journal α) (position : Nat) :
    Generated.rewindJournal journal position = journal.restore position := by
  rfl

theorem rewind_account_journal_refines (journal : AccountJournal) (position : Nat) :
    Generated.rewindAccountJournal journal position = journal.restore position := by
  rfl

theorem rewind_state_refines (state : JournalState) (snapshot : FrameSnapshot) :
    Generated.rewindState state snapshot = state.restore snapshot := by
  cases state
  cases snapshot
  rfl

theorem transition_refines (operation : Operation) (machine : Machine) :
    Generated.transition operation machine = Spec.step operation machine := by
  cases operation <;>
    simp [Generated.transition, Spec.step, Generated.record, Spec.observe,
      establish_original_refines, read_account_refines,
      created_account_refines, replace_account_refines, remove_account_refines,
      replace_persistent_refines, replace_transient_refines, insert_warm_account_refines,
      insert_warm_cell_refines, push_log_refines, insert_destroy_refines,
      snapshot_of_refines, rewind_state_refines]
  case restoreSnapshot => cases machine.snapshots <;> rfl

theorem finite_trace_refines (operations : List Operation) (machine : Machine) :
    Generated.executeTrace operations machine = Spec.run operations machine := by
  induction operations generalizing machine with
  | nil => rfl
  | cons operation tail inductionHypothesis =>
      simp only [Generated.executeTrace, Spec.run, transition_refines]
      cases result : Spec.step operation machine with
      | none => rfl
      | some next => exact inductionHypothesis next

theorem finite_trace_projection_equality (operations : List Operation) (machine : Machine) :
    Option.map (fun result => result.journal.project)
        (Generated.executeTrace operations machine) =
      Option.map (fun result => result.journal.project) (Spec.run operations machine) := by
  rw [finite_trace_refines]

theorem finite_trace_observation_equality (operations : List Operation) (machine : Machine) :
    Option.map (fun result => result.observations)
        (Generated.executeTrace operations machine) =
      Option.map (fun result => result.observations) (Spec.run operations machine) := by
  rw [finite_trace_refines]

theorem finite_trace_extensional_refinement (operations : List Operation) (machine : Machine) :
    ResultExtEq (Generated.executeTrace operations machine) (Spec.run operations machine) := by
  rw [finite_trace_refines]
  cases Spec.run operations machine with
  | none => trivial
  | some result => exact ⟨projection_extensional_reflexive result.journal.project, rfl⟩

theorem nonNested_trace_equality (operations : List Operation) (machine : Machine)
    (_ : Spec.nonNested operations = true) :
    Generated.executeTrace operations machine = Spec.run operations machine :=
  finite_trace_refines operations machine

theorem standard_mainnet_trace_equality (operations : List Operation) (machine : Machine)
    (_ : Spec.standardTraceWellFormed operations machine = true) :
    Generated.executeTrace operations machine = Spec.run operations machine :=
  finite_trace_refines operations machine

theorem read_account_state_projection_stutter (state : JournalState) (address : Address) :
    (Spec.readAccount state address).1.project = state.project := by
  unfold Spec.readAccount
  cases head : AccountJournal.findHead state.accounts.changes address with
  | some => rfl
  | none =>
      cases backing : FiniteMap.lookup state.accounts.initial address with
      | none => rfl
      | some account =>
          simp [JournalState.project, account_journal_push_cache_projection_stutter]

theorem account_read_is_extensional_stutter (machine : Machine) (address : Address) :
    Option.map (fun result => result.journal.project)
        (Spec.step (.readAccount address) machine) = some machine.journal.project := by
  generalize resultEquation : Spec.readAccount machine.journal address = result
  rcases result with ⟨journal, value⟩
  have projection := read_account_state_projection_stutter machine.journal address
  rw [resultEquation] at projection
  simp [Spec.step, resultEquation, Spec.observe, projection]

theorem backing_account_read_adds_raw_entry (machine : Machine) (address : Address)
    (account : AccountView)
    (head : AccountJournal.findHead machine.journal.accounts.changes address = none)
    (backing : FiniteMap.lookup machine.journal.accounts.initial address = some account) :
    Option.map (fun result => result.journal.accounts.position)
        (Spec.step (.readAccount address) machine) =
      some (machine.journal.accounts.position + 1) := by
  simp [Spec.step, Spec.readAccount, head, backing, Spec.observe, AccountJournal.pushJustCache,
    AccountJournal.position]

theorem backing_read_restore_next_snapshot_uses_reappended_raw_position
    (machine : Machine) (address : Address) (account : AccountView)
    (head : AccountJournal.findHead machine.journal.accounts.changes address = none)
    (backing : FiniteMap.lookup machine.journal.accounts.initial address = some account) :
    Option.map (fun result => result.snapshots.head?.map FrameSnapshot.accounts)
        (Spec.run [.takeSnapshot, .readAccount address, .restoreSnapshot, .takeSnapshot] machine) =
      some (some (machine.journal.accounts.position + 1)) := by
  have restored := account_journal_restore_retains_new_backing_cache
    machine.journal.accounts address account head
  simp only [Spec.run, Spec.step, Spec.readAccount, head, backing, Spec.observe,
    JournalState.capture, JournalState.restore, Option.map_some]
  rw [restored]
  simp [AccountJournal.position, AccountJournal.pushJustCache, head]

theorem duplicate_warm_account_does_not_grow_journal (machine : Machine) (address : Address)
    (warm : FiniteSet.contains machine.journal.warmAccounts.current address = true) :
    Option.map (fun result => result.journal.warmAccounts.position)
        (Spec.step (.warmAccount address) machine) =
      some machine.journal.warmAccounts.position := by
  simp [Spec.step, Spec.warmAccount, Spec.observe, warm]

theorem duplicate_destroy_does_not_grow_journal (machine : Machine) (address : Address)
    (present : FiniteSet.contains machine.journal.destroySet.current address = true) :
    Option.map (fun result => result.journal.destroySet.position)
        (Spec.step (.addDestroy address) machine) =
      some machine.journal.destroySet.position := by
  simp [Spec.step, Spec.addDestroy, present]

theorem append_log_preserves_order (machine : Machine) (entry : LogEntry) :
    Option.map (fun result => result.journal.logs.current)
        (Spec.step (.appendLog entry) machine) =
      some (machine.journal.logs.current ++ [entry]) := by
  rfl

theorem take_then_restore_projection (machine : Machine) :
    Option.map (fun result => result.journal.project)
        (Spec.run [.takeSnapshot, .restoreSnapshot] machine) =
      some machine.journal.project := by
  simp [Spec.run, Spec.step, Spec.observe, world_restore_own_capture]

theorem take_then_restore_frame_projection (machine : Machine) :
    Option.map (fun result => result.journal.frameProjection)
        (Spec.run [.takeSnapshot, .restoreSnapshot] machine) =
      some machine.journal.frameProjection := by
  simp [Spec.run, Spec.step, Spec.observe, world_restore_own_capture]

theorem take_then_restore_snapshot_stack (machine : Machine) :
    Option.map (fun result => result.snapshots)
        (Spec.run [.takeSnapshot, .restoreSnapshot] machine) =
      some machine.snapshots := by
  simp [Spec.run, Spec.step, Spec.observe]

theorem restore_without_snapshot_fails (machine : Machine) (empty : machine.snapshots = []) :
    Spec.step .restoreSnapshot machine = none := by
  simp [Spec.step, empty]

end WorldJournalExtractor.Refinement.WorldJournal
