-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import Eip803x.SStore
import Lean.Elab.Tactic.Grind

namespace Eip803x

namespace AccountPricing

/-!
  Handwritten executable reference for the account-operation rows in the pinned
  EIP-8038 table (`8331fb3eed0a5366b28b25a016f1ad04fac0fa8e`), with the
  state-creation components from pinned EIP-8037
  (`052029f3625328d6f51dec8e62a7090201e66f17`).  It deliberately exposes the
  table's components instead of trying to model the EVM instruction or
  transaction machinery.

  `CallSituation.stateTarget` is the account whose balance/nonce/code state is
  changed.  `CallCodeSituation` has a separate `executingAccount` field: the
  code-access account is independent, but CALLCODE never targets an arbitrary
  recipient for state creation.  Optional delegated-target access is the EIP-
  7702 second account read; `none` means no delegation was followed, while
  `some .cold`/`some .warm` records the caller-supplied status after any prior
  warming.  The model prices one operation; the warming side effect is not
  threaded into a later operation.  This leaf assumes the access point is
  reached and does not model delegated-target OOG, warming rollback, frame
  rollback, or the state reservoir.

  CREATE entry/access/collision pricing is separate from committed code
  deposit pricing.  The latter is present only after child success, runtime
  validation, and deposit success.  `CreateStateRollback` makes the responsible
  state-charge rollback reason explicit for a dead destination.  A false
  `depositSucceeded` denotes the pinned standard's exceptional deposit-OOG
  path; legacy soft-deposit behavior is outside this leaf.

  Legacy opcode base costs, memory expansion, and forwarding are intentionally
  outside this component table.  SELFDESTRUCT's legacy base charge is likewise
  outside it; its account-access component remains warm-priced by
  `warmAccess`.
-/

structure AccountGasSchedule where
  base : GasSchedule
  callStipend : Nat
  initCodeWordCost : Nat
  create2HashWordCost : Nat
  codeDepositExecutionWordCost : Nat
  deriving DecidableEq, Repr

namespace AccountGasSchedule

/-- The pinned Amsterdam constants used by the independent vectors. -/
def amsterdam : AccountGasSchedule where
  base := GasSchedule.amsterdam
  callStipend := 2300
  initCodeWordCost := 2
  create2HashWordCost := 6
  codeDepositExecutionWordCost := 6

end AccountGasSchedule

inductive AccountStatus where
  | dead
  | existent
  deriving DecidableEq, Repr

structure CallSituation where
  /-- Warm/cold status of the account whose code is read. -/
  codeAccess : AccessStatus
  /-- Optional EIP-7702 delegated-code target account access. -/
  delegatedTargetAccess : Option AccessStatus
  /-- Account that receives the value and can incur state creation. -/
  stateTarget : AccountStatus
  value : Nat
  deriving DecidableEq, Repr

structure CallCodeSituation where
  /-- Warm/cold status of the account whose code is read. -/
  codeAccess : AccessStatus
  /-- Optional EIP-7702 delegated-code target account access. -/
  delegatedTargetAccess : Option AccessStatus
  /-- The executing account is the CALLCODE value/state target. -/
  executingAccount : AccountStatus
  value : Nat
  deriving DecidableEq, Repr

namespace CallCodeSituation

-- A well-formed EVM frame always has an existing executing account.
def Valid (input : CallCodeSituation) : Prop :=
  input.executingAccount = .existent

end CallCodeSituation

structure SelfDestructSituation where
  beneficiaryAccess : AccessStatus
  sourceBalancePositive : Bool
  beneficiary : AccountStatus
  deriving DecidableEq, Repr

structure CreateSituation where
  destination : AccountStatus
  /-- A nonce/code collision is checked after the destination state charge. -/
  collision : Bool
  /-- Balance, nonce, and depth checks have passed and destination was read. -/
  preChecksPassed : Bool
  initCodeLength : Nat
  runtimeCodeLength : Nat
  /-- Whether initcode execution returned normally rather than revert/exception. -/
  childSucceeded : Bool
  /-- Whether the returned runtime code passed validity and size checks. -/
  runtimeCodeValid : Bool
  /-- Whether the later code-deposit step had sufficient gas and committed. -/
  depositSucceeded : Bool
  deriving DecidableEq, Repr

structure AccountGasEffect where
  accessCharge : Nat
  delegatedTargetAccessCharge : Nat
  accountWriteCharge : Nat
  callStipendCharge : Nat
  secondReadCharge : Nat
  executionCharge : Nat
  stateCharge : Nat
  stateRefill : Nat
  deriving DecidableEq, Repr

