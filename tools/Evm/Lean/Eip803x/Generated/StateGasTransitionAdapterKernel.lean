-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

-- This file is generated. Do not edit.
-- Extractor version: 1.2.0
-- Roslyn compiler version: 5.6.0.0
-- Production source: src/Nethermind/Nethermind.Evm/GasPolicy/StateGasTransitionAdapterKernel.cs
-- Production source SHA-256: ca16429e4cb1b728619b7fa0113b2302ca75600b0a32d9345e38721c7c2d7368
-- Canonical state-gas-transition-adapter IR SHA-256: f51fa70f1d202f3c7fc8c009c83f9cedb353a49fd214e3285bdbbb18b45b51cf

import Eip803x.Generated.StateGasTransitionKernel

namespace Eip803x.Generated.StateGasTransitionAdapterKernel

open Eip803x.Generated.StateGasTransitionKernel

inductive OutcomeKind where
  | completedVoid
  | completedDiscard
  | argumentException
  deriving DecidableEq, Repr

structure Outcome where
  kind : OutcomeKind
  transition : StateGasTransitionResult
  deriving DecidableEq, Repr

def unchanged
    (value : Nat)
    (stateReservoir : Int)
    (stateGasUsed : Int)
    (stateGasSpill : Int)
    (stateGasSpillRefunded : Int) : StateGasTransitionResult :=
  { value := normalizeUInt64 value
    stateReservoir := wrapInt64 stateReservoir
    stateGasUsed := wrapInt64 stateGasUsed
    stateGasSpill := wrapInt64 stateGasSpill
    stateGasSpillRefunded := wrapInt64 stateGasSpillRefunded
    unappliedAmount := 0 }

def addStateGasRefundToReservoir
    (value : Nat)
    (stateReservoir : Int)
    (stateGasUsed : Int)
    (stateGasSpill : Int)
    (stateGasSpillRefunded : Int)
    (amount : Int)
    (trackSpillRefund : Bool)
    : Outcome :=
  { kind := .completedVoid
    transition := Eip803x.Generated.StateGasTransitionKernel.addStateGasRefundToReservoir value stateReservoir stateGasUsed stateGasSpill stateGasSpillRefunded amount trackSpillRefund }

def discardStateGas
    (value : Nat)
    (stateReservoir : Int)
    (stateGasUsed : Int)
    (stateGasSpill : Int)
    (stateGasSpillRefunded : Int)
    (amount : Int)
    (stateGasFloor : Int)
    : Outcome :=
  { kind := .completedDiscard
    transition := Eip803x.Generated.StateGasTransitionKernel.discardStateGas value stateReservoir stateGasUsed stateGasSpill stateGasSpillRefunded amount stateGasFloor }

def refund
    (value : Nat)
    (stateReservoir : Int)
    (stateGasUsed : Int)
    (stateGasSpill : Int)
    (stateGasSpillRefunded : Int)
    (childValue : Nat)
    (childStateReservoir : Int)
    (childStateGasUsed : Int)
    (childStateGasSpill : Int)
    (childStateGasSpillRefunded : Int)
    : Outcome :=
  { kind := .completedVoid
    transition := Eip803x.Generated.StateGasTransitionKernel.refund value stateReservoir stateGasUsed stateGasSpill stateGasSpillRefunded childValue childStateReservoir childStateGasUsed childStateGasSpill childStateGasSpillRefunded }

def refundStateGas
    (value : Nat)
    (stateReservoir : Int)
    (stateGasUsed : Int)
    (stateGasSpill : Int)
    (stateGasSpillRefunded : Int)
    (amount : Int)
    (stateGasFloor : Int)
    (trackSpillRefund : Bool)
    : Outcome :=
  { kind := .completedVoid
    transition := Eip803x.Generated.StateGasTransitionKernel.refundStateGas value stateReservoir stateGasUsed stateGasSpill stateGasSpillRefunded amount stateGasFloor trackSpillRefund }

def removeStateGasRefundFromReservoir
    (value : Nat)
    (stateReservoir : Int)
    (stateGasUsed : Int)
    (stateGasSpill : Int)
    (stateGasSpillRefunded : Int)
    (amount : Int)
    : Outcome :=
  if amount < 0 then
    { kind := .argumentException
      transition := unchanged value stateReservoir stateGasUsed stateGasSpill stateGasSpillRefunded }
  else
    { kind := .completedVoid
      transition := Eip803x.Generated.StateGasTransitionKernel.removeStateGasRefundFromReservoir value stateReservoir stateGasUsed stateGasSpill stateGasSpillRefunded amount }

def repayStateGasSpill
    (value : Nat)
    (stateReservoir : Int)
    (stateGasUsed : Int)
    (stateGasSpill : Int)
    (stateGasSpillRefunded : Int)
    : Outcome :=
  { kind := .completedVoid
    transition := Eip803x.Generated.StateGasTransitionKernel.repayStateGasSpill value stateReservoir stateGasUsed stateGasSpill stateGasSpillRefunded }

def restoreChildStateGas
    (parentValue : Nat)
    (parentStateReservoir : Int)
    (parentStateGasUsed : Int)
    (parentStateGasSpill : Int)
    (parentStateGasSpillRefunded : Int)
    (childStateReservoir : Int)
    (childStateGasUsed : Int)
    (childStateGasSpill : Int)
    (childStateGasSpillRefunded : Int)
    : Outcome :=
  { kind := .completedVoid
    transition := Eip803x.Generated.StateGasTransitionKernel.restoreChildStateGas parentValue parentStateReservoir parentStateGasUsed parentStateGasSpill parentStateGasSpillRefunded childStateReservoir childStateGasUsed childStateGasSpill childStateGasSpillRefunded }

def restoreChildStateGasOnHalt
    (parentValue : Nat)
    (parentStateReservoir : Int)
    (parentStateGasUsed : Int)
    (parentStateGasSpill : Int)
    (parentStateGasSpillRefunded : Int)
    (childStateReservoir : Int)
    (childStateGasUsed : Int)
    (childStateGasSpill : Int)
    (childStateGasSpillRefunded : Int)
    : Outcome :=
  { kind := .completedVoid
    transition := Eip803x.Generated.StateGasTransitionKernel.restoreChildStateGasOnHalt parentValue parentStateReservoir parentStateGasUsed parentStateGasSpill parentStateGasSpillRefunded childStateReservoir childStateGasUsed childStateGasSpill childStateGasSpillRefunded }

def revertRefundToHalt
    (parentValue : Nat)
    (parentStateReservoir : Int)
    (parentStateGasUsed : Int)
    (parentStateGasSpill : Int)
    (parentStateGasSpillRefunded : Int)
    (childStateGasUsed : Int)
    (childStateGasSpill : Int)
    (childStateGasSpillRefunded : Int)
    : Outcome :=
  { kind := .completedVoid
    transition := Eip803x.Generated.StateGasTransitionKernel.revertRefundToHalt parentValue parentStateReservoir parentStateGasUsed parentStateGasSpill parentStateGasSpillRefunded childStateGasUsed childStateGasSpill childStateGasSpillRefunded }

end Eip803x.Generated.StateGasTransitionAdapterKernel
