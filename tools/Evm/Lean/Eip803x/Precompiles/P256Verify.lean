-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.Evm.MemoryStackControl
import Lean.Elab.Tactic.Omega

namespace Eip803x
namespace Precompiles
namespace P256Verify

open Evm.MemoryStackControl

/-!
  A handwritten executable reference for the standard-mainnet EIP-7951
  P256VERIFY precompile leaf. Exact framing, scalar and field bounds, public-key
  validation, output shape, pricing, and metadata are modeled here. Curve
  membership and ECDSA verification are an explicit cryptographic oracle.
-/

structure Schedule where
  fixedGas : Nat
  deriving DecidableEq, Repr

namespace Schedule

def amsterdam : Schedule := { fixedGas := 6900 }

end Schedule

def address : Nat := 0x100

def name : String := "P256VERIFY"

def supportsCaching : Bool := true

def inputLength : Nat := 160

def wordLength : Nat := 32

def baseGasCost (schedule : Schedule) : Nat := schedule.fixedGas

def dataGasCost (_ : List Byte) : Nat := 0

def totalGasCost (schedule : Schedule) (input : List Byte) : Nat :=
  baseGasCost schedule + dataGasCost input

def normalizeInput (input : List Byte) : List Byte :=
  if input.length = inputLength then input else []

structure Word where
  bytes : List Byte
  length_eq : bytes.length = wordLength
  deriving DecidableEq, Repr

def wordOfBytes (bytes : List Byte) (length_eq : bytes.length = wordLength) : Word :=
  { bytes, length_eq }

structure DecodedInput where
  messageHash : Word
  r : Word
  s : Word
  publicKeyX : Word
  publicKeyY : Word
  deriving DecidableEq, Repr

def wordAt (input : List Byte) (offset : Nat) (hLength : input.length = inputLength)
    (hEnd : offset + wordLength ≤ inputLength) : Word :=
  let bytes := input.drop offset |>.take wordLength
  have hWord : bytes.length = wordLength := by
    simp only [bytes, List.length_take, List.length_drop]
    simp only [inputLength, wordLength] at hLength hEnd ⊢
    omega
  wordOfBytes bytes hWord

def decodeInput? (input : List Byte) : Option DecodedInput :=
  if hLength : input.length = inputLength then
    some
      { messageHash := wordAt input 0 hLength (by native_decide)
        r := wordAt input 32 hLength (by native_decide)
        s := wordAt input 64 hLength (by native_decide)
        publicKeyX := wordAt input 96 hLength (by native_decide)
        publicKeyY := wordAt input 128 hLength (by native_decide) }
  else
    none

def byteValue (value : Byte) : Nat := value.val

def wordValue (word : Word) : Nat :=
  word.bytes.foldl (fun accumulator value => accumulator * 256 + byteValue value) 0

def fieldModulus : Nat :=
  115792089210356248762697446949407573530086143415290314195533631308867097853951

def subgroupOrder : Nat :=
  115792089210356248762697446949407573529996955224135760342422259061068512044369

def validScalar (word : Word) : Bool :=
  0 < wordValue word && wordValue word < subgroupOrder

def validFieldElement (word : Word) : Bool :=
  wordValue word < fieldModulus

def isInfinity (x y : Word) : Bool :=
  wordValue x = 0 && wordValue y = 0

structure CryptoOracle where
  isOnCurve : Word -> Word -> Bool
  verifySignature : Word -> Word -> Word -> Word -> Word -> Bool

structure Result where
  success : Bool
  output : List Byte
  deriving DecidableEq, Repr

def emptyResult : Result := { success := true, output := [] }

def successOutput : List Byte := List.replicate 31 zeroByte ++ [byte 1]

def verifiedResult : Result := { success := true, output := successOutput }

def runDecoded (oracle : CryptoOracle) (input : DecodedInput) : Result :=
  if !validScalar input.r then
    emptyResult
  else if !validScalar input.s then
    emptyResult
  else if !validFieldElement input.publicKeyX then
    emptyResult
  else if !validFieldElement input.publicKeyY then
    emptyResult
  else if !oracle.isOnCurve input.publicKeyX input.publicKeyY then
    emptyResult
  else if isInfinity input.publicKeyX input.publicKeyY then
    emptyResult
  else if oracle.verifySignature input.messageHash input.r input.s
      input.publicKeyX input.publicKeyY then
    verifiedResult
  else
    emptyResult

def run (oracle : CryptoOracle) (input : List Byte) : Result :=
  match decodeInput? (normalizeInput input) with
  | none => emptyResult
  | some decoded => runDecoded oracle decoded

theorem metadata_contract :
    address = 0x100 ∧ name = "P256VERIFY" ∧ supportsCaching = true := by
  native_decide

theorem amsterdam_gas_cost (input : List Byte) :
    totalGasCost Schedule.amsterdam input = 6900 := by
  rfl

theorem normalized_exact_input (input : List Byte) (hLength : input.length = inputLength) :
    normalizeInput input = input := by
  simp [normalizeInput, hLength]

theorem normalized_invalid_input (input : List Byte) (hLength : input.length ≠ inputLength) :
    normalizeInput input = [] := by
  simp [normalizeInput, hLength]

