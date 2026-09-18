-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only
-- Independently organized reference for the admitted pre-VM preparation boundary.

import Lean

namespace EvmTransactionPreparationExtractor.Reference

def targetGasPolicy : String := "EthereumGasPolicy"

inductive Status where
  | admissionMismatch
  | preparationOog
  | collision
  | nullCode
  | vmBoundary
  deriving DecidableEq, Repr

structure UInt64Rep where
  raw : Int
  deriving DecidableEq, Repr

structure Int64Rep where
  raw : Int
  deriving DecidableEq, Repr

structure FixedWidthFacts where
  value : UInt64Rep
  stateReservoir : Int64Rep
  stateGasUsed : Int64Rep
  stateGasSpill : Int64Rep
  stateGasSpillRefunded : Int64Rep
  deriving DecidableEq, Repr

def uint64Min : Int := 0
def uint64Max : Int := 18446744073709551615
def int64Min : Int := -9223372036854775808
def int64Max : Int := 9223372036854775807
def fitsUInt64 (value : Int) : Bool := uint64Min ≤ value && value ≤ uint64Max
def fitsInt64 (value : Int) : Bool := int64Min ≤ value && value ≤ int64Max
def twosComplementDecode (raw : Int) : Int := if raw ≤ int64Max then raw else raw - 18446744073709551616
def twosComplementEncode (value : Int) : Int := if value < 0 then value + 18446744073709551616 else value

def fixedWidthValid (facts : FixedWidthFacts) : Bool :=
  fitsUInt64 facts.value.raw && fitsInt64 facts.stateReservoir.raw && fitsInt64 facts.stateGasUsed.raw &&
    fitsInt64 facts.stateGasSpill.raw && fitsInt64 facts.stateGasSpillRefunded.raw &&
    twosComplementDecode (twosComplementEncode facts.stateReservoir.raw) = facts.stateReservoir.raw &&
    twosComplementDecode (twosComplementEncode facts.stateGasUsed.raw) = facts.stateGasUsed.raw &&
    twosComplementDecode (twosComplementEncode facts.stateGasSpill.raw) = facts.stateGasSpill.raw &&
    twosComplementDecode (twosComplementEncode facts.stateGasSpillRefunded.raw) = facts.stateGasSpillRefunded.raw

structure Gas where
  value : Int
  stateReservoir : Int
  stateGasUsed : Int
  stateGasSpill : Int
  stateGasSpillRefunded : Int
  deriving DecidableEq, Repr

structure SnapshotPart where
  persistent : Int
  transient : Int
  deriving DecidableEq, Repr

structure Snapshot where
  storage : SnapshotPart
  state : Int
  blockAccessList : Int
  deriving DecidableEq, Repr

structure AccountFacts where
  physicalExists : Bool
  logicalExists : Bool
  nonce : Nat
  code : String
  delegation : Option String
  balance : Nat
  storageNonEmpty : Bool
  deriving DecidableEq, Repr

structure AuthorityStateFacts where
  authority : String
  original : AccountFacts
  current : AccountFacts
  delegatedBeforeTx : Option String
  deriving DecidableEq, Repr

structure WorldFacts where
  physicalExists : Bool
  logicalExists : Bool
  originalPhysicalExists : Bool
  currentPhysicalExists : Bool
  originalLogicalExists : Bool
  currentLogicalExists : Bool
  originalNonce : Nat
  currentNonce : Nat
  originalCode : String
  currentCode : String
  originalDelegation : Option String
  currentDelegation : Option String
  originalBalance : Nat
  currentBalance : Nat
  recipientBalance : Nat
  delegationRefunds : Nat
  accountWarm : Bool
  storageWarm : Bool
  accountReads : List String
  accountWrites : List String
  storageReads : List String
  storageWrites : List String
  codeInsertRefunds : Nat
  traceAccess : List String
  authorityStates : List AuthorityStateFacts
  deriving DecidableEq, Repr

structure TxExecutionContext where
  sender : String
  codeRepository : String
  blobHashes : List String
  opcodeGasPrice : Nat
  deriving DecidableEq, Repr

structure AccessObservation where
  warmedAccounts : List String
  warmedStorage : List String
  reads : List String
  tracing : Bool
  deriving DecidableEq, Repr

structure CodeIdentity where
  source : String
  target : Option String
  bytes : String
  isEmpty : Bool
  isNull : Bool
  deriving DecidableEq, Repr

structure Environment where
  code : CodeIdentity
  executingAccount : String
  caller : String
  codeSource : Option String
  callDepth : Nat
  value : Nat
  input : String
  deriving DecidableEq, Repr

structure AuthorizationFacts where
  authority : String
  codeAddress : String
  chainIdValid : Bool
  nonceValid : Bool
  signatureValid : Bool
  account : AccountFacts
  expectedNonce : Nat
  delegationBefore : Option String
  clearsDelegation : Bool
  newAccountStateCharge : Int
  accountWriteCharge : Nat
  perAuthStateCharge : Int
  deriving DecidableEq, Repr

structure DelegatedTargetFacts where
  target : Option String
  isPrecompile : Bool
  account : AccountFacts
  accessCharge : Nat
  deriving DecidableEq, Repr

structure DeadRecipientFacts where
  sender : String
  recipient : Option String
  physicalExists : Bool
  logicalExists : Bool
  accountEmpty : Bool
  stateCharge : Int
  deriving DecidableEq, Repr

structure DeploymentFacts where
  physicalExists : Bool
  logicalExists : Bool
  accountEmpty : Bool
  storageNonEmpty : Bool
  storageCleared : Bool
  codeOrNonceCollision : Bool
  collision : Bool
  includeStorageCollision : Bool
  stateCharge : Int
  deriving DecidableEq, Repr

structure ValueTransferFacts where
  sender : String
  recipient : Option String
  senderBalance : Nat
  recipientBalance : Nat
  amount : Nat
  deriving DecidableEq, Repr

structure PreparationRelations where
  delegatedTarget : DelegatedTargetFacts
  deadRecipient : DeadRecipientFacts
  deployment : DeploymentFacts
  valueTransfer : ValueTransferFacts
  deriving DecidableEq, Repr

structure SpecFacts where
  eip7702 : Bool
  eip8037 : Bool
  eip8038 : Bool
  useHotAndColdStorage : Bool
  useTxAccessLists : Bool
  addCoinbaseToTxAccessList : Bool
  deriving DecidableEq, Repr

structure ExecutionOptionsFacts where
  warmup : Bool
  skipValidation : Bool
  deriving DecidableEq, Repr

structure TxFacts where
  isCreate : Bool
  isSetCode : Bool
  sender : String
  recipient : Option String
  value : Nat
  input : String
  accessListAccounts : List String
  accessListStorage : List String
  coinbase : Option String
  deriving DecidableEq, Repr

/- The enriched handoff is the package entry projection.  Its access is the
   fresh StackAccessTracker's modeled AccessObservation; the post-preparation VM observation is
   carried by Result.access and VmInput.access. -/
structure EvmHandoff where
  context : TxExecutionContext
  gas : Gas
  access : AccessObservation
  tx : TxFacts
  spec : SpecFacts
  options : ExecutionOptionsFacts
  executionIntrinsicGasStandard : Gas
  environment : Environment
  delegationRefunds : Nat
  exactBoundary : String
  deriving DecidableEq, Repr

structure VmInput where
  gas : Gas
  executionType : String
  environment : Environment
  access : AccessObservation
  snapshot : Snapshot
  deriving DecidableEq, Repr

inductive Event where
  | txContext (context : TxExecutionContext)
  | stackTracker (tracing : Bool)
  | metrics (name : String)
  | authorizationWarm (authority : String)
  | authorizationRead (authority : String)
  | stateCharge (label : String) (amount : Int)
  | executionCharge (label : String) (amount : Nat)
  | accountWrite (authority : String)
  | delegationWrite (authority : String) (target : String)
  | recipientRead (recipient : String)
  | accessWarm (accounts : List String) (storage : List String)
  | delegatedTargetRead (target : String)
  | delegatedTargetWarm (target : String)
  | deploymentRead (recipient : String)
  | snapshot (which : String) (value : Snapshot)
  | restore (which : String) (value : Snapshot)
  | valueDebit (sender : String) (amount : Nat)
  | environmentRent (environment : Environment)
  | topFrameRent (input : VmInput)
  | topFrameHalt (gas : Gas) (environment : Environment)
  | accessReport (access : AccessObservation)
  | vmCallBoundary
  deriving DecidableEq, Repr

structure Result where
  status : Status
  error : Option String
  gas : Gas
  executionIntrinsicGasStandard : Gas
  prePreparationGas : Gas
  preExecutionSnapshot : Snapshot
  topLevelSnapshot : Snapshot
  postIntrinsicStateReservoir : Int
  world : WorldFacts
  access : AccessObservation
  environment : Environment
  code : CodeIdentity
  input : String
  delegationRefunds : Nat
  vmInput : Option VmInput
  events : List Event
  deriving DecidableEq, Repr

structure Input where
  handoff : EvmHandoff
  prePreparationGas : Gas
  preExecutionIntrinsicGasStandard : Gas
  hasPreExecutionSnapshot : Bool
  preExecutionSnapshot : Snapshot
  topLevelSnapshot : Snapshot
  postIntrinsicStateReservoir : Int
  preExecutionWorld : WorldFacts
  world : WorldFacts
  access : AccessObservation
  authorizations : List AuthorizationFacts
  relations : PreparationRelations
  environment : Environment
  fixedWidth : FixedWidthFacts
  preparationFixedWidth : FixedWidthFacts
  baselineFixedWidth : FixedWidthFacts
  preBaselineFixedWidth : FixedWidthFacts
  delegationRefunds : Nat
  deriving DecidableEq, Repr

def hasValue (tx : TxFacts) : Bool := tx.value > 0
def messageInputData (input : Input) : String :=
  match input.handoff.tx.recipient with
  | some _ => input.handoff.tx.input
  | none => ""
def hasValueIn (value : String) : List String → Bool
  | [] => false
  | head :: tail => if value == head then true else hasValueIn value tail

def appendUnique (value : String) (values : List String) : List String :=
  if hasValueIn value values then values else values ++ [value]

def warmAccount (access : AccessObservation) (account : String) : AccessObservation :=
  { access with warmedAccounts := appendUnique account access.warmedAccounts }

def recordAccessRead (access : AccessObservation) (account : String) : AccessObservation :=
  { access with reads := access.reads ++ [account] }

def warmAccounts (access : AccessObservation) : List String → AccessObservation
  | [] => access
  | account :: rest => warmAccounts (warmAccount access account) rest

def warmStorage (access : AccessObservation) : List String → AccessObservation
  | [] => access
  | cell :: rest => warmStorage { access with warmedStorage := appendUnique cell access.warmedStorage } rest

