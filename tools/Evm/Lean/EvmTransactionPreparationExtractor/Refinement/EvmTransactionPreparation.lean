-- SPDX-License-Identifier: LGPL-3.0-only
-- Universal field-complete refinement and explicit sibling artifact identities.

import EvmTransactionPreparationExtractor.Refinement.Maps

namespace EvmTransactionPreparationExtractor.Generated

/- Reuse the source matcher so the function equality below does not evaluate
   source phases while checking a definitionally equal pattern match. -/
private def prepareWithGates
    (authorizationStage : Input → Result × Bool)
    (environmentStage : Input → Result → Bool → Result × Bool)
    (semantic context contextBranches authorizationDomain preparationBranches reservoir deadChargeGate fixedWidthGate factsGate : Bool) (input : Input) : Result :=
  if !semantic || !context || !contextBranches || !authorizationDomain || !preparationBranches || !reservoir || !fixedWidthGate || !factsGate then
    emptyResult input .admissionMismatch (some "semantic, representation, or source-fact coherence mismatch") input.handoff.gas input.preExecutionIntrinsicGasStandard input.world input.access input.handoff.environment input.delegationRefunds [] none
  else
    let canonical := { input with environment := input.handoff.environment, delegationRefunds := input.handoff.delegationRefunds }
    let authorization := authorizationStage canonical
    let environmentResult := environmentStage canonical authorization.1 authorization.2
    let environmentPreparation := environmentResult.1
    let environmentOog := environmentResult.2
    let withReservoir := { environmentPreparation with postIntrinsicStateReservoir := environmentPreparation.gas.stateReservoir }
    let deadApplicable := !authorization.2 && !environmentOog && deadRecipient canonical withReservoir.world
    let deadCharge := if deadApplicable && deadChargeGate then chargeState withReservoir.gas canonical.relations.deadRecipient.stateCharge else some withReservoir.gas
    chargeDelegatedTarget.match_1 (fun _ => Result) deadCharge
      (fun _ => prepareCall canonical withReservoir withReservoir.environment true)
      (fun gas =>
      let deadCharged := if deadApplicable then { withReservoir with gas := gas, events := withReservoir.events ++ [Event.stateCharge "dead-recipient" canonical.relations.deadRecipient.stateCharge] } else withReservoir
      prepareCall canonical deadCharged deadCharged.environment (authorization.2 || environmentOog))


private theorem prepare_eq_gateDefinition : prepare = fun input =>
    prepareWithGates processAuthorization buildEnvironment semanticAdmission admittedContextOperations admittedContextBranches
      admittedAuthorizationCreateExclusion admittedPreparationBranches admittedReservoirOperations admittedDeadChargeOperation
      (fixedWidthValidForInput input) (inputFactsCoherent input) input := by
  rfl

end EvmTransactionPreparationExtractor.Generated

namespace EvmTransactionPreparationExtractor.Refinement

private theorem map_authorizationFoldOption (enabled : Bool)
    (gas baseline : Generated.GasState) (delta : Int) :
    (if enabled then
      if EvmTransactionPreparationExtractor.Generated.fitsInt64 delta then
        EvmTransactionPreparationExtractor.Generated.foldAuthorizationStateGas gas baseline delta
      else none
    else some (gas, baseline)).map (fun pair => (mapGas pair.1, mapGas pair.2)) =
    (if enabled then
      if EvmTransactionPreparationExtractor.Reference.fitsInt64 delta then
        EvmTransactionPreparationExtractor.Reference.foldAuthorizationStateGas (mapGas gas) (mapGas baseline) delta
      else none
    else some (mapGas gas, mapGas baseline)) := by
  cases enabled <;> cases valid : EvmTransactionPreparationExtractor.Generated.fitsInt64 delta <;>
    simp +instances [show EvmTransactionPreparationExtractor.Reference.fitsInt64 delta =
      EvmTransactionPreparationExtractor.Generated.fitsInt64 delta from rfl,
      valid, map_foldAuthorizationStateGas]

private theorem map_processAuthorization
    (input : Generated.Input) :
    (mapResult ((EvmTransactionPreparationExtractor.Generated.processAuthorization input).1),
      (EvmTransactionPreparationExtractor.Generated.processAuthorization input).2) =
      ((EvmTransactionPreparationExtractor.Reference.processAuthorization (mapInput input)).1,
        (EvmTransactionPreparationExtractor.Reference.processAuthorization (mapInput input)).2) := by
  rcases admittedGateClosure with
    ⟨hAuthorization, _hAuthorizationDomain, _hContext, _hWarm, _hEnvironment, _hTarget,
      _hPrecompile, _hDelegatedCharge, _hDelegatedRead, _hReservoir, _hDead, _hDeadCharge,
      _hCollision, _hCollisionClassification, _hStorageReset, _hPayValue, _hNullCode, _hCall,
      _hContextBranches, hAuthorizationBranches, _hEnvironmentBranches, _hPreparationBranches,
      _hCallBranches⟩
  have hHas : EvmTransactionPreparationExtractor.Reference.hasAuthorization (mapInput input).handoff.tx
      (mapInput input).authorizations =
      EvmTransactionPreparationExtractor.Generated.hasAuthorization input.handoff.tx input.authorizations :=
    (map_hasAuthorization input.handoff.tx input.authorizations).symm
  simp +instances only [EvmTransactionPreparationExtractor.Generated.processAuthorization,
    EvmTransactionPreparationExtractor.Reference.processAuthorization,
    hAuthorization, hAuthorizationBranches, Bool.not_true, Bool.false_or, Bool.false_eq_true, ite_false,
    hHas]
  have hList :
      mapAuthorizationState (EvmTransactionPreparationExtractor.Generated.processAuthorizationList input
        (EvmTransactionPreparationExtractor.Generated.emptyAuthorizationState input) input.authorizations) =
      EvmTransactionPreparationExtractor.Reference.processAuthorizationList (mapInput input)
        (EvmTransactionPreparationExtractor.Reference.emptyAuthorizationState (mapInput input))
        (mapInput input).authorizations := by
    rw [← map_emptyAuthorizationState]
    exact map_processAuthorizationList input
      (EvmTransactionPreparationExtractor.Generated.emptyAuthorizationState input) input.authorizations
  simp +instances only [← hList]
  simp +instances only [← map_emptyAuthorizationState input]
  dsimp +instances only [mapInput, mapHandoff, mapAuthorizationState]
  by_cases enabled : (!input.handoff.spec.eip7702 ||
      !EvmTransactionPreparationExtractor.Generated.hasAuthorization input.handoff.tx input.authorizations) = true
  ·
      simp +instances only [enabled, eq_self, ite_true]
      rfl
  ·
      simp +instances only [enabled, Bool.false_eq_true, ite_false]
      let processed := EvmTransactionPreparationExtractor.Generated.processAuthorizationList input
        (EvmTransactionPreparationExtractor.Generated.emptyAuthorizationState input) input.authorizations
      by_cases failed : processed.failed = true
      ·
          simp +instances only [show (EvmTransactionPreparationExtractor.Generated.processAuthorizationList input
            (EvmTransactionPreparationExtractor.Generated.emptyAuthorizationState input) input.authorizations).failed = true from failed,
            eq_self, ite_true]
          rfl
      ·
          simp +instances only [show ¬(EvmTransactionPreparationExtractor.Generated.processAuthorizationList input
            (EvmTransactionPreparationExtractor.Generated.emptyAuthorizationState input) input.authorizations).failed = true from failed,
            Bool.false_eq_true, ite_false]
          let delta := processed.gas.stateGasUsed - input.handoff.gas.stateGasUsed
          have hFold := map_authorizationFoldOption input.handoff.spec.eip8037 processed.gas processed.baseline delta
          dsimp +instances only [processed, delta, mapGas] at hFold
          dsimp +instances only [mapGas]
          simp +instances only [← hFold]
          cases folded : (if input.handoff.spec.eip8037 then
                if EvmTransactionPreparationExtractor.Generated.fitsInt64 delta then
                  EvmTransactionPreparationExtractor.Generated.foldAuthorizationStateGas processed.gas processed.baseline delta
                else none else some (processed.gas, processed.baseline)) with
          | none => rfl
          | some pair =>
              cases pair
              rfl

