-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.Precompiles.Ripemd160

namespace Eip803x
namespace Precompiles
namespace Ripemd160Vectors

open Evm.MemoryStackControl
open Ripemd160

def schedule : Schedule := Schedule.amsterdam

def bytes (length : Nat) : List Byte :=
  (List.range length).map byte

/- Every expected digest literal carries an explicit 20-byte proof. A malformed
   literal therefore fails this file's build instead of silently becoming a
   fallback value. The expected table and oracle table below are deliberately
   separate. -/
def expectedEmptyDigest : Digest :=
  digestOfBytes
    [byte 0x9c, byte 0x11, byte 0x85, byte 0xa5, byte 0xc5, byte 0xe9, byte 0xfc, byte 0x54,
      byte 0x61, byte 0x28, byte 0x08, byte 0x97, byte 0x7e, byte 0xe8, byte 0xf5, byte 0x48,
      byte 0xb2, byte 0x25, byte 0x8d, byte 0x31]
    (by native_decide)

def expectedOneDigest : Digest :=
  digestOfBytes
    [byte 0xc8, byte 0x1b, byte 0x94, byte 0x93, byte 0x34, byte 0x20, byte 0x22, byte 0x1a,
      byte 0x7a, byte 0xc0, byte 0x04, byte 0xa9, byte 0x02, byte 0x42, byte 0xd8, byte 0xb1,
      byte 0xd3, byte 0xe5, byte 0x07, byte 0x0d]
    (by native_decide)

def expectedThirtyOneDigest : Digest :=
  digestOfBytes
    [byte 0x2a, byte 0xf4, byte 0x16, byte 0x0d, byte 0xad, byte 0xbb, byte 0x84, byte 0x70,
      byte 0x7f, byte 0x73, byte 0x55, byte 0x17, byte 0x7a, byte 0x46, byte 0x44, byte 0xe4,
      byte 0xcf, byte 0x57, byte 0x7d, byte 0xfa]
    (by native_decide)

def expectedThirtyTwoDigest : Digest :=
  digestOfBytes
    [byte 0xe6, byte 0xba, byte 0xbb, byte 0x96, byte 0x19, byte 0xd7, byte 0xa8, byte 0x12,
      byte 0x72, byte 0x71, byte 0x1f, byte 0xc5, byte 0x46, byte 0xa1, byte 0x6b, byte 0x21,
      byte 0x1d, byte 0xd9, byte 0x39, byte 0x57]
    (by native_decide)

def expectedThirtyThreeDigest : Digest :=
  digestOfBytes
    [byte 0x1e, byte 0x37, byte 0x4a, byte 0xb9, byte 0x24, byte 0xa6, byte 0x52, byte 0xfa,
      byte 0x36, byte 0xb3, byte 0x95, byte 0xd6, byte 0x54, byte 0xd2, byte 0x26, byte 0xbf,
      byte 0x90, byte 0x1b, byte 0x6a, byte 0x04]
    (by native_decide)

def expectedOneHundredDigest : Digest :=
  digestOfBytes
    [byte 0x8a, byte 0xe5, byte 0xd2, byte 0xe6, byte 0xb1, byte 0xf3, byte 0xa5, byte 0x14,
      byte 0x25, byte 0x7f, byte 0x24, byte 0x69, byte 0xb6, byte 0x37, byte 0x45, byte 0x49,
      byte 0x31, byte 0x84, byte 0x4a, byte 0xeb]
    (by native_decide)

def oracleEmptyDigest : Digest :=
  digestOfBytes
    [byte 0x9c, byte 0x11, byte 0x85, byte 0xa5, byte 0xc5, byte 0xe9, byte 0xfc, byte 0x54,
      byte 0x61, byte 0x28, byte 0x08, byte 0x97, byte 0x7e, byte 0xe8, byte 0xf5, byte 0x48,
      byte 0xb2, byte 0x25, byte 0x8d, byte 0x31]
    (by native_decide)

def oracleOneDigest : Digest :=
  digestOfBytes
    [byte 0xc8, byte 0x1b, byte 0x94, byte 0x93, byte 0x34, byte 0x20, byte 0x22, byte 0x1a,
      byte 0x7a, byte 0xc0, byte 0x04, byte 0xa9, byte 0x02, byte 0x42, byte 0xd8, byte 0xb1,
      byte 0xd3, byte 0xe5, byte 0x07, byte 0x0d]
    (by native_decide)