def stateSpill (gas : Gas) (amount : Int) : Int :=
  if amount ≤ gas.stateReservoir then 0 else amount - max 0 gas.stateReservoir

def gasFieldsValid (gas : Gas) : Bool :=
  fitsUInt64 gas.value && fitsInt64 gas.stateReservoir && fitsInt64 gas.stateGasUsed &&
    fitsInt64 gas.stateGasSpill && fitsInt64 gas.stateGasSpillRefunded

def fixedWidthMatches (facts : FixedWidthFacts) (gas : Gas) : Bool :=
  facts.value.raw = gas.value && facts.stateReservoir.raw = gas.stateReservoir &&
    facts.stateGasUsed.raw = gas.stateGasUsed && facts.stateGasSpill.raw = gas.stateGasSpill &&
    facts.stateGasSpillRefunded.raw = gas.stateGasSpillRefunded

def gasFactsEqual (left right : Gas) : Bool :=
  left.value == right.value && left.stateReservoir == right.stateReservoir &&
    left.stateGasUsed == right.stateGasUsed && left.stateGasSpill == right.stateGasSpill &&
    left.stateGasSpillRefunded == right.stateGasSpillRefunded

def authorizationChargesValid : List AuthorizationFacts → Bool
  | [] => true
  | auth :: rest =>
    auth.newAccountStateCharge ≥ 0 && fitsInt64 auth.newAccountStateCharge &&
      fitsUInt64 (Int.ofNat auth.accountWriteCharge) &&
      auth.perAuthStateCharge ≥ 0 && fitsInt64 auth.perAuthStateCharge && authorizationChargesValid rest

def relationChargesValid (relations : PreparationRelations) : Bool :=
  relations.delegatedTarget.accessCharge ≤ 18446744073709551615 &&
    fitsUInt64 (Int.ofNat relations.delegatedTarget.accessCharge) &&
    relations.deadRecipient.stateCharge ≥ 0 && fitsInt64 relations.deadRecipient.stateCharge &&
    relations.deployment.stateCharge ≥ 0 && fitsInt64 relations.deployment.stateCharge

def chargeState (gas : Gas) (amount : Int) : Option Gas :=
  if !gasFieldsValid gas then none else if amount ≤ 0 then some gas else
    let spill := stateSpill gas amount
    if gas.value < spill then none
    else if gas.stateReservoir ≥ amount then
      let charged := { gas with stateReservoir := gas.stateReservoir - amount, stateGasUsed := gas.stateGasUsed + amount }
      if gasFieldsValid charged then some charged else none
    else
      let charged := { gas with value := gas.value - spill, stateReservoir := 0, stateGasUsed := gas.stateGasUsed + amount, stateGasSpill := gas.stateGasSpill + spill }
      if gasFieldsValid charged then some charged else none

def chargeExecution (gas : Gas) (amount : Nat) : Option Gas :=
  if !gasFieldsValid gas then none else if gas.value < amount then none else
    let charged := { gas with value := gas.value - amount }
    if gasFieldsValid charged then some charged else none

def chargeDelegatedTarget (enabled alreadyWarm isPrecompile : Bool) (gas : Gas) (target : Option String)
    (amount : Nat) (access : AccessObservation) : Option (Gas × AccessObservation) :=
  if !enabled then some (gas, access) else
    match target with
    | none => some (gas, access)
    | some _address =>
      match chargeExecution gas (if alreadyWarm || isPrecompile then 0 else amount) with
      | none => none
      | some after => some (after, access)

def foldAuthorizationStateGas (gas baseline : Gas) (delta : Int) : Option (Gas × Gas) :=
  if delta ≤ 0 then some (gas, baseline) else
    let foldedGas := { gas with stateGasSpill := 0, stateGasSpillRefunded := 0 }
    let foldedBaseline := { baseline with stateReservoir := baseline.stateReservoir + delta, stateGasUsed := baseline.stateGasUsed + delta }
    if gasFieldsValid foldedGas && gasFieldsValid foldedBaseline then some (foldedGas, foldedBaseline) else none

def authPreliminaryValid (auth : AuthorizationFacts) : Bool :=
  auth.chainIdValid && auth.nonceValid && auth.signatureValid

def findAuthorityStateIn : List AuthorityStateFacts → String → Option AuthorityStateFacts
  | [], _ => none
  | state :: rest, authority => if state.authority == authority then some state else findAuthorityStateIn rest authority

def findAuthorityState (world : WorldFacts) (authority : String) : Option AuthorityStateFacts :=
  findAuthorityStateIn world.authorityStates authority

def replaceAuthorityState (world : WorldFacts) (replacement : AuthorityStateFacts) : WorldFacts :=
  let rec replace : List AuthorityStateFacts → List AuthorityStateFacts
    | [] => []
    | state :: rest => if state.authority == replacement.authority then replacement :: rest else state :: replace rest
  { world with authorityStates := replace world.authorityStates }

def hasUniqueAuthorityStates : List AuthorityStateFacts → Bool
  | [] => true
  | state :: rest => !hasValueIn state.authority (rest.map AuthorityStateFacts.authority) && hasUniqueAuthorityStates rest

def accountFactsEqual (left right : AccountFacts) : Bool :=
  left.physicalExists == right.physicalExists && left.logicalExists == right.logicalExists &&
    left.nonce == right.nonce && left.code == right.code && left.delegation == right.delegation &&
      left.balance == right.balance && left.storageNonEmpty == right.storageNonEmpty

def accountLogicalExists (account : AccountFacts) : Bool :=
  account.balance > 0 || account.nonce > 0 || account.code != ""

def accountFactsCoherent (account : AccountFacts) : Bool :=
  account.nonce ≤ 18446744073709551615 && account.logicalExists == accountLogicalExists account &&
    (account.physicalExists || !account.storageNonEmpty) &&
    (account.physicalExists || (!account.logicalExists && account.balance == 0 && account.nonce == 0 &&
      account.code == "" && account.delegation.isNone)) &&
    (account.delegation.isNone || account.code != "")

def delegatedTargetFactsCoherent (facts : DelegatedTargetFacts) : Bool :=
  facts.target.isNone || accountFactsCoherent facts.account

def authorityStateFactsCoherent (state : AuthorityStateFacts) : Bool :=
  accountFactsCoherent state.original && accountFactsCoherent state.current &&
    state.delegatedBeforeTx == state.original.delegation

def authorityStatesCoherent : List AuthorityStateFacts → Bool
  | [] => true
  | state :: rest => authorityStateFactsCoherent state && authorityStatesCoherent rest

def authorizationCoherent (world : WorldFacts) (auth : AuthorizationFacts) : Bool :=
  match findAuthorityState world auth.authority with
  | none => false
  | some state => accountFactsEqual state.original auth.account && state.delegatedBeforeTx == auth.delegationBefore

def initialWrittenAccounts (input : Input) : List String :=
  if !input.handoff.spec.eip8037 then [] else
    let withSender := [input.handoff.tx.sender]
    if input.handoff.tx.value > 0 then match input.handoff.tx.recipient with
      | some recipient => appendUnique recipient withSender
      | none => withSender
    else withSender

def fixedWidthValidForInput (input : Input) : Bool :=
  fixedWidthValid input.fixedWidth && fixedWidthMatches input.fixedWidth input.handoff.gas &&
    fixedWidthValid input.preparationFixedWidth && fixedWidthMatches input.preparationFixedWidth input.prePreparationGas &&
    fixedWidthValid input.baselineFixedWidth && fixedWidthMatches input.baselineFixedWidth input.handoff.executionIntrinsicGasStandard &&
    fixedWidthValid input.preBaselineFixedWidth && fixedWidthMatches input.preBaselineFixedWidth input.preExecutionIntrinsicGasStandard &&
    fitsInt64 input.postIntrinsicStateReservoir &&
    twosComplementDecode (twosComplementEncode input.postIntrinsicStateReservoir) = input.postIntrinsicStateReservoir &&
    authorizationChargesValid input.authorizations && relationChargesValid input.relations

def authorizationsCoherent (world : WorldFacts) : List AuthorizationFacts → Bool
  | [] => true
  | auth :: rest => authorizationCoherent world auth && auth.expectedNonce ≤ 18446744073709551615 &&
      (!auth.nonceValid || auth.expectedNonce < 18446744073709551615) && authorizationsCoherent world rest

def authorityStateMatchesTransaction (input : Input) (state : AuthorityStateFacts) : Bool :=
  let projectsSender := state.authority == input.handoff.tx.sender
  let projectsRecipient := match input.handoff.tx.recipient with | some recipient => state.authority == recipient | none => false
  (!projectsSender || (state.current.physicalExists == input.world.currentPhysicalExists &&
    state.current.logicalExists == input.world.currentLogicalExists && state.current.nonce == input.world.currentNonce &&
    state.current.code == input.world.currentCode && state.current.delegation == input.world.currentDelegation &&
    state.current.balance == input.world.currentBalance)) &&
    (!projectsRecipient || state.current.balance == input.world.recipientBalance)

def authorityStatesMatchTransaction (input : Input) : List AuthorityStateFacts → Bool
  | [] => true
  | state :: rest => authorityStateMatchesTransaction input state && authorityStatesMatchTransaction input rest

def codeIdentityEqual (left right : CodeIdentity) : Bool :=
  left.source == right.source && left.target == right.target && left.bytes == right.bytes &&
    left.isEmpty == right.isEmpty && left.isNull == right.isNull

def environmentFactsEqual (left right : Environment) : Bool :=
  codeIdentityEqual left.code right.code && left.executingAccount == right.executingAccount &&
    left.caller == right.caller && left.codeSource == right.codeSource && left.callDepth == right.callDepth &&
    left.value == right.value && left.input == right.input

def accessFactsEqual (left right : AccessObservation) : Bool :=
  left.warmedAccounts == right.warmedAccounts && left.warmedStorage == right.warmedStorage &&
    left.reads == right.reads && left.tracing == right.tracing

def freshAccess (access : AccessObservation) : Bool :=
  access.warmedAccounts.isEmpty && access.warmedStorage.isEmpty && access.reads.isEmpty

def authorityStateFactsEqual (left right : AuthorityStateFacts) : Bool :=
  left.authority == right.authority && accountFactsEqual left.original right.original &&
    accountFactsEqual left.current right.current && left.delegatedBeforeTx == right.delegatedBeforeTx

def authorityStateListEqual : List AuthorityStateFacts → List AuthorityStateFacts → Bool
  | [], [] => true
  | left :: leftRest, right :: rightRest => authorityStateFactsEqual left right && authorityStateListEqual leftRest rightRest
  | _, _ => false

