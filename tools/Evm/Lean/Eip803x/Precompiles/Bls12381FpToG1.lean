-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.Evm.MemoryStackControl
import Lean.Elab.Tactic.Omega

namespace Eip803x
namespace Precompiles
namespace Bls12381FpToG1

open Evm.MemoryStackControl

/-!
  A standalone handwritten executable reference for the standard-mainnet
  EIP-2537 BLS12_MAP_FP_TO_G1 leaf. Exact framing, canonical field padding,
  output shape, pricing, metadata, and validation order are modeled here.
  Field canonicality and the map-to-G1 operation are explicit oracle fields.
-/

structure Schedule where
  fixedGas : Nat
  deriving DecidableEq, Repr

namespace Schedule

def amsterdam : Schedule := { fixedGas := 5500 }

end Schedule

def address : Nat := 16

def name : String := "BLS12_MAP_FP_TO_G1"

def supportsCaching : Bool := true

def registeredAddress : Nat := address

def registeredName : String := name

def eip2537Gated : Bool := true

def inputLength : Nat := 64

def fpPaddingLength : Nat := 16

def fpValueLength : Nat := 48

def outputLength : Nat := 128

def baseGasCost (schedule : Schedule) : Nat := schedule.fixedGas

def dataGasCost (_ : List Byte) : Nat := 0

def totalGasCost (schedule : Schedule) (input : List Byte) : Nat :=
  baseGasCost schedule + dataGasCost input

def normalizeInput (input : List Byte) : List Byte := input

def zeroPadding : List Byte := List.replicate fpPaddingLength zeroByte

structure FpValue where
  bytes : List Byte
  length_eq : bytes.length = fpValueLength
  deriving DecidableEq, Repr

structure Padding where
  bytes : List Byte
  length_eq : bytes.length = fpPaddingLength
  deriving DecidableEq, Repr

structure G1Output where
  x : FpValue
  y : FpValue
  deriving DecidableEq, Repr

def encodeOutput (output : G1Output) : List Byte :=
  zeroPadding ++ output.x.bytes ++ zeroPadding ++ output.y.bytes

structure DecodedInput where
  padding : Padding
  value : FpValue
  deriving DecidableEq, Repr

def decodeInput? (input : List Byte) : Option DecodedInput :=
  if hLength : input.length = inputLength then
    let paddingBytes := input.take fpPaddingLength
    let valueBytes := input.drop fpPaddingLength
    have hPadding : paddingBytes.length = fpPaddingLength := by
      simp only [paddingBytes, List.length_take]
      simp only [inputLength, fpPaddingLength] at hLength ⊢
      omega
    have hValue : valueBytes.length = fpValueLength := by
      simp only [valueBytes, List.length_drop]
      simp only [inputLength, fpPaddingLength, fpValueLength] at hLength ⊢
      omega
    some
      { padding := { bytes := paddingBytes, length_eq := hPadding }
        value := { bytes := valueBytes, length_eq := hValue } }
  else
    none

def hasCanonicalPadding (input : DecodedInput) : Bool :=
  input.padding.bytes == zeroPadding

structure MapOracle where
  validField : FpValue → Bool
  mapToG1 : FpValue → G1Output

inductive Error where
  | invalidInputLength
  | invalidFieldElementTopBytes
  | invalidFieldElement
  deriving DecidableEq, Repr

abbrev Result := Except Error G1Output

def runDecoded (oracle : MapOracle) (input : DecodedInput) : Result :=
  if !hasCanonicalPadding input then
    .error .invalidFieldElementTopBytes
  else if !oracle.validField input.value then
    .error .invalidFieldElement
  else
    .ok (oracle.mapToG1 input.value)

def run (oracle : MapOracle) (input : List Byte) : Result :=
  match decodeInput? input with
  | none => .error .invalidInputLength
  | some decoded => runDecoded oracle decoded

theorem metadata_contract :
    address = 16 ∧ name = "BLS12_MAP_FP_TO_G1" ∧ supportsCaching = true := by
  native_decide