structure CreateEntryGasEffect where
  createAccessCharge : Nat
  initCodeCharge : Nat
  create2HashCharge : Nat
  newAccountStateCharge : Nat
  newAccountStateRefill : Nat
  executionCharge : Nat
  stateCharge : Nat
  stateRefill : Nat
  deriving DecidableEq, Repr

structure CodeDepositGasEffect where
  executionCharge : Nat
  stateCharge : Nat
  deriving DecidableEq, Repr

inductive CreateStateRollback where
  | none
  | collision
  | childFailure
  | runtimeValidationFailure
  | depositFailure
  deriving DecidableEq, Repr

structure CreateGasEffect where
  /-- CREATE_ACCESS, initcode/hash, collision, and account-state components. -/
  entry : CreateEntryGasEffect
  /-- Runtime-code deposit components committed only after all success gates. -/
  deposit : CodeDepositGasEffect
  executionCharge : Nat
  stateCharge : Nat
  stateRefill : Nat
  rollback : CreateStateRollback
  deriving DecidableEq, Repr

inductive OperationPrice where
  | account (effect : AccountGasEffect)
  | create (effect : CreateGasEffect)
  deriving DecidableEq, Repr

inductive AccountOperation where
  | balance (access : AccessStatus)
  | extcodehash (access : AccessStatus)
  | extcodesize (access : AccessStatus)
  | extcodecopy (access : AccessStatus)
  | call (input : CallSituation)
  | callcode (input : CallCodeSituation)
  | delegatecall (input : CallSituation)
  | staticcall (input : CallSituation)
  | create (input : CreateSituation)
  | create2 (input : CreateSituation)
  | selfdestruct (input : SelfDestructSituation)
  deriving DecidableEq, Repr

inductive AccountOperationKind where
  | balance
  | extcodehash
  | extcodesize
  | extcodecopy
  | call
  | callcode
  | delegatecall
  | staticcall
  | create
  | create2
  | selfdestruct
  deriving DecidableEq, Repr

namespace AccountOperation

def kind : AccountOperation → AccountOperationKind
  | .balance _ => .balance
  | .extcodehash _ => .extcodehash
  | .extcodesize _ => .extcodesize
  | .extcodecopy _ => .extcodecopy
  | .call _ => .call
  | .callcode _ => .callcode
  | .delegatecall _ => .delegatecall
  | .staticcall _ => .staticcall
  | .create _ => .create
  | .create2 _ => .create2
  | .selfdestruct _ => .selfdestruct

end AccountOperation

def accountAccessCharge (schedule : AccountGasSchedule) : AccessStatus → Nat
  | .cold => schedule.base.coldAccountAccess
  | .warm => schedule.base.warmAccess

def delegatedTargetCharge (schedule : AccountGasSchedule) : Option AccessStatus → Nat
  | none => 0
  | some access => accountAccessCharge schedule access

def accessListAddressPrice (schedule : AccountGasSchedule) : Nat :=
  schedule.base.accessListAddress

def accessListStorageKeyPrice (schedule : AccountGasSchedule) : Nat :=
  schedule.base.accessListStorageKey

def initCodeWords (length : Nat) : Nat :=
  (length + 31) / 32

def runtimeCodeWords (length : Nat) : Nat :=
  (length + 31) / 32

def accountEffect (schedule : AccountGasSchedule) (access : AccessStatus)
    (delegatedTargetAccessCharge accountWriteCharge callStipendCharge secondReadCharge
      stateCharge stateRefill : Nat) : AccountGasEffect :=
  { accessCharge := accountAccessCharge schedule access
    delegatedTargetAccessCharge := delegatedTargetAccessCharge
    accountWriteCharge := accountWriteCharge
    callStipendCharge := callStipendCharge
    secondReadCharge := secondReadCharge
    executionCharge := accountAccessCharge schedule access + delegatedTargetAccessCharge +
      accountWriteCharge + callStipendCharge + secondReadCharge
    stateCharge := stateCharge
    stateRefill := stateRefill }

def readPrice (schedule : AccountGasSchedule) (access : AccessStatus)
    (secondReadCharge : Nat) : AccountGasEffect :=
  accountEffect schedule access 0 0 0 secondReadCharge 0 0

def priceBalance (schedule : AccountGasSchedule) (access : AccessStatus) : AccountGasEffect :=
  readPrice schedule access 0

def priceExtCodeHash (schedule : AccountGasSchedule)
    (access : AccessStatus) : AccountGasEffect :=
  readPrice schedule access 0

def priceExtCodeSize (schedule : AccountGasSchedule)
    (access : AccessStatus) : AccountGasEffect :=
  readPrice schedule access schedule.base.warmAccess

def priceExtCodeCopy (schedule : AccountGasSchedule)
    (access : AccessStatus) : AccountGasEffect :=
  readPrice schedule access schedule.base.warmAccess

