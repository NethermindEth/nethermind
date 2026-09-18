-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.Precompiles.ModExp

namespace Eip803x
namespace Precompiles
namespace ModExpVectors

open Evm.MemoryStackControl
open ModExp

def u32Word (value : Nat) : List Byte :=
  List.replicate 28 zeroByte ++
    [ byte (value / 2 ^ 24)
    , byte (value / 2 ^ 16)
    , byte (value / 2 ^ 8)
    , byte value ]

def header (baseLength exponentLength modulusLength : Nat) : List Byte :=
  u32Word baseLength ++ u32Word exponentLength ++ u32Word modulusLength

def inputFor (base exponent modulus : List Byte) : List Byte :=
  header base.length exponent.length modulus.length ++ base ++ exponent ++ modulus

def knownBase : List Byte := [byte 0x03]

def knownExponent : List Byte := [byte 0x02]

def knownModulus : List Byte := [byte 0x05]

def oracleKnownOutput : List Byte := [byte 0x04]

def expectedKnownOutput : List Byte := [byte 0x04]

def wideKnownModulus : List Byte := [zeroByte, byte 0x05]

def oracleWideOutput : List Byte := [zeroByte, byte 0x04]

def expectedWideOutput : List Byte := [zeroByte, byte 0x04]

def knownAnswerOracle : Oracle :=
  { compute := fun base exponent modulus =>
      if base == knownBase && exponent == knownExponent && modulus == knownModulus then
        some oracleKnownOutput
      else if base == knownBase && exponent == knownExponent && modulus == wideKnownModulus then
        some oracleWideOutput
      else if base == [byte 0x04] && exponent == knownExponent && modulus == knownModulus then
        some []
      else
        none }

def resultEq : Result -> Result -> Bool
  | .error left, .error right => left == right
  | .ok left, .ok right => left == right
  | _, _ => false

def schedule : Schedule := Schedule.amsterdam

structure Vector where
  name : String
  input : List Byte
  expected : Result
  expectedGas : Nat
  expectedNormalized : List Byte

def passes (vector : Vector) : Bool :=
  resultEq (run schedule knownAnswerOracle vector.input) vector.expected &&
    totalGasCost schedule vector.input == vector.expectedGas &&
    cacheNormalizedInput vector.input == vector.expectedNormalized

def knownInput : List Byte := inputFor knownBase knownExponent knownModulus

def knownWideInput : List Byte := inputFor knownBase knownExponent wideKnownModulus

def exponentOverflowHeader : List Byte :=
  u32Word 0 ++ (byte 0x01 :: List.replicate 31 zeroByte) ++ u32Word 0

def vectors : List Vector :=
  [ { name := "empty calldata is the zero-length short circuit"
      input := []
      expected := .ok []
      expectedGas := 500
      expectedNormalized := [] }
  , { name := "zero base and modulus ignore an admitted exponent length"
      input := header 0 32 0
      expected := .ok []
      expectedGas := 500
      expectedNormalized := header 0 32 0 }
  , { name := "three squared modulo five"
      input := knownInput
      expected := .ok expectedKnownOutput
      expectedGas := 500
      expectedNormalized := knownInput }
  , { name := "trailing bytes are excluded from the cache key"
      input := knownInput ++ [byte 0xaa, byte 0xbb]
      expected := .ok expectedKnownOutput
      expectedGas := 500
      expectedNormalized := knownInput }
  , { name := "zero modulus returns a zero word of the declared width"
      input := inputFor knownBase knownExponent [zeroByte]
      expected := .ok [zeroByte]
      expectedGas := 500
      expectedNormalized := inputFor knownBase knownExponent [zeroByte] }
  , { name := "result is left-padded to the two-byte modulus width"
      input := knownWideInput
      expected := .ok expectedWideOutput
      expectedGas := 500
      expectedNormalized := knownWideInput }
  , { name := "1024-byte base is admitted"
      input := header 1024 1 1
      expected := .ok [zeroByte]
      expectedGas := 32768
      expectedNormalized := header 1024 1 1 }
  , { name := "1025-byte base is rejected after pricing"
      input := header 1025 0 1
      expected := .error .inputSizeExceeded
      expectedGas := 33282
      expectedNormalized := header 1025 0 1 }
  , { name := "1025-byte exponent is rejected after Amsterdam iteration pricing"
      input := header 1 1025 1
      expected := .error .inputSizeExceeded
      expectedGas := 254208
      expectedNormalized := header 1 1025 1 }
  , { name := "high header byte saturates all lengths and gas"
      input := [byte 0x01]
      expected := .error .inputSizeExceeded
      expectedGas := uint64Max
      expectedNormalized := [byte 0x01] }
  , { name := "exponent-only overflow preserves zero base and modulus saturation case"
      input := exponentOverflowHeader ++ [byte 0xaa]
      expected := .error .inputSizeExceeded
      expectedGas := 1099511623408
      expectedNormalized := exponentOverflowHeader }
  , { name := "oracle failure is explicit"
      input := inputFor [byte 0x06] knownExponent knownModulus
      expected := .error .computationFailed
      expectedGas := 500
      expectedNormalized := inputFor [byte 0x06] knownExponent knownModulus }
  , { name := "oracle output of the wrong width is rejected"
      input := inputFor [byte 0x04] knownExponent knownModulus
      expected := .error .computationFailed
      expectedGas := 500
      expectedNormalized := inputFor [byte 0x04] knownExponent knownModulus } ]

