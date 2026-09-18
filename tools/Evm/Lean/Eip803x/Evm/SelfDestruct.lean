-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.AccountPricing
import Eip803x.Gas

namespace Eip803x
namespace Evm
namespace SelfDestruct

open AccountPricing
open GasMachine

/-!
  A handwritten Amsterdam handler-local reference for SELFDESTRUCT. It composes
  the legacy base charge, EIP-8038 beneficiary access/account-write pricing,
  EIP-8037 state charging, EIP-6780 destroy-list selection, and the immediate
  EIP-8246/EIP-7708 balance/log behavior in production order. Transaction-level
  rollback and destroy-list finalization remain outside this leaf.
-/

structure Schedule where
  account : AccountGasSchedule
  baseExecutionGas : Nat
  deriving DecidableEq, Repr

namespace Schedule

def amsterdam : Schedule :=
  { account := AccountGasSchedule.amsterdam
    baseExecutionGas := 5000 }

end Schedule

structure Situation where
  staticContext : Bool
  beneficiaryOnStack : Bool
  beneficiaryAccess : AccessStatus
  sourceCreatedThisTransaction : Bool
  sameAccount : Bool
  sourceBalance : Nat
  beneficiaryLeafExists : Bool
  beneficiaryDead : Bool
  deriving DecidableEq, Repr

def Situation.Consistent (input : Situation) : Prop :=
  (input.sameAccount = true →
      input.beneficiaryLeafExists = true ∧ input.beneficiaryDead = false) ∧
    (input.beneficiaryLeafExists = false → input.beneficiaryDead = true)

instance situationConsistentDecidable (input : Situation) : Decidable input.Consistent := by
  unfold Situation.Consistent
  infer_instance

inductive Status where
  | stop
  | outOfGas
  | stackUnderflow
  | staticCallViolation
  | invalidSituation
  deriving DecidableEq, Repr

inductive Event where
  | baseCharged
  | beneficiaryPopped
  | beneficiaryWarmed
  | destroyMarked
  | balanceRead
  | beneficiaryClassified
  | accountWriteCharged
  | stateCharged
  | beneficiaryCreated
  | beneficiaryCredited
  | transferLogged
  | sourceDebited
  deriving DecidableEq, Repr

structure Effects where
  beneficiaryWarmed : Bool
  destroyMarked : Bool
  beneficiaryCreated : Bool
  beneficiaryCredited : Nat
  sourceDebited : Nat
  transferLogged : Bool
  deriving DecidableEq, Repr

def noEffects : Effects :=
  { beneficiaryWarmed := false
    destroyMarked := false
    beneficiaryCreated := false
    beneficiaryCredited := 0
    sourceDebited := 0
    transferLogged := false }

structure Result where
  status : Status
  gas : GasState
  effects : Effects
  trace : List Event
  deriving DecidableEq, Repr

def result (status : Status) (gas : GasState) (effects : Effects)
    (trace : List Event) : Result :=
  { status, gas, effects, trace }

def accessCharge (schedule : Schedule) : AccessStatus → Nat
  | .cold => schedule.account.base.coldAccountAccess
  | .warm => schedule.account.base.warmAccess

def needsNewAccountCharge (input : Situation) : Bool :=
  0 < input.sourceBalance && input.beneficiaryDead

def markedEffects (input : Situation) : Effects :=
  { noEffects with
    beneficiaryWarmed := true
    destroyMarked := input.sourceCreatedThisTransaction }

def finishTransfer (input : Situation) (effects : Effects) : Effects × List Event :=
  if input.sameAccount then
    (effects, [])
  else
    let created := !input.beneficiaryLeafExists
    let transferred := input.sourceBalance
    let logged := 0 < transferred
    let finalEffects : Effects :=
      { effects with
        beneficiaryCreated := created
        beneficiaryCredited := transferred
        sourceDebited := transferred
        transferLogged := logged }
    let creationTrace := if created then [.beneficiaryCreated] else []
    let creditTrace := [.beneficiaryCredited]
    let logTrace := if logged then [.transferLogged] else []
    (finalEffects, creationTrace ++ creditTrace ++ logTrace ++ [.sourceDebited])

def finish (input : Situation) (gas : GasState) (effects : Effects)
    (trace : List Event) : Result :=
  let (finalEffects, transferTrace) := finishTransfer input effects
  result .stop gas finalEffects (trace ++ transferTrace)

