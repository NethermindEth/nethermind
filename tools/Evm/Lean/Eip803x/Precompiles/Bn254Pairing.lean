-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.Evm.MemoryStackControl

namespace Eip803x
namespace Precompiles
namespace Bn254Pairing

open Evm.MemoryStackControl

/-!
  A handwritten executable reference for the standard-mainnet EIP-197/EIP-1108
  BN254_PAIRING precompile leaf. Exact input-length admission, pair-count gas,
  empty-input behavior, Boolean output encoding, and metadata are modeled here.
  G1/G2 decoding, subgroup checks, Miller loops, final exponentiation, and the
  pairing-product predicate are an explicit oracle.
-/

structure Schedule where
  baseGas : Nat
  pairGas : Nat
  deriving DecidableEq, Repr

namespace Schedule

def amsterdam : Schedule := { baseGas := 45000, pairGas := 34000 }

end Schedule

def address : Nat := 8

def name : String := "BN254_PAIRING"

def supportsCaching : Bool := true

def pairLength : Nat := 192

def outputLength : Nat := 32

def pairCount (input : List Byte) : Nat := input.length / pairLength

def validInputLength (input : List Byte) : Bool := input.length % pairLength == 0

def baseGasCost (schedule : Schedule) : Nat := schedule.baseGas

def dataGasCost (schedule : Schedule) (input : List Byte) : Nat :=
  schedule.pairGas * pairCount input

def totalGasCost (schedule : Schedule) (input : List Byte) : Nat :=
  baseGasCost schedule + dataGasCost schedule input

def cacheNormalizedInput (input : List Byte) : List Byte := input

def encodedBoolean (value : Bool) : List Byte :=
  List.replicate 31 zeroByte ++ [if value then byte 0x01 else zeroByte]

structure PairingOracle where
  check : List Byte -> Option Bool

inductive Error where
  | invalidInputLength
  | pairingFailed
  deriving DecidableEq, Repr

abbrev Result := Except Error (List Byte)

def run (oracle : PairingOracle) (input : List Byte) : Result :=
  if !validInputLength input then
    .error .invalidInputLength
  else if input.isEmpty then
    .ok (encodedBoolean true)
  else
    match oracle.check input with
    | none => .error .pairingFailed
    | some value => .ok (encodedBoolean value)

theorem metadata_contract :
    address = 8 ∧ name = "BN254_PAIRING" ∧ supportsCaching = true := by
  native_decide

theorem amsterdam_gas_cost (input : List Byte) :
    totalGasCost Schedule.amsterdam input = 45000 + 34000 * (input.length / 192) := by
  rfl

theorem empty_input_is_valid : validInputLength [] = true := by
  native_decide

theorem cache_input_is_exact (input : List Byte) : cacheNormalizedInput input = input := by
  rfl

theorem empty_input_returns_one (oracle : PairingOracle) :
    run oracle [] = .ok (encodedBoolean true) := by
  rfl

theorem invalid_length_fails (oracle : PairingOracle) (input : List Byte)
    (hInvalid : validInputLength input = false) :
    run oracle input = .error .invalidInputLength := by
  simp [run, hInvalid]

theorem oracle_failure_fails (oracle : PairingOracle) (input : List Byte)
    (hLength : validInputLength input = true)
    (hNonempty : input.isEmpty = false)
    (hFailure : oracle.check input = none) :
    run oracle input = .error .pairingFailed := by
  simp [run, hLength, hNonempty, hFailure]

theorem oracle_boolean_is_encoded_exactly (oracle : PairingOracle) (input : List Byte)
    (value : Bool)
    (hLength : validInputLength input = true)
    (hNonempty : input.isEmpty = false)
    (hCheck : oracle.check input = some value) :
    run oracle input = .ok (encodedBoolean value) := by
  simp [run, hLength, hNonempty, hCheck]

theorem output_length (value : Bool) : (encodedBoolean value).length = outputLength := by
  cases value <;> native_decide

theorem successful_output_length (oracle : PairingOracle) (input output : List Byte)
    (hRun : run oracle input = .ok output) : output.length = outputLength := by
  unfold run at hRun
  split at hRun
  · contradiction
  · split at hRun
    · cases hRun
      exact output_length true
    · split at hRun
      · contradiction
      · cases hRun
        exact output_length _

theorem false_output_is_all_zero : encodedBoolean false = List.replicate 32 zeroByte := by
  native_decide

theorem true_output_is_big_endian_one :
    encodedBoolean true = List.replicate 31 zeroByte ++ [byte 0x01] := by
  rfl

theorem exact_pair_pricing (schedule : Schedule) (input : List Byte)
    (pairTotal : Nat) (hLength : input.length = pairLength * pairTotal) :
    totalGasCost schedule input = schedule.baseGas + schedule.pairGas * pairTotal := by
  simp [totalGasCost, baseGasCost, dataGasCost, pairCount, hLength, pairLength]

theorem run_classification (oracle : PairingOracle) (input : List Byte) :
    run oracle input = .error .invalidInputLength ∨
      run oracle input = .error .pairingFailed ∨
      run oracle input = .ok (encodedBoolean false) ∨
      run oracle input = .ok (encodedBoolean true) := by
  unfold run
  split
  · left; rfl
  · split
    · right; right; right; rfl
    · split
      · right; left; rfl
      · rename_i value _hValue
        cases value
        · right; right; left; rfl
        · right; right; right; rfl

end Bn254Pairing
end Precompiles
end Eip803x
