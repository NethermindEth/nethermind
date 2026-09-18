-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.Precompiles.Bls12381G2Add

namespace Eip803x
namespace Precompiles
namespace Bls12381G2AddVectors

open Evm.MemoryStackControl
open Bls12381G2Add

/-
  The fixtures use a small fail-closed hexadecimal parser. Invalid or odd
  hexadecimal text yields no bytes; exact-point construction then requires a
  proved
  256-byte result, so malformed text cannot silently become a zero point.
  The oracle and expected-output tables are intentionally separate.
-/

def hexNibble : Char -> Option Nat
  | '0' => some 0
  | '1' => some 1
  | '2' => some 2
  | '3' => some 3
  | '4' => some 4
  | '5' => some 5
  | '6' => some 6
  | '7' => some 7
  | '8' => some 8
  | '9' => some 9
  | 'a' => some 10
  | 'b' => some 11
  | 'c' => some 12
  | 'd' => some 13
  | 'e' => some 14
  | 'f' => some 15
  | _ => none

def parseHexAux : List Char -> Option (List Byte)
  | [] => some []
  | high :: low :: tail =>
      match hexNibble high, hexNibble low, parseHexAux tail with
      | some highValue, some lowValue, some bytes =>
          some (byte (16 * highValue + lowValue) :: bytes)
      | _, _, _ => none
  | [_] => none

def parseHex (text : String) : Option (List Byte) :=
  parseHexAux text.toList

def parsedBytes (text : String) : List Byte :=
  match parseHex text with
  | some bytes => bytes
  | none => []

def generatorHex : String := "00000000000000000000000000000000024aa2b2f08f0a91260805272dc51051c6e47ad4fa403b02b4510b647ae3d1770bac0326a805bbefd48056c8c121bdb80000000000000000000000000000000013e02b6052719f607dacd3a088274f65596bd0d09920b61ab5da61bbdc7f5049334cf11213945d57e5ac7d055d042b7e000000000000000000000000000000000ce5d527727d6e118cc9cdc6da2e351aadfd9baa8cbdd3a76d429a695160d12c923ac9cc3baca289e193548608b82801000000000000000000000000000000000606c4a02ea734cc32acd2b02bc28b99cb3e287e85a763af267492ab572e99ab3f370d275cec1da1aaa9075ff05f79be"

def generatorG2 : EncodedPoint :=
  pointOfBytes (parsedBytes generatorHex) (by native_decide)

def zeroPoint : EncodedPoint :=
  pointOfBytes (List.replicate pointLength zeroByte) (by native_decide)

def oracleGeneratorDoubleHex : String := "000000000000000000000000000000001638533957d540a9d2370f17cc7ed5863bc0b995b8825e0ee1ea1e1e4d00dbae81f14b0bf3611b78c952aacab827a053000000000000000000000000000000000a4edef9c1ed7f729f520e47730a124fd70662a904ba1074728114d1031e1572c6c886f6b57ec72a6178288c47c33577000000000000000000000000000000000468fb440d82b0630aeb8dca2b5256789a66da69bf91009cbfe6bd221e47aa8ae88dece9764bf3bd999d95d71e4c9899000000000000000000000000000000000f6d4552fa65dd2638b361543f887136a43253d9c66c411697003f7a13c308f5422e1aa0a59c8967acdefd8b6e36ccf3"

def oracleGeneratorDouble : EncodedPoint :=
  pointOfBytes (parsedBytes oracleGeneratorDoubleHex) (by native_decide)

def expectedGeneratorDoubleHex : String := "000000000000000000000000000000001638533957d540a9d2370f17cc7ed5863bc0b995b8825e0ee1ea1e1e4d00dbae81f14b0bf3611b78c952aacab827a053000000000000000000000000000000000a4edef9c1ed7f729f520e47730a124fd70662a904ba1074728114d1031e1572c6c886f6b57ec72a6178288c47c33577000000000000000000000000000000000468fb440d82b0630aeb8dca2b5256789a66da69bf91009cbfe6bd221e47aa8ae88dece9764bf3bd999d95d71e4c9899000000000000000000000000000000000f6d4552fa65dd2638b361543f887136a43253d9c66c411697003f7a13c308f5422e1aa0a59c8967acdefd8b6e36ccf3"

def expectedGeneratorDouble : EncodedPoint :=
  pointOfBytes (parsedBytes expectedGeneratorDoubleHex) (by native_decide)

def invalidPoint : EncodedPoint :=
  pointOfBytes (byte 0x01 :: generatorG2.bytes.drop 1) (by native_decide)

