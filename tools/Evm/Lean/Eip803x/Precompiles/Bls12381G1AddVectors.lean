-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.Precompiles.Bls12381G1Add

namespace Eip803x
namespace Precompiles
namespace Bls12381G1AddVectors

open Evm.MemoryStackControl
open Bls12381G1Add

def schedule : Schedule := Schedule.amsterdam

def zeroPoint : EncodedPoint :=
  pointOfBytes (List.replicate 128 zeroByte) (by native_decide)

def pointZeroTwo : EncodedPoint :=
  pointOfBytes
    (List.replicate 64 zeroByte ++ List.replicate 63 zeroByte ++ [byte 0x02])
    (by native_decide)

def oracleDoublePoint : EncodedPoint :=
  pointOfBytes
    (List.replicate 64 zeroByte ++ List.replicate 16 zeroByte ++
      [byte 0x1a, byte 0x01, byte 0x11, byte 0xea, byte 0x39, byte 0x7f, byte 0xe6, byte 0x9a,
        byte 0x4b, byte 0x1b, byte 0xa7, byte 0xb6, byte 0x43, byte 0x4b, byte 0xac, byte 0xd7,
        byte 0x64, byte 0x77, byte 0x4b, byte 0x84, byte 0xf3, byte 0x85, byte 0x12, byte 0xbf,
        byte 0x67, byte 0x30, byte 0xd2, byte 0xa0, byte 0xf6, byte 0xb0, byte 0xf6, byte 0x24,
        byte 0x1e, byte 0xab, byte 0xff, byte 0xfe, byte 0xb1, byte 0x53, byte 0xff, byte 0xff,
        byte 0xb9, byte 0xfe, byte 0xff, byte 0xff, byte 0xff, byte 0xff, byte 0xaa, byte 0xa9])
    (by native_decide)

def expectedDoublePoint : EncodedPoint :=
  pointOfBytes
    (List.replicate 64 zeroByte ++ List.replicate 16 zeroByte ++
      [byte 0x1a, byte 0x01, byte 0x11, byte 0xea, byte 0x39, byte 0x7f, byte 0xe6, byte 0x9a,
        byte 0x4b, byte 0x1b, byte 0xa7, byte 0xb6, byte 0x43, byte 0x4b, byte 0xac, byte 0xd7,
        byte 0x64, byte 0x77, byte 0x4b, byte 0x84, byte 0xf3, byte 0x85, byte 0x12, byte 0xbf,
        byte 0x67, byte 0x30, byte 0xd2, byte 0xa0, byte 0xf6, byte 0xb0, byte 0xf6, byte 0x24,
        byte 0x1e, byte 0xab, byte 0xff, byte 0xfe, byte 0xb1, byte 0x53, byte 0xff, byte 0xff,
        byte 0xb9, byte 0xfe, byte 0xff, byte 0xff, byte 0xff, byte 0xff, byte 0xaa, byte 0xa9])
    (by native_decide)

def invalidPoint : EncodedPoint :=
  pointOfBytes (byte 0x01 :: List.replicate 127 zeroByte) (by native_decide)

def knownAnswerOracle : CurveOracle :=
  { valid := fun point => point.bytes == zeroPoint.bytes || point.bytes == pointZeroTwo.bytes
    isInfinity := fun point => point.bytes == zeroPoint.bytes
    add := fun left right =>
      if left.bytes == pointZeroTwo.bytes && right.bytes == pointZeroTwo.bytes then
        oracleDoublePoint
      else
        zeroPoint }

def inputFor (left right : EncodedPoint) : List Byte := left.bytes ++ right.bytes

def resultEq : Result -> Result -> Bool
  | .error left, .error right => left == right
  | .ok left, .ok right => left.bytes == right.bytes
  | _, _ => false

structure Vector where
  name : String
  input : List Byte
  expected : Result
  expectedGas : Nat

def passes (vector : Vector) : Bool :=
  resultEq (run knownAnswerOracle vector.input) vector.expected &&
    totalGasCost schedule vector.input == vector.expectedGas

def vectors : List Vector :=
  [ { name := "empty input is rejected", input := [],
      expected := .error .invalidInputLength, expectedGas := 375 }
  , { name := "255-byte input is rejected", input := List.replicate 255 zeroByte,
      expected := .error .invalidInputLength, expectedGas := 375 }
  , { name := "257-byte input is rejected", input := List.replicate 257 zeroByte,
      expected := .error .invalidInputLength, expectedGas := 375 }
  , { name := "infinity plus infinity is infinity", input := inputFor zeroPoint zeroPoint,
      expected := .ok zeroPoint, expectedGas := 375 }
  , { name := "left infinity returns the right encoding", input := inputFor zeroPoint pointZeroTwo,
      expected := .ok pointZeroTwo, expectedGas := 375 }
  , { name := "right infinity returns the left encoding", input := inputFor pointZeroTwo zeroPoint,
      expected := .ok pointZeroTwo, expectedGas := 375 }
  , { name := "zero-two doubled matches the known answer", input := inputFor pointZeroTwo pointZeroTwo,
      expected := .ok expectedDoublePoint, expectedGas := 375 }
  , { name := "invalid left point is rejected", input := inputFor invalidPoint pointZeroTwo,
      expected := .error .invalidPoint, expectedGas := 375 }
  , { name := "invalid right point is rejected", input := inputFor pointZeroTwo invalidPoint,
      expected := .error .invalidPoint, expectedGas := 375 } ]

