-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.Precompiles.Blake2F

namespace Eip803x
namespace Precompiles
namespace Blake2FVectors

open Evm.MemoryStackControl
open Blake2F

def schedule : Schedule := Schedule.amsterdam

def stateVector : List Byte :=
  [byte 0x48, byte 0xc9, byte 0xbd, byte 0xf2, byte 0x67, byte 0xe6, byte 0x09, byte 0x6a,
    byte 0x3b, byte 0xa7, byte 0xca, byte 0x84, byte 0x85, byte 0xae, byte 0x67, byte 0xbb,
    byte 0x2b, byte 0xf8, byte 0x94, byte 0xfe, byte 0x72, byte 0xf3, byte 0x6e, byte 0x3c,
    byte 0xf1, byte 0x36, byte 0x1d, byte 0x5f, byte 0x3a, byte 0xf5, byte 0x4f, byte 0xa5,
    byte 0xd1, byte 0x82, byte 0xe6, byte 0xad, byte 0x7f, byte 0x52, byte 0x0e, byte 0x51,
    byte 0x1f, byte 0x6c, byte 0x3e, byte 0x2b, byte 0x8c, byte 0x68, byte 0x05, byte 0x9b,
    byte 0x6b, byte 0xbd, byte 0x41, byte 0xfb, byte 0xab, byte 0xd9, byte 0x83, byte 0x1f,
    byte 0x79, byte 0x21, byte 0x7e, byte 0x13, byte 0x19, byte 0xcd, byte 0xe0, byte 0x5b]

def messageVector : List Byte :=
  [byte 0x61, byte 0x62, byte 0x63] ++ List.replicate 125 zeroByte

def counterVector : List Byte :=
  [byte 0x03] ++ List.replicate 15 zeroByte

def inputFor (roundBytes : List Byte) (flag : Byte) : List Byte :=
  roundBytes ++ stateVector ++ messageVector ++ counterVector ++ [flag]

def inputRoundsZeroFinal : List Byte :=
  inputFor [zeroByte, zeroByte, zeroByte, zeroByte] (byte 1)

def inputRoundsTwelveFinal : List Byte :=
  inputFor [zeroByte, zeroByte, zeroByte, byte 12] (byte 1)

def inputRoundsTwelveNotFinal : List Byte :=
  inputFor [zeroByte, zeroByte, zeroByte, byte 12] zeroByte

def inputRoundsMaxFinal : List Byte :=
  inputFor [byte 0xff, byte 0xff, byte 0xff, byte 0xff] (byte 1)

def inputInvalidFlag : List Byte :=
  inputFor [zeroByte, zeroByte, zeroByte, byte 12] (byte 2)

def outputRoundsZero : Output :=
  outputOfBytes
    [byte 0x08, byte 0xc9, byte 0xbc, byte 0xf3, byte 0x67, byte 0xe6, byte 0x09, byte 0x6a,
      byte 0x3b, byte 0xa7, byte 0xca, byte 0x84, byte 0x85, byte 0xae, byte 0x67, byte 0xbb,
      byte 0x2b, byte 0xf8, byte 0x94, byte 0xfe, byte 0x72, byte 0xf3, byte 0x6e, byte 0x3c,
      byte 0xf1, byte 0x36, byte 0x1d, byte 0x5f, byte 0x3a, byte 0xf5, byte 0x4f, byte 0xa5,
      byte 0xd2, byte 0x82, byte 0xe6, byte 0xad, byte 0x7f, byte 0x52, byte 0x0e, byte 0x51,
      byte 0x1f, byte 0x6c, byte 0x3e, byte 0x2b, byte 0x8c, byte 0x68, byte 0x05, byte 0x9b,
      byte 0x94, byte 0x42, byte 0xbe, byte 0x04, byte 0x54, byte 0x26, byte 0x7c, byte 0xe0,
      byte 0x79, byte 0x21, byte 0x7e, byte 0x13, byte 0x19, byte 0xcd, byte 0xe0, byte 0x5b]
    (by native_decide)

