-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Lean.Elab.Tactic.Omega
import Eip803x.Word256

namespace Eip803x
namespace Evm
namespace StackEncoding

/--
This is a dependency-free source-level representation model, not a production-refinement claim.

It pins the byte/limb relationships used by:

* `src/Nethermind/Nethermind.Evm/EvmStack.cs`:
  `ReadUInt256FromSlot`, `WriteUInt256ToSlot`, and the one/two/three-value `PopUInt256` helpers;
* `src/Nethermind/Nethermind.Evm/EvmStack.std.cs`:
  `ReadBeWord`, `ReadBeWords`, and `WriteBeWord`; and
* `src/Nethermind/Nethermind.Core/Extensions/EvmWordExtensions.cs`:
  `EvmWordExtensions.ByteSwap`.

The model abstracts spans, refs, SIMD selection, the physical backing allocation, stack capacity,
and all opcode semantics. It proves only the stated representation and stack-layout facts.
-/

abbrev Byte := Fin 256

/-- Eight raw bytes in native low-to-high order, modelling a `UInt64` limb. -/
structure Limb64 where
  b0 : Byte
  b1 : Byte
  b2 : Byte
  b3 : Byte
  b4 : Byte
  b5 : Byte
  b6 : Byte
  b7 : Byte
  deriving DecidableEq, Repr

def Limb64.bytes (limb : Limb64) : List Byte :=
  [limb.b0, limb.b1, limb.b2, limb.b3, limb.b4, limb.b5, limb.b6, limb.b7]

theorem Limb64.byte_count (limb : Limb64) : limb.bytes.length = 8 := by rfl

/-- The mathematical unsigned value of a native little-endian `UInt64` limb. -/
def Limb64.value (limb : Limb64) : Nat :=
  limb.b0.val + limb.b1.val * 256 + limb.b2.val * 256 ^ 2 + limb.b3.val * 256 ^ 3 +
    limb.b4.val * 256 ^ 4 + limb.b5.val * 256 ^ 5 + limb.b6.val * 256 ^ 6 + limb.b7.val * 256 ^ 7

theorem Limb64.value_lt (limb : Limb64) : limb.value < 2 ^ 64 := by
  unfold Limb64.value
  omega

/-- The canonical unsigned value of this exact eight-byte `UInt64` representation. -/
def Limb64.canonical (limb : Limb64) : Fin (2 ^ 64) :=
  ⟨limb.value, limb.value_lt⟩

theorem Limb64.canonical_value (limb : Limb64) : limb.canonical.val = limb.value := rfl

/-- The one-limb component of `EvmWordExtensions.ByteSwap`. -/
def Limb64.byteSwap (limb : Limb64) : Limb64 :=
  { b0 := limb.b7
    b1 := limb.b6
    b2 := limb.b5
    b3 := limb.b4
    b4 := limb.b3
    b5 := limb.b2
    b6 := limb.b1
    b7 := limb.b0 }

theorem Limb64.byteSwap_involutive (limb : Limb64) : limb.byteSwap.byteSwap = limb := by
  cases limb
  rfl

/-- The production `UInt256` memory shape: four native little-endian `UInt64` limbs. -/
structure LittleEndianWord where
  u0 : Limb64
  u1 : Limb64
  u2 : Limb64
  u3 : Limb64
  deriving DecidableEq, Repr

/-- The canonical unsigned value of the four production-order limbs. -/
def LittleEndianWord.value (word : LittleEndianWord) : Nat :=
  word.u0.value + word.u1.value * 2 ^ 64 + word.u2.value * 2 ^ 128 + word.u3.value * 2 ^ 192

theorem LittleEndianWord.value_lt (word : LittleEndianWord) : word.value < 2 ^ 256 := by
  unfold LittleEndianWord.value Limb64.value
  omega

