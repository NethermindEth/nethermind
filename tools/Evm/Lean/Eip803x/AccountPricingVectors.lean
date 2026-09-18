-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.AccountPricing

namespace Eip803x

namespace AccountPricing

/-!
  Normative boundary vectors for `AccountPricing`.  The operation vectors are
  parameterized by the schedule, with no duplicated fork constants in the
  expected records.  Expected records contain branch-specific table effects
  and totals, rather than calling the pricing definitions.
-/

inductive PricingVector where
  | operation (name : String) (input : AccountOperation) (expected : OperationPrice)
  | accessListAddress (name : String) (expected : Nat)
  | accessListStorageKey (name : String) (expected : Nat)
  deriving DecidableEq, Repr

private def literalAccount (access delegated write stipend second execution state refill : Nat) :
    OperationPrice :=
  .account
    { accessCharge := access
      delegatedTargetAccessCharge := delegated
      accountWriteCharge := write
      callStipendCharge := stipend
      secondReadCharge := second
      executionCharge := execution
      stateCharge := state
      stateRefill := refill }

private def literalCreate
    (entryAccess entryInit entryHash entryNew entryRefill entryExecution entryState entryStateRefill
      depositExecution depositState execution state stateRefill : Nat)
    (rollback : CreateStateRollback) : OperationPrice :=
  .create
    { entry :=
        { createAccessCharge := entryAccess
          initCodeCharge := entryInit
          create2HashCharge := entryHash
          newAccountStateCharge := entryNew
          newAccountStateRefill := entryRefill
          executionCharge := entryExecution
          stateCharge := entryState
          stateRefill := entryStateRefill }
      deposit :=
        { executionCharge := depositExecution
          stateCharge := depositState }
      executionCharge := execution
      stateCharge := state
      stateRefill := stateRefill
      rollback := rollback }

private def callInput (access : AccessStatus) (delegated : Option AccessStatus)
    (target : AccountStatus) (value : Nat) : CallSituation :=
  { codeAccess := access, delegatedTargetAccess := delegated, stateTarget := target, value := value }

private def callCodeInput (access : AccessStatus) (delegated : Option AccessStatus)
    (executingAccount : AccountStatus) (value : Nat) : CallCodeSituation :=
  { codeAccess := access, delegatedTargetAccess := delegated, executingAccount, value }

private def createInput (destination : AccountStatus) (collision preChecksPassed : Bool)
    (initCodeLength runtimeCodeLength : Nat)
    (childSucceeded runtimeCodeValid depositSucceeded : Bool) : CreateSituation :=
  { destination, collision, preChecksPassed, initCodeLength, runtimeCodeLength,
    childSucceeded, runtimeCodeValid, depositSucceeded }

private def selfDestructInput (access : AccessStatus)
    (sourceBalancePositive : Bool) (beneficiary : AccountStatus) : SelfDestructSituation :=
  { beneficiaryAccess := access, sourceBalancePositive, beneficiary }

private def noDelegate : Option AccessStatus := none

