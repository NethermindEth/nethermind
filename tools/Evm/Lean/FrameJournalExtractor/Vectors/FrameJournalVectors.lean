-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import FrameJournalExtractor.Refinement.FrameJournal

namespace FrameJournalExtractor.Vectors.FrameJournalVectors

open FrameJournalExtractor
open WorldJournalExtractor

namespace Generated
export FrameJournalExtractor.Generated.FrameJournalKernel (transition executeTrace)
end Generated

namespace Spec
export FrameJournalExtractor.Specification
  (enter retainAndPop reapplyRipemdTouch restoreAndPop step run)
end Spec

def account0 : AccountView :=
  { nonce := 1, balance := 100, storageRoot := emptyStorageRoot, codeHash := emptyCodeHash }

def account1 : AccountView :=
  { nonce := 2, balance := 75, storageRoot := emptyStorageRoot, codeHash := emptyCodeHash }

def ripemdEmptyWithStorage : AccountView :=
  { nonce := 0, balance := 0, storageRoot := 77, codeHash := emptyCodeHash }

def cell0 : Cell := ⟨1, 9⟩

def log0 : LogEntry := ⟨1, [2], [3, 4]⟩

def baseJournal : JournalState :=
  { accounts := ⟨[(ripemd160Address, ripemdEmptyWithStorage), (1, account0)], []⟩
    persistent := ⟨[(cell0, 5)], []⟩
    persistentOriginals := [(cell0, 5)]
    transient := ⟨[], []⟩
    warmAccounts := ⟨[], []⟩
    warmCells := ⟨[], []⟩
    logs := ⟨[], []⟩
    destroySet := ⟨[], []⟩
    createdThisTx := [90] }

def baseWorld : WorldMachine :=
  { journal := baseJournal, snapshots := [], observations := [] }

def baseMachine : Machine :=
  { world := baseWorld, frames := [], ripemdTouchLatched := false }

def childEffects : List Operation :=
  [ .applyWorld (.updateAccount 1 account1)
  , .applyWorld (.writePersistent cell0 7)
  , .applyWorld (.writeTransient cell0 8)
  , .applyWorld (.warmAccount 2)
  , .applyWorld (.warmCell cell0)
  , .applyWorld (.appendLog log0)
  , .applyWorld (.addDestroy 1) ]

def childSuccess : List Operation := [.enterCall] ++ childEffects ++ [.exitSuccess]
def childRevert : List Operation := [.enterCall] ++ childEffects ++ [.exitRevert]
def childException : List Operation := [.enterCall] ++ childEffects ++ [.exitException]

def nestedLifo : List Operation :=
  [ .applyWorld (.updateAccount 1 account1)
  , .enterCall
  , .applyWorld (.writePersistent cell0 7)
  , .enterCreate 44
  , .applyWorld (.writePersistent cell0 9)
  , .applyWorld (.writeTransient cell0 8)
  , .applyWorld (.appendLog log0)
  , .applyWorld (.addDestroy 1)
  , .exitRevert
  , .exitSuccess ]

def outerRollback : List Operation :=
  [ .enterCall
  , .applyWorld (.writePersistent cell0 7)
  , .enterCreate 44
  , .applyWorld (.writeTransient cell0 8)
  , .exitSuccess
  , .exitException ]

def ripemdLatchedInChild : List Operation :=
  [ .enterCall
  , .applyWorld (.updateAccount ripemd160Address ripemdEmptyWithStorage)
  , .latchRipemdTouch
  , .applyWorld (.writePersistent cell0 7)
  , .exitRevert ]

def ripemdLatchedEarlier : List Operation :=
  [ .applyWorld (.updateAccount ripemd160Address ripemdEmptyWithStorage)
  , .latchRipemdTouch
  , .enterCall
  , .applyWorld (.writeTransient cell0 8)
  , .exitException ]

example : baseMachine.WellFormed := by native_decide

example : Spec.run childSuccess baseMachine = Generated.executeTrace childSuccess baseMachine := by
  rw [FrameJournalExtractor.Refinement.FrameJournal.finite_trace_refines]

example : (Spec.run childSuccess baseMachine).map
    (fun machine => machine.world.journal.project.persistent) = some [(cell0, 7)] := by native_decide

example : (Spec.run childSuccess baseMachine).map
    (fun machine => machine.world.journal.project.logs) = some [log0] := by native_decide

example : (Spec.run childSuccess baseMachine).map
    (fun machine => machine.world.journal.project.destroySet) = some [1] := by native_decide

example : (Spec.run childRevert baseMachine).map
    (fun machine => machine.world.journal.project) = some baseJournal.project := by native_decide

example : (Spec.run childException baseMachine).map
    (fun machine => machine.world.journal.project) = some baseJournal.project := by native_decide

example : (Spec.run childRevert baseMachine).map
    (fun machine => machine.world.journal.persistentOriginals) = some baseJournal.persistentOriginals := by native_decide

example : (Spec.run childException baseMachine).map
    (fun machine => machine.world.journal.createdThisTx) = some baseJournal.createdThisTx := by native_decide

example : (Spec.run [.enterCreate 44, .exitRevert] baseMachine).map
    (fun machine => machine.world.journal.createdThisTx) = some [44, 90] := by native_decide

