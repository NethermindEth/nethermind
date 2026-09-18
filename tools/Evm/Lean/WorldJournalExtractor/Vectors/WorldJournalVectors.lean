-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import WorldJournalExtractor.Refinement.WorldJournal

namespace WorldJournalExtractor.Vectors.WorldJournalVectors

open WorldJournalExtractor

namespace Generated
export WorldJournalExtractor.Generated.WorldJournalKernel (admittedOperationNames transition executeTrace)
end Generated

namespace Spec
export WorldJournalExtractor.Specification
  (captureOriginal readAccount createdAccount writeAccount eraseAccount writePersistent
    writeTransient warmAccount appendLog step run nonNested standardTraceWellFormed)
end Spec

def accountA : AccountView := { nonce := 1, balance := 20, storageRoot := 30, codeHash := 40 }
def accountB : AccountView := { nonce := 2, balance := 21, storageRoot := 31, codeHash := 41 }
def createdAccountA : AccountView := Spec.createdAccount 20 1
def storageOnlyAccount : AccountView := { emptyAccount with storageRoot := 99 }
def cellA : Cell := ⟨1, 11⟩
def cellB : Cell := ⟨2, 12⟩
def logA : LogEntry := ⟨1, [3, 4], [5, 6]⟩
def logB : LogEntry := ⟨2, [7], [8, 9]⟩

def seededJournal : JournalState :=
  { accounts := ⟨[(3, emptyAccount)], []⟩
    persistent := ⟨[(cellA, 5)], []⟩
    persistentOriginals := []
    transient := ⟨[], []⟩
    warmAccounts := ⟨[], []⟩
    warmCells := ⟨[], []⟩
    logs := ⟨[], []⟩
    destroySet := ⟨[], []⟩
    createdThisTx := [77] }

def seededMachine : Machine :=
  { journal := seededJournal, snapshots := [], observations := [] }

def storageOnlyMachine : Machine :=
  { journal := { seededJournal with accounts := ⟨[(4, storageOnlyAccount)], []⟩ }
    snapshots := []
    observations := [] }

def completeTrace : List Operation :=
  [ .readAccount 99
  , .readAccount 3
  , .createAccount 1 20 1
  , .updateAccount 1 accountB
  , .readPersistent cellA
  , .writePersistent cellA 7
  , .readTransient cellA
  , .writeTransient cellA 9
  , .warmAccount 1
  , .warmAccount 1
  , .warmCell cellA
  , .warmCell cellA
  , .appendLog logA
  , .addDestroy 1
  , .addDestroy 1
  , .deleteAccount 1 ]

def nestedTrace : List Operation :=
  [ .takeSnapshot
  , .createAccount 1 20 1
  , .readPersistent cellA
  , .writePersistent cellA 7
  , .writeTransient cellA 9
  , .warmAccount 1
  , .warmCell cellA
  , .appendLog logA
  , .addDestroy 1
  , .takeSnapshot
  , .updateAccount 1 accountB
  , .writePersistent cellA 8
  , .writeTransient cellA 10
  , .warmAccount 2
  , .warmCell cellB
  , .appendLog logB
  , .addDestroy 2
  , .restoreSnapshot
  , .readAccount 1
  , .readPersistent cellA
  , .readTransient cellA
  , .restoreSnapshot ]

def nestedResult : Option Machine := Spec.run nestedTrace seededMachine
def generatedNestedResult : Option Machine := Generated.executeTrace nestedTrace seededMachine
def innerRestored : Option Machine := Spec.run (nestedTrace.take 18) seededMachine
def warmedTwice : Option Machine :=
  (Spec.step (.warmAccount 1) seededMachine).bind (Spec.step (.warmAccount 1))
def destroyedTwice : Option Machine :=
  (Spec.step (.addDestroy 1) seededMachine).bind (Spec.step (.addDestroy 1))
def justCacheSnapshotTrace : List Operation :=
  [.takeSnapshot, .readAccount 3, .restoreSnapshot, .takeSnapshot]
def justCacheSnapshotResult : Option Machine := Spec.run justCacheSnapshotTrace seededMachine

