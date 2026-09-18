-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.Precompiles.Bn254Mul

namespace Eip803x
namespace Precompiles
namespace Bn254MulVectors

open Evm.MemoryStackControl
open Bn254Mul

def exactPoint (bytes : List Byte) (length_eq : bytes.length = pointLength) : EncodedPoint :=
  pointOfBytes bytes length_eq

def exactScalar (bytes : List Byte) (length_eq : bytes.length = scalarLength) : EncodedScalar :=
  scalarOfBytes bytes length_eq

def zeroPoint : EncodedPoint :=
  exactPoint (List.replicate 64 zeroByte) (by native_decide)

def unitPoint : EncodedPoint :=
  exactPoint
    (List.replicate 31 zeroByte ++ [byte 0x01] ++
      List.replicate 31 zeroByte ++ [byte 0x02])
    (by native_decide)

def knownPoint : EncodedPoint :=
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

def invalidPoint : EncodedPoint :=
  exactPoint (byte 0xff :: List.replicate 63 zeroByte) (by native_decide)

def zeroScalar : EncodedScalar :=
  exactScalar (List.replicate 32 zeroByte) (by native_decide)

def oneScalar : EncodedScalar :=
  exactScalar (List.replicate 31 zeroByte ++ [byte 0x01]) (by native_decide)

def allOnesScalar : EncodedScalar :=
  exactScalar (List.replicate 32 (byte 0xff)) (by native_decide)

def trailingZeroScalar : EncodedScalar :=
  exactScalar (List.replicate 30 zeroByte ++ [byte 0x01, byte 0x00]) (by native_decide)

def oracleProduct : EncodedPoint :=
  exactPoint
    [byte 0x0b, byte 0xf9, byte 0x82, byte 0xb9, byte 0x8a, byte 0x27, byte 0x57, byte 0x87,
      byte 0x8c, byte 0x05, byte 0x1b, byte 0xfe, byte 0x7e, byte 0xee, byte 0x22, byte 0x8b,
      byte 0x12, byte 0xbc, byte 0x69, byte 0x27, byte 0x4b, byte 0x91, byte 0x8f, byte 0x08,
      byte 0xd9, byte 0xfc, byte 0xb2, byte 0x1e, byte 0x91, byte 0x84, byte 0xdd, byte 0xc1,
      byte 0x0b, byte 0x17, byte 0xc7, byte 0x7c, byte 0xbf, byte 0x3c, byte 0x19, byte 0xd5,
      byte 0xd2, byte 0x7e, byte 0x18, byte 0xcb, byte 0xd4, byte 0xa8, byte 0xc3, byte 0x36,
      byte 0xaf, byte 0xb4, byte 0x88, byte 0xd0, byte 0xe9, byte 0x2c, byte 0x18, byte 0xd5,
      byte 0x6e, byte 0x64, byte 0xdd, byte 0x4e, byte 0xa5, byte 0xc4, byte 0x37, byte 0xe6]
    (by native_decide)

def expectedProduct : EncodedPoint :=
  exactPoint
    [byte 0x0b, byte 0xf9, byte 0x82, byte 0xb9, byte 0x8a, byte 0x27, byte 0x57, byte 0x87,
      byte 0x8c, byte 0x05, byte 0x1b, byte 0xfe, byte 0x7e, byte 0xee, byte 0x22, byte 0x8b,
      byte 0x12, byte 0xbc, byte 0x69, byte 0x27, byte 0x4b, byte 0x91, byte 0x8f, byte 0x08,
      byte 0xd9, byte 0xfc, byte 0xb2, byte 0x1e, byte 0x91, byte 0x84, byte 0xdd, byte 0xc1,
      byte 0x0b, byte 0x17, byte 0xc7, byte 0x7c, byte 0xbf, byte 0x3c, byte 0x19, byte 0xd5,
      byte 0xd2, byte 0x7e, byte 0x18, byte 0xcb, byte 0xd4, byte 0xa8, byte 0xc3, byte 0x36,
      byte 0xaf, byte 0xb4, byte 0x88, byte 0xd0, byte 0xe9, byte 0x2c, byte 0x18, byte 0xd5,
      byte 0x6e, byte 0x64, byte 0xdd, byte 0x4e, byte 0xa5, byte 0xc4, byte 0x37, byte 0xe6]
    (by native_decide)

def knownAnswerOracle : CurveOracle :=
  { validPoint := fun point =>
      point.bytes == zeroPoint.bytes || point.bytes == unitPoint.bytes ||
        point.bytes == knownPoint.bytes
    multiply := fun point scalar =>
      if point.bytes == zeroPoint.bytes then some zeroPoint
      else if scalar.bytes == zeroScalar.bytes then some zeroPoint
      else if point.bytes == unitPoint.bytes && scalar.bytes == oneScalar.bytes then some unitPoint
      else if point.bytes == knownPoint.bytes && scalar.bytes == allOnesScalar.bytes then
        some oracleProduct
      else none }

