-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.Evm.MemoryStackControl
import Lean.Elab.Tactic.Omega

namespace Eip803x
namespace Precompiles
namespace Bn254Mul

open Evm.MemoryStackControl

/-!
  A handwritten executable reference for the standard-mainnet EIP-196/EIP-1108
  BN254_MUL precompile leaf. Input padding/truncation, point and scalar framing,
  point validation order, fixed pricing, output shape, and metadata are modeled
  here. Field decoding, scalar reduction, curve validation, multiplication, and
  serialization are an explicit oracle.
-/

structure Schedule where
  fixedGas : Nat
  deriving DecidableEq, Repr

namespace Schedule

def amsterdam : Schedule := { fixedGas := 6000 }

end Schedule

def address : Nat := 7

def name : String := "BN254_MUL"

def supportsCaching : Bool := true

def inputLength : Nat := 96

def pointLength : Nat := 64

def scalarLength : Nat := 32

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

structure EncodedScalar where
  bytes : List Byte
  length_eq : bytes.length = scalarLength
  deriving DecidableEq, Repr

def scalarOfBytes (bytes : List Byte) (length_eq : bytes.length = scalarLength) : EncodedScalar :=
  { bytes, length_eq }

structure InputPair where
  point : EncodedPoint
  scalar : EncodedScalar
  deriving DecidableEq, Repr

def decodeInput (input : List Byte) : InputPair :=
  let normalized := effectiveInput input
  let pointBytes := normalized.take pointLength
  let scalarBytes := normalized.drop pointLength
  have hNormalized : normalized.length = inputLength := by
    simp only [normalized, effectiveInput, List.length_append, List.length_take,
      List.length_replicate]
    omega
  have hPoint : pointBytes.length = pointLength := by
    simp only [pointBytes, List.length_take]
    simp only [inputLength, pointLength] at hNormalized ⊢
    omega
  have hScalar : scalarBytes.length = scalarLength := by
    simp only [scalarBytes, List.length_drop]
    simp only [inputLength, pointLength, scalarLength] at hNormalized ⊢
    omega
  { point := pointOfBytes pointBytes hPoint, scalar := scalarOfBytes scalarBytes hScalar }

structure CurveOracle where
  validPoint : EncodedPoint -> Bool
  multiply : EncodedPoint -> EncodedScalar -> Option EncodedPoint

inductive Error where
  | invalidPoint
  | multiplicationFailed
  deriving DecidableEq, Repr

abbrev Result := Except Error EncodedPoint

def runPair (oracle : CurveOracle) (input : InputPair) : Result :=
  if !oracle.validPoint input.point then
    .error .invalidPoint
  else
    match oracle.multiply input.point input.scalar with
    | none => .error .multiplicationFailed
    | some output => .ok output

def run (oracle : CurveOracle) (input : List Byte) : Result :=
  runPair oracle (decodeInput input)

theorem metadata_contract :
    address = 7 ∧ name = "BN254_MUL" ∧ supportsCaching = true := by
  native_decide

theorem amsterdam_gas_cost (input : List Byte) :
    totalGasCost Schedule.amsterdam input = 6000 := by
  rfl

theorem effective_input_length (input : List Byte) :
    (effectiveInput input).length = inputLength := by
  simp [effectiveInput, inputLength]
  omega

theorem decoded_component_lengths (input : List Byte) :
    (decodeInput input).point.bytes.length = pointLength ∧
      (decodeInput input).scalar.bytes.length = scalarLength := by
  exact ⟨(decodeInput input).point.length_eq, (decodeInput input).scalar.length_eq⟩

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

theorem invalid_point_fails (oracle : CurveOracle) (input : InputPair)
    (hInvalid : oracle.validPoint input.point = false) :
    runPair oracle input = .error .invalidPoint := by
  simp [runPair, hInvalid]

theorem multiplication_failure_fails (oracle : CurveOracle) (input : InputPair)
    (hValid : oracle.validPoint input.point = true)
    (hFailed : oracle.multiply input.point input.scalar = none) :
    runPair oracle input = .error .multiplicationFailed := by
  simp [runPair, hValid, hFailed]

theorem valid_input_returns_exact_oracle_product (oracle : CurveOracle) (input : InputPair)
    (output : EncodedPoint)
    (hValid : oracle.validPoint input.point = true)
    (hProduct : oracle.multiply input.point input.scalar = some output) :
    runPair oracle input = .ok output := by
  simp [runPair, hValid, hProduct]

theorem successful_output_length (oracle : CurveOracle) (input : List Byte)
    (output : EncodedPoint) (_hRun : run oracle input = .ok output) :
    output.bytes.length = pointLength :=
  output.length_eq

theorem run_classification (oracle : CurveOracle) (input : List Byte) :
    run oracle input = .error .invalidPoint ∨
      run oracle input = .error .multiplicationFailed ∨
      ∃ output, run oracle input = .ok output := by
  unfold run runPair
  split
  · left; rfl
  · split
    · right; left; rfl
    · right; right; exact ⟨_, rfl⟩

end Bn254Mul
end Precompiles
end Eip803x
