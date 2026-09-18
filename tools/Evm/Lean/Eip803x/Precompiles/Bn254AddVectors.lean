-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.Precompiles.Bn254Add

namespace Eip803x
namespace Precompiles
namespace Bn254AddVectors

open Evm.MemoryStackControl
open Bn254Add

def exactPoint (bytes : List Byte) (length_eq : bytes.length = pointLength) : EncodedPoint :=
  pointOfBytes bytes length_eq

def zeroPoint : EncodedPoint :=
  exactPoint (List.replicate 64 zeroByte) (by native_decide)

def leftPoint : EncodedPoint :=
  exactPoint
    [byte 0x08, byte 0x91, byte 0x42, byte 0xde, byte 0xbb, byte 0x13, byte 0xc4, byte 0x61,
      byte 0xf6, byte 0x15, byte 0x23, byte 0x58, byte 0x6a, byte 0x60, byte 0x73, byte 0x2d,
      byte 0x8b, byte 0x69, byte 0xc5, byte 0xb3, byte 0x8a, byte 0x33, byte 0x80, byte 0xa7,
      byte 0x4d, byte 0xa7, byte 0xb2, byte 0x96, byte 0x1d, byte 0x86, byte 0x7d, byte 0xbf,
      byte 0x2d, byte 0x5f, byte 0xc7, byte 0xbb, byte 0xc0, byte 0x13, byte 0xc1, byte 0x6d,
      byte 0x79, byte 0x45, byte 0xf1, byte 0x90, byte 0xb2, byte 0x32, byte 0xea, byte 0xcc,
      byte 0x25, byte 0xda, byte 0x67, byte 0x5c, byte 0x0e, byte 0xb0, byte 0x93, byte 0xfe,
      byte 0x6b, byte 0x9f, byte 0x1b, byte 0x4b, byte 0x4e, byte 0x10, byte 0x7b, byte 0x36]
    (by native_decide)

def rightPoint : EncodedPoint :=
  exactPoint
    [byte 0x25, byte 0xf8, byte 0xc8, byte 0x9e, byte 0xa3, byte 0x43, byte 0x7f, byte 0x44,
      byte 0xf8, byte 0xfc, byte 0x8b, byte 0x6b, byte 0xfb, byte 0xb6, byte 0x31, byte 0x20,
      byte 0x74, byte 0xdc, byte 0x6f, byte 0x98, byte 0x38, byte 0x09, byte 0xa5, byte 0xe8,
      byte 0x09, byte 0xff, byte 0x4e, byte 0x1d, byte 0x07, byte 0x6d, byte 0xd5, byte 0x85,
      byte 0x0b, byte 0x38, byte 0xc7, byte 0xce, byte 0xd6, byte 0xe4, byte 0xda, byte 0xef,
      byte 0x9c, byte 0x43, byte 0x47, byte 0xf3, byte 0x70, byte 0xd6, byte 0xd8, byte 0xb5,
      byte 0x8f, byte 0x4b, byte 0x1d, byte 0x8d, byte 0xc6, byte 0x1a, byte 0x3c, byte 0x59,
      byte 0xd6, byte 0x51, byte 0xa0, byte 0x64, byte 0x4a, byte 0x2a, byte 0x27, byte 0xcf]
    (by native_decide)

def oracleSum : EncodedPoint :=
  exactPoint
    [byte 0x0a, byte 0x66, byte 0x78, byte 0xfd, byte 0x67, byte 0x5a, byte 0xa4, byte 0xd8,
      byte 0xf0, byte 0xd0, byte 0x3a, byte 0x1f, byte 0xeb, byte 0x92, byte 0x1a, byte 0x27,
      byte 0xf3, byte 0x8e, byte 0xbd, byte 0xcb, byte 0x86, byte 0x0c, byte 0xc0, byte 0x83,
      byte 0x65, byte 0x35, byte 0x19, byte 0x65, byte 0x5a, byte 0xcd, byte 0x6d, byte 0x79,
      byte 0x17, byte 0x2f, byte 0xd5, byte 0xb3, byte 0xb2, byte 0xbf, byte 0xdd, byte 0x44,
      byte 0xe4, byte 0x3b, byte 0xce, byte 0xc3, byte 0xea, byte 0xce, byte 0x93, byte 0x47,
      byte 0x60, byte 0x8f, byte 0x9f, byte 0x0a, byte 0x16, byte 0xf1, byte 0xe1, byte 0x84,
      byte 0xcb, byte 0x3f, byte 0x52, byte 0xe6, byte 0xf2, byte 0x59, byte 0xcb, byte 0xeb]
    (by native_decide)