theorem vector_count : vectors.length = 9 := rfl

theorem all_vectors_pass : vectors.all passes = true := by
  native_decide

theorem known_answer_tables_agree : oracleDoublePoint = expectedDoublePoint := by
  native_decide

example : (inputFor zeroPoint zeroPoint).length = inputLength := by native_decide

example : pointZeroTwo.bytes.take 64 = List.replicate 64 zeroByte := by native_decide

example : pointZeroTwo.bytes.drop 64 = List.replicate 63 zeroByte ++ [byte 0x02] := by
  native_decide

example : resultEq (run knownAnswerOracle (inputFor zeroPoint pointZeroTwo)) (.ok pointZeroTwo) = true := by
  native_decide

example : resultEq (run knownAnswerOracle (inputFor pointZeroTwo zeroPoint)) (.ok pointZeroTwo) = true := by
  native_decide

example : resultEq (run knownAnswerOracle (inputFor pointZeroTwo pointZeroTwo))
    (.ok expectedDoublePoint) = true := by
  native_decide

def mutatedAddress : Nat := 12

example : mutatedAddress ≠ address := by native_decide

def mutatedName : String := "BLS_G1ADD"

example : mutatedName ≠ name := by native_decide

def mutatedCaching : Bool := false

example : mutatedCaching ≠ supportsCaching := by native_decide

def mutatedFixedGas : Nat := 376

example : mutatedFixedGas ≠ totalGasCost schedule (inputFor zeroPoint zeroPoint) := by native_decide

def mutatedInputLength : Nat := 255

example : mutatedInputLength ≠ inputLength := by native_decide

def runPairWithoutLeftValidation (oracle : CurveOracle) (input : InputPair) : Result :=
  if !oracle.valid input.right then .error .invalidPoint
  else if oracle.isInfinity input.left then .ok input.right
  else if oracle.isInfinity input.right then .ok input.left
  else .ok (oracle.add input.left input.right)

example : resultEq
    (runPairWithoutLeftValidation knownAnswerOracle { left := invalidPoint, right := pointZeroTwo })
    (runPair knownAnswerOracle { left := invalidPoint, right := pointZeroTwo }) = false := by
  native_decide

def runPairWithoutRightValidation (oracle : CurveOracle) (input : InputPair) : Result :=
  if !oracle.valid input.left then .error .invalidPoint
  else if oracle.isInfinity input.left then .ok input.right
  else if oracle.isInfinity input.right then .ok input.left
  else .ok (oracle.add input.left input.right)

example : resultEq
    (runPairWithoutRightValidation knownAnswerOracle { left := pointZeroTwo, right := invalidPoint })
    (runPair knownAnswerOracle { left := pointZeroTwo, right := invalidPoint }) = false := by
  native_decide

def runPairWithWrongLeftInfinityResult (oracle : CurveOracle) (input : InputPair) : Result :=
  if !oracle.valid input.left || !oracle.valid input.right then .error .invalidPoint
  else if oracle.isInfinity input.left then .ok input.left
  else runPair oracle input

example : resultEq
    (runPairWithWrongLeftInfinityResult knownAnswerOracle { left := zeroPoint, right := pointZeroTwo })
    (runPair knownAnswerOracle { left := zeroPoint, right := pointZeroTwo }) = false := by
  native_decide

def runPairWithWrongRightInfinityResult (oracle : CurveOracle) (input : InputPair) : Result :=
  if !oracle.valid input.left || !oracle.valid input.right then .error .invalidPoint
  else if oracle.isInfinity input.left then .ok input.right
  else if oracle.isInfinity input.right then .ok input.right
  else .ok (oracle.add input.left input.right)

example : resultEq
    (runPairWithWrongRightInfinityResult knownAnswerOracle { left := pointZeroTwo, right := zeroPoint })
    (runPair knownAnswerOracle { left := pointZeroTwo, right := zeroPoint }) = false := by
  native_decide

def runPairWithoutAddition (oracle : CurveOracle) (input : InputPair) : Result :=
  if !oracle.valid input.left || !oracle.valid input.right then .error .invalidPoint
  else if oracle.isInfinity input.left then .ok input.right
  else .ok input.left

example : resultEq
    (runPairWithoutAddition knownAnswerOracle { left := pointZeroTwo, right := pointZeroTwo })
    (runPair knownAnswerOracle { left := pointZeroTwo, right := pointZeroTwo }) = false := by
  native_decide

def mutatedExpectedDoublePoint : EncodedPoint :=
  pointOfBytes (byte 0x01 :: expectedDoublePoint.bytes.drop 1) (by native_decide)

theorem mutated_known_answer_is_detected :
    mutatedExpectedDoublePoint ≠ expectedDoublePoint ∧
      mutatedExpectedDoublePoint ≠ oracleDoublePoint := by
  native_decide

def mutatedOracleDoublePoint : EncodedPoint :=
  pointOfBytes (byte 0x01 :: oracleDoublePoint.bytes.drop 1) (by native_decide)

theorem mutated_oracle_answer_is_detected :
    mutatedOracleDoublePoint ≠ expectedDoublePoint ∧
      mutatedOracleDoublePoint ≠ oracleDoublePoint := by
  native_decide

end Bls12381G1AddVectors
end Precompiles
end Eip803x