def all (schedule : AccountGasSchedule) : List PricingVector :=
  [ .operation "balance-cold"
      (.balance .cold)
      (literalAccount schedule.base.coldAccountAccess 0 0 0 0
        schedule.base.coldAccountAccess 0 0)
  , .operation "balance-warm"
      (.balance .warm)
      (literalAccount schedule.base.warmAccess 0 0 0 0 schedule.base.warmAccess 0 0)
  , .operation "extcodehash-cold"
      (.extcodehash .cold)
      (literalAccount schedule.base.coldAccountAccess 0 0 0 0
        schedule.base.coldAccountAccess 0 0)
  , .operation "extcodehash-warm"
      (.extcodehash .warm)
      (literalAccount schedule.base.warmAccess 0 0 0 0 schedule.base.warmAccess 0 0)
  , .operation "extcodesize-cold-second-warm"
      (.extcodesize .cold)
      (literalAccount schedule.base.coldAccountAccess 0 0 0 schedule.base.warmAccess
        (schedule.base.coldAccountAccess + schedule.base.warmAccess) 0 0)
  , .operation "extcodesize-warm-second-warm"
      (.extcodesize .warm)
      (literalAccount schedule.base.warmAccess 0 0 0 schedule.base.warmAccess
        (schedule.base.warmAccess + schedule.base.warmAccess) 0 0)
  , .operation "extcodecopy-cold-second-warm"
      (.extcodecopy .cold)
      (literalAccount schedule.base.coldAccountAccess 0 0 0 schedule.base.warmAccess
        (schedule.base.coldAccountAccess + schedule.base.warmAccess) 0 0)
  , .operation "extcodecopy-warm-second-warm"
      (.extcodecopy .warm)
      (literalAccount schedule.base.warmAccess 0 0 0 schedule.base.warmAccess
        (schedule.base.warmAccess + schedule.base.warmAccess) 0 0)
  , .operation "call-zero-cold-dead-target-no-state"
      (.call (callInput .cold noDelegate .dead 0))
      (literalAccount schedule.base.coldAccountAccess 0 0 0 0
        schedule.base.coldAccountAccess 0 0)
  , .operation "call-zero-warm-dead-target-no-state"
      (.call (callInput .warm noDelegate .dead 0))
      (literalAccount schedule.base.warmAccess 0 0 0 0 schedule.base.warmAccess 0 0)
  , .operation "call-value-existent-cold"
      (.call (callInput .cold noDelegate .existent 1))
      (literalAccount schedule.base.coldAccountAccess 0 schedule.base.accountWrite
        schedule.callStipend 0
        (schedule.base.coldAccountAccess + schedule.base.accountWrite + schedule.callStipend)
        0 0)
  , .operation "call-value-existent-warm"
      (.call (callInput .warm noDelegate .existent 1))
      (literalAccount schedule.base.warmAccess 0 schedule.base.accountWrite
        schedule.callStipend 0
        (schedule.base.warmAccess + schedule.base.accountWrite + schedule.callStipend)
        0 0)
  , .operation "call-value-dead-cold-new-account"
      (.call (callInput .cold noDelegate .dead 1))
      (literalAccount schedule.base.coldAccountAccess 0 schedule.base.accountWrite
        schedule.callStipend 0
        (schedule.base.coldAccountAccess + schedule.base.accountWrite + schedule.callStipend)
        schedule.base.newAccountGas 0)
  , .operation "call-value-dead-warm-new-account"
      (.call (callInput .warm noDelegate .dead 1))
      (literalAccount schedule.base.warmAccess 0 schedule.base.accountWrite
        schedule.callStipend 0
        (schedule.base.warmAccess + schedule.base.accountWrite + schedule.callStipend)
        schedule.base.newAccountGas 0)
  , .operation "call-value-existent-cold-delegated-cold"
      (.call (callInput .cold (some .cold) .existent 1))
      (literalAccount schedule.base.coldAccountAccess schedule.base.coldAccountAccess
        schedule.base.accountWrite schedule.callStipend 0
        (schedule.base.coldAccountAccess + schedule.base.coldAccountAccess +
          schedule.base.accountWrite + schedule.callStipend) 0 0)
  , .operation "call-value-existent-warm-delegated-warm"
      (.call (callInput .warm (some .warm) .existent 1))
      (literalAccount schedule.base.warmAccess schedule.base.warmAccess
        schedule.base.accountWrite schedule.callStipend 0
        (schedule.base.warmAccess + schedule.base.warmAccess +
          schedule.base.accountWrite + schedule.callStipend) 0 0)
  , .operation "callcode-zero-cold-executing-account"
      (.callcode (callCodeInput .cold noDelegate .existent 0))
      (literalAccount schedule.base.coldAccountAccess 0 0 0 0
        schedule.base.coldAccountAccess 0 0)
  , .operation "callcode-zero-warm-executing-account"
      (.callcode (callCodeInput .warm noDelegate .existent 0))
      (literalAccount schedule.base.warmAccess 0 0 0 0 schedule.base.warmAccess 0 0)
  , .operation "callcode-value-cold-account-write-no-state"
      (.callcode (callCodeInput .cold noDelegate .existent 1))
      (literalAccount schedule.base.coldAccountAccess 0 schedule.base.accountWrite
        schedule.callStipend 0
        (schedule.base.coldAccountAccess + schedule.base.accountWrite + schedule.callStipend)
        0 0)
  , .operation "callcode-value-warm-account-write-no-state"
      (.callcode (callCodeInput .warm noDelegate .existent 1))
      (literalAccount schedule.base.warmAccess 0 schedule.base.accountWrite
        schedule.callStipend 0
        (schedule.base.warmAccess + schedule.base.accountWrite + schedule.callStipend)
        0 0)
  , .operation "callcode-value-cold-delegated-cold-no-state"
      (.callcode (callCodeInput .cold (some .cold) .existent 1))
      (literalAccount schedule.base.coldAccountAccess schedule.base.coldAccountAccess
        schedule.base.accountWrite schedule.callStipend 0
        (schedule.base.coldAccountAccess + schedule.base.coldAccountAccess +
          schedule.base.accountWrite + schedule.callStipend) 0 0)
  , .operation "callcode-value-warm-delegated-warm-no-state"
      (.callcode (callCodeInput .warm (some .warm) .existent 1))
      (literalAccount schedule.base.warmAccess schedule.base.warmAccess
        schedule.base.accountWrite schedule.callStipend 0
        (schedule.base.warmAccess + schedule.base.warmAccess +
          schedule.base.accountWrite + schedule.callStipend) 0 0)
  , .operation "delegatecall-cold-ignores-environment-value"
      (.delegatecall (callInput .cold noDelegate .dead 1))
      (literalAccount schedule.base.coldAccountAccess 0 0 0 0
        schedule.base.coldAccountAccess 0 0)
  , .operation "delegatecall-warm-ignores-environment-value"
      (.delegatecall (callInput .warm noDelegate .dead 1))
      (literalAccount schedule.base.warmAccess 0 0 0 0 schedule.base.warmAccess 0 0)
  , .operation "delegatecall-cold-delegated-cold"
      (.delegatecall (callInput .cold (some .cold) .dead 1))
      (literalAccount schedule.base.coldAccountAccess schedule.base.coldAccountAccess 0 0 0
        (schedule.base.coldAccountAccess + schedule.base.coldAccountAccess) 0 0)
  , .operation "delegatecall-warm-delegated-warm"
      (.delegatecall (callInput .warm (some .warm) .dead 1))
      (literalAccount schedule.base.warmAccess schedule.base.warmAccess 0 0 0
        (schedule.base.warmAccess + schedule.base.warmAccess) 0 0)
  , .operation "staticcall-cold-no-write"
      (.staticcall (callInput .cold noDelegate .dead 1))
      (literalAccount schedule.base.coldAccountAccess 0 0 0 0
        schedule.base.coldAccountAccess 0 0)
  , .operation "staticcall-warm-no-write"
      (.staticcall (callInput .warm noDelegate .dead 1))
      (literalAccount schedule.base.warmAccess 0 0 0 0 schedule.base.warmAccess 0 0)
  , .operation "staticcall-cold-delegated-cold"
      (.staticcall (callInput .cold (some .cold) .dead 1))
      (literalAccount schedule.base.coldAccountAccess schedule.base.coldAccountAccess 0 0 0
        (schedule.base.coldAccountAccess + schedule.base.coldAccountAccess) 0 0)
  , .operation "staticcall-warm-delegated-warm"
      (.staticcall (callInput .warm (some .warm) .dead 1))
      (literalAccount schedule.base.warmAccess schedule.base.warmAccess 0 0 0
        (schedule.base.warmAccess + schedule.base.warmAccess) 0 0)
  , .operation "create-existing-empty-success-boundary-zero"
      (.create (createInput .existent false true 0 0 true true true))
      (literalCreate schedule.base.createAccess 0 0 0 0 schedule.base.createAccess 0 0
        0 0 schedule.base.createAccess 0 0 .none)
  , .operation "create-existing-one-byte-success-boundary-one"
      (.create (createInput .existent false true 1 1 true true true))
      (literalCreate schedule.base.createAccess schedule.initCodeWordCost 0 0 0
        (schedule.base.createAccess + schedule.initCodeWordCost) 0 0
        schedule.codeDepositExecutionWordCost schedule.base.cpsb
        (schedule.base.createAccess + schedule.initCodeWordCost +
          schedule.codeDepositExecutionWordCost) (schedule.base.cpsb) 0 .none)
  , .operation "create-existing-collision-boundary-32"
      (.create (createInput .existent true true 32 32 true true true))
      (literalCreate schedule.base.createAccess (schedule.initCodeWordCost) 0 0 0
        (schedule.base.createAccess + schedule.initCodeWordCost) 0 0
        0 0 (schedule.base.createAccess + schedule.initCodeWordCost) 0 0 .none)
  , .operation "create-dead-success-boundary-33"
      (.create (createInput .dead false true 33 33 true true true))
      (literalCreate schedule.base.createAccess (schedule.initCodeWordCost + schedule.initCodeWordCost)
        0 schedule.base.newAccountGas 0
        (schedule.base.createAccess + schedule.initCodeWordCost + schedule.initCodeWordCost)
        schedule.base.newAccountGas 0
        (schedule.codeDepositExecutionWordCost + schedule.codeDepositExecutionWordCost)
        (33 * schedule.base.cpsb)
        (schedule.base.createAccess + schedule.initCodeWordCost + schedule.initCodeWordCost +
          schedule.codeDepositExecutionWordCost + schedule.codeDepositExecutionWordCost)
        (schedule.base.newAccountGas + 33 * schedule.base.cpsb) 0 .none)
  , .operation "create-dead-collision-refills-new-account"
      (.create (createInput .dead true true 33 33 true true true))
      (literalCreate schedule.base.createAccess (schedule.initCodeWordCost + schedule.initCodeWordCost)
        0 schedule.base.newAccountGas schedule.base.newAccountGas
        (schedule.base.createAccess + schedule.initCodeWordCost + schedule.initCodeWordCost)
        schedule.base.newAccountGas schedule.base.newAccountGas
        0 0 (schedule.base.createAccess + schedule.initCodeWordCost + schedule.initCodeWordCost)
        schedule.base.newAccountGas schedule.base.newAccountGas .collision)
  , .operation "create-dead-child-revert-or-exception"
      (.create (createInput .dead false true 33 33 false true true))
      (literalCreate schedule.base.createAccess (schedule.initCodeWordCost + schedule.initCodeWordCost)
        0 schedule.base.newAccountGas schedule.base.newAccountGas
        (schedule.base.createAccess + schedule.initCodeWordCost + schedule.initCodeWordCost)
        schedule.base.newAccountGas schedule.base.newAccountGas
        0 0 (schedule.base.createAccess + schedule.initCodeWordCost + schedule.initCodeWordCost)
        schedule.base.newAccountGas schedule.base.newAccountGas .childFailure)
  , .operation "create-dead-invalid-or-oversize-runtime"
      (.create (createInput .dead false true 33 33 true false true))
      (literalCreate schedule.base.createAccess (schedule.initCodeWordCost + schedule.initCodeWordCost)
        0 schedule.base.newAccountGas schedule.base.newAccountGas
        (schedule.base.createAccess + schedule.initCodeWordCost + schedule.initCodeWordCost)
        schedule.base.newAccountGas schedule.base.newAccountGas
        0 0 (schedule.base.createAccess + schedule.initCodeWordCost + schedule.initCodeWordCost)
        schedule.base.newAccountGas schedule.base.newAccountGas .runtimeValidationFailure)
  , .operation "create-dead-deposit-oog"
      (.create (createInput .dead false true 33 33 true true false))
      (literalCreate schedule.base.createAccess (schedule.initCodeWordCost + schedule.initCodeWordCost)
        0 schedule.base.newAccountGas schedule.base.newAccountGas
        (schedule.base.createAccess + schedule.initCodeWordCost + schedule.initCodeWordCost)
        schedule.base.newAccountGas schedule.base.newAccountGas
        0 0 (schedule.base.createAccess + schedule.initCodeWordCost + schedule.initCodeWordCost)
        schedule.base.newAccountGas schedule.base.newAccountGas .depositFailure)
  , .operation "create-precheck-failure-no-destination-charge"
      (.create (createInput .dead true false 33 33 true true true))
      (literalCreate schedule.base.createAccess (schedule.initCodeWordCost + schedule.initCodeWordCost)
        0 0 0 (schedule.base.createAccess + schedule.initCodeWordCost + schedule.initCodeWordCost)
        0 0 0 0 (schedule.base.createAccess + schedule.initCodeWordCost + schedule.initCodeWordCost)
        0 0 .none)
  , .operation "create2-existing-empty-success-boundary-zero"
      (.create2 (createInput .existent false true 0 0 true true true))
      (literalCreate schedule.base.createAccess 0 0 0 0 schedule.base.createAccess 0 0
        0 0 schedule.base.createAccess 0 0 .none)
  , .operation "create2-existing-one-byte-success-boundary-one"
      (.create2 (createInput .existent false true 1 1 true true true))
      (literalCreate schedule.base.createAccess schedule.initCodeWordCost schedule.create2HashWordCost 0 0
        (schedule.base.createAccess + schedule.initCodeWordCost + schedule.create2HashWordCost) 0 0
        schedule.codeDepositExecutionWordCost schedule.base.cpsb
        (schedule.base.createAccess + schedule.initCodeWordCost + schedule.create2HashWordCost +
          schedule.codeDepositExecutionWordCost) schedule.base.cpsb 0 .none)
  , .operation "create2-existing-collision-boundary-32"
      (.create2 (createInput .existent true true 32 32 true true true))
      (literalCreate schedule.base.createAccess schedule.initCodeWordCost schedule.create2HashWordCost 0 0
        (schedule.base.createAccess + schedule.initCodeWordCost + schedule.create2HashWordCost) 0 0
        0 0 (schedule.base.createAccess + schedule.initCodeWordCost + schedule.create2HashWordCost)
        0 0 .none)
  , .operation "create2-dead-success-boundary-33"
      (.create2 (createInput .dead false true 33 33 true true true))
      (literalCreate schedule.base.createAccess
        (schedule.initCodeWordCost + schedule.initCodeWordCost)
        (schedule.create2HashWordCost + schedule.create2HashWordCost)
        schedule.base.newAccountGas 0
        (schedule.base.createAccess + schedule.initCodeWordCost + schedule.initCodeWordCost +
          schedule.create2HashWordCost + schedule.create2HashWordCost)
        schedule.base.newAccountGas 0
        (schedule.codeDepositExecutionWordCost + schedule.codeDepositExecutionWordCost)
        (33 * schedule.base.cpsb)
        (schedule.base.createAccess + schedule.initCodeWordCost + schedule.initCodeWordCost +
          schedule.create2HashWordCost + schedule.create2HashWordCost +
          schedule.codeDepositExecutionWordCost + schedule.codeDepositExecutionWordCost)
        (schedule.base.newAccountGas + 33 * schedule.base.cpsb) 0 .none)
  , .operation "create2-dead-collision-refills-new-account"
      (.create2 (createInput .dead true true 33 33 true true true))
      (literalCreate schedule.base.createAccess
        (schedule.initCodeWordCost + schedule.initCodeWordCost)
        (schedule.create2HashWordCost + schedule.create2HashWordCost)
        schedule.base.newAccountGas schedule.base.newAccountGas
        (schedule.base.createAccess + schedule.initCodeWordCost + schedule.initCodeWordCost +
          schedule.create2HashWordCost + schedule.create2HashWordCost)
        schedule.base.newAccountGas schedule.base.newAccountGas 0 0
        (schedule.base.createAccess + schedule.initCodeWordCost + schedule.initCodeWordCost +
          schedule.create2HashWordCost + schedule.create2HashWordCost)
        schedule.base.newAccountGas schedule.base.newAccountGas .collision)
  , .operation "create2-dead-child-revert-or-exception"
      (.create2 (createInput .dead false true 33 33 false true true))
      (literalCreate schedule.base.createAccess
        (schedule.initCodeWordCost + schedule.initCodeWordCost)
        (schedule.create2HashWordCost + schedule.create2HashWordCost)
        schedule.base.newAccountGas schedule.base.newAccountGas
        (schedule.base.createAccess + schedule.initCodeWordCost + schedule.initCodeWordCost +
          schedule.create2HashWordCost + schedule.create2HashWordCost)
        schedule.base.newAccountGas schedule.base.newAccountGas 0 0
        (schedule.base.createAccess + schedule.initCodeWordCost + schedule.initCodeWordCost +
          schedule.create2HashWordCost + schedule.create2HashWordCost)
        schedule.base.newAccountGas schedule.base.newAccountGas .childFailure)
  , .operation "create2-dead-invalid-or-oversize-runtime"
      (.create2 (createInput .dead false true 33 33 true false true))
      (literalCreate schedule.base.createAccess
        (schedule.initCodeWordCost + schedule.initCodeWordCost)
        (schedule.create2HashWordCost + schedule.create2HashWordCost)
        schedule.base.newAccountGas schedule.base.newAccountGas
        (schedule.base.createAccess + schedule.initCodeWordCost + schedule.initCodeWordCost +
          schedule.create2HashWordCost + schedule.create2HashWordCost)
        schedule.base.newAccountGas schedule.base.newAccountGas 0 0
        (schedule.base.createAccess + schedule.initCodeWordCost + schedule.initCodeWordCost +
          schedule.create2HashWordCost + schedule.create2HashWordCost)
        schedule.base.newAccountGas schedule.base.newAccountGas .runtimeValidationFailure)
  , .operation "create2-dead-deposit-oog"
      (.create2 (createInput .dead false true 33 33 true true false))
      (literalCreate schedule.base.createAccess
        (schedule.initCodeWordCost + schedule.initCodeWordCost)
        (schedule.create2HashWordCost + schedule.create2HashWordCost)
        schedule.base.newAccountGas schedule.base.newAccountGas
        (schedule.base.createAccess + schedule.initCodeWordCost + schedule.initCodeWordCost +
          schedule.create2HashWordCost + schedule.create2HashWordCost)
        schedule.base.newAccountGas schedule.base.newAccountGas 0 0
        (schedule.base.createAccess + schedule.initCodeWordCost + schedule.initCodeWordCost +
          schedule.create2HashWordCost + schedule.create2HashWordCost)
        schedule.base.newAccountGas schedule.base.newAccountGas .depositFailure)
  , .operation "create2-precheck-failure-no-destination-charge"
      (.create2 (createInput .dead true false 33 33 true true true))
      (literalCreate schedule.base.createAccess
        (schedule.initCodeWordCost + schedule.initCodeWordCost)
        (schedule.create2HashWordCost + schedule.create2HashWordCost)
        0 0
        (schedule.base.createAccess + schedule.initCodeWordCost + schedule.initCodeWordCost +
          schedule.create2HashWordCost + schedule.create2HashWordCost)
        0 0 0 0
        (schedule.base.createAccess + schedule.initCodeWordCost + schedule.initCodeWordCost +
          schedule.create2HashWordCost + schedule.create2HashWordCost) 0 0 .none)
  , .operation "selfdestruct-cold-zero-dead-no-write"
      (.selfdestruct (selfDestructInput .cold false .dead))
      (literalAccount schedule.base.coldAccountAccess 0 0 0 0
        schedule.base.coldAccountAccess 0 0)
  , .operation "selfdestruct-warm-zero-existent-no-write"
      (.selfdestruct (selfDestructInput .warm false .existent))
      (literalAccount schedule.base.warmAccess 0 0 0 0 schedule.base.warmAccess 0 0)
  , .operation "selfdestruct-cold-positive-existent-no-write"
      (.selfdestruct (selfDestructInput .cold true .existent))
      (literalAccount schedule.base.coldAccountAccess 0 0 0 0
        schedule.base.coldAccountAccess 0 0)
  , .operation "selfdestruct-warm-positive-existent-no-write"
      (.selfdestruct (selfDestructInput .warm true .existent))
      (literalAccount schedule.base.warmAccess 0 0 0 0 schedule.base.warmAccess 0 0)
  , .operation "selfdestruct-cold-positive-dead-write-and-state"
      (.selfdestruct (selfDestructInput .cold true .dead))
      (literalAccount schedule.base.coldAccountAccess 0 schedule.base.accountWrite 0 0
        (schedule.base.coldAccountAccess + schedule.base.accountWrite)
        schedule.base.newAccountGas 0)
  , .operation "selfdestruct-warm-positive-dead-write-and-state"
      (.selfdestruct (selfDestructInput .warm true .dead))
      (literalAccount schedule.base.warmAccess 0 schedule.base.accountWrite 0 0
        (schedule.base.warmAccess + schedule.base.accountWrite)
        schedule.base.newAccountGas 0)
  , .accessListAddress "access-list-address" schedule.base.accessListAddress
  , .accessListStorageKey "access-list-storage-key" schedule.base.accessListStorageKey
  ]

