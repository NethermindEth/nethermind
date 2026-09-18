-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.Evm.Word

namespace Eip803x
namespace Evm
namespace TransientStorage

open Word

/-- A transient-storage cell is scoped by executing account and 256-bit key. -/
structure Cell where
  address : UInt256
  key : UInt256
  deriving DecidableEq, Repr

/--
An extensional reference store. The first binding for a cell is authoritative;
`write` replaces that binding and preserves every other cell.
-/
abbrev Store := List (Cell × UInt256)

def read : Store → Cell → UInt256
  | [], _ => Word.zero
  | (candidate, value) :: tail, cell =>
      if candidate = cell then value else read tail cell

def write : Store → Cell → UInt256 → Store
  | [], cell, value => [(cell, value)]
  | (candidate, current) :: tail, cell, value =>
      if candidate = cell then
        (cell, value) :: tail
      else
        (candidate, current) :: write tail cell value

/-- Removes every transient cell owned by an account, as account destruction requires. -/
def clearAddress : Store → UInt256 → Store
  | [], _ => []
  | (cell, value) :: tail, address =>
      if cell.address = address then
        clearAddress tail address
      else
        (cell, value) :: clearAddress tail address

/-- A call-frame checkpoint and the transaction-wide transient store it sees. -/
structure Frame where
  checkpoint : Store
  current : Store
  deriving Repr

def enterFrame (store : Store) : Frame :=
  { checkpoint := store, current := store }

def store (frame : Frame) (address key value : UInt256) : Frame :=
  { frame with current := write frame.current ⟨address, key⟩ value }

def clearAccount (frame : Frame) (address : UInt256) : Frame :=
  { frame with current := clearAddress frame.current address }

def load (frame : Frame) (address key : UInt256) : UInt256 :=
  read frame.current ⟨address, key⟩

/-- A successful child exposes all of its transient writes to its parent. -/
def mergeSuccess (parent child : Frame) : Frame :=
  { parent with current := child.current }

/-- REVERT and exceptional halt both restore the child's entry checkpoint. -/
def restoreFailure (frame : Frame) : Store :=
  frame.checkpoint

/-- Transient storage is empty at the boundary of every transaction. -/
def endTransaction (_ : Store) : Store :=
  []

inductive StoreStatus where
  | ok
  | staticCallViolation
  deriving DecidableEq, Repr

structure StoreOutcome where
  status : StoreStatus
  frame : Frame
  deriving Repr

/-- The state-selection part of TSTORE, before gas and stack composition. -/
def executeTStore (isStatic : Bool) (frame : Frame)
    (address key value : UInt256) : StoreOutcome :=
  if isStatic then
    { status := .staticCallViolation, frame }
  else
    { status := .ok, frame := store frame address key value }

theorem read_empty (cell : Cell) : read [] cell = Word.zero := by
  rfl

theorem read_write_same (before : Store) (cell : Cell) (value : UInt256) :
    read (write before cell value) cell = value := by
  induction before with
  | nil => simp [write, read]
  | cons head tail ih =>
      rcases head with ⟨candidate, current⟩
      by_cases h : candidate = cell
      · simp [write, read, h]
      · simp [write, read, h, ih]

theorem read_write_other (before : Store) (written queried : Cell) (value : UInt256)
    (hDifferent : written ≠ queried) :
    read (write before written value) queried = read before queried := by
  induction before with
  | nil => simp [write, read, hDifferent]
  | cons head tail ih =>
      rcases head with ⟨candidate, current⟩
      by_cases hWritten : candidate = written
      · subst candidate
        simp [write, read, hDifferent]
      · by_cases hQueried : candidate = queried
        · subst candidate
          simp [write, read, hWritten]
        · simp [write, read, hWritten, hQueried, ih]

