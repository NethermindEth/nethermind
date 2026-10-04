-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only
-- Generated from the exact admitted PayFees slot, body, and standard receiver lineages.

namespace SimpleTransferCompletionExtractor.StageB.PayFees.Generated

inductive Receiver where | ethereum | balEthereum deriving DecidableEq, Repr
inductive Owner where | standardBase | systemOverride deriving DecidableEq, Repr
def resolve (_ : Receiver) : Owner := .standardBase
def systemOwner : Owner := .systemOverride
def systemBodyEmpty : Bool := true

inductive Op where
  | premium | destroyProbe | beneficiary | effectiveBase | baseFees
  | initialCollector | blobCollector | collector | report | returnVoid
  deriving DecidableEq, Repr

def program : List Op :=
  [.premium, .destroyProbe, .beneficiary, .effectiveBase, .baseFees,
   .initialCollector, .blobCollector, .collector, .report, .returnVoid]

def wordModulus : Nat := 2 ^ 256
def priorityFee (premium paid : Nat) : Nat := premium * paid % wordModulus
def effectiveBaseFee (base effective : Nat) : Nat := min base effective
def baseFees (free : Bool) (effectiveBase paid : Nat) : Nat :=
  if !free then effectiveBase * paid % wordModulus else 0
def collectorFees (eip1559 supportsBlobs blobCollector : Bool) (base blob : Nat) : Nat :=
  if supportsBlobs && blobCollector then ((if eip1559 then base else 0) + blob) % wordModulus
  else if eip1559 then base else 0
def reportedBurnt (base blob : Nat) : Nat := (base + blob) % wordModulus

end SimpleTransferCompletionExtractor.StageB.PayFees.Generated
