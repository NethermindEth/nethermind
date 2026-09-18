-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.Evm.MemoryStackControl

namespace Eip803x
namespace Precompiles
namespace Blake2F

open Evm.MemoryStackControl

/-!
  A handwritten executable reference for the EIP-152 BLAKE2 compression
  precompile leaf. The input grammar, normalization, round pricing, result
  shape, and metadata are modeled here. The BLAKE2b compression function is an
  explicit oracle and is not claimed as a verified cryptographic primitive.
-/

structure Schedule where
  gasPerRound : Nat
  deriving DecidableEq, Repr

namespace Schedule

def amsterdam : Schedule := { gasPerRound := 1 }

end Schedule

def address : Nat := 9

def name : String := "BLAKE2F"

def supportsCaching : Bool := true

def requiredInputLength : Nat := 213

def outputLength : Nat := 64

def baseGasCost : Nat := 0

def byteValue (value : Byte) : Nat := value.val

def rounds (input : List Byte) : Nat :=
  (input.take 4).foldl (fun accumulator value => accumulator * 256 + byteValue value) 0

def finalFlag (input : List Byte) : Byte := readByte input 212

def validFinalFlag (value : Byte) : Bool :=
  value == byte 0 || value == byte 1

def invalidLengthInput : List Byte := []

def invalidFlagInput : List Byte :=
  List.replicate 212 zeroByte ++ [byte 0xff]

def normalizeInput (input : List Byte) : List Byte :=
  if input.length != requiredInputLength then
    invalidLengthInput
  else if validFinalFlag (finalFlag input) then
    input
  else
    invalidFlagInput

def dataGasCost (schedule : Schedule) (input : List Byte) : Nat :=
  if input.length != requiredInputLength then
    0
  else if validFinalFlag (finalFlag input) then
    schedule.gasPerRound * rounds input
  else
    0

def totalGasCost (schedule : Schedule) (input : List Byte) : Nat :=
  baseGasCost + dataGasCost schedule input

structure Output where
  bytes : List Byte
  length_eq : bytes.length = outputLength
  deriving DecidableEq, Repr

def outputOfBytes (bytes : List Byte) (length_eq : bytes.length = outputLength) : Output :=
  { bytes, length_eq }

structure CompressionOracle where
  compress : List Byte -> Output

inductive Error where
  | invalidInputLength
  | invalidFinalBlockFlag
  deriving DecidableEq, Repr

abbrev Result := Except Error Output

def run (oracle : CompressionOracle) (input : List Byte) : Result :=
  if input.length != requiredInputLength then
    .error .invalidInputLength
  else if validFinalFlag (finalFlag input) then
    .ok (oracle.compress input)
  else
    .error .invalidFinalBlockFlag

theorem metadata_contract :
    address = 9 ∧ name = "BLAKE2F" ∧ supportsCaching = true := by
  native_decide

theorem invalid_flag_input_length : invalidFlagInput.length = requiredInputLength := by
  native_decide

theorem invalid_flag_input_is_invalid : validFinalFlag (finalFlag invalidFlagInput) = false := by
  native_decide

theorem invalid_length_has_zero_data_gas (schedule : Schedule) (input : List Byte)
    (hLength : input.length ≠ requiredInputLength) :
    dataGasCost schedule input = 0 := by
  simp [dataGasCost, hLength]

theorem invalid_flag_has_zero_data_gas (schedule : Schedule) (input : List Byte)
    (hLength : input.length = requiredInputLength)
    (hFlag : validFinalFlag (finalFlag input) = false) :
    dataGasCost schedule input = 0 := by
  simp [dataGasCost, hLength, hFlag]

theorem valid_input_cost (schedule : Schedule) (input : List Byte)
    (hLength : input.length = requiredInputLength)
    (hFlag : validFinalFlag (finalFlag input) = true) :
    totalGasCost schedule input = schedule.gasPerRound * rounds input := by
  simp [totalGasCost, baseGasCost, dataGasCost, hLength, hFlag]

theorem invalid_length_run (oracle : CompressionOracle) (input : List Byte)
    (hLength : input.length ≠ requiredInputLength) :
    run oracle input = .error .invalidInputLength := by
  simp [run, hLength]

theorem invalid_flag_run (oracle : CompressionOracle) (input : List Byte)
    (hLength : input.length = requiredInputLength)
    (hFlag : validFinalFlag (finalFlag input) = false) :
    run oracle input = .error .invalidFinalBlockFlag := by
  simp [run, hLength, hFlag]

theorem valid_input_run (oracle : CompressionOracle) (input : List Byte)
    (hLength : input.length = requiredInputLength)
    (hFlag : validFinalFlag (finalFlag input) = true) :
    run oracle input = .ok (oracle.compress input) := by
  simp [run, hLength, hFlag]

theorem oracle_output_length (oracle : CompressionOracle) (input : List Byte) :
    (oracle.compress input).bytes.length = outputLength :=
  (oracle.compress input).length_eq

theorem invalid_length_normalizes_to_empty (input : List Byte)
    (hLength : input.length ≠ requiredInputLength) :
    normalizeInput input = [] := by
  simp [normalizeInput, hLength, invalidLengthInput]

theorem valid_input_normalizes_to_self (input : List Byte)
    (hLength : input.length = requiredInputLength)
    (hFlag : validFinalFlag (finalFlag input) = true) :
    normalizeInput input = input := by
  simp [normalizeInput, hLength, hFlag]

theorem invalid_flag_normalizes_to_sentinel (input : List Byte)
    (hLength : input.length = requiredInputLength)
    (hFlag : validFinalFlag (finalFlag input) = false) :
    normalizeInput input = invalidFlagInput := by
  simp [normalizeInput, hLength, hFlag]

theorem run_normalized (oracle : CompressionOracle) (input : List Byte) :
    run oracle (normalizeInput input) = run oracle input := by
  by_cases hLength : input.length = requiredInputLength
  · by_cases hFlag : validFinalFlag (finalFlag input) = true
    · rw [valid_input_normalizes_to_self input hLength hFlag]
    · have hFlagFalse : validFinalFlag (finalFlag input) = false := by
        cases hValue : validFinalFlag (finalFlag input) <;> simp_all
      rw [invalid_flag_normalizes_to_sentinel input hLength hFlagFalse]
      rw [invalid_flag_run oracle input hLength hFlagFalse]
      exact invalid_flag_run oracle invalidFlagInput invalid_flag_input_length
        invalid_flag_input_is_invalid
  · rw [invalid_length_normalizes_to_empty input hLength]
    rw [invalid_length_run oracle input hLength]
    exact invalid_length_run oracle [] (by native_decide)

end Blake2F
end Precompiles
end Eip803x
