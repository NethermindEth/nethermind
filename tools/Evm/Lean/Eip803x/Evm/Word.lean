-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Lean.Elab.Tactic.Omega
import Eip803x.Word256

namespace Eip803x
namespace Evm
namespace Word

def bitWidth : Nat := 256

def byteWidth : Nat := 8

def byteCount : Nat := 32

def modulus : Nat := 2 ^ bitWidth

def signBit : Nat := 2 ^ (bitWidth - 1)

def maxValue : Nat := modulus - 1

theorem modulus_positive : 0 < modulus := by
  simp [modulus, bitWidth]

/-- Reduces a natural number into the canonical unsigned 256-bit range. -/
def normalize (value : Nat) : Nat :=
  value % modulus

def ofNat (value : Nat) : UInt256 :=
  ⟨normalize value, Nat.mod_lt _ modulus_positive⟩

def zero : UInt256 := ofNat 0

def one : UInt256 := ofNat 1

def allOnes : UInt256 := ofNat maxValue

def minSignedWord : UInt256 := ofNat signBit

def minSignedInt : Int := -(signBit : Int)

/-- Interprets the canonical word as a two's-complement signed integer. -/
def toSigned (word : UInt256) : Int :=
  if word.val < signBit then
    word.val
  else
    (word.val : Int) - (modulus : Int)

/-- Canonically reduces a signed integer into a 256-bit two's-complement word. -/
def fromInt (value : Int) : UInt256 :=
  if value < 0 then
    ofNat (modulus - value.natAbs % modulus)
  else
    ofNat value.natAbs

theorem range (word : UInt256) : word.val < modulus := word.isLt

theorem normalize_range (value : Nat) : normalize value < modulus :=
  Nat.mod_lt _ modulus_positive

theorem normalize_canonical (word : UInt256) : normalize word.val = word.val :=
  Nat.mod_eq_of_lt word.isLt

theorem ofNat_value (value : Nat) : (ofNat value).val = normalize value := rfl

theorem toSigned_of_lt {word : UInt256} (h : word.val < signBit) :
    toSigned word = (word.val : Int) := by
  simp [toSigned, h]

theorem toSigned_of_ge {word : UInt256} (h : signBit ≤ word.val) :
    toSigned word = (word.val : Int) - (modulus : Int) := by
  simp [toSigned, Nat.not_lt_of_ge h]

theorem modulus_eq_twice_signBit : modulus = signBit + signBit := by native_decide

theorem toSigned_bounds (word : UInt256) :
    minSignedInt ≤ toSigned word ∧ toSigned word < (signBit : Int) := by
  by_cases h : word.val < signBit
  · constructor
    · rw [toSigned_of_lt h]
      unfold minSignedInt
      omega
    · rw [toSigned_of_lt h]
      exact_mod_cast h
  · have hSigned : signBit ≤ word.val := Nat.le_of_not_gt h
    rw [toSigned_of_ge hSigned]
    have hModulus : (modulus : Int) = (signBit : Int) + (signBit : Int) := by
      exact_mod_cast modulus_eq_twice_signBit
    have hValue : (word.val : Int) < (modulus : Int) := by
      exact_mod_cast word.isLt
    have hSignedInt : (signBit : Int) ≤ (word.val : Int) := by
      exact_mod_cast hSigned
    constructor
    · unfold minSignedInt
      omega
    · omega

theorem zero_value : zero.val = 0 := by native_decide

theorem one_value : one.val = 1 := by native_decide

@[simp] theorem toSigned_zero : toSigned zero = 0 := by native_decide

theorem minSigned_value : minSignedWord.val = signBit := by native_decide

theorem minSigned_signed : toSigned minSignedWord = minSignedInt := by native_decide

theorem allOnes_signed : toSigned allOnes = -1 := by native_decide

def add (left right : UInt256) : UInt256 :=
  ofNat (left.val + right.val)

def mul (left right : UInt256) : UInt256 :=
  ofNat (left.val * right.val)

def sub (left right : UInt256) : UInt256 :=
  ofNat (modulus + left.val - right.val)

def udiv (dividend divisor : UInt256) : UInt256 :=
  if divisor.val = 0 then zero else ofNat (dividend.val / divisor.val)

def sdiv (dividend divisor : UInt256) : UInt256 :=
  let signedDividend := toSigned dividend
  let signedDivisor := toSigned divisor
  if signedDivisor = 0 then
    zero
  else if signedDividend = minSignedInt ∧ signedDivisor = -1 then
    minSignedWord
  else
    fromInt (Int.tdiv signedDividend signedDivisor)

def umod (dividend divisor : UInt256) : UInt256 :=
  if divisor.val = 0 then zero else ofNat (dividend.val % divisor.val)

def smod (dividend divisor : UInt256) : UInt256 :=
  let signedDividend := toSigned dividend
  let signedDivisor := toSigned divisor
  if signedDivisor = 0 then zero else fromInt (Int.tmod signedDividend signedDivisor)

def addmod (left right modulusWord : UInt256) : UInt256 :=
  if modulusWord.val = 0 then
    zero
  else
    ofNat ((left.val + right.val) % modulusWord.val)

def mulmod (left right modulusWord : UInt256) : UInt256 :=
  if modulusWord.val = 0 then
    zero
  else
    ofNat ((left.val * right.val) % modulusWord.val)

def powMod (base exponent : Nat) : Nat :=
  go 1 (base % modulus) exponent
