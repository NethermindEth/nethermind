-- SPDX-License-Identifier: LGPL-3.0-only
-- Universal field-complete refinement and explicit sibling artifact identities.

import EvmTransactionPreparationExtractor.Generated.EvmTransactionPreparation
import EvmTransactionPreparationExtractor.Reference.EvmTransactionPreparationReference
import EvmTransactionPreparationExtractor.Refinement.Admission
import OrdinaryPostNonceDispatchExtractor.Generated.OrdinaryPostNonceDispatch
import OrdinaryPostNonceDispatchExtractor.Reference.OrdinaryPostNonceDispatchReference
import OrdinaryPostNonceDispatchExtractor.Refinement.OrdinaryPostNonceDispatch
import AuthorizationStateGasFoldExtractor.Generated.AuthorizationStateGasFold
import AuthorizationStateGasFoldExtractor.Refinement.AuthorizationStateGasFold
import Eip803x.Refinement.TransactionGasInitialization
import Eip803x.Refinement.StateGasCharge
import Eip803x.Refinement.StateGasTransition
import Lean.Elab.Tactic.Omega

namespace EvmTransactionPreparationExtractor.Refinement

namespace Generated
abbrev Input := EvmTransactionPreparationExtractor.Generated.Input
abbrev Result := EvmTransactionPreparationExtractor.Generated.Result
abbrev Status := EvmTransactionPreparationExtractor.Generated.Status
abbrev GasState := EvmTransactionPreparationExtractor.Generated.GasState
abbrev Snapshot := EvmTransactionPreparationExtractor.Generated.Snapshot
abbrev SnapshotPart := EvmTransactionPreparationExtractor.Generated.SnapshotPart
abbrev WorldFacts := EvmTransactionPreparationExtractor.Generated.WorldFacts
abbrev AccessObservation := EvmTransactionPreparationExtractor.Generated.AccessObservation
abbrev Environment := EvmTransactionPreparationExtractor.Generated.Environment
abbrev CodeIdentity := EvmTransactionPreparationExtractor.Generated.CodeIdentity
abbrev VmInput := EvmTransactionPreparationExtractor.Generated.VmInput
abbrev Event := EvmTransactionPreparationExtractor.Generated.Event
abbrev AuthorizationFacts := EvmTransactionPreparationExtractor.Generated.AuthorizationFacts
abbrev AuthorityStateFacts := EvmTransactionPreparationExtractor.Generated.AuthorityStateFacts
abbrev AuthorizationState := EvmTransactionPreparationExtractor.Generated.AuthorizationState
end Generated

namespace Reference
abbrev Input := EvmTransactionPreparationExtractor.Reference.Input
abbrev Result := EvmTransactionPreparationExtractor.Reference.Result
abbrev Status := EvmTransactionPreparationExtractor.Reference.Status
abbrev Gas := EvmTransactionPreparationExtractor.Reference.Gas
abbrev Snapshot := EvmTransactionPreparationExtractor.Reference.Snapshot
abbrev SnapshotPart := EvmTransactionPreparationExtractor.Reference.SnapshotPart
abbrev WorldFacts := EvmTransactionPreparationExtractor.Reference.WorldFacts
abbrev AccessObservation := EvmTransactionPreparationExtractor.Reference.AccessObservation
abbrev Environment := EvmTransactionPreparationExtractor.Reference.Environment
abbrev CodeIdentity := EvmTransactionPreparationExtractor.Reference.CodeIdentity
abbrev VmInput := EvmTransactionPreparationExtractor.Reference.VmInput
abbrev Event := EvmTransactionPreparationExtractor.Reference.Event
abbrev AuthorizationFacts := EvmTransactionPreparationExtractor.Reference.AuthorizationFacts
abbrev AuthorityStateFacts := EvmTransactionPreparationExtractor.Reference.AuthorityStateFacts
abbrev AuthorizationState := EvmTransactionPreparationExtractor.Reference.AuthorizationState
end Reference

def sourceAdmissionSha256 : String := EvmTransactionPreparationExtractor.Generated.admissionSha256
def semanticPlanSha256 : String := EvmTransactionPreparationExtractor.Generated.semanticPlanSha256

/- The strings are provenance labels.  The identity-only sibling theorem below
   instantiates only the ordinary-dispatch and authorization-fold pins; the
   remaining journal, frame, and gas pins are explicit external adapter
   obligations for a future production refinement. -/
def ordinaryPostNonceArtifactSha256 : String := "850ca2852786e8978f046d7b7b3999d8624108cf3e8aee04cab7baaee274432d"
def ordinaryPostNonceManifestSha256 : String := "df941ccbc98cbf479f1b8accfff2f2ad67fd0076f98975e0042e4bb8e1f505f3"
def ordinaryPostNonceLeanSha256 : String := "645ee68e4edf2e4d81d3e27da202a588d57b9f8ffdc883f035e3c55a0c1f475c"
def ordinaryPostNonceReferenceSha256 : String := "2d9347ad22562e3f2430455fc035c580458e0754060d46f1ec43c58f5caeea1b"
def ordinaryPostNonceRefinementSha256 : String := "7352ca6fd6124edeb8f622ef6272a825d9e71b8cd17c454304aa5924e256d256"
def authorizationFoldRefinementSha256 : String := "8340d14520ac44e4b8e542373be1bbcda6b215fb381c2f879f3bc872075e950d"
def worldJournalRefinementSha256 : String := "f37d5ebd59a9f6353bf5200f6dd1952c1591cde70f408d23403fb9f0ecd91407"
def frameJournalRefinementSha256 : String := "36fbd6da282c88a590b038d8824a4149f03c1b9d2abcce61b1ed498524948737"
def frameControlRefinementSha256 : String := "a3a4e2b02216bb484ce5f3164c95a3887df648d3fb0afd79ad22393da490a266"
def txGasRefinementSha256 : String := "8986504ad786cc621dcb0c74400980366847c43d832830973f52a5d38c290578"
def stateChargeRefinementSha256 : String := "4cb359f611ef04300618f9109ac89ca6b32c857e8e9e6c2305e11a0323d57ead"
def stateTransitionRefinementSha256 : String := "a3d3beb8004c1431e2550a702f84d816751657ad90c8320ddc5809956da41cec"
def accountAccessRefinementSha256 : String := "7dfef78e9fb1fc429799537b12a4eed0dd18d183c950bd73bdfae77f25345b9d"

def mapGas (gas : Generated.GasState) : Reference.Gas :=
  { value := gas.value, stateReservoir := gas.stateReservoir, stateGasUsed := gas.stateGasUsed,
    stateGasSpill := gas.stateGasSpill, stateGasSpillRefunded := gas.stateGasSpillRefunded }

def mapSnapshotPart (part : Generated.SnapshotPart) : Reference.SnapshotPart :=
  { persistent := part.persistent, transient := part.transient }

def mapSnapshot (snapshot : Generated.Snapshot) : Reference.Snapshot :=
  { storage := mapSnapshotPart snapshot.storage, state := snapshot.state, blockAccessList := snapshot.blockAccessList }

def mapAuthorityState (state : Generated.AuthorityStateFacts) : Reference.AuthorityStateFacts :=
  { authority := state.authority, original := { physicalExists := state.original.physicalExists, logicalExists := state.original.logicalExists, nonce := state.original.nonce, code := state.original.code, delegation := state.original.delegation, balance := state.original.balance, storageNonEmpty := state.original.storageNonEmpty }, current := { physicalExists := state.current.physicalExists, logicalExists := state.current.logicalExists, nonce := state.current.nonce, code := state.current.code, delegation := state.current.delegation, balance := state.current.balance, storageNonEmpty := state.current.storageNonEmpty }, delegatedBeforeTx := state.delegatedBeforeTx }

def mapAuthorityStates : List Generated.AuthorityStateFacts → List Reference.AuthorityStateFacts
  | [] => []
  | state :: rest => mapAuthorityState state :: mapAuthorityStates rest

def mapWorld (world : Generated.WorldFacts) : Reference.WorldFacts :=
  { physicalExists := world.physicalExists, logicalExists := world.logicalExists,
    originalPhysicalExists := world.originalPhysicalExists, currentPhysicalExists := world.currentPhysicalExists,
    originalLogicalExists := world.originalLogicalExists, currentLogicalExists := world.currentLogicalExists,
    originalNonce := world.originalNonce, currentNonce := world.currentNonce,
    originalCode := world.originalCode, currentCode := world.currentCode,
    originalDelegation := world.originalDelegation, currentDelegation := world.currentDelegation,
    originalBalance := world.originalBalance, currentBalance := world.currentBalance,
    recipientBalance := world.recipientBalance, delegationRefunds := world.delegationRefunds,
    accountWarm := world.accountWarm, storageWarm := world.storageWarm,
    accountReads := world.accountReads, accountWrites := world.accountWrites,
    storageReads := world.storageReads, storageWrites := world.storageWrites,
    codeInsertRefunds := world.codeInsertRefunds, traceAccess := world.traceAccess,
     authorityStates := mapAuthorityStates world.authorityStates }

theorem mapWorld_projection (world : Generated.WorldFacts) :
    mapWorld
        { physicalExists := world.physicalExists, logicalExists := world.logicalExists,
          originalPhysicalExists := world.originalPhysicalExists,
          currentPhysicalExists := world.currentPhysicalExists,
          originalLogicalExists := world.originalLogicalExists,
          currentLogicalExists := world.currentLogicalExists,
          originalNonce := world.originalNonce, currentNonce := world.currentNonce,
          originalCode := world.originalCode, currentCode := world.currentCode,
          originalDelegation := world.originalDelegation, currentDelegation := world.currentDelegation,
          originalBalance := world.originalBalance, currentBalance := world.currentBalance,
          recipientBalance := world.recipientBalance, delegationRefunds := world.delegationRefunds,
          accountWarm := world.accountWarm, storageWarm := world.storageWarm,
          accountReads := world.accountReads, accountWrites := world.accountWrites,
          storageReads := world.storageReads, storageWrites := world.storageWrites,
          codeInsertRefunds := world.codeInsertRefunds, traceAccess := world.traceAccess,
          authorityStates := world.authorityStates } = mapWorld world := by
  cases world
  rfl

theorem mapWorld_projection_withCodeInsert (world : Generated.WorldFacts) (extra : Nat) :
    mapWorld
        { physicalExists := world.physicalExists, logicalExists := world.logicalExists,
          originalPhysicalExists := world.originalPhysicalExists,
          currentPhysicalExists := world.currentPhysicalExists,
          originalLogicalExists := world.originalLogicalExists,
          currentLogicalExists := world.currentLogicalExists,
          originalNonce := world.originalNonce, currentNonce := world.currentNonce,
          originalCode := world.originalCode, currentCode := world.currentCode,
          originalDelegation := world.originalDelegation, currentDelegation := world.currentDelegation,
          originalBalance := world.originalBalance, currentBalance := world.currentBalance,
          recipientBalance := world.recipientBalance, delegationRefunds := world.delegationRefunds,
          accountWarm := world.accountWarm, storageWarm := world.storageWarm,
          accountReads := world.accountReads, accountWrites := world.accountWrites,
          storageReads := world.storageReads, storageWrites := world.storageWrites,
          codeInsertRefunds := world.codeInsertRefunds + extra, traceAccess := world.traceAccess,
          authorityStates := world.authorityStates } =
      { mapWorld world with codeInsertRefunds := world.codeInsertRefunds + extra } := by
  cases world
  rfl

theorem mapWorld_recordUpdate (world : Generated.WorldFacts) (extra : Nat) :
    mapWorld { world with codeInsertRefunds := world.codeInsertRefunds + extra } =
      { mapWorld world with codeInsertRefunds := world.codeInsertRefunds + extra } := by
  cases world
  rfl

def mapAccess (access : Generated.AccessObservation) : Reference.AccessObservation :=
  { warmedAccounts := access.warmedAccounts, warmedStorage := access.warmedStorage,
    reads := access.reads, tracing := access.tracing }

def mapCode (code : Generated.CodeIdentity) : Reference.CodeIdentity :=
  { source := code.source, target := code.target, bytes := code.bytes, isEmpty := code.isEmpty, isNull := code.isNull }

def mapEnvironment (environment : Generated.Environment) : Reference.Environment :=
  { code := mapCode environment.code, executingAccount := environment.executingAccount,
    caller := environment.caller, codeSource := environment.codeSource, callDepth := environment.callDepth,
    value := environment.value, input := environment.input }

def mapAuthorization (auth : Generated.AuthorizationFacts) : Reference.AuthorizationFacts :=
  { authority := auth.authority, codeAddress := auth.codeAddress, chainIdValid := auth.chainIdValid, nonceValid := auth.nonceValid, signatureValid := auth.signatureValid, account := { physicalExists := auth.account.physicalExists, logicalExists := auth.account.logicalExists, nonce := auth.account.nonce, code := auth.account.code, delegation := auth.account.delegation, balance := auth.account.balance, storageNonEmpty := auth.account.storageNonEmpty }, expectedNonce := auth.expectedNonce, delegationBefore := auth.delegationBefore, clearsDelegation := auth.clearsDelegation, newAccountStateCharge := auth.newAccountStateCharge, accountWriteCharge := auth.accountWriteCharge, perAuthStateCharge := auth.perAuthStateCharge }

def mapAuthorizations : List Generated.AuthorizationFacts → List Reference.AuthorizationFacts
  | [] => []
  | auth :: rest => mapAuthorization auth :: mapAuthorizations rest

theorem mapAuthorizations_isEmpty (auths : List Generated.AuthorizationFacts) :
    (mapAuthorizations auths).isEmpty = auths.isEmpty := by
  cases auths <;> rfl

def mapTx (tx : Generated.TxFacts) : Reference.TxFacts :=
  { isCreate := tx.isCreate, isSetCode := tx.isSetCode, sender := tx.sender, recipient := tx.recipient,
    value := tx.value, input := tx.input, accessListAccounts := tx.accessListAccounts,
    accessListStorage := tx.accessListStorage, coinbase := tx.coinbase }

def mapVmInput (input : Generated.VmInput) : Reference.VmInput :=
  { gas := mapGas input.gas, executionType := input.executionType, environment := mapEnvironment input.environment,
    access := mapAccess input.access, snapshot := mapSnapshot input.snapshot }

def mapEvent : Generated.Event → Reference.Event
  | .txContext context => .txContext { sender := context.sender, codeRepository := context.codeRepository, blobHashes := context.blobHashes, opcodeGasPrice := context.opcodeGasPrice }
  | .stackTracker tracing => .stackTracker tracing
  | .metrics name => .metrics name
  | .authorizationWarm authority => .authorizationWarm authority
  | .authorizationRead authority => .authorizationRead authority
  | .stateCharge label amount => .stateCharge label amount
  | .executionCharge label amount => .executionCharge label amount
  | .accountWrite authority => .accountWrite authority
  | .delegationWrite authority target => .delegationWrite authority target
  | .recipientRead recipient => .recipientRead recipient
  | .accessWarm accounts storage => .accessWarm accounts storage
  | .delegatedTargetRead target => .delegatedTargetRead target
  | .delegatedTargetWarm target => .delegatedTargetWarm target
  | .deploymentRead recipient => .deploymentRead recipient
  | .snapshot which value => .snapshot which (mapSnapshot value)
  | .restore which value => .restore which (mapSnapshot value)
  | .valueDebit sender amount => .valueDebit sender amount
  | .environmentRent environment => .environmentRent (mapEnvironment environment)
  | .topFrameRent input => .topFrameRent (mapVmInput input)
  | .topFrameHalt gas environment => .topFrameHalt (mapGas gas) (mapEnvironment environment)
  | .accessReport access => .accessReport (mapAccess access)
  | .vmCallBoundary => .vmCallBoundary

def mapEvents : List Generated.Event → List Reference.Event
  | [] => []
  | event :: rest => mapEvent event :: mapEvents rest

def mapStatus : Generated.Status → Reference.Status
  | .admissionMismatch => .admissionMismatch
  | .preparationOog => .preparationOog
  | .collision => .collision
  | .nullCode => .nullCode
  | .vmBoundary => .vmBoundary

def mapAuthorizationState (state : Generated.AuthorizationState) : Reference.AuthorizationState :=
  { gas := mapGas state.gas, baseline := mapGas state.baseline, world := mapWorld state.world,
    access := mapAccess state.access, refunds := state.refunds,
    writtenAuthorities := state.writtenAuthorities, delegationSetFor := state.delegationSetFor,
    events := mapEvents state.events, failed := state.failed }

theorem mapAuthorizationState_record
    (gas baseline : Generated.GasState) (world : Generated.WorldFacts)
    (access : Generated.AccessObservation) (refunds : Nat)
    (writtenAuthorities delegationSetFor : List String)
    (events : List Generated.Event) (failed : Bool) :
    mapAuthorizationState
        { gas := gas, baseline := baseline, world := world, access := access, refunds := refunds,
          writtenAuthorities := writtenAuthorities, delegationSetFor := delegationSetFor,
          events := events, failed := failed } =
      { gas := mapGas gas, baseline := mapGas baseline, world := mapWorld world,
        access := mapAccess access, refunds := refunds, writtenAuthorities := writtenAuthorities,
        delegationSetFor := delegationSetFor, events := mapEvents events, failed := failed } := by
  rfl

