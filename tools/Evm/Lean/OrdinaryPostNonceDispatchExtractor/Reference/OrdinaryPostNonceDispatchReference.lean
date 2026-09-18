-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only
-- Independent reference vocabulary and decision structure for post-nonce dispatch.

import Lean

namespace OrdinaryPostNonceDispatchExtractor.Reference

structure Options where
  raw : Nat
  deriving DecidableEq, Repr

structure Transaction where
  id : Nat
  to : Option Nat
  authorizationListPresent : Bool
  deriving DecidableEq, Repr

structure Header where
  id : Nat
  deriving DecidableEq, Repr

structure Spec where
  id : Nat
  eip8037Enabled : Bool
  eip658Enabled : Bool
  deriving DecidableEq, Repr

structure Tracer where
  id : Nat
  isTracingState : Bool
  deriving DecidableEq, Repr

structure Intrinsic where
  standard : Nat
  deriving DecidableEq, Repr

structure GasPolicy where
  value : Nat
  stateReservoir : Int
  stateGasUsed : Int
  stateGasSpill : Int
  stateGasSpillRefunded : Int
  deriving DecidableEq, Repr

structure LoadedPreload where
  codeHandle : Nat
  delegation : Option Nat
  codeIsEmpty : Bool
  deriving DecidableEq, Repr

inductive Preload where
  | none
  | loaded (value : LoadedPreload)
  deriving DecidableEq, Repr

structure LookupResponse where
  escaped : Bool
  preload : Preload
  deriving DecidableEq, Repr

structure LookupRequest where
  recipient : Nat
  followDelegation : Bool
  spec : Spec
  deriving DecidableEq, Repr

inductive CommitTracer where
  | tracing (value : Tracer)
  | null
  deriving DecidableEq, Repr

structure CommitRequest where
  spec : Spec
  tracer : CommitTracer
  commitRoots : Bool
  deriving DecidableEq, Repr

structure CommitResponse where
  escaped : Bool
  deriving DecidableEq, Repr

structure AvailableGasResponse where
  accepted : Bool
  gas : GasPolicy
  deriving DecidableEq, Repr

structure Input where
  tx : Transaction
  header : Header
  spec : Spec
  tracer : Tracer
  options : Options
  intrinsic : Intrinsic
  gasLimit : Nat
  isCodeOverridable : Bool
  forceSimpleTransferDisabled : Bool
  lookup : LookupResponse
  commitResponse : CommitResponse
  availableGas : AvailableGasResponse
  opcodePrice : Nat
  premium : Nat
  reserved : Nat
  blobBaseFee : Nat
  deleteCallerAccount : Bool
  deriving DecidableEq, Repr

structure Handoff where
  tx : Transaction
  header : Header
  spec : Spec
  tracer : Tracer
  opts : Options
  restore : Bool
  commit : Bool
  deleteCallerAccount : Bool
  intrinsic : Intrinsic
  gasAvailable : GasPolicy
  opcodePrice : Nat
  premium : Nat
  reserved : Nat
  blobBaseFee : Nat
  preload : Preload
  recipient : Option Nat
  deriving DecidableEq, Repr

inductive TerminalOutcome where
  | EscapedLookup
  | EscapedPrecommit
  | GasRejected
  | SimpleHandoff
  | EvmHandoff
  deriving DecidableEq, Repr

structure Result where
  outcome : TerminalOutcome
  preload : Preload
  gasAvailable : GasPolicy
  handoff : Option Handoff
  lookupRequest : Option LookupRequest
  commitRequest : Option CommitRequest
  failure : Option String
  deriving DecidableEq, Repr

structure Prepared where
  recipient : Option Nat
  preload : Preload
  simpleRecipient : Option Nat
  lookupEscaped : Bool
  lookupRequest : Option LookupRequest
  deriving DecidableEq, Repr

def hasFlag (raw flag : Nat) : Bool := flag != 0 && ((raw / flag) % 2 == 1)
def restore (options : Options) : Bool := hasFlag options.raw 2
def effectiveCommit (options : Options) (eip658Enabled : Bool) : Bool :=
  hasFlag options.raw 1 || (!hasFlag options.raw 4 && !eip658Enabled)

def simpleDecision : Preload → Bool
  | .none => false
  | .loaded value => value.delegation.isNone && value.codeIsEmpty

def zeroGas : GasPolicy :=
  { value := 0, stateReservoir := 0, stateGasUsed := 0, stateGasSpill := 0, stateGasSpillRefunded := 0 }

def candidateAllowed (input : Input) : Bool :=
  !input.isCodeOverridable && !input.tx.authorizationListPresent && !input.forceSimpleTransferDisabled

def noRecipientPrepared : Prepared :=
  { recipient := none, preload := .none, simpleRecipient := none, lookupEscaped := false, lookupRequest := none }

