-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.Evm.MemoryStackControl
import Lean.Elab.Tactic.Omega

namespace Eip803x
namespace Precompiles
namespace ModExp

open Evm.MemoryStackControl

/-!
  A handwritten executable Amsterdam reference for the EIP-198 modular
  exponentiation precompile after EIP-2565, EIP-7823, and EIP-7883. Header
  saturation, zero-padding, size admission, gas arithmetic, output shape, and
  cache normalization are modeled here. Big-integer exponentiation is an
  explicit oracle.
-/

structure Schedule where
  maxInputSize : Nat
  minGas : Nat
  shortComplexity : Nat
  largeComplexityMultiplier : Nat
  exponentIterationMultiplier : Nat
  deriving DecidableEq, Repr

namespace Schedule

def amsterdam : Schedule :=
  { maxInputSize := 1024
    minGas := 500
    shortComplexity := 16
    largeComplexityMultiplier := 2
    exponentIterationMultiplier := 16 }

end Schedule

def address : Nat := 5

def name : String := "MODEXP"

def supportsCaching : Bool := true

def lengthWordSize : Nat := 32

def headerLength : Nat := 96

def uint32Max : Nat := 2 ^ 32 - 1

def uint64Max : Nat := 2 ^ 64 - 1

def readNatBE (bytes : List Byte) : Nat :=
  bytes.foldl (fun accumulator value => accumulator * 256 + value.val) 0

def effectiveHeader (input : List Byte) : List Byte :=
  input.take headerLength ++ List.replicate (headerLength - input.length) zeroByte

def lengthWord (input : List Byte) (index : Nat) : List Byte :=
  (effectiveHeader input).drop (index * lengthWordSize) |>.take lengthWordSize

structure Lengths where
  base : Nat
  exponent : Nat
  modulus : Nat
  deriving DecidableEq, Repr

def decodeLengths (input : List Byte) : Lengths :=
  let base := readNatBE (lengthWord input 0)
  let exponent := readNatBE (lengthWord input 1)
  let modulus := readNatBE (lengthWord input 2)
  if uint32Max < base || uint32Max < exponent || uint32Max < modulus then
    if base != 0 || modulus != 0 then
      { base := uint32Max, exponent := uint32Max, modulus := uint32Max }
    else
      { base := 0, exponent := uint32Max, modulus := 0 }
  else
    { base, exponent, modulus }

def sliceWithRightZeroPadding (input : List Byte) (offset length : Nat) : List Byte :=
  readRange input offset length

def baseBytes (input : List Byte) (lengths : Lengths) : List Byte :=
  sliceWithRightZeroPadding input headerLength lengths.base

def exponentBytes (input : List Byte) (lengths : Lengths) : List Byte :=
  sliceWithRightZeroPadding input (headerLength + lengths.base) lengths.exponent

def modulusBytes (input : List Byte) (lengths : Lengths) : List Byte :=
  sliceWithRightZeroPadding input
    (headerLength + lengths.base + lengths.exponent) lengths.modulus

def exponentHeadValue (input : List Byte) (lengths : Lengths) : Nat :=
  readNatBE (sliceWithRightZeroPadding input (headerLength + lengths.base)
    (min lengthWordSize lengths.exponent))

def bitLength (value : Nat) : Nat :=
  if value = 0 then 0 else Nat.log2 value + 1

def iterationCount (schedule : Schedule) (exponentLength exponentHead : Nat) : Nat :=
  if exponentLength ≤ lengthWordSize then
    max 1 (bitLength exponentHead - 1)
  else
    schedule.exponentIterationMultiplier * (exponentLength - lengthWordSize) +
      (bitLength exponentHead - 1)

def multiplicationComplexity (schedule : Schedule) (baseLength modulusLength : Nat) : Nat :=
  let maximumLength := max baseLength modulusLength
  let words := (maximumLength + 7) / 8
  if maximumLength > lengthWordSize then
    schedule.largeComplexityMultiplier * words * words
  else
    schedule.shortComplexity

def saturateU64 (value : Nat) : Nat := min value uint64Max

def baseGasCost (_ : Schedule) : Nat := 0

def dataGasCost (schedule : Schedule) (input : List Byte) : Nat :=
  let lengths := decodeLengths input
  let complexity := multiplicationComplexity schedule lengths.base lengths.modulus
  let iterations := iterationCount schedule lengths.exponent (exponentHeadValue input lengths)
  saturateU64 (max schedule.minGas (complexity * iterations))

def totalGasCost (schedule : Schedule) (input : List Byte) : Nat :=
  baseGasCost schedule + dataGasCost schedule input

def sizesAdmitted (schedule : Schedule) (lengths : Lengths) : Bool :=
  lengths.base ≤ schedule.maxInputSize &&
    lengths.exponent ≤ schedule.maxInputSize &&
    lengths.modulus ≤ schedule.maxInputSize

def allZero (bytes : List Byte) : Bool := bytes.all (· == zeroByte)