def afterAccess (schedule : Schedule) (input : Situation) (gas : GasState)
    (trace : List Event) : Result :=
  let effects := markedEffects input
  let markedTrace :=
    if input.sourceCreatedThisTransaction then
      trace ++ [.destroyMarked, .balanceRead, .beneficiaryClassified]
    else
      trace ++ [.balanceRead, .beneficiaryClassified]
  if needsNewAccountCharge input then
    match chargeExecution schedule.account.base.accountWrite gas with
    | .error _ => result .outOfGas gas effects markedTrace
    | .ok executionGas =>
        let executionTrace := markedTrace ++ [.accountWriteCharged]
        match chargeState schedule.account.base.newAccountGas executionGas with
        | .error _ => result .outOfGas executionGas effects executionTrace
        | .ok stateGas => finish input stateGas effects (executionTrace ++ [.stateCharged])
  else
    finish input gas effects markedTrace

def run (schedule : Schedule) (input : Situation) (gas : GasState) : Result :=
  if !decide input.Consistent then
    result .invalidSituation gas noEffects []
  else if input.staticContext then
    result .staticCallViolation gas noEffects []
  else
    match chargeExecution schedule.baseExecutionGas gas with
    | .error _ => result .outOfGas gas noEffects []
    | .ok baseGas =>
        if !input.beneficiaryOnStack then
          result .stackUnderflow baseGas noEffects [.baseCharged]
        else
          let popTrace := [.baseCharged, .beneficiaryPopped]
          let warmedEffects := { noEffects with beneficiaryWarmed := true }
          match chargeExecution (accessCharge schedule input.beneficiaryAccess) baseGas with
          | .error _ =>
              result .outOfGas baseGas warmedEffects (popTrace ++ [.beneficiaryWarmed])
          | .ok accessGas =>
              afterAccess schedule input accessGas (popTrace ++ [.beneficiaryWarmed])

theorem static_violation_precedes_charges (schedule : Schedule) (input : Situation)
    (gas : GasState) (hConsistent : input.Consistent) (hStatic : input.staticContext = true) :
    run schedule input gas = result .staticCallViolation gas noEffects [] := by
  simp [run, hConsistent, hStatic]

theorem base_oog_precedes_stack_read (schedule : Schedule) (input : Situation)
    (gas : GasState) (hConsistent : input.Consistent)
    (hStatic : input.staticContext = false)
    (hOog : chargeExecution schedule.baseExecutionGas gas = .error .outOfGas) :
    run schedule input gas = result .outOfGas gas noEffects [] := by
  simp [run, hConsistent, hStatic, hOog]

theorem stack_underflow_retains_base_charge (schedule : Schedule) (input : Situation)
    (gas baseGas : GasState) (hConsistent : input.Consistent)
    (hStatic : input.staticContext = false)
    (hBase : chargeExecution schedule.baseExecutionGas gas = .ok baseGas)
    (hStack : input.beneficiaryOnStack = false) :
    run schedule input gas =
      result .stackUnderflow baseGas noEffects [.baseCharged] := by
  simp [run, hConsistent, hStatic, hBase, hStack]

theorem access_oog_still_warms (schedule : Schedule) (input : Situation)
    (gas baseGas : GasState) (hConsistent : input.Consistent)
    (hStatic : input.staticContext = false)
    (hBase : chargeExecution schedule.baseExecutionGas gas = .ok baseGas)
    (hStack : input.beneficiaryOnStack = true)
    (hOog : chargeExecution (accessCharge schedule input.beneficiaryAccess) baseGas =
      .error .outOfGas) :
    (run schedule input gas).status = .outOfGas ∧
      (run schedule input gas).effects.beneficiaryWarmed = true := by
  simp [run, hConsistent, hStatic, hBase, hStack, hOog, result]

theorem new_account_charge_condition (input : Situation) :
    needsNewAccountCharge input = true ↔
      0 < input.sourceBalance ∧ input.beneficiaryDead = true := by
  simp [needsNewAccountCharge]

theorem successful_self_target_moves_no_balance (_schedule : Schedule) (input : Situation)
    (gas : GasState) (hSame : input.sameAccount = true) :
    (finish input gas (markedEffects input) []).effects.sourceDebited = 0 ∧
      (finish input gas (markedEffects input) []).effects.beneficiaryCredited = 0 ∧
      (finish input gas (markedEffects input) []).effects.transferLogged = false := by
  simp [finish, finishTransfer, hSame, markedEffects, noEffects, result]

theorem successful_other_target_moves_exact_balance (_schedule : Schedule) (input : Situation)
    (gas : GasState) (hOther : input.sameAccount = false) :
    (finish input gas (markedEffects input) []).effects.sourceDebited = input.sourceBalance ∧
      (finish input gas (markedEffects input) []).effects.beneficiaryCredited = input.sourceBalance := by
  simp [finish, finishTransfer, hOther, result]

theorem destroy_marked_exactly_for_same_transaction_creation (input : Situation) :
    (markedEffects input).destroyMarked = input.sourceCreatedThisTransaction := by
  rfl

end SelfDestruct
end Evm
end Eip803x