theorem read_clear_same_address (before : Store) (address key : UInt256) :
    read (clearAddress before address) ⟨address, key⟩ = Word.zero := by
  induction before with
  | nil => rfl
  | cons head tail ih =>
      rcases head with ⟨cell, value⟩
      by_cases hAddress : cell.address = address
      · simp [clearAddress, hAddress, ih]
      · have hCell : cell ≠ ⟨address, key⟩ := by
          intro hEqual
          exact hAddress (congrArg Cell.address hEqual)
        simp [clearAddress, read, hAddress, hCell, ih]

theorem read_clear_other_address (before : Store) (cleared reader key : UInt256)
    (hAddress : cleared ≠ reader) :
    read (clearAddress before cleared) ⟨reader, key⟩ = read before ⟨reader, key⟩ := by
  induction before with
  | nil => rfl
  | cons head tail ih =>
      rcases head with ⟨cell, value⟩
      by_cases hCleared : cell.address = cleared
      · have hCell : cell ≠ ⟨reader, key⟩ := by
          intro hEqual
          apply hAddress
          exact hCleared.symm.trans (congrArg Cell.address hEqual)
        simp [clearAddress, read, hCleared, hCell, ih]
      · simp [clearAddress, read, hCleared, ih]

theorem load_after_store (frame : Frame) (address key value : UInt256) :
    load (store frame address key value) address key = value := by
  simp [load, store, read_write_same]

theorem separate_addresses (frame : Frame) (writer reader key value : UInt256)
    (hAddress : writer ≠ reader) :
    load (store frame writer key value) reader key = load frame reader key := by
  apply read_write_other
  intro hCell
  exact hAddress (congrArg Cell.address hCell)

theorem separate_keys (frame : Frame) (address written queried value : UInt256)
    (hKey : written ≠ queried) :
    load (store frame address written value) address queried = load frame address queried := by
  apply read_write_other
  intro hCell
  exact hKey (congrArg Cell.key hCell)

theorem reentrant_frame_reads_parent_write (parent : Frame) (address key value : UInt256) :
    load (enterFrame (store parent address key value).current) address key = value := by
  simp [load, enterFrame, store, read_write_same]

theorem cleared_account_reads_zero (frame : Frame) (address key : UInt256) :
    load (clearAccount frame address) address key = Word.zero := by
  exact read_clear_same_address frame.current address key

theorem clearing_account_preserves_other_accounts (frame : Frame)
    (cleared reader key : UInt256) (hAddress : cleared ≠ reader) :
    load (clearAccount frame cleared) reader key = load frame reader key := by
  exact read_clear_other_address frame.current cleared reader key hAddress

theorem successful_child_write_is_visible (parent : Frame) (address key value : UInt256) :
    let child := store (enterFrame parent.current) address key value
    load (mergeSuccess parent child) address key = value := by
  simp [load, mergeSuccess, enterFrame, store, read_write_same]

theorem failed_child_restores_parent_store (parent : Frame) (address key value : UInt256) :
    let child := store (enterFrame parent.current) address key value
    restoreFailure child = parent.current := by
  rfl

theorem failed_outer_frame_undoes_successful_inner_frame
    (initial : Store) (address key outerValue innerValue : UInt256) :
    let outer := store (enterFrame initial) address key outerValue
    let inner := store (enterFrame outer.current) address key innerValue
    let outerAfterInner := mergeSuccess outer inner
    restoreFailure outerAfterInner = initial := by
  rfl

theorem static_tstore_preserves_frame (frame : Frame) (address key value : UInt256) :
    executeTStore true frame address key value =
      { status := .staticCallViolation, frame } := by
  rfl

theorem nonstatic_tstore_writes (frame : Frame) (address key value : UInt256) :
    executeTStore false frame address key value =
      { status := .ok, frame := store frame address key value } := by
  rfl

theorem transaction_boundary_clears_every_cell (store : Store) (cell : Cell) :
    read (endTransaction store) cell = Word.zero := by
  rfl

end TransientStorage
end Evm
end Eip803x