def callAccountWrite (schedule : AccountGasSchedule) (value : Nat) : Nat :=
  if value ≠ 0 then schedule.base.accountWrite else 0

def callStipend (schedule : AccountGasSchedule) (value : Nat) : Nat :=
  if value ≠ 0 then schedule.callStipend else 0

def callNewAccountState (schedule : AccountGasSchedule)
    (input : CallSituation) : Nat :=
  if input.value ≠ 0 ∧ input.stateTarget = .dead then schedule.base.newAccountGas else 0

def priceCall (schedule : AccountGasSchedule) (input : CallSituation) : AccountGasEffect :=
  accountEffect schedule input.codeAccess
    (delegatedTargetCharge schedule input.delegatedTargetAccess)
    (callAccountWrite schedule input.value)
    (callStipend schedule input.value)
    0
    (callNewAccountState schedule input)
    0

/-- CALLCODE retains CALL's per-operation value write/stipend, but its state
    target is the executing account and therefore never incurs NEW_ACCOUNT. -/
def priceCallCode (schedule : AccountGasSchedule)
    (input : CallCodeSituation) : AccountGasEffect :=
  accountEffect schedule input.codeAccess
    (delegatedTargetCharge schedule input.delegatedTargetAccess)
    (callAccountWrite schedule input.value)
    (callStipend schedule input.value)
    0
    0
    0

def priceDelegateCall (schedule : AccountGasSchedule)
    (input : CallSituation) : AccountGasEffect :=
  accountEffect schedule input.codeAccess
    (delegatedTargetCharge schedule input.delegatedTargetAccess) 0 0 0 0 0

def priceStaticCall (schedule : AccountGasSchedule)
    (input : CallSituation) : AccountGasEffect :=
  accountEffect schedule input.codeAccess
    (delegatedTargetCharge schedule input.delegatedTargetAccess) 0 0 0 0 0

def selfDestructWrites (schedule : AccountGasSchedule)
    (input : SelfDestructSituation) : Nat :=
  if input.sourceBalancePositive = true ∧ input.beneficiary = .dead then
    schedule.base.accountWrite
  else 0

def selfDestructState (schedule : AccountGasSchedule)
    (input : SelfDestructSituation) : Nat :=
  if input.sourceBalancePositive = true ∧ input.beneficiary = .dead then
    schedule.base.newAccountGas
  else 0

def priceSelfDestruct (schedule : AccountGasSchedule)
    (input : SelfDestructSituation) : AccountGasEffect :=
  accountEffect schedule input.beneficiaryAccess 0
    (selfDestructWrites schedule input) 0 0
    (selfDestructState schedule input) 0

abbrev createEntryReached (input : CreateSituation) : Prop :=
  input.preChecksPassed = true ∧ input.collision = false

abbrev createDepositCommitted (input : CreateSituation) : Prop :=
  createEntryReached input ∧
    input.childSucceeded = true ∧ input.runtimeCodeValid = true ∧
    input.depositSucceeded = true

abbrev createDestinationIsDead (input : CreateSituation) : Prop :=
  input.preChecksPassed = true ∧ input.destination = .dead

abbrev createStateMustRefill (input : CreateSituation) : Prop :=
  createDestinationIsDead input ∧ ¬ createDepositCommitted input

inductive CreateDepositCase where
  | entryNotReached
  | collision
  | childFailure
  | runtimeValidationFailure
  | depositFailure
  | committed
  deriving DecidableEq, Repr

namespace CreateDepositCase

def Matches (input : CreateSituation) : CreateDepositCase → Prop
  | .entryNotReached => input.preChecksPassed = false
  | .collision => input.preChecksPassed = true ∧ input.collision = true
  | .childFailure =>
      input.preChecksPassed = true ∧ input.collision = false ∧ input.childSucceeded = false
  | .runtimeValidationFailure =>
      input.preChecksPassed = true ∧ input.collision = false ∧
        input.childSucceeded = true ∧ input.runtimeCodeValid = false
  | .depositFailure =>
      input.preChecksPassed = true ∧ input.collision = false ∧
        input.childSucceeded = true ∧ input.runtimeCodeValid = true ∧
        input.depositSucceeded = false
  | .committed => createDepositCommitted input

def classify (input : CreateSituation) : CreateDepositCase :=
  if input.preChecksPassed = false then .entryNotReached
  else if input.collision = true then .collision
  else if input.childSucceeded = false then .childFailure
  else if input.runtimeCodeValid = false then .runtimeValidationFailure
  else if input.depositSucceeded = false then .depositFailure
  else .committed

end CreateDepositCase