/-- The canonical bounded word observed by the other Lean EVM models. -/
def LittleEndianWord.canonical (word : LittleEndianWord) : UInt256 :=
  ⟨word.value, word.value_lt⟩

theorem LittleEndianWord.canonical_value (word : LittleEndianWord) : word.canonical.val = word.value := rfl

/--
Exactly 32 raw memory bytes, in increasing address order and grouped as the four native `UInt64`
loads that the production stack helpers use. A logical EVM value is big-endian in this slot.
-/
structure BigEndianSlot where
  q0 : Limb64
  q1 : Limb64
  q2 : Limb64
  q3 : Limb64
  deriving DecidableEq, Repr

def BigEndianSlot.bytes (slot : BigEndianSlot) : List Byte :=
  slot.q0.bytes ++ slot.q1.bytes ++ slot.q2.bytes ++ slot.q3.bytes

theorem BigEndianSlot.byte_count (slot : BigEndianSlot) : slot.bytes.length = 32 := by
  simp [BigEndianSlot.bytes, Limb64.bytes]

/--
The source-level effect of `EvmWordExtensions.ByteSwap`: reverse all 32 raw bytes, so both the
order of the four limbs and every byte inside each limb are reversed.
-/
def byteSwap256 (word : BigEndianSlot) : BigEndianSlot :=
  { q0 := word.q3.byteSwap
    q1 := word.q2.byteSwap
    q2 := word.q1.byteSwap
    q3 := word.q0.byteSwap }

theorem byteSwap256_involutive (word : BigEndianSlot) : byteSwap256 (byteSwap256 word) = word := by
  cases word
  simp [byteSwap256, Limb64.byteSwap_involutive]

def rawOfLittleEndian (word : LittleEndianWord) : BigEndianSlot :=
  { q0 := word.u0
    q1 := word.u1
    q2 := word.u2
    q3 := word.u3 }

def littleEndianOfRaw (slot : BigEndianSlot) : LittleEndianWord :=
  { u0 := slot.q0
    u1 := slot.q1
    u2 := slot.q2
    u3 := slot.q3 }

/-- Models `EvmStack.WriteUInt256ToSlot`: native limbs followed by a 256-bit byte reversal. -/
def encode (word : LittleEndianWord) : BigEndianSlot :=
  byteSwap256 (rawOfLittleEndian word)

/-- Models `EvmStack.ReadUInt256FromSlot`: a 256-bit byte reversal then native limb interpretation. -/
def decode (slot : BigEndianSlot) : LittleEndianWord :=
  littleEndianOfRaw (byteSwap256 slot)

theorem decode_encode (word : LittleEndianWord) : decode (encode word) = word := by
  cases word
  simp [decode, encode, rawOfLittleEndian, littleEndianOfRaw, byteSwap256,
    Limb64.byteSwap_involutive]

theorem encode_decode (slot : BigEndianSlot) : encode (decode slot) = slot := by
  cases slot
  simp [decode, encode, rawOfLittleEndian, littleEndianOfRaw, byteSwap256,
    Limb64.byteSwap_involutive]

theorem canonical_preserved_by_slot_round_trip (word : LittleEndianWord) :
    (decode (encode word)).canonical = word.canonical := by
  rw [decode_encode]

theorem encode_bytes_are_big_endian (word : LittleEndianWord) :
    (encode word).bytes =
      [ word.u3.b7, word.u3.b6, word.u3.b5, word.u3.b4, word.u3.b3, word.u3.b2, word.u3.b1, word.u3.b0
      , word.u2.b7, word.u2.b6, word.u2.b5, word.u2.b4, word.u2.b3, word.u2.b2, word.u2.b1, word.u2.b0
      , word.u1.b7, word.u1.b6, word.u1.b5, word.u1.b4, word.u1.b3, word.u1.b2, word.u1.b1, word.u1.b0
      , word.u0.b7, word.u0.b6, word.u0.b5, word.u0.b4, word.u0.b3, word.u0.b2, word.u0.b1, word.u0.b0 ] := by
  rfl