theorem registration_contract :
    registeredAddress = address ∧ registeredName = name ∧ eip2537Gated = true := by
  native_decide

theorem amsterdam_gas_cost (input : List Byte) :
    totalGasCost Schedule.amsterdam input = 5500 := by
  rfl

theorem data_gas_is_zero (input : List Byte) : dataGasCost input = 0 := by
  rfl

theorem normalization_is_identity (input : List Byte) :
    normalizeInput input = input := by
  rfl

theorem output_length (output : G1Output) :
    (encodeOutput output).length = outputLength := by
  simp only [encodeOutput, List.length_append, outputLength, fpValueLength,
    output.x.length_eq, output.y.length_eq]
  rfl

theorem output_has_zero_x_padding (output : G1Output) :
    (encodeOutput output).take fpPaddingLength = zeroPadding := by
  simp [encodeOutput, zeroPadding, fpPaddingLength]

theorem output_has_zero_y_padding (output : G1Output) :
    ((encodeOutput output).drop (fpPaddingLength + fpValueLength) |>.take fpPaddingLength) =
      zeroPadding := by
  simp [encodeOutput, zeroPadding, fpPaddingLength, fpValueLength, output.x.length_eq]

theorem invalid_length_decode (input : List Byte) (hLength : input.length ≠ inputLength) :
    decodeInput? input = none := by
  simp [decodeInput?, hLength]

theorem invalid_length_run (oracle : MapOracle) (input : List Byte)
    (hLength : input.length ≠ inputLength) :
    run oracle input = .error .invalidInputLength := by
  simp [run, invalid_length_decode input hLength]

theorem decoded_input_lengths (input : List Byte) (decoded : DecodedInput)
    (_hDecode : decodeInput? input = some decoded) :
    decoded.padding.bytes.length = fpPaddingLength ∧
      decoded.value.bytes.length = fpValueLength := by
  exact ⟨decoded.padding.length_eq, decoded.value.length_eq⟩

theorem invalid_padding_fails (oracle : MapOracle) (input : DecodedInput)
    (hPadding : hasCanonicalPadding input = false) :
    runDecoded oracle input = .error .invalidFieldElementTopBytes := by
  simp [runDecoded, hPadding]

theorem invalid_field_fails (oracle : MapOracle) (input : DecodedInput)
    (hPadding : hasCanonicalPadding input = true)
    (hInvalid : oracle.validField input.value = false) :
    runDecoded oracle input = .error .invalidFieldElement := by
  simp [runDecoded, hPadding, hInvalid]

theorem valid_field_reaches_map (oracle : MapOracle) (input : DecodedInput)
    (hPadding : hasCanonicalPadding input = true)
    (hValid : oracle.validField input.value = true) :
    runDecoded oracle input = .ok (oracle.mapToG1 input.value) := by
  simp [runDecoded, hPadding, hValid]

theorem padding_validation_precedes_field (oracle : MapOracle) (input : DecodedInput)
    (hPadding : hasCanonicalPadding input = false) :
    runDecoded oracle input = .error .invalidFieldElementTopBytes := by
  exact invalid_padding_fails oracle input hPadding

theorem successful_output_length (oracle : MapOracle) (input : List Byte)
    (output : G1Output) (_hRun : run oracle input = .ok output) :
    (encodeOutput output).length = outputLength :=
  output_length output

theorem run_classification (oracle : MapOracle) (input : List Byte) :
    run oracle input = .error .invalidInputLength ∨
      run oracle input = .error .invalidFieldElementTopBytes ∨
      run oracle input = .error .invalidFieldElement ∨
      ∃ output, run oracle input = .ok output := by
  unfold run
  split
  · left; rfl
  · unfold runDecoded
    split
    · right; left; rfl
    · split
      · right; right; left; rfl
      · right; right; right; exact ⟨_, rfl⟩

end Bls12381FpToG1
end Precompiles
end Eip803x
