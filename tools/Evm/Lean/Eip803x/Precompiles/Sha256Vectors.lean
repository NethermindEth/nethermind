-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.Precompiles.Sha256

namespace Eip803x
namespace Precompiles
namespace Sha256Vectors

open Evm.MemoryStackControl
open Sha256

def schedule : Schedule := Schedule.amsterdam

def bytes (length : Nat) : List Byte :=
  (List.range length).map byte

/- Every digest literal carries an explicit 32-byte proof.  A malformed
   literal therefore fails this file's build instead of silently becoming a
   different fallback value.  The expected table and oracle table are kept
   separate so mutating either side breaks `all_boundary_vectors_pass`. -/
def expectedEmptyDigest : Digest :=
  digestOfBytes
    [byte 0xe3, byte 0xb0, byte 0xc4, byte 0x42, byte 0x98, byte 0xfc, byte 0x1c, byte 0x14,
      byte 0x9a, byte 0xfb, byte 0xf4, byte 0xc8, byte 0x99, byte 0x6f, byte 0xb9, byte 0x24,
      byte 0x27, byte 0xae, byte 0x41, byte 0xe4, byte 0x64, byte 0x9b, byte 0x93, byte 0x4c,
      byte 0xa4, byte 0x95, byte 0x99, byte 0x1b, byte 0x78, byte 0x52, byte 0xb8, byte 0x55]
    (by native_decide)

def expectedOneDigest : Digest :=
  digestOfBytes
    [byte 0x6e, byte 0x34, byte 0x0b, byte 0x9c, byte 0xff, byte 0xb3, byte 0x7a, byte 0x98,
      byte 0x9c, byte 0xa5, byte 0x44, byte 0xe6, byte 0xbb, byte 0x78, byte 0x0a, byte 0x2c,
      byte 0x78, byte 0x90, byte 0x1d, byte 0x3f, byte 0xb3, byte 0x37, byte 0x38, byte 0x76,
      byte 0x85, byte 0x11, byte 0xa3, byte 0x06, byte 0x17, byte 0xaf, byte 0xa0, byte 0x1d]
    (by native_decide)

def expectedThirtyOneDigest : Digest :=
  digestOfBytes
    [byte 0x4f, byte 0x23, byte 0xc2, byte 0xca, byte 0x8c, byte 0x5c, byte 0x96, byte 0x2e,
      byte 0x50, byte 0xcd, byte 0x31, byte 0xe2, byte 0x21, byte 0xbf, byte 0xb6, byte 0xd0,
      byte 0xad, byte 0xca, byte 0x19, byte 0x11, byte 0x1d, byte 0xca, byte 0x8e, byte 0x0c,
      byte 0x62, byte 0x59, byte 0x8f, byte 0xf1, byte 0x46, byte 0xdd, byte 0x19, byte 0xc4]
    (by native_decide)

def expectedThirtyTwoDigest : Digest :=
  digestOfBytes
    [byte 0x63, byte 0x0d, byte 0xcd, byte 0x29, byte 0x66, byte 0xc4, byte 0x33, byte 0x66,
      byte 0x91, byte 0x12, byte 0x54, byte 0x48, byte 0xbb, byte 0xb2, byte 0x5b, byte 0x4f,
      byte 0xf4, byte 0x12, byte 0xa4, byte 0x9c, byte 0x73, byte 0x2d, byte 0xb2, byte 0xc8,
      byte 0xab, byte 0xc1, byte 0xb8, byte 0x58, byte 0x1b, byte 0xd7, byte 0x10, byte 0xdd]
    (by native_decide)

def expectedThirtyThreeDigest : Digest :=
  digestOfBytes
    [byte 0x5d, byte 0x8f, byte 0xcf, byte 0xef, byte 0xa9, byte 0xae, byte 0xeb, byte 0x71,
      byte 0x1f, byte 0xb8, byte 0xed, byte 0x1e, byte 0x4b, byte 0x7d, byte 0x5c, byte 0x8a,
      byte 0x9b, byte 0xaf, byte 0xa4, byte 0x6e, byte 0x8e, byte 0x76, byte 0xe6, byte 0x8a,
      byte 0xa1, byte 0x8a, byte 0xdc, byte 0xe5, byte 0xa1, byte 0x0d, byte 0xf6, byte 0xab]
    (by native_decide)

def expectedOneHundredDigest : Digest :=
  digestOfBytes
    [byte 0xbc, byte 0xe0, byte 0xaf, byte 0xf1, byte 0x9c, byte 0xf5, byte 0xaa, byte 0x6a,
      byte 0x74, byte 0x69, byte 0xa3, byte 0x0d, byte 0x61, byte 0xd0, byte 0x4e, byte 0x43,
      byte 0x76, byte 0xe4, byte 0xbb, byte 0xf6, byte 0x38, byte 0x10, byte 0x52, byte 0xee,
      byte 0x9e, byte 0x7f, byte 0x33, byte 0x92, byte 0x5c, byte 0x95, byte 0x4d, byte 0x52]
    (by native_decide)