def worldFactsEqual (left right : WorldFacts) : Bool :=
  left.physicalExists == right.physicalExists && left.logicalExists == right.logicalExists &&
    left.originalPhysicalExists == right.originalPhysicalExists && left.currentPhysicalExists == right.currentPhysicalExists &&
    left.originalLogicalExists == right.originalLogicalExists && left.currentLogicalExists == right.currentLogicalExists &&
    left.originalNonce == right.originalNonce && left.currentNonce == right.currentNonce &&
    left.originalCode == right.originalCode && left.currentCode == right.currentCode &&
    left.originalDelegation == right.originalDelegation && left.currentDelegation == right.currentDelegation &&
    left.originalBalance == right.originalBalance && left.currentBalance == right.currentBalance &&
    left.recipientBalance == right.recipientBalance && left.delegationRefunds == right.delegationRefunds &&
    left.accountWarm == right.accountWarm && left.storageWarm == right.storageWarm &&
    left.accountReads == right.accountReads && left.accountWrites == right.accountWrites &&
    left.storageReads == right.storageReads && left.storageWrites == right.storageWrites &&
    left.codeInsertRefunds == right.codeInsertRefunds && left.traceAccess == right.traceAccess &&
    authorityStateListEqual left.authorityStates right.authorityStates

def snapshotFactsEqual (left right : Snapshot) : Bool :=
  left.storage.persistent == right.storage.persistent && left.storage.transient == right.storage.transient &&
    left.state == right.state && left.blockAccessList == right.blockAccessList

def snapshotIsEmpty (snapshot : Snapshot) : Bool :=
  snapshotFactsEqual snapshot { storage := { persistent := -1, transient := -1 }, state := -1, blockAccessList := -1 }

def hasAuthorization (tx : TxFacts) (authorizations : List AuthorizationFacts) : Bool := tx.isSetCode && !authorizations.isEmpty

def sourceAccessLineageAdmitted : Bool := true

def snapshotPresenceCoherent (input : Input) : Bool :=
  input.hasPreExecutionSnapshot == (input.handoff.spec.eip7702 && hasAuthorization input.handoff.tx input.authorizations && input.handoff.spec.eip8037)

def sourceEntryFactsCoherent (input : Input) : Bool :=
  accessFactsEqual input.access input.handoff.access && freshAccess input.access &&
    input.access.tracing == input.handoff.access.tracing &&
    input.delegationRefunds == 0 && input.handoff.delegationRefunds == 0 &&
    input.world.delegationRefunds == 0 && input.preExecutionWorld.delegationRefunds == 0 &&
    worldFactsEqual input.preExecutionWorld input.world &&
    gasFactsEqual input.prePreparationGas input.handoff.gas &&
    gasFactsEqual input.preExecutionIntrinsicGasStandard input.handoff.executionIntrinsicGasStandard &&
    environmentFactsEqual input.environment input.handoff.environment &&
    (input.hasPreExecutionSnapshot || snapshotIsEmpty input.preExecutionSnapshot) &&
    snapshotPresenceCoherent input &&
    sourceAccessLineageAdmitted &&
    input.handoff.context.codeRepository != "" &&
    targetGasPolicy == "EthereumGasPolicy" &&
    input.handoff.exactBoundary == "VirtualMachine.ExecuteTransaction"

def inputFactsCoherent (input : Input) : Bool :=
  sourceEntryFactsCoherent input && input.handoff.context.sender == input.handoff.tx.sender &&
    gasFactsEqual input.prePreparationGas input.handoff.gas &&
    gasFactsEqual input.preExecutionIntrinsicGasStandard input.handoff.executionIntrinsicGasStandard &&
    (!hasAuthorization input.handoff.tx input.authorizations || input.handoff.tx.recipient.isSome) &&
    (!input.handoff.tx.isSetCode || input.handoff.tx.recipient.isSome) &&
    (!input.handoff.tx.isSetCode || !input.authorizations.isEmpty) &&
    (input.authorizations.isEmpty || input.handoff.tx.isSetCode) &&
    (input.handoff.tx.recipient.isSome || !input.handoff.environment.code.isNull) &&
    environmentFactsEqual input.environment input.handoff.environment &&
    input.delegationRefunds == input.handoff.delegationRefunds &&
    input.handoff.environment.input == messageInputData input &&
    input.environment.input == messageInputData input &&
    input.handoff.tx.sender == input.relations.valueTransfer.sender &&
    input.relations.valueTransfer.recipient == input.handoff.tx.recipient &&
    input.handoff.tx.value == input.relations.valueTransfer.amount &&
    input.relations.valueTransfer.senderBalance == input.world.currentBalance &&
    input.relations.valueTransfer.recipientBalance == input.world.recipientBalance &&
    (!hasValue input.handoff.tx || input.handoff.options.warmup || input.relations.valueTransfer.senderBalance ≥ input.handoff.tx.value) &&
    input.relations.deadRecipient.sender == input.handoff.tx.sender &&
    input.relations.deadRecipient.recipient == input.handoff.tx.recipient &&
    input.relations.deadRecipient.logicalExists ==
      (input.relations.deadRecipient.physicalExists && !input.relations.deadRecipient.accountEmpty) &&
    input.relations.deadRecipient.accountEmpty == !input.relations.deadRecipient.logicalExists &&
    delegatedTargetFactsCoherent input.relations.delegatedTarget &&
    (input.handoff.tx.recipient.isSome || input.relations.delegatedTarget.target.isNone) &&
    input.relations.deployment.includeStorageCollision == input.handoff.spec.eip8037 &&
    input.relations.deployment.logicalExists ==
      (input.relations.deployment.physicalExists && !input.relations.deployment.accountEmpty) &&
    input.relations.deployment.accountEmpty == !input.relations.deployment.logicalExists &&
    input.relations.deployment.collision ==
      (input.relations.deployment.codeOrNonceCollision ||
        (input.relations.deployment.includeStorageCollision && input.relations.deployment.storageNonEmpty)) &&
    input.relations.deployment.storageCleared ==
      (input.handoff.tx.recipient.isNone && !input.handoff.spec.eip8037 && !input.relations.deployment.collision) &&
    input.handoff.tx.isCreate == input.handoff.tx.recipient.isNone &&
    input.hasPreExecutionSnapshot == (input.handoff.spec.eip7702 && hasAuthorization input.handoff.tx input.authorizations && input.handoff.spec.eip8037) &&
    hasUniqueAuthorityStates input.world.authorityStates && authorityStatesCoherent input.world.authorityStates &&
    authorizationsCoherent input.world input.authorizations &&
    authorityStatesMatchTransaction input input.world.authorityStates

def authHasCode (account : AccountFacts) : Bool := account.code != ""
def authHasDelegation (account : AccountFacts) : Bool := account.delegation.isSome
def authExecutionValid (world : WorldFacts) (auth : AuthorizationFacts) : Bool :=
  match findAuthorityState world auth.authority with
  | none => false
  | some state => authPreliminaryValid auth && state.current.nonce == auth.expectedNonce &&
      (!authHasCode state.current || authHasDelegation state.current)

structure AuthorizationState where
  gas : Gas
  baseline : Gas
  world : WorldFacts
  access : AccessObservation
  refunds : Nat
  writtenAuthorities : List String
  delegationSetFor : List String
  events : List Event
  failed : Bool
  deriving DecidableEq, Repr

def recordAccountRead (world : WorldFacts) (account : String) : WorldFacts :=
  { world with accountReads := world.accountReads ++ [account], traceAccess := world.traceAccess ++ ["read:" ++ account] }

def recordAccountWrite (world : WorldFacts) (account : String) : WorldFacts :=
  { world with accountWrites := world.accountWrites ++ [account], traceAccess := world.traceAccess ++ ["write:" ++ account] }

def recordStorageWrite (world : WorldFacts) (account : String) : WorldFacts :=
  { world with storageWrites := world.storageWrites ++ [account], traceAccess := world.traceAccess ++ ["storage-write:" ++ account] }

def updateAuthorityBalance (world : WorldFacts) (authority : String) (balance : Nat) : WorldFacts :=
  let rec update : List AuthorityStateFacts → List AuthorityStateFacts
    | [] => []
    | state :: rest =>
      if state.authority == authority then
        { state with current := { state.current with balance := balance } } :: rest
      else state :: update rest
  { world with authorityStates := update world.authorityStates }

def applyAuthorizationWorld (input : Input) (world : WorldFacts) (auth : AuthorizationFacts) : WorldFacts :=
  match findAuthorityState world auth.authority with
  | none => world
  | some authorityState =>
    let current := authorityState.current
    let nextPhysical := true
    let nextLogical := true
    let nextNonce := if current.physicalExists then current.nonce + 1 else 1
    let nextCode := if auth.clearsDelegation then "" else "delegation:" ++ auth.codeAddress
    let nextDelegation := if auth.clearsDelegation then none else some auth.codeAddress
    let next := { current with physicalExists := nextPhysical, logicalExists := nextLogical, nonce := nextNonce, code := nextCode, delegation := nextDelegation }
    let changed := replaceAuthorityState world { authorityState with current := next }
    let projectsSender := auth.authority == input.handoff.tx.sender
    let projectsRecipient := match input.handoff.tx.recipient with | some recipient => auth.authority == recipient | none => false
    let projected := { changed with
      physicalExists := if projectsSender then nextPhysical else changed.physicalExists, logicalExists := if projectsSender then nextLogical else changed.logicalExists, currentPhysicalExists := if projectsSender then nextPhysical else changed.currentPhysicalExists, currentLogicalExists := if projectsSender then nextLogical else changed.currentLogicalExists, currentNonce := if projectsSender then nextNonce else changed.currentNonce, currentCode := if projectsSender then nextCode else changed.currentCode, currentDelegation := if projectsSender then nextDelegation else changed.currentDelegation, currentBalance := if projectsSender then next.balance else changed.currentBalance, recipientBalance := if projectsRecipient then next.balance else changed.recipientBalance }
    recordAccountWrite projected auth.authority

def authorizationCharge (state : AuthorizationState) (label : String) (amount : Int) : Option AuthorizationState :=
  match chargeState state.gas amount with
  | none => none
  | some gas => some { state with gas := gas, events := state.events ++ [Event.stateCharge label amount] }

def authorizationExecutionCharge (state : AuthorizationState) (label : String) (amount : Nat) : Option AuthorizationState :=
  match chargeExecution state.gas amount with
  | none => none
  | some gas => some { state with gas := gas, events := state.events ++ [Event.executionCharge label amount] }