def stateRollback (input : CreateSituation) : CreateStateRollback :=
  if input.destination = .existent then .none
  else
    match CreateDepositCase.classify input with
    | .entryNotReached => .none
    | .collision => .collision
    | .childFailure => .childFailure
    | .runtimeValidationFailure => .runtimeValidationFailure
    | .depositFailure => .depositFailure
    | .committed => .none

def priceCreateWithHash (schedule : AccountGasSchedule)
    (input : CreateSituation) (create2HashCharge : Nat) : CreateGasEffect :=
  let initCharge := initCodeWords input.initCodeLength * schedule.initCodeWordCost
  let entryExecution := schedule.base.createAccess + initCharge + create2HashCharge
  let codeDepositExecution :=
    if createDepositCommitted input then
      runtimeCodeWords input.runtimeCodeLength * schedule.codeDepositExecutionWordCost
    else 0
  let newAccountCharge :=
    if createDestinationIsDead input then schedule.base.newAccountGas else 0
  let newAccountRefill :=
    if createStateMustRefill input then schedule.base.newAccountGas else 0
  let codeDepositState :=
    if createDepositCommitted input then input.runtimeCodeLength * schedule.base.cpsb else 0
  let entry : CreateEntryGasEffect :=
    { createAccessCharge := schedule.base.createAccess
      initCodeCharge := initCharge
      create2HashCharge := create2HashCharge
      newAccountStateCharge := newAccountCharge
      newAccountStateRefill := newAccountRefill
      executionCharge := entryExecution
      stateCharge := newAccountCharge
      stateRefill := newAccountRefill }
  let deposit : CodeDepositGasEffect :=
    { executionCharge := codeDepositExecution
      stateCharge := codeDepositState }
  { entry := entry
    deposit := deposit
    executionCharge := entryExecution + codeDepositExecution
    stateCharge := newAccountCharge + codeDepositState
    stateRefill := newAccountRefill
    rollback := stateRollback input }

def priceCreate (schedule : AccountGasSchedule)
    (input : CreateSituation) : CreateGasEffect :=
  priceCreateWithHash schedule input 0

def priceCreate2 (schedule : AccountGasSchedule)
    (input : CreateSituation) : CreateGasEffect :=
  priceCreateWithHash schedule input
    (initCodeWords input.initCodeLength * schedule.create2HashWordCost)

def price : AccountGasSchedule → AccountOperation → OperationPrice
  | schedule, .balance access => .account (priceBalance schedule access)
  | schedule, .extcodehash access => .account (priceExtCodeHash schedule access)
  | schedule, .extcodesize access => .account (priceExtCodeSize schedule access)
  | schedule, .extcodecopy access => .account (priceExtCodeCopy schedule access)
  | schedule, .call input => .account (priceCall schedule input)
  | schedule, .callcode input => .account (priceCallCode schedule input)
  | schedule, .delegatecall input => .account (priceDelegateCall schedule input)
  | schedule, .staticcall input => .account (priceStaticCall schedule input)
  | schedule, .create input => .create (priceCreate schedule input)
  | schedule, .create2 input => .create (priceCreate2 schedule input)
  | schedule, .selfdestruct input => .account (priceSelfDestruct schedule input)

structure ScheduleWellFormed (schedule : AccountGasSchedule) : Prop where
  warmAccountLeCold : schedule.base.warmAccess ≤ schedule.base.coldAccountAccess
  warmStorageLeCold : schedule.base.warmAccess ≤ schedule.base.coldStorageAccess
  createAccessDecomposition :
    schedule.base.createAccess = schedule.base.accountWrite + schedule.base.coldAccountAccess
  accessListAddressDecomposition :
    schedule.base.accessListAddress + schedule.base.warmAccess = schedule.base.coldAccountAccess
  accessListStorageKeyDecomposition :
    schedule.base.accessListStorageKey + schedule.base.warmAccess =
      schedule.base.coldStorageAccess

theorem amsterdam_well_formed : ScheduleWellFormed AccountGasSchedule.amsterdam := by
  constructor <;> native_decide

theorem create_access_eq_account_write_add_cold
    (schedule : AccountGasSchedule) (wellFormed : ScheduleWellFormed schedule) :
    schedule.base.createAccess =
      schedule.base.accountWrite + schedule.base.coldAccountAccess :=
  wellFormed.createAccessDecomposition

theorem access_list_address_eq_cold_decomposition
    (schedule : AccountGasSchedule) (wellFormed : ScheduleWellFormed schedule) :
    accessListAddressPrice schedule + schedule.base.warmAccess =
      schedule.base.coldAccountAccess :=
  wellFormed.accessListAddressDecomposition