private theorem map_buildEnvironment
    (input : Generated.Input) (preparation : Generated.Result) (topFrameOutOfGas : Bool)
    :
    (mapResult ((EvmTransactionPreparationExtractor.Generated.buildEnvironment input preparation topFrameOutOfGas).1),
      (EvmTransactionPreparationExtractor.Generated.buildEnvironment input preparation topFrameOutOfGas).2) =
      ((EvmTransactionPreparationExtractor.Reference.buildEnvironment (mapInput input) (mapResult preparation)
        topFrameOutOfGas).1,
        (EvmTransactionPreparationExtractor.Reference.buildEnvironment (mapInput input) (mapResult preparation)
          topFrameOutOfGas).2) := by
  rcases admittedGateClosure with
    ⟨_hAuthorization, _hAuthorizationDomain, _hContext, hWarm, hEnvironment, hTarget,
      hPrecompile, hDelegatedCharge, hDelegatedRead, _hReservoir, _hDead, _hDeadCharge,
      _hCollision, _hCollisionClassification, _hStorageReset, _hPayValue, _hNullCode, _hCall,
      _hContextBranches, _hAuthorizationBranches, hEnvironmentBranches, _hPreparationBranches,
      _hCallBranches⟩
  have hLegacy := admittedLegacyDelegatedWarm
  let recipient := EvmTransactionPreparationExtractor.Generated.deriveRecipient input preparation.world
  let access0 := EvmTransactionPreparationExtractor.Generated.warmTransactionAccesses input preparation.access recipient !topFrameOutOfGas
  let messageRecipient := !EvmTransactionPreparationExtractor.Generated.isCreateTx input && !topFrameOutOfGas
  let access1 := if messageRecipient then
      match recipient with
      | some value => EvmTransactionPreparationExtractor.Generated.recordAccessRead access0 value
      | none => access0
    else access0
  let world0 := if messageRecipient then
      match recipient with
      | some value => EvmTransactionPreparationExtractor.Generated.recordAccountRead preparation.world value
      | none => preparation.world
    else preparation.world
  let targetResolved := !EvmTransactionPreparationExtractor.Generated.isCreateTx input &&
    input.relations.delegatedTarget.target.isSome && !topFrameOutOfGas
  let targetPresent := targetResolved && input.handoff.spec.eip8037
  let targetWarmEnabled := targetResolved &&
    (!input.handoff.spec.eip8037 || input.handoff.spec.useHotAndColdStorage)
  let targetChargeEnabled := targetPresent && input.handoff.spec.useHotAndColdStorage
  let targetWasWarm := if targetWarmEnabled then
      match input.relations.delegatedTarget.target with
      | some value => EvmTransactionPreparationExtractor.Generated.hasValueIn value access1.warmedAccounts
      | none => false
    else false
  let targetAccess0 := if targetWarmEnabled then
      match input.relations.delegatedTarget.target with
      | some value => EvmTransactionPreparationExtractor.Generated.warmAccount access1 value
      | none => access1
    else access1
  have hRecipient := map_deriveRecipient input preparation.world
  have hWarmAccess := map_warmTransactionAccesses input preparation.access recipient (!topFrameOutOfGas)
  have hCreate : EvmTransactionPreparationExtractor.Reference.isCreateTx (mapInput input) =
      EvmTransactionPreparationExtractor.Generated.isCreateTx input := rfl
  have hAccess1 := map_accessReadForRecipient messageRecipient recipient access0
  have hWorld0 := map_accountReadForRecipient messageRecipient recipient preparation.world
  have hTargetAccess0 := map_warmForTarget targetWarmEnabled input.relations.delegatedTarget.target access1
  have hTargetWasWarm := map_targetWasWarm targetWarmEnabled input.relations.delegatedTarget.target access1
  have hCharge := map_chargeDelegatedTarget input targetChargeEnabled targetWasWarm
      input.relations.delegatedTarget.isPrecompile preparation.gas input.relations.delegatedTarget.target
      input.relations.delegatedTarget.accessCharge targetAccess0
  dsimp +instances only [recipient] at hWarmAccess
  dsimp +instances only [messageRecipient, recipient, access0] at hAccess1 hWorld0
  dsimp +instances only [targetChargeEnabled, targetPresent, targetResolved, targetWasWarm,
    targetWarmEnabled, targetAccess0, access1, messageRecipient, recipient, access0] at hCharge hTargetAccess0 hTargetWasWarm
  have hPreparation : mapResult preparation =
      { status := mapStatus preparation.status,
        error := preparation.error, gas := mapGas preparation.gas, executionIntrinsicGasStandard := mapGas preparation.executionIntrinsicGasStandard,
        prePreparationGas := mapGas preparation.prePreparationGas,
        preExecutionSnapshot := mapSnapshot preparation.preExecutionSnapshot, topLevelSnapshot := mapSnapshot preparation.topLevelSnapshot,
        postIntrinsicStateReservoir := preparation.postIntrinsicStateReservoir, world := mapWorld preparation.world,
        access := mapAccess preparation.access, environment := mapEnvironment preparation.environment, code := mapCode preparation.code,
        input := preparation.input, delegationRefunds := preparation.delegationRefunds,
        vmInput := preparation.vmInput.map mapVmInput, events := mapEvents preparation.events } := rfl
  simp +instances only [EvmTransactionPreparationExtractor.Generated.buildEnvironment,
    EvmTransactionPreparationExtractor.Reference.buildEnvironment, hEnvironment, hEnvironmentBranches,
    hWarm, hTarget, hLegacy, hDelegatedCharge, hDelegatedRead, hPrecompile,
    Bool.not_true, Bool.false_or, Bool.false_eq_true, eq_self, ite_false, ite_true,
    Bool.true_and, Bool.and_true, hPreparation]
  simp +instances only [←hRecipient]
  simp +instances only [←hWarmAccess]
  simp +instances only [hCreate]
  delta EvmTransactionPreparationExtractor.Reference.messageInputData.match_1
    EvmTransactionPreparationExtractor.Generated.messageInputData.match_1
    map_warmTransactionCore.match_1 at hAccess1 hWorld0 hTargetAccess0 hTargetWasWarm ⊢
  simp +instances only [←hAccess1, ←hWorld0]
  simp +instances only [←map_messageInputData]
  dsimp +instances only [mapInput, mapHandoff, mapRelations]
  delta map_buildEnvironment.match_1_1 at hTargetAccess0 hTargetWasWarm hCharge
  simp +instances only [←hTargetAccess0]
  simp +instances only [←hTargetWasWarm]
  simp +instances only [←hCharge]
  cases charged : EvmTransactionPreparationExtractor.Generated.chargeDelegatedTarget
      targetChargeEnabled targetWasWarm input.relations.delegatedTarget.isPrecompile preparation.gas
      input.relations.delegatedTarget.target input.relations.delegatedTarget.accessCharge targetAccess0 with
  | none =>
      dsimp +instances only [targetChargeEnabled, targetPresent, targetResolved, targetWasWarm,
        targetWarmEnabled, targetAccess0, access1, messageRecipient, recipient, access0] at charged
      delta map_buildEnvironment.match_1_1 at charged
      simp +instances only [charged, Option.map_none]
      simp +instances [mapResult, EvmTransactionPreparationExtractor.Generated.emptyResult,
        EvmTransactionPreparationExtractor.Reference.emptyResult, mapStatus, mapGas,
        mapEvents_append_list, mapEvents, mapEvent, mapEnvironment, mapCode,
        mapTx, mapAccess,
        
        EvmTransactionPreparationExtractor.Generated.messageInputData,
        
        EvmTransactionPreparationExtractor.Generated.isCreateTx]
      cases hRecipientValue : EvmTransactionPreparationExtractor.Generated.deriveRecipient input preparation.world <;>
        cases hTargetValue : input.relations.delegatedTarget.target <;>
        simp [apply_ite mapEvents, mapEvents, mapEvent]
  | some pair =>
      cases pair with
      | mk gas access =>
          dsimp +instances only [targetChargeEnabled, targetPresent, targetResolved, targetWasWarm,
            targetWarmEnabled, targetAccess0, access1, messageRecipient, recipient, access0] at charged
          delta map_buildEnvironment.match_1_1 at charged
          simp +instances only [charged, Option.map_some]
          simp +instances [mapResult, EvmTransactionPreparationExtractor.Generated.emptyResult,
            EvmTransactionPreparationExtractor.Reference.emptyResult, mapStatus, mapGas,
            mapEvents_append_list, mapEvents, mapEvent, mapEnvironment, mapCode,
            mapTx,
            EvmTransactionPreparationExtractor.Generated.creationCode,
            EvmTransactionPreparationExtractor.Reference.creationCode,
            
            
            EvmTransactionPreparationExtractor.Generated.messageInputData,
            
            EvmTransactionPreparationExtractor.Generated.isCreateTx]
          cases hRecipientValue : EvmTransactionPreparationExtractor.Generated.deriveRecipient input preparation.world <;>
            cases hTargetValue : input.relations.delegatedTarget.target <;>
            cases hTxRecipient : input.handoff.tx.recipient <;>
            cases hTop : topFrameOutOfGas <;>
            cases hEip : input.handoff.spec.eip8037 <;>
            cases hHot : input.handoff.spec.useHotAndColdStorage <;>
            cases hPrecompileValue : input.relations.delegatedTarget.isPrecompile <;>
            simp [
              mapEvents, mapEvent, map_recordAccountRead, mapAccess, 
              EvmTransactionPreparationExtractor.Generated.recordAccessRead,
              EvmTransactionPreparationExtractor.Reference.recordAccessRead]



