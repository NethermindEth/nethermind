-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.Evm.SelfDestruct

namespace Eip803x
namespace Evm
namespace SelfDestructVectors

open SelfDestruct

def schedule : Schedule := Schedule.amsterdam

def gas (gasLeft stateReservoir stateFromGasLeft stateUsed : Nat) : GasState :=
  { gasLeft, stateReservoir, stateFromGasLeft, stateUsed, refundCounter := 0 }

def gasWithRefund (gasLeft stateReservoir stateFromGasLeft stateUsed : Nat)
    (refundCounter : Int) : GasState :=
  { gasLeft, stateReservoir, stateFromGasLeft, stateUsed, refundCounter }

def situation (staticContext beneficiaryOnStack : Bool) (access : AccessStatus)
    (created sameAccount : Bool) (sourceBalance : Nat)
    (beneficiaryLeafExists beneficiaryDead : Bool) : Situation :=
  { staticContext
    beneficiaryOnStack
    beneficiaryAccess := access
    sourceCreatedThisTransaction := created
    sameAccount
    sourceBalance
    beneficiaryLeafExists
    beneficiaryDead }

def effects (warmed destroyed created : Bool) (credited debited : Nat)
    (logged : Bool) : Effects :=
  { beneficiaryWarmed := warmed
    destroyMarked := destroyed
    beneficiaryCreated := created
    beneficiaryCredited := credited
    sourceDebited := debited
    transferLogged := logged }

def expected (status : Status) (gasState : GasState) (worldEffects : Effects)
    (trace : List Event) : Result :=
  { status, gas := gasState, effects := worldEffects, trace }

def warmZeroAbsent : Situation := situation false true .warm false false 0 false true

def warmPositiveDead : Situation := situation false true .warm true false 7 false true

def warmPositiveExisting : Situation := situation false true .warm false false 7 true false

def warmPositiveEmptyLeaf : Situation := situation false true .warm false false 7 true true

def warmCreatedSelf : Situation := situation false true .warm true true 7 true false

def warmExistingSelf : Situation := situation false true .warm false true 7 true false

structure Vector where
  name : String
  input : Situation
  initialGas : GasState
  expected : Result

def passes (vector : Vector) : Bool := run schedule vector.input vector.initialGas == vector.expected