def knownAnswerOracle : CurveOracle :=
  { valid := fun point =>
      point.bytes == zeroPoint.bytes || point.bytes == generatorG2.bytes
    isInfinity := fun point => point.bytes == zeroPoint.bytes
    add := fun left right =>
      if left.bytes == generatorG2.bytes && right.bytes == generatorG2.bytes then
        oracleGeneratorDouble
      else
        zeroPoint }

def inputFor (left right : EncodedPoint) : List Byte :=
  left.bytes ++ right.bytes

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
  [ { name := "empty input is rejected", input := [],
      expected := .error .invalidInputLength, expectedGas := 600 }
  , { name := "511-byte input is rejected", input := List.replicate 511 zeroByte,
      expected := .error .invalidInputLength, expectedGas := 600 }
  , { name := "513-byte input is rejected", input := List.replicate 513 zeroByte,
      expected := .error .invalidInputLength, expectedGas := 600 }
  , { name := "infinity plus infinity is infinity", input := inputFor zeroPoint zeroPoint,
      expected := .ok zeroPoint, expectedGas := 600 }
  , { name := "left infinity returns the right generator encoding",
      input := inputFor zeroPoint generatorG2, expected := .ok generatorG2, expectedGas := 600 }
  , { name := "right infinity returns the left generator encoding",
      input := inputFor generatorG2 zeroPoint, expected := .ok generatorG2, expectedGas := 600 }
  , { name := "generator plus generator matches the known answer",
      input := inputFor generatorG2 generatorG2,
      expected := .ok expectedGeneratorDouble, expectedGas := 600 }
  , { name := "invalid left point is rejected",
      input := inputFor invalidPoint generatorG2,
      expected := .error .invalidPoint, expectedGas := 600 }
  , { name := "invalid right point is rejected",
      input := inputFor generatorG2 invalidPoint,
      expected := .error .invalidPoint, expectedGas := 600 } ]

theorem vector_count : vectors.length = 9 := rfl

theorem all_vectors_pass : vectors.all passes = true := by
  native_decide

theorem known_answer_tables_agree :
    oracleGeneratorDouble = expectedGeneratorDouble := by
  native_decide

theorem fixture_lengths :
    zeroPoint.bytes.length = pointLength ∧
      generatorG2.bytes.length = pointLength ∧
      oracleGeneratorDouble.bytes.length = pointLength ∧
      expectedGeneratorDouble.bytes.length = pointLength ∧
      (inputFor generatorG2 generatorG2).length = inputLength := by
  native_decide

example : parseHex "0" = none := by native_decide

example : parseHex "zz" = none := by native_decide

example : (inputFor zeroPoint zeroPoint).length = inputLength := by native_decide

example : generatorG2.bytes.length = 256 := by native_decide

example : oracleGeneratorDouble.bytes.length = 256 := by native_decide

example : expectedGeneratorDouble.bytes.length = 256 := by native_decide

example : resultEq (run knownAnswerOracle (inputFor zeroPoint generatorG2))
    (.ok generatorG2) = true := by
  native_decide

example : resultEq (run knownAnswerOracle (inputFor generatorG2 zeroPoint))
    (.ok generatorG2) = true := by
  native_decide

example : resultEq (run knownAnswerOracle (inputFor generatorG2 generatorG2))
    (.ok expectedGeneratorDouble) = true := by
  native_decide

example : resultEq (run knownAnswerOracle (inputFor invalidPoint generatorG2))
    (.error .invalidPoint) = true := by
  native_decide

example : resultEq (run knownAnswerOracle (inputFor generatorG2 invalidPoint))
    (.error .invalidPoint) = true := by
  native_decide

/- Mutation sentinels cover metadata, fixed gas, exact framing, both
   validation gates, both infinity projections, addition, and both answer
   tables. -/

def mutatedAddress : Nat := 12

example : mutatedAddress ≠ address := by native_decide

def mutatedName : String := "BLS12_G2_ADD"

example : mutatedName ≠ name := by native_decide

def mutatedCaching : Bool := false

example : mutatedCaching ≠ supportsCaching := by native_decide

def mutatedFixedGas : Nat := 601

example : mutatedFixedGas ≠ totalGasCost schedule (inputFor zeroPoint zeroPoint) := by
  native_decide

def mutatedInputLength : Nat := 511

example : mutatedInputLength ≠ inputLength := by native_decide

def rightPadToInputLength (input : List Byte) : List Byte :=
  input ++ List.replicate (inputLength - input.length) zeroByte