private theorem map_prepareCall
    (input : Generated.Input) (preparation : Generated.Result) (environment : Generated.Environment)
    (topFrameOutOfGas : Bool) :
    mapResult (EvmTransactionPreparationExtractor.Generated.prepareCall input preparation environment topFrameOutOfGas) =
      EvmTransactionPreparationExtractor.Reference.prepareCall (mapInput input) (mapResult preparation)
        (mapEnvironment environment) topFrameOutOfGas := by
  rcases admittedGateClosure with
    ⟨_hAuthorization, _hAuthorizationDomain, _hContext, _hWarm, _hEnvironment, _hTarget,
      _hPrecompile, _hDelegatedCharge, _hDelegatedRead, _hReservoir, _hDead, _hDeadCharge,
      hCollision, hCollisionClassification, hStorageReset, _hPayValue, hNullCode, hCall,
      _hContextBranches, _hAuthorizationBranches, _hEnvironmentBranches, _hPreparationBranches,
      hCallBranches⟩

  have hPreparation : mapResult preparation =
      { status := mapStatus preparation.status,
        error := preparation.error, gas := mapGas preparation.gas, executionIntrinsicGasStandard := mapGas preparation.executionIntrinsicGasStandard,
        prePreparationGas := mapGas preparation.prePreparationGas,
        preExecutionSnapshot := mapSnapshot preparation.preExecutionSnapshot, topLevelSnapshot := mapSnapshot preparation.topLevelSnapshot,
        postIntrinsicStateReservoir := preparation.postIntrinsicStateReservoir, world := mapWorld preparation.world,
        access := mapAccess preparation.access, environment := mapEnvironment preparation.environment, code := mapCode preparation.code,
        input := preparation.input, delegationRefunds := preparation.delegationRefunds,
        vmInput := preparation.vmInput.map mapVmInput, events := mapEvents preparation.events } := rfl
  have hCreate : EvmTransactionPreparationExtractor.Reference.isCreateTx (mapInput input) =
      EvmTransactionPreparationExtractor.Generated.isCreateTx input := rfl
  have mapStorage (world : Generated.WorldFacts) (account : String) :
      mapWorld (EvmTransactionPreparationExtractor.Generated.recordStorageWrite world account) =
        EvmTransactionPreparationExtractor.Reference.recordStorageWrite (mapWorld world) account := rfl
  simp +instances only [EvmTransactionPreparationExtractor.Generated.prepareCall,
    EvmTransactionPreparationExtractor.Reference.prepareCall, hCall, hCallBranches,
    hStorageReset, hNullCode, hPreparation,
    Bool.not_true, Bool.false_or, Bool.false_eq_true, ite_false, 
    Bool.true_and, hCreate]
  cases topFrameOutOfGas with
  | true =>
      simpa only [Bool.true_eq, ite_true] using
        (map_preparationOog input preparation.gas preparation.executionIntrinsicGasStandard
          preparation.world preparation.access environment preparation.delegationRefunds preparation.events)
  | false =>
      simp +instances only [Bool.false_eq_true, ite_false, mapEnvironment]
      simp +instances only [←map_recordAccessRead, ←map_recordAccountRead]
      simp +instances only [←map_chargeState]
      simp +instances only [←map_deploymentCollision,
        show (mapInput input).handoff.spec.eip8037 = input.handoff.spec.eip8037 from rfl,
        show (mapInput input).relations.deployment.logicalExists = input.relations.deployment.logicalExists from rfl,
        show (mapInput input).relations.deployment.storageCleared = input.relations.deployment.storageCleared from rfl,
        show (mapInput input).relations.deployment.stateCharge = input.relations.deployment.stateCharge from rfl]
      cases hCreateValue : EvmTransactionPreparationExtractor.Generated.isCreateTx input <;>
        cases hEipValue : input.handoff.spec.eip8037 <;>
        cases hExistsValue : input.relations.deployment.logicalExists <;>
        cases hCharged : EvmTransactionPreparationExtractor.Generated.chargeState preparation.gas input.relations.deployment.stateCharge <;>
        simp +instances only [
          Bool.not_true, Bool.not_false, Bool.false_and, Bool.true_and, Bool.and_false, Bool.and_true,
          eq_self, Bool.false_eq_true, ite_true, ite_false, Option.map_none, Option.map_some,
          map_topLevelCreateOog, map_recordAccessRead]
      all_goals try simp +instances only [mapEnvironment]
      all_goals try simp +instances only [←map_recordAccessRead]
      all_goals try simp +instances only [←mapStorage]
      all_goals try simp +instances only [←apply_ite mapWorld]
      all_goals try simp +instances only [←map_payValue, ←map_valueDebitAmount]
      all_goals simp +instances only [EvmTransactionPreparationExtractor.Generated.deploymentCollision,
        hCollision, hCollisionClassification, Bool.true_and, hCreateValue, Bool.false_and]
      all_goals try simp +instances only [Bool.false_eq_true, ite_false]
      all_goals
        cases hCollisionValue : input.relations.deployment.collision <;>
          cases hNullValue : environment.code.isNull <;>
          cases hStorageValue : input.relations.deployment.storageCleared <;>
          cases hTracingValue : preparation.access.tracing
      all_goals try simp +instances [mapResult, mapStatus,
        EvmTransactionPreparationExtractor.Generated.emptyResult,
        EvmTransactionPreparationExtractor.Reference.emptyResult,
        mapInput, mapHandoff, mapTx, mapEnvironment, mapCode, mapVmInput, mapAccess,
        mapEvents_append_list, mapEvents, mapEvent,
        hEipValue, hNullValue, hTracingValue,
        apply_ite mapEvents,
        EvmTransactionPreparationExtractor.Generated.recordAccessRead]