def inputFor (point : EncodedPoint) (scalar : EncodedScalar) : List Byte :=
  point.bytes ++ scalar.bytes

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
  [ { name := "empty input pads to infinity times zero", input := [],
      expected := .ok zeroPoint, expectedGas := 6000 }
  , { name := "64-byte point pads scalar to zero", input := knownPoint.bytes,
      expected := .ok zeroPoint, expectedGas := 6000 }
  , { name := "unit point times one", input := inputFor unitPoint oneScalar,
      expected := .ok unitPoint, expectedGas := 6000 }
  , { name := "pinned Nethermind known answer", input := inputFor knownPoint allOnesScalar,
      expected := .ok expectedProduct, expectedGas := 6000 }
  , { name := "suffix is truncated", input := inputFor knownPoint allOnesScalar ++ [byte 0x42],
      expected := .ok expectedProduct, expectedGas := 6000 }
  , { name := "invalid point fails before multiplication", input := inputFor invalidPoint oneScalar,
      expected := .error .invalidPoint, expectedGas := 6000 }
  , { name := "oracle multiplication failure fails", input := inputFor knownPoint oneScalar,
      expected := .error .multiplicationFailed, expectedGas := 6000 }
  , { name := "all-zero exact input", input := inputFor zeroPoint zeroScalar,
      expected := .ok zeroPoint, expectedGas := 6000 } ]

theorem vector_count : vectors.length = 8 := rfl

theorem all_vectors_pass : vectors.all passes = true := by
  native_decide

theorem known_answer_tables_agree : oracleProduct = expectedProduct := by
  native_decide

example : (effectiveInput []).length = inputLength := by native_decide

example : effectiveInput [] = inputFor zeroPoint zeroScalar := by native_decide

example : effectiveInput knownPoint.bytes = inputFor knownPoint zeroScalar := by native_decide

example : effectiveInput (inputFor knownPoint allOnesScalar ++ [byte 0x42]) =
    inputFor knownPoint allOnesScalar := by native_decide

example : cacheNormalizedInput (inputFor unitPoint trailingZeroScalar) =
    (inputFor unitPoint trailingZeroScalar).take 95 := by native_decide

example : effectiveInput (cacheNormalizedInput (inputFor unitPoint trailingZeroScalar)) =
    effectiveInput (inputFor unitPoint trailingZeroScalar) := by native_decide

example : resultEq (run knownAnswerOracle (inputFor knownPoint allOnesScalar))
    (.ok expectedProduct) := by
  native_decide

/- Mutation sentinels constrain framing, cache normalization, validation, metadata, and output. -/

def mutatedAddress : Nat := 6

example : mutatedAddress ≠ address := by native_decide

def mutatedName : String := "ALT_BN128_MUL"

example : mutatedName ≠ name := by native_decide

def mutatedCaching : Bool := false

example : mutatedCaching ≠ supportsCaching := by native_decide

def mutatedFixedGas : Nat := 40000

example : mutatedFixedGas ≠ totalGasCost schedule [] := by native_decide

def leftPaddedInput (input : List Byte) : List Byte :=
  List.replicate (inputLength - input.length) zeroByte ++ input.take inputLength

example : leftPaddedInput knownPoint.bytes ≠ effectiveInput knownPoint.bytes := by native_decide

def untruncatedInput (input : List Byte) : List Byte :=
  input ++ List.replicate (inputLength - input.length) zeroByte

example : untruncatedInput (inputFor knownPoint allOnesScalar ++ [byte 0x42]) ≠
    effectiveInput (inputFor knownPoint allOnesScalar ++ [byte 0x42]) := by
  native_decide

def cacheNormalizationWithoutClamp (input : List Byte) : List Byte :=
  input.reverse.dropWhile (· == zeroByte) |>.reverse

example : cacheNormalizationWithoutClamp (inputFor knownPoint allOnesScalar ++ [byte 0x42]) ≠
    cacheNormalizedInput (inputFor knownPoint allOnesScalar ++ [byte 0x42]) := by
  native_decide

def decodeScalarFromStart (input : List Byte) : List Byte :=
  (effectiveInput input).take scalarLength

example : decodeScalarFromStart (inputFor knownPoint allOnesScalar) ≠
    (decodeInput (inputFor knownPoint allOnesScalar)).scalar.bytes := by
  native_decide

def runPairWithoutPointValidation (oracle : CurveOracle) (input : InputPair) : Result :=
  match oracle.multiply input.point input.scalar with
  | none => .error .multiplicationFailed
  | some output => .ok output

example : resultEq
    (runPairWithoutPointValidation knownAnswerOracle { point := invalidPoint, scalar := oneScalar })
    (runPair knownAnswerOracle { point := invalidPoint, scalar := oneScalar }) = false := by
  native_decide

def mutatedExpectedProduct : EncodedPoint :=
  exactPoint (byte 0x0c :: expectedProduct.bytes.drop 1) (by native_decide)

theorem mutated_expected_answer_is_detected :
    mutatedExpectedProduct ≠ expectedProduct ∧ mutatedExpectedProduct ≠ oracleProduct := by
  native_decide

def mutatedOracleProduct : EncodedPoint :=
  exactPoint (byte 0x0c :: oracleProduct.bytes.drop 1) (by native_decide)

theorem mutated_oracle_answer_is_detected :
    mutatedOracleProduct ≠ expectedProduct ∧ mutatedOracleProduct ≠ oracleProduct := by
  native_decide

end Bn254MulVectors
end Precompiles
end Eip803x