def oracleThirtyOneDigest : Digest :=
  digestOfBytes
    [byte 0x2a, byte 0xf4, byte 0x16, byte 0x0d, byte 0xad, byte 0xbb, byte 0x84, byte 0x70,
      byte 0x7f, byte 0x73, byte 0x55, byte 0x17, byte 0x7a, byte 0x46, byte 0x44, byte 0xe4,
      byte 0xcf, byte 0x57, byte 0x7d, byte 0xfa]
    (by native_decide)

def oracleThirtyTwoDigest : Digest :=
  digestOfBytes
    [byte 0xe6, byte 0xba, byte 0xbb, byte 0x96, byte 0x19, byte 0xd7, byte 0xa8, byte 0x12,
      byte 0x72, byte 0x71, byte 0x1f, byte 0xc5, byte 0x46, byte 0xa1, byte 0x6b, byte 0x21,
      byte 0x1d, byte 0xd9, byte 0x39, byte 0x57]
    (by native_decide)

def oracleThirtyThreeDigest : Digest :=
  digestOfBytes
    [byte 0x1e, byte 0x37, byte 0x4a, byte 0xb9, byte 0x24, byte 0xa6, byte 0x52, byte 0xfa,
      byte 0x36, byte 0xb3, byte 0x95, byte 0xd6, byte 0x54, byte 0xd2, byte 0x26, byte 0xbf,
      byte 0x90, byte 0x1b, byte 0x6a, byte 0x04]
    (by native_decide)

def oracleOneHundredDigest : Digest :=
  digestOfBytes
    [byte 0x8a, byte 0xe5, byte 0xd2, byte 0xe6, byte 0xb1, byte 0xf3, byte 0xa5, byte 0x14,
      byte 0x25, byte 0x7f, byte 0x24, byte 0x69, byte 0xb6, byte 0x37, byte 0x45, byte 0x49,
      byte 0x31, byte 0x84, byte 0x4a, byte 0xeb]
    (by native_decide)

/- This is a literal known-answer oracle for independently authored boundary
   vectors, not a RIPEMD-160 implementation. -/
def boundaryOracle : Ripemd160Oracle :=
  { hash := fun input =>
      if input = bytes 0 then oracleEmptyDigest
      else if input = bytes 1 then oracleOneDigest
      else if input = bytes 31 then oracleThirtyOneDigest
      else if input = bytes 32 then oracleThirtyTwoDigest
      else if input = bytes 33 then oracleThirtyThreeDigest
      else if input = bytes 100 then oracleOneHundredDigest
      else zeroDigest }

structure BoundaryVector where
  inputLength : Nat
  expectedGas : Nat
  expectedDigest : Digest
  deriving DecidableEq, Repr

def boundaryVectors : List BoundaryVector :=
  [ { inputLength := 0, expectedGas := 600, expectedDigest := expectedEmptyDigest }
  , { inputLength := 1, expectedGas := 720, expectedDigest := expectedOneDigest }
  , { inputLength := 31, expectedGas := 720, expectedDigest := expectedThirtyOneDigest }
  , { inputLength := 32, expectedGas := 720, expectedDigest := expectedThirtyTwoDigest }
  , { inputLength := 33, expectedGas := 840, expectedDigest := expectedThirtyThreeDigest }
  , { inputLength := 100, expectedGas := 1080, expectedDigest := expectedOneHundredDigest }
  ]

def passes (vector : BoundaryVector) : Bool :=
  totalGasCost schedule (bytes vector.inputLength) == vector.expectedGas &&
    (run boundaryOracle (bytes vector.inputLength)).1 == true &&
    (run boundaryOracle (bytes vector.inputLength)).2 ==
      zeroPrefix ++ vector.expectedDigest.bytes

theorem boundary_vector_count : boundaryVectors.length = 6 := rfl

theorem all_boundary_vectors_pass : boundaryVectors.all passes = true := by
  native_decide

example : address = 3 := by native_decide

example : name = "RIPEMD160" := by native_decide

example : supportsCaching = true := by native_decide

example : totalGasCost schedule (bytes 0) = 600 := by native_decide

example : totalGasCost schedule (bytes 1) = 720 := by native_decide

example : totalGasCost schedule (bytes 31) = 720 := by native_decide

example : totalGasCost schedule (bytes 32) = 720 := by native_decide

example : totalGasCost schedule (bytes 33) = 840 := by native_decide

example : totalGasCost schedule (bytes 100) = 1080 := by native_decide

example : (run boundaryOracle (bytes 0)).2 =
    zeroPrefix ++ expectedEmptyDigest.bytes := by native_decide