private theorem map_authorizationChargesValid (auths : List Generated.AuthorizationFacts) :
    EvmTransactionPreparationExtractor.Generated.authorizationChargesValid auths =
      EvmTransactionPreparationExtractor.Reference.authorizationChargesValid (mapAuthorizations auths) := by
  induction auths with
  | nil => rfl
  | cons auth rest ih =>
      simp only [EvmTransactionPreparationExtractor.Generated.authorizationChargesValid,
        EvmTransactionPreparationExtractor.Reference.authorizationChargesValid, mapAuthorizations]
      rw [ih]
      rfl

private theorem map_fixedWidthValidForInput (input : Generated.Input) :
    EvmTransactionPreparationExtractor.Generated.fixedWidthValidForInput input =
      EvmTransactionPreparationExtractor.Reference.fixedWidthValidForInput (mapInput input) := by
  simp only [EvmTransactionPreparationExtractor.Generated.fixedWidthValidForInput,
    EvmTransactionPreparationExtractor.Reference.fixedWidthValidForInput, mapInput, mapHandoff]
  rw [map_authorizationChargesValid]
  rfl

private theorem map_resultWithReservoir (result : Generated.Result) :
    mapResult { result with postIntrinsicStateReservoir := result.gas.stateReservoir } =
      { (mapResult result) with postIntrinsicStateReservoir := (mapResult result).gas.stateReservoir } := rfl

private theorem map_deadChargedResult (result : Generated.Result) (gas : Generated.GasState) (amount : Int) :
    mapResult { result with gas := gas, events := result.events ++ [Generated.Event.stateCharge "dead-recipient" amount] } =
      { (mapResult result) with gas := mapGas gas, events := (mapResult result).events ++ [Reference.Event.stateCharge "dead-recipient" amount] } := by
  simp only [mapResult, mapEvents_append_list, mapEvents, mapEvent]


private def finishGenerated (input : Generated.Input) (preparation : Generated.Result)
    (authorizationOog environmentOog : Bool) : Generated.Result :=
  let applicable := !authorizationOog && !environmentOog &&
    EvmTransactionPreparationExtractor.Generated.deadRecipient input preparation.world
  let charge := if applicable then
      EvmTransactionPreparationExtractor.Generated.chargeState preparation.gas input.relations.deadRecipient.stateCharge
    else some preparation.gas
  EvmTransactionPreparationExtractor.Generated.chargeDelegatedTarget.match_1
    (fun _ => EvmTransactionPreparationExtractor.Generated.Result) charge
    (fun _ => EvmTransactionPreparationExtractor.Generated.prepareCall input preparation preparation.environment true)
    (fun gas =>
      let charged := if applicable then
          { preparation with gas := gas, events := preparation.events ++ [Generated.Event.stateCharge "dead-recipient" input.relations.deadRecipient.stateCharge] }
        else preparation
      EvmTransactionPreparationExtractor.Generated.prepareCall input charged charged.environment (authorizationOog || environmentOog))