def mapResult (result : Generated.Result) : Reference.Result :=
  { status := mapStatus result.status,
    error := result.error, gas := mapGas result.gas, executionIntrinsicGasStandard := mapGas result.executionIntrinsicGasStandard,
    prePreparationGas := mapGas result.prePreparationGas,
    preExecutionSnapshot := mapSnapshot result.preExecutionSnapshot, topLevelSnapshot := mapSnapshot result.topLevelSnapshot,
    postIntrinsicStateReservoir := result.postIntrinsicStateReservoir, world := mapWorld result.world,
    access := mapAccess result.access, environment := mapEnvironment result.environment, code := mapCode result.code,
    input := result.input, delegationRefunds := result.delegationRefunds,
    vmInput := result.vmInput.map mapVmInput, events := mapEvents result.events }

def mapHandoff (handoff : Generated.EvmHandoff) : Reference.EvmHandoff :=
  { context := { sender := handoff.context.sender, codeRepository := handoff.context.codeRepository, blobHashes := handoff.context.blobHashes, opcodeGasPrice := handoff.context.opcodeGasPrice }, gas := mapGas handoff.gas, access := mapAccess handoff.access, tx := mapTx handoff.tx, spec := { eip7702 := handoff.spec.eip7702, eip8037 := handoff.spec.eip8037, eip8038 := handoff.spec.eip8038, useHotAndColdStorage := handoff.spec.useHotAndColdStorage, useTxAccessLists := handoff.spec.useTxAccessLists, addCoinbaseToTxAccessList := handoff.spec.addCoinbaseToTxAccessList }, options := { warmup := handoff.options.warmup, skipValidation := handoff.options.skipValidation }, executionIntrinsicGasStandard := mapGas handoff.executionIntrinsicGasStandard, environment := mapEnvironment handoff.environment, delegationRefunds := handoff.delegationRefunds, exactBoundary := handoff.exactBoundary }

def mapRelations (relations : Generated.PreparationRelations) : Reference.PreparationRelations :=
   { delegatedTarget := { target := relations.delegatedTarget.target, isPrecompile := relations.delegatedTarget.isPrecompile, account := { physicalExists := relations.delegatedTarget.account.physicalExists, logicalExists := relations.delegatedTarget.account.logicalExists, nonce := relations.delegatedTarget.account.nonce, code := relations.delegatedTarget.account.code, delegation := relations.delegatedTarget.account.delegation, balance := relations.delegatedTarget.account.balance, storageNonEmpty := relations.delegatedTarget.account.storageNonEmpty }, accessCharge := relations.delegatedTarget.accessCharge }, deadRecipient := { sender := relations.deadRecipient.sender, recipient := relations.deadRecipient.recipient, physicalExists := relations.deadRecipient.physicalExists, logicalExists := relations.deadRecipient.logicalExists, accountEmpty := relations.deadRecipient.accountEmpty, stateCharge := relations.deadRecipient.stateCharge }, deployment := { physicalExists := relations.deployment.physicalExists, logicalExists := relations.deployment.logicalExists, accountEmpty := relations.deployment.accountEmpty, storageNonEmpty := relations.deployment.storageNonEmpty, storageCleared := relations.deployment.storageCleared, codeOrNonceCollision := relations.deployment.codeOrNonceCollision, collision := relations.deployment.collision, includeStorageCollision := relations.deployment.includeStorageCollision, stateCharge := relations.deployment.stateCharge }, valueTransfer := { sender := relations.valueTransfer.sender, recipient := relations.valueTransfer.recipient, senderBalance := relations.valueTransfer.senderBalance, recipientBalance := relations.valueTransfer.recipientBalance, amount := relations.valueTransfer.amount } }

def mapInput (input : Generated.Input) : Reference.Input :=
  { handoff := mapHandoff input.handoff,
    prePreparationGas := mapGas input.prePreparationGas,
    preExecutionIntrinsicGasStandard := mapGas input.preExecutionIntrinsicGasStandard,
    hasPreExecutionSnapshot := input.hasPreExecutionSnapshot,
    preExecutionSnapshot := mapSnapshot input.preExecutionSnapshot,
    topLevelSnapshot := mapSnapshot input.topLevelSnapshot,
    postIntrinsicStateReservoir := input.postIntrinsicStateReservoir,
    preExecutionWorld := mapWorld input.preExecutionWorld, world := mapWorld input.world,
    access := mapAccess input.access, authorizations := mapAuthorizations input.authorizations,
    relations := mapRelations input.relations, environment := mapEnvironment input.environment,
     fixedWidth := { value := { raw := input.fixedWidth.value.raw }, stateReservoir := { raw := input.fixedWidth.stateReservoir.raw }, stateGasUsed := { raw := input.fixedWidth.stateGasUsed.raw }, stateGasSpill := { raw := input.fixedWidth.stateGasSpill.raw }, stateGasSpillRefunded := { raw := input.fixedWidth.stateGasSpillRefunded.raw } },
     preparationFixedWidth := { value := { raw := input.preparationFixedWidth.value.raw }, stateReservoir := { raw := input.preparationFixedWidth.stateReservoir.raw }, stateGasUsed := { raw := input.preparationFixedWidth.stateGasUsed.raw }, stateGasSpill := { raw := input.preparationFixedWidth.stateGasSpill.raw }, stateGasSpillRefunded := { raw := input.preparationFixedWidth.stateGasSpillRefunded.raw } },
     baselineFixedWidth := { value := { raw := input.baselineFixedWidth.value.raw }, stateReservoir := { raw := input.baselineFixedWidth.stateReservoir.raw }, stateGasUsed := { raw := input.baselineFixedWidth.stateGasUsed.raw }, stateGasSpill := { raw := input.baselineFixedWidth.stateGasSpill.raw }, stateGasSpillRefunded := { raw := input.baselineFixedWidth.stateGasSpillRefunded.raw } },
    preBaselineFixedWidth := { value := { raw := input.preBaselineFixedWidth.value.raw }, stateReservoir := { raw := input.preBaselineFixedWidth.stateReservoir.raw }, stateGasUsed := { raw := input.preBaselineFixedWidth.stateGasUsed.raw }, stateGasSpill := { raw := input.preBaselineFixedWidth.stateGasSpill.raw }, stateGasSpillRefunded := { raw := input.preBaselineFixedWidth.stateGasSpillRefunded.raw } },
    delegationRefunds := input.delegationRefunds }

theorem mapInput_handoff (input : Generated.Input) :
    (mapInput input).handoff = mapHandoff input.handoff := by rfl

theorem mapInput_prePreparationGas (input : Generated.Input) :
    (mapInput input).prePreparationGas = mapGas input.prePreparationGas := by rfl

theorem mapInput_preExecutionIntrinsicGasStandard (input : Generated.Input) :
    (mapInput input).preExecutionIntrinsicGasStandard =
      mapGas input.preExecutionIntrinsicGasStandard := by rfl

theorem mapInput_preExecutionSnapshot (input : Generated.Input) :
    (mapInput input).preExecutionSnapshot = mapSnapshot input.preExecutionSnapshot := by rfl

theorem mapInput_hasPreExecutionSnapshot (input : Generated.Input) :
    (mapInput input).hasPreExecutionSnapshot = input.hasPreExecutionSnapshot := by rfl

theorem mapInput_topLevelSnapshot (input : Generated.Input) :
    (mapInput input).topLevelSnapshot = mapSnapshot input.topLevelSnapshot := by rfl

theorem mapInput_preExecutionWorld (input : Generated.Input) :
    (mapInput input).preExecutionWorld = mapWorld input.preExecutionWorld := by rfl

theorem mapInput_world (input : Generated.Input) :
    (mapInput input).world = mapWorld input.world := by rfl

theorem mapInput_access (input : Generated.Input) :
    (mapInput input).access = mapAccess input.access := by rfl

theorem mapInput_authorizations (input : Generated.Input) :
    (mapInput input).authorizations = mapAuthorizations input.authorizations := by rfl

theorem mapInput_relations (input : Generated.Input) :
    (mapInput input).relations = mapRelations input.relations := by rfl

theorem mapInput_environment (input : Generated.Input) :
    (mapInput input).environment = mapEnvironment input.environment := by rfl

theorem mapInput_delegationRefunds (input : Generated.Input) :
    (mapInput input).delegationRefunds = input.delegationRefunds := by rfl

/- This is the typed, conditional entry adapter for the production call.  Its
   fields are source-entry facts, rather than a result-agreement witness.  The
   package's enriched handoff is the entry projection (the ordinary sibling
   handoff has no access tracker), so its access is the fresh tracker's modeled
   AccessObservation projection;
   Result.access and VmInput.access are the later post-preparation boundary
   projection.  The adapter binds the fresh tracker/tracing and zero refund
   initialization to that entry handoff,
   then bind the entry/pre-execution world facts, gas objects, code/environment,
   empty-or-captured preparation snapshot, the exact EIP-7702 authorization-list
   presence relation, target gas policy, and exact VM boundary.  A captured
   snapshot is opaque here (its provenance is the admitted `TakeSnapshot`
   binding); journal internals and restore correctness are outside this
   adapter. -/
structure SourceEntryAdapter (input : Generated.Input) : Prop where
  accessHandoff : EvmTransactionPreparationExtractor.Generated.accessFactsEqual input.access input.handoff.access = true
  freshTracker : EvmTransactionPreparationExtractor.Generated.freshAccess input.access = true
  tracing : input.access.tracing = input.handoff.access.tracing
  zeroDelegationRefunds :
    input.delegationRefunds = 0 ∧ input.handoff.delegationRefunds = 0 ∧
      input.world.delegationRefunds = 0 ∧ input.preExecutionWorld.delegationRefunds = 0
  worldRoute : EvmTransactionPreparationExtractor.Generated.worldFactsEqual input.preExecutionWorld input.world = true
  preparationGas : EvmTransactionPreparationExtractor.Generated.gasFactsEqual input.prePreparationGas input.handoff.gas = true
  intrinsicGas : EvmTransactionPreparationExtractor.Generated.gasFactsEqual input.preExecutionIntrinsicGasStandard input.handoff.executionIntrinsicGasStandard = true
  environmentRoute : EvmTransactionPreparationExtractor.Generated.environmentFactsEqual input.environment input.handoff.environment = true
  snapshotPresence : input.hasPreExecutionSnapshot =
    (input.handoff.spec.eip7702 && EvmTransactionPreparationExtractor.Generated.hasAuthorization input.handoff.tx input.authorizations && input.handoff.spec.eip8037)
  accessLineage : EvmTransactionPreparationExtractor.Generated.sourceAccessLineageAdmitted = true
  preparationSnapshot : input.hasPreExecutionSnapshot = true ∨ EvmTransactionPreparationExtractor.Generated.snapshotIsEmpty input.preExecutionSnapshot = true
  codeRepository : input.handoff.context.codeRepository ≠ ""
  gasPolicy : EvmTransactionPreparationExtractor.Generated.targetGasPolicy = "EthereumGasPolicy"
  boundary : input.handoff.exactBoundary = "VirtualMachine.ExecuteTransaction"

set_option maxRecDepth 100000 in
theorem sourceEntryAdapter_coherent
    (input : Generated.Input) (adapter : SourceEntryAdapter input) :
    EvmTransactionPreparationExtractor.Generated.sourceEntryFactsCoherent input = true := by
  rcases adapter.preparationSnapshot with hPresent | hEmpty
  · have hParts : (input.handoff.spec.eip7702 = true ∧
        EvmTransactionPreparationExtractor.Generated.hasAuthorization input.handoff.tx input.authorizations = true) ∧
        input.handoff.spec.eip8037 = true := by
      have hPresence := adapter.snapshotPresence
      rw [hPresent] at hPresence
      have hPresence' :
          (input.handoff.spec.eip7702 &&
              EvmTransactionPreparationExtractor.Generated.hasAuthorization input.handoff.tx input.authorizations &&
            input.handoff.spec.eip8037) = true := by
        exact hPresence.symm
      cases hEip : input.handoff.spec.eip7702 <;>
        cases hAuth : EvmTransactionPreparationExtractor.Generated.hasAuthorization
          input.handoff.tx input.authorizations <;>
        cases h8037 : input.handoff.spec.eip8037 <;>
        simp [hEip, hAuth, h8037] at hPresence' ⊢
    simp [EvmTransactionPreparationExtractor.Generated.sourceEntryFactsCoherent,
      EvmTransactionPreparationExtractor.Generated.snapshotPresenceCoherent,
      adapter.accessHandoff, adapter.freshTracker, adapter.tracing,
      adapter.zeroDelegationRefunds, adapter.worldRoute, adapter.preparationGas,
      adapter.intrinsicGas, adapter.environmentRoute, adapter.snapshotPresence,
      adapter.accessLineage, adapter.codeRepository, adapter.gasPolicy, adapter.boundary,
      hParts]
  · simp [EvmTransactionPreparationExtractor.Generated.sourceEntryFactsCoherent,
      EvmTransactionPreparationExtractor.Generated.snapshotPresenceCoherent,
      adapter.accessHandoff, adapter.freshTracker, adapter.tracing,
      adapter.zeroDelegationRefunds, adapter.worldRoute, adapter.preparationGas,
      adapter.intrinsicGas, adapter.environmentRoute, adapter.snapshotPresence,
      adapter.accessLineage, adapter.codeRepository, adapter.gasPolicy, adapter.boundary, hEmpty]

structure RepresentationObligation (input : Generated.Input) : Prop where
  /- `fixedWidthValidForInput` is the single source-bound representation
     predicate.  It expands to the five fields of each of the four gas
     objects, their UInt64/Int64 range and two's-complement round-trip
     obligations, the post-intrinsic reservoir, and every modeled charge.
     These input ranges do not prove reservoir nonnegativity or no signed
     counter wrap during execution; production StateGasChargeKernel agreement
     on those admitted model states remains outside this theorem's claim.
     Keeping that conjunction as one premise prevents the refinement theorem
     from advertising duplicated fields that it does not independently use. -/
  fixedWidthDomain : EvmTransactionPreparationExtractor.Generated.fixedWidthValidForInput input = true
  sourceEntry : SourceEntryAdapter input
  inputFacts : EvmTransactionPreparationExtractor.Generated.inputFactsCoherent input = true

/- These lemmas are the representation-preserving part of the refinement.  They
   unfold the two independently authored kernels and establish equality by
   structural reduction; no caller can supply a proposition whose conclusion
   is already a generated/reference result equality. -/
theorem map_gasFieldsValid (gas : Generated.GasState) :
    EvmTransactionPreparationExtractor.Generated.gasFieldsValid gas =
      EvmTransactionPreparationExtractor.Reference.gasFieldsValid (mapGas gas) := by
  cases gas
  rfl

theorem map_stateSpill (gas : Generated.GasState) (amount : Int) :
    EvmTransactionPreparationExtractor.Generated.stateSpill gas amount =
      EvmTransactionPreparationExtractor.Reference.stateSpill (mapGas gas) amount := by
  cases gas
  rfl