def passes (schedule : AccountGasSchedule) : PricingVector → Bool
  | .operation _ input expected => decide (price schedule input = expected)
  | .accessListAddress _ expected => decide (accessListAddressPrice schedule = expected)
  | .accessListStorageKey _ expected => decide (accessListStorageKeyPrice schedule = expected)

def pinned : List PricingVector := all AccountGasSchedule.amsterdam

theorem all_pass : (pinned.map (passes AccountGasSchedule.amsterdam)).all (fun result => result) = true := by
  native_decide

theorem vector_count : pinned.length = 56 := by
  native_decide

private def isOperation : PricingVector → Bool
  | .operation _ _ _ => true
  | _ => false

theorem operation_vector_count : (pinned.filter isOperation).length = 54 := by
  native_decide

private def omitSecondRead (schedule : AccountGasSchedule)
    (input : AccountOperation) : OperationPrice :=
  match price schedule input with
  | .account effect =>
      .account { effect with
        secondReadCharge := 0
        executionCharge := effect.executionCharge - effect.secondReadCharge }
  | .create effect => .create effect

private def omitDelegatedTarget (schedule : AccountGasSchedule)
    (input : AccountOperation) : OperationPrice :=
  match price schedule input with
  | .account effect =>
      .account { effect with
        delegatedTargetAccessCharge := 0
        executionCharge := effect.executionCharge - effect.delegatedTargetAccessCharge }
  | .create effect => .create effect