/-- A logical EVM stack; `topFirst` is the EVM pop order. -/
structure Stack where
  topFirst : List LittleEndianWord
  deriving DecidableEq, Repr

/-- The production `Head` abstraction: the index of the next free 32-byte slot. -/
def Stack.head (stack : Stack) : Nat := stack.topFirst.length

/-- Production stack memory order: deepest slot first, with the top at the highest active address. -/
def physicalSlots (stack : Stack) : List BigEndianSlot :=
  stack.topFirst.reverse.map encode

structure TwoPopped where
  top : LittleEndianWord
  next : LittleEndianWord
  deriving DecidableEq, Repr

structure ThreePopped where
  top : LittleEndianWord
  next : LittleEndianWord
  third : LittleEndianWord
  deriving DecidableEq, Repr

structure PopTwoOutcome where
  values : Option TwoPopped
  stack : Stack
  deriving DecidableEq, Repr

structure PopThreeOutcome where
  values : Option ThreePopped
  stack : Stack
  deriving DecidableEq, Repr

/-- Models the two-value `PopUInt256` overload, including its no-mutation underflow path. -/
def popTwo (stack : Stack) : PopTwoOutcome :=
  match stack.topFirst with
  | top :: next :: rest =>
    { values := some { top, next }
      stack := { topFirst := rest } }
  | _ => { values := none, stack }

/-- Models the three-value `PopUInt256` overload, including its no-mutation underflow path. -/
def popThree (stack : Stack) : PopThreeOutcome :=
  match stack.topFirst with
  | top :: next :: third :: rest =>
    { values := some { top, next, third }
      stack := { topFirst := rest } }
  | _ => { values := none, stack }

theorem pop_two_layout (top next : LittleEndianWord) (tail : List LittleEndianWord) :
    popTwo { topFirst := top :: next :: tail } =
      { values := some { top, next }
        stack := { topFirst := tail } } := rfl

theorem pop_three_layout (top next third : LittleEndianWord) (tail : List LittleEndianWord) :
    popThree { topFirst := top :: next :: third :: tail } =
      { values := some { top, next, third }
        stack := { topFirst := tail } } := rfl

theorem pop_two_head_delta (top next : LittleEndianWord) (tail : List LittleEndianWord) :
    (popTwo { topFirst := top :: next :: tail }).stack.head + 2 =
      ({ topFirst := top :: next :: tail } : Stack).head := by
  simp [popTwo, Stack.head]

theorem pop_three_head_delta (top next third : LittleEndianWord) (tail : List LittleEndianWord) :
    (popThree { topFirst := top :: next :: third :: tail }).stack.head + 3 =
      ({ topFirst := top :: next :: third :: tail } : Stack).head := by
  simp [popThree, Stack.head]

theorem pop_two_underflow_preserves (stack : Stack) (h : stack.topFirst.length < 2) :
    (popTwo stack).values = none ∧ (popTwo stack).stack = stack := by
  cases stack with
  | mk words =>
    cases words with
    | nil => simp [popTwo]
    | cons top tail =>
      cases tail with
      | nil => simp [popTwo]
      | cons next rest =>
        simp only [List.length_cons] at h
        omega

theorem pop_three_underflow_preserves (stack : Stack) (h : stack.topFirst.length < 3) :
    (popThree stack).values = none ∧ (popThree stack).stack = stack := by
  cases stack with
  | mk words =>
    cases words with
    | nil => simp [popThree]
    | cons top tail =>
      cases tail with
      | nil => simp [popThree]
      | cons next tail =>
        cases tail with
        | nil => simp [popThree]
        | cons third rest =>
          simp only [List.length_cons] at h
          omega