theorem map_chargeState (gas : Generated.GasState) (amount : Int) :
    (EvmTransactionPreparationExtractor.Generated.chargeState gas amount).map mapGas =
      EvmTransactionPreparationExtractor.Reference.chargeState (mapGas gas) amount := by
  have hvalid : ∀ g : Generated.GasState,
      EvmTransactionPreparationExtractor.Generated.gasFieldsValid g =
        EvmTransactionPreparationExtractor.Reference.gasFieldsValid (mapGas g) := by
    intro g
    exact map_gasFieldsValid g
  by_cases hValid : EvmTransactionPreparationExtractor.Generated.gasFieldsValid gas = true
  · have hValidRef : EvmTransactionPreparationExtractor.Reference.gasFieldsValid (mapGas gas) = true := by
      rw [← hvalid gas]
      exact hValid
    simp only [mapGas] at hValidRef
    by_cases hNonPos : amount ≤ 0
    · simp [EvmTransactionPreparationExtractor.Generated.chargeState,
        EvmTransactionPreparationExtractor.Reference.chargeState, hValid, hValidRef, hNonPos,
        mapGas]
    · by_cases hInsufficient : gas.value < EvmTransactionPreparationExtractor.Generated.stateSpill gas amount
      · have hInsufficientRef : gas.value <
            EvmTransactionPreparationExtractor.Reference.stateSpill
              { value := gas.value, stateReservoir := gas.stateReservoir, stateGasUsed := gas.stateGasUsed,
                stateGasSpill := gas.stateGasSpill, stateGasSpillRefunded := gas.stateGasSpillRefunded } amount := by
          simpa [EvmTransactionPreparationExtractor.Generated.stateSpill,
            EvmTransactionPreparationExtractor.Reference.stateSpill] using hInsufficient
        simp [EvmTransactionPreparationExtractor.Generated.chargeState,
            EvmTransactionPreparationExtractor.Reference.chargeState,
            hValid, hValidRef, hNonPos, hInsufficient, hInsufficientRef, mapGas]
      · have hSufficientRef : EvmTransactionPreparationExtractor.Reference.stateSpill
            { value := gas.value, stateReservoir := gas.stateReservoir, stateGasUsed := gas.stateGasUsed,
              stateGasSpill := gas.stateGasSpill, stateGasSpillRefunded := gas.stateGasSpillRefunded } amount ≤ gas.value := by
          have hNotRef : ¬ gas.value < EvmTransactionPreparationExtractor.Reference.stateSpill
              { value := gas.value, stateReservoir := gas.stateReservoir, stateGasUsed := gas.stateGasUsed,
                stateGasSpill := gas.stateGasSpill, stateGasSpillRefunded := gas.stateGasSpillRefunded } amount := by
            intro h
            apply hInsufficient
            simpa [EvmTransactionPreparationExtractor.Generated.stateSpill,
              EvmTransactionPreparationExtractor.Reference.stateSpill] using h
          omega
        have hValueRefFalse : ¬ gas.value < EvmTransactionPreparationExtractor.Reference.stateSpill
            { value := gas.value, stateReservoir := gas.stateReservoir, stateGasUsed := gas.stateGasUsed,
              stateGasSpill := gas.stateGasSpill, stateGasSpillRefunded := gas.stateGasSpillRefunded } amount := by
          omega
        have hSpillRef : EvmTransactionPreparationExtractor.Generated.stateSpill gas amount =
            EvmTransactionPreparationExtractor.Reference.stateSpill
              { value := gas.value, stateReservoir := gas.stateReservoir, stateGasUsed := gas.stateGasUsed,
                stateGasSpill := gas.stateGasSpill, stateGasSpillRefunded := gas.stateGasSpillRefunded } amount := by
          cases gas
          rfl
        by_cases hReservoir : amount ≤ gas.stateReservoir
        · let charged : Generated.GasState :=
            { value := gas.value, stateReservoir := gas.stateReservoir - amount,
              stateGasUsed := gas.stateGasUsed + amount, stateGasSpill := gas.stateGasSpill,
              stateGasSpillRefunded := gas.stateGasSpillRefunded }
          by_cases hChargedValid : EvmTransactionPreparationExtractor.Generated.gasFieldsValid charged = true
          · have hChargedValidRef : EvmTransactionPreparationExtractor.Reference.gasFieldsValid (mapGas charged) = true := by
              rw [← hvalid charged]
              exact hChargedValid
            simp only [mapGas] at hChargedValidRef
            simp [EvmTransactionPreparationExtractor.Generated.chargeState,
              EvmTransactionPreparationExtractor.Reference.chargeState,
              hValid, hValidRef, hNonPos, hInsufficient, hReservoir, hChargedValid,
              hChargedValidRef, hSufficientRef, mapGas, charged]
          · have hChargedInvalidRef : ¬ EvmTransactionPreparationExtractor.Reference.gasFieldsValid (mapGas charged) = true := by
              intro h
              apply hChargedValid
              rw [hvalid charged]
              exact h
            simp only [mapGas] at hChargedInvalidRef
            simp [EvmTransactionPreparationExtractor.Generated.chargeState,
              EvmTransactionPreparationExtractor.Reference.chargeState,
              hValid, hValidRef, hNonPos, hInsufficient, hReservoir, hChargedValid,
              hChargedInvalidRef, mapGas, charged]
        · let spill := EvmTransactionPreparationExtractor.Reference.stateSpill
              { value := gas.value, stateReservoir := gas.stateReservoir, stateGasUsed := gas.stateGasUsed,
                stateGasSpill := gas.stateGasSpill, stateGasSpillRefunded := gas.stateGasSpillRefunded } amount
          let charged : Generated.GasState :=
            { value := gas.value - spill, stateReservoir := 0,
              stateGasUsed := gas.stateGasUsed + amount,
              stateGasSpill := gas.stateGasSpill + spill,
              stateGasSpillRefunded := gas.stateGasSpillRefunded }
          by_cases hChargedValid : EvmTransactionPreparationExtractor.Generated.gasFieldsValid charged = true
          · have hChargedValidRef : EvmTransactionPreparationExtractor.Reference.gasFieldsValid (mapGas charged) = true := by
              rw [← hvalid charged]
              exact hChargedValid
            simp only [mapGas] at hChargedValidRef
            simp [EvmTransactionPreparationExtractor.Generated.chargeState,
              EvmTransactionPreparationExtractor.Reference.chargeState,
              hValid, hValidRef, hNonPos, hReservoir, hChargedValid,
              hChargedValidRef, hSpillRef, hValueRefFalse, mapGas, charged, spill]
          · have hChargedInvalidRef : ¬ EvmTransactionPreparationExtractor.Reference.gasFieldsValid (mapGas charged) = true := by
              intro h
              apply hChargedValid
              rw [hvalid charged]
              exact h
            simp only [mapGas] at hChargedInvalidRef
            simp [EvmTransactionPreparationExtractor.Generated.chargeState,
              EvmTransactionPreparationExtractor.Reference.chargeState,
              hValid, hValidRef, hNonPos, hReservoir, hChargedValid,
              hChargedInvalidRef, hSpillRef, hValueRefFalse, mapGas, charged, spill]
  · have hValidRef : ¬ EvmTransactionPreparationExtractor.Reference.gasFieldsValid (mapGas gas) = true := by
      intro h
      apply hValid
      rw [hvalid gas]
      exact h
    simp only [mapGas] at hValidRef
    simp [EvmTransactionPreparationExtractor.Generated.chargeState,
      EvmTransactionPreparationExtractor.Reference.chargeState, hValid, hValidRef,
      mapGas]

theorem map_chargeExecution (gas : Generated.GasState) (amount : Nat) :
    (EvmTransactionPreparationExtractor.Generated.chargeExecution gas amount).map mapGas =
      EvmTransactionPreparationExtractor.Reference.chargeExecution (mapGas gas) amount := by
  have hvalid : ∀ g : Generated.GasState,
      EvmTransactionPreparationExtractor.Generated.gasFieldsValid g =
        EvmTransactionPreparationExtractor.Reference.gasFieldsValid (mapGas g) := by
    intro g
    exact map_gasFieldsValid g
  have validImpliesNonNeg : ∀ g : Generated.GasState,
      EvmTransactionPreparationExtractor.Generated.gasFieldsValid g = true → 0 ≤ g.value := by
    intro g h
    have h' := h
    simp only [EvmTransactionPreparationExtractor.Generated.gasFieldsValid,
      Bool.and_eq_true] at h'
    have hUInt : EvmTransactionPreparationExtractor.Generated.fitsUInt64 g.value = true := h'.1.1.1.1
    simp only [EvmTransactionPreparationExtractor.Generated.fitsUInt64,
      Bool.and_eq_true] at hUInt
    have hNonNeg : EvmTransactionPreparationExtractor.Generated.uint64Min ≤ g.value :=
      of_decide_eq_true hUInt.1
    simpa [EvmTransactionPreparationExtractor.Generated.uint64Min] using hNonNeg
  by_cases hValid : EvmTransactionPreparationExtractor.Generated.gasFieldsValid gas = true
  · have hValidRef : EvmTransactionPreparationExtractor.Reference.gasFieldsValid (mapGas gas) = true := by
      rw [← hvalid gas]
      exact hValid
    simp only [mapGas] at hValidRef
    have hNonNeg := validImpliesNonNeg gas hValid
    by_cases hZero : amount = 0
    · have hNotInsufficient : ¬ gas.value < (amount : Int) := by omega
      simp [EvmTransactionPreparationExtractor.Generated.chargeExecution,
        EvmTransactionPreparationExtractor.Reference.chargeExecution,
        hValid, hValidRef, hNonNeg, hZero, mapGas]
    · by_cases hInsufficient : gas.value < (amount : Int)
      · have hInsufficientRef : (mapGas gas).value < (amount : Int) := by simpa [mapGas] using hInsufficient
        simp [EvmTransactionPreparationExtractor.Generated.chargeExecution,
          EvmTransactionPreparationExtractor.Reference.chargeExecution,
          hValid, hValidRef, hZero, hInsufficient, mapGas]
      · let charged : Generated.GasState :=
          { value := gas.value - amount, stateReservoir := gas.stateReservoir,
            stateGasUsed := gas.stateGasUsed, stateGasSpill := gas.stateGasSpill,
            stateGasSpillRefunded := gas.stateGasSpillRefunded }
        by_cases hChargedValid : EvmTransactionPreparationExtractor.Generated.gasFieldsValid charged = true
        · have hChargedValidRef : EvmTransactionPreparationExtractor.Reference.gasFieldsValid (mapGas charged) = true := by
            rw [← hvalid charged]
            exact hChargedValid
          simp only [mapGas] at hChargedValidRef
          simp [EvmTransactionPreparationExtractor.Generated.chargeExecution,
            EvmTransactionPreparationExtractor.Reference.chargeExecution,
            hValid, hValidRef, hZero, hInsufficient, hChargedValid,
            hChargedValidRef, mapGas, charged]
        · have hChargedInvalidRef : ¬ EvmTransactionPreparationExtractor.Reference.gasFieldsValid (mapGas charged) = true := by
            intro h
            apply hChargedValid
            rw [hvalid charged]
            exact h
          simp only [mapGas] at hChargedInvalidRef
          simp [EvmTransactionPreparationExtractor.Generated.chargeExecution,
            EvmTransactionPreparationExtractor.Reference.chargeExecution,
            hValid, hValidRef, hZero, hInsufficient, hChargedValid,
            hChargedInvalidRef, mapGas, charged]
  · have hValidRef : ¬ EvmTransactionPreparationExtractor.Reference.gasFieldsValid (mapGas gas) = true := by
      intro h
      apply hValid
      rw [hvalid gas]
      exact h
    simp only [mapGas] at hValidRef
    simp [EvmTransactionPreparationExtractor.Generated.chargeExecution,
      EvmTransactionPreparationExtractor.Reference.chargeExecution, hValid, hValidRef,
      mapGas]

theorem map_foldAuthorizationStateGas
    (gas baseline : Generated.GasState) (delta : Int) :
    (EvmTransactionPreparationExtractor.Generated.foldAuthorizationStateGas gas baseline delta).map
        (fun folded => (mapGas folded.1, mapGas folded.2)) =
      (EvmTransactionPreparationExtractor.Reference.foldAuthorizationStateGas (mapGas gas) (mapGas baseline) delta).map
        (fun folded => (folded.1, folded.2)) := by
  have hvalid : ∀ g : Generated.GasState,
      EvmTransactionPreparationExtractor.Generated.gasFieldsValid g =
        EvmTransactionPreparationExtractor.Reference.gasFieldsValid (mapGas g) := by
    intro g
    exact map_gasFieldsValid g
  by_cases hDelta : delta ≤ 0
  · simp [EvmTransactionPreparationExtractor.Generated.foldAuthorizationStateGas,
      EvmTransactionPreparationExtractor.Reference.foldAuthorizationStateGas, hDelta, mapGas]
  · let foldedGas : Generated.GasState :=
      { value := gas.value, stateReservoir := gas.stateReservoir, stateGasUsed := gas.stateGasUsed,
        stateGasSpill := 0, stateGasSpillRefunded := 0 }
    let foldedBaseline : Generated.GasState :=
      { value := baseline.value, stateReservoir := baseline.stateReservoir + delta,
        stateGasUsed := baseline.stateGasUsed + delta, stateGasSpill := baseline.stateGasSpill,
        stateGasSpillRefunded := baseline.stateGasSpillRefunded }
    by_cases hFoldGas : EvmTransactionPreparationExtractor.Generated.gasFieldsValid foldedGas = true
    · by_cases hFoldBaseline : EvmTransactionPreparationExtractor.Generated.gasFieldsValid foldedBaseline = true
      · have hFoldGasRef : EvmTransactionPreparationExtractor.Reference.gasFieldsValid (mapGas foldedGas) = true := by
          rw [← hvalid foldedGas]
          exact hFoldGas
        have hFoldBaselineRef : EvmTransactionPreparationExtractor.Reference.gasFieldsValid (mapGas foldedBaseline) = true := by
          rw [← hvalid foldedBaseline]
          exact hFoldBaseline
        simp only [mapGas] at hFoldGasRef hFoldBaselineRef
        simp [EvmTransactionPreparationExtractor.Generated.foldAuthorizationStateGas,
          EvmTransactionPreparationExtractor.Reference.foldAuthorizationStateGas,
          hDelta, hFoldGas, hFoldBaseline, hFoldGasRef, hFoldBaselineRef,
          mapGas, foldedGas, foldedBaseline]
      · have hFoldBaselineRef : ¬ EvmTransactionPreparationExtractor.Reference.gasFieldsValid (mapGas foldedBaseline) = true := by
          intro h
          apply hFoldBaseline
          rw [hvalid foldedBaseline]
          exact h
        simp only [mapGas] at hFoldBaselineRef
        simp [EvmTransactionPreparationExtractor.Generated.foldAuthorizationStateGas,
          EvmTransactionPreparationExtractor.Reference.foldAuthorizationStateGas,
          hDelta, hFoldGas, hFoldBaseline, hFoldBaselineRef,
          mapGas, foldedGas, foldedBaseline]
    · have hFoldGasRef : ¬ EvmTransactionPreparationExtractor.Reference.gasFieldsValid (mapGas foldedGas) = true := by
        intro h
        apply hFoldGas
        rw [hvalid foldedGas]
        exact h
      simp only [mapGas] at hFoldGasRef
      simp [EvmTransactionPreparationExtractor.Generated.foldAuthorizationStateGas,
        EvmTransactionPreparationExtractor.Reference.foldAuthorizationStateGas,
        hDelta, hFoldGas, hFoldGasRef, mapGas, foldedGas]

theorem map_hasValueIn (value : String) (values : List String) :
    EvmTransactionPreparationExtractor.Generated.hasValueIn value values =
      EvmTransactionPreparationExtractor.Reference.hasValueIn value values := by
  induction values with
  | nil => rfl
  | cons head tail ih =>
      simp [EvmTransactionPreparationExtractor.Generated.hasValueIn,
        EvmTransactionPreparationExtractor.Reference.hasValueIn, ih]

theorem map_warmAccount
    (access : Generated.AccessObservation) (account : String) :
    mapAccess (EvmTransactionPreparationExtractor.Generated.warmAccount access account) =
      EvmTransactionPreparationExtractor.Reference.warmAccount (mapAccess access) account := by
  cases access
  simp [EvmTransactionPreparationExtractor.Generated.warmAccount,
    EvmTransactionPreparationExtractor.Reference.warmAccount, mapAccess,
    EvmTransactionPreparationExtractor.Generated.appendUnique,
    EvmTransactionPreparationExtractor.Reference.appendUnique, map_hasValueIn]
  split <;> simp_all

theorem map_warmAccounts
    (access : Generated.AccessObservation) (accounts : List String) :
    mapAccess (EvmTransactionPreparationExtractor.Generated.warmAccounts access accounts) =
      EvmTransactionPreparationExtractor.Reference.warmAccounts (mapAccess access) accounts := by
  induction accounts generalizing access with
  | nil => rfl
  | cons account rest ih =>
      simp only [EvmTransactionPreparationExtractor.Generated.warmAccounts,
        EvmTransactionPreparationExtractor.Reference.warmAccounts]
      rw [ih, map_warmAccount]

theorem map_warmStorage
    (access : Generated.AccessObservation) (cells : List String) :
    mapAccess (EvmTransactionPreparationExtractor.Generated.warmStorage access cells) =
      EvmTransactionPreparationExtractor.Reference.warmStorage (mapAccess access) cells := by
  induction cells generalizing access with
  | nil => rfl
  | cons cell rest ih =>
      simp only [EvmTransactionPreparationExtractor.Generated.warmStorage,
        EvmTransactionPreparationExtractor.Reference.warmStorage]
      rw [ih]
      congr 1
      simp [mapAccess, EvmTransactionPreparationExtractor.Generated.appendUnique,
        EvmTransactionPreparationExtractor.Reference.appendUnique, map_hasValueIn]
      split <;> simp_all

theorem map_warmTransactionCore
    (access : Generated.AccessObservation)
    (useHotAndColdStorage useTxAccessLists addCoinbaseToTxAccessList : Bool)
    (accessListAccounts accessListStorage : List String) (coinbase : Option String)
    (sender : String) (recipient : Option String) (includeRecipient : Bool) :
    mapAccess
        (if !useHotAndColdStorage then access else
          let access0 :=
            if useTxAccessLists then
              EvmTransactionPreparationExtractor.Generated.warmStorage
                (EvmTransactionPreparationExtractor.Generated.warmAccounts access accessListAccounts)
                accessListStorage
            else access
          let access1 :=
            if addCoinbaseToTxAccessList then
              match coinbase with
              | some value => EvmTransactionPreparationExtractor.Generated.warmAccount access0 value
              | none => access0
            else access0
          let access2 :=
            if includeRecipient then
              match recipient with
              | some value => EvmTransactionPreparationExtractor.Generated.warmAccount access1 value
              | none => access1
            else access1
          EvmTransactionPreparationExtractor.Generated.warmAccount access2 sender) =
      (if !useHotAndColdStorage then mapAccess access else
        let access0 :=
          if useTxAccessLists then
            EvmTransactionPreparationExtractor.Reference.warmStorage
              (EvmTransactionPreparationExtractor.Reference.warmAccounts (mapAccess access) accessListAccounts)
              accessListStorage
          else mapAccess access
        let access1 :=
          if addCoinbaseToTxAccessList then
            match coinbase with
            | some value => EvmTransactionPreparationExtractor.Reference.warmAccount access0 value
            | none => access0
          else access0
        let access2 :=
          if includeRecipient then
            match recipient with
            | some value => EvmTransactionPreparationExtractor.Reference.warmAccount access1 value
            | none => access1
          else access1
        EvmTransactionPreparationExtractor.Reference.warmAccount access2 sender) := by
  cases useHotAndColdStorage <;> cases useTxAccessLists <;>
    cases addCoinbaseToTxAccessList <;> cases coinbase <;>
    cases recipient <;> cases includeRecipient <;>
    simp [map_warmAccount, map_warmAccounts, map_warmStorage]