private def finishReference (input : Reference.Input) (preparation : Reference.Result)
    (authorizationOog environmentOog : Bool) : Reference.Result :=
  let applicable := !authorizationOog && !environmentOog &&
    EvmTransactionPreparationExtractor.Reference.deadRecipient input preparation.world
  let charge := if applicable then
      EvmTransactionPreparationExtractor.Reference.chargeState preparation.gas input.relations.deadRecipient.stateCharge
    else some preparation.gas
  EvmTransactionPreparationExtractor.Reference.chargeDelegatedTarget.match_1
    (fun _ => EvmTransactionPreparationExtractor.Reference.Result) charge
    (fun _ => EvmTransactionPreparationExtractor.Reference.prepareCall input preparation preparation.environment true)
    (fun gas =>
      let charged := if applicable then
          { preparation with gas := gas, events := preparation.events ++ [Reference.Event.stateCharge "dead-recipient" input.relations.deadRecipient.stateCharge] }
        else preparation
      EvmTransactionPreparationExtractor.Reference.prepareCall input charged charged.environment (authorizationOog || environmentOog))

private theorem map_finish (input : Generated.Input) (preparation : Generated.Result)
    (authorizationOog environmentOog : Bool) :
    mapResult (finishGenerated input preparation authorizationOog environmentOog) =
      finishReference (mapInput input) (mapResult preparation) authorizationOog environmentOog := by
  simp +instances only [finishGenerated, finishReference,
    show (mapResult preparation).world = mapWorld preparation.world from rfl,
    show (mapResult preparation).gas = mapGas preparation.gas from rfl,
    show (mapInput input).relations.deadRecipient.stateCharge = input.relations.deadRecipient.stateCharge from rfl,
    ←map_deadRecipient, ←map_chargeState]
  cases hApplicable : (!authorizationOog && !environmentOog &&
      EvmTransactionPreparationExtractor.Generated.deadRecipient input preparation.world) <;>
    cases hCharged : EvmTransactionPreparationExtractor.Generated.chargeState preparation.gas input.relations.deadRecipient.stateCharge <;>
    simp +instances only [Bool.false_eq_true, eq_self, ite_false, ite_true,
      Option.map_none, Option.map_some]
  case false.none =>
    exact map_prepareCall input preparation preparation.environment (authorizationOog || environmentOog)
  case false.some gas =>
    exact map_prepareCall input preparation preparation.environment (authorizationOog || environmentOog)
  case true.none =>
    exact map_prepareCall input preparation preparation.environment true
  case true.some gas =>
    simpa +instances only [map_deadChargedResult,
      show (mapResult preparation).world = mapWorld preparation.world from rfl,
      show (mapResult preparation).environment = mapEnvironment preparation.environment from rfl] using
      (map_prepareCall input
        { preparation with gas := gas, events := preparation.events ++ [Generated.Event.stateCharge "dead-recipient" input.relations.deadRecipient.stateCharge] }
        preparation.environment (authorizationOog || environmentOog))

private theorem map_postEnvironment (input : Generated.Input) (result : Generated.Result)
    (authorizationOog environmentOog : Bool) :
    mapResult (finishGenerated input { result with postIntrinsicStateReservoir := result.gas.stateReservoir }
      authorizationOog environmentOog) =
      finishReference (mapInput input)
        { (mapResult result) with postIntrinsicStateReservoir := (mapResult result).gas.stateReservoir }
        authorizationOog environmentOog := by
  rw [map_finish, map_resultWithReservoir]


private def afterStagesGenerated
    (authorizationStage : Generated.Input → Generated.Result × Bool)
    (environmentStage : Generated.Input → Generated.Result → Bool → Generated.Result × Bool)
    (input : Generated.Input) : Generated.Result :=
      let canonical : Generated.Input := { input with environment := input.handoff.environment, delegationRefunds := input.handoff.delegationRefunds }
      let authorization := authorizationStage canonical
      let environmentResult := environmentStage canonical authorization.1 authorization.2
      finishGenerated canonical { environmentResult.1 with postIntrinsicStateReservoir := environmentResult.1.gas.stateReservoir } authorization.2 environmentResult.2

private theorem prepareWithGates_lowered
    (authorizationStage : Generated.Input → Generated.Result × Bool)
    (environmentStage : Generated.Input → Generated.Result → Bool → Generated.Result × Bool)
    (semantic context contextBranches authorizationDomain preparationBranches reservoir deadChargeGate fixedWidthGate factsGate : Bool)
    (input : Generated.Input)
    (hSemantic : semantic = true) (hContext : context = true) (hContextBranches : contextBranches = true)
    (hAuthorizationDomain : authorizationDomain = true) (hPreparationBranches : preparationBranches = true)
    (hReservoir : reservoir = true) (hDeadCharge : deadChargeGate = true)
    (hFixedWidth : fixedWidthGate = true) (hFacts : factsGate = true) :
    EvmTransactionPreparationExtractor.Generated.prepareWithGates authorizationStage environmentStage semantic context contextBranches
      authorizationDomain preparationBranches reservoir deadChargeGate fixedWidthGate factsGate input =
      afterStagesGenerated authorizationStage environmentStage input := by
  simp +instances only [hSemantic, hContext, hContextBranches, hAuthorizationDomain,
    hPreparationBranches, hReservoir, hDeadCharge, hFixedWidth, hFacts]
  dsimp +instances only [EvmTransactionPreparationExtractor.Generated.prepareWithGates]
  simp +instances only [Bool.not_true, Bool.false_or, Bool.false_eq_true, ite_false, Bool.and_true]
  dsimp +instances only [afterStagesGenerated, finishGenerated]

private theorem generated_prepare_lowered (input : Generated.Input)
    (representation : RepresentationObligation input) :
    EvmTransactionPreparationExtractor.Generated.prepare input =
      afterStagesGenerated EvmTransactionPreparationExtractor.Generated.processAuthorization
        EvmTransactionPreparationExtractor.Generated.buildEnvironment input := by
  have hSemantic := admittedSemanticClosure
  rcases admittedGateClosure with
    ⟨_hAuthorization, hAuthorizationDomain, hContext, _hWarm, _hEnvironment, _hTarget,
      _hPrecompile, _hDelegatedCharge, _hDelegatedRead, hReservoir, _hDead, hDeadCharge,
      _hCollision, _hCollisionClassification, _hStorageReset, _hPayValue, _hNullCode, _hCall,
      hContextBranches, _hAuthorizationBranches, _hEnvironmentBranches, hPreparationBranches,
      _hCallBranches⟩
  have hLower := prepareWithGates_lowered EvmTransactionPreparationExtractor.Generated.processAuthorization
    EvmTransactionPreparationExtractor.Generated.buildEnvironment
    EvmTransactionPreparationExtractor.Generated.semanticAdmission
    EvmTransactionPreparationExtractor.Generated.admittedContextOperations
    EvmTransactionPreparationExtractor.Generated.admittedContextBranches
    EvmTransactionPreparationExtractor.Generated.admittedAuthorizationCreateExclusion
    EvmTransactionPreparationExtractor.Generated.admittedPreparationBranches
    EvmTransactionPreparationExtractor.Generated.admittedReservoirOperations
    EvmTransactionPreparationExtractor.Generated.admittedDeadChargeOperation
    (EvmTransactionPreparationExtractor.Generated.fixedWidthValidForInput input)
    (EvmTransactionPreparationExtractor.Generated.inputFactsCoherent input)
    input hSemantic hContext hContextBranches hAuthorizationDomain hPreparationBranches hReservoir hDeadCharge
    representation.fixedWidthDomain representation.inputFacts
  exact (congrFun EvmTransactionPreparationExtractor.Generated.prepare_eq_gateDefinition input).trans hLower