example : (Spec.run [.preFrameCallFailure] baseMachine) = some baseMachine := by native_decide

example : (Spec.run [.preFrameCreateFailure] baseMachine) = some baseMachine := by native_decide

example : (Spec.run nestedLifo baseMachine).map
    (fun machine => machine.world.journal.project.persistent) = some [(cell0, 7)] := by native_decide

example : (Spec.run nestedLifo baseMachine).map
    (fun machine => machine.world.journal.project.transient) = some [] := by native_decide

example : (Spec.run nestedLifo baseMachine).map
    (fun machine => machine.world.journal.project.logs) = some [] := by native_decide

example : (Spec.run nestedLifo baseMachine).map
    (fun machine => machine.world.journal.project.destroySet) = some [] := by native_decide

example : (Spec.run nestedLifo baseMachine).map
    (fun machine => machine.world.journal.createdThisTx) = some [44, 90] := by native_decide

example : (Spec.run nestedLifo baseMachine).map (fun machine => machine.frames) = some [] := by native_decide

example : (Spec.run nestedLifo baseMachine).map
    (fun machine => machine.world.snapshots) = some [] := by native_decide

example (next : Machine) (result : Generated.executeTrace nestedLifo baseMachine = some next) :
    next.WellFormed :=
  FrameJournalExtractor.Refinement.FrameJournal.finite_trace_preserves_wellformed
    nestedLifo baseMachine next (by native_decide) result

example : (Spec.run outerRollback baseMachine).map
    (fun machine => machine.world.journal.frameProjection) =
      some baseJournal.frameProjection := by native_decide

example : (Spec.run outerRollback baseMachine).map
    (fun machine => machine.world.journal.createdThisTx) = some [44, 90] := by native_decide

example : (Spec.run ripemdLatchedInChild baseMachine).map
    (fun machine => machine.ripemdTouchLatched) = some true := by native_decide

example : (Spec.run ripemdLatchedInChild baseMachine).map
    (fun machine => machine.world.journal.frameProjection) =
      some baseJournal.frameProjection := by native_decide

example : (Spec.run ripemdLatchedInChild baseMachine).map
    (fun machine => machine.world.journal.accounts.position) = some 2 := by native_decide

example : (Spec.run ripemdLatchedEarlier baseMachine).map
    (fun machine => machine.ripemdTouchLatched) = some true := by native_decide

example : (Spec.run ripemdLatchedEarlier baseMachine).map
    (fun machine => machine.world.journal.frameProjection) =
      (Spec.run [.applyWorld (.updateAccount ripemd160Address ripemdEmptyWithStorage)]
        baseMachine).map (fun machine => machine.world.journal.frameProjection) := by native_decide

example : isEmptyAccount ripemdEmptyWithStorage = true := by native_decide

example : Spec.step .exitSuccess baseMachine = none := by native_decide

example : Spec.step .exitRevert baseMachine = none := by native_decide

example : Spec.step (.applyWorld .takeSnapshot) baseMachine = none := by native_decide

example : Spec.step (.applyWorld .restoreSnapshot) baseMachine = none := by native_decide

def commitRestoresMutant (machine : Machine) : Option Machine :=
  Spec.restoreAndPop machine

def revertRetainsMutant (machine : Machine) : Option Machine :=
  Spec.retainAndPop machine

def createDropsCreatedMutant (machine : Machine) : Option Machine :=
  Spec.enter .call machine

def fifoRestoreMutant (machine : Machine) : Option Machine :=
  match machine.frames.reverse, machine.world.snapshots.reverse with
  | _ :: frames, _ :: snapshots =>
      some { machine with world := { machine.world with snapshots }, frames }
  | _, _ => none

def dropsRipemdReplayMutant (machine : Machine) : Option Machine :=
  match machine.frames with
  | [] => none
  | _ :: frames =>
      match WorldJournalExtractor.Specification.step .restoreSnapshot machine.world with
      | none => none
      | some world => some { machine with world, frames }

def clearsRipemdLatchMutant (machine : Machine) : Option Machine :=
  some { machine with ripemdTouchLatched := false }

example : (Spec.run ([.enterCall] ++ childEffects) baseMachine).bind commitRestoresMutant ≠
    Spec.run childSuccess baseMachine := by native_decide

example : (Spec.run ([.enterCall] ++ childEffects) baseMachine).bind revertRetainsMutant ≠
    Spec.run childRevert baseMachine := by native_decide

example : createDropsCreatedMutant baseMachine ≠ Spec.step (.enterCreate 44) baseMachine := by native_decide

example : (Spec.run [.enterCall, .enterCreate 44] baseMachine).bind fifoRestoreMutant ≠
    Spec.run [.enterCall, .enterCreate 44, .exitRevert] baseMachine := by native_decide

example : (Spec.run (ripemdLatchedInChild.dropLast) baseMachine).bind
    dropsRipemdReplayMutant ≠ Spec.run ripemdLatchedInChild baseMachine := by native_decide

example : (Spec.step .latchRipemdTouch baseMachine).bind clearsRipemdLatchMutant ≠
    Spec.step .latchRipemdTouch baseMachine := by native_decide

end FrameJournalExtractor.Vectors.FrameJournalVectors