theorem map_warmTransactionAccesses
    (input : Generated.Input) (access : Generated.AccessObservation)
    (recipient : Option String) (includeRecipient : Bool) :
    mapAccess (EvmTransactionPreparationExtractor.Generated.warmTransactionAccesses input access recipient includeRecipient) =
      EvmTransactionPreparationExtractor.Reference.warmTransaction (mapInput input) (mapAccess access)
        recipient includeRecipient := by
  unfold EvmTransactionPreparationExtractor.Generated.warmTransactionAccesses
    EvmTransactionPreparationExtractor.Reference.warmTransaction
  cases hHot : input.handoff.spec.useHotAndColdStorage <;>
    cases hTx : input.handoff.spec.useTxAccessLists <;>
    cases hAdd : input.handoff.spec.addCoinbaseToTxAccessList <;>
    cases hCoinbase : input.handoff.tx.coinbase <;>
    cases recipient <;> cases includeRecipient <;>
    simp [hHot, hTx, hAdd, hCoinbase, mapInput, mapHandoff, mapTx,
      map_warmAccount, map_warmAccounts, map_warmStorage]

theorem map_findAuthorityStateIn :
    ∀ (states : List Generated.AuthorityStateFacts) (authority : String),
      EvmTransactionPreparationExtractor.Reference.findAuthorityStateIn (mapAuthorityStates states) authority =
        (EvmTransactionPreparationExtractor.Generated.findAuthorityStateIn states authority).map mapAuthorityState
  | [], _ => rfl
  | state :: rest, authority => by
      by_cases hEq : state.authority = authority
      · simp [EvmTransactionPreparationExtractor.Generated.findAuthorityStateIn,
          EvmTransactionPreparationExtractor.Reference.findAuthorityStateIn,
          mapAuthorityStates, mapAuthorityState, hEq]
      · simp [EvmTransactionPreparationExtractor.Generated.findAuthorityStateIn,
          EvmTransactionPreparationExtractor.Reference.findAuthorityStateIn,
          mapAuthorityStates, mapAuthorityState, hEq, map_findAuthorityStateIn rest authority]

theorem map_findAuthorityState (world : Generated.WorldFacts) (authority : String) :
    EvmTransactionPreparationExtractor.Reference.findAuthorityState (mapWorld world) authority =
      (EvmTransactionPreparationExtractor.Generated.findAuthorityState world authority).map mapAuthorityState := by
  simp only [EvmTransactionPreparationExtractor.Generated.findAuthorityState,
    EvmTransactionPreparationExtractor.Reference.findAuthorityState]
  exact map_findAuthorityStateIn world.authorityStates authority

theorem map_findAuthorityState_forward (world : Generated.WorldFacts) (authority : String) :
    (EvmTransactionPreparationExtractor.Generated.findAuthorityState world authority).map mapAuthorityState =
      EvmTransactionPreparationExtractor.Reference.findAuthorityState (mapWorld world) authority := by
  symm
  exact map_findAuthorityState world authority

theorem map_recipientAccountEmpty (input : Generated.Input) (world : Generated.WorldFacts) :
    EvmTransactionPreparationExtractor.Generated.recipientAccountEmpty input world =
      EvmTransactionPreparationExtractor.Reference.recipientAccountEmpty (mapInput input) (mapWorld world) := by
  cases hRecipient : input.handoff.tx.recipient with
  | none =>
      simp [EvmTransactionPreparationExtractor.Generated.recipientAccountEmpty,
        EvmTransactionPreparationExtractor.Reference.recipientAccountEmpty, hRecipient,
        mapInput, mapHandoff, mapTx, mapWorld, mapRelations]
  | some recipient =>
      cases hFind : EvmTransactionPreparationExtractor.Generated.findAuthorityState world recipient with
      | none =>
          have hFindRef := map_findAuthorityState world recipient
          simp only [EvmTransactionPreparationExtractor.Generated.recipientAccountEmpty,
            EvmTransactionPreparationExtractor.Reference.recipientAccountEmpty,
            mapInput, mapHandoff, mapTx, hRecipient]
          rw [hFindRef]
          simp [hFind, mapRelations]
      | some state =>
          have hFindRef := map_findAuthorityState world recipient
          simp only [EvmTransactionPreparationExtractor.Generated.recipientAccountEmpty,
            EvmTransactionPreparationExtractor.Reference.recipientAccountEmpty,
            mapInput, mapHandoff, mapTx, hRecipient]
          rw [hFindRef]
          simp [hFind, mapAuthorityState]

theorem map_isCreateTx (input : Generated.Input) :
    EvmTransactionPreparationExtractor.Generated.isCreateTx input =
    EvmTransactionPreparationExtractor.Reference.isCreateTx (mapInput input) := by
  simp [EvmTransactionPreparationExtractor.Generated.isCreateTx,
    EvmTransactionPreparationExtractor.Reference.isCreateTx, mapInput, mapHandoff, mapTx]

theorem map_hasValue (input : Generated.Input) :
    EvmTransactionPreparationExtractor.Generated.hasValue input.handoff.tx =
    EvmTransactionPreparationExtractor.Reference.hasValue (mapInput input).handoff.tx := by
  simp [EvmTransactionPreparationExtractor.Generated.hasValue,
    EvmTransactionPreparationExtractor.Reference.hasValue, mapInput, mapHandoff, mapTx]

theorem map_deadRecipient (input : Generated.Input) (world : Generated.WorldFacts) :
    EvmTransactionPreparationExtractor.Generated.deadRecipient input world =
      EvmTransactionPreparationExtractor.Reference.deadRecipient (mapInput input) (mapWorld world) := by
  have hDead : EvmTransactionPreparationExtractor.Generated.op_chargeDeadRecipient = true := by decide
  have hAdmitted : EvmTransactionPreparationExtractor.Generated.admittedDeadOperation = true := hDead
  rw [EvmTransactionPreparationExtractor.Generated.deadRecipient,
    EvmTransactionPreparationExtractor.Reference.deadRecipient]
  rw [map_recipientAccountEmpty]
  rw [map_isCreateTx, map_hasValue]
  simp only [hAdmitted]
  rfl

theorem map_valueDebitAmount (input : Generated.Input) (world : Generated.WorldFacts) :
    EvmTransactionPreparationExtractor.Generated.valueDebitAmount input world =
      EvmTransactionPreparationExtractor.Reference.valueDebitAmount (mapInput input) (mapWorld world) := by
  simp only [EvmTransactionPreparationExtractor.Generated.valueDebitAmount,
    EvmTransactionPreparationExtractor.Reference.valueDebitAmount]
  rw [map_hasValue]
  rfl

theorem map_updateAuthorityBalanceList :
    ∀ (states : List Generated.AuthorityStateFacts) (authority : String) (balance : Nat),
      mapAuthorityStates
          (EvmTransactionPreparationExtractor.Generated.updateAuthorityBalance.update authority balance states) =
        EvmTransactionPreparationExtractor.Reference.updateAuthorityBalance.update authority balance
          (mapAuthorityStates states)
  | [], _, _ => rfl
  | state :: rest, authority, balance => by
      by_cases hEq : state.authority = authority
      · simp [EvmTransactionPreparationExtractor.Generated.updateAuthorityBalance.update,
          EvmTransactionPreparationExtractor.Reference.updateAuthorityBalance.update,
          mapAuthorityStates, mapAuthorityState, hEq]
      · simp [EvmTransactionPreparationExtractor.Generated.updateAuthorityBalance.update,
          EvmTransactionPreparationExtractor.Reference.updateAuthorityBalance.update,
          mapAuthorityStates, mapAuthorityState, hEq,
          map_updateAuthorityBalanceList rest authority balance]

theorem map_updateAuthorityBalance (world : Generated.WorldFacts) (authority : String) (balance : Nat) :
    mapWorld (EvmTransactionPreparationExtractor.Generated.updateAuthorityBalance world authority balance) =
      EvmTransactionPreparationExtractor.Reference.updateAuthorityBalance (mapWorld world) authority balance := by
  cases world
  simp only [EvmTransactionPreparationExtractor.Generated.updateAuthorityBalance,
    EvmTransactionPreparationExtractor.Reference.updateAuthorityBalance, mapWorld]
  rw [map_updateAuthorityBalanceList]

theorem map_recordAccountWrite (world : Generated.WorldFacts) (account : String) :
    mapWorld (EvmTransactionPreparationExtractor.Generated.recordAccountWrite world account) =
      EvmTransactionPreparationExtractor.Reference.recordAccountWrite (mapWorld world) account := by
  cases world
  rfl

theorem map_paidWorld (world : Generated.WorldFacts) (account : String)
    (balance : Nat) (recipientBalance : Nat) :
    mapWorld
        { EvmTransactionPreparationExtractor.Generated.recordAccountWrite world account with
          currentBalance := balance, recipientBalance := recipientBalance } =
      { EvmTransactionPreparationExtractor.Reference.recordAccountWrite (mapWorld world) account with
        currentBalance := balance, recipientBalance := recipientBalance } := by
  cases world
  rfl

theorem map_payValue (input : Generated.Input) (world : Generated.WorldFacts) :
    mapWorld (EvmTransactionPreparationExtractor.Generated.payValue input world) =
      EvmTransactionPreparationExtractor.Reference.payValue (mapInput input) (mapWorld world) := by
  have hPay : EvmTransactionPreparationExtractor.Generated.admittedPayValueOperation = true := by
    rcases admittedGateClosure with ⟨_, _, _, _, _, _, _, _, _, _, _, _, _, _, _, hClosed, _⟩
    exact hClosed
  have hAmountRef :
      EvmTransactionPreparationExtractor.Reference.valueDebitAmount (mapInput input) (mapWorld world) =
        EvmTransactionPreparationExtractor.Generated.valueDebitAmount input world := by
    symm
    exact map_valueDebitAmount input world
  by_cases hAmount : EvmTransactionPreparationExtractor.Generated.valueDebitAmount input world = 0
  · have hAmountRefZero :
        EvmTransactionPreparationExtractor.Reference.valueDebitAmount (mapInput input) (mapWorld world) = 0 := by
      rw [hAmountRef]
      exact hAmount
    simp only [EvmTransactionPreparationExtractor.Generated.payValue,
      EvmTransactionPreparationExtractor.Reference.payValue, hPay, hAmount, hAmountRefZero,
      Bool.not_true, ↓reduceIte]
    simp
  · have hAmountRefNonzero :
        EvmTransactionPreparationExtractor.Reference.valueDebitAmount (mapInput input) (mapWorld world) ≠ 0 := by
      rw [hAmountRef]
      exact hAmount
    simp only [EvmTransactionPreparationExtractor.Generated.payValue,
      EvmTransactionPreparationExtractor.Reference.payValue, hPay, hAmount, hAmountRefNonzero,
      Bool.not_true, ↓reduceIte]
    simp
    rw [map_updateAuthorityBalance]
    rw [map_paidWorld]
    rw [hAmountRef]
    simp [EvmTransactionPreparationExtractor.Generated.recordAccountWrite,
      EvmTransactionPreparationExtractor.Reference.recordAccountWrite]
    cases input
    cases world
    rfl

theorem map_deploymentCollision (input : Generated.Input) :
    EvmTransactionPreparationExtractor.Generated.deploymentCollision input =
      EvmTransactionPreparationExtractor.Reference.deploymentCollision (mapInput input) := by
  have hResult : EvmTransactionPreparationExtractor.Generated.admittedCreateCollisionOperation = true := by
    rcases admittedGateClosure with ⟨_, _, _, _, _, _, _, _, _, _, _, _, hClosed, _⟩
    exact hClosed
  have hClass : EvmTransactionPreparationExtractor.Generated.admittedCreateCollisionClassificationOperation = true := by
    rcases admittedGateClosure with ⟨_, _, _, _, _, _, _, _, _, _, _, _, _, hClosed, _⟩
    exact hClosed
  simp only [EvmTransactionPreparationExtractor.Generated.deploymentCollision,
    EvmTransactionPreparationExtractor.Reference.deploymentCollision, hResult, hClass,
    Bool.true_and]
  rw [map_isCreateTx]
  cases input
  simp [
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    
    mapInput, mapHandoff, mapTx, mapRelations, mapWorld, mapAccess,
    mapEnvironment, mapCode, mapGas, mapSnapshot, mapSnapshotPart]

theorem map_authorizationCoherent (world : Generated.WorldFacts) (auth : Generated.AuthorizationFacts) :
    EvmTransactionPreparationExtractor.Generated.authorizationCoherent world auth =
      EvmTransactionPreparationExtractor.Reference.authorizationCoherent (mapWorld world) (mapAuthorization auth) := by
  cases hFind : EvmTransactionPreparationExtractor.Generated.findAuthorityState world auth.authority with
  | none =>
      have hFindRef := map_findAuthorityState world auth.authority
      simp only [EvmTransactionPreparationExtractor.Generated.authorizationCoherent,
        EvmTransactionPreparationExtractor.Reference.authorizationCoherent, hFind]
      simp only [mapAuthorization]
      rw [hFind] at hFindRef
      rw [hFindRef]
      simp
  | some state =>
      have hFindRef := map_findAuthorityState world auth.authority
      simp only [EvmTransactionPreparationExtractor.Generated.authorizationCoherent,
        EvmTransactionPreparationExtractor.Reference.authorizationCoherent, hFind]
      simp only [mapAuthorization]
      rw [hFind] at hFindRef
      rw [hFindRef]
      simp
      cases state
      cases auth
      rfl

theorem map_authorityStateFactsCoherent (state : Generated.AuthorityStateFacts) :
    EvmTransactionPreparationExtractor.Generated.authorityStateFactsCoherent state =
      EvmTransactionPreparationExtractor.Reference.authorityStateFactsCoherent (mapAuthorityState state) := by
  cases state
  rfl

theorem map_authorityStatesCoherent (states : List Generated.AuthorityStateFacts) :
    EvmTransactionPreparationExtractor.Generated.authorityStatesCoherent states =
      EvmTransactionPreparationExtractor.Reference.authorityStatesCoherent (mapAuthorityStates states) := by
  induction states with
  | nil => rfl
  | cons state rest ih =>
      simp only [EvmTransactionPreparationExtractor.Generated.authorityStatesCoherent,
        EvmTransactionPreparationExtractor.Reference.authorityStatesCoherent,
        mapAuthorityStates, map_authorityStateFactsCoherent, ih]

theorem map_authorizationsCoherent (world : Generated.WorldFacts)
    (auths : List Generated.AuthorizationFacts) :
    EvmTransactionPreparationExtractor.Generated.authorizationsCoherent world auths =
      EvmTransactionPreparationExtractor.Reference.authorizationsCoherent (mapWorld world)
        (mapAuthorizations auths) := by
  induction auths with
  | nil => rfl
  | cons auth rest ih =>
      simp only [EvmTransactionPreparationExtractor.Generated.authorizationsCoherent,
        map_authorizationCoherent, mapAuthorization, ih]
      rw [mapAuthorizations]
      simp only [mapAuthorization]
      rfl

theorem map_accountFactsEqual (left right : Generated.AccountFacts) :
    EvmTransactionPreparationExtractor.Generated.accountFactsEqual left right =
      EvmTransactionPreparationExtractor.Reference.accountFactsEqual
        { physicalExists := left.physicalExists, logicalExists := left.logicalExists,
          nonce := left.nonce, code := left.code, delegation := left.delegation,
          balance := left.balance, storageNonEmpty := left.storageNonEmpty }
        { physicalExists := right.physicalExists, logicalExists := right.logicalExists,
          nonce := right.nonce, code := right.code, delegation := right.delegation,
          balance := right.balance, storageNonEmpty := right.storageNonEmpty } := by
  cases left
  cases right
  rfl

theorem map_authorityStateFactsEqual (left right : Generated.AuthorityStateFacts) :
    EvmTransactionPreparationExtractor.Generated.authorityStateFactsEqual left right =
      EvmTransactionPreparationExtractor.Reference.authorityStateFactsEqual
        (mapAuthorityState left) (mapAuthorityState right) := by
  cases left
  cases right
  rfl