def processAuthorizationTuple (input : Input) (state : AuthorizationState) (auth : AuthorizationFacts) : AuthorizationState :=
  if state.failed then state else
    let preliminary := authPreliminaryValid auth
    let access0 := if preliminary then recordAccessRead (warmAccount state.access auth.authority) auth.authority else state.access
    let world0 := if preliminary then recordAccountRead state.world auth.authority else state.world
     let observed := { state with access := access0, world := world0, events := if preliminary then state.events ++ [Event.authorizationWarm auth.authority, Event.authorizationRead auth.authority] else state.events }
    match findAuthorityState state.world auth.authority with
    | none => { observed with failed := true }
    | some authorityState =>
      let current := authorityState.current
      let codeValid := !authHasCode current || authHasDelegation current
      let nonceValid := current.nonce == auth.expectedNonce
      if !preliminary || !authorizationCoherent state.world auth || !codeValid || !nonceValid then observed
      else
        let firstWrite := !hasValueIn auth.authority observed.writtenAuthorities
        let firstDelegation := !auth.clearsDelegation && authorityState.delegatedBeforeTx.isNone && !hasValueIn auth.authority observed.delegationSetFor
        match if input.handoff.spec.eip8037 && !current.logicalExists then authorizationCharge observed "new-account" auth.newAccountStateCharge else some observed with
        | none => { observed with failed := true }
        | some afterNew =>
          match if input.handoff.spec.eip8037 && firstWrite then authorizationExecutionCharge afterNew "account-write" auth.accountWriteCharge else some afterNew with
          | none => { afterNew with gas := { afterNew.gas with value := 0 }, failed := true }
          | some afterWrite =>
            match if input.handoff.spec.eip8037 && firstDelegation then authorizationCharge afterWrite "per-auth" auth.perAuthStateCharge else some afterWrite with
            | none => { afterWrite with failed := true }
            | some charged =>
              let nextWorld := applyAuthorizationWorld input charged.world auth
              let codeInsertRefund := if input.handoff.spec.eip8037 || !current.physicalExists then 0 else 1
              let nextWorld := { nextWorld with codeInsertRefunds := nextWorld.codeInsertRefunds + codeInsertRefund }
              { charged with world := nextWorld, refunds := charged.refunds + codeInsertRefund, writtenAuthorities := if firstWrite then charged.writtenAuthorities ++ [auth.authority] else charged.writtenAuthorities, delegationSetFor := if firstDelegation then charged.delegationSetFor ++ [auth.authority] else charged.delegationSetFor, events := charged.events ++ [Event.accountWrite auth.authority, Event.delegationWrite auth.authority auth.codeAddress] }

def processAuthorizationList (input : Input) (state : AuthorizationState) : List AuthorizationFacts → AuthorizationState
  | [] => state
  | auth :: rest => processAuthorizationList input (processAuthorizationTuple input state auth) rest

def emptyAuthorizationState (input : Input) : AuthorizationState :=
  { gas := input.handoff.gas, baseline := input.preExecutionIntrinsicGasStandard, world := input.world, access := input.access, refunds := input.delegationRefunds, writtenAuthorities := initialWrittenAccounts input, delegationSetFor := [], events := [Event.txContext input.handoff.context] ++ (if input.handoff.tx.recipient.isNone then [Event.metrics "creates"] else []) ++ [Event.stackTracker input.access.tracing] ++ (if input.hasPreExecutionSnapshot then [Event.snapshot "preparation" input.preExecutionSnapshot] else []), failed := false }

def emptyResult (input : Input) (status : Status) (error : Option String) (gas baseline : Gas) (world : WorldFacts)
    (access : AccessObservation) (environment : Environment) (refunds : Nat) (events : List Event) (vmInput : Option VmInput) : Result :=
  { status := status, error := error, gas := gas, executionIntrinsicGasStandard := baseline, prePreparationGas := input.prePreparationGas, preExecutionSnapshot := input.preExecutionSnapshot, topLevelSnapshot := input.topLevelSnapshot, postIntrinsicStateReservoir := input.postIntrinsicStateReservoir, world := world, access := access, environment := environment, code := environment.code, input := environment.input, delegationRefunds := refunds, vmInput := vmInput, events := events }

def preparationOog (input : Input) (_gas _baseline : Gas) (world : WorldFacts) (access : AccessObservation)
    (environment : Environment) (refunds : Nat) (events : List Event) : Result :=
  let restoredWorld := if input.hasPreExecutionSnapshot then input.preExecutionWorld else world
  let restoredGas := input.prePreparationGas
  let restoredBaseline := input.preExecutionIntrinsicGasStandard
  let restoreEvents := if input.hasPreExecutionSnapshot then [Event.restore "preparation" input.preExecutionSnapshot] else []
  let accessEvents := if access.tracing then [Event.accessReport access] else []
  let result := emptyResult input .preparationOog (some "preparation out of gas") restoredGas restoredBaseline restoredWorld access environment refunds
    (events ++ restoreEvents ++ [Event.snapshot "topLevel" input.topLevelSnapshot, Event.topFrameHalt restoredGas environment] ++ accessEvents) none
  { result with postIntrinsicStateReservoir := restoredGas.stateReservoir }

def topLevelCreateOog (input : Input) (gas baseline : Gas) (postReservoir : Int) (world : WorldFacts) (access : AccessObservation)
    (environment : Environment) (refunds : Nat) (events : List Event) : Result :=
  let accessEvents := if access.tracing then [Event.accessReport access] else []
  let result := emptyResult input .preparationOog (some "top-level create out of gas") gas baseline world access environment refunds
    (events ++ [Event.snapshot "topLevel" input.topLevelSnapshot, Event.deploymentRead environment.executingAccount, Event.topFrameHalt gas environment, Event.restore "topLevel" input.topLevelSnapshot] ++ accessEvents) none
  { result with postIntrinsicStateReservoir := postReservoir }

def processAuthorization (input : Input) : Result × Bool :=
  if !input.handoff.spec.eip7702 || !hasAuthorization input.handoff.tx input.authorizations then
    let initialized := emptyAuthorizationState input
    let result := emptyResult input .vmBoundary none initialized.gas initialized.baseline initialized.world initialized.access input.handoff.environment initialized.refunds initialized.events none
    ({ result with postIntrinsicStateReservoir := initialized.gas.stateReservoir }, false)
  else
    let processed := processAuthorizationList input (emptyAuthorizationState input) input.authorizations
    if processed.failed then
      let result := emptyResult input .vmBoundary none processed.gas processed.baseline processed.world processed.access input.handoff.environment processed.refunds processed.events none
      ({ result with postIntrinsicStateReservoir := processed.gas.stateReservoir }, true)
    else
      let delta := processed.gas.stateGasUsed - input.handoff.gas.stateGasUsed
      let folded := if input.handoff.spec.eip8037 then if fitsInt64 delta then foldAuthorizationStateGas processed.gas processed.baseline delta else none else some (processed.gas, processed.baseline)
      match folded with
      | none =>
        let result := emptyResult input .admissionMismatch (some "authorization gas representation overflow") processed.gas processed.baseline processed.world processed.access input.handoff.environment processed.refunds processed.events none
        ({ result with postIntrinsicStateReservoir := processed.gas.stateReservoir }, true)
      | some (authorizationGas, authorizationBaseline) =>
        let result := emptyResult input .vmBoundary none authorizationGas authorizationBaseline processed.world processed.access input.handoff.environment processed.refunds processed.events none
        ({ result with postIntrinsicStateReservoir := authorizationGas.stateReservoir }, false)

def isCreateTx (input : Input) : Bool := input.handoff.tx.recipient.isNone
def senderCurrentNonce (input : Input) (world : WorldFacts) : Nat := match findAuthorityState world input.handoff.tx.sender with | some state => state.current.nonce | none => world.currentNonce
def deriveRecipient (input : Input) (world : WorldFacts) : Option String :=
  if isCreateTx input then some ("create:" ++ toString (senderCurrentNonce input world)) else input.handoff.tx.recipient

def creationCode (input : Input) : CodeIdentity :=
  { input.environment.code with source := "initcode", target := none, bytes := input.handoff.tx.input, isEmpty := input.handoff.tx.input == "", isNull := false }

def warmTransaction (input : Input) (access : AccessObservation) (recipient : Option String) (includeRecipient : Bool) : AccessObservation :=
  if !input.handoff.spec.useHotAndColdStorage then access else
    let access0 := if input.handoff.spec.useTxAccessLists then warmStorage (warmAccounts access input.handoff.tx.accessListAccounts) input.handoff.tx.accessListStorage else access
    let access1 := if input.handoff.spec.addCoinbaseToTxAccessList then match input.handoff.tx.coinbase with | some coinbase => warmAccount access0 coinbase | none => access0 else access0
    let access2 := if includeRecipient then match recipient with | some value => warmAccount access1 value | none => access1 else access1
    warmAccount access2 input.handoff.tx.sender

def buildEnvironment (input : Input) (preparation : Result) (topFrameOutOfGas : Bool) : Result × Bool :=
  let recipient := deriveRecipient input preparation.world
  let access0 := warmTransaction input preparation.access recipient !topFrameOutOfGas
  let isMessageRecipient := !isCreateTx input && !topFrameOutOfGas
  let access1 := if isMessageRecipient then match recipient with | some value => recordAccessRead access0 value | none => access0 else access0
  let world0 := if isMessageRecipient then match recipient with | some value => recordAccountRead preparation.world value | none => preparation.world else preparation.world
  let recipientEvents := if isMessageRecipient then match recipient with | some value => [Event.recipientRead value] | none => [] else []
  let delegated := input.relations.delegatedTarget
  let targetResolved := !isCreateTx input && delegated.target.isSome && !topFrameOutOfGas
  let targetPresent := targetResolved && input.handoff.spec.eip8037
  let targetWarmEnabled := targetResolved && (!input.handoff.spec.eip8037 || input.handoff.spec.useHotAndColdStorage)
  let targetChargeEnabled := targetPresent && input.handoff.spec.useHotAndColdStorage
  let targetWasWarm := if targetWarmEnabled then match delegated.target with | some value => hasValueIn value access1.warmedAccounts | none => false else false
  let targetAccess0 := if targetWarmEnabled then match delegated.target with | some value => warmAccount access1 value | none => access1 else access1
  let charged := chargeDelegatedTarget targetChargeEnabled targetWasWarm delegated.isPrecompile preparation.gas delegated.target delegated.accessCharge targetAccess0
  match charged with
  | none =>
    let targetWarmEvents := if targetWarmEnabled then match delegated.target with | some value => [Event.delegatedTargetWarm value] | none => [] else []
    let environment := { input.environment with code := { input.environment.code with target := none, bytes := "", isEmpty := true, isNull := false }, executingAccount := match recipient with | some value => value | none => input.environment.executingAccount, caller := input.handoff.tx.sender, codeSource := recipient, callDepth := 0, value := input.handoff.tx.value, input := messageInputData input }
    let result := emptyResult input .vmBoundary none { preparation.gas with value := 0 } preparation.executionIntrinsicGasStandard world0 targetAccess0 environment preparation.delegationRefunds
      (preparation.events ++ [Event.accessWarm targetAccess0.warmedAccounts targetAccess0.warmedStorage] ++ recipientEvents ++ targetWarmEvents ++ [Event.environmentRent environment]) none
    ({ result with postIntrinsicStateReservoir := preparation.postIntrinsicStateReservoir }, true)
  | some (gas, access2) =>
    let access := if targetPresent then match delegated.target with | some value => recordAccessRead access2 value | none => access2 else access2
    let world := if targetPresent then match delegated.target with | some value => recordAccountRead world0 value | none => world0 else world0
    let targetWarmEvents := if targetWarmEnabled then match delegated.target with | some value => [Event.delegatedTargetWarm value] | none => [] else []
    let targetReadEvents := if targetPresent then match delegated.target with | some value => [Event.delegatedTargetRead value] | none => [] else []
    let resolvedCode := if isCreateTx input then creationCode input else if targetPresent then match delegated.target with | some value => if delegated.isPrecompile then { input.environment.code with target := some value, bytes := "", isEmpty := true, isNull := false } else { input.environment.code with target := some value, bytes := delegated.account.code, isEmpty := delegated.account.code == "", isNull := false } | none => input.environment.code else if topFrameOutOfGas then { input.environment.code with target := none, bytes := "", isEmpty := true, isNull := false } else input.environment.code
     let environment := { input.environment with code := resolvedCode, executingAccount := match recipient with | some value => value | none => input.environment.executingAccount, caller := input.handoff.tx.sender, codeSource := recipient, callDepth := 0, value := input.handoff.tx.value, input := messageInputData input }
    let targetEvents := targetWarmEvents ++ targetReadEvents
    let result := emptyResult input .vmBoundary none gas preparation.executionIntrinsicGasStandard world access environment preparation.delegationRefunds
      (preparation.events ++ [Event.accessWarm access.warmedAccounts access.warmedStorage] ++ recipientEvents ++ targetEvents ++ [Event.environmentRent environment]) none
    ({ result with postIntrinsicStateReservoir := preparation.postIntrinsicStateReservoir }, false)