private def omitAccountWrite (schedule : AccountGasSchedule)
    (input : AccountOperation) : OperationPrice :=
  match price schedule input with
  | .account effect =>
      .account { effect with
        accountWriteCharge := 0
        executionCharge := effect.executionCharge - effect.accountWriteCharge }
  | .create effect => .create effect

private def omitNewAccountState (schedule : AccountGasSchedule)
    (input : AccountOperation) : OperationPrice :=
  match price schedule input with
  | .account effect => .account { effect with stateCharge := 0 }
  | .create effect => .create effect

private def omitCreate2Hash (schedule : AccountGasSchedule)
    (input : AccountOperation) : OperationPrice :=
  match price schedule input with
  | .account effect => .account effect
  | .create effect =>
      .create { effect with
        entry := { effect.entry with
          create2HashCharge := 0
          executionCharge := effect.entry.executionCharge - effect.entry.create2HashCharge }
        executionCharge := effect.executionCharge - effect.entry.create2HashCharge }

private def omitCreateRollback (schedule : AccountGasSchedule)
    (input : AccountOperation) : OperationPrice :=
  match price schedule input with
  | .account effect => .account effect
  | .create effect =>
      .create { effect with
        entry := { effect.entry with newAccountStateRefill := 0, stateRefill := 0 }
        stateRefill := 0
        rollback := .none }

