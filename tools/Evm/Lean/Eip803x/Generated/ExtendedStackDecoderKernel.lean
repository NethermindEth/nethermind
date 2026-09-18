-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

-- This file is generated. Do not edit.
-- Extractor version: 1.2.0
-- Roslyn compiler version: 5.6.0.0
-- Production source: src/Nethermind/Nethermind.Evm/Instructions/ExtendedStackDecoderKernel.cs
-- Production source SHA-256: 3a7bb91ec7ff267e2cb4aaa4de4c8ea03279cc1d7a9fdf4dff57e34a80b963ce
-- Canonical extended-stack-decoder IR SHA-256: bf68d7954e47ab150b9acfb3c567f00cca9820e637714257052250503b1f6ebc

namespace Eip803x.Generated.ExtendedStackDecoderKernel

def byteModulus : Nat := 2 ^ 8

def normalizeByte (value : Nat) : Nat :=
  value % byteModulus

structure SingleDecode where
  isValid : Bool
  depth : Nat
  deriving DecidableEq, Repr

structure PairDecode where
  isValid : Bool
  firstPosition : Nat
  secondPosition : Nat
  deriving DecidableEq, Repr

def decodeSingle (immediate : Nat) : SingleDecode :=
  let value := normalizeByte immediate
  { isValid := if 91 ≤ value ∧ value ≤ 127 then false else true
    depth := (value + 145) % byteModulus }

/-- Production positions are one-based because `EvmStack.Exchange` consumes one-based positions. -/
def decodePair (immediate : Nat) : PairDecode :=
  let value := normalizeByte immediate
  let shifted := Nat.xor value 143
  let quotient := shifted / 16
  let remainder := shifted % 16
  { isValid := if 82 ≤ value ∧ value ≤ 127 then false else true
    firstPosition := if quotient < remainder then quotient + 2 else remainder + 2
    secondPosition := if quotient < remainder then remainder + 2 else 30 - quotient }

end Eip803x.Generated.ExtendedStackDecoderKernel