theorem map_authorityStateListEqual :
    ∀ (left right : List Generated.AuthorityStateFacts),
      EvmTransactionPreparationExtractor.Generated.authorityStateListEqual left right =
        EvmTransactionPreparationExtractor.Reference.authorityStateListEqual
          (mapAuthorityStates left) (mapAuthorityStates right)
  | [], [] => rfl
  | [], _ :: _ => rfl
  | _ :: _, [] => rfl
  | left :: leftRest, right :: rightRest => by
      simp only [EvmTransactionPreparationExtractor.Generated.authorityStateListEqual,
        EvmTransactionPreparationExtractor.Reference.authorityStateListEqual,
        mapAuthorityStates, map_authorityStateFactsEqual,
        map_authorityStateListEqual leftRest rightRest]

theorem map_worldFactsEqual (left right : Generated.WorldFacts) :
    EvmTransactionPreparationExtractor.Generated.worldFactsEqual left right =
      EvmTransactionPreparationExtractor.Reference.worldFactsEqual (mapWorld left) (mapWorld right) := by
  cases left
  cases right
  simp only [EvmTransactionPreparationExtractor.Generated.worldFactsEqual,
    EvmTransactionPreparationExtractor.Reference.worldFactsEqual, mapWorld]
  rw [map_authorityStateListEqual]

theorem map_accessFactsEqual (left right : Generated.AccessObservation) :
    EvmTransactionPreparationExtractor.Generated.accessFactsEqual left right =
      EvmTransactionPreparationExtractor.Reference.accessFactsEqual (mapAccess left) (mapAccess right) := by
  cases left
  cases right
  rfl

theorem map_freshAccess (access : Generated.AccessObservation) :
    EvmTransactionPreparationExtractor.Generated.freshAccess access =
      EvmTransactionPreparationExtractor.Reference.freshAccess (mapAccess access) := by
  cases access
  rfl

theorem map_gasFactsEqual (left right : Generated.GasState) :
    EvmTransactionPreparationExtractor.Generated.gasFactsEqual left right =
      EvmTransactionPreparationExtractor.Reference.gasFactsEqual (mapGas left) (mapGas right) := by
  cases left
  cases right
  rfl

theorem map_codeIdentityEqual (left right : Generated.CodeIdentity) :
    EvmTransactionPreparationExtractor.Generated.codeIdentityEqual left right =
      EvmTransactionPreparationExtractor.Reference.codeIdentityEqual (mapCode left) (mapCode right) := by
  cases left
  cases right
  rfl

theorem map_environmentFactsEqual (left right : Generated.Environment) :
    EvmTransactionPreparationExtractor.Generated.environmentFactsEqual left right =
      EvmTransactionPreparationExtractor.Reference.environmentFactsEqual
        (mapEnvironment left) (mapEnvironment right) := by
  cases left
  cases right
  simp only [EvmTransactionPreparationExtractor.Generated.environmentFactsEqual,
    EvmTransactionPreparationExtractor.Reference.environmentFactsEqual,
    mapEnvironment, map_codeIdentityEqual]

theorem map_snapshotFactsEqual (left right : Generated.Snapshot) :
    EvmTransactionPreparationExtractor.Generated.snapshotFactsEqual left right =
      EvmTransactionPreparationExtractor.Reference.snapshotFactsEqual (mapSnapshot left) (mapSnapshot right) := by
  cases left
  cases right
  rfl

theorem map_snapshotIsEmpty (snapshot : Generated.Snapshot) :
    EvmTransactionPreparationExtractor.Generated.snapshotIsEmpty snapshot =
      EvmTransactionPreparationExtractor.Reference.snapshotIsEmpty (mapSnapshot snapshot) := by
  cases snapshot
  rfl

set_option maxRecDepth 100000 in
theorem map_sourceAccessLineageAdmitted :
    EvmTransactionPreparationExtractor.Generated.sourceAccessLineageAdmitted =
      EvmTransactionPreparationExtractor.Reference.sourceAccessLineageAdmitted := by
  decide +kernel

theorem map_hasAuthorization (tx : Generated.TxFacts) (auths : List Generated.AuthorizationFacts) :
    EvmTransactionPreparationExtractor.Generated.hasAuthorization tx auths =
      EvmTransactionPreparationExtractor.Reference.hasAuthorization (mapTx tx) (mapAuthorizations auths) := by
  cases tx
  cases auths <;> rfl

theorem map_messageInputData (input : Generated.Input) :
    EvmTransactionPreparationExtractor.Generated.messageInputData input =
      EvmTransactionPreparationExtractor.Reference.messageInputData (mapInput input) := by
  cases input
  rfl

theorem map_snapshotPresenceCoherent (input : Generated.Input) :
    EvmTransactionPreparationExtractor.Generated.snapshotPresenceCoherent input =
      EvmTransactionPreparationExtractor.Reference.snapshotPresenceCoherent (mapInput input) := by
  simp only [EvmTransactionPreparationExtractor.Generated.snapshotPresenceCoherent,
    EvmTransactionPreparationExtractor.Reference.snapshotPresenceCoherent]
  rw [map_hasAuthorization]
  rfl

theorem map_sourceEntryFactsCoherent (input : Generated.Input) :
    EvmTransactionPreparationExtractor.Generated.sourceEntryFactsCoherent input =
      EvmTransactionPreparationExtractor.Reference.sourceEntryFactsCoherent (mapInput input) := by
  simp only [EvmTransactionPreparationExtractor.Generated.sourceEntryFactsCoherent,
    EvmTransactionPreparationExtractor.Reference.sourceEntryFactsCoherent]
  rw [map_accessFactsEqual, map_freshAccess, map_worldFactsEqual,
    map_gasFactsEqual, map_gasFactsEqual, map_environmentFactsEqual,
    map_snapshotIsEmpty, map_snapshotPresenceCoherent, map_sourceAccessLineageAdmitted]
  rfl

theorem mapEvents_append (events : List Generated.Event) (event : Generated.Event) :
    mapEvents (events ++ [event]) = mapEvents events ++ [mapEvent event] := by
  induction events with
  | nil => rfl
  | cons head tail ih =>
      simp only [mapEvents, List.cons_append, ih]

theorem mapEvents_append_list (events rest : List Generated.Event) :
    mapEvents (events ++ rest) = mapEvents events ++ mapEvents rest := by
  induction events with
  | nil => rfl
  | cons head tail ih =>
      simp only [mapEvents, List.cons_append, ih]

theorem mapEvents_append_two (events : List Generated.Event)
    (first second : Generated.Event) :
    mapEvents (events ++ [first, second]) =
      mapEvents events ++ [mapEvent first, mapEvent second] := by
  rw [show events ++ [first, second] = (events ++ [first]) ++ [second] by
    simp [List.append_assoc]]
  rw [mapEvents_append, mapEvents_append]
  simp [List.append_assoc]

theorem map_authorityStateMatchesTransaction
    (input : Generated.Input) (state : Generated.AuthorityStateFacts) :
    EvmTransactionPreparationExtractor.Generated.authorityStateMatchesTransaction input state =
      EvmTransactionPreparationExtractor.Reference.authorityStateMatchesTransaction
        (mapInput input) (mapAuthorityState state) := by
  cases input
  cases state
  rfl

theorem map_authorityStatesMatchTransaction
    (input : Generated.Input) :
    ∀ (states : List Generated.AuthorityStateFacts),
      EvmTransactionPreparationExtractor.Generated.authorityStatesMatchTransaction input states =
        EvmTransactionPreparationExtractor.Reference.authorityStatesMatchTransaction
          (mapInput input) (mapAuthorityStates states)
  | [] => rfl
  | state :: rest => by
      simp only [EvmTransactionPreparationExtractor.Generated.authorityStatesMatchTransaction,
        EvmTransactionPreparationExtractor.Reference.authorityStatesMatchTransaction,
        mapAuthorityStates, map_authorityStateMatchesTransaction,
        map_authorityStatesMatchTransaction input rest]

theorem map_authorityNames :
    ∀ (states : List Generated.AuthorityStateFacts),
      (mapAuthorityStates states).map EvmTransactionPreparationExtractor.Reference.AuthorityStateFacts.authority =
        states.map EvmTransactionPreparationExtractor.Generated.AuthorityStateFacts.authority
  | [] => rfl
  | state :: rest => by
      simp only [mapAuthorityStates, mapAuthorityState, List.map_cons, map_authorityNames rest]

theorem map_hasUniqueAuthorityStates (states : List Generated.AuthorityStateFacts) :
    EvmTransactionPreparationExtractor.Generated.hasUniqueAuthorityStates states =
      EvmTransactionPreparationExtractor.Reference.hasUniqueAuthorityStates (mapAuthorityStates states) := by
  induction states with
  | nil => rfl
  | cons state rest ih =>
      simp only [EvmTransactionPreparationExtractor.Generated.hasUniqueAuthorityStates,
        EvmTransactionPreparationExtractor.Reference.hasUniqueAuthorityStates,
        mapAuthorityStates, mapAuthorityState, map_authorityNames, map_hasValueIn, ih]

def mapDelegatedTarget
    (facts : EvmTransactionPreparationExtractor.Generated.DelegatedTargetFacts) :
    EvmTransactionPreparationExtractor.Reference.DelegatedTargetFacts :=
  { target := facts.target, isPrecompile := facts.isPrecompile, account := { physicalExists := facts.account.physicalExists, logicalExists := facts.account.logicalExists, nonce := facts.account.nonce, code := facts.account.code, delegation := facts.account.delegation, balance := facts.account.balance, storageNonEmpty := facts.account.storageNonEmpty }, accessCharge := facts.accessCharge }

theorem map_delegatedTargetFactsCoherent
    (facts : EvmTransactionPreparationExtractor.Generated.DelegatedTargetFacts) :
    EvmTransactionPreparationExtractor.Generated.delegatedTargetFactsCoherent facts =
      EvmTransactionPreparationExtractor.Reference.delegatedTargetFactsCoherent
        (mapDelegatedTarget facts) := by
  cases facts
  rfl

theorem map_delegatedTargetFactsCoherent_ofRelations
    (relations : EvmTransactionPreparationExtractor.Generated.PreparationRelations) :
    EvmTransactionPreparationExtractor.Generated.delegatedTargetFactsCoherent relations.delegatedTarget =
      EvmTransactionPreparationExtractor.Reference.delegatedTargetFactsCoherent
        (mapRelations relations).delegatedTarget := by
  cases relations
  rfl

set_option maxRecDepth 100000 in
theorem map_inputFactsCoherent (input : Generated.Input) :
      EvmTransactionPreparationExtractor.Generated.inputFactsCoherent input =
      EvmTransactionPreparationExtractor.Reference.inputFactsCoherent (mapInput input) := by
  simp only [EvmTransactionPreparationExtractor.Generated.inputFactsCoherent,
    EvmTransactionPreparationExtractor.Reference.inputFactsCoherent,
    map_sourceEntryFactsCoherent, map_gasFactsEqual, map_hasAuthorization,
    map_environmentFactsEqual, map_messageInputData, map_hasValue,
    map_delegatedTargetFactsCoherent_ofRelations, map_hasUniqueAuthorityStates,
    map_authorityStatesCoherent, map_authorizationsCoherent,
    map_authorityStatesMatchTransaction, mapAuthorizations_isEmpty,
    mapInput_handoff, mapInput_prePreparationGas,
    mapInput_preExecutionIntrinsicGasStandard, mapInput_hasPreExecutionSnapshot,
    
    mapInput_world, 
    mapInput_authorizations, mapInput_relations, mapInput_environment,
    mapInput_delegationRefunds, mapHandoff, mapTx, mapRelations, mapWorld,
    mapAccess, mapEnvironment, mapCode, mapGas]

theorem map_authPreliminaryValid (auth : Generated.AuthorizationFacts) :
    EvmTransactionPreparationExtractor.Generated.authPreliminaryValid auth =
      EvmTransactionPreparationExtractor.Reference.authPreliminaryValid (mapAuthorization auth) := by
  rfl

theorem map_recordAccountRead (world : Generated.WorldFacts) (account : String) :
    mapWorld (EvmTransactionPreparationExtractor.Generated.recordAccountRead world account) =
      EvmTransactionPreparationExtractor.Reference.recordAccountRead (mapWorld world) account := by
  cases world
  rfl

theorem map_recordAccessRead (access : Generated.AccessObservation) (account : String) :
    mapAccess (EvmTransactionPreparationExtractor.Generated.recordAccessRead access account) =
      EvmTransactionPreparationExtractor.Reference.recordAccessRead (mapAccess access) account := by
  cases access
  rfl

theorem map_observedAuthorizationFields
    (state : Generated.AuthorizationState) (auth : Generated.AuthorizationFacts) :
    mapWorld (EvmTransactionPreparationExtractor.Generated.recordAccountRead state.world auth.authority) =
        EvmTransactionPreparationExtractor.Reference.recordAccountRead (mapWorld state.world)
          (mapAuthorization auth).authority ∧
      mapAccess
          (EvmTransactionPreparationExtractor.Generated.recordAccessRead
            (EvmTransactionPreparationExtractor.Generated.warmAccount state.access auth.authority)
            auth.authority) =
        EvmTransactionPreparationExtractor.Reference.recordAccessRead
          (EvmTransactionPreparationExtractor.Reference.warmAccount (mapAccess state.access)
            (mapAuthorization auth).authority) (mapAuthorization auth).authority ∧
      mapEvents
          (state.events ++
            [EvmTransactionPreparationExtractor.Generated.Event.authorizationWarm auth.authority,
              EvmTransactionPreparationExtractor.Generated.Event.authorizationRead auth.authority]) =
        mapEvents state.events ++
          [EvmTransactionPreparationExtractor.Reference.Event.authorizationWarm (mapAuthorization auth).authority,
            EvmTransactionPreparationExtractor.Reference.Event.authorizationRead (mapAuthorization auth).authority] := by
  constructor
  · rw [map_recordAccountRead]
    rfl
  constructor
  · rw [map_recordAccessRead, map_warmAccount]
    rfl
  · rw [mapEvents_append_two]
    rfl

theorem map_observedAuthorization
    (state : Generated.AuthorizationState) (auth : Generated.AuthorizationFacts)
    (preliminary resultFailed : Bool) :
    mapAuthorizationState
        { state with
          access := if preliminary then
            EvmTransactionPreparationExtractor.Generated.recordAccessRead
              (EvmTransactionPreparationExtractor.Generated.warmAccount state.access auth.authority)
              auth.authority
            else state.access,
          world := if preliminary then
            EvmTransactionPreparationExtractor.Generated.recordAccountRead state.world auth.authority
            else state.world,
          events := if preliminary then
            state.events ++ [EvmTransactionPreparationExtractor.Generated.Event.authorizationWarm auth.authority,
              EvmTransactionPreparationExtractor.Generated.Event.authorizationRead auth.authority]
            else state.events,
          failed := resultFailed } =
      { mapAuthorizationState state with
        access := if preliminary then
          EvmTransactionPreparationExtractor.Reference.recordAccessRead
            (EvmTransactionPreparationExtractor.Reference.warmAccount (mapAccess state.access) auth.authority)
            auth.authority
          else mapAccess state.access,
        world := if preliminary then
          EvmTransactionPreparationExtractor.Reference.recordAccountRead (mapWorld state.world) auth.authority
          else mapWorld state.world,
        events := if preliminary then
          mapEvents state.events ++ [EvmTransactionPreparationExtractor.Reference.Event.authorizationWarm auth.authority,
            EvmTransactionPreparationExtractor.Reference.Event.authorizationRead auth.authority]
          else mapEvents state.events,
        failed := resultFailed } := by
  cases preliminary
  · rfl
  · simp [mapAuthorizationState, map_recordAccessRead, map_recordAccountRead,
      map_warmAccount]
    rw [mapEvents_append_two]
    rfl

theorem map_observedAuthorization_true
    (state : Generated.AuthorizationState) (auth : Generated.AuthorizationFacts)
    (resultFailed : Bool) :
    mapAuthorizationState
        { state with
          access := EvmTransactionPreparationExtractor.Generated.recordAccessRead
            (EvmTransactionPreparationExtractor.Generated.warmAccount state.access auth.authority)
            auth.authority,
          world := EvmTransactionPreparationExtractor.Generated.recordAccountRead
            state.world auth.authority,
          events := state.events ++
            [EvmTransactionPreparationExtractor.Generated.Event.authorizationWarm auth.authority,
              EvmTransactionPreparationExtractor.Generated.Event.authorizationRead auth.authority],
          failed := resultFailed } =
      { mapAuthorizationState state with
        access := EvmTransactionPreparationExtractor.Reference.recordAccessRead
          (EvmTransactionPreparationExtractor.Reference.warmAccount (mapAccess state.access) auth.authority)
          auth.authority,
        world := EvmTransactionPreparationExtractor.Reference.recordAccountRead
          (mapWorld state.world) auth.authority,
        events := mapEvents state.events ++
          [EvmTransactionPreparationExtractor.Reference.Event.authorizationWarm auth.authority,
            EvmTransactionPreparationExtractor.Reference.Event.authorizationRead auth.authority],
        failed := resultFailed } := by
  simpa [mapAuthorizationState, map_recordAccessRead, map_recordAccountRead,
    map_warmAccount] using map_observedAuthorization state auth true resultFailed