where
  go (acc factor exponent : Nat) : Nat :=
    if h : exponent = 0 then
      acc
    else
      let nextAcc := if exponent % 2 = 0 then acc else (acc * factor) % modulus
      go nextAcc ((factor * factor) % modulus) (exponent / 2)
  termination_by exponent
  decreasing_by
    exact Nat.div_lt_self (Nat.pos_of_ne_zero h) (by decide)

def exp (base exponent : UInt256) : UInt256 :=
  ofNat (powMod base.val exponent.val)

def signextend (byteIndex value : UInt256) : UInt256 :=
  if byteIndex.val < byteCount then
    let width := byteWidth * (byteIndex.val + 1)
    let lowValue := value.val % 2 ^ width
    if (lowValue / 2 ^ (width - 1)) % 2 = 0 then
      ofNat lowValue
    else
      ofNat (lowValue + (modulus - 2 ^ width))
  else
    value

def unsignedLt (left right : UInt256) : UInt256 :=
  if left.val < right.val then one else zero

def unsignedGt (left right : UInt256) : UInt256 :=
  if left.val > right.val then one else zero

def signedLt (left right : UInt256) : UInt256 :=
  if toSigned left < toSigned right then one else zero

def signedGt (left right : UInt256) : UInt256 :=
  if toSigned left > toSigned right then one else zero

def equal (left right : UInt256) : UInt256 :=
  if left.val = right.val then one else zero

def isZero (value : UInt256) : UInt256 :=
  if value.val = 0 then one else zero

def bitwiseAnd (left right : UInt256) : UInt256 :=
  ofNat (Nat.land left.val right.val)

def bitwiseOr (left right : UInt256) : UInt256 :=
  ofNat (Nat.lor left.val right.val)

def bitwiseXor (left right : UInt256) : UInt256 :=
  ofNat (Nat.xor left.val right.val)

def bitwiseNot (value : UInt256) : UInt256 :=
  ofNat (maxValue - value.val)

def byte (byteIndex value : UInt256) : UInt256 :=
  if byteIndex.val < byteCount then
    ofNat ((value.val / 2 ^ (byteWidth * (byteCount - 1 - byteIndex.val))) % 256)
  else
    zero

def shl (shift value : UInt256) : UInt256 :=
  if shift.val < bitWidth then
    ofNat (value.val * 2 ^ shift.val)
  else
    zero

def shr (shift value : UInt256) : UInt256 :=
  if shift.val < bitWidth then
    ofNat (value.val / 2 ^ shift.val)
  else
    zero

def sar (shift value : UInt256) : UInt256 :=
  if shift.val < bitWidth then
    fromInt (Int.ediv (toSigned value) (2 ^ shift.val))
  else if toSigned value < 0 then
    allOnes
  else
    zero

def clz (value : UInt256) : UInt256 :=
  if value.val = 0 then
    ofNat bitWidth
  else
    ofNat (bitWidth - 1 - Nat.log2 value.val)

theorem udiv_by_zero (dividend : UInt256) : udiv dividend zero = zero := by
  simp [udiv, zero, ofNat, normalize]

theorem umod_by_zero (dividend : UInt256) : umod dividend zero = zero := by
  simp [umod, zero, ofNat, normalize]

theorem sdiv_by_zero (dividend : UInt256) : sdiv dividend zero = zero := by
  simp [sdiv]

theorem smod_by_zero (dividend : UInt256) : smod dividend zero = zero := by
  simp [smod]

theorem addmod_by_zero (left right : UInt256) : addmod left right zero = zero := by
  simp [addmod, zero, ofNat, normalize]

theorem mulmod_by_zero (left right : UInt256) : mulmod left right zero = zero := by
  simp [mulmod, zero, ofNat, normalize]

theorem signextend_large (byteIndex value : UInt256) (h : byteCount ≤ byteIndex.val) :
    signextend byteIndex value = value := by
  simp [signextend, Nat.not_lt_of_ge h]

theorem byte_large (byteIndex value : UInt256) (h : byteCount ≤ byteIndex.val) :
    byte byteIndex value = zero := by
  simp [byte, Nat.not_lt_of_ge h]

theorem shl_large (shift value : UInt256) (h : bitWidth ≤ shift.val) :
    shl shift value = zero := by
  simp [shl, Nat.not_lt_of_ge h]

theorem shr_large (shift value : UInt256) (h : bitWidth ≤ shift.val) :
    shr shift value = zero := by
  simp [shr, Nat.not_lt_of_ge h]

theorem sar_large_nonnegative (shift value : UInt256) (hShift : bitWidth ≤ shift.val)
    (hNonnegative : 0 ≤ toSigned value) : sar shift value = zero := by
  simp [sar, Nat.not_lt_of_ge hShift, Int.not_lt_of_ge hNonnegative]

theorem sar_large_negative (shift value : UInt256) (hShift : bitWidth ≤ shift.val)
    (hNegative : toSigned value < 0) : sar shift value = allOnes := by
  simp [sar, Nat.not_lt_of_ge hShift, hNegative]

theorem sdiv_minSigned_negative_one :
    sdiv minSignedWord (fromInt (-1)) = minSignedWord := by native_decide

theorem smod_negative_sign :
    smod (fromInt (-7)) (ofNat 3) = fromInt (-1) := by native_decide

theorem exp_modulo_edge :
    exp (ofNat (2 ^ 255)) (ofNat 2) = zero := by native_decide

end Word
end Evm
end Eip803x