def recipientAccountEmpty (input : Input) (world : WorldFacts) : Bool :=
  match input.handoff.tx.recipient with
  | some recipient => match findAuthorityState world recipient with
    | some state => !state.current.logicalExists
    | none => input.relations.deadRecipient.accountEmpty
  | none => input.relations.deadRecipient.accountEmpty

def deadRecipient (input : Input) (world : WorldFacts) : Bool :=
  recipientAccountEmpty input world && input.relations.deadRecipient.recipient.isSome &&
    input.handoff.tx.sender != (match input.relations.deadRecipient.recipient with | some value => value | none => input.handoff.tx.sender) &&
    !isCreateTx input && hasValue input.handoff.tx && input.handoff.spec.eip8037

def valueDebitAmount (input : Input) (world : WorldFacts) : Nat :=
  if !hasValue input.handoff.tx then 0 else if input.handoff.options.warmup then min input.handoff.tx.value world.currentBalance else if world.currentBalance ≥ input.handoff.tx.value then input.handoff.tx.value else 0

def payValue (input : Input) (world : WorldFacts) : WorldFacts :=
  let amount := valueDebitAmount input world
  if amount = 0 then world else
    let balance := world.currentBalance - amount
    let paid := recordAccountWrite world input.handoff.tx.sender
    updateAuthorityBalance { paid with currentBalance := balance }
      input.handoff.tx.sender balance

def deploymentCollision (input : Input) : Bool :=
  isCreateTx input && input.relations.deployment.collision

def prepareCall (input : Input) (preparation : Result) (environment : Environment) (topFrameOutOfGas : Bool) : Result :=
  if topFrameOutOfGas then preparationOog input preparation.gas preparation.executionIntrinsicGasStandard preparation.world preparation.access environment preparation.delegationRefunds preparation.events
  else
    let deploymentAccess := if isCreateTx input then recordAccessRead preparation.access environment.executingAccount else preparation.access
    let deploymentWorld := if isCreateTx input then recordAccountRead preparation.world environment.executingAccount else preparation.world
    let deploymentEvents := if isCreateTx input then [Event.deploymentRead environment.executingAccount] else []
    let storageWorld := if isCreateTx input && !input.handoff.spec.eip8037 && input.relations.deployment.storageCleared then recordStorageWrite deploymentWorld environment.executingAccount else deploymentWorld
    let charged := if isCreateTx input && input.handoff.spec.eip8037 && !input.relations.deployment.logicalExists then
      chargeState preparation.gas input.relations.deployment.stateCharge else some preparation.gas
    match charged with
    | none => topLevelCreateOog input preparation.gas preparation.executionIntrinsicGasStandard preparation.postIntrinsicStateReservoir preparation.world deploymentAccess environment preparation.delegationRefunds
        preparation.events
    | some gas =>
      if deploymentCollision input then
        let result := emptyResult input .collision (some "transaction collision") gas preparation.executionIntrinsicGasStandard preparation.world deploymentAccess environment preparation.delegationRefunds
          (preparation.events ++ [Event.snapshot "topLevel" input.topLevelSnapshot] ++ deploymentEvents ++
            (if input.handoff.spec.eip8037 && !input.relations.deployment.logicalExists then
              [Event.stateCharge "create-state" input.relations.deployment.stateCharge] else []) ++
            [Event.restore "topLevel" input.topLevelSnapshot] ++
            (if deploymentAccess.tracing then [Event.accessReport deploymentAccess] else [])) none
        { result with postIntrinsicStateReservoir := preparation.postIntrinsicStateReservoir }
      else
        let debit := valueDebitAmount input storageWorld
        let world := payValue input storageWorld
        let chargeEvents := if isCreateTx input && input.handoff.spec.eip8037 && !input.relations.deployment.logicalExists then
          [Event.stateCharge "create-state" input.relations.deployment.stateCharge] else []
        let paymentEvents := if debit > 0 then [Event.valueDebit input.handoff.tx.sender debit] else []
        if environment.code.isNull then
          let result := emptyResult input .nullCode none gas preparation.executionIntrinsicGasStandard world deploymentAccess environment preparation.delegationRefunds
            (preparation.events ++ [Event.snapshot "topLevel" input.topLevelSnapshot] ++ deploymentEvents ++ chargeEvents ++ paymentEvents ++
              (if deploymentAccess.tracing then [Event.accessReport deploymentAccess] else [])) none
          { result with postIntrinsicStateReservoir := preparation.postIntrinsicStateReservoir }
        else
          let vmInput := { gas := gas, executionType := if isCreateTx input then "CREATE" else "TRANSACTION", environment := environment, access := deploymentAccess, snapshot := input.topLevelSnapshot }
          let result := emptyResult input .vmBoundary none gas preparation.executionIntrinsicGasStandard world deploymentAccess environment preparation.delegationRefunds
              (preparation.events ++ [Event.snapshot "topLevel" input.topLevelSnapshot] ++ deploymentEvents ++ chargeEvents ++ paymentEvents ++
              [Event.topFrameRent vmInput, Event.vmCallBoundary]) (some vmInput)
          { result with postIntrinsicStateReservoir := preparation.postIntrinsicStateReservoir }

def run (input : Input) : Result :=
  if !fixedWidthValidForInput input || !inputFactsCoherent input then
    emptyResult input .admissionMismatch (some "reference representation or source-fact coherence mismatch") input.handoff.gas input.preExecutionIntrinsicGasStandard input.world input.access input.handoff.environment input.delegationRefunds [] none
  else
    let canonical := { input with environment := input.handoff.environment, delegationRefunds := input.handoff.delegationRefunds }
    let authorization := processAuthorization canonical
    let environmentResult := buildEnvironment canonical authorization.1 authorization.2
    let environmentPreparation := environmentResult.1
    let environmentOog := environmentResult.2
    let withReservoir := { environmentPreparation with postIntrinsicStateReservoir := environmentPreparation.gas.stateReservoir }
    let deadApplicable := !authorization.2 && !environmentOog && deadRecipient canonical withReservoir.world
    let deadCharge := if deadApplicable then chargeState withReservoir.gas canonical.relations.deadRecipient.stateCharge else some withReservoir.gas
    match deadCharge with
    | none => prepareCall canonical withReservoir withReservoir.environment true
    | some gas =>
      let deadPreparation := if deadApplicable then { withReservoir with gas := gas, events := withReservoir.events ++ [Event.stateCharge "dead-recipient" canonical.relations.deadRecipient.stateCharge] } else withReservoir
      prepareCall canonical deadPreparation deadPreparation.environment (authorization.2 || environmentOog)

/- Small executable vectors keep the ordered side effects observable without
   importing the generated artifact.  They intentionally exercise facts that
   cannot be represented by a single caller-selected predicate. -/
def vectorGas : Gas :=
  { value := 100, stateReservoir := 10, stateGasUsed := 0, stateGasSpill := 0, stateGasSpillRefunded := 0 }

def vectorAuthorizationOogGas : Gas :=
  { value := 5, stateReservoir := 0, stateGasUsed := 0, stateGasSpill := 0, stateGasSpillRefunded := 0 }

def vectorSnapshotPart : SnapshotPart := { persistent := 0, transient := 0 }
def vectorSnapshot : Snapshot := { storage := vectorSnapshotPart, state := 0, blockAccessList := 0 }
def vectorEmptySnapshotPart : SnapshotPart := { persistent := -1, transient := -1 }
def vectorEmptySnapshot : Snapshot := { storage := vectorEmptySnapshotPart, state := -1, blockAccessList := -1 }

example : snapshotIsEmpty vectorEmptySnapshot = true && snapshotIsEmpty vectorSnapshot = false := by
  decide

def vectorAccount (physical logical : Bool) (nonce : Nat) (code : String)
    (delegation : Option String) (balance : Nat) : AccountFacts :=
  { physicalExists := physical, logicalExists := logical, nonce := nonce, code := code, delegation := delegation, balance := balance, storageNonEmpty := false }

def vectorWorld : WorldFacts :=
  { physicalExists := true, logicalExists := true, originalPhysicalExists := true, currentPhysicalExists := true, originalLogicalExists := true, currentLogicalExists := true, originalNonce := 0, currentNonce := 0, originalCode := "", currentCode := "", originalDelegation := none, currentDelegation := none, originalBalance := 100, currentBalance := 100, recipientBalance := 0, delegationRefunds := 0, accountWarm := false, storageWarm := false, accountReads := [], accountWrites := [], storageReads := [], storageWrites := [], codeInsertRefunds := 0, traceAccess := [], authorityStates := [] }

def vectorAccess : AccessObservation := { warmedAccounts := [], warmedStorage := [], reads := [], tracing := false }
def vectorContext : TxExecutionContext :=
  { sender := "sender", codeRepository := "repository", blobHashes := [], opcodeGasPrice := 1 }