example : Generated.admittedOperationNames.length = 14 := by native_decide
example : completeTrace.length = 16 := by native_decide
example : nestedTrace.length = 22 := by native_decide
example : Spec.standardTraceWellFormed completeTrace seededMachine = true := by native_decide
example : Spec.standardTraceWellFormed nestedTrace seededMachine = true := by native_decide
example : Spec.nonNested completeTrace = true := by native_decide
example : seededMachine.journal.project.WellFormed := by
  simp [WorldProjection.WellFormed, FiniteMap.Canonical, FiniteSet.Canonical, seededMachine,
    seededJournal, JournalState.project, Journal.current, AccountJournal.current]
example : generatedNestedResult = nestedResult := by native_decide
example : nestedResult.isSome = true := by native_decide

example : nestedResult.map (fun machine => machine.snapshots) = some [] := by native_decide
example : nestedResult.map (fun machine => machine.journal.accounts.current) =
    some [(3, emptyAccount)] := by native_decide
example : nestedResult.map (fun machine => machine.journal.persistent.current) =
    some [(cellA, 5)] := by native_decide
example : nestedResult.map (fun machine => machine.journal.transient.current) = some [] := by native_decide
example : nestedResult.map (fun machine => machine.journal.warmAccounts.current) = some [] := by native_decide
example : nestedResult.map (fun machine => machine.journal.warmCells.current) = some [] := by native_decide
example : nestedResult.map (fun machine => machine.journal.logs.current) = some [] := by native_decide
example : nestedResult.map (fun machine => machine.journal.destroySet.current) = some [] := by native_decide
example : nestedResult.map (fun machine => machine.journal.createdThisTx) = some [77] := by native_decide
example : nestedResult.map (fun machine => machine.journal.persistentOriginals) =
    some [(cellA, 5)] := by native_decide
example : nestedResult.map (fun machine => machine.observations.length) = some 12 := by native_decide

example : innerRestored.map (fun machine => machine.snapshots.length) = some 1 := by native_decide
example : innerRestored.map (fun machine => machine.journal.accounts.current) =
    some [(3, emptyAccount), (1, createdAccountA)] := by native_decide
example : innerRestored.map (fun machine => machine.journal.persistent.current) =
    some [(cellA, 7)] := by native_decide
example : innerRestored.map (fun machine => machine.journal.persistentOriginals) =
    some [(cellA, 5)] := by native_decide
example : innerRestored.map (fun machine => machine.journal.transient.current) =
    some [(cellA, 9)] := by native_decide
example : innerRestored.map (fun machine => machine.journal.warmAccounts.current) = some [1] := by native_decide
example : innerRestored.map (fun machine => machine.journal.warmCells.current) = some [cellA] := by native_decide
example : innerRestored.map (fun machine => machine.journal.logs.current) = some [logA] := by native_decide
example : innerRestored.map (fun machine => machine.journal.destroySet.current) = some [1] := by native_decide
example : innerRestored.map (fun machine => machine.journal.createdThisTx) = some [77] := by native_decide

example : (Spec.step (.readAccount 99) seededMachine).map
    (fun machine => machine.journal.accounts.current) =
      some seededMachine.journal.accounts.current := by native_decide
example : (Spec.step (.readAccount 99) seededMachine).map
    (fun machine => machine.observations) = some [.account 99 none] := by native_decide
example : (Spec.step (.readAccount 3) seededMachine).map
    (fun machine => machine.observations) = some [.account 3 (some emptyAccount)] := by native_decide
example : (Spec.step (.readAccount 3) seededMachine).map
    (fun machine => machine.journal.accounts.position) = some 1 := by native_decide
example : (Spec.step (.readAccount 99) seededMachine).map
    (fun machine => machine.journal.accounts.position) = some 0 := by native_decide
example : justCacheSnapshotResult.map (fun machine => machine.journal.accounts.position) =
    some 1 := by native_decide
example : (justCacheSnapshotResult.bind (fun machine => machine.snapshots.head?)).map
    FrameSnapshot.accounts = some 1 := by native_decide
example : justCacheSnapshotResult.map (fun machine => machine.journal.accounts.current) =
    some seededMachine.journal.accounts.current := by native_decide
example : (Spec.step (.createAccount 1 20 1) seededMachine).map
    (fun machine => machine.journal.createdThisTx) = some [77] := by native_decide