def vectors : List Vector :=
  [ { name := "inconsistent self target fails closed"
      input := situation false true .warm false true 7 false true
      initialGas := gas 200000 0 0 0
      expected := expected .invalidSituation (gas 200000 0 0 0) noEffects [] }
  , { name := "static violation precedes gas and stack"
      input := situation true false .cold false false 0 false true
      initialGas := gas 200000 0 0 0
      expected := expected .staticCallViolation (gas 200000 0 0 0) noEffects [] }
  , { name := "base execution charge one short"
      input := warmZeroAbsent
      initialGas := gas 4999 0 0 0
      expected := expected .outOfGas (gas 4999 0 0 0) noEffects [] }
  , { name := "stack underflow follows exact base charge"
      input := situation false false .warm false false 0 false true
      initialGas := gas 5000 0 0 0
      expected := expected .stackUnderflow (gas 0 0 0 0) noEffects [.baseCharged] }
  , { name := "warm access one short still warms"
      input := warmZeroAbsent
      initialGas := gas 5099 0 0 0
      expected := expected .outOfGas (gas 99 0 0 0) (effects true false false 0 0 false)
        [.baseCharged, .beneficiaryPopped, .beneficiaryWarmed] }
  , { name := "cold zero transfer creates transient empty beneficiary"
      input := situation false true .cold false false 0 false true
      initialGas := gas 8000 0 0 0
      expected := expected .stop (gas 0 0 0 0) (effects true false true 0 0 false)
        [.baseCharged, .beneficiaryPopped, .beneficiaryWarmed, .balanceRead, .beneficiaryClassified,
          .beneficiaryCreated, .beneficiaryCredited, .sourceDebited] }
  , { name := "account write charge one short"
      input := warmPositiveDead
      initialGas := gas 14099 183600 0 3
      expected := expected .outOfGas (gas 8999 183600 0 3) (effects true true false 0 0 false)
        [.baseCharged, .beneficiaryPopped, .beneficiaryWarmed, .destroyMarked, .balanceRead,
          .beneficiaryClassified] }
  , { name := "state charge one short after execution charges"
      input := warmPositiveDead
      initialGas := gas 14149 183550 0 3
      expected := expected .outOfGas (gas 49 183550 0 3) (effects true true false 0 0 false)
        [.baseCharged, .beneficiaryPopped, .beneficiaryWarmed, .destroyMarked, .balanceRead,
          .beneficiaryClassified,
          .accountWriteCharged] }
  , { name := "exact reservoir creates beneficiary and records no refill"
      input := warmPositiveDead
      initialGas := gas 14100 183600 0 3
      expected := expected .stop (gas 0 0 0 183603) (effects true true true 7 7 true)
        [.baseCharged, .beneficiaryPopped, .beneficiaryWarmed, .destroyMarked, .balanceRead,
          .beneficiaryClassified,
          .accountWriteCharged, .stateCharged, .beneficiaryCreated, .beneficiaryCredited,
          .transferLogged, .sourceDebited] }
  , { name := "partial reservoir spills state charge from gas left"
      input := warmPositiveDead
      initialGas := gas 14150 183550 0 3
      expected := expected .stop (gas 0 0 50 183603) (effects true true true 7 7 true)
        [.baseCharged, .beneficiaryPopped, .beneficiaryWarmed, .destroyMarked, .balanceRead,
          .beneficiaryClassified,
          .accountWriteCharged, .stateCharged, .beneficiaryCreated, .beneficiaryCredited,
          .transferLogged, .sourceDebited] }
  , { name := "positive existing beneficiary avoids write and state charges"
      input := warmPositiveExisting
      initialGas := gasWithRefund 5100 77 4 3 11
      expected := expected .stop (gasWithRefund 0 77 4 3 11) (effects true false false 7 7 true)
        [.baseCharged, .beneficiaryPopped, .beneficiaryWarmed, .balanceRead, .beneficiaryClassified,
          .beneficiaryCredited, .transferLogged, .sourceDebited] }
  , { name := "positive empty account leaf pays new-account state gas without CreateAccount"
      input := warmPositiveEmptyLeaf
      initialGas := gas 14100 183600 0 3
      expected := expected .stop (gas 0 0 0 183603) (effects true false false 7 7 true)
        [.baseCharged, .beneficiaryPopped, .beneficiaryWarmed, .balanceRead, .beneficiaryClassified,
          .accountWriteCharged, .stateCharged, .beneficiaryCredited, .transferLogged,
          .sourceDebited] }
  , { name := "same-transaction self target is marked but preserves balance under EIP-8246"
      input := warmCreatedSelf
      initialGas := gas 5100 77 4 3
      expected := expected .stop (gas 0 77 4 3) (effects true true false 0 0 false)
        [.baseCharged, .beneficiaryPopped, .beneficiaryWarmed, .destroyMarked, .balanceRead,
          .beneficiaryClassified] }
  , { name := "preexisting self target is an immediate no-op after access"
      input := warmExistingSelf
      initialGas := gas 5100 77 4 3
      expected := expected .stop (gas 0 77 4 3) (effects true false false 0 0 false)
        [.baseCharged, .beneficiaryPopped, .beneficiaryWarmed, .balanceRead,
          .beneficiaryClassified] } ]

theorem vector_count : vectors.length = 14 := rfl

theorem all_vectors_pass : vectors.all passes = true := by
  native_decide