private def forceCreateDeposit (schedule : AccountGasSchedule)
    (input : AccountOperation) : OperationPrice :=
  match input, price schedule input with
  | .create situation, .create effect =>
      let execution := runtimeCodeWords situation.runtimeCodeLength *
        schedule.codeDepositExecutionWordCost
      let state := situation.runtimeCodeLength * schedule.base.cpsb
      .create { effect with
        deposit := { executionCharge := execution, stateCharge := state }
        executionCharge := effect.executionCharge - effect.deposit.executionCharge + execution
        stateCharge := effect.stateCharge - effect.deposit.stateCharge + state }
  | .create2 situation, .create effect =>
      let execution := runtimeCodeWords situation.runtimeCodeLength *
        schedule.codeDepositExecutionWordCost
      let state := situation.runtimeCodeLength * schedule.base.cpsb
      .create { effect with
        deposit := { executionCharge := execution, stateCharge := state }
        executionCharge := effect.executionCharge - effect.deposit.executionCharge + execution
        stateCharge := effect.stateCharge - effect.deposit.stateCharge + state }
  | _, result => result

private def mutationFails
    (mutated : AccountGasSchedule → AccountOperation → OperationPrice)
    (vector : PricingVector) : Bool :=
  match vector with
  | .operation _ input expected => mutated AccountGasSchedule.amsterdam input != expected
  | _ => false