def vectorCode : CodeIdentity :=
  { source := "recipient", target := none, bytes := "", isEmpty := true, isNull := false }
def vectorEnvironment : Environment :=
  { code := vectorCode, executingAccount := "recipient", caller := "sender", codeSource := some "recipient", callDepth := 0, value := 1, input := "" }
def vectorTransaction : TxFacts :=
  { isCreate := false, isSetCode := false, sender := "sender", recipient := some "recipient", value := 1, input := "", accessListAccounts := [], accessListStorage := [], coinbase := none }
def vectorSpec : SpecFacts :=
  { eip7702 := true, eip8037 := true, eip8038 := true, useHotAndColdStorage := true, useTxAccessLists := true, addCoinbaseToTxAccessList := true }
def vectorOptions : ExecutionOptionsFacts := { warmup := false, skipValidation := false }
def vectorRelations : PreparationRelations :=
  { delegatedTarget := { target := none, isPrecompile := false, account := vectorAccount false false 0 "" none 0, accessCharge := 0 }, deadRecipient := { sender := "sender", recipient := some "recipient", physicalExists := false, logicalExists := false, accountEmpty := true, stateCharge := 1 }, deployment := { physicalExists := false, logicalExists := false, accountEmpty := true, storageNonEmpty := false, storageCleared := false, codeOrNonceCollision := false, collision := false, includeStorageCollision := true, stateCharge := 1 }, valueTransfer := { sender := "sender", recipient := some "recipient", senderBalance := 100, recipientBalance := 0, amount := 1 } }
def vectorHandoff : EvmHandoff :=
  { context := vectorContext, gas := vectorGas, access := vectorAccess, tx := vectorTransaction, spec := vectorSpec, options := vectorOptions, executionIntrinsicGasStandard := vectorGas, environment := vectorEnvironment, delegationRefunds := 0, exactBoundary := "VirtualMachine.ExecuteTransaction" }
def vectorFixedWidth (gas : Gas) : FixedWidthFacts :=
  { value := { raw := gas.value }, stateReservoir := { raw := gas.stateReservoir }, stateGasUsed := { raw := gas.stateGasUsed }, stateGasSpill := { raw := gas.stateGasSpill }, stateGasSpillRefunded := { raw := gas.stateGasSpillRefunded } }
def vectorInput : Input :=
  { handoff := vectorHandoff, prePreparationGas := vectorGas, preExecutionIntrinsicGasStandard := vectorGas, hasPreExecutionSnapshot := false, preExecutionSnapshot := vectorEmptySnapshot, topLevelSnapshot := vectorSnapshot, postIntrinsicStateReservoir := 10, preExecutionWorld := vectorWorld, world := vectorWorld, access := vectorAccess, authorizations := [], relations := vectorRelations, environment := vectorEnvironment, fixedWidth := vectorFixedWidth vectorGas, preparationFixedWidth := vectorFixedWidth vectorGas, baselineFixedWidth := vectorFixedWidth vectorGas, preBaselineFixedWidth := vectorFixedWidth vectorGas, delegationRefunds := 0 }

theorem legacy_delegated_target_warms_without_charge (hotAndCold : Bool) :
    let input := { vectorInput with
      handoff := { vectorHandoff with spec := { vectorSpec with eip8037 := false, useHotAndColdStorage := hotAndCold } },
      relations := { vectorRelations with delegatedTarget := { vectorRelations.delegatedTarget with target := some "legacy-target", accessCharge := 1000 } } }
    let preparation := (processAuthorization input).1
    let result := buildEnvironment input preparation false
    result.2 = false ∧ result.1.gas = preparation.gas ∧
      hasValueIn "legacy-target" result.1.access.warmedAccounts = true ∧
      hasValueIn "legacy-target" result.1.access.reads = false ∧
      result.1.code = input.environment.code ∧
      result.1.events.contains (.delegatedTargetWarm "legacy-target") = true ∧
      result.1.events.contains (.delegatedTargetRead "legacy-target") = false := by
  cases hotAndCold <;> decide

example :
    inputFactsCoherent { vectorInput with preExecutionSnapshot := vectorSnapshot } = false &&
      (run { vectorInput with preExecutionSnapshot := vectorSnapshot }).status = .admissionMismatch := by
  decide

def vectorAuthority : String := "authority"
def vectorAuthorityOriginal : AccountFacts := vectorAccount false false 0 "" none 0
def vectorAuthorityState (nonce : Nat) (delegatedBeforeTx : Option String) : AuthorityStateFacts :=
  { authority := vectorAuthority, original := vectorAuthorityOriginal, current := vectorAccount true false nonce "" none 0, delegatedBeforeTx := delegatedBeforeTx }
def vectorAuthorization (expectedNonce : Nat) (accountWriteCharge : Nat) : AuthorizationFacts :=
  { authority := vectorAuthority, codeAddress := "target", chainIdValid := true, nonceValid := true, signatureValid := true, account := vectorAuthorityOriginal, expectedNonce := expectedNonce, delegationBefore := none, clearsDelegation := false, newAccountStateCharge := 0, accountWriteCharge := accountWriteCharge, perAuthStateCharge := 0 }
def vectorAuthWorld (nonce : Nat) : WorldFacts :=
  { vectorWorld with authorityStates := [vectorAuthorityState nonce none] }
def vectorAuthInput (nonce expectedNonce : Nat) (accountWriteCharge : Nat) : Input :=
  { vectorInput with handoff := { vectorHandoff with spec := vectorSpec, tx := { vectorTransaction with isSetCode := true } }, world := vectorAuthWorld nonce, preExecutionWorld := vectorAuthWorld nonce, hasPreExecutionSnapshot := true, preExecutionSnapshot := vectorSnapshot, authorizations := [vectorAuthorization expectedNonce accountWriteCharge] }

example :
    hasAuthorization ({ vectorTransaction with isSetCode := false } : TxFacts)
      [vectorAuthorization 0 0] = false := by
  decide

example :
    inputFactsCoherent { vectorAuthInput 0 0 0 with authorizations := [], hasPreExecutionSnapshot := false } = false := by
  decide

example :
    inputFactsCoherent { vectorInput with authorizations := [vectorAuthorization 0 0] } = false := by
  decide

def vectorAuthState (nonce : Nat) (gas : Gas) : AuthorizationState :=
  { gas := gas, baseline := vectorGas, world := vectorAuthWorld nonce, access := vectorAccess, refunds := 0, writtenAuthorities := [], delegationSetFor := [], events := [], failed := false }

def vectorPreexistingDelegationState : AuthorityStateFacts :=
  { authority := vectorAuthority, original := vectorAccount true true 0 "delegation:old" (some "old") 0, current := vectorAccount true true 0 "delegation:old" (some "old") 0, delegatedBeforeTx := some "old" }

def vectorAuthorizationOogInput : Input :=
  let base := vectorAuthInput 0 0 3
  let gas := vectorAuthorizationOogGas
  { base with handoff := { base.handoff with gas := gas, executionIntrinsicGasStandard := gas }, prePreparationGas := gas, preExecutionIntrinsicGasStandard := gas, hasPreExecutionSnapshot := true, postIntrinsicStateReservoir := 0, authorizations := [{ vectorAuthorization 0 3 with perAuthStateCharge := 3 }], fixedWidth := vectorFixedWidth gas, preparationFixedWidth := vectorFixedWidth gas, baselineFixedWidth := vectorFixedWidth gas, preBaselineFixedWidth := vectorFixedWidth gas }

example :
    let result := processAuthorizationTuple (vectorAuthInput 7 8 0) (vectorAuthState 7 vectorGas)
      (vectorAuthorization 8 0)
    result.failed = false && result.access.warmedAccounts = [vectorAuthority] &&
      result.access.reads = [vectorAuthority] && result.world.accountReads = [vectorAuthority] &&
      result.world.accountWrites = [] := by
  decide

example :
    let state := processAuthorizationTuple (vectorAuthInput 0 0 0) (vectorAuthState 0 vectorGas)
      (vectorAuthorization 0 0)
    let duplicate := processAuthorizationTuple (vectorAuthInput 1 1 0)
      { state with world := state.world } (vectorAuthorization 1 0)
    (duplicate.world.authorityStates.head?.getD (vectorAuthorityState 0 none)).current.nonce = 2 := by
  decide

example :
    let processed := processAuthorizationList (vectorAuthInput 0 0 0) (vectorAuthState 0 vectorGas)
      [vectorAuthorization 7 0, vectorAuthorization 0 0]
    processed.failed = false && processed.access.reads = [vectorAuthority, vectorAuthority] &&
      processed.world.accountWrites = [vectorAuthority] := by
  decide

example :
    let setThenClearThenSet := processAuthorizationList (vectorAuthInput 0 0 0) (vectorAuthState 0 vectorGas)
      [{ vectorAuthorization 0 0 with perAuthStateCharge := 2 }, { vectorAuthorization 1 0 with clearsDelegation := true, perAuthStateCharge := 2 }, { vectorAuthorization 2 0 with perAuthStateCharge := 2 }]
    setThenClearThenSet.failed = false && setThenClearThenSet.gas.stateGasUsed = 2 &&
      setThenClearThenSet.delegationSetFor = [vectorAuthority] &&
      (setThenClearThenSet.world.authorityStates.head?.getD (vectorAuthorityState 0 none)).current.nonce = 3 &&
      (setThenClearThenSet.world.authorityStates.head?.getD (vectorAuthorityState 0 none)).current.delegation = some "target" := by
  decide

example :
    let clearThenSet := processAuthorizationList (vectorAuthInput 0 0 0) (vectorAuthState 0 vectorGas)
      [{ vectorAuthorization 0 0 with clearsDelegation := true, perAuthStateCharge := 2 }, { vectorAuthorization 1 0 with perAuthStateCharge := 2 }]
    clearThenSet.failed = false && clearThenSet.gas.stateGasUsed = 2 &&
      clearThenSet.delegationSetFor = [vectorAuthority] &&
      (clearThenSet.world.authorityStates.head?.getD (vectorAuthorityState 0 none)).current.nonce = 2 &&
      (clearThenSet.world.authorityStates.head?.getD (vectorAuthorityState 0 none)).current.delegation = some "target" := by
  decide

