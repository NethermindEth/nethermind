-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import WorldJournalExtractor.Specification.WorldProjection

namespace WorldJournalExtractor.Specification

open WorldJournalExtractor

def observe (machine : Machine) (observation : Observation) : Machine :=
  { machine with observations := machine.observations ++ [observation] }

def captureOriginal (state : JournalState) (cell : Cell) : JournalState :=
  match FiniteMap.lookup state.persistentOriginals cell with
  | some _ => state
  | none =>
      let current := FiniteMap.getD state.persistent.current cell 0
      { state with persistentOriginals := FiniteMap.put state.persistentOriginals cell current }

def readAccount (state : JournalState) (address : Address) : JournalState × Option AccountView :=
  match AccountJournal.findHead state.accounts.changes address with
  | some change => (state, change.value)
  | none =>
      match FiniteMap.lookup state.accounts.initial address with
      | none => (state, none)
      | some account =>
          ({ state with accounts := state.accounts.pushJustCache address account }, some account)

def createdAccount (balance : Word) (nonce : Nat) : AccountView :=
  { nonce, balance, storageRoot := emptyStorageRoot, codeHash := emptyCodeHash }

def writeAccount (state : JournalState) (address : Address) (account : AccountView) : JournalState :=
  { state with accounts := state.accounts.pushSemantic address (some account) }

def eraseAccount (state : JournalState) (address : Address) : JournalState :=
  { state with accounts := state.accounts.pushSemantic address none }

def writePersistent (state : JournalState) (cell : Cell) (value : Word) : JournalState :=
  { state with persistent := state.persistent.push (FiniteMap.put state.persistent.current cell value) }

def writeTransient (state : JournalState) (cell : Cell) (value : Word) : JournalState :=
  { state with transient := state.transient.push (FiniteMap.put state.transient.current cell value) }

def warmAccount (state : JournalState) (address : Address) : JournalState × Bool :=
  let alreadyWarm := FiniteSet.contains state.warmAccounts.current address
  if alreadyWarm then
    (state, false)
  else
    ({ state with warmAccounts := state.warmAccounts.push (address :: state.warmAccounts.current) }, true)

def warmCell (state : JournalState) (cell : Cell) : JournalState × Bool :=
  let alreadyWarm := FiniteSet.contains state.warmCells.current cell
  if alreadyWarm then
    (state, false)
  else
    ({ state with warmCells := state.warmCells.push (cell :: state.warmCells.current) }, true)

def appendLog (state : JournalState) (entry : LogEntry) : JournalState :=
  { state with logs := state.logs.push (state.logs.current ++ [entry]) }

def addDestroy (state : JournalState) (address : Address) : JournalState :=
  if FiniteSet.contains state.destroySet.current address then
    state
  else
    { state with destroySet := state.destroySet.push (address :: state.destroySet.current) }

def step (operation : Operation) (machine : Machine) : Option Machine :=
  match operation with
  | .readAccount address =>
      let (journal, value) := readAccount machine.journal address
      some (observe { machine with journal } (.account address value))
  | .createAccount address balance nonce =>
      some { machine with journal := writeAccount machine.journal address (createdAccount balance nonce) }
  | .updateAccount address account =>
      some { machine with journal := writeAccount machine.journal address account }
  | .deleteAccount address =>
      some { machine with journal := eraseAccount machine.journal address }
  | .readPersistent cell =>
      let journal := captureOriginal machine.journal cell
      let current := FiniteMap.getD journal.persistent.current cell 0
      let original := FiniteMap.getD journal.persistentOriginals cell 0
      some (observe { machine with journal } (.persistent cell current original))
  | .writePersistent cell value =>
      some { machine with journal := writePersistent machine.journal cell value }
  | .readTransient cell =>
      let current := FiniteMap.getD machine.journal.transient.current cell 0
      some (observe machine (.transient cell current))
  | .writeTransient cell value =>
      some { machine with journal := writeTransient machine.journal cell value }
  | .warmAccount address =>
      let (journal, inserted) := warmAccount machine.journal address
      some (observe { machine with journal } (.warmedAccount address inserted))
  | .warmCell cell =>
      let (journal, inserted) := warmCell machine.journal cell
      some (observe { machine with journal } (.warmedCell cell inserted))
  | .appendLog entry =>
      some { machine with journal := appendLog machine.journal entry }
  | .addDestroy address =>
      some { machine with journal := addDestroy machine.journal address }
  | .takeSnapshot =>
      let snapshot := machine.journal.capture
      some (observe { machine with snapshots := snapshot :: machine.snapshots } (.snapshotTaken snapshot))
  | .restoreSnapshot =>
      match machine.snapshots with
      | [] => none
      | snapshot :: tail =>
          some (observe
            { machine with journal := machine.journal.restore snapshot, snapshots := tail }
            (.snapshotRestored snapshot))

def run : List Operation → Machine → Option Machine
  | [], machine => some machine
  | operation :: tail, machine =>
      match step operation machine with
      | none => none
      | some next => run tail next

def isSnapshot : Operation → Bool
  | .takeSnapshot => true
  | .restoreSnapshot => true
  | _ => false

def nonNested (operations : List Operation) : Bool :=
  operations.all (fun operation => !isSnapshot operation)

def operationReady (operation : Operation) (machine : Machine) : Bool :=
  match operation with
  | .writePersistent cell _ => (FiniteMap.lookup machine.journal.persistentOriginals cell).isSome
  | .restoreSnapshot => !machine.snapshots.isEmpty
  | _ => true

def standardTraceWellFormed : List Operation → Machine → Bool
  | [], _ => true
  | operation :: tail, machine =>
      if operationReady operation machine then
        match step operation machine with
        | none => false
        | some next => standardTraceWellFormed tail next
      else
        false

end WorldJournalExtractor.Specification