theorem map_observedAuthorization_true_explicit
    (state : Generated.AuthorizationState) (auth : Generated.AuthorizationFacts)
    (resultFailed : Bool) :
    mapAuthorizationState
        { state with
          access := EvmTransactionPreparationExtractor.Generated.recordAccessRead
            (EvmTransactionPreparationExtractor.Generated.warmAccount state.access auth.authority)
            auth.authority,
          world := EvmTransactionPreparationExtractor.Generated.recordAccountRead
            state.world auth.authority,
          events := state.events ++
            [EvmTransactionPreparationExtractor.Generated.Event.authorizationWarm auth.authority,
              EvmTransactionPreparationExtractor.Generated.Event.authorizationRead auth.authority],
          failed := resultFailed } =
      { gas := mapGas state.gas, baseline := mapGas state.baseline,
        world := EvmTransactionPreparationExtractor.Reference.recordAccountRead
          (mapWorld state.world) auth.authority,
        access := EvmTransactionPreparationExtractor.Reference.recordAccessRead
          (EvmTransactionPreparationExtractor.Reference.warmAccount (mapAccess state.access) auth.authority)
          auth.authority,
        refunds := state.refunds, writtenAuthorities := state.writtenAuthorities,
        delegationSetFor := state.delegationSetFor,
        events := mapEvents state.events ++
          [EvmTransactionPreparationExtractor.Reference.Event.authorizationWarm auth.authority,
            EvmTransactionPreparationExtractor.Reference.Event.authorizationRead auth.authority],
        failed := resultFailed } := by
  rw [map_observedAuthorization_true]
  cases state
  cases auth
  simp [mapAuthorizationState, mapWorld, mapAccess]

theorem map_replaceAuthorityStateList :
    ∀ (states : List Generated.AuthorityStateFacts) (replacement : Generated.AuthorityStateFacts),
      mapAuthorityStates
          (EvmTransactionPreparationExtractor.Generated.replaceAuthorityState.replace replacement states) =
        EvmTransactionPreparationExtractor.Reference.replaceAuthorityState.replace
          (mapAuthorityState replacement) (mapAuthorityStates states)
  | [], _ => rfl
  | state :: rest, replacement => by
      by_cases hEq : state.authority = replacement.authority
      · simp [EvmTransactionPreparationExtractor.Generated.replaceAuthorityState.replace,
          EvmTransactionPreparationExtractor.Reference.replaceAuthorityState.replace,
          mapAuthorityStates, mapAuthorityState, hEq]
      · simp [EvmTransactionPreparationExtractor.Generated.replaceAuthorityState.replace,
          EvmTransactionPreparationExtractor.Reference.replaceAuthorityState.replace,
          mapAuthorityStates, mapAuthorityState, hEq,
          map_replaceAuthorityStateList rest replacement]

theorem map_replaceAuthorityState
    (world : Generated.WorldFacts) (replacement : Generated.AuthorityStateFacts) :
    mapWorld
        (EvmTransactionPreparationExtractor.Generated.replaceAuthorityState world replacement) =
      EvmTransactionPreparationExtractor.Reference.replaceAuthorityState
        (mapWorld world) (mapAuthorityState replacement) := by
  cases world
  simp only [EvmTransactionPreparationExtractor.Generated.replaceAuthorityState,
    EvmTransactionPreparationExtractor.Reference.replaceAuthorityState, mapWorld]
  rw [map_replaceAuthorityStateList]

theorem map_replaceAuthorityState_physicalExists
    (world : Generated.WorldFacts) (replacement : Generated.AuthorityStateFacts) :
    (EvmTransactionPreparationExtractor.Generated.replaceAuthorityState world replacement).physicalExists =
      (EvmTransactionPreparationExtractor.Reference.replaceAuthorityState (mapWorld world)
        (mapAuthorityState replacement)).physicalExists := by
  have h := congrArg (fun value : EvmTransactionPreparationExtractor.Reference.WorldFacts => value.physicalExists)
    (map_replaceAuthorityState world replacement)
  simpa [mapWorld] using h

theorem map_replaceAuthorityState_logicalExists
    (world : Generated.WorldFacts) (replacement : Generated.AuthorityStateFacts) :
    (EvmTransactionPreparationExtractor.Generated.replaceAuthorityState world replacement).logicalExists =
      (EvmTransactionPreparationExtractor.Reference.replaceAuthorityState (mapWorld world)
        (mapAuthorityState replacement)).logicalExists := by
  have h := congrArg (fun value : EvmTransactionPreparationExtractor.Reference.WorldFacts => value.logicalExists)
    (map_replaceAuthorityState world replacement)
  simpa [mapWorld] using h

theorem map_replaceAuthorityState_currentPhysicalExists
    (world : Generated.WorldFacts) (replacement : Generated.AuthorityStateFacts) :
    (EvmTransactionPreparationExtractor.Generated.replaceAuthorityState world replacement).currentPhysicalExists =
      (EvmTransactionPreparationExtractor.Reference.replaceAuthorityState (mapWorld world)
        (mapAuthorityState replacement)).currentPhysicalExists := by
  have h := congrArg (fun value : EvmTransactionPreparationExtractor.Reference.WorldFacts => value.currentPhysicalExists)
    (map_replaceAuthorityState world replacement)
  simpa [mapWorld] using h

theorem map_replaceAuthorityState_currentLogicalExists
    (world : Generated.WorldFacts) (replacement : Generated.AuthorityStateFacts) :
    (EvmTransactionPreparationExtractor.Generated.replaceAuthorityState world replacement).currentLogicalExists =
      (EvmTransactionPreparationExtractor.Reference.replaceAuthorityState (mapWorld world)
        (mapAuthorityState replacement)).currentLogicalExists := by
  have h := congrArg (fun value : EvmTransactionPreparationExtractor.Reference.WorldFacts => value.currentLogicalExists)
    (map_replaceAuthorityState world replacement)
  simpa [mapWorld] using h

theorem map_replaceAuthorityState_currentNonce
    (world : Generated.WorldFacts) (replacement : Generated.AuthorityStateFacts) :
    (EvmTransactionPreparationExtractor.Generated.replaceAuthorityState world replacement).currentNonce =
      (EvmTransactionPreparationExtractor.Reference.replaceAuthorityState (mapWorld world)
        (mapAuthorityState replacement)).currentNonce := by
  have h := congrArg (fun value : EvmTransactionPreparationExtractor.Reference.WorldFacts => value.currentNonce)
    (map_replaceAuthorityState world replacement)
  simpa [mapWorld] using h

theorem map_replaceAuthorityState_currentCode
    (world : Generated.WorldFacts) (replacement : Generated.AuthorityStateFacts) :
    (EvmTransactionPreparationExtractor.Generated.replaceAuthorityState world replacement).currentCode =
      (EvmTransactionPreparationExtractor.Reference.replaceAuthorityState (mapWorld world)
        (mapAuthorityState replacement)).currentCode := by
  have h := congrArg (fun value : EvmTransactionPreparationExtractor.Reference.WorldFacts => value.currentCode)
    (map_replaceAuthorityState world replacement)
  simpa [mapWorld] using h

theorem map_replaceAuthorityState_currentDelegation
    (world : Generated.WorldFacts) (replacement : Generated.AuthorityStateFacts) :
    (EvmTransactionPreparationExtractor.Generated.replaceAuthorityState world replacement).currentDelegation =
      (EvmTransactionPreparationExtractor.Reference.replaceAuthorityState (mapWorld world)
        (mapAuthorityState replacement)).currentDelegation := by
  have h := congrArg (fun value : EvmTransactionPreparationExtractor.Reference.WorldFacts => value.currentDelegation)
    (map_replaceAuthorityState world replacement)
  simpa [mapWorld] using h

theorem map_replaceAuthorityState_currentBalance
    (world : Generated.WorldFacts) (replacement : Generated.AuthorityStateFacts) :
    (EvmTransactionPreparationExtractor.Generated.replaceAuthorityState world replacement).currentBalance =
      (EvmTransactionPreparationExtractor.Reference.replaceAuthorityState (mapWorld world)
        (mapAuthorityState replacement)).currentBalance := by
  have h := congrArg (fun value : EvmTransactionPreparationExtractor.Reference.WorldFacts => value.currentBalance)
    (map_replaceAuthorityState world replacement)
  simpa [mapWorld] using h

theorem map_replaceAuthorityState_recipientBalance
    (world : Generated.WorldFacts) (replacement : Generated.AuthorityStateFacts) :
    (EvmTransactionPreparationExtractor.Generated.replaceAuthorityState world replacement).recipientBalance =
      (EvmTransactionPreparationExtractor.Reference.replaceAuthorityState (mapWorld world)
        (mapAuthorityState replacement)).recipientBalance := by
  have h := congrArg (fun value : EvmTransactionPreparationExtractor.Reference.WorldFacts => value.recipientBalance)
    (map_replaceAuthorityState world replacement)
  simpa [mapWorld] using h

theorem map_replaceAuthorityState_authorityStates
    (world : Generated.WorldFacts) (replacement : Generated.AuthorityStateFacts) :
    mapAuthorityStates
        (EvmTransactionPreparationExtractor.Generated.replaceAuthorityState world replacement).authorityStates =
      (EvmTransactionPreparationExtractor.Reference.replaceAuthorityState (mapWorld world)
        (mapAuthorityState replacement)).authorityStates := by
  have h := congrArg (fun value : EvmTransactionPreparationExtractor.Reference.WorldFacts => value.authorityStates)
    (map_replaceAuthorityState world replacement)
  simpa [mapWorld] using h