private def afterStagesReference
    (authorizationStage : Reference.Input → Reference.Result × Bool)
    (environmentStage : Reference.Input → Reference.Result → Bool → Reference.Result × Bool)
    (input : Reference.Input) : Reference.Result :=
  let canonical : Reference.Input := { input with environment := input.handoff.environment, delegationRefunds := input.handoff.delegationRefunds }
  let authorization := authorizationStage canonical
  let environmentResult := environmentStage canonical authorization.1 authorization.2
  finishReference canonical { environmentResult.1 with postIntrinsicStateReservoir := environmentResult.1.gas.stateReservoir } authorization.2 environmentResult.2

private theorem reference_run_lowered (input : Reference.Input)
    (hFixedWidth : EvmTransactionPreparationExtractor.Reference.fixedWidthValidForInput input = true)
    (hFacts : EvmTransactionPreparationExtractor.Reference.inputFactsCoherent input = true) :
    EvmTransactionPreparationExtractor.Reference.run input =
      afterStagesReference EvmTransactionPreparationExtractor.Reference.processAuthorization
        EvmTransactionPreparationExtractor.Reference.buildEnvironment input := by
  delta EvmTransactionPreparationExtractor.Reference.run
  simp +instances only [hFixedWidth, hFacts, Bool.not_true, Bool.false_or, Bool.false_eq_true, ite_false]
  dsimp +instances only [afterStagesReference, finishReference]

private theorem map_afterStages
    (authorizationGenerated : Generated.Input → Generated.Result × Bool)
    (environmentGenerated : Generated.Input → Generated.Result → Bool → Generated.Result × Bool)
    (authorizationReference : Reference.Input → Reference.Result × Bool)
    (environmentReference : Reference.Input → Reference.Result → Bool → Reference.Result × Bool)
    (hAuthorization : ∀ input, authorizationReference (mapInput input) =
      (mapResult (authorizationGenerated input).1, (authorizationGenerated input).2))
    (hEnvironment : ∀ input preparation oog, environmentReference (mapInput input) (mapResult preparation) oog =
      (mapResult (environmentGenerated input preparation oog).1, (environmentGenerated input preparation oog).2))
    (input : Generated.Input) :
    mapResult (afterStagesGenerated authorizationGenerated environmentGenerated input) =
      afterStagesReference authorizationReference environmentReference (mapInput input) := by
  let canonical : Generated.Input := { input with environment := input.handoff.environment, delegationRefunds := input.handoff.delegationRefunds }
  have hCanonical : mapInput canonical =
      { (mapInput input) with environment := (mapInput input).handoff.environment, delegationRefunds := (mapInput input).handoff.delegationRefunds } := rfl
  dsimp +instances only [afterStagesGenerated, afterStagesReference]
  simp +instances only [←hCanonical]
  simp +instances only [hAuthorization]
  simp +instances only [hEnvironment]
  have hFinish := map_postEnvironment canonical
    (environmentGenerated canonical (authorizationGenerated canonical).1 (authorizationGenerated canonical).2).1
    (authorizationGenerated canonical).2
    (environmentGenerated canonical (authorizationGenerated canonical).1 (authorizationGenerated canonical).2).2
  simpa +instances only [canonical] using hFinish

private theorem map_run (input : Generated.Input) (representation : RepresentationObligation input) :
    mapResult (EvmTransactionPreparationExtractor.Generated.prepare input) =
      EvmTransactionPreparationExtractor.Reference.run (mapInput input) := by
  have hSource := congrArg mapResult (generated_prepare_lowered input representation)
  have hStages := map_afterStages EvmTransactionPreparationExtractor.Generated.processAuthorization
    EvmTransactionPreparationExtractor.Generated.buildEnvironment
    EvmTransactionPreparationExtractor.Reference.processAuthorization
    EvmTransactionPreparationExtractor.Reference.buildEnvironment
    (fun value => (map_processAuthorization value).symm)
    (fun value preparation oog => (map_buildEnvironment value preparation oog).symm) input
  have hReference := reference_run_lowered (mapInput input)
    ((map_fixedWidthValidForInput input).symm.trans representation.fixedWidthDomain)
    ((map_inputFactsCoherent input).symm.trans representation.inputFacts)
  exact hSource.trans (hStages.trans hReference.symm)

def findComposition : List EvmTransactionPreparationExtractor.Generated.CompositionEvidence →
    String → Option EvmTransactionPreparationExtractor.Generated.CompositionEvidence
  | [], _ => none
  | evidence :: rest, id => if evidence.id == id then some evidence else findComposition rest id

def exactCompositionIdentity (id kind path hash theoremName theoremShape : String) (fields : List String) : Bool :=
  match findComposition EvmTransactionPreparationExtractor.Generated.admittedCompositions id with
  | none => false
  | some evidence => evidence.kind == kind && evidence.artifactPath == path &&
      evidence.artifactSha256 == hash && evidence.theoremName == theoremName &&
      evidence.theoremShape == theoremShape &&
      evidence.requiredFields == fields

