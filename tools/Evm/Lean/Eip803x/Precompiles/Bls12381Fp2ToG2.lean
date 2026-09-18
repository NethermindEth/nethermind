-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.Evm.MemoryStackControl
import Lean.Elab.Tactic.Omega

/-!
  The accepted handwritten executable reference for the standard
  EIP-2537 BLS12_MAP_FP2_TO_G2 leaf.  It fixes framing, ordered field-element
  validation, output wire shape, pricing, and metadata.  Canonical Fp
  comparison and map-to-G2 remain named oracle boundaries.
-/

namespace Eip803x
namespace Precompiles
namespace Bls12381Fp2ToG2

open Evm.MemoryStackControl

structure Schedule where
  fixedGas : Nat
  dataGas : Nat
  deriving DecidableEq, Repr

namespace Schedule

def amsterdam : Schedule := { fixedGas := 23800, dataGas := 0 }

end Schedule

def address : Nat := 17

def name : String := "BLS12_MAP_FP2_TO_G2"

def supportsCaching : Bool := true

def registeredAddress : Nat := address

def registeredName : String := name

def cachedAddress : Nat := address

def eip2537Gated : Bool := true

structure Metadata where
  address : Nat
  name : String
  supportsCaching : Bool
  registeredAddress : Nat
  registeredName : String
  cachedAddress : Nat
  eip2537Gated : Bool
  deriving DecidableEq, Repr

def metadata : Metadata :=
  { address := address
    name := name
    supportsCaching := supportsCaching
    registeredAddress := registeredAddress
    registeredName := registeredName
    cachedAddress := cachedAddress
    eip2537Gated := eip2537Gated }

def metadataMatchesRegistration (candidate : Metadata) : Bool :=
  candidate.address == registeredAddress && candidate.name == registeredName &&
    candidate.supportsCaching == supportsCaching &&
    candidate.registeredAddress == registeredAddress && candidate.registeredName == registeredName &&
    candidate.cachedAddress == cachedAddress && candidate.eip2537Gated == eip2537Gated

def inputLength : Nat := 128

def fpElementLength : Nat := 64

def fpPaddingLength : Nat := 16

def fpValueLength : Nat := 48

def g2ElementCount : Nat := 4

def outputLength : Nat := 256

def baseGasCost (schedule : Schedule) : Nat := schedule.fixedGas

def dataGasCost (schedule : Schedule) (_ : List Byte) : Nat := schedule.dataGas

def totalGasCost (schedule : Schedule) (input : List Byte) : Nat :=
  baseGasCost schedule + dataGasCost schedule input

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

structure FpElement where
  padding : Padding
  value : FpValue
  deriving DecidableEq, Repr

/-- Input wire order is exactly the first then second 64-byte Fp element. -/
structure Fp2Input where
  first : FpElement
  second : FpElement
  deriving DecidableEq, Repr

/--
  The four 64-byte G2 wire elements, retained in their encoded order.  Their
  correspondence to the standard and accelerator internal permutations is a
  refinement obligation, not a theorem of this candidate.
-/
structure G2Output where
  wire0 : FpValue
  wire1 : FpValue
  wire2 : FpValue
  wire3 : FpValue
  deriving DecidableEq, Repr

def encodeFpElement (value : FpValue) : List Byte :=
  zeroPadding ++ value.bytes

/-- Encodes exactly four padded Fp values in the oracle-supplied G2 wire order. -/
def encodeOutput (output : G2Output) : List Byte :=
  encodeFpElement output.wire0 ++ encodeFpElement output.wire1 ++
    encodeFpElement output.wire2 ++ encodeFpElement output.wire3

def decodeFpElement (raw : List Byte) (hLength : raw.length = fpElementLength) : FpElement :=
  let paddingBytes := raw.take fpPaddingLength
  let valueBytes := raw.drop fpPaddingLength
  have hPadding : paddingBytes.length = fpPaddingLength := by
    simp only [paddingBytes, List.length_take]
    simp only [fpElementLength, fpPaddingLength] at hLength ⊢
    omega
  have hValue : valueBytes.length = fpValueLength := by
    simp only [valueBytes, List.length_drop]
    simp only [fpElementLength, fpPaddingLength, fpValueLength] at hLength ⊢
    omega
  { padding := { bytes := paddingBytes, length_eq := hPadding }
    value := { bytes := valueBytes, length_eq := hValue } }

