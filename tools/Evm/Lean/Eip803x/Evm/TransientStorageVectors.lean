-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.Evm.TransientStorage

namespace Eip803x
namespace Evm
namespace TransientStorageVectors

open Word
open TransientStorage

def accountA : UInt256 := Word.ofNat 0xaa
def accountB : UInt256 := Word.ofNat 0xbb
def key1 : UInt256 := Word.ofNat 1
def key2 : UInt256 := Word.ofNat 2
def value7 : UInt256 := Word.ofNat 7
def value8 : UInt256 := Word.ofNat 8
def value9 : UInt256 := Word.ofNat 9

def emptyFrame : Frame := enterFrame []

example : load emptyFrame accountA key1 = Word.zero := by native_decide

example : load (store emptyFrame accountA key1 value8) accountA key1 = value8 := by
  native_decide

example : load (store emptyFrame accountA key1 value8) accountA key2 = Word.zero := by
  native_decide

example : load (store emptyFrame accountA key1 value8) accountB key1 = Word.zero := by
  native_decide

example :
    let parent := store emptyFrame accountA key1 value8
    let child := enterFrame parent.current
    load child accountA key1 = value8 := by
  native_decide

example :
    let seeded := store (store emptyFrame accountA key1 value8) accountB key1 value9
    let cleared := clearAccount seeded accountA
    load cleared accountA key1 = Word.zero ∧ load cleared accountB key1 = value9 := by
  native_decide

example :
    let parent := store emptyFrame accountA key1 value8
    let child := store (enterFrame parent.current) accountA key1 value9
    load (mergeSuccess parent child) accountA key1 = value9 := by
  native_decide

example :
    let parent := store emptyFrame accountA key1 value8
    let child := store (enterFrame parent.current) accountA key1 value9
    read (restoreFailure child) ⟨accountA, key1⟩ = value8 := by
  native_decide

example :
    let outer := store (enterFrame []) accountA key1 value7
    let inner := store (enterFrame outer.current) accountA key1 value9
    let outerAfterInner := mergeSuccess outer inner
    read (restoreFailure outerAfterInner) ⟨accountA, key1⟩ = Word.zero := by
  native_decide

example :
    (executeTStore true emptyFrame accountA key1 value8).status =
      StoreStatus.staticCallViolation := by
  native_decide

example :
    (executeTStore true emptyFrame accountA key1 value8).frame.current = [] := by
  native_decide

example :
    let outcome := executeTStore false emptyFrame accountA key1 value8
    outcome.status = .ok ∧ load outcome.frame accountA key1 = value8 := by
  native_decide

example : read (endTransaction [(⟨accountA, key1⟩, value8)]) ⟨accountA, key1⟩ = Word.zero := by
  native_decide

/-- Mutation sentinel: key-only scoping would leak account A's value to account B. -/
def mutatedReadIgnoringAddress (store : Store) (key : UInt256) : UInt256 :=
  match store.find? (fun entry => entry.1.key = key) with
  | some entry => entry.2
  | none => Word.zero

example :
    mutatedReadIgnoringAddress (store emptyFrame accountA key1 value8).current key1 ≠
      load (store emptyFrame accountA key1 value8) accountB key1 := by
  native_decide

/-- Mutation sentinel: retaining the child's current store on failure exposes its write. -/
example :
    let parent := store emptyFrame accountA key1 value8
    let child := store (enterFrame parent.current) accountA key1 value9
    child.current ≠ restoreFailure child := by
  native_decide

end TransientStorageVectors
end Evm
end Eip803x