def oracleEmptyDigest : Digest :=
  digestOfBytes
    [byte 0xe3, byte 0xb0, byte 0xc4, byte 0x42, byte 0x98, byte 0xfc, byte 0x1c, byte 0x14,
      byte 0x9a, byte 0xfb, byte 0xf4, byte 0xc8, byte 0x99, byte 0x6f, byte 0xb9, byte 0x24,
      byte 0x27, byte 0xae, byte 0x41, byte 0xe4, byte 0x64, byte 0x9b, byte 0x93, byte 0x4c,
      byte 0xa4, byte 0x95, byte 0x99, byte 0x1b, byte 0x78, byte 0x52, byte 0xb8, byte 0x55]
    (by native_decide)

def oracleOneDigest : Digest :=
  digestOfBytes
    [byte 0x6e, byte 0x34, byte 0x0b, byte 0x9c, byte 0xff, byte 0xb3, byte 0x7a, byte 0x98,
      byte 0x9c, byte 0xa5, byte 0x44, byte 0xe6, byte 0xbb, byte 0x78, byte 0x0a, byte 0x2c,
      byte 0x78, byte 0x90, byte 0x1d, byte 0x3f, byte 0xb3, byte 0x37, byte 0x38, byte 0x76,
      byte 0x85, byte 0x11, byte 0xa3, byte 0x06, byte 0x17, byte 0xaf, byte 0xa0, byte 0x1d]
    (by native_decide)

def oracleThirtyOneDigest : Digest :=
  digestOfBytes
    [byte 0x4f, byte 0x23, byte 0xc2, byte 0xca, byte 0x8c, byte 0x5c, byte 0x96, byte 0x2e,
      byte 0x50, byte 0xcd, byte 0x31, byte 0xe2, byte 0x21, byte 0xbf, byte 0xb6, byte 0xd0,
      byte 0xad, byte 0xca, byte 0x19, byte 0x11, byte 0x1d, byte 0xca, byte 0x8e, byte 0x0c,
      byte 0x62, byte 0x59, byte 0x8f, byte 0xf1, byte 0x46, byte 0xdd, byte 0x19, byte 0xc4]
    (by native_decide)

def oracleThirtyTwoDigest : Digest :=
  digestOfBytes
    [byte 0x63, byte 0x0d, byte 0xcd, byte 0x29, byte 0x66, byte 0xc4, byte 0x33, byte 0x66,
      byte 0x91, byte 0x12, byte 0x54, byte 0x48, byte 0xbb, byte 0xb2, byte 0x5b, byte 0x4f,
      byte 0xf4, byte 0x12, byte 0xa4, byte 0x9c, byte 0x73, byte 0x2d, byte 0xb2, byte 0xc8,
      byte 0xab, byte 0xc1, byte 0xb8, byte 0x58, byte 0x1b, byte 0xd7, byte 0x10, byte 0xdd]
    (by native_decide)

def oracleThirtyThreeDigest : Digest :=
  digestOfBytes
    [byte 0x5d, byte 0x8f, byte 0xcf, byte 0xef, byte 0xa9, byte 0xae, byte 0xeb, byte 0x71,
      byte 0x1f, byte 0xb8, byte 0xed, byte 0x1e, byte 0x4b, byte 0x7d, byte 0x5c, byte 0x8a,
      byte 0x9b, byte 0xaf, byte 0xa4, byte 0x6e, byte 0x8e, byte 0x76, byte 0xe6, byte 0x8a,
      byte 0xa1, byte 0x8a, byte 0xdc, byte 0xe5, byte 0xa1, byte 0x0d, byte 0xf6, byte 0xab]
    (by native_decide)

def oracleOneHundredDigest : Digest :=
  digestOfBytes
    [byte 0xbc, byte 0xe0, byte 0xaf, byte 0xf1, byte 0x9c, byte 0xf5, byte 0xaa, byte 0x6a,
      byte 0x74, byte 0x69, byte 0xa3, byte 0x0d, byte 0x61, byte 0xd0, byte 0x4e, byte 0x43,
      byte 0x76, byte 0xe4, byte 0xbb, byte 0xf6, byte 0x38, byte 0x10, byte 0x52, byte 0xee,
      byte 0x9e, byte 0x7f, byte 0x33, byte 0x92, byte 0x5c, byte 0x95, byte 0x4d, byte 0x52]
    (by native_decide)

/- This is a literal known-answer oracle for the independently authored
   boundary vectors, not a SHA-256 implementation. -/
def boundaryOracle : Sha256Oracle :=
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
  [ { inputLength := 0, expectedGas := 60, expectedDigest := expectedEmptyDigest }
  , { inputLength := 1, expectedGas := 72, expectedDigest := expectedOneDigest }
  , { inputLength := 31, expectedGas := 72, expectedDigest := expectedThirtyOneDigest }
  , { inputLength := 32, expectedGas := 72, expectedDigest := expectedThirtyTwoDigest }
  , { inputLength := 33, expectedGas := 84, expectedDigest := expectedThirtyThreeDigest }
  , { inputLength := 100, expectedGas := 108, expectedDigest := expectedOneHundredDigest }
  ]