def decodeInput? (input : List Byte) : Option Fp2Input :=
  if hLength : input.length = inputLength then
    let firstBytes := input.take fpElementLength
    let secondBytes := input.drop fpElementLength
    have hFirst : firstBytes.length = fpElementLength := by
      simp only [firstBytes, List.length_take]
      simp only [inputLength, fpElementLength] at hLength ⊢
      omega
    have hSecond : secondBytes.length = fpElementLength := by
      simp only [secondBytes, List.length_drop]
      simp only [inputLength, fpElementLength] at hLength ⊢
      omega
    some
      { first := decodeFpElement firstBytes hFirst
        second := decodeFpElement secondBytes hSecond }
  else
    none

def hasCanonicalPadding (element : FpElement) : Bool :=
  element.padding.bytes == zeroPadding

/--
  The field and map implementation boundary.  `validField` is an arbitrary
  predicate here; a production refinement must prove that it is exactly the
  big-endian 48-byte BLS12-381 condition `value < p`.  `mapFp2ToG2` receives
  values in first-then-second order and returns already ordered G2 wire
  elements.
-/
structure MapOracle where
  validField : FpValue → Bool
  mapFp2ToG2 : FpValue → FpValue → G2Output

inductive Error where
  | invalidInputLength
  | invalidFieldElementTopBytes
  | invalidFieldElement
  deriving DecidableEq, Repr

abbrev Result := Except Error G2Output

/--
  The production `&&` sequence: validate all of the first Fp element before
  inspecting padding or canonicality of the second one, then invoke the map.
-/
def runDecoded (oracle : MapOracle) (input : Fp2Input) : Result :=
  if !hasCanonicalPadding input.first then
    .error .invalidFieldElementTopBytes
  else if !oracle.validField input.first.value then
    .error .invalidFieldElement
  else if !hasCanonicalPadding input.second then
    .error .invalidFieldElementTopBytes
  else if !oracle.validField input.second.value then
    .error .invalidFieldElement
  else
    .ok (oracle.mapFp2ToG2 input.first.value input.second.value)

def run (oracle : MapOracle) (input : List Byte) : Result :=
  match decodeInput? input with
  | none => .error .invalidInputLength
  | some decoded => runDecoded oracle decoded

theorem metadata_contract :
    address = 17 ∧ name = "BLS12_MAP_FP2_TO_G2" ∧ supportsCaching = true ∧
      cachedAddress = address := by
  native_decide

theorem metadata_projection_contract :
    metadataMatchesRegistration metadata = true := by
  native_decide

theorem registration_contract :
    registeredAddress = address ∧ registeredName = name ∧ eip2537Gated = true := by
  native_decide

theorem framing_contract :
    inputLength = 2 * fpElementLength ∧ fpElementLength = fpPaddingLength + fpValueLength ∧
      outputLength = g2ElementCount * fpElementLength := by
  native_decide

theorem amsterdam_gas_cost (input : List Byte) :
    totalGasCost Schedule.amsterdam input = 23800 := by
  rfl

theorem data_gas_is_configurable (schedule : Schedule) (input : List Byte) :
    dataGasCost schedule input = schedule.dataGas := by
  rfl

theorem amsterdam_data_gas_is_zero (input : List Byte) :
    dataGasCost Schedule.amsterdam input = 0 := by
  rfl

theorem normalization_is_identity (input : List Byte) :
    normalizeInput input = input := by
  rfl

theorem encoded_fp_element_length (value : FpValue) :
    (encodeFpElement value).length = fpElementLength := by
  simp only [encodeFpElement, List.length_append, zeroPadding, List.length_replicate,
    value.length_eq, fpPaddingLength, fpValueLength, fpElementLength]

theorem output_length (output : G2Output) :
    (encodeOutput output).length = outputLength := by
  simp only [encodeOutput, List.length_append, encoded_fp_element_length]
  native_decide

