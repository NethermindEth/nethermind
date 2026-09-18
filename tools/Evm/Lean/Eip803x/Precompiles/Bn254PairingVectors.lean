-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.Precompiles.Bn254Pairing

namespace Eip803x
namespace Precompiles
namespace Bn254PairingVectors

open Evm.MemoryStackControl
open Bn254Pairing

def zeroPair : List Byte := List.replicate 192 zeroByte

def knownFalsePair : List Byte :=
  [ byte 0x03, byte 0x41, byte 0xb6, byte 0x5d, byte 0x1b, byte 0x32, byte 0x80, byte 0x5a,
    byte 0xed, byte 0xf2, byte 0x9c, byte 0x47, byte 0x04, byte 0xae, byte 0x12, byte 0x5b,
    byte 0x98, byte 0xbb, byte 0x9b, byte 0x73, byte 0x6d, byte 0x6e, byte 0x05, byte 0xbd,
    byte 0x93, byte 0x43, byte 0x20, byte 0x63, byte 0x2b, byte 0xf4, byte 0x6b, byte 0xb6,
    byte 0x0d, byte 0x22, byte 0xbc, byte 0x98, byte 0x57, byte 0x18, byte 0xac, byte 0xbc,
    byte 0xf5, byte 0x1e, byte 0x37, byte 0x40, byte 0xc1, byte 0x56, byte 0x5f, byte 0x66,
    byte 0xff, byte 0x89, byte 0x0d, byte 0xfd, byte 0x23, byte 0x02, byte 0xfc, byte 0x51,
    byte 0xab, byte 0xc9, byte 0x99, byte 0xc8, byte 0x3d, byte 0x87, byte 0x74, byte 0xba,
    byte 0x0d, byte 0x2c, byte 0x49, byte 0x2b, byte 0xf1, byte 0x35, byte 0xed, byte 0x45,
    byte 0xb0, byte 0xd6, byte 0x26, byte 0x5c, byte 0x27, byte 0x4d, byte 0x14, byte 0x5d,
    byte 0x35, byte 0xb7, byte 0x3a, byte 0xfd, byte 0x41, byte 0xee, byte 0x95, byte 0xd3,
    byte 0xf1, byte 0xda, byte 0x4b, byte 0xc8, byte 0x76, byte 0x10, byte 0x38, byte 0x80,
    byte 0x02, byte 0x51, byte 0xd1, byte 0x38, byte 0xdb, byte 0x1b, byte 0x97, byte 0x48,
    byte 0xff, byte 0xc2, byte 0x57, byte 0xb1, byte 0x47, byte 0xa1, byte 0xae, byte 0xa6,
    byte 0x64, byte 0x13, byte 0xb1, byte 0x4d, byte 0xf7, byte 0x67, byte 0xf9, byte 0x8f,
    byte 0x7b, byte 0xa0, byte 0x24, byte 0x89, byte 0xc6, byte 0x17, byte 0xea, byte 0xe5,
    byte 0x10, byte 0x65, byte 0xff, byte 0x2b, byte 0xd9, byte 0xa5, byte 0xb1, byte 0x67,
    byte 0xdb, byte 0x36, byte 0x22, byte 0x5a, byte 0x35, byte 0xfd, byte 0x71, byte 0x2d,
    byte 0x78, byte 0x13, byte 0x09, byte 0xf4, byte 0xe2, byte 0xc8, byte 0x54, byte 0x1a,
    byte 0x33, byte 0x5b, byte 0x2c, byte 0x42, byte 0xbd, byte 0x2b, byte 0xca, byte 0xe4,
    byte 0x19, byte 0x1c, byte 0xd5, byte 0x28, byte 0xd7, byte 0x49, byte 0xc5, byte 0x2f,
    byte 0x3e, byte 0x19, byte 0x8e, byte 0x53, byte 0x48, byte 0x68, byte 0xd5, byte 0x37,
    byte 0x86, byte 0x71, byte 0x09, byte 0x41, byte 0x9a, byte 0x32, byte 0x31, byte 0x48,
    byte 0x86, byte 0xf6, byte 0xbb, byte 0x2b, byte 0xcd, byte 0x33, byte 0x77, byte 0x73 ]

def unknownPair : List Byte := byte 0x01 :: List.replicate 191 zeroByte

def knownAnswerOracle : PairingOracle :=
  { check := fun input =>
      if input == zeroPair || input == zeroPair ++ zeroPair then some true
      else if input == knownFalsePair || input == knownFalsePair ++ zeroPair then some false
      else none }

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

