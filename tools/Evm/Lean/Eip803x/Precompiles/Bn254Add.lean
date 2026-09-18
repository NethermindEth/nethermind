-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.Evm.MemoryStackControl
import Lean.Elab.Tactic.Omega

namespace Eip803x
namespace Precompiles
namespace Bn254Add

open Evm.MemoryStackControl

/-!
  A handwritten executable reference for the standard-mainnet EIP-196/EIP-1108
  BN254_ADD precompile leaf. Input padding/truncation, two point encodings,
  validation order, fixed pricing, output shape, and metadata are modeled here.
  Field decoding, curve validation, and group addition are an explicit oracle.
-/

structure Schedule where
  fixedGas : Nat
  deriving DecidableEq, Repr

namespace Schedule

def amsterdam : Schedule := { fixedGas := 150 }

end Schedule

def address : Nat := 6

def name : String := "BN254_ADD"

def supportsCaching : Bool := true

def inputLength : Nat := 128

def pointLength : Nat := 64

def baseGasCost (schedule : Schedule) : Nat := schedule.fixedGas

def dataGasCost (_ : List Byte) : Nat := 0

def totalGasCost (schedule : Schedule) (input : List Byte) : Nat :=
  baseGasCost schedule + dataGasCost input

def effectiveInput (input : List Byte) : List Byte :=
  input.take inputLength ++ List.replicate (inputLength - input.length) zeroByte

def cacheNormalizedInput (input : List Byte) : List Byte :=
  (input.take inputLength).reverse.dropWhile (· == zeroByte) |>.reverse

structure EncodedPoint where
  bytes : List Byte
  length_eq : bytes.length = pointLength
  deriving DecidableEq, Repr

def pointOfBytes (bytes : List Byte) (length_eq : bytes.length = pointLength) : EncodedPoint :=
  { bytes, length_eq }

structure InputPair where
  left : EncodedPoint
  right : EncodedPoint
  deriving DecidableEq, Repr

def decodeInput (input : List Byte) : InputPair :=
  let normalized := effectiveInput input
  let leftBytes := normalized.take pointLength
  let rightBytes := normalized.drop pointLength
  have hNormalized : normalized.length = inputLength := by
    simp only [normalized, effectiveInput, List.length_append, List.length_take,
      List.length_replicate]
    omega
  have hLeft : leftBytes.length = pointLength := by
    simp only [leftBytes, List.length_take]
    simp only [inputLength, pointLength] at hNormalized ⊢
    omega
  have hRight : rightBytes.length = pointLength := by
    simp only [rightBytes, List.length_drop]
    simp only [inputLength, pointLength] at hNormalized ⊢
    omega
  { left := pointOfBytes leftBytes hLeft, right := pointOfBytes rightBytes hRight }

structure CurveOracle where
  valid : EncodedPoint -> Bool
  add : EncodedPoint -> EncodedPoint -> EncodedPoint

inductive Error where
  | invalidPoint
  deriving DecidableEq, Repr

abbrev Result := Except Error EncodedPoint

def runPair (oracle : CurveOracle) (input : InputPair) : Result :=
  if !oracle.valid input.left then
    .error .invalidPoint
  else if !oracle.valid input.right then
    .error .invalidPoint
  else
    .ok (oracle.add input.left input.right)

def run (oracle : CurveOracle) (input : List Byte) : Result :=
  runPair oracle (decodeInput input)

theorem metadata_contract :
    address = 6 ∧ name = "BN254_ADD" ∧ supportsCaching = true := by
  native_decide

theorem amsterdam_gas_cost (input : List Byte) :
    totalGasCost Schedule.amsterdam input = 150 := by
  rfl

theorem effective_input_length (input : List Byte) :
    (effectiveInput input).length = inputLength := by
  simp [effectiveInput, inputLength]
  omega

theorem decoded_point_lengths (input : List Byte) :
    (decodeInput input).left.bytes.length = pointLength ∧
      (decodeInput input).right.bytes.length = pointLength := by
  exact ⟨(decodeInput input).left.length_eq, (decodeInput input).right.length_eq⟩

theorem exact_input_is_unchanged (input : List Byte) (hLength : input.length = inputLength) :
    effectiveInput input = input := by
  simp [effectiveInput, ← hLength]

theorem suffix_is_ignored (input suffix : List Byte) (hLength : inputLength ≤ input.length) :
    effectiveInput (input ++ suffix) = effectiveInput input := by
  unfold effectiveInput
  have hAppend : inputLength ≤ (input ++ suffix).length := by simp; omega
  rw [List.take_append_of_le_length hLength]
  rw [Nat.sub_eq_zero_of_le hAppend, Nat.sub_eq_zero_of_le hLength]

theorem short_input_is_right_zero_padded (input : List Byte) (hLength : input.length ≤ inputLength) :
    effectiveInput input = input ++ List.replicate (inputLength - input.length) zeroByte := by
  simp [effectiveInput, List.take_of_length_le hLength]

theorem invalid_left_fails (oracle : CurveOracle) (input : InputPair)
    (hInvalid : oracle.valid input.left = false) :
    runPair oracle input = .error .invalidPoint := by
  simp [runPair, hInvalid]

theorem invalid_right_fails (oracle : CurveOracle) (input : InputPair)
    (hLeft : oracle.valid input.left = true)
    (hInvalid : oracle.valid input.right = false) :
    runPair oracle input = .error .invalidPoint := by
  simp [runPair, hLeft, hInvalid]

theorem valid_points_return_exact_oracle_sum (oracle : CurveOracle) (input : InputPair)
    (hLeft : oracle.valid input.left = true)
    (hRight : oracle.valid input.right = true) :
    runPair oracle input = .ok (oracle.add input.left input.right) := by
  simp [runPair, hLeft, hRight]

theorem successful_output_length (oracle : CurveOracle) (input : List Byte)
    (output : EncodedPoint) (_hRun : run oracle input = .ok output) :
    output.bytes.length = pointLength :=
  output.length_eq

theorem run_classification (oracle : CurveOracle) (input : List Byte) :
    run oracle input = .error .invalidPoint ∨ ∃ output, run oracle input = .ok output := by
  unfold run runPair
  split
  · left; rfl
  · split
    · left; rfl
    · right; exact ⟨_, rfl⟩

end Bn254Add
end Precompiles
end Eip803x