def nonCandidatePrepared (recipient : Nat) : Prepared :=
  { recipient := some recipient, preload := .none, simpleRecipient := none, lookupEscaped := false, lookupRequest := none }

def lookupPrepared (input : Input) (recipient : Nat) : Prepared :=
  let request : Option LookupRequest := some
    { recipient := recipient, followDelegation := !input.spec.eip8037Enabled, spec := input.spec }
  if input.lookup.escaped then
    { recipient := some recipient, preload := .none, simpleRecipient := none, lookupEscaped := true,
      lookupRequest := request }
  else
    let returned := input.lookup.preload
    let simpleRecipient := if simpleDecision returned then some recipient else none
    { recipient := some recipient, preload := returned, simpleRecipient := simpleRecipient,
      lookupEscaped := false, lookupRequest := request }

def prepare (input : Input) : Prepared :=
  match input.tx.to with
  | none => noRecipientPrepared
  | some recipient => if candidateAllowed input then lookupPrepared input recipient else nonCandidatePrepared recipient

structure Controls where
  restore : Bool
  commit : Bool
  beforeExecution : Bool
  deriving DecidableEq, Repr

def controls (input : Input) (simpleRecipient : Option Nat) : Controls :=
  let restoreValue := restore input.options
  let commitValue := effectiveCommit input.options input.spec.eip658Enabled
  { restore := restoreValue,
    commit := commitValue,
    beforeExecution := commitValue && (simpleRecipient.isNone || restoreValue || input.tracer.isTracingState) }

def selectedCommitTracer (tracer : Tracer) : CommitTracer :=
  if tracer.isTracingState then .tracing tracer else .null

def commitRequest (input : Input) : CommitRequest :=
  { spec := input.spec,
    tracer := selectedCommitTracer input.tracer,
    commitRoots := false }

def makeHandoff (input : Input) (prepared : Prepared) (gas : GasPolicy) (recipient : Option Nat)
    (restoreValue commitValue : Bool) : Handoff :=
  { tx := input.tx, header := input.header, spec := input.spec, tracer := input.tracer, opts := input.options,
    restore := restoreValue, commit := commitValue, deleteCallerAccount := input.deleteCallerAccount,
    intrinsic := input.intrinsic, gasAvailable := gas, opcodePrice := input.opcodePrice, premium := input.premium,
    reserved := input.reserved, blobBaseFee := input.blobBaseFee, preload := prepared.preload, recipient := recipient }

def lookupEscape (prepared : Prepared) : Result :=
  { outcome := .EscapedLookup, preload := prepared.preload, gasAvailable := zeroGas, handoff := none,
    lookupRequest := prepared.lookupRequest, commitRequest := none, failure := none }

def precommitEscape (input : Input) (prepared : Prepared) : Result :=
  { outcome := .EscapedPrecommit, preload := prepared.preload, gasAvailable := zeroGas, handoff := none,
    lookupRequest := prepared.lookupRequest, commitRequest := some (commitRequest input), failure := none }

def gasRejected (input : Input) (prepared : Prepared) (control : Controls) : Result :=
  { outcome := .GasRejected, preload := prepared.preload, gasAvailable := zeroGas, handoff := none,
    lookupRequest := prepared.lookupRequest,
    commitRequest := if control.beforeExecution then some (commitRequest input) else none,
    failure := some "GasLimitBelowIntrinsicGas" }

def acceptedGas (input : Input) (prepared : Prepared) (control : Controls) : Result :=
  let output := makeHandoff input prepared input.availableGas.gas prepared.simpleRecipient control.restore control.commit
  let request := if control.beforeExecution then some (commitRequest input) else none
  match prepared.simpleRecipient with
  | some _ =>
    { outcome := .SimpleHandoff, preload := prepared.preload, gasAvailable := input.availableGas.gas,
      handoff := some output, lookupRequest := prepared.lookupRequest, commitRequest := request, failure := none }
  | none =>
    { outcome := .EvmHandoff, preload := prepared.preload, gasAvailable := input.availableGas.gas,
      handoff := some output, lookupRequest := prepared.lookupRequest, commitRequest := request, failure := none }

def afterPreparation (input : Input) (prepared : Prepared) (control : Controls) : Result :=
  if control.beforeExecution && input.commitResponse.escaped then
    precommitEscape input prepared
  else if input.availableGas.accepted then
    acceptedGas input prepared control
  else
    gasRejected input prepared control

def run (input : Input) : Result :=
  let prepared := prepare input
  if prepared.lookupEscaped then lookupEscape prepared
  else afterPreparation input prepared (controls input prepared.simpleRecipient)

end OrdinaryPostNonceDispatchExtractor.Reference