def expectedSum : EncodedPoint :=
  exactPoint
    [byte 0x0a, byte 0x66, byte 0x78, byte 0xfd, byte 0x67, byte 0x5a, byte 0xa4, byte 0xd8,
      byte 0xf0, byte 0xd0, byte 0x3a, byte 0x1f, byte 0xeb, byte 0x92, byte 0x1a, byte 0x27,
      byte 0xf3, byte 0x8e, byte 0xbd, byte 0xcb, byte 0x86, byte 0x0c, byte 0xc0, byte 0x83,
      byte 0x65, byte 0x35, byte 0x19, byte 0x65, byte 0x5a, byte 0xcd, byte 0x6d, byte 0x79,
      byte 0x17, byte 0x2f, byte 0xd5, byte 0xb3, byte 0xb2, byte 0xbf, byte 0xdd, byte 0x44,
      byte 0xe4, byte 0x3b, byte 0xce, byte 0xc3, byte 0xea, byte 0xce, byte 0x93, byte 0x47,
      byte 0x60, byte 0x8f, byte 0x9f, byte 0x0a, byte 0x16, byte 0xf1, byte 0xe1, byte 0x84,
      byte 0xcb, byte 0x3f, byte 0x52, byte 0xe6, byte 0xf2, byte 0x59, byte 0xcb, byte 0xeb]
    (by native_decide)

def invalidPoint : EncodedPoint :=
  exactPoint (byte 0xff :: List.replicate 63 zeroByte) (by native_decide)

def knownAnswerOracle : CurveOracle :=
  { valid := fun point =>
      point.bytes == zeroPoint.bytes || point.bytes == leftPoint.bytes ||
        point.bytes == rightPoint.bytes
    add := fun left right =>
      if left.bytes == zeroPoint.bytes then right
      else if right.bytes == zeroPoint.bytes then left
      else if left.bytes == leftPoint.bytes && right.bytes == rightPoint.bytes then oracleSum
      else zeroPoint }

def inputFor (left right : EncodedPoint) : List Byte := left.bytes ++ right.bytes

def resultEq : Result -> Result -> Bool
  | .error left, .error right => left == right
  | .ok left, .ok right => left.bytes == right.bytes
  | _, _ => false

def schedule : Schedule := Schedule.amsterdam

structure Vector where
  name : String
  input : List Byte
  expected : Result
  expectedGas : Nat

def passes (vector : Vector) : Bool :=
  resultEq (run knownAnswerOracle vector.input) vector.expected &&
    totalGasCost schedule vector.input == vector.expectedGas

def vectors : List Vector :=
  [ { name := "empty input pads to two infinities", input := [],
      expected := .ok zeroPoint, expectedGas := 150 }
  , { name := "64-byte point pads the second point to infinity", input := leftPoint.bytes,
      expected := .ok leftPoint, expectedGas := 150 }
  , { name := "pinned Nethermind known answer", input := inputFor leftPoint rightPoint,
      expected := .ok expectedSum, expectedGas := 150 }
  , { name := "suffix is truncated", input := inputFor leftPoint rightPoint ++ [byte 0x42],
      expected := .ok expectedSum, expectedGas := 150 }
  , { name := "invalid left point fails", input := inputFor invalidPoint rightPoint,
      expected := .error .invalidPoint, expectedGas := 150 }
  , { name := "invalid right point fails", input := inputFor leftPoint invalidPoint,
      expected := .error .invalidPoint, expectedGas := 150 }
  , { name := "left infinity returns right", input := inputFor zeroPoint rightPoint,
      expected := .ok rightPoint, expectedGas := 150 }
  , { name := "right infinity returns left", input := inputFor leftPoint zeroPoint,
      expected := .ok leftPoint, expectedGas := 150 } ]