/-- Models the in-place binary opcode path: pop the top and overwrite the former next slot. -/
def binaryOverwrite (operation : LittleEndianWord → LittleEndianWord → LittleEndianWord)
    (stack : Stack) : Option Stack :=
  match stack.topFirst with
  | top :: next :: tail => some { topFirst := operation top next :: tail }
  | _ => none

/-- Models the in-place ternary opcode path: pop the top two and overwrite the former third slot. -/
def ternaryOverwrite (operation : LittleEndianWord → LittleEndianWord → LittleEndianWord → LittleEndianWord)
    (stack : Stack) : Option Stack :=
  match stack.topFirst with
  | top :: next :: third :: tail => some { topFirst := operation top next third :: tail }
  | _ => none

/-- The stack left unchanged when the guarded in-place binary path underflows. -/
def binaryStateAfter (operation : LittleEndianWord → LittleEndianWord → LittleEndianWord)
    (stack : Stack) : Stack :=
  (binaryOverwrite operation stack).getD stack

/-- The stack left unchanged when the guarded in-place ternary path underflows. -/
def ternaryStateAfter (operation : LittleEndianWord → LittleEndianWord → LittleEndianWord → LittleEndianWord)
    (stack : Stack) : Stack :=
  (ternaryOverwrite operation stack).getD stack

theorem binary_operand_physical_layout (top next : LittleEndianWord) (tail : List LittleEndianWord) :
    physicalSlots { topFirst := top :: next :: tail } =
      physicalSlots { topFirst := tail } ++ [encode next, encode top] := by
  simp [physicalSlots]

theorem ternary_operand_physical_layout (top next third : LittleEndianWord)
    (tail : List LittleEndianWord) :
    physicalSlots { topFirst := top :: next :: third :: tail } =
      physicalSlots { topFirst := tail } ++ [encode third, encode next, encode top] := by
  simp [physicalSlots]

theorem binary_overwrite_layout (operation : LittleEndianWord → LittleEndianWord → LittleEndianWord)
    (top next : LittleEndianWord) (tail : List LittleEndianWord) :
    binaryOverwrite operation { topFirst := top :: next :: tail } =
      some { topFirst := operation top next :: tail } := rfl

theorem ternary_overwrite_layout
    (operation : LittleEndianWord → LittleEndianWord → LittleEndianWord → LittleEndianWord)
    (top next third : LittleEndianWord) (tail : List LittleEndianWord) :
    ternaryOverwrite operation { topFirst := top :: next :: third :: tail } =
      some { topFirst := operation top next third :: tail } := rfl

theorem binary_overwrite_head_delta
    (operation : LittleEndianWord → LittleEndianWord → LittleEndianWord)
    (top next : LittleEndianWord) (tail : List LittleEndianWord) :
    ({ topFirst := operation top next :: tail } : Stack).head + 1 =
      ({ topFirst := top :: next :: tail } : Stack).head := by
  simp [Stack.head]

theorem ternary_overwrite_head_delta
    (operation : LittleEndianWord → LittleEndianWord → LittleEndianWord → LittleEndianWord)
    (top next third : LittleEndianWord) (tail : List LittleEndianWord) :
    ({ topFirst := operation top next third :: tail } : Stack).head + 2 =
      ({ topFirst := top :: next :: third :: tail } : Stack).head := by
  simp [Stack.head]

theorem binary_overwrite_target_slot (operation : LittleEndianWord → LittleEndianWord → LittleEndianWord)
    (top next : LittleEndianWord) (tail : List LittleEndianWord) :
    physicalSlots { topFirst := operation top next :: tail } =
      physicalSlots { topFirst := tail } ++ [encode (operation top next)] := by
  simp [physicalSlots]

theorem ternary_overwrite_target_slot
    (operation : LittleEndianWord → LittleEndianWord → LittleEndianWord → LittleEndianWord)
    (top next third : LittleEndianWord) (tail : List LittleEndianWord) :
    physicalSlots { topFirst := operation top next third :: tail } =
      physicalSlots { topFirst := tail } ++ [encode (operation top next third)] := by
  simp [physicalSlots]