theorem map_authorizationCharge
    (state : Generated.AuthorizationState) (label : String) (amount : Int) :
    (EvmTransactionPreparationExtractor.Generated.authorizationCharge state label amount).map
        mapAuthorizationState =
      (EvmTransactionPreparationExtractor.Reference.authorizationCharge
        (mapAuthorizationState state) label amount).map id := by
  cases hCharge : EvmTransactionPreparationExtractor.Generated.chargeState state.gas amount with
  | none =>
      have hChargeRef : EvmTransactionPreparationExtractor.Reference.chargeState (mapGas state.gas) amount = none := by
        rw [← map_chargeState]
        simp [hCharge]
      simp [EvmTransactionPreparationExtractor.Generated.authorizationCharge,
        EvmTransactionPreparationExtractor.Reference.authorizationCharge, hCharge, hChargeRef,
        mapAuthorizationState]
  | some gas =>
      have hChargeRef : EvmTransactionPreparationExtractor.Reference.chargeState (mapGas state.gas) amount = some (mapGas gas) := by
        rw [← map_chargeState]
        simp [hCharge]
      have hChargeRef' :
          EvmTransactionPreparationExtractor.Reference.chargeState
              (mapAuthorizationState state).gas amount = some (mapGas gas) := by
        simpa [mapAuthorizationState] using hChargeRef
      simp [EvmTransactionPreparationExtractor.Generated.authorizationCharge,
        EvmTransactionPreparationExtractor.Reference.authorizationCharge, hCharge,
        hChargeRef']
      cases state
      simp [mapAuthorizationState, mapEvents_append, mapEvent]

theorem map_authorizationExecutionCharge
    (state : Generated.AuthorizationState) (label : String) (amount : Nat) :
    (EvmTransactionPreparationExtractor.Generated.authorizationExecutionCharge state label amount).map
        mapAuthorizationState =
      (EvmTransactionPreparationExtractor.Reference.authorizationExecutionCharge
        (mapAuthorizationState state) label amount).map id := by
  cases hCharge : EvmTransactionPreparationExtractor.Generated.chargeExecution state.gas amount with
  | none =>
      have hChargeRef : EvmTransactionPreparationExtractor.Reference.chargeExecution (mapGas state.gas) amount = none := by
        rw [← map_chargeExecution]
        simp [hCharge]
      simp [EvmTransactionPreparationExtractor.Generated.authorizationExecutionCharge,
        EvmTransactionPreparationExtractor.Reference.authorizationExecutionCharge, hCharge, hChargeRef,
        mapAuthorizationState]
  | some gas =>
      have hChargeRef : EvmTransactionPreparationExtractor.Reference.chargeExecution (mapGas state.gas) amount = some (mapGas gas) := by
        rw [← map_chargeExecution]
        simp [hCharge]
      have hChargeRef' :
          EvmTransactionPreparationExtractor.Reference.chargeExecution
              (mapAuthorizationState state).gas amount = some (mapGas gas) := by
        simpa [mapAuthorizationState] using hChargeRef
      simp [EvmTransactionPreparationExtractor.Generated.authorizationExecutionCharge,
        EvmTransactionPreparationExtractor.Reference.authorizationExecutionCharge, hCharge,
        hChargeRef']
      cases state
      simp [mapAuthorizationState, mapEvents_append, mapEvent]

theorem map_authorizationWriteOog (state : Generated.AuthorizationState) :
    mapAuthorizationState
        { state with gas := { state.gas with value := 0 }, failed := true } =
      { mapAuthorizationState state with
        gas := { (mapAuthorizationState state).gas with value := 0 }, failed := true } := by
  cases state
  rfl

theorem map_authorizationFailedState (state : Generated.AuthorizationState) :
    mapAuthorizationState { state with failed := true } =
      { mapAuthorizationState state with failed := true } := by
  cases state
  rfl

theorem generatedAuthorizationCharge_preserves_lists
    (state : Generated.AuthorizationState) (label : String) (amount : Int)
    (next : Generated.AuthorizationState)
    (hCharge : EvmTransactionPreparationExtractor.Generated.authorizationCharge state label amount = some next) :
    next.writtenAuthorities = state.writtenAuthorities ∧
      next.delegationSetFor = state.delegationSetFor := by
  cases hGas : EvmTransactionPreparationExtractor.Generated.chargeState state.gas amount with
  | none =>
      simp [EvmTransactionPreparationExtractor.Generated.authorizationCharge, hGas] at hCharge
  | some gas =>
      simp [EvmTransactionPreparationExtractor.Generated.authorizationCharge, hGas] at hCharge
      cases hCharge
      simp

theorem generatedAuthorizationExecutionCharge_preserves_lists
    (state : Generated.AuthorizationState) (label : String) (amount : Nat)
    (next : Generated.AuthorizationState)
    (hCharge : EvmTransactionPreparationExtractor.Generated.authorizationExecutionCharge state label amount = some next) :
    next.writtenAuthorities = state.writtenAuthorities ∧
      next.delegationSetFor = state.delegationSetFor := by
  cases hGas : EvmTransactionPreparationExtractor.Generated.chargeExecution state.gas amount with
  | none =>
      simp [EvmTransactionPreparationExtractor.Generated.authorizationExecutionCharge, hGas] at hCharge
  | some gas =>
      simp [EvmTransactionPreparationExtractor.Generated.authorizationExecutionCharge, hGas] at hCharge
      cases hCharge
      simp

theorem map_applyAuthorizationWorld
    (input : Generated.Input) (world : Generated.WorldFacts) (auth : Generated.AuthorizationFacts) :
    mapWorld (EvmTransactionPreparationExtractor.Generated.applyAuthorizationWorld input world auth) =
      EvmTransactionPreparationExtractor.Reference.applyAuthorizationWorld (mapInput input)
        (mapWorld world) (mapAuthorization auth) := by
  cases hFind : EvmTransactionPreparationExtractor.Generated.findAuthorityState world auth.authority with
  | none =>
      have hFindRef := map_findAuthorityState world auth.authority
      simp only [EvmTransactionPreparationExtractor.Generated.applyAuthorizationWorld,
        EvmTransactionPreparationExtractor.Reference.applyAuthorizationWorld, hFind]
      rw [hFind] at hFindRef
      have hFindRefMapped :
          EvmTransactionPreparationExtractor.Reference.findAuthorityState (mapWorld world)
              (mapAuthorization auth).authority = none := by
                 simpa [mapAuthorization, Option.map] using hFindRef
      simp [hFindRefMapped]
  | some state =>
      have hFindRef := map_findAuthorityState world auth.authority
      simp only [EvmTransactionPreparationExtractor.Generated.applyAuthorizationWorld,
        EvmTransactionPreparationExtractor.Reference.applyAuthorizationWorld, hFind]
      rw [hFind] at hFindRef
      have hFindRefMapped :
          EvmTransactionPreparationExtractor.Reference.findAuthorityState (mapWorld world)
              (mapAuthorization auth).authority = some (mapAuthorityState state) := by
                 simpa [mapAuthorization, mapAuthorityState, Option.map] using hFindRef
      rw [hFindRefMapped]
      rw [map_recordAccountWrite]
      cases input
      cases world
      cases state
      cases auth
      simp only [EvmTransactionPreparationExtractor.Reference.recordAccountWrite,
        mapWorld]
      rw [map_replaceAuthorityState_physicalExists,
        map_replaceAuthorityState_logicalExists,
        map_replaceAuthorityState_currentPhysicalExists,
        map_replaceAuthorityState_currentLogicalExists,
        map_replaceAuthorityState_currentNonce,
        map_replaceAuthorityState_currentCode,
        map_replaceAuthorityState_currentDelegation,
        map_replaceAuthorityState_currentBalance,
        map_replaceAuthorityState_recipientBalance,
        map_replaceAuthorityState_authorityStates]
      rfl

theorem mapWorld_applyAuthorizationWorld_recordUpdate
    (input : Generated.Input) (world : Generated.WorldFacts)
    (auth : Generated.AuthorizationFacts) (extra : Nat) :
    mapWorld
        { EvmTransactionPreparationExtractor.Generated.applyAuthorizationWorld input world auth with
          codeInsertRefunds :=
            (EvmTransactionPreparationExtractor.Generated.applyAuthorizationWorld input world auth).codeInsertRefunds +
              extra } =
      { EvmTransactionPreparationExtractor.Reference.applyAuthorizationWorld (mapInput input)
          (mapWorld world) (mapAuthorization auth) with
        codeInsertRefunds :=
          (EvmTransactionPreparationExtractor.Reference.applyAuthorizationWorld (mapInput input)
            (mapWorld world) (mapAuthorization auth)).codeInsertRefunds + extra } := by
  rw [mapWorld_recordUpdate]
  rw [map_applyAuthorizationWorld]
  have hCodeInsert :
      (EvmTransactionPreparationExtractor.Generated.applyAuthorizationWorld input world auth).codeInsertRefunds =
        (EvmTransactionPreparationExtractor.Reference.applyAuthorizationWorld (mapInput input)
          (mapWorld world) (mapAuthorization auth)).codeInsertRefunds := by
    have hMapped := congrArg (fun value : Reference.WorldFacts => value.codeInsertRefunds)
      (map_applyAuthorizationWorld input world auth)
    simpa [mapWorld] using hMapped
  rw [hCodeInsert]

theorem map_applyAuthorizationWorld_codeInsert
    (input : Generated.Input) (world : Generated.WorldFacts)
    (auth : Generated.AuthorizationFacts) :
    (EvmTransactionPreparationExtractor.Generated.applyAuthorizationWorld input world auth).codeInsertRefunds =
      (EvmTransactionPreparationExtractor.Reference.applyAuthorizationWorld (mapInput input)
        (mapWorld world) (mapAuthorization auth)).codeInsertRefunds := by
  have hMapped := congrArg (fun value : Reference.WorldFacts => value.codeInsertRefunds)
    (map_applyAuthorizationWorld input world auth)
  simpa [mapWorld] using hMapped

theorem map_authorizationSuccessState
    (input : Generated.Input) (charged : Generated.AuthorizationState)
    (auth : Generated.AuthorizationFacts) (authorityState : Generated.AuthorityStateFacts)
    (extra : Nat) :
    mapAuthorizationState
        { charged with
          world :=
            { EvmTransactionPreparationExtractor.Generated.applyAuthorizationWorld input charged.world auth with
              codeInsertRefunds :=
                (EvmTransactionPreparationExtractor.Generated.applyAuthorizationWorld input charged.world auth).codeInsertRefunds +
                  extra },
          refunds := charged.refunds + extra,
          writtenAuthorities :=
            if EvmTransactionPreparationExtractor.Generated.hasValueIn auth.authority charged.writtenAuthorities = false then
              charged.writtenAuthorities ++ [auth.authority]
            else charged.writtenAuthorities,
          delegationSetFor :=
            if (auth.clearsDelegation = false ∧ authorityState.delegatedBeforeTx = none) ∧
                EvmTransactionPreparationExtractor.Generated.hasValueIn auth.authority charged.delegationSetFor = false then
              charged.delegationSetFor ++ [auth.authority]
            else charged.delegationSetFor,
          events :=
            charged.events ++
              [EvmTransactionPreparationExtractor.Generated.Event.accountWrite auth.authority,
                EvmTransactionPreparationExtractor.Generated.Event.delegationWrite auth.authority auth.codeAddress] } =
      { gas := mapGas charged.gas, baseline := mapGas charged.baseline,
        world :=
          { EvmTransactionPreparationExtractor.Reference.applyAuthorizationWorld (mapInput input)
              (mapWorld charged.world) (mapAuthorization auth) with
            codeInsertRefunds :=
              (EvmTransactionPreparationExtractor.Reference.applyAuthorizationWorld (mapInput input)
                (mapWorld charged.world) (mapAuthorization auth)).codeInsertRefunds + extra },
        access := mapAccess charged.access,
        refunds := charged.refunds + extra,
        writtenAuthorities :=
          if EvmTransactionPreparationExtractor.Reference.hasValueIn
              (mapAuthorization auth).authority charged.writtenAuthorities = false then
            charged.writtenAuthorities ++ [(mapAuthorization auth).authority]
          else charged.writtenAuthorities,
        delegationSetFor :=
          if ((mapAuthorization auth).clearsDelegation = false ∧
                (mapAuthorityState authorityState).delegatedBeforeTx = none) ∧
              EvmTransactionPreparationExtractor.Reference.hasValueIn
                (mapAuthorization auth).authority charged.delegationSetFor = false then
            charged.delegationSetFor ++ [(mapAuthorization auth).authority]
          else charged.delegationSetFor,
        events := mapEvents
          (charged.events ++
            [EvmTransactionPreparationExtractor.Generated.Event.accountWrite auth.authority,
              EvmTransactionPreparationExtractor.Generated.Event.delegationWrite auth.authority auth.codeAddress]),
        failed := charged.failed } := by
  cases input
  cases charged
  cases auth
  rw [mapAuthorizationState_record]
  rw [mapWorld_applyAuthorizationWorld_recordUpdate]
  simp [mapAuthorization, mapAuthorityState, mapInput, mapHandoff, mapAccess,
    map_hasValueIn]
  constructor <;> rfl

theorem mapAuthorizationState_option {α : Type}
    (value : Option α) (fallback : Generated.AuthorizationState)
    (success : α → Generated.AuthorizationState) :
    mapAuthorizationState (match value with | none => fallback | some item => success item) =
      match value with | none => mapAuthorizationState fallback | some item => mapAuthorizationState (success item) := by
  cases value <;> rfl

theorem mapAuthorizationState_ite (condition : Prop) [Decidable condition]
    (yes no : Generated.AuthorizationState) :
    mapAuthorizationState (if condition then yes else no) =
      if condition then mapAuthorizationState yes else mapAuthorizationState no := by
  split <;> rfl

theorem option_map_ite {α β : Type} (condition : Prop) [Decidable condition]
    (yes no : Option α) (f : α → β) :
    (if condition then yes else no).map f =
      if condition then yes.map f else no.map f := by
  split <;> rfl

theorem option_match_map {α β γ : Type}
    (value : Option α) (f : α → β) (fallback : γ) (success : β → γ) :
    (match value.map f with | none => fallback | some item => success item) =
      match value with | none => fallback | some item => success (f item) := by
  cases value <;> rfl

theorem map_authorizationSuccess
    (input : Generated.Input) (charged : Generated.AuthorizationState)
    (auth : Generated.AuthorizationFacts) (extra : Nat) (firstWrite firstDelegation : Bool) :
    mapAuthorizationState
        { charged with
          world := { EvmTransactionPreparationExtractor.Generated.applyAuthorizationWorld input charged.world auth with
            codeInsertRefunds := (EvmTransactionPreparationExtractor.Generated.applyAuthorizationWorld input charged.world auth).codeInsertRefunds + extra },
          refunds := charged.refunds + extra,
          writtenAuthorities := if firstWrite then charged.writtenAuthorities ++ [auth.authority] else charged.writtenAuthorities,
          delegationSetFor := if firstDelegation then charged.delegationSetFor ++ [auth.authority] else charged.delegationSetFor,
          events := charged.events ++ [Generated.Event.accountWrite auth.authority, Generated.Event.delegationWrite auth.authority auth.codeAddress] } =
      { mapAuthorizationState charged with
        world := { EvmTransactionPreparationExtractor.Reference.applyAuthorizationWorld (mapInput input) (mapWorld charged.world) (mapAuthorization auth) with
          codeInsertRefunds := (EvmTransactionPreparationExtractor.Reference.applyAuthorizationWorld (mapInput input) (mapWorld charged.world) (mapAuthorization auth)).codeInsertRefunds + extra },
        refunds := charged.refunds + extra,
        writtenAuthorities := if firstWrite then charged.writtenAuthorities ++ [auth.authority] else charged.writtenAuthorities,
        delegationSetFor := if firstDelegation then charged.delegationSetFor ++ [auth.authority] else charged.delegationSetFor,
        events := mapEvents charged.events ++ [Reference.Event.accountWrite auth.authority, Reference.Event.delegationWrite auth.authority auth.codeAddress] } := by
  rw [mapAuthorizationState_record, mapWorld_applyAuthorizationWorld_recordUpdate,
    mapEvents_append_two]
  rfl

private def generatedAuthorizationCharges
    (input : Generated.Input) (observed : Generated.AuthorizationState)
    (auth : Generated.AuthorizationFacts) (authorityState : Generated.AuthorityStateFacts) :
    Generated.AuthorizationState :=
  let firstWrite := !EvmTransactionPreparationExtractor.Generated.hasValueIn auth.authority observed.writtenAuthorities
  let firstDelegation := !auth.clearsDelegation && authorityState.delegatedBeforeTx.isNone &&
    !EvmTransactionPreparationExtractor.Generated.hasValueIn auth.authority observed.delegationSetFor
  match if input.handoff.spec.eip8037 && !authorityState.current.logicalExists then
    EvmTransactionPreparationExtractor.Generated.authorizationCharge observed "new-account" auth.newAccountStateCharge else some observed with
  | none => { observed with failed := true }
  | some afterNew =>
    match if input.handoff.spec.eip8037 && firstWrite then
      EvmTransactionPreparationExtractor.Generated.authorizationExecutionCharge afterNew "account-write" auth.accountWriteCharge else some afterNew with
    | none => { afterNew with gas := { afterNew.gas with value := 0 }, failed := true }
    | some afterWrite =>
      match if input.handoff.spec.eip8037 && firstDelegation then
        EvmTransactionPreparationExtractor.Generated.authorizationCharge afterWrite "per-auth" auth.perAuthStateCharge else some afterWrite with
      | none => { afterWrite with failed := true }
      | some charged =>
        let nextWorld := EvmTransactionPreparationExtractor.Generated.applyAuthorizationWorld input charged.world auth
        let extra := if input.handoff.spec.eip8037 || !authorityState.current.physicalExists then 0 else 1
        { charged with
          world := { nextWorld with codeInsertRefunds := nextWorld.codeInsertRefunds + extra },
          refunds := charged.refunds + extra,
          writtenAuthorities := if firstWrite then charged.writtenAuthorities ++ [auth.authority] else charged.writtenAuthorities,
          delegationSetFor := if firstDelegation then charged.delegationSetFor ++ [auth.authority] else charged.delegationSetFor,
          events := charged.events ++ [Generated.Event.accountWrite auth.authority, Generated.Event.delegationWrite auth.authority auth.codeAddress] }

private def referenceAuthorizationCharges
    (input : Reference.Input) (observed : Reference.AuthorizationState)
    (auth : Reference.AuthorizationFacts) (authorityState : Reference.AuthorityStateFacts) :
    Reference.AuthorizationState :=
  let firstWrite := !EvmTransactionPreparationExtractor.Reference.hasValueIn auth.authority observed.writtenAuthorities
  let firstDelegation := !auth.clearsDelegation && authorityState.delegatedBeforeTx.isNone &&
    !EvmTransactionPreparationExtractor.Reference.hasValueIn auth.authority observed.delegationSetFor
  match if input.handoff.spec.eip8037 && !authorityState.current.logicalExists then
    EvmTransactionPreparationExtractor.Reference.authorizationCharge observed "new-account" auth.newAccountStateCharge else some observed with
  | none => { observed with failed := true }
  | some afterNew =>
    match if input.handoff.spec.eip8037 && firstWrite then
      EvmTransactionPreparationExtractor.Reference.authorizationExecutionCharge afterNew "account-write" auth.accountWriteCharge else some afterNew with
    | none => { afterNew with gas := { afterNew.gas with value := 0 }, failed := true }
    | some afterWrite =>
      match if input.handoff.spec.eip8037 && firstDelegation then
        EvmTransactionPreparationExtractor.Reference.authorizationCharge afterWrite "per-auth" auth.perAuthStateCharge else some afterWrite with
      | none => { afterWrite with failed := true }
      | some charged =>
        let nextWorld := EvmTransactionPreparationExtractor.Reference.applyAuthorizationWorld input charged.world auth
        let extra := if input.handoff.spec.eip8037 || !authorityState.current.physicalExists then 0 else 1
        { charged with
          world := { nextWorld with codeInsertRefunds := nextWorld.codeInsertRefunds + extra },
          refunds := charged.refunds + extra,
          writtenAuthorities := if firstWrite then charged.writtenAuthorities ++ [auth.authority] else charged.writtenAuthorities,
          delegationSetFor := if firstDelegation then charged.delegationSetFor ++ [auth.authority] else charged.delegationSetFor,
          events := charged.events ++ [Reference.Event.accountWrite auth.authority, Reference.Event.delegationWrite auth.authority auth.codeAddress] }

theorem map_authorizationCharges
    (input : Generated.Input) (observed : Generated.AuthorizationState)
    (auth : Generated.AuthorizationFacts) (authorityState : Generated.AuthorityStateFacts) :
    mapAuthorizationState (generatedAuthorizationCharges input observed auth authorityState) =
      referenceAuthorizationCharges (mapInput input) (mapAuthorizationState observed)
        (mapAuthorization auth) (mapAuthorityState authorityState) := by
  have hStateWritten : (mapAuthorizationState observed).writtenAuthorities = observed.writtenAuthorities := rfl
  have hStateDelegations : (mapAuthorizationState observed).delegationSetFor = observed.delegationSetFor := rfl
  have hEip : (mapInput input).handoff.spec.eip8037 = input.handoff.spec.eip8037 := rfl
  have hNew :
      (if input.handoff.spec.eip8037 && !authorityState.current.logicalExists then
        EvmTransactionPreparationExtractor.Generated.authorizationCharge observed "new-account" auth.newAccountStateCharge
       else some observed).map mapAuthorizationState =
      (if input.handoff.spec.eip8037 && !authorityState.current.logicalExists then
        EvmTransactionPreparationExtractor.Reference.authorizationCharge (mapAuthorizationState observed) "new-account" auth.newAccountStateCharge
       else some (mapAuthorizationState observed)) := by
    split <;> simp [map_authorizationCharge]
  unfold generatedAuthorizationCharges referenceAuthorizationCharges
  simp only [hStateWritten, hStateDelegations, hEip, mapAuthorization, mapAuthorityState,
    ← map_hasValueIn]
  rw [← hNew]
  cases hNewResult : (if input.handoff.spec.eip8037 && !authorityState.current.logicalExists then
      EvmTransactionPreparationExtractor.Generated.authorizationCharge observed "new-account" auth.newAccountStateCharge else some observed) with
  | none => simp only [Option.map_none, mapAuthorizationState]
  | some afterNew =>
    simp only [Option.map_some]
    have hWrite :
        (if input.handoff.spec.eip8037 && !EvmTransactionPreparationExtractor.Generated.hasValueIn auth.authority observed.writtenAuthorities then
          EvmTransactionPreparationExtractor.Generated.authorizationExecutionCharge afterNew "account-write" auth.accountWriteCharge
         else some afterNew).map mapAuthorizationState =
        (if input.handoff.spec.eip8037 && !EvmTransactionPreparationExtractor.Generated.hasValueIn auth.authority observed.writtenAuthorities then
          EvmTransactionPreparationExtractor.Reference.authorizationExecutionCharge (mapAuthorizationState afterNew) "account-write" auth.accountWriteCharge
         else some (mapAuthorizationState afterNew)) := by
      split <;> simp [map_authorizationExecutionCharge]
    rw [← hWrite]
    cases hWriteResult : (if input.handoff.spec.eip8037 && !EvmTransactionPreparationExtractor.Generated.hasValueIn auth.authority observed.writtenAuthorities then
        EvmTransactionPreparationExtractor.Generated.authorizationExecutionCharge afterNew "account-write" auth.accountWriteCharge else some afterNew) with
    | none => simpa only [Option.map_none, mapAuthorizationState] using map_authorizationWriteOog afterNew
    | some afterWrite =>
      simp only [Option.map_some]
      have hPer :
          (if input.handoff.spec.eip8037 && (!auth.clearsDelegation && authorityState.delegatedBeforeTx.isNone &&
              !EvmTransactionPreparationExtractor.Generated.hasValueIn auth.authority observed.delegationSetFor) then
            EvmTransactionPreparationExtractor.Generated.authorizationCharge afterWrite "per-auth" auth.perAuthStateCharge
           else some afterWrite).map mapAuthorizationState =
          (if input.handoff.spec.eip8037 && (!auth.clearsDelegation && authorityState.delegatedBeforeTx.isNone &&
              !EvmTransactionPreparationExtractor.Generated.hasValueIn auth.authority observed.delegationSetFor) then
            EvmTransactionPreparationExtractor.Reference.authorizationCharge (mapAuthorizationState afterWrite) "per-auth" auth.perAuthStateCharge
           else some (mapAuthorizationState afterWrite)) := by
        split <;> simp [map_authorizationCharge]
      rw [← hPer]
      cases hPerResult : (if input.handoff.spec.eip8037 && (!auth.clearsDelegation && authorityState.delegatedBeforeTx.isNone &&
          !EvmTransactionPreparationExtractor.Generated.hasValueIn auth.authority observed.delegationSetFor) then
          EvmTransactionPreparationExtractor.Generated.authorizationCharge afterWrite "per-auth" auth.perAuthStateCharge else some afterWrite) with
      | none => simp only [Option.map_none, mapAuthorizationState]
      | some charged =>
        simp only [Option.map_some]
        simpa only [mapAuthorizationState, mapAuthorization] using map_authorizationSuccess input charged auth
          (if input.handoff.spec.eip8037 || !authorityState.current.physicalExists then 0 else 1)
          (!EvmTransactionPreparationExtractor.Generated.hasValueIn auth.authority observed.writtenAuthorities)
          (!auth.clearsDelegation && authorityState.delegatedBeforeTx.isNone &&
            !EvmTransactionPreparationExtractor.Generated.hasValueIn auth.authority observed.delegationSetFor)

theorem map_processAuthorizationTuple
    (input : Generated.Input) (state : Generated.AuthorizationState)
    (auth : Generated.AuthorizationFacts) :
    mapAuthorizationState (EvmTransactionPreparationExtractor.Generated.processAuthorizationTuple input state auth) =
      EvmTransactionPreparationExtractor.Reference.processAuthorizationTuple (mapInput input)
        (mapAuthorizationState state) (mapAuthorization auth) := by
  have hState : mapAuthorizationState state =
      { gas := mapGas state.gas, baseline := mapGas state.baseline,
        world := mapWorld state.world, access := mapAccess state.access,
        refunds := state.refunds, writtenAuthorities := state.writtenAuthorities,
        delegationSetFor := state.delegationSetFor, events := mapEvents state.events,
        failed := state.failed } := rfl
  have hEip : (mapInput input).handoff.spec.eip8037 = input.handoff.spec.eip8037 := rfl
  have hAuthority : (mapAuthorization auth).authority = auth.authority := rfl
  have hNonce : (mapAuthorization auth).expectedNonce = auth.expectedNonce := rfl
  have hCodeAddress : (mapAuthorization auth).codeAddress = auth.codeAddress := rfl
  have hClears : (mapAuthorization auth).clearsDelegation = auth.clearsDelegation := rfl
  have hNewCharge : (mapAuthorization auth).newAccountStateCharge = auth.newAccountStateCharge := rfl
  have hWriteCharge : (mapAuthorization auth).accountWriteCharge = auth.accountWriteCharge := rfl
  have hPerCharge : (mapAuthorization auth).perAuthStateCharge = auth.perAuthStateCharge := rfl
  have hPre := map_authPreliminaryValid auth
  have hCoherent := map_authorizationCoherent state.world auth
  have hFindMap := map_findAuthorityState state.world auth.authority
  cases hFailed : state.failed with
  | true =>
    simp only [EvmTransactionPreparationExtractor.Generated.processAuthorizationTuple,
      EvmTransactionPreparationExtractor.Reference.processAuthorizationTuple, hFailed, hState, if_true]
  | false =>
    cases hPreliminary : EvmTransactionPreparationExtractor.Generated.authPreliminaryValid auth with
    | false =>
      cases hFind : EvmTransactionPreparationExtractor.Generated.findAuthorityState state.world auth.authority <;>
        simp [EvmTransactionPreparationExtractor.Generated.processAuthorizationTuple,
          EvmTransactionPreparationExtractor.Reference.processAuthorizationTuple,
          hFailed, ← hPre, hAuthority, hNonce, map_findAuthorityState, hFind,
          hPreliminary, mapAuthorizationState]
    | true =>
      let observed : Generated.AuthorizationState :=
        { state with
          world := EvmTransactionPreparationExtractor.Generated.recordAccountRead state.world auth.authority,
          access := EvmTransactionPreparationExtractor.Generated.recordAccessRead
            (EvmTransactionPreparationExtractor.Generated.warmAccount state.access auth.authority) auth.authority,
          events := state.events ++ [Generated.Event.authorizationWarm auth.authority, Generated.Event.authorizationRead auth.authority],
          failed := false }
      have hObserved := map_observedAuthorization_true_explicit state auth false
      have hEvents : mapEvents (state.events ++
          [Generated.Event.authorizationWarm auth.authority, Generated.Event.authorizationRead auth.authority]) =
          mapEvents state.events ++
            [Reference.Event.authorizationWarm auth.authority, Reference.Event.authorizationRead auth.authority] := by
        rw [mapEvents_append_two]
        rfl
      cases hFind : EvmTransactionPreparationExtractor.Generated.findAuthorityState state.world auth.authority with
      | none =>
        simp only [EvmTransactionPreparationExtractor.Generated.processAuthorizationTuple,
          EvmTransactionPreparationExtractor.Reference.processAuthorizationTuple,
          hState, hFailed, ← hPre, hAuthority, map_findAuthorityState, hFind,
          hPreliminary, Bool.false_eq_true, if_false, if_true, Option.map_none]
        exact map_observedAuthorization_true_explicit state auth true
      | some authorityState =>
        have hCore := map_authorizationCharges input observed auth authorityState
        simp only [EvmTransactionPreparationExtractor.Generated.processAuthorizationTuple,
          EvmTransactionPreparationExtractor.Reference.processAuthorizationTuple,
          hState, hFailed, ← hPre, ← hCoherent, hAuthority, hNonce, hCodeAddress, hClears, hNewCharge, hWriteCharge, hPerCharge, map_findAuthorityState, hFind,
          hPreliminary, Bool.false_eq_true, if_false, if_true, Option.map_some,
          EvmTransactionPreparationExtractor.Generated.authHasCode,
          EvmTransactionPreparationExtractor.Reference.authHasCode,
          EvmTransactionPreparationExtractor.Generated.authHasDelegation,
          EvmTransactionPreparationExtractor.Reference.authHasDelegation,
          mapAuthorityState, hEip]
        simp only [← map_recordAccountRead, ← map_recordAccessRead, ← map_warmAccount,
          ← hEvents]
        let invalid := !true || !EvmTransactionPreparationExtractor.Generated.authorizationCoherent state.world auth ||
          !(!EvmTransactionPreparationExtractor.Generated.authHasCode authorityState.current ||
            EvmTransactionPreparationExtractor.Generated.authHasDelegation authorityState.current) ||
          !(authorityState.current.nonce == auth.expectedNonce)
        change mapAuthorizationState (if invalid then observed else generatedAuthorizationCharges input observed auth authorityState) =
          if invalid then mapAuthorizationState observed else
            referenceAuthorizationCharges (mapInput input) (mapAuthorizationState observed)
              (mapAuthorization auth) (mapAuthorityState authorityState)
        split
        · rfl
        · exact hCore

theorem map_processAuthorizationList
    (input : Generated.Input) (state : Generated.AuthorizationState)
    (auths : List Generated.AuthorizationFacts) :
    mapAuthorizationState (EvmTransactionPreparationExtractor.Generated.processAuthorizationList input state auths) =
      EvmTransactionPreparationExtractor.Reference.processAuthorizationList (mapInput input)
        (mapAuthorizationState state) (mapAuthorizations auths) := by
  induction auths generalizing state with
  | nil => rfl
  | cons auth rest ih =>
      simp only [EvmTransactionPreparationExtractor.Generated.processAuthorizationList,
        EvmTransactionPreparationExtractor.Reference.processAuthorizationList,
        mapAuthorizations]
      rw [ih, map_processAuthorizationTuple]

theorem map_emptyResult
    (input : Generated.Input) (status : Generated.Status) (error : Option String)
    (gas baseline : Generated.GasState) (world : Generated.WorldFacts)
    (access : Generated.AccessObservation) (environment : Generated.Environment)
    (refunds : Nat) (events : List Generated.Event) (vmInput : Option Generated.VmInput) :
    mapResult (EvmTransactionPreparationExtractor.Generated.emptyResult input status error gas baseline world access environment refunds events vmInput) =
      EvmTransactionPreparationExtractor.Reference.emptyResult (mapInput input) (mapStatus status) error
        (mapGas gas) (mapGas baseline) (mapWorld world) (mapAccess access) (mapEnvironment environment) refunds
        (mapEvents events) (vmInput.map mapVmInput) := by
  cases status <;> cases vmInput <;> rfl

theorem map_preparationOog
    (input : Generated.Input) (gas baseline : Generated.GasState) (world : Generated.WorldFacts)
    (access : Generated.AccessObservation) (environment : Generated.Environment)
    (refunds : Nat) (events : List Generated.Event) :
    mapResult (EvmTransactionPreparationExtractor.Generated.preparationOog input gas baseline world access environment refunds events) =
      EvmTransactionPreparationExtractor.Reference.preparationOog (mapInput input) (mapGas gas) (mapGas baseline)
        (mapWorld world) (mapAccess access) (mapEnvironment environment) refunds (mapEvents events) := by
  cases snapshot : input.hasPreExecutionSnapshot <;> cases tracing : access.tracing <;>
    simp [EvmTransactionPreparationExtractor.Generated.preparationOog,
      EvmTransactionPreparationExtractor.Reference.preparationOog, mapResult, mapStatus, mapGas,
      EvmTransactionPreparationExtractor.Generated.emptyResult,
      EvmTransactionPreparationExtractor.Reference.emptyResult,
      mapInput, mapSnapshot, mapWorld, mapAccess, mapEnvironment, mapCode,
      mapEvents_append_list, mapEvents, mapEvent, snapshot, tracing]

theorem map_topLevelCreateOog
    (input : Generated.Input) (gas baseline : Generated.GasState) (postReservoir : Int)
    (world : Generated.WorldFacts) (access : Generated.AccessObservation)
    (environment : Generated.Environment) (refunds : Nat) (events : List Generated.Event) :
    mapResult (EvmTransactionPreparationExtractor.Generated.topLevelCreateOog input gas baseline postReservoir world access environment refunds events) =
      EvmTransactionPreparationExtractor.Reference.topLevelCreateOog (mapInput input) (mapGas gas) (mapGas baseline)
        postReservoir (mapWorld world) (mapAccess access) (mapEnvironment environment) refunds (mapEvents events) := by
  cases tracing : access.tracing <;>
   simp [EvmTransactionPreparationExtractor.Generated.topLevelCreateOog,
    EvmTransactionPreparationExtractor.Reference.topLevelCreateOog, mapResult, mapStatus, mapGas,
    EvmTransactionPreparationExtractor.Generated.emptyResult,
    EvmTransactionPreparationExtractor.Reference.emptyResult,
    mapInput, mapSnapshot, mapWorld, mapAccess, mapEnvironment, mapCode,
    mapEvents_append_list, mapEvents, mapEvent, tracing]

theorem map_deriveRecipient (input : Generated.Input) (world : Generated.WorldFacts) :
    EvmTransactionPreparationExtractor.Generated.deriveRecipient input world =
      EvmTransactionPreparationExtractor.Reference.deriveRecipient (mapInput input) (mapWorld world) := by
  cases recipient : input.handoff.tx.recipient with
  | none =>
      simp [EvmTransactionPreparationExtractor.Generated.deriveRecipient,
        EvmTransactionPreparationExtractor.Reference.deriveRecipient,
        
        EvmTransactionPreparationExtractor.Reference.isCreateTx,
        EvmTransactionPreparationExtractor.Generated.senderCurrentNonce,
        EvmTransactionPreparationExtractor.Reference.senderCurrentNonce,
        map_findAuthorityState, mapInput, mapHandoff, mapTx, recipient]
      cases found : EvmTransactionPreparationExtractor.Generated.findAuthorityState world input.handoff.tx.sender <;>
        simp [mapAuthorityState, mapWorld]
  | some value =>
      simp [EvmTransactionPreparationExtractor.Generated.deriveRecipient,
        EvmTransactionPreparationExtractor.Reference.deriveRecipient,
        
        EvmTransactionPreparationExtractor.Reference.isCreateTx,
        mapInput, mapHandoff, mapTx, recipient]

theorem map_emptyAuthorizationState (input : Generated.Input) :
    mapAuthorizationState (EvmTransactionPreparationExtractor.Generated.emptyAuthorizationState input) =
      EvmTransactionPreparationExtractor.Reference.emptyAuthorizationState (mapInput input) := by
  cases recipient : input.handoff.tx.recipient <;> cases snapshot : input.hasPreExecutionSnapshot <;>
  simp [EvmTransactionPreparationExtractor.Generated.emptyAuthorizationState,
    EvmTransactionPreparationExtractor.Reference.emptyAuthorizationState, mapAuthorizationState,
    EvmTransactionPreparationExtractor.Generated.initialWrittenAccounts,
    EvmTransactionPreparationExtractor.Reference.initialWrittenAccounts,
    mapInput, mapHandoff, mapTx, mapWorld, mapAccess, mapEvent, mapEvents, mapSnapshot, mapGas,
    recipient, snapshot,
    EvmTransactionPreparationExtractor.Generated.appendUnique,
    EvmTransactionPreparationExtractor.Reference.appendUnique,
    EvmTransactionPreparationExtractor.Generated.hasValueIn,
    EvmTransactionPreparationExtractor.Reference.hasValueIn]

theorem map_chargeDelegatedTarget
    (_input : Generated.Input) (enabled alreadyWarm isPrecompile : Bool)
    (gas : Generated.GasState) (target : Option String) (amount : Nat)
    (access : Generated.AccessObservation) :
    (EvmTransactionPreparationExtractor.Generated.chargeDelegatedTarget enabled alreadyWarm isPrecompile gas target amount access).map
        (fun charged => (mapGas charged.1, mapAccess charged.2)) =
      EvmTransactionPreparationExtractor.Reference.chargeDelegatedTarget enabled alreadyWarm isPrecompile (mapGas gas) target amount
        (mapAccess access) := by
  cases enabled with
  | false => rfl
  | true =>
      cases target with
      | none => rfl
      | some address =>
          simp only [EvmTransactionPreparationExtractor.Generated.chargeDelegatedTarget,
            EvmTransactionPreparationExtractor.Reference.chargeDelegatedTarget,
            Bool.not_true, Bool.false_eq_true, ite_false]
          have hAmount : (if alreadyWarm || isPrecompile then (0 : Int) else ↑amount) =
              ↑(if alreadyWarm || isPrecompile then (0 : Nat) else amount) := by
            split <;> rfl
          rw [hAmount]
          rw [← map_chargeExecution]
          cases EvmTransactionPreparationExtractor.Generated.chargeExecution gas
            (↑(if alreadyWarm || isPrecompile then (0 : Nat) else amount) : Int) <;> rfl


theorem map_accessReadForRecipient (enabled : Bool) (recipient : Option String)
    (access : Generated.AccessObservation) :
    mapAccess (if enabled then match recipient with
      | some value => EvmTransactionPreparationExtractor.Generated.recordAccessRead access value
      | none => access
    else access) =
    (if enabled then match recipient with
      | some value => EvmTransactionPreparationExtractor.Reference.recordAccessRead (mapAccess access) value
      | none => mapAccess access
    else mapAccess access) := by
  cases enabled <;> cases recipient <;> simp [map_recordAccessRead]

theorem map_accountReadForRecipient (enabled : Bool) (recipient : Option String)
    (world : Generated.WorldFacts) :
    mapWorld (if enabled then match recipient with
      | some value => EvmTransactionPreparationExtractor.Generated.recordAccountRead world value
      | none => world
    else world) =
    (if enabled then match recipient with
      | some value => EvmTransactionPreparationExtractor.Reference.recordAccountRead (mapWorld world) value
      | none => mapWorld world
    else mapWorld world) := by
  cases enabled <;> cases recipient <;> simp [map_recordAccountRead]

theorem map_warmForTarget (enabled : Bool) (target : Option String)
    (access : Generated.AccessObservation) :
    mapAccess (if enabled then match target with
      | some value => EvmTransactionPreparationExtractor.Generated.warmAccount access value
      | none => access
    else access) =
    (if enabled then match target with
      | some value => EvmTransactionPreparationExtractor.Reference.warmAccount (mapAccess access) value
      | none => mapAccess access
    else mapAccess access) := by
  cases enabled <;> cases target <;> simp [map_warmAccount]

theorem map_targetWasWarm (enabled : Bool) (target : Option String)
    (access : Generated.AccessObservation) :
    (if enabled then match target with
      | some value => EvmTransactionPreparationExtractor.Generated.hasValueIn value access.warmedAccounts
      | none => false
    else false) =
    (if enabled then match target with
      | some value => EvmTransactionPreparationExtractor.Reference.hasValueIn value (mapAccess access).warmedAccounts
      | none => false
    else false) := by
  cases enabled <;> cases target <;> simp [map_hasValueIn, mapAccess]


end EvmTransactionPreparationExtractor.Refinement