theorem access_list_storage_key_eq_cold_decomposition
    (schedule : AccountGasSchedule) (wellFormed : ScheduleWellFormed schedule) :
    accessListStorageKeyPrice schedule + schedule.base.warmAccess =
      schedule.base.coldStorageAccess :=
  wellFormed.accessListStorageKeyDecomposition

inductive CallCase where
  | noValue
  | valueToExistent
  | valueToDead
  deriving DecidableEq, Repr

namespace CallCase

def Matches (input : CallSituation) : CallCase → Prop
  | .noValue => input.value = 0
  | .valueToExistent => input.value ≠ 0 ∧ input.stateTarget = .existent
  | .valueToDead => input.value ≠ 0 ∧ input.stateTarget = .dead

def classify (input : CallSituation) : CallCase :=
  if input.value = 0 then .noValue
  else if input.stateTarget = .dead then .valueToDead
  else .valueToExistent

end CallCase

theorem call_case_matches_classify (input : CallSituation) (case : CallCase)
    (hMatches : CallCase.Matches input case) :
    CallCase.classify input = case := by
  cases case <;> simp_all [CallCase.Matches, CallCase.classify]

theorem call_cases_exhaustive (input : CallSituation) :
    ∃ case, CallCase.Matches input case := by
  by_cases noValue : input.value = 0
  · exact ⟨.noValue, noValue⟩
  cases target : input.stateTarget with
  | dead => exact ⟨.valueToDead, noValue, target⟩
  | existent => exact ⟨.valueToExistent, noValue, target⟩

theorem call_cases_mutually_exclusive {input : CallSituation} {left right : CallCase}
    (leftMatches : CallCase.Matches input left)
    (rightMatches : CallCase.Matches input right) :
    left = right := by
  have leftClassified := call_case_matches_classify input left leftMatches
  have rightClassified := call_case_matches_classify input right rightMatches
  exact leftClassified.symm.trans rightClassified

inductive SelfDestructCase where
  | noWrite
  | positiveBalanceToDead
  deriving DecidableEq, Repr

namespace SelfDestructCase

def Matches (input : SelfDestructSituation) : SelfDestructCase → Prop
  | .noWrite => ¬ (input.sourceBalancePositive = true ∧ input.beneficiary = .dead)
  | .positiveBalanceToDead =>
      input.sourceBalancePositive = true ∧ input.beneficiary = .dead

def classify (input : SelfDestructSituation) : SelfDestructCase :=
  if input.sourceBalancePositive = true ∧ input.beneficiary = .dead then
    .positiveBalanceToDead
  else .noWrite

end SelfDestructCase

theorem selfdestruct_case_matches_classify (input : SelfDestructSituation)
    (case : SelfDestructCase) (hMatches : SelfDestructCase.Matches input case) :
    SelfDestructCase.classify input = case := by
  cases case <;> simp_all [SelfDestructCase.Matches, SelfDestructCase.classify]

theorem selfdestruct_cases_exhaustive (input : SelfDestructSituation) :
    ∃ case, SelfDestructCase.Matches input case := by
  by_cases writes : input.sourceBalancePositive = true ∧ input.beneficiary = .dead
  · exact ⟨.positiveBalanceToDead, writes⟩
  · exact ⟨.noWrite, writes⟩

theorem selfdestruct_cases_mutually_exclusive
    {input : SelfDestructSituation} {left right : SelfDestructCase}
    (leftMatches : SelfDestructCase.Matches input left)
    (rightMatches : SelfDestructCase.Matches input right) :
    left = right := by
  have leftClassified := selfdestruct_case_matches_classify input left leftMatches
  have rightClassified := selfdestruct_case_matches_classify input right rightMatches
  exact leftClassified.symm.trans rightClassified

inductive CreateCase where
  | precheckFailure
  | existingNoCollision
  | existingCollision
  | deadNoCollision
  | deadCollision
  deriving DecidableEq, Repr

namespace CreateCase

def Matches (input : CreateSituation) : CreateCase → Prop
  | .precheckFailure => input.preChecksPassed = false
  | .existingNoCollision =>
      input.preChecksPassed = true ∧ input.destination = .existent ∧ input.collision = false
  | .existingCollision =>
      input.preChecksPassed = true ∧ input.destination = .existent ∧ input.collision = true
  | .deadNoCollision =>
      input.preChecksPassed = true ∧ input.destination = .dead ∧ input.collision = false
  | .deadCollision =>
      input.preChecksPassed = true ∧ input.destination = .dead ∧ input.collision = true

def classify (input : CreateSituation) : CreateCase :=
  if input.preChecksPassed = false then .precheckFailure
  else if input.destination = .dead then
    if input.collision = true then .deadCollision else .deadNoCollision
  else if input.collision = true then .existingCollision
  else .existingNoCollision

end CreateCase