example : (run boundaryOracle (bytes 1)).2 =
    zeroPrefix ++ expectedOneDigest.bytes := by native_decide

example : (run boundaryOracle (bytes 31)).2 =
    zeroPrefix ++ expectedThirtyOneDigest.bytes := by native_decide

example : (run boundaryOracle (bytes 32)).2 =
    zeroPrefix ++ expectedThirtyTwoDigest.bytes := by native_decide

example : (run boundaryOracle (bytes 33)).2 =
    zeroPrefix ++ expectedThirtyThreeDigest.bytes := by native_decide

example : (run boundaryOracle (bytes 100)).2 =
    zeroPrefix ++ expectedOneHundredDigest.bytes := by native_decide

example : (run boundaryOracle (bytes 100)).2.length = 32 := by native_decide

example : (run boundaryOracle (bytes 100)).2.take 12 = zeroPrefix := by native_decide

example : (run boundaryOracle (bytes 100)).2.drop 12 =
    expectedOneHundredDigest.bytes := by native_decide

example : totalGasCost { base := 7, word := 11 } (bytes 33) = 29 := by native_decide

/- Mutation sentinel: changing one digest byte is observable in both tables. -/
def mutatedOneByteDigest : Digest :=
  digestOfBytes
    [byte 0xc8, byte 0x1b, byte 0x94, byte 0x93, byte 0x34, byte 0x20, byte 0x22, byte 0x1a,
      byte 0x7a, byte 0xc0, byte 0x04, byte 0xa9, byte 0x02, byte 0x42, byte 0xd8, byte 0xb1,
      byte 0xd3, byte 0xe5, byte 0x07, byte 0x0c]
    (by native_decide)

theorem one_byte_digest_mutation_sentinel :
    mutatedOneByteDigest.bytes ≠ expectedOneDigest.bytes ∧
      mutatedOneByteDigest.bytes ≠ oracleOneDigest.bytes := by
  native_decide

/- Mutation sentinel: floor division would undercharge a one-byte input. -/
def mutatedFloorWords (length : Nat) : Nat := length / 32

example : mutatedFloorWords 1 ≠ wordsForBytes 1 := by native_decide

/- Mutation sentinel: changing the base price changes the empty-input charge. -/
def mutatedBaseSchedule : Schedule := { schedule with base := schedule.base + 1 }

example : totalGasCost mutatedBaseSchedule (bytes 0) ≠
    totalGasCost schedule (bytes 0) := by native_decide

/- Mutation sentinel: changing the per-word price changes a one-word charge. -/
def mutatedWordSchedule : Schedule := { schedule with word := schedule.word + 1 }

example : totalGasCost mutatedWordSchedule (bytes 1) ≠
    totalGasCost schedule (bytes 1) := by native_decide

/- Mutation sentinel: address 3 is not interchangeable with another address. -/
def mutatedAddress : Nat := 4

example : mutatedAddress ≠ address := by native_decide

/- Mutation sentinel: the registered metadata name is exact. -/
def mutatedName : String := "RIPEMD-160"

example : mutatedName ≠ name := by native_decide

/- Mutation sentinel: disabling the inherited cache flag changes metadata. -/
def mutatedCaching : Bool := false

example : mutatedCaching ≠ supportsCaching := by native_decide

/- Mutation sentinel: changing any byte in the twelve-byte prefix is observable. -/
def mutatedPrefixOutput (oracle : Ripemd160Oracle) (input : List Byte) : List Byte :=
  byte 1 :: List.replicate 11 zeroByte ++ (oracle.hash input).bytes

example : (mutatedPrefixOutput boundaryOracle (bytes 0)).take 12 ≠ zeroPrefix := by
  native_decide

/- Mutation sentinel: omitting one padding byte violates the output-size contract. -/
def mutatedShortOutput (oracle : Ripemd160Oracle) (input : List Byte) : Bool × List Byte :=
  (true, List.replicate 11 zeroByte ++ (oracle.hash input).bytes)

example : (mutatedShortOutput boundaryOracle (bytes 0)).2.length ≠ 32 := by
  native_decide

/- Mutation sentinel: returning failure changes the unconditional-success contract. -/
def mutatedSuccess (oracle : Ripemd160Oracle) (input : List Byte) : Bool × List Byte :=
  (false, paddedOutput (oracle.hash input))

example : (mutatedSuccess boundaryOracle (bytes 0)).1 ≠ true := by native_decide

end Ripemd160Vectors
end Precompiles
end Eip803x