example : (Spec.step (.createAccount 1 20 1) seededMachine).map
    (fun machine => FiniteMap.lookup machine.journal.accounts.current 1) =
      some (some createdAccountA) := by native_decide
example : createdAccountA.storageRoot = emptyStorageRoot := by native_decide
example : createdAccountA.codeHash = emptyCodeHash := by native_decide
example : storageOnlyAccount ≠ emptyAccount := by native_decide
example : (some storageOnlyAccount : Option AccountView) ≠ none := by native_decide
example : (Spec.step (.readAccount 4) storageOnlyMachine).map
    (fun machine => machine.observations) =
      some [.account 4 (some storageOnlyAccount)] := by native_decide
example : (Spec.step (.readAccount 4) storageOnlyMachine).map
    (fun machine => machine.journal.accounts.position) = some 1 := by native_decide
example : (Spec.step (.deleteAccount 3) seededMachine).map
    (fun machine => FiniteMap.lookup machine.journal.accounts.current 3) = some none := by native_decide
example : (Spec.step (.deleteAccount 99) seededMachine).map
    (fun machine => machine.journal.accounts.position) = some 1 := by native_decide
example : (Spec.step (.deleteAccount 99) seededMachine).map
    (fun machine => machine.journal.accounts.current) = some seededJournal.accounts.current := by native_decide
example : warmedTwice.map (fun machine => machine.journal.warmAccounts.position) =
    some 1 := by native_decide
example : destroyedTwice.map (fun machine => machine.journal.destroySet.position) =
    some 1 := by native_decide
example : (Spec.run [.appendLog logA, .appendLog logB] seededMachine).map
    (fun machine => machine.journal.logs.current) = some [logA, logB] := by native_decide
example : Spec.standardTraceWellFormed [.writePersistent cellA 7] seededMachine = false := by native_decide
example : Spec.step .restoreSnapshot seededMachine = none := by native_decide
example : (Spec.run [.readPersistent cellA, .writePersistent cellA 7, .readPersistent cellA]
    seededMachine).map (fun machine => machine.journal.persistentOriginals) =
      some [(cellA, 5)] := by native_decide

def restoreOriginalsMutant (state : JournalState) (snapshot : FrameSnapshot) : JournalState :=
  { state.restore snapshot with persistentOriginals := [] }

def restoreCreatedMutant (state : JournalState) (snapshot : FrameSnapshot) : JournalState :=
  { state.restore snapshot with createdThisTx := [] }

def prependLogMutant (state : JournalState) (entry : LogEntry) : JournalState :=
  { state with logs := state.logs.push (entry :: state.logs.current) }

def duplicateWarmMutant (state : JournalState) (address : Address) : JournalState :=
  { state with warmAccounts := state.warmAccounts.push (address :: state.warmAccounts.current) }

def physicalEmptyReadMutant (state : JournalState) (address : Address) : JournalState :=
  match FiniteMap.lookup state.accounts.current address with
  | some _ => state
  | none => { state with accounts := state.accounts.pushSemantic address (some emptyAccount) }

def nonemptyStorageCreateMutant (balance : Word) (nonce : Nat) : AccountView :=
  { nonce, balance, storageRoot := 1, codeHash := emptyCodeHash }

def nonemptyCodeCreateMutant (balance : Word) (nonce : Nat) : AccountView :=
  { nonce, balance, storageRoot := emptyStorageRoot, codeHash := 1 }

def deleteToEmptyMutant (state : JournalState) (address : Address) : JournalState :=
  Spec.writeAccount state address emptyAccount

def writeCapturesOriginalMutant (state : JournalState) (cell : Cell) (value : Word) : JournalState :=
  Spec.writePersistent (Spec.captureOriginal state cell) cell value

def variedJournal : JournalState :=
  { accounts := ⟨[], [{ kind := .semantic, address := 10, value := some accountA, hadPrevious := false }]⟩
    persistent := ⟨[], List.replicate 2 []⟩
    persistentOriginals := [(cellA, 5)]
    transient := ⟨[], List.replicate 3 []⟩
    warmAccounts := ⟨[], List.replicate 4 []⟩
    warmCells := ⟨[], List.replicate 5 []⟩
    logs := ⟨[], List.replicate 6 []⟩
    destroySet := ⟨[], List.replicate 7 []⟩
    createdThisTx := [77] }