theorem create_case_matches_classify (input : CreateSituation) (case : CreateCase)
    (hMatches : CreateCase.Matches input case) :
    CreateCase.classify input = case := by
  cases case <;> simp_all [CreateCase.Matches, CreateCase.classify]

theorem create_cases_exhaustive (input : CreateSituation) :
    ∃ case, CreateCase.Matches input case := by
  by_cases hPrecheck : input.preChecksPassed = false
  · exact ⟨.precheckFailure, hPrecheck⟩
  have precheckPassed : input.preChecksPassed = true := by
    cases hValue : input.preChecksPassed with
    | false => exact False.elim (hPrecheck hValue)
    | true => rfl
  cases destination : input.destination with
  | dead =>
    by_cases hCollision : input.collision = true
    · exact ⟨.deadCollision, precheckPassed, destination, hCollision⟩
    · have noCollision : input.collision = false := by
        cases hValue : input.collision with
        | false => rfl
        | true => exact False.elim (hCollision hValue)
      exact ⟨.deadNoCollision, precheckPassed, destination, noCollision⟩
  | existent =>
    by_cases hCollision : input.collision = true
    · exact ⟨.existingCollision, precheckPassed, destination, hCollision⟩
    · have noCollision : input.collision = false := by
        cases hValue : input.collision with
        | false => rfl
        | true => exact False.elim (hCollision hValue)
      exact ⟨.existingNoCollision, precheckPassed, destination, noCollision⟩

theorem create_cases_mutually_exclusive {input : CreateSituation}
    {left right : CreateCase} (leftMatches : CreateCase.Matches input left)
    (rightMatches : CreateCase.Matches input right) : left = right := by
  have leftClassified := create_case_matches_classify input left leftMatches
  have rightClassified := create_case_matches_classify input right rightMatches
  exact leftClassified.symm.trans rightClassified

theorem create_deposit_case_matches_classify (input : CreateSituation)
    (case : CreateDepositCase) (hMatches : CreateDepositCase.Matches input case) :
    CreateDepositCase.classify input = case := by
  cases case <;> simp_all [CreateDepositCase.Matches, CreateDepositCase.classify,
    createDepositCommitted, createEntryReached]

theorem create_deposit_cases_exhaustive (input : CreateSituation) :
    ∃ case, CreateDepositCase.Matches input case := by
  by_cases hPrecheck : input.preChecksPassed = false
  · exact ⟨.entryNotReached, hPrecheck⟩
  have precheckPassed : input.preChecksPassed = true := by
    cases hValue : input.preChecksPassed with
    | false => exact False.elim (hPrecheck hValue)
    | true => rfl
  by_cases hCollision : input.collision = true
  · exact ⟨.collision, precheckPassed, hCollision⟩
  have noCollision : input.collision = false := by
    cases hValue : input.collision with
    | false => rfl
    | true => exact False.elim (hCollision hValue)
  by_cases hChild : input.childSucceeded = false
  · exact ⟨.childFailure, precheckPassed, noCollision, hChild⟩
  have childSucceeded : input.childSucceeded = true := by
    cases hValue : input.childSucceeded with
    | false => exact False.elim (hChild hValue)
    | true => rfl
  by_cases hRuntime : input.runtimeCodeValid = false
  · exact ⟨.runtimeValidationFailure, precheckPassed, noCollision, childSucceeded, hRuntime⟩
  have runtimeValid : input.runtimeCodeValid = true := by
    cases hValue : input.runtimeCodeValid with
    | false => exact False.elim (hRuntime hValue)
    | true => rfl
  by_cases hDeposit : input.depositSucceeded = false
  · exact ⟨.depositFailure, precheckPassed, noCollision, childSucceeded, runtimeValid,
      hDeposit⟩
  have depositSucceeded : input.depositSucceeded = true := by
    cases hValue : input.depositSucceeded with
    | false => exact False.elim (hDeposit hValue)
    | true => rfl
  exact ⟨.committed, ⟨precheckPassed, noCollision⟩, childSucceeded, runtimeValid,
    depositSucceeded⟩

theorem create_deposit_cases_mutually_exclusive {input : CreateSituation}
    {left right : CreateDepositCase}
    (leftMatches : CreateDepositCase.Matches input left)
    (rightMatches : CreateDepositCase.Matches input right) : left = right := by
  have leftClassified := create_deposit_case_matches_classify input left leftMatches
  have rightClassified := create_deposit_case_matches_classify input right rightMatches
  exact leftClassified.symm.trans rightClassified

def allKinds : List AccountOperationKind :=
  [.balance, .extcodehash, .extcodesize, .extcodecopy, .call, .callcode,
    .delegatecall, .staticcall, .create, .create2, .selfdestruct]