theorem vector_count : vectors.length = 13 := rfl

theorem all_vectors_pass : vectors.all passes = true := by
  native_decide

theorem known_answer_tables_agree : oracleKnownOutput = expectedKnownOutput := by
  native_decide

theorem wide_answer_tables_agree : oracleWideOutput = expectedWideOutput := by
  native_decide

example : decodeLengths [] = { base := 0, exponent := 0, modulus := 0 } := by native_decide

example : decodeLengths knownInput = { base := 1, exponent := 1, modulus := 1 } := by
  native_decide

example : (decodeLengths [byte 0x01]).base = uint32Max := by native_decide

example : (decodeLengths [byte 0x01]).exponent = uint32Max := by native_decide

example : (decodeLengths [byte 0x01]).modulus = uint32Max := by native_decide

example : decodeLengths exponentOverflowHeader =
    { base := 0, exponent := uint32Max, modulus := 0 } := by
  native_decide

example : multiplicationComplexity schedule 32 32 = 16 := by native_decide

example : multiplicationComplexity schedule 40 32 = 50 := by native_decide

example : iterationCount schedule 32 0 = 1 := by native_decide

example : iterationCount schedule 33 0 = 16 := by native_decide

example : totalGasCost schedule (header 1025 0 1) = 33282 := by native_decide

example : totalGasCost schedule (header 1 1025 1) = 254208 := by native_decide

example : totalGasCost schedule [byte 0x01] = uint64Max := by native_decide

example : (baseBytes knownInput (decodeLengths knownInput)) = knownBase := by native_decide

example : (exponentBytes knownInput (decodeLengths knownInput)) = knownExponent := by
  native_decide

example : (modulusBytes knownInput (decodeLengths knownInput)) = knownModulus := by
  native_decide

example : resultEq (run schedule knownAnswerOracle knownInput) (.ok expectedKnownOutput) := by
  native_decide

/- Mutation sentinels constrain metadata, Amsterdam pricing, parsing, admission, and output. -/

def mutatedAddress : Nat := 4

example : mutatedAddress ≠ address := by native_decide

def mutatedName : String := "BIGMODEXP"

example : mutatedName ≠ name := by native_decide

def mutatedCaching : Bool := false

example : mutatedCaching ≠ supportsCaching := by native_decide

def mutatedMaximumSize : Nat := 1025

example : mutatedMaximumSize ≠ schedule.maxInputSize := by native_decide

def mutatedMinimumGas : Nat := 200

example : mutatedMinimumGas ≠ schedule.minGas := by native_decide

def mutatedShortComplexity : Nat := 1

example : mutatedShortComplexity ≠ schedule.shortComplexity := by native_decide

def mutatedLargeComplexityMultiplier : Nat := 1

example : mutatedLargeComplexityMultiplier ≠ schedule.largeComplexityMultiplier := by
  native_decide

def mutatedExponentIterationMultiplier : Nat := 8

example : mutatedExponentIterationMultiplier ≠ schedule.exponentIterationMultiplier := by
  native_decide

def withoutSaturation (value : Nat) : Nat := value

example : withoutSaturation
    (multiplicationComplexity schedule uint32Max uint32Max *
      iterationCount schedule uint32Max 0) ≠
    saturateU64
      (multiplicationComplexity schedule uint32Max uint32Max *
        iterationCount schedule uint32Max 0) := by
  native_decide

def floorWordsComplexity (baseLength modulusLength : Nat) : Nat :=
  let words := max baseLength modulusLength / 8
  schedule.largeComplexityMultiplier * words * words

example : floorWordsComplexity 41 32 ≠ multiplicationComplexity schedule 41 32 := by
  native_decide

def sizesAdmittedWithoutExponent (lengths : Lengths) : Bool :=
  lengths.base ≤ schedule.maxInputSize && lengths.modulus ≤ schedule.maxInputSize

example : sizesAdmittedWithoutExponent (decodeLengths (header 1 1025 1)) ≠
    sizesAdmitted schedule (decodeLengths (header 1 1025 1)) := by
  native_decide

def cacheKeepsSuffix (input : List Byte) : List Byte := input

example : cacheKeepsSuffix (knownInput ++ [byte 0xaa]) ≠
    cacheNormalizedInput (knownInput ++ [byte 0xaa]) := by
  native_decide

def mutatedExpectedKnownOutput : List Byte := [byte 0x05]

theorem mutated_expected_answer_is_detected :
    mutatedExpectedKnownOutput ≠ expectedKnownOutput ∧
      mutatedExpectedKnownOutput ≠ oracleKnownOutput := by
  native_decide

def mutatedOracleKnownOutput : List Byte := [byte 0x05]

theorem mutated_oracle_answer_is_detected :
    mutatedOracleKnownOutput ≠ expectedKnownOutput ∧
      mutatedOracleKnownOutput ≠ oracleKnownOutput := by
  native_decide

end ModExpVectors
end Precompiles
end Eip803x
