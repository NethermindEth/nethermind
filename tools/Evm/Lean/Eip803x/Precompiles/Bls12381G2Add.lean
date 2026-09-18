-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.Evm.MemoryStackControl
import Lean.Elab.Tactic.Omega

namespace Eip803x
namespace Precompiles
namespace Bls12381G2Add

open Evm.MemoryStackControl

/-!
  A handwritten executable reference for the standard-mainnet EIP-2537
  BLS12_G2ADD leaf. Framing, left-to-right point validation, infinity
  shortcuts, result shape, pricing, and metadata are modeled here. Field and
  curve validation plus nontrivial group addition are an explicit oracle.
-/

structure Schedule where
  fixedGas : Nat
  deriving DecidableEq, Repr

namespace Schedule

def amsterdam : Schedule := { fixedGas := 600 }

end Schedule

def address : Nat := 13

def name : String := "BLS12_G2ADD"

def supportsCaching : Bool := true

def inputLength : Nat := 512

def pointLength : Nat := 256

def baseGasCost (schedule : Schedule) : Nat := schedule.fixedGas

def dataGasCost (_ : List Byte) : Nat := 0

def totalGasCost (schedule : Schedule) (input : List Byte) : Nat :=
  baseGasCost schedule + dataGasCost input

def normalizeInput (input : List Byte) : List Byte := input

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

def decodeInput? (input : List Byte) : Option InputPair :=
  if hLength : input.length = inputLength then
    let leftBytes := input.take pointLength
    let rightBytes := input.drop pointLength
    have hLeft : leftBytes.length = pointLength := by
      simp only [leftBytes, List.length_take]
      simp only [inputLength, pointLength] at hLength ⊢
      omega
    have hRight : rightBytes.length = pointLength := by
      simp only [rightBytes, List.length_drop]
      simp only [inputLength, pointLength] at hLength ⊢
      omega
    some { left := pointOfBytes leftBytes hLeft, right := pointOfBytes rightBytes hRight }
  else
    none

structure CurveOracle where
  valid : EncodedPoint -> Bool
  isInfinity : EncodedPoint -> Bool
  add : EncodedPoint -> EncodedPoint -> EncodedPoint

inductive Error where
  | invalidInputLength
  | invalidPoint
  deriving DecidableEq, Repr

abbrev Result := Except Error EncodedPoint

def runPair (oracle : CurveOracle) (input : InputPair) : Result :=
  if !oracle.valid input.left then
    .error .invalidPoint
  else if !oracle.valid input.right then
    .error .invalidPoint
  else if oracle.isInfinity input.left then
    .ok input.right
  else if oracle.isInfinity input.right then
    .ok input.left
  else
    .ok (oracle.add input.left input.right)

def run (oracle : CurveOracle) (input : List Byte) : Result :=
  match decodeInput? input with
  | none => .error .invalidInputLength
  | some decoded => runPair oracle decoded

theorem metadata_contract :
    address = 13 ∧ name = "BLS12_G2ADD" ∧ supportsCaching = true := by
  native_decide

theorem amsterdam_gas_cost (input : List Byte) :
    totalGasCost Schedule.amsterdam input = 600 := by
  rfl

theorem normalization_is_identity (input : List Byte) :
    normalizeInput input = input := by
  rfl

theorem invalid_length_decode (input : List Byte) (hLength : input.length ≠ inputLength) :
    decodeInput? input = none := by
  simp [decodeInput?, hLength]

theorem invalid_length_run (oracle : CurveOracle) (input : List Byte)
    (hLength : input.length ≠ inputLength) :
    run oracle input = .error .invalidInputLength := by
  simp [run, invalid_length_decode input hLength]

theorem decoded_point_lengths (input : List Byte) (decoded : InputPair)
    (_hDecode : decodeInput? input = some decoded) :
    decoded.left.bytes.length = pointLength ∧ decoded.right.bytes.length = pointLength := by
  exact ⟨decoded.left.length_eq, decoded.right.length_eq⟩

theorem invalid_left_fails (oracle : CurveOracle) (input : InputPair)
    (hInvalid : oracle.valid input.left = false) :
    runPair oracle input = .error .invalidPoint := by
  simp [runPair, hInvalid]

theorem invalid_right_fails (oracle : CurveOracle) (input : InputPair)
    (hLeft : oracle.valid input.left = true)
    (hInvalid : oracle.valid input.right = false) :
    runPair oracle input = .error .invalidPoint := by
  simp [runPair, hLeft, hInvalid]

theorem left_infinity_returns_right (oracle : CurveOracle) (input : InputPair)
    (hLeft : oracle.valid input.left = true)
    (hRight : oracle.valid input.right = true)
    (hInfinity : oracle.isInfinity input.left = true) :
    runPair oracle input = .ok input.right := by
  simp [runPair, hLeft, hRight, hInfinity]

theorem right_infinity_returns_left (oracle : CurveOracle) (input : InputPair)
    (hLeft : oracle.valid input.left = true)
    (hRight : oracle.valid input.right = true)
    (hLeftFinite : oracle.isInfinity input.left = false)
    (hInfinity : oracle.isInfinity input.right = true) :
    runPair oracle input = .ok input.left := by
  simp [runPair, hLeft, hRight, hLeftFinite, hInfinity]

theorem finite_points_return_oracle_sum (oracle : CurveOracle) (input : InputPair)
    (hLeft : oracle.valid input.left = true)
    (hRight : oracle.valid input.right = true)
    (hLeftFinite : oracle.isInfinity input.left = false)
    (hRightFinite : oracle.isInfinity input.right = false) :
    runPair oracle input = .ok (oracle.add input.left input.right) := by
  simp [runPair, hLeft, hRight, hLeftFinite, hRightFinite]

theorem successful_output_length (oracle : CurveOracle) (input : List Byte)
    (output : EncodedPoint) (_hRun : run oracle input = .ok output) :
    output.bytes.length = pointLength :=
  output.length_eq

theorem run_classification (oracle : CurveOracle) (input : List Byte) :
    run oracle input = .error .invalidInputLength ∨
      run oracle input = .error .invalidPoint ∨
      ∃ output, run oracle input = .ok output := by
  unfold run
  split
  · left; rfl
  · unfold runPair
    split
    · right; left; rfl
    · split
      · right; left; rfl
      · split
        · right; right; exact ⟨_, rfl⟩
        · split
          · right; right; exact ⟨_, rfl⟩
          · right; right; exact ⟨_, rfl⟩

end Bls12381G2Add
end Precompiles
end Eip803x
