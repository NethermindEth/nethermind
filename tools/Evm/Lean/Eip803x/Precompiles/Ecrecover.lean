-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.Evm.MemoryStackControl
import Lean.Elab.Tactic.Omega

namespace Eip803x
namespace Precompiles
namespace Ecrecover

open Evm.MemoryStackControl

/-!
  A handwritten executable reference for the ECRECOVER precompile leaf.

  The byte decoding, signature-domain checks, result shape, and metadata are
  modeled here.  Secp256k1 public-key recovery and Keccak-256 are deliberately
  an explicit oracle: this leaf neither implements nor proves cryptography.
  The oracle returns a length-indexed address only after its combined recovery
  and hash computation succeeds.
-/

def address : Nat := 1

def name : String := "ECREC"

def supportsCaching : Bool := true

def inputLength : Nat := 128

def wordLength : Nat := 32

def baseGasCost : Nat := 3000

def dataGasCost : Nat := 0

def totalGasCost : Nat := baseGasCost + dataGasCost

def normalizedInput (input : List Byte) : List Byte :=
  input.take inputLength ++ List.replicate (inputLength - input.length) zeroByte

def wordAt (input : List Byte) (offset : Nat) : List Byte :=
  (normalizedInput input).drop offset |>.take wordLength

def byteValue (value : Byte) : Nat := value.val

def wordValue (word : List Byte) : Nat :=
  word.foldl (fun accumulator value => accumulator * 256 + byteValue value) 0

def allZero (bytes : List Byte) : Bool :=
  bytes.all (fun value => value == zeroByte)

def terminalByte (word : List Byte) : Byte :=
  readByte word 31

def validV (word : List Byte) : Bool :=
  allZero (word.take 31) &&
    (terminalByte word == byte 27 || terminalByte word == byte 28)

def secp256k1Order : Nat :=
  115792089237316195423570985008687907852837564279074904382605163141518161494337

def validScalar (word : List Byte) : Bool :=
  0 < wordValue word && wordValue word < secp256k1Order

structure DecodedInput where
  message : List Byte
  v : List Byte
  r : List Byte
  s : List Byte
  deriving DecidableEq, Repr

def decode (input : List Byte) : DecodedInput :=
  { message := wordAt input 0
    v := wordAt input 32
    r := wordAt input 64
    s := wordAt input 96 }

def recoveryId (decoded : DecodedInput) : Nat :=
  byteValue (terminalByte decoded.v) - 27

structure Address where
  bytes : List Byte
  length_eq : bytes.length = 20
  deriving DecidableEq, Repr

def addressOfBytes (bytes : List Byte) (length_eq : bytes.length = 20) : Address :=
  { bytes, length_eq }

/-!
  This is the only cryptographic boundary in the reference.  It represents
  scalar/public-key validity beyond the range checks, secp256k1 recovery, and
  Keccak-256 followed by address truncation.  `none` represents an invalid or
  unrecoverable signature; it is not an exceptional EVM halt.
-/
structure Secp256k1KeccakOracle where
  recoverAddress : List Byte -> Nat -> List Byte -> List Byte -> Option Address

def zeroPrefix : List Byte := List.replicate 12 zeroByte

def paddedAddress (recovered : Address) : List Byte :=
  zeroPrefix ++ recovered.bytes

structure Result where
  success : Bool
  output : List Byte
  deriving DecidableEq, Repr

def emptyResult : Result := { success := true, output := [] }

def addressResult (recovered : Address) : Result :=
  { success := true, output := paddedAddress recovered }

def runDecoded (oracle : Secp256k1KeccakOracle) (decoded : DecodedInput) : Result :=
  if validV decoded.v then
    if validScalar decoded.r then
      if validScalar decoded.s then
        match oracle.recoverAddress decoded.message (recoveryId decoded) decoded.r decoded.s with
        | none => emptyResult
        | some recovered => addressResult recovered
      else emptyResult
    else emptyResult
  else emptyResult

def run (oracle : Secp256k1KeccakOracle) (input : List Byte) : Result :=
  runDecoded oracle (decode input)

theorem normalized_input_length (input : List Byte) :
    (normalizedInput input).length = inputLength := by
  unfold normalizedInput inputLength
  simp [List.length_take]
  omega

theorem word_at_length (input : List Byte) (offset : Nat) (h : offset + wordLength ≤ inputLength) :
    (wordAt input offset).length = wordLength := by
  unfold wordAt
  rw [List.length_take, List.length_drop, normalized_input_length]
  rw [Nat.min_eq_left]
  simp only [wordLength, inputLength] at h ⊢
  omega

theorem decoded_word_lengths (input : List Byte) :
    (decode input).message.length = wordLength ∧
      (decode input).v.length = wordLength ∧
      (decode input).r.length = wordLength ∧
      (decode input).s.length = wordLength := by
  exact ⟨word_at_length input 0 (by native_decide), word_at_length input 32 (by native_decide),
    word_at_length input 64 (by native_decide), word_at_length input 96 (by native_decide)⟩

theorem padded_address_length (recovered : Address) :
    (paddedAddress recovered).length = 32 := by
  simp [paddedAddress, zeroPrefix, recovered.length_eq]

theorem padded_address_prefix (recovered : Address) :
    (paddedAddress recovered).take 12 = zeroPrefix := by
  simp [paddedAddress, zeroPrefix]