def passes (vector : BoundaryVector) : Bool :=
  totalGasCost schedule (bytes vector.inputLength) == vector.expectedGas &&
    (run boundaryOracle (bytes vector.inputLength)).1 == true &&
    (run boundaryOracle (bytes vector.inputLength)).2 == vector.expectedDigest.bytes

theorem boundary_vector_count : boundaryVectors.length = 6 := rfl

theorem all_boundary_vectors_pass : boundaryVectors.all passes = true := by
  native_decide

example : address = 2 := by native_decide

example : name = "SHA256" := by native_decide

example : supportsCaching = true := by native_decide

example : totalGasCost schedule (bytes 0) = 60 := by native_decide

example : totalGasCost schedule (bytes 1) = 72 := by native_decide

example : totalGasCost schedule (bytes 31) = 72 := by native_decide

example : totalGasCost schedule (bytes 32) = 72 := by native_decide

example : totalGasCost schedule (bytes 33) = 84 := by native_decide

example : totalGasCost schedule (bytes 100) = 108 := by native_decide

example : (run boundaryOracle (bytes 0)).2 = expectedEmptyDigest.bytes := by native_decide

example : (run boundaryOracle (bytes 1)).2 = expectedOneDigest.bytes := by native_decide

example : (run boundaryOracle (bytes 31)).2 = expectedThirtyOneDigest.bytes := by native_decide

example : (run boundaryOracle (bytes 32)).2 = expectedThirtyTwoDigest.bytes := by native_decide

example : (run boundaryOracle (bytes 33)).2 = expectedThirtyThreeDigest.bytes := by native_decide

example : (run boundaryOracle (bytes 100)).2 = expectedOneHundredDigest.bytes := by native_decide

example : (run boundaryOracle (bytes 100)).2.length = 32 := by native_decide

example : totalGasCost { base := 7, word := 11 } (bytes 33) = 29 := by native_decide

/-- Mutation sentinel: floor division would undercharge a one-byte input. -/
def mutatedFloorWords (length : Nat) : Nat := length / 32

example : mutatedFloorWords 1 ≠ wordsForBytes 1 := by native_decide

/-- Mutation sentinel: changing the base price changes the empty-input charge. -/
def mutatedBaseSchedule : Schedule := { schedule with base := schedule.base + 1 }

example : totalGasCost mutatedBaseSchedule (bytes 0) ≠
    totalGasCost schedule (bytes 0) := by native_decide

/-- Mutation sentinel: changing the per-word price changes a one-word charge. -/
def mutatedWordSchedule : Schedule := { schedule with word := schedule.word + 1 }

example : totalGasCost mutatedWordSchedule (bytes 1) ≠
    totalGasCost schedule (bytes 1) := by native_decide

/-- Mutation sentinel: address 2 is not interchangeable with another address. -/
def mutatedAddress : Nat := 3

example : mutatedAddress ≠ address := by native_decide

/-- Mutation sentinel: the registered metadata name is exact. -/
def mutatedName : String := "SHA-256"

example : mutatedName ≠ name := by native_decide

/-- Mutation sentinel: disabling the inherited cache flag changes metadata. -/
def mutatedCaching : Bool := false

example : mutatedCaching ≠ supportsCaching := by native_decide

/-- Mutation sentinel: changing one digest byte is observable in both tables. -/
def mutatedOneByteDigest : Digest :=
  digestOfBytes
    [byte 0x6e, byte 0x34, byte 0x0b, byte 0x9c, byte 0xff, byte 0xb3, byte 0x7a, byte 0x98,
      byte 0x9c, byte 0xa5, byte 0x44, byte 0xe6, byte 0xbb, byte 0x78, byte 0x0a, byte 0x2c,
      byte 0x78, byte 0x90, byte 0x1d, byte 0x3f, byte 0xb3, byte 0x37, byte 0x38, byte 0x76,
      byte 0x85, byte 0x11, byte 0xa3, byte 0x06, byte 0x17, byte 0xaf, byte 0xa0, byte 0x1c]
    (by native_decide)

theorem one_byte_digest_mutation_sentinel :
    mutatedOneByteDigest.bytes ≠ expectedOneDigest.bytes ∧
      mutatedOneByteDigest.bytes ≠ (run boundaryOracle (bytes 1)).2 := by
  native_decide

/-- Mutation sentinel: truncating the digest violates the 32-byte output contract. -/
def mutatedOutput (oracle : Sha256Oracle) (input : List Byte) : Bool × List Byte :=
  (true, (oracle.hash input).bytes.take 31)

example : (mutatedOutput boundaryOracle (bytes 0)).2.length ≠ 32 := by
  native_decide

/-- Mutation sentinel: returning failure changes the unconditional-success contract. -/
def mutatedSuccess (oracle : Sha256Oracle) (input : List Byte) : Bool × List Byte :=
  (false, (oracle.hash input).bytes)

example : (mutatedSuccess boundaryOracle (bytes 0)).1 ≠ true := by native_decide

end Sha256Vectors
end Precompiles
end Eip803x