theorem invalid_length_returns_empty (oracle : CryptoOracle) (input : List Byte)
    (hLength : input.length ≠ inputLength) :
    run oracle input = emptyResult := by
  simp [run, normalized_invalid_input input hLength, decodeInput?, inputLength]

theorem decoded_word_lengths (input : List Byte) (decoded : DecodedInput)
    (_hDecode : decodeInput? input = some decoded) :
    decoded.messageHash.bytes.length = wordLength ∧
      decoded.r.bytes.length = wordLength ∧
      decoded.s.bytes.length = wordLength ∧
      decoded.publicKeyX.bytes.length = wordLength ∧
      decoded.publicKeyY.bytes.length = wordLength := by
  exact ⟨decoded.messageHash.length_eq, decoded.r.length_eq, decoded.s.length_eq,
    decoded.publicKeyX.length_eq, decoded.publicKeyY.length_eq⟩

theorem success_output_length : successOutput.length = 32 := by
  native_decide

theorem invalid_r_returns_empty (oracle : CryptoOracle) (input : DecodedInput)
    (hInvalid : validScalar input.r = false) :
    runDecoded oracle input = emptyResult := by
  simp [runDecoded, hInvalid]

theorem invalid_s_returns_empty (oracle : CryptoOracle) (input : DecodedInput)
    (hR : validScalar input.r = true) (hInvalid : validScalar input.s = false) :
    runDecoded oracle input = emptyResult := by
  simp [runDecoded, hR, hInvalid]

theorem invalid_public_key_x_returns_empty (oracle : CryptoOracle) (input : DecodedInput)
    (hR : validScalar input.r = true) (hS : validScalar input.s = true)
    (hInvalid : validFieldElement input.publicKeyX = false) :
    runDecoded oracle input = emptyResult := by
  simp [runDecoded, hR, hS, hInvalid]

theorem invalid_public_key_y_returns_empty (oracle : CryptoOracle) (input : DecodedInput)
    (hR : validScalar input.r = true) (hS : validScalar input.s = true)
    (hX : validFieldElement input.publicKeyX = true)
    (hInvalid : validFieldElement input.publicKeyY = false) :
    runDecoded oracle input = emptyResult := by
  simp [runDecoded, hR, hS, hX, hInvalid]

theorem off_curve_returns_empty (oracle : CryptoOracle) (input : DecodedInput)
    (hR : validScalar input.r = true) (hS : validScalar input.s = true)
    (hX : validFieldElement input.publicKeyX = true)
    (hY : validFieldElement input.publicKeyY = true)
    (hCurve : oracle.isOnCurve input.publicKeyX input.publicKeyY = false) :
    runDecoded oracle input = emptyResult := by
  simp [runDecoded, hR, hS, hX, hY, hCurve]

theorem infinity_returns_empty (oracle : CryptoOracle) (input : DecodedInput)
    (hR : validScalar input.r = true) (hS : validScalar input.s = true)
    (hX : validFieldElement input.publicKeyX = true)
    (hY : validFieldElement input.publicKeyY = true)
    (hCurve : oracle.isOnCurve input.publicKeyX input.publicKeyY = true)
    (hInfinity : isInfinity input.publicKeyX input.publicKeyY = true) :
    runDecoded oracle input = emptyResult := by
  simp [runDecoded, hR, hS, hX, hY, hCurve, hInfinity]

theorem failed_verification_returns_empty (oracle : CryptoOracle) (input : DecodedInput)
    (hR : validScalar input.r = true) (hS : validScalar input.s = true)
    (hX : validFieldElement input.publicKeyX = true)
    (hY : validFieldElement input.publicKeyY = true)
    (hCurve : oracle.isOnCurve input.publicKeyX input.publicKeyY = true)
    (hFinite : isInfinity input.publicKeyX input.publicKeyY = false)
    (hVerify : oracle.verifySignature input.messageHash input.r input.s
      input.publicKeyX input.publicKeyY = false) :
    runDecoded oracle input = emptyResult := by
  simp [runDecoded, hR, hS, hX, hY, hCurve, hFinite, hVerify]

theorem verified_signature_returns_one (oracle : CryptoOracle) (input : DecodedInput)
    (hR : validScalar input.r = true) (hS : validScalar input.s = true)
    (hX : validFieldElement input.publicKeyX = true)
    (hY : validFieldElement input.publicKeyY = true)
    (hCurve : oracle.isOnCurve input.publicKeyX input.publicKeyY = true)
    (hFinite : isInfinity input.publicKeyX input.publicKeyY = false)
    (hVerify : oracle.verifySignature input.messageHash input.r input.s
      input.publicKeyX input.publicKeyY = true) :
    runDecoded oracle input = verifiedResult := by
  simp [runDecoded, hR, hS, hX, hY, hCurve, hFinite, hVerify]

theorem run_succeeds (oracle : CryptoOracle) (input : List Byte) :
    (run oracle input).success = true := by
  unfold run
  split
  · rfl
  · unfold runDecoded
    repeat' first | split | rfl

theorem output_is_empty_or_one (oracle : CryptoOracle) (input : List Byte) :
    (run oracle input).output = [] ∨ (run oracle input).output = successOutput := by
  unfold run
  split
  · left; rfl
  · unfold runDecoded
    repeat' first | split | (first | exact Or.inl rfl | exact Or.inr rfl)

end P256Verify
end Precompiles
end Eip803x