private def mutationFailsKind
    (mutated : AccountGasSchedule → AccountOperation → OperationPrice)
    (kind : AccountOperationKind) : Bool :=
  pinned.any (fun vector =>
    match vector with
    | .operation _ input expected =>
        AccountOperation.kind input = kind ∧
          mutated AccountGasSchedule.amsterdam input != expected
    | _ => false)

theorem mutation_checks :
    pinned.any (mutationFails omitSecondRead) = true ∧
    pinned.any (mutationFails omitAccountWrite) = true ∧
    pinned.any (mutationFails omitNewAccountState) = true ∧
    pinned.any (mutationFails omitCreate2Hash) = true ∧
    pinned.any (mutationFails omitCreateRollback) = true ∧
    pinned.any (mutationFails omitDelegatedTarget) = true ∧
    pinned.any (mutationFails forceCreateDeposit) = true := by
  native_decide

theorem mutation_checks_per_call_family :
    mutationFailsKind omitDelegatedTarget .call = true ∧
    mutationFailsKind omitDelegatedTarget .callcode = true ∧
    mutationFailsKind omitDelegatedTarget .delegatecall = true ∧
    mutationFailsKind omitDelegatedTarget .staticcall = true := by
  native_decide

private def mutationFailsCreateDepositCase (case : CreateDepositCase) : Bool :=
  pinned.any (fun vector =>
    match vector with
    | .operation _ (.create input) expected =>
        CreateDepositCase.classify input = case ∧
          forceCreateDeposit AccountGasSchedule.amsterdam (.create input) != expected
    | .operation _ (.create2 input) expected =>
        CreateDepositCase.classify input = case ∧
          forceCreateDeposit AccountGasSchedule.amsterdam (.create2 input) != expected
    | _ => false)