example :
    let initialWorld := { vectorWorld with authorityStates := [vectorPreexistingDelegationState] }
    let initialState := { vectorAuthState 0 vectorGas with world := initialWorld }
    let input := { vectorAuthInput 0 0 0 with
      world := initialWorld, preExecutionWorld := initialWorld, authorizations := [{ vectorAuthorization 0 0 with account := vectorPreexistingDelegationState.original, delegationBefore := some "old", clearsDelegation := true }, { vectorAuthorization 1 0 with account := vectorPreexistingDelegationState.original, delegationBefore := some "old" }] }
    let clearThenSet := processAuthorizationList input initialState input.authorizations
    clearThenSet.failed = false && clearThenSet.gas.stateGasUsed = 0 &&
      clearThenSet.delegationSetFor = [] &&
      (clearThenSet.world.authorityStates.head?.getD vectorPreexistingDelegationState).current.nonce = 2 &&
      (clearThenSet.world.authorityStates.head?.getD vectorPreexistingDelegationState).current.delegation = some "target" := by
  decide

example :
    let input := vectorAuthorizationOogInput
    let state := { vectorAuthState 0 vectorAuthorizationOogGas with
      baseline := vectorAuthorizationOogGas }
    let processed := processAuthorizationTuple input state
      { vectorAuthorization 0 3 with perAuthStateCharge := 3 }
    processed.failed = true && processed.gas.value = 2 && processed.gas.stateReservoir = 0 &&
      (processed.world.authorityStates.head?.getD (vectorAuthorityState 0 none)).current.nonce = 0 &&
      processed.world.accountWrites = [] && processed.access.warmedAccounts = [vectorAuthority] &&
      processed.access.reads = [vectorAuthority] := by
  decide

example :
    let result := run vectorAuthorizationOogInput
    result.status = .preparationOog && result.gas.value = 5 && result.gas.stateReservoir = 0 &&
      (result.world.authorityStates.head?.getD (vectorAuthorityState 0 none)).current.nonce = 0 &&
      (result.world.authorityStates.head?.getD (vectorAuthorityState 0 none)).current.delegation = none &&
      result.access.warmedAccounts = [vectorAuthority, "sender"] &&
      result.access.reads = [vectorAuthority] &&
      result.events.any (fun event => event = Event.executionCharge "account-write" 3) &&
      result.events.any (fun event => event = Event.restore "preparation" vectorSnapshot) &&
      result.events.any (fun event => event = Event.snapshot "topLevel" vectorSnapshot) := by
  decide

example :
    let state := { vectorAuthState 0 vectorGas with world :=
      { vectorWorld with authorityStates := [{ authority := vectorAuthority, original := vectorAuthorityOriginal, current := vectorAccount false false 0 "" none 0, delegatedBeforeTx := none }] } }
    let charged := processAuthorizationTuple (vectorAuthInput 0 0 0) state
      { vectorAuthorization 0 0 with newAccountStateCharge := 1 }
    charged.gas.stateGasUsed = 1 && (charged.world.authorityStates.head?.getD (vectorAuthorityState 0 none)).current.nonce = 1 := by
  decide

def vectorPhysicalEmptyAuthorityOriginal : AccountFacts := vectorAccount true false 0 "" none 0
def vectorPhysicalEmptyAuthorityState : AuthorityStateFacts :=
  { authority := vectorAuthority, original := vectorPhysicalEmptyAuthorityOriginal, current := vectorPhysicalEmptyAuthorityOriginal, delegatedBeforeTx := none }

example :
    let physicalEmptyWorld := { vectorWorld with authorityStates := [vectorPhysicalEmptyAuthorityState] }
    let auth := { vectorAuthorization 0 0 with account := vectorPhysicalEmptyAuthorityOriginal, newAccountStateCharge := 1 }
    let input := { vectorAuthInput 0 0 0 with
      world := physicalEmptyWorld, preExecutionWorld := physicalEmptyWorld, authorizations := [auth] }
    let state := { vectorAuthState 0 vectorGas with world := physicalEmptyWorld }
    let charged := processAuthorizationTuple input state auth
    (physicalEmptyWorld.authorityStates.head?.getD vectorPhysicalEmptyAuthorityState).current.logicalExists = false &&
      charged.failed = false && charged.gas.stateGasUsed = 1 &&
      (charged.world.authorityStates.head?.getD vectorPhysicalEmptyAuthorityState).current.nonce = 1 &&
      (charged.world.authorityStates.head?.getD vectorPhysicalEmptyAuthorityState).current.physicalExists = true &&
      (charged.world.authorityStates.head?.getD vectorPhysicalEmptyAuthorityState).current.logicalExists = true := by
  decide

def vectorStorageOnlyAuthorityOriginal : AccountFacts :=
  { vectorPhysicalEmptyAuthorityOriginal with storageNonEmpty := true }
def vectorStorageOnlyAuthorityState : AuthorityStateFacts :=
  { authority := vectorAuthority, original := vectorStorageOnlyAuthorityOriginal, current := vectorStorageOnlyAuthorityOriginal, delegatedBeforeTx := none }

example :
    let storageOnlyWorld := { vectorWorld with authorityStates := [vectorStorageOnlyAuthorityState] }
    let auth := { vectorAuthorization 0 0 with account := vectorStorageOnlyAuthorityOriginal, newAccountStateCharge := 1 }
    let input := { vectorAuthInput 0 0 0 with
      world := storageOnlyWorld, preExecutionWorld := storageOnlyWorld, authorizations := [auth] }
    let state := { vectorAuthState 0 vectorGas with world := storageOnlyWorld }
    let charged := processAuthorizationTuple input state auth
    (storageOnlyWorld.authorityStates.head?.getD vectorStorageOnlyAuthorityState).current.logicalExists = false &&
      (storageOnlyWorld.authorityStates.head?.getD vectorStorageOnlyAuthorityState).current.storageNonEmpty = true &&
      charged.failed = false && charged.gas.stateGasUsed = 1 &&
      (charged.world.authorityStates.head?.getD vectorStorageOnlyAuthorityState).current.nonce = 1 &&
      (charged.world.authorityStates.head?.getD vectorStorageOnlyAuthorityState).current.logicalExists = true &&
      (charged.world.authorityStates.head?.getD vectorStorageOnlyAuthorityState).current.storageNonEmpty = true := by
  decide

example :
    let input := { vectorAuthInput 0 0 0 with handoff := { vectorHandoff with spec := { vectorSpec with eip8037 := false } } }
    let refunded := processAuthorizationTuple input (vectorAuthState 0 vectorGas)
      (vectorAuthorization 0 0)
    refunded.refunds = 1 && refunded.world.codeInsertRefunds = 1 := by
  decide

example :
    initialWrittenAccounts vectorInput = ["sender", "recipient"] &&
      initialWrittenAccounts { vectorInput with handoff := { vectorHandoff with tx :=
        { vectorTransaction with recipient := some "sender" } } } = ["sender"] := by
  decide

example :
    let inconsistentSenderAlias := { vectorInput with
      handoff := { vectorHandoff with
        context := { vectorContext with sender := vectorAuthority }, tx := { vectorTransaction with sender := vectorAuthority } }, world := { vectorWorld with
        authorityStates := [{ vectorAuthorityState 0 none with
          current := vectorAccount true true 0 "" none 99 }] }, relations := { vectorRelations with
        valueTransfer := { vectorRelations.valueTransfer with sender := vectorAuthority }, deadRecipient := { vectorRelations.deadRecipient with sender := vectorAuthority } } }
    inputFactsCoherent inconsistentSenderAlias = false := by
  decide

example :
    deadRecipient vectorInput vectorWorld = true &&
      deadRecipient { vectorInput with relations := { vectorRelations with deadRecipient :=
        { vectorRelations.deadRecipient with physicalExists := true, logicalExists := false, accountEmpty := true } } } vectorWorld = true &&
      deadRecipient { vectorInput with relations := { vectorRelations with deadRecipient :=
        { vectorRelations.deadRecipient with physicalExists := true, logicalExists := true, accountEmpty := false } } } vectorWorld = false &&
      deadRecipient { vectorInput with handoff := { vectorHandoff with tx :=
        { vectorTransaction with sender := "recipient" } }, relations := { vectorRelations with deadRecipient :=
        { vectorRelations.deadRecipient with sender := "recipient" } } } vectorWorld = false := by
  decide

example :
    let aliasWorld := { vectorWorld with authorityStates := [vectorAuthorityState 0 none] }
    let aliasInput := { vectorInput with
      handoff := { vectorHandoff with tx :=
        { vectorTransaction with recipient := some vectorAuthority, value := 1 } }, world := aliasWorld, preExecutionWorld := aliasWorld, relations := { vectorRelations with
        deadRecipient := { vectorRelations.deadRecipient with recipient := some vectorAuthority, physicalExists := true, logicalExists := false, accountEmpty := true }, valueTransfer := { vectorRelations.valueTransfer with recipient := some vectorAuthority } } }
    deadRecipient aliasInput aliasWorld = true &&
      deadRecipient aliasInput (applyAuthorizationWorld aliasInput aliasWorld (vectorAuthorization 0 0)) = false := by
  decide

def vectorCreateTransaction : TxFacts :=
  { vectorTransaction with isCreate := true, recipient := none, input := "initcode" }
def vectorCreateRelations : PreparationRelations :=
  { vectorRelations with
    deadRecipient := { vectorRelations.deadRecipient with recipient := none }, deployment := { vectorRelations.deployment with physicalExists := true, logicalExists := false, accountEmpty := true, storageNonEmpty := false, storageCleared := true, codeOrNonceCollision := false, collision := false, includeStorageCollision := false }, valueTransfer := { vectorRelations.valueTransfer with recipient := none } }
def vectorCreateInput : Input :=
  { vectorInput with
    handoff := { vectorHandoff with spec := { vectorSpec with eip8037 := false }, tx := vectorCreateTransaction }, relations := vectorCreateRelations }

example :
    let inconsistent := { vectorCreateInput with relations := { vectorCreateInput.relations with
      delegatedTarget := { vectorCreateInput.relations.delegatedTarget with target := some "target" } } }
    inputFactsCoherent inconsistent = false && (run inconsistent).status = .admissionMismatch := by
  decide

def vectorCreateCollisionInput : Input :=
  { vectorCreateInput with
    handoff := { vectorCreateInput.handoff with spec := { vectorCreateInput.handoff.spec with eip8037 := true } }, relations := { vectorCreateRelations with deployment :=
      { vectorCreateRelations.deployment with includeStorageCollision := true, storageCleared := false } } }

example :
    deploymentCollision { vectorCreateCollisionInput with relations := { vectorCreateCollisionInput.relations with deployment :=
      { vectorCreateCollisionInput.relations.deployment with storageNonEmpty := true, collision := true } } } = true &&
      deploymentCollision { vectorCreateCollisionInput with relations := { vectorCreateCollisionInput.relations with deployment :=
        { vectorCreateCollisionInput.relations.deployment with physicalExists := true, logicalExists := false, storageNonEmpty := false, collision := false } } } = false &&
      deploymentCollision { vectorCreateCollisionInput with relations := { vectorCreateCollisionInput.relations with deployment :=
        { vectorCreateCollisionInput.relations.deployment with physicalExists := false, logicalExists := false, storageNonEmpty := true, collision := true } } } = true := by
  decide