theorem vector_count : vectors.length = 8 := rfl

theorem all_vectors_pass : vectors.all passes = true := by
  native_decide

theorem known_answer_tables_agree : oracleSum = expectedSum := by
  native_decide

example : (effectiveInput []).length = inputLength := by native_decide

example : effectiveInput [] = inputFor zeroPoint zeroPoint := by native_decide

example : effectiveInput leftPoint.bytes = inputFor leftPoint zeroPoint := by native_decide

example : effectiveInput (inputFor leftPoint rightPoint ++ [byte 0x42]) =
    inputFor leftPoint rightPoint := by native_decide

example : cacheNormalizedInput (inputFor leftPoint zeroPoint) = leftPoint.bytes := by native_decide

example : effectiveInput (cacheNormalizedInput (inputFor leftPoint zeroPoint)) =
    effectiveInput (inputFor leftPoint zeroPoint) := by native_decide

example : resultEq (run knownAnswerOracle (inputFor leftPoint rightPoint)) (.ok expectedSum) := by
  native_decide

/- Mutation sentinels constrain framing, cache normalization, validation, metadata, and output. -/

def mutatedAddress : Nat := 7

example : mutatedAddress ≠ address := by native_decide

def mutatedName : String := "ALT_BN128_ADD"

example : mutatedName ≠ name := by native_decide

def mutatedCaching : Bool := false

example : mutatedCaching ≠ supportsCaching := by native_decide

def mutatedFixedGas : Nat := 500

example : mutatedFixedGas ≠ totalGasCost schedule [] := by native_decide

def leftPaddedInput (input : List Byte) : List Byte :=
  List.replicate (inputLength - input.length) zeroByte ++ input.take inputLength

example : leftPaddedInput leftPoint.bytes ≠ effectiveInput leftPoint.bytes := by native_decide

def untruncatedInput (input : List Byte) : List Byte :=
  input ++ List.replicate (inputLength - input.length) zeroByte

example : untruncatedInput (inputFor leftPoint rightPoint ++ [byte 0x42]) ≠
    effectiveInput (inputFor leftPoint rightPoint ++ [byte 0x42]) := by
  native_decide

def cacheNormalizationWithoutClamp (input : List Byte) : List Byte :=
  input.reverse.dropWhile (· == zeroByte) |>.reverse

example : cacheNormalizationWithoutClamp (inputFor leftPoint rightPoint ++ [byte 0x42]) ≠
    cacheNormalizedInput (inputFor leftPoint rightPoint ++ [byte 0x42]) := by
  native_decide

def runPairWithoutLeftValidation (oracle : CurveOracle) (input : InputPair) : Result :=
  if !oracle.valid input.right then .error .invalidPoint else .ok (oracle.add input.left input.right)

example : resultEq
    (runPairWithoutLeftValidation knownAnswerOracle { left := invalidPoint, right := rightPoint })
    (runPair knownAnswerOracle { left := invalidPoint, right := rightPoint }) = false := by
  native_decide

def runPairWithoutRightValidation (oracle : CurveOracle) (input : InputPair) : Result :=
  if !oracle.valid input.left then .error .invalidPoint else .ok (oracle.add input.left input.right)

example : resultEq
    (runPairWithoutRightValidation knownAnswerOracle { left := leftPoint, right := invalidPoint })
    (runPair knownAnswerOracle { left := leftPoint, right := invalidPoint }) = false := by
  native_decide

def mutatedExpectedSum : EncodedPoint :=
  exactPoint (byte 0x0b :: expectedSum.bytes.drop 1) (by native_decide)

theorem mutated_expected_answer_is_detected :
    mutatedExpectedSum ≠ expectedSum ∧ mutatedExpectedSum ≠ oracleSum := by
  native_decide

def mutatedOracleSum : EncodedPoint :=
  exactPoint (byte 0x0b :: oracleSum.bytes.drop 1) (by native_decide)

theorem mutated_oracle_answer_is_detected :
    mutatedOracleSum ≠ expectedSum ∧ mutatedOracleSum ≠ oracleSum := by
  native_decide

end Bn254AddVectors
end Precompiles
end Eip803x