def cacheNormalizedInput (input : List Byte) : List Byte :=
  if input.length ≤ headerLength then
    input
  else
    let lengths := decodeLengths input
    if lengths.base = uint32Max || lengths.modulus = uint32Max ||
        (lengths.base = 0 && lengths.modulus = 0) then
      input.take headerLength
    else
      input.take (headerLength + lengths.base + lengths.exponent + lengths.modulus)

structure Oracle where
  compute : List Byte -> List Byte -> List Byte -> Option (List Byte)

inductive Error where
  | inputSizeExceeded
  | computationFailed
  deriving DecidableEq, Repr

abbrev Result := Except Error (List Byte)

def run (schedule : Schedule) (oracle : Oracle) (input : List Byte) : Result :=
  let lengths := decodeLengths input
  if !sizesAdmitted schedule lengths then
    .error .inputSizeExceeded
  else if lengths.base = 0 && lengths.modulus = 0 then
    .ok []
  else
    let modulus := modulusBytes input lengths
    if allZero modulus then
      .ok (List.replicate lengths.modulus zeroByte)
    else
      match oracle.compute (baseBytes input lengths) (exponentBytes input lengths) modulus with
      | none => .error .computationFailed
      | some output =>
          if output.length = lengths.modulus then .ok output else .error .computationFailed

theorem metadata_contract :
    address = 5 ∧ name = "MODEXP" ∧ supportsCaching = true := by
  native_decide

theorem effective_header_length (input : List Byte) :
    (effectiveHeader input).length = headerLength := by
  simp [effectiveHeader, headerLength]
  omega

theorem base_slice_length (input : List Byte) (lengths : Lengths) :
    (baseBytes input lengths).length = lengths.base := by
  simp [baseBytes, sliceWithRightZeroPadding, readRange]

theorem exponent_slice_length (input : List Byte) (lengths : Lengths) :
    (exponentBytes input lengths).length = lengths.exponent := by
  simp [exponentBytes, sliceWithRightZeroPadding, readRange]

theorem modulus_slice_length (input : List Byte) (lengths : Lengths) :
    (modulusBytes input lengths).length = lengths.modulus := by
  simp [modulusBytes, sliceWithRightZeroPadding, readRange]

theorem gas_has_amsterdam_floor (input : List Byte) :
    Schedule.amsterdam.minGas ≤ dataGasCost Schedule.amsterdam input := by
  simp [dataGasCost, saturateU64, Schedule.amsterdam, uint64Max]
  omega

theorem zero_base_and_modulus_return_empty (oracle : Oracle) (input : List Byte)
    (hLengths : decodeLengths input = { base := 0, exponent := 0, modulus := 0 }) :
    run Schedule.amsterdam oracle input = .ok [] := by
  simp [run, hLengths, sizesAdmitted, Schedule.amsterdam]

theorem oversized_input_fails (oracle : Oracle) (input : List Byte)
    (hRejected : sizesAdmitted Schedule.amsterdam (decodeLengths input) = false) :
    run Schedule.amsterdam oracle input = .error .inputSizeExceeded := by
  simp [run, hRejected]

theorem zero_modulus_returns_exact_width (oracle : Oracle) (input : List Byte)
    (lengths : Lengths)
    (hLengths : decodeLengths input = lengths)
    (hAdmitted : sizesAdmitted Schedule.amsterdam lengths = true)
    (hZero : allZero (modulusBytes input lengths) = true) :
    run Schedule.amsterdam oracle input = .ok (List.replicate lengths.modulus zeroByte) := by
  simp [run, hLengths, hAdmitted, hZero]

theorem oracle_success_returns_exact_output (oracle : Oracle) (input output : List Byte)
    (lengths : Lengths)
    (hLengths : decodeLengths input = lengths)
    (hAdmitted : sizesAdmitted Schedule.amsterdam lengths = true)
    (hBaseOrMod : lengths.base ≠ 0 ∨ lengths.modulus ≠ 0)
    (hModulus : allZero (modulusBytes input lengths) = false)
    (hCompute : oracle.compute (baseBytes input lengths) (exponentBytes input lengths)
      (modulusBytes input lengths) = some output)
    (hOutput : output.length = lengths.modulus) :
    run Schedule.amsterdam oracle input = .ok output := by
  have hNotBoth : ¬(lengths.base = 0 ∧ lengths.modulus = 0) := by
    intro hBoth
    rcases hBaseOrMod with hBase | hModulus
    · exact hBase hBoth.1
    · exact hModulus hBoth.2
  simp [run, hLengths, hAdmitted, hNotBoth, hModulus, hCompute, hOutput]

theorem successful_output_has_modulus_width (oracle : Oracle) (input output : List Byte)
    (hRun : run Schedule.amsterdam oracle input = .ok output) :
    output.length = (decodeLengths input).modulus := by
  simp only [run] at hRun
  split at hRun <;> simp_all
  split at hRun
  · simp_all
  split at hRun
  · cases hRun
    simp
  split at hRun
  · simp_all
  split at hRun <;> simp_all

end ModExp
end Precompiles
end Eip803x