def outputRoundsTwelveFinal : Output :=
  outputOfBytes
    [byte 0xba, byte 0x80, byte 0xa5, byte 0x3f, byte 0x98, byte 0x1c, byte 0x4d, byte 0x0d,
      byte 0x6a, byte 0x27, byte 0x97, byte 0xb6, byte 0x9f, byte 0x12, byte 0xf6, byte 0xe9,
      byte 0x4c, byte 0x21, byte 0x2f, byte 0x14, byte 0x68, byte 0x5a, byte 0xc4, byte 0xb7,
      byte 0x4b, byte 0x12, byte 0xbb, byte 0x6f, byte 0xdb, byte 0xff, byte 0xa2, byte 0xd1,
      byte 0x7d, byte 0x87, byte 0xc5, byte 0x39, byte 0x2a, byte 0xab, byte 0x79, byte 0x2d,
      byte 0xc2, byte 0x52, byte 0xd5, byte 0xde, byte 0x45, byte 0x33, byte 0xcc, byte 0x95,
      byte 0x18, byte 0xd3, byte 0x8a, byte 0xa8, byte 0xdb, byte 0xf1, byte 0x92, byte 0x5a,
      byte 0xb9, byte 0x23, byte 0x86, byte 0xed, byte 0xd4, byte 0x00, byte 0x99, byte 0x23]
    (by native_decide)

def outputRoundsTwelveNotFinal : Output :=
  outputOfBytes
    [byte 0x75, byte 0xab, byte 0x69, byte 0xd3, byte 0x19, byte 0x0a, byte 0x56, byte 0x2c,
      byte 0x51, byte 0xae, byte 0xf8, byte 0xd8, byte 0x8f, byte 0x1c, byte 0x27, byte 0x75,
      byte 0x87, byte 0x69, byte 0x44, byte 0x40, byte 0x72, byte 0x70, byte 0xc4, byte 0x2c,
      byte 0x98, byte 0x44, byte 0x25, byte 0x2c, byte 0x26, byte 0xd2, byte 0x87, byte 0x52,
      byte 0x98, byte 0x74, byte 0x3e, byte 0x7f, byte 0x6d, byte 0x5e, byte 0xa2, byte 0xf2,
      byte 0xd3, byte 0xe8, byte 0xd2, byte 0x26, byte 0x03, byte 0x9c, byte 0xd3, byte 0x1b,
      byte 0x4e, byte 0x42, byte 0x6a, byte 0xc4, byte 0xf2, byte 0xd3, byte 0xd6, byte 0x66,
      byte 0xa6, byte 0x10, byte 0xc2, byte 0x11, byte 0x6f, byte 0xde, byte 0x47, byte 0x35]
    (by native_decide)

/- The expected fixtures above and oracle fixtures below are deliberately
   separate literals. Mutating either table must break the vector relation. -/
def oracleOutputRoundsZero : Output :=
  outputOfBytes
    [byte 0x08, byte 0xc9, byte 0xbc, byte 0xf3, byte 0x67, byte 0xe6, byte 0x09, byte 0x6a,
      byte 0x3b, byte 0xa7, byte 0xca, byte 0x84, byte 0x85, byte 0xae, byte 0x67, byte 0xbb,
      byte 0x2b, byte 0xf8, byte 0x94, byte 0xfe, byte 0x72, byte 0xf3, byte 0x6e, byte 0x3c,
      byte 0xf1, byte 0x36, byte 0x1d, byte 0x5f, byte 0x3a, byte 0xf5, byte 0x4f, byte 0xa5,
      byte 0xd2, byte 0x82, byte 0xe6, byte 0xad, byte 0x7f, byte 0x52, byte 0x0e, byte 0x51,
      byte 0x1f, byte 0x6c, byte 0x3e, byte 0x2b, byte 0x8c, byte 0x68, byte 0x05, byte 0x9b,
      byte 0x94, byte 0x42, byte 0xbe, byte 0x04, byte 0x54, byte 0x26, byte 0x7c, byte 0xe0,
      byte 0x79, byte 0x21, byte 0x7e, byte 0x13, byte 0x19, byte 0xcd, byte 0xe0, byte 0x5b]
    (by native_decide)