def acceptedCompositionIdentities : Bool :=
  exactCompositionIdentity "ordinary_post_nonce_ir" "generated"
    "tools/Evm/Lean/OrdinaryPostNonceDispatchExtractor/Generated/OrdinaryPostNonceDispatch.ir.json"
    ordinaryPostNonceArtifactSha256
    "OrdinaryPostNonceDispatchExtractor.Refinement.generated_postNonceDispatch_refines_reference"
    "generated-artifact-identity-required"
    ["EvmHandoff"] &&
  exactCompositionIdentity "ordinary_post_nonce_manifest" "generated"
    "tools/Evm/Lean/OrdinaryPostNonceDispatchExtractor/Generated/OrdinaryPostNonceDispatch.source-manifest.json"
    ordinaryPostNonceManifestSha256
    "OrdinaryPostNonceDispatchExtractor.Refinement.erases_to_lifecycleControl"
    "generated-artifact-identity-required"
    ["EvmHandoff"] &&
  exactCompositionIdentity "ordinary_post_nonce_lean" "generated"
    "tools/Evm/Lean/OrdinaryPostNonceDispatchExtractor/Generated/OrdinaryPostNonceDispatch.lean"
    ordinaryPostNonceLeanSha256
    "OrdinaryPostNonceDispatchExtractor.Refinement.generated_postNonceDispatch_refines_reference"
    "generated-artifact-identity-required"
    ["EvmHandoff"] &&
  exactCompositionIdentity "ordinary_post_nonce_reference" "reference"
    "tools/Evm/Lean/OrdinaryPostNonceDispatchExtractor/Reference/OrdinaryPostNonceDispatchReference.lean"
    ordinaryPostNonceReferenceSha256
    "OrdinaryPostNonceDispatchExtractor.Refinement.generated_postNonceDispatch_refines_reference"
    "generated-artifact-identity-required"
    ["EvmHandoff"] &&
  exactCompositionIdentity "ordinary_post_nonce_refinement" "proof"
    "tools/Evm/Lean/OrdinaryPostNonceDispatchExtractor/Refinement/OrdinaryPostNonceDispatch.lean"
    ordinaryPostNonceRefinementSha256
    "OrdinaryPostNonceDispatchExtractor.Refinement.generated_postNonceDispatch_refines_reference"
    "typed-theorem-identity-required"
    ["EvmHandoff"] &&
  exactCompositionIdentity "authorization_fold_ir" "generated"
    "tools/Evm/Lean/AuthorizationStateGasFoldExtractor/Generated/AuthorizationStateGasFold.ir.json"
    "d1d86731c09eabde1385e6a2f811ed881a4071940946ab4aa8e762f2ff443b3c"
    "Eip803x.Refinement.AuthorizationStateGasFold.generated_process_refines_spec"
    "generated-artifact-identity-required"
    ["source-bound-adapter"] &&
  exactCompositionIdentity "authorization_fold" "generated"
    "tools/Evm/Lean/AuthorizationStateGasFoldExtractor/Generated/AuthorizationStateGasFold.lean"
    "562a288d1ce8538310287867dd1346e74431693c1d5b4bef7e83103a22a790be"
    "Eip803x.Refinement.AuthorizationStateGasFold.generated_process_refines_spec"
    "generated-artifact-identity-required"
    ["gas.Value", "gas.StateReservoir", "gas.StateGasUsed", "gas.StateGasSpill", "gas.StateGasSpillRefunded"] &&
  exactCompositionIdentity "authorization_fold_refinement" "proof"
    "tools/Evm/Lean/AuthorizationStateGasFoldExtractor/Refinement/AuthorizationStateGasFold.lean"
    authorizationFoldRefinementSha256
    "Eip803x.Refinement.AuthorizationStateGasFold.generated_process_refines_spec"
    "typed-theorem-identity-required"
    ["gas.Value", "gas.StateReservoir", "gas.StateGasUsed", "gas.StateGasSpill", "gas.StateGasSpillRefunded"]

/- The ordinary handoff has a smaller, independently generated representation
   than the EVM preparation input.  These maps make the boundary concrete:
   transaction identity, addresses, and opaque prefix values are supplied by
   an explicit representation, while the route-bearing fields are projected
   from this input. -/
structure OrdinaryInputOpaque where
  transactionId : Nat
  headerId : Nat
  specId : Nat
  eip658Enabled : Bool
  tracerId : Nat
  optionsRaw : Nat
  gasLimit : Nat
  isCodeOverridable : Bool
  forceSimpleTransferDisabled : Bool
  opcodePrice : Nat
  premium : Nat
  reserved : Nat
  blobBaseFee : Nat
  deleteCallerAccount : Bool
  preload : OrdinaryPostNonceDispatchExtractor.Generated.Preload

def mapOrdinaryGas (gas : Generated.GasState) :
    OrdinaryPostNonceDispatchExtractor.Generated.GasPolicy :=
  { value := gas.value.toNat, stateReservoir := gas.stateReservoir,
    stateGasUsed := gas.stateGasUsed, stateGasSpill := gas.stateGasSpill,
    stateGasSpillRefunded := gas.stateGasSpillRefunded }

def makeOrdinaryInput (input : Generated.Input) (address : String → Nat)
    (opaqueFacts : OrdinaryInputOpaque) :
    OrdinaryPostNonceDispatchExtractor.Generated.Input :=
  let recipient := input.handoff.tx.recipient.map address
  let authorizationListPresent :=
    EvmTransactionPreparationExtractor.Generated.hasAuthorization input.handoff.tx input.authorizations
  { tx := { id := opaqueFacts.transactionId, to := recipient, authorizationListPresent := authorizationListPresent },
    header := { id := opaqueFacts.headerId },
    spec := { id := opaqueFacts.specId, eip8037Enabled := input.handoff.spec.eip8037, eip658Enabled := opaqueFacts.eip658Enabled },
    tracer := { id := opaqueFacts.tracerId, isTracingState := input.handoff.access.tracing },
    options := { raw := opaqueFacts.optionsRaw },
    intrinsic := { standard := input.handoff.executionIntrinsicGasStandard.value.toNat },
    gasLimit := opaqueFacts.gasLimit, isCodeOverridable := opaqueFacts.isCodeOverridable,
    forceSimpleTransferDisabled := opaqueFacts.forceSimpleTransferDisabled,
    lookup := { escaped := false, preload := opaqueFacts.preload },
    commitResponse := { escaped := false },
    availableGas := { accepted := true, gas := mapOrdinaryGas input.handoff.gas },
    opcodePrice := opaqueFacts.opcodePrice, premium := opaqueFacts.premium,
    reserved := opaqueFacts.reserved, blobBaseFee := opaqueFacts.blobBaseFee,
    deleteCallerAccount := opaqueFacts.deleteCallerAccount }

def makeOrdinaryHandoff (input : Generated.Input) (address : String → Nat)
    (opaqueFacts : OrdinaryInputOpaque) (restore commit : Bool) :
    OrdinaryPostNonceDispatchExtractor.Generated.Handoff :=
  let recipient := input.handoff.tx.recipient.map address
  let authorizationListPresent :=
    EvmTransactionPreparationExtractor.Generated.hasAuthorization input.handoff.tx input.authorizations
  { tx := { id := opaqueFacts.transactionId, to := recipient, authorizationListPresent := authorizationListPresent },
    header := { id := opaqueFacts.headerId },
    spec := { id := opaqueFacts.specId, eip8037Enabled := input.handoff.spec.eip8037, eip658Enabled := opaqueFacts.eip658Enabled },
    tracer := { id := opaqueFacts.tracerId, isTracingState := input.handoff.access.tracing },
    opts := { raw := opaqueFacts.optionsRaw }, restore := restore, commit := commit,
    deleteCallerAccount := opaqueFacts.deleteCallerAccount,
    intrinsic := { standard := input.handoff.executionIntrinsicGasStandard.value.toNat },
    gasAvailable := mapOrdinaryGas input.handoff.gas, opcodePrice := opaqueFacts.opcodePrice,
    premium := opaqueFacts.premium, reserved := opaqueFacts.reserved, blobBaseFee := opaqueFacts.blobBaseFee,
    preload := opaqueFacts.preload, recipient := none }