def variedMutated : JournalState :=
  { accounts := variedJournal.accounts.pushSemantic 1 (some accountA)
    persistent := variedJournal.persistent.push [(cellA, 7)]
    persistentOriginals := variedJournal.persistentOriginals
    transient := variedJournal.transient.push [(cellA, 9)]
    warmAccounts := variedJournal.warmAccounts.push [1]
    warmCells := variedJournal.warmCells.push [cellA]
    logs := variedJournal.logs.push [logA]
    destroySet := variedJournal.destroySet.push [1]
    createdThisTx := variedJournal.createdThisTx }

def onePositionRestoreMutant (state : JournalState) (snapshot : FrameSnapshot) : JournalState :=
  { accounts := state.accounts.restore snapshot.accounts
    persistent := state.persistent.restore snapshot.accounts
    persistentOriginals := state.persistentOriginals
    transient := state.transient.restore snapshot.accounts
    warmAccounts := state.warmAccounts.restore snapshot.accounts
    warmCells := state.warmCells.restore snapshot.accounts
    logs := state.logs.restore snapshot.accounts
    destroySet := state.destroySet.restore snapshot.accounts
    createdThisTx := state.createdThisTx }

def swapStorageRestoreMutant (state : JournalState) (snapshot : FrameSnapshot) : JournalState :=
  { state.restore snapshot with
    persistent := state.persistent.restore snapshot.transient
    transient := state.transient.restore snapshot.persistent }

def dropCacheRestoreMutant (journal : AccountJournal) (position : Nat) : AccountJournal :=
  { journal with changes := journal.changes.drop (journal.changes.length - position) }

def readSeededPersistent : JournalState := Spec.captureOriginal seededJournal cellA

example : restoreOriginalsMutant
    (Spec.writePersistent readSeededPersistent cellA 7) seededJournal.capture ≠
      (Spec.writePersistent readSeededPersistent cellA 7).restore seededJournal.capture := by native_decide
example : restoreCreatedMutant seededJournal seededJournal.capture ≠
    seededJournal.restore seededJournal.capture := by native_decide
example : prependLogMutant (Spec.appendLog seededJournal logA) logB ≠
    Spec.appendLog (Spec.appendLog seededJournal logA) logB := by native_decide
example : duplicateWarmMutant (Spec.warmAccount seededJournal 1).1 1 ≠
    (Spec.warmAccount (Spec.warmAccount seededJournal 1).1 1).1 := by native_decide
example : physicalEmptyReadMutant seededJournal 99 ≠ seededJournal := by native_decide
example : nonemptyStorageCreateMutant 20 1 ≠ Spec.createdAccount 20 1 := by native_decide
example : nonemptyCodeCreateMutant 20 1 ≠ Spec.createdAccount 20 1 := by native_decide
example : deleteToEmptyMutant seededJournal 3 ≠ Spec.eraseAccount seededJournal 3 := by native_decide
example : writeCapturesOriginalMutant seededJournal cellB 7 ≠
    Spec.writePersistent seededJournal cellB 7 := by native_decide
example : variedMutated.restore variedJournal.capture = variedJournal := by native_decide
example : onePositionRestoreMutant variedMutated variedJournal.capture ≠ variedJournal := by native_decide
example : swapStorageRestoreMutant variedMutated variedJournal.capture ≠ variedJournal := by native_decide
example : dropCacheRestoreMutant (seededJournal.accounts.pushJustCache 3 emptyAccount)
    seededJournal.accounts.position ≠
      (seededJournal.accounts.pushJustCache 3 emptyAccount).restore
        seededJournal.accounts.position := by native_decide

example : (Spec.writePersistent seededJournal cellA 7).transient = seededJournal.transient := by rfl
example : (Spec.writeTransient seededJournal cellA 9).persistent = seededJournal.persistent := by rfl
example : (Spec.writeTransient seededJournal cellA 9).persistentOriginals =
    seededJournal.persistentOriginals := by rfl

end WorldJournalExtractor.Vectors.WorldJournalVectors