def oracleOutputRoundsTwelveFinal : Output :=
  outputOfBytes
    [byte 0xba, byte 0x80, byte 0xa5, byte 0x3f, byte 0x98, byte 0x1c, byte 0x4d, byte 0x0d,
      byte 0x6a, byte 0x27, byte 0x97, byte 0xb6, byte 0x9f, byte 0x12, byte 0xf6, byte 0xe9,
      byte 0x4c, byte 0x21, byte 0x2f, byte 0x14, byte 0x68, byte 0x5a, byte 0xc4, byte 0xb7,
      byte 0x4b, byte 0x12, byte 0xbb, byte 0x6f, byte 0xdb, byte 0xff, byte 0xa2, byte 0xd1,
      byte 0x7d, byte 0x87, byte 0xc5, byte 0x39, byte 0x2a, byte 0xab, byte 0x79, byte 0x2d,
      byte 0xc2, byte 0x52, byte 0xd5, byte 0xde, byte 0x45, byte 0x33, byte 0xcc, byte 0x95,
      byte 0x18, byte 0xd3, byte 0x8a, byte 0xa8, byte 0xdb, byte 0xf1, byte 0x92, byte 0x5a,
      byte 0xb9, byte 0x23, byte 0x86, byte 0xed, byte 0xd4, byte 0x00, byte 0x99, byte 0x23]
    (by native_decide)

def oracleOutputRoundsTwelveNotFinal : Output :=
  outputOfBytes
    [byte 0x75, byte 0xab, byte 0x69, byte 0xd3, byte 0x19, byte 0x0a, byte 0x56, byte 0x2c,
      byte 0x51, byte 0xae, byte 0xf8, byte 0xd8, byte 0x8f, byte 0x1c, byte 0x27, byte 0x75,
      byte 0x87, byte 0x69, byte 0x44, byte 0x40, byte 0x72, byte 0x70, byte 0xc4, byte 0x2c,
      byte 0x98, byte 0x44, byte 0x25, byte 0x2c, byte 0x26, byte 0xd2, byte 0x87, byte 0x52,
      byte 0x98, byte 0x74, byte 0x3e, byte 0x7f, byte 0x6d, byte 0x5e, byte 0xa2, byte 0xf2,
      byte 0xd3, byte 0xe8, byte 0xd2, byte 0x26, byte 0x03, byte 0x9c, byte 0xd3, byte 0x1b,
      byte 0x4e, byte 0x42, byte 0x6a, byte 0xc4, byte 0xf2, byte 0xd3, byte 0xd6, byte 0x66,
      byte 0xa6, byte 0x10, byte 0xc2, byte 0x11, byte 0x6f, byte 0xde, byte 0x47, byte 0x35]
    (by native_decide)

def zeroOutput : Output :=
  outputOfBytes (List.replicate 64 zeroByte) (by native_decide)

def eipOracle : CompressionOracle :=
  { compress := fun input =>
      if input = inputRoundsZeroFinal then oracleOutputRoundsZero
      else if input = inputRoundsTwelveFinal then oracleOutputRoundsTwelveFinal
      else if input = inputRoundsTwelveNotFinal then oracleOutputRoundsTwelveNotFinal
      else zeroOutput }

structure Vector where
  input : List Byte
  expectedGas : Nat
  expected : Result

def vectors : List Vector :=
  [ { input := [], expectedGas := 0, expected := .error .invalidInputLength }
  , { input := inputRoundsTwelveFinal.take 212, expectedGas := 0,
      expected := .error .invalidInputLength }
  , { input := inputRoundsTwelveFinal ++ [zeroByte], expectedGas := 0,
      expected := .error .invalidInputLength }
  , { input := inputInvalidFlag, expectedGas := 0,
      expected := .error .invalidFinalBlockFlag }
  , { input := inputRoundsZeroFinal, expectedGas := 0,
      expected := .ok outputRoundsZero }
  , { input := inputRoundsTwelveFinal, expectedGas := 12,
      expected := .ok outputRoundsTwelveFinal }
  , { input := inputRoundsTwelveNotFinal, expectedGas := 12,
      expected := .ok outputRoundsTwelveNotFinal }
  , { input := inputRoundsMaxFinal, expectedGas := 4_294_967_295,
      expected := .ok zeroOutput } ]

def resultEq : Result -> Result -> Bool
  | .error left, .error right => left == right
  | .ok left, .ok right => left.bytes == right.bytes
  | _, _ => false