theorem vector_state_used_never_decreases :
    vectors.all (fun vector => vector.initialGas.stateUsed ≤ vector.expected.gas.stateUsed) = true := by
  native_decide

theorem successful_vectors_never_refill_state :
    vectors.all (fun vector =>
      vector.expected.status != .stop ||
        vector.expected.gas.stateReservoir ≤ vector.initialGas.stateReservoir) = true := by
  native_decide

example : accessCharge schedule .warm = 100 := by native_decide

example : accessCharge schedule .cold = 3000 := by native_decide

example : schedule.account.base.accountWrite = 9000 := by native_decide

example : schedule.account.base.newAccountGas = 183600 := by native_decide

example : needsNewAccountCharge warmPositiveDead = true := by native_decide

example : needsNewAccountCharge warmPositiveExisting = false := by native_decide

example : needsNewAccountCharge warmZeroAbsent = false := by native_decide

/- Mutation sentinels constrain charge order, access pricing, state effects, and EIP-8246 behavior. -/

def mutatedBaseGas : Nat := 4999

example : mutatedBaseGas ≠ schedule.baseExecutionGas := by native_decide

def accessChargeLegacyWarm (_schedule : Schedule) : AccessStatus → Nat
  | .cold => 3000
  | .warm => 0

example : accessChargeLegacyWarm schedule .warm ≠ accessCharge schedule .warm := by native_decide

def omitAccountWriteSchedule : Schedule :=
  { schedule with account := { schedule.account with base :=
      { schedule.account.base with accountWrite := 0 } } }

example : run omitAccountWriteSchedule warmPositiveDead (gas 5100 183600 0 3) ≠
    run schedule warmPositiveDead (gas 5100 183600 0 3) := by
  native_decide

def omitStateChargeSchedule : Schedule :=
  { schedule with account := { schedule.account with base :=
      { schedule.account.base with newAccountBytes := 0 } } }

example : run omitStateChargeSchedule warmPositiveDead (gas 14100 183600 0 3) ≠
    run schedule warmPositiveDead (gas 14100 183600 0 3) := by
  native_decide

def chargeZeroBalanceAsNew (input : Situation) : Bool := input.beneficiaryDead

example : chargeZeroBalanceAsNew warmZeroAbsent ≠ needsNewAccountCharge warmZeroAbsent := by
  native_decide

def chargeEveryPositiveTransfer (input : Situation) : Bool := 0 < input.sourceBalance

example : chargeEveryPositiveTransfer warmPositiveExisting ≠
    needsNewAccountCharge warmPositiveExisting := by
  native_decide

def markEverySource (_input : Situation) : Bool := true

example : markEverySource warmPositiveExisting ≠ (markedEffects warmPositiveExisting).destroyMarked := by
  native_decide

def mutatedSelfTransferEffects : Effects := effects true true false 7 7 true

example : mutatedSelfTransferEffects ≠ (finish warmCreatedSelf (gas 0 77 4 3)
    (markedEffects warmCreatedSelf) []).effects := by
  native_decide

def mutatedAccessOogEffects : Effects := noEffects

example : mutatedAccessOogEffects ≠ (run schedule warmZeroAbsent (gas 5099 0 0 0)).effects := by
  native_decide

def mutatedStateOogGas : GasState := gas 0 183550 0 3

example : mutatedStateOogGas ≠ (run schedule warmPositiveDead (gas 14149 183550 0 3)).gas := by
  native_decide

def mutatedStateSuccessGas : GasState := gas 0 0 0 183603

example : mutatedStateSuccessGas ≠ (run schedule warmPositiveDead (gas 14150 183550 0 3)).gas := by
  native_decide

def mutatedZeroTransferEffects : Effects := effects true false false 0 0 false

example : mutatedZeroTransferEffects ≠
    (run schedule (situation false true .cold false false 0 false true) (gas 8000 0 0 0)).effects := by
  native_decide

end SelfDestructVectors
end Evm
end Eip803x
