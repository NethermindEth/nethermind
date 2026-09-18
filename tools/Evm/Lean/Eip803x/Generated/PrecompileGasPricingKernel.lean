-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

-- This file is generated. Do not edit.
-- Extractor version: 1.2.0
-- Roslyn compiler version: 5.6.0.0
-- Production source: src/Nethermind/Nethermind.Evm/GasPolicy/PrecompileGasPricingKernel.cs
-- Production source SHA-256: dd23b8bd461a13c75ec203b1dbe853a885f61640edf4d47287c1dadd0d9ff576
-- Canonical precompile-gas-pricing IR SHA-256: a972e120172c64fa5ec1012fc098e5c9f23d0f2cc1fbc2fce53b6a7bfbfcb485

namespace Eip803x.Generated.PrecompileGasPricingKernel

def uint64Modulus : Nat := 2 ^ 64
def uint64Max : Nat := uint64Modulus - 1

def normalizeUInt64 (value : Nat) : Nat :=
  if value <= uint64Max then value else value % uint64Modulus

inductive Outcome where
  | success
  | baseDataOverflow
  | outOfGas
  deriving DecidableEq, Repr

structure Result where
  outcome : Outcome
  remainingGas : Nat
  chargedGas : Nat
  deriving DecidableEq, Repr

def tryConsumeNormalized
    (gas : Nat)
    (baseGasCost : Nat)
    (dataGasCost : Nat) : Result :=
  if baseGasCost > uint64Max - dataGasCost then
    { outcome := .baseDataOverflow, remainingGas := gas, chargedGas := 0 }
  else
    let totalGasCost := baseGasCost + dataGasCost
    if gas < totalGasCost then
      { outcome := .outOfGas, remainingGas := 0, chargedGas := 0 }
    else
      { outcome := .success,
        remainingGas := gas - totalGasCost,
        chargedGas := totalGasCost }

def tryConsume
    (gas : Nat)
    (baseGasCost : Nat)
    (dataGasCost : Nat) : Result :=
  tryConsumeNormalized
    (normalizeUInt64 gas)
    (normalizeUInt64 baseGasCost)
    (normalizeUInt64 dataGasCost)

end Eip803x.Generated.PrecompileGasPricingKernel