theorem operation_kinds_exhaustive (input : AccountOperation) :
    AccountOperation.kind input ∈ allKinds := by
  cases input <;> simp [AccountOperation.kind, allKinds]

theorem operation_kinds_mutually_exclusive {input : AccountOperation}
    {left right : AccountOperationKind}
    (leftMatches : AccountOperation.kind input = left)
    (rightMatches : AccountOperation.kind input = right) : left = right := by
  exact leftMatches.symm.trans rightMatches

theorem read_second_access_is_warm (schedule : AccountGasSchedule)
    (access : AccessStatus) :
    (priceExtCodeSize schedule access).secondReadCharge = schedule.base.warmAccess ∧
    (priceExtCodeCopy schedule access).secondReadCharge = schedule.base.warmAccess := by
  exact ⟨rfl, rfl⟩

theorem balance_and_extcodehash_have_no_second_read (schedule : AccountGasSchedule)
    (access : AccessStatus) :
    (priceBalance schedule access).secondReadCharge = 0 ∧
    (priceExtCodeHash schedule access).secondReadCharge = 0 := by
  exact ⟨rfl, rfl⟩

theorem delegated_target_none_is_free (schedule : AccountGasSchedule) :
    delegatedTargetCharge schedule none = 0 := by
  rfl

theorem delegated_target_cold_pays_cold_access (schedule : AccountGasSchedule) :
    delegatedTargetCharge schedule (some .cold) = schedule.base.coldAccountAccess := by
  rfl

theorem delegated_target_warm_pays_warm_access (schedule : AccountGasSchedule) :
    delegatedTargetCharge schedule (some .warm) = schedule.base.warmAccess := by
  rfl

theorem call_value_pays_account_write_every_time (schedule : AccountGasSchedule)
    (input : CallSituation) (hasValue : input.value ≠ 0) :
    (priceCall schedule input).accountWriteCharge = schedule.base.accountWrite ∧
    (priceCall schedule input).callStipendCharge = schedule.callStipend := by
  simp [priceCall, callAccountWrite, callStipend, hasValue, accountEffect]

theorem call_value_state_creation_only_for_dead (schedule : AccountGasSchedule)
    (input : CallSituation) (hasValue : input.value ≠ 0) :
    (priceCall schedule input).stateCharge =
      if input.stateTarget = .dead then schedule.base.newAccountGas else 0 := by
  simp [priceCall, callNewAccountState, hasValue, accountEffect]

theorem callcode_value_has_same_price_as_call (schedule : AccountGasSchedule)
    (input : CallCodeSituation) :
    priceCallCode schedule input = priceCall schedule
      { codeAccess := input.codeAccess
        delegatedTargetAccess := input.delegatedTargetAccess
        stateTarget := .existent
        value := input.value } := by
  simp [priceCallCode, priceCall, callAccountWrite, callStipend, callNewAccountState,
    delegatedTargetCharge, accountEffect]

theorem callcode_value_pays_account_write_every_time (schedule : AccountGasSchedule)
    (input : CallCodeSituation) (hasValue : input.value ≠ 0) :
    (priceCallCode schedule input).accountWriteCharge = schedule.base.accountWrite ∧
    (priceCallCode schedule input).callStipendCharge = schedule.callStipend := by
  simp [priceCallCode, callAccountWrite, callStipend, hasValue, accountEffect]

theorem callcode_valid_price_matches_executing_account_call
    (schedule : AccountGasSchedule) (input : CallCodeSituation)
    (valid : CallCodeSituation.Valid input) :
    priceCallCode schedule input = priceCall schedule
      { codeAccess := input.codeAccess
        delegatedTargetAccess := input.delegatedTargetAccess
        stateTarget := input.executingAccount
        value := input.value } := by
  change input.executingAccount = .existent at valid
  rw [valid]
  simp [priceCallCode, priceCall, callAccountWrite, callStipend, callNewAccountState,
    delegatedTargetCharge, accountEffect]

theorem delegatecall_never_writes_or_creates (schedule : AccountGasSchedule)
    (input : CallSituation) :
    let effect := priceDelegateCall schedule input
    effect.accountWriteCharge = 0 ∧ effect.stateCharge = 0 := by
  simp [priceDelegateCall, accountEffect]

theorem staticcall_never_writes_or_creates (schedule : AccountGasSchedule)
    (input : CallSituation) :
    let effect := priceStaticCall schedule input
    effect.accountWriteCharge = 0 ∧ effect.stateCharge = 0 := by
  simp [priceStaticCall, accountEffect]

theorem callcode_never_creates_new_account (schedule : AccountGasSchedule)
    (input : CallCodeSituation) :
    (priceCallCode schedule input).stateCharge = 0 := by
  rfl