private def mutationFailsRollbackCase (case : CreateStateRollback) : Bool :=
  pinned.any (fun vector =>
    match vector with
    | .operation _ (.create input) expected =>
        stateRollback input = case ∧
          omitCreateRollback AccountGasSchedule.amsterdam (.create input) != expected
    | .operation _ (.create2 input) expected =>
        stateRollback input = case ∧
          omitCreateRollback AccountGasSchedule.amsterdam (.create2 input) != expected
    | _ => false)

theorem mutation_checks_per_create_failure_branch :
    mutationFailsCreateDepositCase .entryNotReached = true ∧
    mutationFailsCreateDepositCase .collision = true ∧
    mutationFailsCreateDepositCase .childFailure = true ∧
    mutationFailsCreateDepositCase .runtimeValidationFailure = true ∧
    mutationFailsCreateDepositCase .depositFailure = true ∧
    mutationFailsRollbackCase .collision = true ∧
    mutationFailsRollbackCase .childFailure = true ∧
    mutationFailsRollbackCase .runtimeValidationFailure = true ∧
    mutationFailsRollbackCase .depositFailure = true := by
  native_decide

theorem mutation_checks_per_write_family :
    mutationFailsKind omitAccountWrite .call = true ∧
    mutationFailsKind omitAccountWrite .callcode = true ∧
    mutationFailsKind omitAccountWrite .selfdestruct = true ∧
    mutationFailsKind omitNewAccountState .call = true ∧
    mutationFailsKind omitNewAccountState .selfdestruct = true ∧
    mutationFailsKind omitSecondRead .extcodesize = true ∧
    mutationFailsKind omitSecondRead .extcodecopy = true := by
  native_decide