def mapFoldGas (gas : Generated.GasState) :
    Eip803x.Refinement.AuthorizationStateGasFold.Generated.GasPolicyState :=
  { value := gas.value.toNat, stateReservoir := gas.stateReservoir,
    stateGasUsed := gas.stateGasUsed, stateGasSpill := gas.stateGasSpill,
    stateGasSpillRefunded := gas.stateGasSpillRefunded }

def foldEffectMap (_input : Generated.Input) (state : Generated.AuthorizationState)
    (auth : Generated.AuthorizationFacts) :
    Eip803x.Refinement.AuthorizationStateGasFold.Generated.AuthorizationEffect :=
  match EvmTransactionPreparationExtractor.Generated.findAuthorityState state.world auth.authority with
  | none =>
      { valid := false, logicalAccountExists := false, delegatedBefore := false,
        clearsDelegation := auth.clearsDelegation, firstWrite := false,
        firstDelegation := false, newAccountStateCost := auth.newAccountStateCharge,
        perAuthorizationStateCost := auth.perAuthStateCharge,
        accountWriteExecutionCost := auth.accountWriteCharge }
  | some authorityState =>
      let codeValid := !EvmTransactionPreparationExtractor.Generated.authHasCode authorityState.current ||
        EvmTransactionPreparationExtractor.Generated.authHasDelegation authorityState.current
      let nonceValid := authorityState.current.nonce == auth.expectedNonce
      { valid := EvmTransactionPreparationExtractor.Generated.authPreliminaryValid auth &&
          EvmTransactionPreparationExtractor.Generated.authorizationCoherent state.world auth && codeValid && nonceValid,
        logicalAccountExists := authorityState.current.logicalExists,
        delegatedBefore := authorityState.delegatedBeforeTx.isSome,
        clearsDelegation := auth.clearsDelegation,
        firstWrite := !EvmTransactionPreparationExtractor.Generated.hasValueIn auth.authority state.writtenAuthorities,
        firstDelegation := !auth.clearsDelegation && authorityState.delegatedBeforeTx.isNone &&
          !EvmTransactionPreparationExtractor.Generated.hasValueIn auth.authority state.delegationSetFor,
        newAccountStateCost := auth.newAccountStateCharge,
        perAuthorizationStateCost := auth.perAuthStateCharge,
        accountWriteExecutionCost := auth.accountWriteCharge }

def foldEffectsMap (input : Generated.Input) (state : Generated.AuthorizationState) :
    List Generated.AuthorizationFacts →
      List Eip803x.Refinement.AuthorizationStateGasFold.Generated.AuthorizationEffect
  | [] => []
  | auth :: rest =>
      foldEffectMap input state auth ::
        foldEffectsMap input
          (EvmTransactionPreparationExtractor.Generated.processAuthorizationTuple input state auth) rest

def makeFoldInput (input : Generated.Input) :
    Eip803x.Refinement.AuthorizationStateGasFold.Generated.ProcessInput :=
  { eip8037 := input.handoff.spec.eip8037,
    available := mapFoldGas input.handoff.gas,
    baseline := mapFoldGas input.preExecutionIntrinsicGasStandard,
    authorizations := foldEffectsMap input
      (EvmTransactionPreparationExtractor.Generated.emptyAuthorizationState input)
      input.authorizations }

/- These are the only adapter parameters needed by the separate sibling
   identity theorem.  The route-bearing sibling inputs are not caller
   witnesses: `makeOrdinaryInput` and `makeFoldInput` construct them directly
   from this package's input.  The two parameters name the address projection
   and the opaque ordinary-dispatch prefix that this package does not model. -/
structure TypedAdapterObligation where
  ordinaryAddress : String → Nat
  ordinaryOpaque : OrdinaryInputOpaque

theorem generated_refines_reference
    (input : Generated.Input)
    (representation : RepresentationObligation input) :
    mapResult (EvmTransactionPreparationExtractor.Generated.prepare input) =
      EvmTransactionPreparationExtractor.Reference.run (mapInput input) := by
  exact map_run input representation

theorem acceptedCompositionIdentities_closed : acceptedCompositionIdentities = true := by
  decide +kernel

/- This theorem is identity-only.  It instantiates each imported sibling
   theorem at the concrete representation map constructed from this package's
   input and checks the pinned artifact closure.  It does not claim an equality
   between either sibling result and this package's preparation transition;
   that cross-package adapter remains an explicit future obligation. -/
theorem sibling_identity_at_concrete_maps
    (input : Generated.Input)
    (typedAdapters : TypedAdapterObligation) :
    acceptedCompositionIdentities = true ∧
      OrdinaryPostNonceDispatchExtractor.Refinement.mapResult
          (OrdinaryPostNonceDispatchExtractor.Generated.run
            (makeOrdinaryInput input typedAdapters.ordinaryAddress typedAdapters.ordinaryOpaque)) =
        OrdinaryPostNonceDispatchExtractor.Reference.run
          (OrdinaryPostNonceDispatchExtractor.Refinement.mapInput
            (makeOrdinaryInput input typedAdapters.ordinaryAddress typedAdapters.ordinaryOpaque)) ∧
      Eip803x.Refinement.AuthorizationStateGasFold.generatedOutcomeToSpec
          (Eip803x.Refinement.AuthorizationStateGasFold.Generated.processDelegations
            (makeFoldInput input)) =
        Eip803x.Refinement.AuthorizationStateGasFold.Spec.processDelegations
          (Eip803x.Refinement.AuthorizationStateGasFold.generatedInputToSpec
            (makeFoldInput input)) := by
  have hOrdinaryMapped :=
    OrdinaryPostNonceDispatchExtractor.Refinement.generated_postNonceDispatch_refines_reference
      (makeOrdinaryInput input typedAdapters.ordinaryAddress typedAdapters.ordinaryOpaque)
  have hAuthorizationMapped :=
    Eip803x.Refinement.AuthorizationStateGasFold.generated_process_refines_spec
      (makeFoldInput input)
  exact ⟨acceptedCompositionIdentities_closed, hOrdinaryMapped, hAuthorizationMapped⟩

end EvmTransactionPreparationExtractor.Refinement