def runWithRightPadding (oracle : CurveOracle) (input : List Byte) : Result :=
  run oracle (rightPadToInputLength input)

example : resultEq
    (runWithRightPadding knownAnswerOracle (List.replicate 511 zeroByte))
    (run knownAnswerOracle (List.replicate 511 zeroByte)) = false := by
  native_decide

def truncateToInputLength (input : List Byte) : List Byte :=
  input.take inputLength

def runWithTruncation (oracle : CurveOracle) (input : List Byte) : Result :=
  run oracle (truncateToInputLength input)

example : resultEq
    (runWithTruncation knownAnswerOracle (List.replicate 513 zeroByte))
    (run knownAnswerOracle (List.replicate 513 zeroByte)) = false := by
  native_decide

def runPairWithoutLeftValidation (oracle : CurveOracle) (input : InputPair) : Result :=
  if !oracle.valid input.right then
    .error .invalidPoint
  else if oracle.isInfinity input.left then
    .ok input.right
  else if oracle.isInfinity input.right then
    .ok input.left
  else
    .ok (oracle.add input.left input.right)

example : resultEq
    (runPairWithoutLeftValidation knownAnswerOracle
      { left := invalidPoint, right := generatorG2 })
    (runPair knownAnswerOracle { left := invalidPoint, right := generatorG2 }) = false := by
  native_decide

def runPairWithoutRightValidation (oracle : CurveOracle) (input : InputPair) : Result :=
  if !oracle.valid input.left then
    .error .invalidPoint
  else if oracle.isInfinity input.left then
    .ok input.right
  else if oracle.isInfinity input.right then
    .ok input.left
  else
    .ok (oracle.add input.left input.right)

example : resultEq
    (runPairWithoutRightValidation knownAnswerOracle
      { left := generatorG2, right := invalidPoint })
    (runPair knownAnswerOracle { left := generatorG2, right := invalidPoint }) = false := by
  native_decide

def runPairWithWrongLeftInfinityResult (oracle : CurveOracle) (input : InputPair) : Result :=
  if !oracle.valid input.left || !oracle.valid input.right then
    .error .invalidPoint
  else if oracle.isInfinity input.left then
    .ok input.left
  else
    runPair oracle input

example : resultEq
    (runPairWithWrongLeftInfinityResult knownAnswerOracle
      { left := zeroPoint, right := generatorG2 })
    (runPair knownAnswerOracle { left := zeroPoint, right := generatorG2 }) = false := by
  native_decide

def runPairWithWrongRightInfinityResult (oracle : CurveOracle) (input : InputPair) : Result :=
  if !oracle.valid input.left || !oracle.valid input.right then
    .error .invalidPoint
  else if oracle.isInfinity input.left then
    .ok input.right
  else if oracle.isInfinity input.right then
    .ok input.right
  else
    .ok (oracle.add input.left input.right)

example : resultEq
    (runPairWithWrongRightInfinityResult knownAnswerOracle
      { left := generatorG2, right := zeroPoint })
    (runPair knownAnswerOracle { left := generatorG2, right := zeroPoint }) = false := by
  native_decide

def runPairWithoutAddition (oracle : CurveOracle) (input : InputPair) : Result :=
  if !oracle.valid input.left || !oracle.valid input.right then
    .error .invalidPoint
  else if oracle.isInfinity input.left then
    .ok input.right
  else if oracle.isInfinity input.right then
    .ok input.left
  else
    .ok input.left

example : resultEq
    (runPairWithoutAddition knownAnswerOracle
      { left := generatorG2, right := generatorG2 })
    (runPair knownAnswerOracle { left := generatorG2, right := generatorG2 }) = false := by
  native_decide

def mutatedExpectedGeneratorDouble : EncodedPoint :=
  pointOfBytes (byte 0x01 :: expectedGeneratorDouble.bytes.drop 1) (by native_decide)

theorem mutated_expected_answer_is_detected :
    mutatedExpectedGeneratorDouble ≠ expectedGeneratorDouble ∧
      mutatedExpectedGeneratorDouble ≠ oracleGeneratorDouble := by
  native_decide

def mutatedOracleGeneratorDouble : EncodedPoint :=
  pointOfBytes (byte 0x01 :: oracleGeneratorDouble.bytes.drop 1) (by native_decide)

theorem mutated_oracle_answer_is_detected :
    mutatedOracleGeneratorDouble ≠ expectedGeneratorDouble ∧
      mutatedOracleGeneratorDouble ≠ oracleGeneratorDouble := by
  native_decide

end Bls12381G2AddVectors
end Precompiles
end Eip803x