def passes (vector : Vector) : Bool :=
  resultEq (run knownAnswerOracle vector.input) vector.expected &&
    totalGasCost schedule vector.input == vector.expectedGas

def vectors : List Vector :=
  [ { name := "empty input returns one", input := [],
      expected := .ok (encodedBoolean true), expectedGas := 45000 }
  , { name := "one infinity pair returns one", input := zeroPair,
      expected := .ok (encodedBoolean true), expectedGas := 79000 }
  , { name := "two infinity pairs return one", input := zeroPair ++ zeroPair,
      expected := .ok (encodedBoolean true), expectedGas := 113000 }
  , { name := "pinned Nethermind one-pair non-identity result", input := knownFalsePair,
      expected := .ok (encodedBoolean false), expectedGas := 79000 }
  , { name := "known false product with infinity remains false",
      input := knownFalsePair ++ zeroPair,
      expected := .ok (encodedBoolean false), expectedGas := 113000 }
  , { name := "one byte is invalid and prices zero complete pairs", input := [byte 0x01],
      expected := .error .invalidInputLength, expectedGas := 45000 }
  , { name := "193 bytes are invalid and price one complete pair",
      input := List.replicate 193 zeroByte,
      expected := .error .invalidInputLength, expectedGas := 79000 }
  , { name := "valid-length oracle failure fails", input := unknownPair,
      expected := .error .pairingFailed, expectedGas := 79000 } ]

theorem vector_count : vectors.length = 8 := rfl

theorem all_vectors_pass : vectors.all passes = true := by
  native_decide

theorem known_fixture_length : knownFalsePair.length = pairLength := by
  native_decide

theorem known_fixture_expected_result :
    resultEq (run knownAnswerOracle knownFalsePair) (.ok (List.replicate 32 zeroByte)) = true := by
  native_decide

example : validInputLength [] = true := by native_decide

example : validInputLength zeroPair = true := by native_decide

example : validInputLength (zeroPair ++ zeroPair) = true := by native_decide

example : validInputLength (List.replicate 191 zeroByte) = false := by native_decide

example : pairCount (List.replicate 191 zeroByte) = 0 := by native_decide

example : pairCount (List.replicate 193 zeroByte) = 1 := by native_decide

example : encodedBoolean false = List.replicate 32 zeroByte := by native_decide

example : encodedBoolean true = List.replicate 31 zeroByte ++ [byte 0x01] := by native_decide

/- Mutation sentinels constrain metadata, floor pricing, admission, empty handling, and output. -/

def mutatedAddress : Nat := 7

example : mutatedAddress ≠ address := by native_decide

def mutatedName : String := "ALT_BN128_PAIRING"

example : mutatedName ≠ name := by native_decide

def mutatedCaching : Bool := false

example : mutatedCaching ≠ supportsCaching := by native_decide

def mutatedBaseGas : Nat := 100000

example : mutatedBaseGas ≠ totalGasCost schedule [] := by native_decide

def mutatedPairGas : Nat := 80000

example : schedule.baseGas + mutatedPairGas ≠ totalGasCost schedule zeroPair := by native_decide

def ceilingPairCount (input : List Byte) : Nat :=
  (input.length + pairLength - 1) / pairLength

example : ceilingPairCount [byte 0x01] ≠ pairCount [byte 0x01] := by native_decide

def acceptsEveryLength (_ : List Byte) : Bool := true

example : acceptsEveryLength [byte 0x01] ≠ validInputLength [byte 0x01] := by native_decide

def emptyReturnsFalse : Result := .ok (encodedBoolean false)

example : resultEq emptyReturnsFalse (run knownAnswerOracle []) = false := by native_decide

def littleEndianBoolean (value : Bool) : List Byte :=
  (if value then byte 0x01 else zeroByte) :: List.replicate 31 zeroByte

example : littleEndianBoolean true ≠ encodedBoolean true := by native_decide

def mutatedKnownAnswerOracle : PairingOracle :=
  { check := fun input => if input == knownFalsePair then some true else knownAnswerOracle.check input }

example : resultEq (run mutatedKnownAnswerOracle knownFalsePair)
    (run knownAnswerOracle knownFalsePair) = false := by
  native_decide

def mutatedKnownFalsePair : List Byte :=
  byte 0x04 :: knownFalsePair.drop 1

theorem mutated_known_input_is_detected :
    mutatedKnownFalsePair ≠ knownFalsePair ∧ knownAnswerOracle.check mutatedKnownFalsePair = none := by
  native_decide

end Bn254PairingVectors
end Precompiles
end Eip803x