example :
    let input := { vectorCreateCollisionInput with relations := { vectorCreateCollisionInput.relations with deployment :=
      { vectorCreateCollisionInput.relations.deployment with storageNonEmpty := true, collision := true } } }
    let preparation := emptyResult input .vmBoundary none vectorGas vectorGas vectorWorld vectorAccess
      vectorEnvironment 0 [] none
    let result := prepareCall input preparation vectorEnvironment false
    result.status = .collision && result.gas.stateReservoir = 9 && result.world.accountReads = [] &&
      result.access.reads = ["recipient"] && result.events =
        [Event.snapshot "topLevel" vectorSnapshot, Event.deploymentRead "recipient", Event.stateCharge "create-state" 1, Event.restore "topLevel" vectorSnapshot] := by
  decide

example : inputFactsCoherent vectorCreateInput = true &&
    inputFactsCoherent { vectorCreateInput with relations := { vectorCreateRelations with deployment :=
      { vectorCreateRelations.deployment with storageCleared := false } } } = false := by
  decide

def vectorSenderPostAuthorizationWorld : WorldFacts :=
  { vectorWorld with authorityStates :=
      [{ authority := "sender", original := vectorAccount true true 0 "" none 100, current := vectorAccount true true 1 "delegation:target" (some "target") 100, delegatedBeforeTx := none }] }

example :
    deriveRecipient vectorCreateInput vectorWorld = some "create:0" &&
      deriveRecipient vectorCreateInput vectorSenderPostAuthorizationWorld = some "create:1" := by
  decide

example :
    valueDebitAmount { vectorInput with handoff := { vectorHandoff with options := { warmup := true, skipValidation := true }, tx := { vectorTransaction with value := 10 } } } { vectorWorld with currentBalance := 3 } = 3 &&
    valueDebitAmount { vectorInput with handoff := { vectorHandoff with options := vectorOptions, tx := { vectorTransaction with value := 10 } } } { vectorWorld with currentBalance := 3 } = 0 := by
  decide

def vectorSenderAuthorityState : AuthorityStateFacts :=
  { authority := "sender", original := vectorAccount true true 0 "" none 100, current := vectorAccount true true 0 "" none 100, delegatedBeforeTx := none }

example :
    let senderWorld := { vectorWorld with recipientBalance := 100, authorityStates := [vectorSenderAuthorityState] }
    let input := { vectorInput with
      handoff := { vectorHandoff with tx :=
        { vectorTransaction with sender := "sender", recipient := some "sender", value := 7 } }, world := senderWorld }
    let paid := payValue input senderWorld
    paid.currentBalance = 93 && paid.recipientBalance = 100 &&
      paid.accountWrites = ["sender"] &&
      (paid.authorityStates.head?.getD vectorSenderAuthorityState).current.balance = 93 := by
  decide

example :
    let result := run vectorInput
    result.status = .vmBoundary && result.world.currentBalance = 99 &&
      result.world.recipientBalance = 0 &&
      result.vmInput.isSome := by
  decide

example :
    let nullEnvironment := { vectorEnvironment with code := { vectorCode with isNull := true } }
    let preparation := emptyResult vectorInput .vmBoundary none vectorGas vectorGas vectorWorld vectorAccess
      nullEnvironment 0 [] none
    (prepareCall vectorInput preparation nullEnvironment false).status = .nullCode &&
      (prepareCall vectorInput preparation vectorEnvironment false).status = .vmBoundary := by
  decide

example :
    let delegated := { vectorInput with relations := { vectorRelations with delegatedTarget :=
      { vectorRelations.delegatedTarget with target := some "target", accessCharge := 101 } }, handoff := { vectorHandoff with spec := vectorSpec } }
    let preparation := emptyResult delegated .vmBoundary none vectorGas vectorGas vectorWorld vectorAccess
      vectorEnvironment 0 [] none
    let built := buildEnvironment delegated preparation false
    built.2 = true && built.1.access.warmedAccounts = ["recipient", "sender", "target"] &&
      built.1.access.reads = ["recipient"] && built.1.world.accountReads = ["recipient"] := by
  decide

example :
    let delegated := { vectorInput with relations := { vectorRelations with delegatedTarget :=
      { vectorRelations.delegatedTarget with target := some "target", accessCharge := 101 } }, access := { vectorAccess with warmedAccounts := ["target"] } }
    let preparation := emptyResult delegated .vmBoundary none vectorGas vectorGas vectorWorld
      delegated.access vectorEnvironment 0 [] none
    let built := buildEnvironment delegated preparation false
    built.2 = false && built.1.gas.value = vectorGas.value &&
      built.1.access.warmedAccounts = ["target", "recipient", "sender"] := by
  decide

example :
    let delegated := { vectorInput with relations := { vectorRelations with delegatedTarget :=
      { vectorRelations.delegatedTarget with target := some "target", isPrecompile := true, accessCharge := 101 } } }
    let preparation := emptyResult delegated .vmBoundary none vectorGas vectorGas vectorWorld vectorAccess
      vectorEnvironment 0 [] none
    let built := buildEnvironment delegated preparation false
    built.2 = false && built.1.gas.value = vectorGas.value &&
      built.1.gas.stateReservoir = vectorGas.stateReservoir &&
      built.1.access.warmedAccounts = ["recipient", "sender", "target"] &&
      built.1.access.reads = ["recipient", "target"] && built.1.world.accountReads = ["recipient", "target"] &&
      built.1.environment.code.target = some "target" && built.1.environment.code.bytes = "" &&
      built.1.environment.code.isEmpty = true && built.1.environment.code.isNull = false &&
      built.1.environment.codeSource = some "recipient" := by
  decide

example :
    let delegated := { vectorInput with relations := { vectorRelations with delegatedTarget :=
      { vectorRelations.delegatedTarget with target := some "target", account := vectorAccount true true 0 "runtime" (some "target") 0, accessCharge := 0 } } }
    let preparation := emptyResult delegated .vmBoundary none vectorGas vectorGas vectorWorld vectorAccess
      vectorEnvironment 0 [] none
    let built := buildEnvironment delegated preparation false
    built.2 = false && built.1.environment.code.target = some "target" &&
      built.1.environment.code.bytes = "runtime" && built.1.environment.code.isEmpty = false &&
      built.1.environment.codeSource = some "recipient" &&
      built.1.environment.executingAccount = "recipient" := by
  decide

example :
    let delegated := { vectorInput with
      handoff := { vectorHandoff with spec := { vectorSpec with useHotAndColdStorage := false } }, relations := { vectorRelations with delegatedTarget :=
        { vectorRelations.delegatedTarget with target := some "target", account := vectorAccount true true 0 "runtime" (some "target") 0, accessCharge := 101 } } }
    let preparation := emptyResult delegated .vmBoundary none vectorGas vectorGas vectorWorld vectorAccess
      vectorEnvironment 0 [] none
    let built := buildEnvironment delegated preparation false
    built.2 = false && built.1.gas.value = vectorGas.value &&
      built.1.access.warmedAccounts = [] &&
      built.1.access.reads = ["recipient", "target"] &&
      built.1.world.accountReads = ["recipient", "target"] &&
      built.1.environment.code.bytes = "runtime" &&
      !built.1.events.any (fun event => event = Event.delegatedTargetWarm "target") := by
  decide

example :
    let preparation := emptyResult vectorCreateInput .vmBoundary none vectorGas vectorGas vectorWorld vectorAccess
      vectorEnvironment 0 [] none
    let normal := buildEnvironment vectorCreateInput preparation false
    let oog := buildEnvironment vectorCreateInput preparation true
    normal.2 = false && normal.1.access.warmedAccounts = ["create:0", "sender"] &&
      normal.1.access.reads = [] && normal.1.world.accountReads = [] &&
      normal.1.environment.code.bytes = "initcode" && normal.1.environment.code.isEmpty = false &&
      normal.1.environment.code.isNull = false && normal.1.environment.input = "" &&
      oog.2 = false && oog.1.access.warmedAccounts = ["sender"] && oog.1.access.reads = [] &&
      oog.1.world.accountReads = [] && oog.1.environment.code.bytes = "initcode" &&
      oog.1.environment.code.isNull = false := by
  decide

example :
    inputFactsCoherent { vectorInput with
      handoff := { vectorHandoff with tx := { vectorTransaction with value := 10 } }, world := { vectorWorld with currentBalance := 3 }, relations := { vectorRelations with valueTransfer :=
        { vectorRelations.valueTransfer with senderBalance := 3, amount := 10 } } } = false &&
    (run { vectorInput with
      handoff := { vectorHandoff with tx := { vectorTransaction with value := 10 } }, world := { vectorWorld with currentBalance := 3 }, relations := { vectorRelations with valueTransfer :=
        { vectorRelations.valueTransfer with senderBalance := 3, amount := 10 } } }).status = .admissionMismatch := by
  decide

example :
    let warmupInput := { vectorInput with
      handoff := { vectorHandoff with options := { warmup := true, skipValidation := true }, tx := { vectorTransaction with value := 10 } }, world := { vectorWorld with currentBalance := 3 }, preExecutionWorld := { vectorWorld with currentBalance := 3 }, relations := { vectorRelations with valueTransfer :=
        { vectorRelations.valueTransfer with senderBalance := 3, amount := 10 } } }
    let result := run warmupInput
    result.status = .vmBoundary && result.world.currentBalance = 0 &&
      result.events.reverse.head? = some (Event.vmCallBoundary) &&
      result.events.any (fun event => event = Event.valueDebit "sender" 3) := by
  decide

example :
    let changed := { vectorInput with
      prePreparationGas := { vectorGas with value := 99 }, preparationFixedWidth := vectorFixedWidth { vectorGas with value := 99 } }
    inputFactsCoherent changed = false && (run changed).status = .admissionMismatch := by
  decide

example :
    let stray := { vectorInput with
      access := { vectorAccess with warmedAccounts := ["target"] }, handoff := { vectorHandoff with access := { vectorAccess with warmedAccounts := ["target"] } } }
    inputFactsCoherent stray = false && (run stray).status = .admissionMismatch := by
  decide

example :
    let refunded := { vectorInput with
      delegationRefunds := 1, handoff := { vectorHandoff with delegationRefunds := 1 }, world := { vectorWorld with delegationRefunds := 1 }, preExecutionWorld := { vectorWorld with delegationRefunds := 1 } }
    inputFactsCoherent refunded = false && (run refunded).status = .admissionMismatch := by
  decide

example :
    let processed := processAuthorization (vectorAuthInput 0 0 101)
    processed.2 = true && processed.1.executionIntrinsicGasStandard.stateGasUsed = 0 := by
  decide

end EvmTransactionPreparationExtractor.Reference