theorem binary_overwrite_reuses_former_next_slot
    (operation : LittleEndianWord → LittleEndianWord → LittleEndianWord)
    (top next : LittleEndianWord) (tail : List LittleEndianWord) :
    physicalSlots { topFirst := top :: next :: tail } =
      physicalSlots { topFirst := tail } ++ [encode next, encode top] ∧
    physicalSlots { topFirst := operation top next :: tail } =
      physicalSlots { topFirst := tail } ++ [encode (operation top next)] := by
  exact ⟨binary_operand_physical_layout top next tail, binary_overwrite_target_slot operation top next tail⟩

theorem ternary_overwrite_reuses_former_third_slot
    (operation : LittleEndianWord → LittleEndianWord → LittleEndianWord → LittleEndianWord)
    (top next third : LittleEndianWord) (tail : List LittleEndianWord) :
    physicalSlots { topFirst := top :: next :: third :: tail } =
      physicalSlots { topFirst := tail } ++ [encode third, encode next, encode top] ∧
    physicalSlots { topFirst := operation top next third :: tail } =
      physicalSlots { topFirst := tail } ++ [encode (operation top next third)] := by
  exact ⟨ternary_operand_physical_layout top next third tail,
    ternary_overwrite_target_slot operation top next third tail⟩

theorem binary_overwrite_underflow
    (operation : LittleEndianWord → LittleEndianWord → LittleEndianWord)
    (stack : Stack) (h : stack.topFirst.length < 2) :
    binaryOverwrite operation stack = none ∧ binaryStateAfter operation stack = stack := by
  cases stack with
  | mk words =>
    cases words with
    | nil => simp [binaryOverwrite, binaryStateAfter]
    | cons top tail =>
      cases tail with
      | nil => simp [binaryOverwrite, binaryStateAfter]
      | cons next rest =>
        simp only [List.length_cons] at h
        omega

theorem ternary_overwrite_underflow
    (operation : LittleEndianWord → LittleEndianWord → LittleEndianWord → LittleEndianWord)
    (stack : Stack) (h : stack.topFirst.length < 3) :
    ternaryOverwrite operation stack = none ∧ ternaryStateAfter operation stack = stack := by
  cases stack with
  | mk words =>
    cases words with
    | nil => simp [ternaryOverwrite, ternaryStateAfter]
    | cons top tail =>
      cases tail with
      | nil => simp [ternaryOverwrite, ternaryStateAfter]
      | cons next tail =>
        cases tail with
        | nil => simp [ternaryOverwrite, ternaryStateAfter]
        | cons third rest =>
          simp only [List.length_cons] at h
          omega

namespace BoundaryVector

def byte (value : Nat) : Byte :=
  ⟨value % 256, Nat.mod_lt _ (by decide)⟩

def limb (b0 b1 b2 b3 b4 b5 b6 b7 : Nat) : Limb64 :=
  { b0 := byte b0
    b1 := byte b1
    b2 := byte b2
    b3 := byte b3
    b4 := byte b4
    b5 := byte b5
    b6 := byte b6
    b7 := byte b7 }

/-- Eight distinct bytes per limb and distinct ranges per limb expose both byte and limb reversals. -/
def ascendingWord (start : Nat) : LittleEndianWord :=
  { u0 := limb start (start + 1) (start + 2) (start + 3) (start + 4) (start + 5) (start + 6) (start + 7)
    u1 := limb (start + 8) (start + 9) (start + 10) (start + 11) (start + 12) (start + 13) (start + 14) (start + 15)
    u2 := limb (start + 16) (start + 17) (start + 18) (start + 19) (start + 20) (start + 21) (start + 22) (start + 23)
    u3 := limb (start + 24) (start + 25) (start + 26) (start + 27) (start + 28) (start + 29) (start + 30) (start + 31) }

