-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

-- This file is generated. Do not edit.
-- Extractor version: 1.2.0
-- Roslyn compiler version: 5.6.0.0
-- Production source: src/Nethermind/Nethermind.Evm/GasPolicy/AccountAccessPricingKernel.cs
-- Production source SHA-256: 1988faefb55bb65bf4e915e77e99966f43b88675dec47689e04378bde4f31c92
-- Canonical account-access-pricing IR SHA-256: 026161d45607f750fdc7bc6acc023c3b0c7c69ee237d77432fe3bfe86a12ffae

namespace Eip803x.Generated.AccountAccessPricingKernel

def uint64Modulus : Nat := 2 ^ 64
def uint64Max : Nat := uint64Modulus - 1

def normalizeUInt64 (value : Nat) : Nat :=
  if value <= uint64Max then value else value % uint64Modulus

inductive Decision where
  | noCharge
  | charge
  deriving DecidableEq, Repr

inductive AccessKind where
  | default
  | selfDestructBeneficiary
  deriving DecidableEq, Repr

structure Result where
  decision : Decision
  amount : Nat
  deriving DecidableEq, Repr

def priceNormalized
    (hotAndColdEnabled : Bool)
    (eip8038Enabled : Bool)
    (isCold : Bool)
    (isPrecompile : Bool)
    (kind : AccessKind)
    (coldAccountAccessGas : Nat)
    (warmAccessGas : Nat) : Result :=
  if !hotAndColdEnabled then
    { decision := .noCharge, amount := 0 }
  else if isCold && !isPrecompile then
    { decision := .charge, amount := coldAccountAccessGas }
  else if kind == .selfDestructBeneficiary && !eip8038Enabled then
    { decision := .noCharge, amount := 0 }
  else
    { decision := .charge, amount := warmAccessGas }

def price
    (hotAndColdEnabled : Bool)
    (eip8038Enabled : Bool)
    (isCold : Bool)
    (isPrecompile : Bool)
    (kind : AccessKind)
    (coldAccountAccessGas : Nat)
    (warmAccessGas : Nat) : Result :=
  priceNormalized
    hotAndColdEnabled
    eip8038Enabled
    isCold
    isPrecompile
    kind
    (normalizeUInt64 coldAccountAccessGas)
    (normalizeUInt64 warmAccessGas)

end Eip803x.Generated.AccountAccessPricingKernel