private def operationVectorKinds : List AccountOperationKind :=
  [.balance, .extcodehash, .extcodesize, .extcodecopy, .call, .callcode,
    .delegatecall, .staticcall, .create, .create2, .selfdestruct]

private def vectorCoversKind (kind : AccountOperationKind) : Bool :=
  pinned.any (fun vector =>
    match vector with
    | .operation _ input _ => AccountOperation.kind input = kind
    | _ => false)

theorem every_operation_has_boundary_vector : operationVectorKinds.all vectorCoversKind = true := by
  native_decide

private def callCases : List CallCase :=
  [.noValue, .valueToExistent, .valueToDead]

private def vectorCoversCallCase (case : CallCase) : Bool :=
  pinned.any (fun vector =>
    match vector with
    | .operation _ (.call input) _ => CallCase.classify input = case
    | _ => false)

private def createCases : List CreateCase :=
  [.precheckFailure, .existingNoCollision, .existingCollision, .deadNoCollision,
    .deadCollision]

private def vectorCoversCreateCase (kind : AccountOperationKind)
    (case : CreateCase) : Bool :=
  pinned.any (fun vector =>
    match vector with
    | .operation _ (.create input) _ =>
        kind = .create ∧ CreateCase.classify input = case
    | .operation _ (.create2 input) _ =>
        kind = .create2 ∧ CreateCase.classify input = case
    | _ => false)

private def createDepositCases : List CreateDepositCase :=
  [.entryNotReached, .collision, .childFailure, .runtimeValidationFailure,
    .depositFailure, .committed]

private def vectorCoversCreateDepositCase (case : CreateDepositCase) : Bool :=
  pinned.any (fun vector =>
    match vector with
    | .operation _ (.create input) _ => CreateDepositCase.classify input = case
    | .operation _ (.create2 input) _ => CreateDepositCase.classify input = case
    | _ => false)

private def selfDestructCases : List SelfDestructCase :=
  [.noWrite, .positiveBalanceToDead]

private def vectorCoversSelfDestructCase (case : SelfDestructCase) : Bool :=
  pinned.any (fun vector =>
    match vector with
    | .operation _ (.selfdestruct input) _ => SelfDestructCase.classify input = case
    | _ => false)

theorem every_call_case_has_vector : callCases.all vectorCoversCallCase = true := by
  native_decide

theorem every_create_case_has_create_and_create2_vector :
    (createCases.all (vectorCoversCreateCase .create) = true) ∧
    (createCases.all (vectorCoversCreateCase .create2) = true) := by
  native_decide

theorem every_create_deposit_case_has_vector :
    createDepositCases.all vectorCoversCreateDepositCase = true := by
  native_decide

theorem every_selfdestruct_case_has_vector :
    selfDestructCases.all vectorCoversSelfDestructCase = true := by
  native_decide

end AccountPricing
end Eip803x