/-- Independently written expected raw slot for `ascendingWord`; it does not call `encode`. -/
def descendingSlot (start : Nat) : BigEndianSlot :=
  { q0 := limb (start + 31) (start + 30) (start + 29) (start + 28) (start + 27) (start + 26) (start + 25) (start + 24)
    q1 := limb (start + 23) (start + 22) (start + 21) (start + 20) (start + 19) (start + 18) (start + 17) (start + 16)
    q2 := limb (start + 15) (start + 14) (start + 13) (start + 12) (start + 11) (start + 10) (start + 9) (start + 8)
    q3 := limb (start + 7) (start + 6) (start + 5) (start + 4) (start + 3) (start + 2) (start + 1) start }

def a : LittleEndianWord := ascendingWord 0
def b : LittleEndianWord := ascendingWord 32
def c : LittleEndianWord := ascendingWord 64
def d : LittleEndianWord := ascendingWord 96
def result : LittleEndianWord := ascendingWord 128

def slotA : BigEndianSlot := descendingSlot 0
def slotB : BigEndianSlot := descendingSlot 32
def slotC : BigEndianSlot := descendingSlot 64
def slotD : BigEndianSlot := descendingSlot 96
def slotResult : BigEndianSlot := descendingSlot 128

structure Vector where
  name : String
  passes : Bool
  deriving DecidableEq, Repr

def vectors : List Vector :=
  [ { name := "asymmetric-word-encodes-all-32-bytes-big-endian"
      passes := decide (encode a = slotA) }
  , { name := "asymmetric-slot-decodes-to-native-little-endian-limbs"
      passes := decide (decode slotA = a) }
  , { name := "full-word-byte-swap-reverses-both-byte-and-limb-order"
      passes := decide (byteSwap256 slotA = rawOfLittleEndian a) }
  , { name := "triple-stack-memory-is-deepest-first-and-pop-order-is-top-first"
      passes := decide (physicalSlots { topFirst := [a, b, c] } = [slotC, slotB, slotA]) }
  , { name := "two-pop-returns-top-then-next-and-preserves-tail"
      passes := decide (popTwo { topFirst := [a, b, c] } =
        { values := some { top := a, next := b }, stack := { topFirst := [c] } }) }
  , { name := "three-pop-returns-top-next-third-and-preserves-tail"
      passes := decide (popThree { topFirst := [a, b, c, d] } =
        { values := some { top := a, next := b, third := c }, stack := { topFirst := [d] } }) }
  , { name := "binary-overwrite-reuses-the-former-next-slot"
      passes := decide (binaryOverwrite (fun _ _ => result) { topFirst := [a, b, c] } =
        some { topFirst := [result, c] }) }
  , { name := "binary-overwrite-keeps-tail-deepest-and-result-at-former-next-address"
      passes := decide (physicalSlots { topFirst := [result, c] } = [slotC, slotResult]) }
  , { name := "ternary-overwrite-reuses-the-former-third-slot"
      passes := decide (ternaryOverwrite (fun _ _ _ => result) { topFirst := [a, b, c, d] } =
        some { topFirst := [result, d] }) }
  , { name := "ternary-overwrite-keeps-tail-deepest-and-result-at-former-third-address"
      passes := decide (physicalSlots { topFirst := [result, d] } = [slotD, slotResult]) }
  , { name := "two-pop-underflow-preserves-singleton-stack"
      passes := decide (popTwo { topFirst := [a] } =
        { values := none, stack := { topFirst := [a] } }) }
  , { name := "three-pop-underflow-preserves-two-word-stack"
      passes := decide (popThree { topFirst := [a, b] } =
        { values := none, stack := { topFirst := [a, b] } }) } ]

theorem all_pass : vectors.all (fun vector => vector.passes) = true := by native_decide

theorem vector_count : vectors.length = 12 := by native_decide

end BoundaryVector
end StackEncoding
end Evm
end Eip803x