theorem padded_address_suffix (recovered : Address) :
    (paddedAddress recovered).drop 12 = recovered.bytes := by
  simp [paddedAddress, zeroPrefix]

theorem run_decoded_classification (oracle : Secp256k1KeccakOracle) (decoded : DecodedInput) :
    runDecoded oracle decoded = emptyResult ∨
      ∃ recovered, runDecoded oracle decoded = addressResult recovered := by
  unfold runDecoded
  split
  · split
    · split
      · cases hRecovery : oracle.recoverAddress decoded.message (recoveryId decoded) decoded.r decoded.s with
        | none => exact Or.inl rfl
        | some recovered => exact Or.inr ⟨recovered, rfl⟩
      · exact Or.inl rfl
    · exact Or.inl rfl
  · exact Or.inl rfl

theorem run_succeeds (oracle : Secp256k1KeccakOracle) (input : List Byte) :
    (run oracle input).success = true := by
  cases run_decoded_classification oracle (decode input) with
  | inl h => simp [run, h, emptyResult]
  | inr h =>
    obtain ⟨recovered, h⟩ := h
    simp [run, h, addressResult]

theorem invalid_v_returns_empty (oracle : Secp256k1KeccakOracle) (input : List Byte)
    (h : validV (decode input).v = false) :
    run oracle input = emptyResult := by
  simp [run, runDecoded, h]

theorem invalid_r_returns_empty (oracle : Secp256k1KeccakOracle) (input : List Byte)
    (hV : validV (decode input).v = true)
    (hR : validScalar (decode input).r = false) :
    run oracle input = emptyResult := by
  simp [run, runDecoded, hV, hR]

theorem invalid_s_returns_empty (oracle : Secp256k1KeccakOracle) (input : List Byte)
    (hV : validV (decode input).v = true)
    (hR : validScalar (decode input).r = true)
    (hS : validScalar (decode input).s = false) :
    run oracle input = emptyResult := by
  simp [run, runDecoded, hV, hR, hS]

theorem unrecoverable_signature_returns_empty (oracle : Secp256k1KeccakOracle) (input : List Byte)
    (hV : validV (decode input).v = true)
    (hR : validScalar (decode input).r = true)
    (hS : validScalar (decode input).s = true)
    (hOracle : oracle.recoverAddress (decode input).message (recoveryId (decode input))
      (decode input).r (decode input).s = none) :
    run oracle input = emptyResult := by
  simp [run, runDecoded, hV, hR, hS, hOracle]

theorem oracle_success_returns_exact_address_result
    (oracle : Secp256k1KeccakOracle) (input : List Byte) (recovered : Address)
    (hV : validV (decode input).v = true)
    (hR : validScalar (decode input).r = true)
    (hS : validScalar (decode input).s = true)
    (hOracle : oracle.recoverAddress (decode input).message (recoveryId (decode input))
      (decode input).r (decode input).s = some recovered) :
    run oracle input = addressResult recovered ∧
      (run oracle input).output = paddedAddress recovered ∧
      (run oracle input).output.drop 12 = recovered.bytes := by
  have hRun : run oracle input = addressResult recovered := by
    simp [run, runDecoded, hV, hR, hS, hOracle]
  refine ⟨hRun, ?_, ?_⟩
  · rw [hRun]
    rfl
  · rw [hRun]
    exact padded_address_suffix recovered

theorem output_is_empty_or_padded_address (oracle : Secp256k1KeccakOracle) (input : List Byte) :
    (run oracle input).output = [] ∨ (run oracle input).output.length = 32 := by
  cases run_decoded_classification oracle (decode input) with
  | inl h => left; simp [run, h, emptyResult]
  | inr h =>
    right
    obtain ⟨recovered, h⟩ := h
    simp [run, h, addressResult, padded_address_length]

theorem nonempty_output_has_zero_prefix (oracle : Secp256k1KeccakOracle) (input : List Byte)
    (h : (run oracle input).output ≠ []) :
    (run oracle input).output.take 12 = zeroPrefix := by
  cases run_decoded_classification oracle (decode input) with
  | inl hEmpty =>
    exfalso
    apply h
    simp [run, hEmpty, emptyResult]
  | inr hAddress =>
    obtain ⟨recovered, hRecovered⟩ := hAddress
    simp [run, hRecovered, addressResult, padded_address_prefix]

theorem normalization_ignores_suffix (head tail : List Byte) (h : inputLength ≤ head.length) :
    normalizedInput (head ++ tail) = normalizedInput head := by
  unfold normalizedInput
  have hAppend : inputLength ≤ (head ++ tail).length := by
    simp only [List.length_append]
    omega
  rw [List.take_append_of_le_length h]
  rw [Nat.sub_eq_zero_of_le hAppend, Nat.sub_eq_zero_of_le h]

theorem run_ignores_suffix (oracle : Secp256k1KeccakOracle) (head tail : List Byte)
    (h : inputLength ≤ head.length) :
    run oracle (head ++ tail) = run oracle head := by
  unfold run runDecoded decode wordAt
  rw [normalization_ignores_suffix head tail h]

theorem metadata_contract :
    address = 1 ∧ name = "ECREC" ∧ supportsCaching = true ∧
      inputLength = 128 ∧ wordLength = 32 ∧ baseGasCost = 3000 ∧ dataGasCost = 0 := by
  native_decide

end Ecrecover
end Precompiles
end Eip803x