def passes (vector : Vector) : Bool :=
  totalGasCost schedule vector.input == vector.expectedGas &&
    resultEq (run eipOracle vector.input) vector.expected

theorem vector_count : vectors.length = 8 := rfl

theorem all_vectors_pass : vectors.all passes = true := by
  native_decide

theorem eip_known_answers_are_distinct :
    outputRoundsZero.bytes ≠ outputRoundsTwelveFinal.bytes ∧
      outputRoundsTwelveFinal.bytes ≠ outputRoundsTwelveNotFinal.bytes := by
  native_decide

example : inputRoundsTwelveFinal.length = requiredInputLength := by native_decide

example : rounds inputRoundsZeroFinal = 0 := by native_decide

example : rounds inputRoundsTwelveFinal = 12 := by native_decide

example : rounds inputRoundsMaxFinal = 4_294_967_295 := by native_decide

example : validFinalFlag (finalFlag inputRoundsTwelveFinal) = true := by native_decide

example : validFinalFlag (finalFlag inputRoundsTwelveNotFinal) = true := by native_decide

example : validFinalFlag (finalFlag inputInvalidFlag) = false := by native_decide

example : normalizeInput (inputRoundsTwelveFinal.take 212) = [] := by native_decide

example : normalizeInput inputInvalidFlag = invalidFlagInput := by native_decide

example : run eipOracle (normalizeInput inputInvalidFlag) = run eipOracle inputInvalidFlag := by
  exact run_normalized eipOracle inputInvalidFlag

/-- Mutation sentinel: little-endian round decoding disagrees at 12 rounds. -/
def mutatedLittleEndianRounds (input : List Byte) : Nat :=
  (input.take 4).reverse.foldl
    (fun accumulator value => accumulator * 256 + byteValue value) 0

example : mutatedLittleEndianRounds inputRoundsTwelveFinal ≠ rounds inputRoundsTwelveFinal := by
  native_decide

/-- Mutation sentinel: accepting flag 2 changes the invalid-flag contract. -/
def mutatedValidFinalFlag (value : Byte) : Bool :=
  value == byte 0 || value == byte 1 || value == byte 2

example : mutatedValidFinalFlag (finalFlag inputInvalidFlag) ≠
    validFinalFlag (finalFlag inputInvalidFlag) := by
  native_decide

/-- Mutation sentinel: pricing malformed input by its prefix violates zero cost. -/
def mutatedMalformedDataGas (input : List Byte) : Nat := rounds input

example : mutatedMalformedDataGas (inputRoundsTwelveFinal.take 212) ≠
    dataGasCost schedule (inputRoundsTwelveFinal.take 212) := by
  native_decide

/-- Mutation sentinel: a one-byte-short length is not a valid encoding. -/
def mutatedRequiredInputLength : Nat := 212

example : mutatedRequiredInputLength ≠ requiredInputLength := by native_decide

/-- Mutation sentinel: the precompile address is exact. -/
def mutatedAddress : Nat := 8

example : mutatedAddress ≠ address := by native_decide

/-- Mutation sentinel: the registered name is exact. -/
def mutatedName : String := "BLAKE2"

example : mutatedName ≠ name := by native_decide

/-- Mutation sentinel: the inherited cache flag is enabled. -/
def mutatedCaching : Bool := false

example : mutatedCaching ≠ supportsCaching := by native_decide

/-- Mutation sentinel: truncating success output violates the 64-byte contract. -/
def mutatedOutput : List Byte := outputRoundsTwelveFinal.bytes.take 63

example : mutatedOutput.length ≠ outputLength := by native_decide

/-- Mutation sentinel: changing one known-answer byte disagrees with both
    independent fixture tables. -/
def mutatedKnownAnswer : Output :=
  outputOfBytes (byte 0xbb :: outputRoundsTwelveFinal.bytes.drop 1) (by native_decide)

theorem mutated_known_answer_is_detected :
    mutatedKnownAnswer.bytes ≠ outputRoundsTwelveFinal.bytes ∧
      mutatedKnownAnswer.bytes ≠ oracleOutputRoundsTwelveFinal.bytes := by
  native_decide

end Blake2FVectors
end Precompiles
end Eip803x