theorem selfdestruct_write_condition (schedule : AccountGasSchedule)
    (input : SelfDestructSituation) :
    (priceSelfDestruct schedule input).accountWriteCharge =
      (if input.sourceBalancePositive = true ∧ input.beneficiary = .dead then
        schedule.base.accountWrite else 0) := by
  rfl

theorem selfdestruct_state_condition (schedule : AccountGasSchedule)
    (input : SelfDestructSituation) :
    (priceSelfDestruct schedule input).stateCharge =
      (if input.sourceBalancePositive = true ∧ input.beneficiary = .dead then
        schedule.base.newAccountGas else 0) := by
  rfl

theorem create_dead_account_charges_new_account (schedule : AccountGasSchedule)
    (input : CreateSituation) (precheck : input.preChecksPassed = true)
    (dead : input.destination = .dead) :
    (priceCreate schedule input).entry.newAccountStateCharge = schedule.base.newAccountGas := by
  simp [priceCreate, priceCreateWithHash, createDestinationIsDead, precheck, dead]

theorem create_existing_account_has_no_new_account_charge (schedule : AccountGasSchedule)
    (input : CreateSituation) (precheck : input.preChecksPassed = true)
    (existent : input.destination = .existent) :
    (priceCreate schedule input).entry.newAccountStateCharge = 0 := by
  simp [priceCreate, priceCreateWithHash, createDestinationIsDead, precheck, existent]

theorem create_collision_refills_dead_account_charge (schedule : AccountGasSchedule)
    (input : CreateSituation) (precheck : input.preChecksPassed = true)
    (dead : input.destination = .dead) (collision : input.collision = true) :
    (priceCreate schedule input).entry.newAccountStateRefill = schedule.base.newAccountGas ∧
    (priceCreate schedule input).rollback = .collision := by
  simp [priceCreate, priceCreateWithHash, createStateMustRefill, stateRollback,
    createDepositCommitted, createEntryReached, precheck, dead, collision,
    CreateDepositCase.classify]

theorem create_collision_does_not_deposit_code (schedule : AccountGasSchedule)
    (input : CreateSituation) (collision : input.collision = true) :
    (priceCreate schedule input).deposit.executionCharge = 0 ∧
    (priceCreate schedule input).deposit.stateCharge = 0 := by
  simp [priceCreate, priceCreateWithHash, createDepositCommitted, createEntryReached,
    collision]

theorem create_code_deposit_only_after_success (schedule : AccountGasSchedule)
    (input : CreateSituation) (notSuccess : ¬ createDepositCommitted input) :
    (priceCreate schedule input).deposit.executionCharge = 0 ∧
    (priceCreate schedule input).deposit.stateCharge = 0 := by
  simp [priceCreate, priceCreateWithHash, createDepositCommitted, notSuccess]

theorem create2_pays_initcode_hash_charge (schedule : AccountGasSchedule)
    (input : CreateSituation) :
    (priceCreate2 schedule input).entry.create2HashCharge =
      initCodeWords input.initCodeLength * schedule.create2HashWordCost := by
  rfl

theorem create_has_no_initcode_hash_charge (schedule : AccountGasSchedule)
    (input : CreateSituation) :
    (priceCreate schedule input).entry.create2HashCharge = 0 := by
  rfl

theorem create_code_deposit_components (schedule : AccountGasSchedule)
    (input : CreateSituation) (succeeds : createDepositCommitted input) :
    (priceCreate schedule input).deposit.executionCharge =
        runtimeCodeWords input.runtimeCodeLength * schedule.codeDepositExecutionWordCost ∧
    (priceCreate schedule input).deposit.stateCharge =
        input.runtimeCodeLength * schedule.base.cpsb := by
  simp [priceCreate, priceCreateWithHash, createDepositCommitted, succeeds]

theorem create_access_is_a_flat_table_component (schedule : AccountGasSchedule)
    (input : CreateSituation) :
    (priceCreate schedule input).entry.createAccessCharge = schedule.base.createAccess := by
  rfl

theorem create_state_refill_matches_rollback (schedule : AccountGasSchedule)
    (input : CreateSituation) :
    (priceCreate schedule input).entry.newAccountStateRefill =
      (if createStateMustRefill input then schedule.base.newAccountGas else 0) ∧
    (priceCreate schedule input).rollback = stateRollback input := by
  simp [priceCreate, priceCreateWithHash]

theorem create_execution_components_add_up (schedule : AccountGasSchedule)
    (input : CreateSituation) :
    (priceCreate2 schedule input).executionCharge =
      (priceCreate2 schedule input).entry.executionCharge +
      (priceCreate2 schedule input).deposit.executionCharge := by
  rfl

end AccountPricing
end Eip803x
