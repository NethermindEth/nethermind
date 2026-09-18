-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

-- This file is generated. Do not edit.
-- Extractor version: 1.2.0
-- Roslyn compiler version: 5.6.0.0
-- Production source: src/Nethermind/Nethermind.Evm.Precompiles/IdentityPrecompileKernel.cs
-- Production source SHA-256: 394631d94010725bdf1c62f714220ea051c961ed26d10725e685a2026928729b
-- Canonical identity-precompile IR SHA-256: a0d79600c384e3141f65995b6d45313d23b1f1279f019a6915d9573a648eb82d

namespace Eip803x.Generated.IdentityPrecompileKernel

def uint32Modulus : Nat := 2 ^ 32
def uint32Max : Nat := uint32Modulus - 1

def normalizeUInt32 (value : Nat) : Nat :=
  if value <= uint32Max then value else value % uint32Modulus

def baseGasCost : Nat := 15

def wordsForBytes (inputLength : Nat) : Nat :=
  (normalizeUInt32 inputLength + 31) / 32

def dataGasCost (inputLength : Nat) : Nat :=
  3 * wordsForBytes inputLength

end Eip803x.Generated.IdentityPrecompileKernel