theorem output_first_padding (output : G2Output) :
    (encodeOutput output).take fpPaddingLength = zeroPadding := by
  simp [encodeOutput, encodeFpElement, zeroPadding, fpPaddingLength]

theorem output_slot_lengths (output : G2Output) :
    (encodeFpElement output.wire0).length = fpElementLength ∧
    (encodeFpElement output.wire1).length = fpElementLength ∧
    (encodeFpElement output.wire2).length = fpElementLength ∧
    (encodeFpElement output.wire3).length = fpElementLength := by
  exact ⟨encoded_fp_element_length output.wire0, encoded_fp_element_length output.wire1,
    encoded_fp_element_length output.wire2, encoded_fp_element_length output.wire3⟩

theorem invalid_length_decode (input : List Byte) (hLength : input.length ≠ inputLength) :
    decodeInput? input = none := by
  simp [decodeInput?, hLength]

theorem invalid_length_run (oracle : MapOracle) (input : List Byte)
    (hLength : input.length ≠ inputLength) :
    run oracle input = .error .invalidInputLength := by
  simp [run, invalid_length_decode input hLength]

theorem decoded_input_lengths (input : List Byte) (decoded : Fp2Input)
    (_hDecode : decodeInput? input = some decoded) :
    decoded.first.padding.bytes.length = fpPaddingLength ∧
      decoded.first.value.bytes.length = fpValueLength ∧
      decoded.second.padding.bytes.length = fpPaddingLength ∧
      decoded.second.value.bytes.length = fpValueLength := by
  exact ⟨decoded.first.padding.length_eq, decoded.first.value.length_eq,
    decoded.second.padding.length_eq, decoded.second.value.length_eq⟩

theorem first_padding_fails (oracle : MapOracle) (input : Fp2Input)
    (hPadding : hasCanonicalPadding input.first = false) :
    runDecoded oracle input = .error .invalidFieldElementTopBytes := by
  simp [runDecoded, hPadding]

theorem first_field_fails (oracle : MapOracle) (input : Fp2Input)
    (hPadding : hasCanonicalPadding input.first = true)
    (hInvalid : oracle.validField input.first.value = false) :
    runDecoded oracle input = .error .invalidFieldElement := by
  simp [runDecoded, hPadding, hInvalid]

theorem second_padding_fails (oracle : MapOracle) (input : Fp2Input)
    (hFirstPadding : hasCanonicalPadding input.first = true)
    (hFirstValid : oracle.validField input.first.value = true)
    (hSecondPadding : hasCanonicalPadding input.second = false) :
    runDecoded oracle input = .error .invalidFieldElementTopBytes := by
  simp [runDecoded, hFirstPadding, hFirstValid, hSecondPadding]

theorem second_field_fails (oracle : MapOracle) (input : Fp2Input)
    (hFirstPadding : hasCanonicalPadding input.first = true)
    (hFirstValid : oracle.validField input.first.value = true)
    (hSecondPadding : hasCanonicalPadding input.second = true)
    (hSecondInvalid : oracle.validField input.second.value = false) :
    runDecoded oracle input = .error .invalidFieldElement := by
  simp [runDecoded, hFirstPadding, hFirstValid, hSecondPadding, hSecondInvalid]

theorem valid_fields_reach_ordered_map (oracle : MapOracle) (input : Fp2Input)
    (hFirstPadding : hasCanonicalPadding input.first = true)
    (hFirstValid : oracle.validField input.first.value = true)
    (hSecondPadding : hasCanonicalPadding input.second = true)
    (hSecondValid : oracle.validField input.second.value = true) :
    runDecoded oracle input = .ok (oracle.mapFp2ToG2 input.first.value input.second.value) := by
  simp [runDecoded, hFirstPadding, hFirstValid, hSecondPadding, hSecondValid]

theorem successful_output_length (oracle : MapOracle) (input : List Byte)
    (output : G2Output) (_hRun : run oracle input = .ok output) :
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
      · split
        · right; left; rfl
        · split
          · right; right; left; rfl
          · right; right; right; exact ⟨_, rfl⟩

end Bls12381Fp2ToG2
end Precompiles
end Eip803x
