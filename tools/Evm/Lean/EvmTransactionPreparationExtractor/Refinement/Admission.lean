-- SPDX-License-Identifier: LGPL-3.0-only

import EvmTransactionPreparationExtractor.Generated.EvmTransactionPreparation

namespace EvmTransactionPreparationExtractor.Refinement

set_option maxRecDepth 100000

deriving instance ReflBEq for EvmTransactionPreparationExtractor.Generated.DependencyEvidence
deriving instance ReflBEq for EvmTransactionPreparationExtractor.Generated.StageEvidence
deriving instance ReflBEq for EvmTransactionPreparationExtractor.Generated.OperationEvidence
deriving instance ReflBEq for EvmTransactionPreparationExtractor.Generated.BranchEvidence
deriving instance ReflBEq for EvmTransactionPreparationExtractor.Generated.AdapterEvidence
deriving instance ReflBEq for EvmTransactionPreparationExtractor.Generated.CompositionEvidence
deriving instance ReflBEq for EvmTransactionPreparationExtractor.Generated.ControlFlowEvidence
deriving instance ReflBEq for EvmTransactionPreparationExtractor.Generated.BindingEvidence
deriving instance ReflBEq for EvmTransactionPreparationExtractor.Generated.GasFieldEvidence
deriving instance ReflBEq for EvmTransactionPreparationExtractor.Generated.SnapshotEvidence
deriving instance ReflBEq for EvmTransactionPreparationExtractor.Generated.EnvironmentEvidence
deriving instance ReflBEq for EvmTransactionPreparationExtractor.Generated.HandoffEvidence


private theorem admittedSourceBits0 :
    EvmTransactionPreparationExtractor.Generated.op_setTransactionContext = true ∧
    EvmTransactionPreparationExtractor.Generated.op_incrementCreateMetrics = true ∧
    EvmTransactionPreparationExtractor.Generated.op_allocateStackAccessTracker = true ∧
    EvmTransactionPreparationExtractor.Generated.op_capturePreparationGas = true ∧
    EvmTransactionPreparationExtractor.Generated.op_captureExecutionIntrinsicGasStandard = true ∧
    EvmTransactionPreparationExtractor.Generated.op_captureDelegationRefunds = true ∧
    EvmTransactionPreparationExtractor.Generated.op_initializePreparationSnapshot = true ∧
    EvmTransactionPreparationExtractor.Generated.op_capturePreExecutionSnapshot = true ∧
    EvmTransactionPreparationExtractor.Generated.op_processDelegations = true ∧
    EvmTransactionPreparationExtractor.Generated.op_authorizationListNonEmpty = true ∧
    EvmTransactionPreparationExtractor.Generated.op_authorizationCreateExclusion = true ∧
    EvmTransactionPreparationExtractor.Generated.op_chainIdGuard = true ∧
    EvmTransactionPreparationExtractor.Generated.op_authorizationNonceGuard = true ∧
    EvmTransactionPreparationExtractor.Generated.op_authorizationSignatureGuard = true ∧
    EvmTransactionPreparationExtractor.Generated.op_authorityCodeGuard = true ∧
    EvmTransactionPreparationExtractor.Generated.op_delegationCodeGuard = true ∧
    EvmTransactionPreparationExtractor.Generated.op_authorityNonceGuard = true ∧
    EvmTransactionPreparationExtractor.Generated.op_authorizationWarm = true ∧
    EvmTransactionPreparationExtractor.Generated.op_authorizationHasCode = true ∧
    EvmTransactionPreparationExtractor.Generated.op_authorizationDelegationRead = true ∧
    EvmTransactionPreparationExtractor.Generated.op_authorizationNonceRead = true ∧
    EvmTransactionPreparationExtractor.Generated.op_delegationStateUsedBefore = true ∧
    EvmTransactionPreparationExtractor.Generated.op_delegationAccountLogicalExistence = true ∧
    EvmTransactionPreparationExtractor.Generated.op_delegationAccountExistsEip8037 = true := by
  simp only [EvmTransactionPreparationExtractor.Generated.op_setTransactionContext,
    EvmTransactionPreparationExtractor.Generated.op_incrementCreateMetrics,
    EvmTransactionPreparationExtractor.Generated.op_allocateStackAccessTracker,
    EvmTransactionPreparationExtractor.Generated.op_capturePreparationGas,
    EvmTransactionPreparationExtractor.Generated.op_captureExecutionIntrinsicGasStandard,
    EvmTransactionPreparationExtractor.Generated.op_captureDelegationRefunds,
    EvmTransactionPreparationExtractor.Generated.op_initializePreparationSnapshot,
    EvmTransactionPreparationExtractor.Generated.op_capturePreExecutionSnapshot,
    EvmTransactionPreparationExtractor.Generated.op_processDelegations,
    EvmTransactionPreparationExtractor.Generated.op_authorizationListNonEmpty,
    EvmTransactionPreparationExtractor.Generated.op_authorizationCreateExclusion,
    EvmTransactionPreparationExtractor.Generated.op_chainIdGuard,
    EvmTransactionPreparationExtractor.Generated.op_authorizationNonceGuard,
    EvmTransactionPreparationExtractor.Generated.op_authorizationSignatureGuard,
    EvmTransactionPreparationExtractor.Generated.op_authorityCodeGuard,
    EvmTransactionPreparationExtractor.Generated.op_delegationCodeGuard,
    EvmTransactionPreparationExtractor.Generated.op_authorityNonceGuard,
    EvmTransactionPreparationExtractor.Generated.op_authorizationWarm,
    EvmTransactionPreparationExtractor.Generated.op_authorizationHasCode,
    EvmTransactionPreparationExtractor.Generated.op_authorizationDelegationRead,
    EvmTransactionPreparationExtractor.Generated.op_authorizationNonceRead,
    EvmTransactionPreparationExtractor.Generated.op_delegationStateUsedBefore,
    EvmTransactionPreparationExtractor.Generated.op_delegationAccountLogicalExistence,
    EvmTransactionPreparationExtractor.Generated.op_delegationAccountExistsEip8037]
  simp only [EvmTransactionPreparationExtractor.Generated.operationReady,
    
    
    
    
    EvmTransactionPreparationExtractor.Generated.admittedOperations]
  simp only [List.any_cons, List.any_nil, BEq.refl, Bool.true_or, Bool.or_true, Bool.or_false, eq_self, and_self]

private theorem admittedSourceBits24 :
    EvmTransactionPreparationExtractor.Generated.op_delegationAccountExistsLegacy = true ∧
    EvmTransactionPreparationExtractor.Generated.op_delegationNewAccountCharge = true ∧
    EvmTransactionPreparationExtractor.Generated.op_delegationAccountWriteSet = true ∧
    EvmTransactionPreparationExtractor.Generated.op_delegationAccountWriteCharge = true ∧
    EvmTransactionPreparationExtractor.Generated.op_delegationPerAuthSet = true ∧
    EvmTransactionPreparationExtractor.Generated.op_delegationPerAuthCharge = true ∧
    EvmTransactionPreparationExtractor.Generated.op_delegationCreateAccount = true ∧
    EvmTransactionPreparationExtractor.Generated.op_delegationIncrementNonce = true ∧
    EvmTransactionPreparationExtractor.Generated.op_delegationSetCode = true ∧
    EvmTransactionPreparationExtractor.Generated.op_delegationStateUsedAfter = true ∧
    EvmTransactionPreparationExtractor.Generated.op_delegationFold = true ∧
    EvmTransactionPreparationExtractor.Generated.op_delegationCodeInsertRefund = true ∧
    EvmTransactionPreparationExtractor.Generated.op_deriveRecipient = true ∧
    EvmTransactionPreparationExtractor.Generated.op_warmTransactionAccesses = true ∧
    EvmTransactionPreparationExtractor.Generated.op_resolveCreationCode = true ∧
    EvmTransactionPreparationExtractor.Generated.op_resolveCode = true ∧
    EvmTransactionPreparationExtractor.Generated.op_resolveDelegationAddress = true ∧
    EvmTransactionPreparationExtractor.Generated.op_resolveDelegationPrecompile = true ∧
    EvmTransactionPreparationExtractor.Generated.op_chargeDelegatedTarget = true ∧
    EvmTransactionPreparationExtractor.Generated.op_readDelegationTarget = true ∧
    EvmTransactionPreparationExtractor.Generated.op_warmLegacyDelegatedTarget = true ∧
    EvmTransactionPreparationExtractor.Generated.op_rentEnvironment = true ∧
    EvmTransactionPreparationExtractor.Generated.op_capturePostIntrinsicReservoir = true ∧
    EvmTransactionPreparationExtractor.Generated.op_chargeDeadRecipient = true := by
  simp only [EvmTransactionPreparationExtractor.Generated.op_delegationAccountExistsLegacy,
    EvmTransactionPreparationExtractor.Generated.op_delegationNewAccountCharge,
    EvmTransactionPreparationExtractor.Generated.op_delegationAccountWriteSet,
    EvmTransactionPreparationExtractor.Generated.op_delegationAccountWriteCharge,
    EvmTransactionPreparationExtractor.Generated.op_delegationPerAuthSet,
    EvmTransactionPreparationExtractor.Generated.op_delegationPerAuthCharge,
    EvmTransactionPreparationExtractor.Generated.op_delegationCreateAccount,
    EvmTransactionPreparationExtractor.Generated.op_delegationIncrementNonce,
    EvmTransactionPreparationExtractor.Generated.op_delegationSetCode,
    EvmTransactionPreparationExtractor.Generated.op_delegationStateUsedAfter,
    EvmTransactionPreparationExtractor.Generated.op_delegationFold,
    EvmTransactionPreparationExtractor.Generated.op_delegationCodeInsertRefund,
    EvmTransactionPreparationExtractor.Generated.op_deriveRecipient,
    EvmTransactionPreparationExtractor.Generated.op_warmTransactionAccesses,
    EvmTransactionPreparationExtractor.Generated.op_resolveCreationCode,
    EvmTransactionPreparationExtractor.Generated.op_resolveCode,
    EvmTransactionPreparationExtractor.Generated.op_resolveDelegationAddress,
    EvmTransactionPreparationExtractor.Generated.op_resolveDelegationPrecompile,
    EvmTransactionPreparationExtractor.Generated.op_chargeDelegatedTarget,
    EvmTransactionPreparationExtractor.Generated.op_readDelegationTarget,
    EvmTransactionPreparationExtractor.Generated.op_warmLegacyDelegatedTarget,
    EvmTransactionPreparationExtractor.Generated.op_rentEnvironment,
    EvmTransactionPreparationExtractor.Generated.op_capturePostIntrinsicReservoir,
    EvmTransactionPreparationExtractor.Generated.op_chargeDeadRecipient]
  simp only [EvmTransactionPreparationExtractor.Generated.operationReady,
    
    
    
    
    EvmTransactionPreparationExtractor.Generated.admittedOperations]
  simp only [List.any_cons, List.any_nil, BEq.refl, Bool.true_or, Bool.or_true, Bool.or_false, eq_self, and_self]

private theorem admittedSourceBits48 :
    EvmTransactionPreparationExtractor.Generated.op_chargeDeadRecipientState = true ∧
    EvmTransactionPreparationExtractor.Generated.op_restorePreparationSnapshot = true ∧
    EvmTransactionPreparationExtractor.Generated.op_restorePreparationGas = true ∧
    EvmTransactionPreparationExtractor.Generated.op_buildExecutionIntrinsic = true ∧
    EvmTransactionPreparationExtractor.Generated.op_createCollisionClassification = true ∧
    EvmTransactionPreparationExtractor.Generated.op_createStorageReset = true ∧
    EvmTransactionPreparationExtractor.Generated.op_captureTopLevelSnapshot = true ∧
    EvmTransactionPreparationExtractor.Generated.op_haltTopFrame = true ∧
    EvmTransactionPreparationExtractor.Generated.op_haltTopFrameCreate = true ∧
    EvmTransactionPreparationExtractor.Generated.op_prepareCreateDestination = true ∧
    EvmTransactionPreparationExtractor.Generated.op_createLogicalExistence = true ∧
    EvmTransactionPreparationExtractor.Generated.op_createCollisionResult = true ∧
    EvmTransactionPreparationExtractor.Generated.op_chargeCreateState = true ∧
    EvmTransactionPreparationExtractor.Generated.op_restoreCreateSnapshot = true ∧
    EvmTransactionPreparationExtractor.Generated.op_rejectCreateCollision = true ∧
    EvmTransactionPreparationExtractor.Generated.op_payValue = true ∧
    EvmTransactionPreparationExtractor.Generated.op_nullCodeShortCircuit = true ∧
    EvmTransactionPreparationExtractor.Generated.op_rentTopLevel = true ∧
    EvmTransactionPreparationExtractor.Generated.op_executeTransactionBoundary = true ∧
    EvmTransactionPreparationExtractor.Generated.branch_staticValidation_authorizationList = true ∧
    EvmTransactionPreparationExtractor.Generated.branch_authorization_chainIdGuard = true ∧
    EvmTransactionPreparationExtractor.Generated.branch_authorization_authorizationNonceGuard = true ∧
    EvmTransactionPreparationExtractor.Generated.branch_authorization_authorizationSignatureGuard = true ∧
    EvmTransactionPreparationExtractor.Generated.branch_authorization_authorityCodeGuard = true := by
  simp only [EvmTransactionPreparationExtractor.Generated.op_chargeDeadRecipientState,
    EvmTransactionPreparationExtractor.Generated.op_restorePreparationSnapshot,
    EvmTransactionPreparationExtractor.Generated.op_restorePreparationGas,
    EvmTransactionPreparationExtractor.Generated.op_buildExecutionIntrinsic,
    EvmTransactionPreparationExtractor.Generated.op_createCollisionClassification,
    EvmTransactionPreparationExtractor.Generated.op_createStorageReset,
    EvmTransactionPreparationExtractor.Generated.op_captureTopLevelSnapshot,
    EvmTransactionPreparationExtractor.Generated.op_haltTopFrame,
    EvmTransactionPreparationExtractor.Generated.op_haltTopFrameCreate,
    EvmTransactionPreparationExtractor.Generated.op_prepareCreateDestination,
    EvmTransactionPreparationExtractor.Generated.op_createLogicalExistence,
    EvmTransactionPreparationExtractor.Generated.op_createCollisionResult,
    EvmTransactionPreparationExtractor.Generated.op_chargeCreateState,
    EvmTransactionPreparationExtractor.Generated.op_restoreCreateSnapshot,
    EvmTransactionPreparationExtractor.Generated.op_rejectCreateCollision,
    EvmTransactionPreparationExtractor.Generated.op_payValue,
    EvmTransactionPreparationExtractor.Generated.op_nullCodeShortCircuit,
    EvmTransactionPreparationExtractor.Generated.op_rentTopLevel,
    EvmTransactionPreparationExtractor.Generated.op_executeTransactionBoundary,
    EvmTransactionPreparationExtractor.Generated.branch_staticValidation_authorizationList,
    EvmTransactionPreparationExtractor.Generated.branch_authorization_chainIdGuard,
    EvmTransactionPreparationExtractor.Generated.branch_authorization_authorizationNonceGuard,
    EvmTransactionPreparationExtractor.Generated.branch_authorization_authorizationSignatureGuard,
    EvmTransactionPreparationExtractor.Generated.branch_authorization_authorityCodeGuard]
  simp only [EvmTransactionPreparationExtractor.Generated.operationReady,
    EvmTransactionPreparationExtractor.Generated.branchReady,
    
    
    
    EvmTransactionPreparationExtractor.Generated.admittedOperations,
    EvmTransactionPreparationExtractor.Generated.admittedBranches]
  simp only [List.any_cons, List.any_nil, BEq.refl, Bool.true_or, Bool.or_true, Bool.or_false, eq_self, and_self]

private theorem admittedSourceBits72 :
    EvmTransactionPreparationExtractor.Generated.branch_authorization_delegationCodeGuard = true ∧
    EvmTransactionPreparationExtractor.Generated.branch_authorization_authorityNonceGuard = true ∧
    EvmTransactionPreparationExtractor.Generated.branch_authorization_authorizationListLoop = true ∧
    EvmTransactionPreparationExtractor.Generated.branch_authorization_eip8037StateCharges = true ∧
    EvmTransactionPreparationExtractor.Generated.branch_authorization_newAccountStateChargeGuard = true ∧
    EvmTransactionPreparationExtractor.Generated.branch_authorization_accountMaterializationGuard = true ∧
    EvmTransactionPreparationExtractor.Generated.branch_environment_recipientResolved = true ∧
    EvmTransactionPreparationExtractor.Generated.branch_environment_creationCode = true ∧
    EvmTransactionPreparationExtractor.Generated.branch_environment_skipRecipientLoad = true ∧
    EvmTransactionPreparationExtractor.Generated.branch_environment_preloadedCode = true ∧
    EvmTransactionPreparationExtractor.Generated.branch_environment_delegationCode = true ∧
    EvmTransactionPreparationExtractor.Generated.branch_environment_delegationTargetCharge = true ∧
    EvmTransactionPreparationExtractor.Generated.branch_environment_delegationTargetOog = true ∧
    EvmTransactionPreparationExtractor.Generated.branch_preparation_createMetrics = true ∧
    EvmTransactionPreparationExtractor.Generated.branch_preparation_authorizationGate = true ∧
    EvmTransactionPreparationExtractor.Generated.branch_preparation_authorizationSnapshot = true ∧
    EvmTransactionPreparationExtractor.Generated.branch_preparation_authorizationOog = true ∧
    EvmTransactionPreparationExtractor.Generated.branch_preparation_authorizationOogDeferral = true ∧
    EvmTransactionPreparationExtractor.Generated.branch_preparation_environmentResult = true ∧
    EvmTransactionPreparationExtractor.Generated.branch_preparation_deadRecipient = true ∧
    EvmTransactionPreparationExtractor.Generated.branch_preparation_preparationOogRollback = true ∧
    EvmTransactionPreparationExtractor.Generated.branch_preparation_preparationSnapshotRestore = true ∧
    EvmTransactionPreparationExtractor.Generated.branch_call_topFrameOog = true ∧
    EvmTransactionPreparationExtractor.Generated.branch_call_createPath = true := by
  simp only [EvmTransactionPreparationExtractor.Generated.branch_authorization_delegationCodeGuard,
    EvmTransactionPreparationExtractor.Generated.branch_authorization_authorityNonceGuard,
    EvmTransactionPreparationExtractor.Generated.branch_authorization_authorizationListLoop,
    EvmTransactionPreparationExtractor.Generated.branch_authorization_eip8037StateCharges,
    EvmTransactionPreparationExtractor.Generated.branch_authorization_newAccountStateChargeGuard,
    EvmTransactionPreparationExtractor.Generated.branch_authorization_accountMaterializationGuard,
    EvmTransactionPreparationExtractor.Generated.branch_environment_recipientResolved,
    EvmTransactionPreparationExtractor.Generated.branch_environment_creationCode,
    EvmTransactionPreparationExtractor.Generated.branch_environment_skipRecipientLoad,
    EvmTransactionPreparationExtractor.Generated.branch_environment_preloadedCode,
    EvmTransactionPreparationExtractor.Generated.branch_environment_delegationCode,
    EvmTransactionPreparationExtractor.Generated.branch_environment_delegationTargetCharge,
    EvmTransactionPreparationExtractor.Generated.branch_environment_delegationTargetOog,
    EvmTransactionPreparationExtractor.Generated.branch_preparation_createMetrics,
    EvmTransactionPreparationExtractor.Generated.branch_preparation_authorizationGate,
    EvmTransactionPreparationExtractor.Generated.branch_preparation_authorizationSnapshot,
    EvmTransactionPreparationExtractor.Generated.branch_preparation_authorizationOog,
    EvmTransactionPreparationExtractor.Generated.branch_preparation_authorizationOogDeferral,
    EvmTransactionPreparationExtractor.Generated.branch_preparation_environmentResult,
    EvmTransactionPreparationExtractor.Generated.branch_preparation_deadRecipient,
    EvmTransactionPreparationExtractor.Generated.branch_preparation_preparationOogRollback,
    EvmTransactionPreparationExtractor.Generated.branch_preparation_preparationSnapshotRestore,
    EvmTransactionPreparationExtractor.Generated.branch_call_topFrameOog,
    EvmTransactionPreparationExtractor.Generated.branch_call_createPath]
  simp only [
    EvmTransactionPreparationExtractor.Generated.branchReady,
    
    
    
    
    EvmTransactionPreparationExtractor.Generated.admittedBranches]
  simp only [List.any_cons, List.any_nil, BEq.refl, Bool.true_or, Bool.or_true, Bool.or_false, eq_self, and_self]

private theorem admittedSourceBits96 :
    EvmTransactionPreparationExtractor.Generated.branch_call_createStatePath = true ∧
    EvmTransactionPreparationExtractor.Generated.branch_call_createStateOog = true ∧
    EvmTransactionPreparationExtractor.Generated.branch_call_createCollision = true ∧
    EvmTransactionPreparationExtractor.Generated.branch_call_collisionTrace = true ∧
    EvmTransactionPreparationExtractor.Generated.branch_call_nullCode = true ∧
    EvmTransactionPreparationExtractor.Generated.branch_call_refundValidation = true ∧
    EvmTransactionPreparationExtractor.Generated.adapter_source_entry = true ∧
    EvmTransactionPreparationExtractor.Generated.adapter_authorization_state = true ∧
    EvmTransactionPreparationExtractor.Generated.adapter_state_gas = true ∧
    EvmTransactionPreparationExtractor.Generated.adapter_execution_gas = true ∧
    EvmTransactionPreparationExtractor.Generated.adapter_world_snapshot = true ∧
    EvmTransactionPreparationExtractor.Generated.adapter_vm_boundary = true ∧
    EvmTransactionPreparationExtractor.Generated.cfg_executeEvmTransaction = true ∧
    EvmTransactionPreparationExtractor.Generated.cfg_validateStatic = true ∧
    EvmTransactionPreparationExtractor.Generated.cfg_processDelegations = true ∧
    EvmTransactionPreparationExtractor.Generated.cfg_isValidForExecution = true ∧
    EvmTransactionPreparationExtractor.Generated.cfg_buildExecutionEnvironment = true ∧
    EvmTransactionPreparationExtractor.Generated.cfg_executeEvmCall = true ∧
    EvmTransactionPreparationExtractor.Generated.cfg_prepareDeployment = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_cfg_executeEvmTransaction_ba259fc6 = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_cfg_validateStatic_a35d284e = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_cfg_processDelegations_1ccb3a70 = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_cfg_isValidForExecution_2f38ec40 = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_cfg_buildExecutionEnvironment_25689d4e = true := by
  simp only [EvmTransactionPreparationExtractor.Generated.branch_call_createStatePath,
    EvmTransactionPreparationExtractor.Generated.branch_call_createStateOog,
    EvmTransactionPreparationExtractor.Generated.branch_call_createCollision,
    EvmTransactionPreparationExtractor.Generated.branch_call_collisionTrace,
    EvmTransactionPreparationExtractor.Generated.branch_call_nullCode,
    EvmTransactionPreparationExtractor.Generated.branch_call_refundValidation,
    EvmTransactionPreparationExtractor.Generated.adapter_source_entry,
    EvmTransactionPreparationExtractor.Generated.adapter_authorization_state,
    EvmTransactionPreparationExtractor.Generated.adapter_state_gas,
    EvmTransactionPreparationExtractor.Generated.adapter_execution_gas,
    EvmTransactionPreparationExtractor.Generated.adapter_world_snapshot,
    EvmTransactionPreparationExtractor.Generated.adapter_vm_boundary,
    EvmTransactionPreparationExtractor.Generated.cfg_executeEvmTransaction,
    EvmTransactionPreparationExtractor.Generated.cfg_validateStatic,
    EvmTransactionPreparationExtractor.Generated.cfg_processDelegations,
    EvmTransactionPreparationExtractor.Generated.cfg_isValidForExecution,
    EvmTransactionPreparationExtractor.Generated.cfg_buildExecutionEnvironment,
    EvmTransactionPreparationExtractor.Generated.cfg_executeEvmCall,
    EvmTransactionPreparationExtractor.Generated.cfg_prepareDeployment,
    EvmTransactionPreparationExtractor.Generated.binding_cfg_executeEvmTransaction_ba259fc6,
    EvmTransactionPreparationExtractor.Generated.binding_cfg_validateStatic_a35d284e,
    EvmTransactionPreparationExtractor.Generated.binding_cfg_processDelegations_1ccb3a70,
    EvmTransactionPreparationExtractor.Generated.binding_cfg_isValidForExecution_2f38ec40,
    EvmTransactionPreparationExtractor.Generated.binding_cfg_buildExecutionEnvironment_25689d4e]
  simp only [
    EvmTransactionPreparationExtractor.Generated.branchReady,
    EvmTransactionPreparationExtractor.Generated.adapterReady,
    EvmTransactionPreparationExtractor.Generated.controlFlowReady,
    EvmTransactionPreparationExtractor.Generated.bindingReady,
    
    EvmTransactionPreparationExtractor.Generated.admittedBranches,
    EvmTransactionPreparationExtractor.Generated.admittedAdapters,
    EvmTransactionPreparationExtractor.Generated.admittedControlFlows,
    EvmTransactionPreparationExtractor.Generated.admittedBindings]
  simp only [List.any_cons, List.any_nil, BEq.refl, Bool.true_or, Bool.or_true, Bool.or_false, eq_self, and_self]

private theorem admittedSourceBits120 :
    EvmTransactionPreparationExtractor.Generated.binding_cfg_executeEvmCall_1f724717 = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_cfg_prepareDeployment_bc6c6ada = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_vm_interface_executeTransaction_nonGeneric_1009f53f = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_vm_interface_executeTransaction_generic_7400694a = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_vm_interface_97ec7685 = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_fork_amsterdam_type_84e84267 = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_fork_amsterdam_apply_69f82fd2 = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_fork_amsterdam_IsEip8037Enabled_d6cc41a9 = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_fork_amsterdam_IsEip8038Enabled_a175cd62 = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_fork_mainnet_type_d16edc25 = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_transaction_hasAuthorizationListSetCode_98a9d672 = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_transaction_getRecipient_c6fed549 = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_di_ITransactionProcessor_EthereumTransactionProcessor_ecd2abd8 = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_di_ITransactionProcessorFactory_TransactionProcessorFactory_EthereumGasPolicy__c5032b7f = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_di_ICodeInfoRepository_CacheCodeInfoRepository_c20b139a = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_di_IWorldState_WorldState_69bb9627 = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_di_IVirtualMachine_EthereumVirtualMachine_0c6ed1e8 = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_setTransactionContext_50a47dd6 = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_incrementCreateMetrics_b6873ec8 = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_allocateStackAccessTracker_6d1d2eea = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_capturePreparationGas_c7d0ca70 = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_captureExecutionIntrinsicGasStandard_109e0b1e = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_captureDelegationRefunds_203d26cb = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_initializePreparationSnapshot_638878d2 = true := by
  simp only [EvmTransactionPreparationExtractor.Generated.binding_cfg_executeEvmCall_1f724717,
    EvmTransactionPreparationExtractor.Generated.binding_cfg_prepareDeployment_bc6c6ada,
    EvmTransactionPreparationExtractor.Generated.binding_vm_interface_executeTransaction_nonGeneric_1009f53f,
    EvmTransactionPreparationExtractor.Generated.binding_vm_interface_executeTransaction_generic_7400694a,
    EvmTransactionPreparationExtractor.Generated.binding_vm_interface_97ec7685,
    EvmTransactionPreparationExtractor.Generated.binding_fork_amsterdam_type_84e84267,
    EvmTransactionPreparationExtractor.Generated.binding_fork_amsterdam_apply_69f82fd2,
    EvmTransactionPreparationExtractor.Generated.binding_fork_amsterdam_IsEip8037Enabled_d6cc41a9,
    EvmTransactionPreparationExtractor.Generated.binding_fork_amsterdam_IsEip8038Enabled_a175cd62,
    EvmTransactionPreparationExtractor.Generated.binding_fork_mainnet_type_d16edc25,
    EvmTransactionPreparationExtractor.Generated.binding_transaction_hasAuthorizationListSetCode_98a9d672,
    EvmTransactionPreparationExtractor.Generated.binding_transaction_getRecipient_c6fed549,
    EvmTransactionPreparationExtractor.Generated.binding_di_ITransactionProcessor_EthereumTransactionProcessor_ecd2abd8,
    EvmTransactionPreparationExtractor.Generated.binding_di_ITransactionProcessorFactory_TransactionProcessorFactory_EthereumGasPolicy__c5032b7f,
    EvmTransactionPreparationExtractor.Generated.binding_di_ICodeInfoRepository_CacheCodeInfoRepository_c20b139a,
    EvmTransactionPreparationExtractor.Generated.binding_di_IWorldState_WorldState_69bb9627,
    EvmTransactionPreparationExtractor.Generated.binding_di_IVirtualMachine_EthereumVirtualMachine_0c6ed1e8,
    EvmTransactionPreparationExtractor.Generated.binding_setTransactionContext_50a47dd6,
    EvmTransactionPreparationExtractor.Generated.binding_incrementCreateMetrics_b6873ec8,
    EvmTransactionPreparationExtractor.Generated.binding_allocateStackAccessTracker_6d1d2eea,
    EvmTransactionPreparationExtractor.Generated.binding_capturePreparationGas_c7d0ca70,
    EvmTransactionPreparationExtractor.Generated.binding_captureExecutionIntrinsicGasStandard_109e0b1e,
    EvmTransactionPreparationExtractor.Generated.binding_captureDelegationRefunds_203d26cb,
    EvmTransactionPreparationExtractor.Generated.binding_initializePreparationSnapshot_638878d2]
  simp only [
    
    
    
    EvmTransactionPreparationExtractor.Generated.bindingReady,
    
    
    
    
    EvmTransactionPreparationExtractor.Generated.admittedBindings]
  simp only [List.any_cons, List.any_nil, BEq.refl, Bool.true_or, Bool.or_true, Bool.or_false, eq_self, and_self]

private theorem admittedSourceBits144 :
    EvmTransactionPreparationExtractor.Generated.binding_capturePreExecutionSnapshot_3bbc9b99 = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_processDelegations_4fac3ba0 = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_authorizationCreateExclusionGuard_358cce87 = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_authorizationCreateExclusionFailureGuard_e2b83797 = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_authorizationCreateExclusion_9e21f1cb = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_authorizationListNonEmpty_30a439ed = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_authorizationListNonEmptyFailureGuard_048ce443 = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_chainIdGuard_27ee91fe = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_authorizationNonceGuard_69af4122 = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_authorizationSignatureGuard_d99ab31c = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_authorityCodeGuard_50935788 = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_delegationCodeGuard_bb7fb2db = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_authorityNonceGuard_487b96e0 = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_authorizationWarm_caae0cb1 = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_authorizationHasCode_50935788 = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_authorizationDelegationRead_1dda9d5d = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_authorizationNonceRead_fe8abb60 = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_authorizationListLoop_7f7747b8 = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_authorization_eip8037StateCharges_888e7bef = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_authorization_newAccountStateChargeGuard_880bfa2c = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_authorization_accountMaterializationGuard_ca941253 = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_delegationStateUsedBefore_2e57ee69 = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_delegationAccountLogicalExistence_16371745 = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_delegationAccountExistsEip8037_665e16fd = true := by
  simp only [EvmTransactionPreparationExtractor.Generated.binding_capturePreExecutionSnapshot_3bbc9b99,
    EvmTransactionPreparationExtractor.Generated.binding_processDelegations_4fac3ba0,
    EvmTransactionPreparationExtractor.Generated.binding_authorizationCreateExclusionGuard_358cce87,
    EvmTransactionPreparationExtractor.Generated.binding_authorizationCreateExclusionFailureGuard_e2b83797,
    EvmTransactionPreparationExtractor.Generated.binding_authorizationCreateExclusion_9e21f1cb,
    EvmTransactionPreparationExtractor.Generated.binding_authorizationListNonEmpty_30a439ed,
    EvmTransactionPreparationExtractor.Generated.binding_authorizationListNonEmptyFailureGuard_048ce443,
    EvmTransactionPreparationExtractor.Generated.binding_chainIdGuard_27ee91fe,
    EvmTransactionPreparationExtractor.Generated.binding_authorizationNonceGuard_69af4122,
    EvmTransactionPreparationExtractor.Generated.binding_authorizationSignatureGuard_d99ab31c,
    EvmTransactionPreparationExtractor.Generated.binding_authorityCodeGuard_50935788,
    EvmTransactionPreparationExtractor.Generated.binding_delegationCodeGuard_bb7fb2db,
    EvmTransactionPreparationExtractor.Generated.binding_authorityNonceGuard_487b96e0,
    EvmTransactionPreparationExtractor.Generated.binding_authorizationWarm_caae0cb1,
    EvmTransactionPreparationExtractor.Generated.binding_authorizationHasCode_50935788,
    EvmTransactionPreparationExtractor.Generated.binding_authorizationDelegationRead_1dda9d5d,
    EvmTransactionPreparationExtractor.Generated.binding_authorizationNonceRead_fe8abb60,
    EvmTransactionPreparationExtractor.Generated.binding_authorizationListLoop_7f7747b8,
    EvmTransactionPreparationExtractor.Generated.binding_authorization_eip8037StateCharges_888e7bef,
    EvmTransactionPreparationExtractor.Generated.binding_authorization_newAccountStateChargeGuard_880bfa2c,
    EvmTransactionPreparationExtractor.Generated.binding_authorization_accountMaterializationGuard_ca941253,
    EvmTransactionPreparationExtractor.Generated.binding_delegationStateUsedBefore_2e57ee69,
    EvmTransactionPreparationExtractor.Generated.binding_delegationAccountLogicalExistence_16371745,
    EvmTransactionPreparationExtractor.Generated.binding_delegationAccountExistsEip8037_665e16fd]
  simp only [
    
    
    
    EvmTransactionPreparationExtractor.Generated.bindingReady,
    
    
    
    
    EvmTransactionPreparationExtractor.Generated.admittedBindings]
  simp only [List.any_cons, List.any_nil, BEq.refl, Bool.true_or, Bool.or_true, Bool.or_false, eq_self, and_self]

private theorem admittedSourceBits168 :
    EvmTransactionPreparationExtractor.Generated.binding_delegationAccountExistsLegacy_665e16fd = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_delegationNewAccountCharge_34ca21ba = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_delegationAccountWriteSet_5af414a1 = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_delegationAccountWriteCharge_31a5cfad = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_delegationPerAuthSet_ed360173 = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_delegationPerAuthCharge_00bc0c69 = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_delegationCreateAccount_c14ac903 = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_delegationIncrementNonce_0e342bde = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_delegationSetCode_7e8c17a7 = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_delegationStateUsedAfter_2e57ee69 = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_delegationFold_807a8327 = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_delegationCodeInsertRefund_47af813f = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_deriveRecipient_2dcbcd67 = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_warmTransactionAccesses_31e15946 = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_resolveCreationCode_f3955308 = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_resolveCode_90bc56c4 = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_resolveDelegationAddress_b35b31e0 = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_resolveDelegationPrecompile_a2e7992f = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_chargeDelegatedTarget_bf68b9b4 = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_readDelegationTarget_ebad9686 = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_warmLegacyDelegatedTarget_c49ae7a6 = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_rentEnvironment_d6943ec6 = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_recipientResolved_3a300e84 = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_creationCode_56361b3a = true := by
  simp only [EvmTransactionPreparationExtractor.Generated.binding_delegationAccountExistsLegacy_665e16fd,
    EvmTransactionPreparationExtractor.Generated.binding_delegationNewAccountCharge_34ca21ba,
    EvmTransactionPreparationExtractor.Generated.binding_delegationAccountWriteSet_5af414a1,
    EvmTransactionPreparationExtractor.Generated.binding_delegationAccountWriteCharge_31a5cfad,
    EvmTransactionPreparationExtractor.Generated.binding_delegationPerAuthSet_ed360173,
    EvmTransactionPreparationExtractor.Generated.binding_delegationPerAuthCharge_00bc0c69,
    EvmTransactionPreparationExtractor.Generated.binding_delegationCreateAccount_c14ac903,
    EvmTransactionPreparationExtractor.Generated.binding_delegationIncrementNonce_0e342bde,
    EvmTransactionPreparationExtractor.Generated.binding_delegationSetCode_7e8c17a7,
    EvmTransactionPreparationExtractor.Generated.binding_delegationStateUsedAfter_2e57ee69,
    EvmTransactionPreparationExtractor.Generated.binding_delegationFold_807a8327,
    EvmTransactionPreparationExtractor.Generated.binding_delegationCodeInsertRefund_47af813f,
    EvmTransactionPreparationExtractor.Generated.binding_deriveRecipient_2dcbcd67,
    EvmTransactionPreparationExtractor.Generated.binding_warmTransactionAccesses_31e15946,
    EvmTransactionPreparationExtractor.Generated.binding_resolveCreationCode_f3955308,
    EvmTransactionPreparationExtractor.Generated.binding_resolveCode_90bc56c4,
    EvmTransactionPreparationExtractor.Generated.binding_resolveDelegationAddress_b35b31e0,
    EvmTransactionPreparationExtractor.Generated.binding_resolveDelegationPrecompile_a2e7992f,
    EvmTransactionPreparationExtractor.Generated.binding_chargeDelegatedTarget_bf68b9b4,
    EvmTransactionPreparationExtractor.Generated.binding_readDelegationTarget_ebad9686,
    EvmTransactionPreparationExtractor.Generated.binding_warmLegacyDelegatedTarget_c49ae7a6,
    EvmTransactionPreparationExtractor.Generated.binding_rentEnvironment_d6943ec6,
    EvmTransactionPreparationExtractor.Generated.binding_recipientResolved_3a300e84,
    EvmTransactionPreparationExtractor.Generated.binding_creationCode_56361b3a]
  simp only [
    
    
    
    EvmTransactionPreparationExtractor.Generated.bindingReady,
    
    
    
    
    EvmTransactionPreparationExtractor.Generated.admittedBindings]
  simp only [List.any_cons, List.any_nil, BEq.refl, Bool.true_or, Bool.or_true, Bool.or_false, eq_self, and_self]

private theorem admittedSourceBits192 :
    EvmTransactionPreparationExtractor.Generated.binding_skipRecipientLoad_ea985055 = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_preloadedCode_a5df4288 = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_delegationCode_8ccca089 = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_delegationTargetCharge_888e7bef = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_delegationTargetOog_be3106b3 = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_capturePostIntrinsicReservoir_2df89330 = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_chargeDeadRecipient_4c6cfc77 = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_chargeDeadRecipientState_34ca21ba = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_restorePreparationSnapshot_b2e4b8fc = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_restorePreparationGas_89e950ad = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_buildExecutionIntrinsic_13313728 = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_createMetrics_56361b3a = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_authorizationGate_d3a7a56d = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_authorizationSnapshot_888e7bef = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_authorizationOog_ffd012ba = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_authorizationOogDeferral_888e7bef = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_environmentResult_11a4a662 = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_deadRecipient_b16abdaf = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_preparationOogRollback_0756aa50 = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_preparationSnapshotRestore_019ec5ab = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_createCollisionClassification_22e8fb08 = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_createStorageReset_9cfbede0 = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_captureTopLevelSnapshot_aa62cfb8 = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_haltTopFrame_56177468 = true := by
  simp only [EvmTransactionPreparationExtractor.Generated.binding_skipRecipientLoad_ea985055,
    EvmTransactionPreparationExtractor.Generated.binding_preloadedCode_a5df4288,
    EvmTransactionPreparationExtractor.Generated.binding_delegationCode_8ccca089,
    EvmTransactionPreparationExtractor.Generated.binding_delegationTargetCharge_888e7bef,
    EvmTransactionPreparationExtractor.Generated.binding_delegationTargetOog_be3106b3,
    EvmTransactionPreparationExtractor.Generated.binding_capturePostIntrinsicReservoir_2df89330,
    EvmTransactionPreparationExtractor.Generated.binding_chargeDeadRecipient_4c6cfc77,
    EvmTransactionPreparationExtractor.Generated.binding_chargeDeadRecipientState_34ca21ba,
    EvmTransactionPreparationExtractor.Generated.binding_restorePreparationSnapshot_b2e4b8fc,
    EvmTransactionPreparationExtractor.Generated.binding_restorePreparationGas_89e950ad,
    EvmTransactionPreparationExtractor.Generated.binding_buildExecutionIntrinsic_13313728,
    EvmTransactionPreparationExtractor.Generated.binding_createMetrics_56361b3a,
    EvmTransactionPreparationExtractor.Generated.binding_authorizationGate_d3a7a56d,
    EvmTransactionPreparationExtractor.Generated.binding_authorizationSnapshot_888e7bef,
    EvmTransactionPreparationExtractor.Generated.binding_authorizationOog_ffd012ba,
    EvmTransactionPreparationExtractor.Generated.binding_authorizationOogDeferral_888e7bef,
    EvmTransactionPreparationExtractor.Generated.binding_environmentResult_11a4a662,
    EvmTransactionPreparationExtractor.Generated.binding_deadRecipient_b16abdaf,
    EvmTransactionPreparationExtractor.Generated.binding_preparationOogRollback_0756aa50,
    EvmTransactionPreparationExtractor.Generated.binding_preparationSnapshotRestore_019ec5ab,
    EvmTransactionPreparationExtractor.Generated.binding_createCollisionClassification_22e8fb08,
    EvmTransactionPreparationExtractor.Generated.binding_createStorageReset_9cfbede0,
    EvmTransactionPreparationExtractor.Generated.binding_captureTopLevelSnapshot_aa62cfb8,
    EvmTransactionPreparationExtractor.Generated.binding_haltTopFrame_56177468]
  simp only [
    
    
    
    EvmTransactionPreparationExtractor.Generated.bindingReady,
    
    
    
    
    EvmTransactionPreparationExtractor.Generated.admittedBindings]
  simp only [List.any_cons, List.any_nil, BEq.refl, Bool.true_or, Bool.or_true, Bool.or_false, eq_self, and_self]

private theorem admittedSourceBits216 :
    EvmTransactionPreparationExtractor.Generated.binding_haltTopFrameCreate_590cf8f4 = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_prepareCreateDestination_b6dc60d4 = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_createLogicalExistence_b6dc60d4 = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_createCollisionResult_b6dc60d4 = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_chargeCreateState_47d1439a = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_restoreCreateSnapshot_dcf6684a = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_rejectCreateCollision_ec5d99e6 = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_payValue_c4c47c03 = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_nullCodeShortCircuit_96b75655 = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_rentTopLevel_bf7bb936 = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_executeTransactionBoundary_off_dc2f767c = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_executeTransactionBoundary_on_e3ec0aed = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_topFrameOog_be3106b3 = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_createPath_56361b3a = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_createStatePath_1413da1f = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_createStateOog_59de4daa = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_createCollision_ec5d99e6 = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_collisionTrace_c3d5706a = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_nullCode_96b75655 = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_refundValidation_a9be37bf = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_gasField_Value_8e37953d = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_gasField_StateReservoir_e1d22f60 = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_gasField_StateGasUsed_0bec4fea = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_gasField_StateGasSpill_d91f3125 = true := by
  simp only [EvmTransactionPreparationExtractor.Generated.binding_haltTopFrameCreate_590cf8f4,
    EvmTransactionPreparationExtractor.Generated.binding_prepareCreateDestination_b6dc60d4,
    EvmTransactionPreparationExtractor.Generated.binding_createLogicalExistence_b6dc60d4,
    EvmTransactionPreparationExtractor.Generated.binding_createCollisionResult_b6dc60d4,
    EvmTransactionPreparationExtractor.Generated.binding_chargeCreateState_47d1439a,
    EvmTransactionPreparationExtractor.Generated.binding_restoreCreateSnapshot_dcf6684a,
    EvmTransactionPreparationExtractor.Generated.binding_rejectCreateCollision_ec5d99e6,
    EvmTransactionPreparationExtractor.Generated.binding_payValue_c4c47c03,
    EvmTransactionPreparationExtractor.Generated.binding_nullCodeShortCircuit_96b75655,
    EvmTransactionPreparationExtractor.Generated.binding_rentTopLevel_bf7bb936,
    EvmTransactionPreparationExtractor.Generated.binding_executeTransactionBoundary_off_dc2f767c,
    EvmTransactionPreparationExtractor.Generated.binding_executeTransactionBoundary_on_e3ec0aed,
    EvmTransactionPreparationExtractor.Generated.binding_topFrameOog_be3106b3,
    EvmTransactionPreparationExtractor.Generated.binding_createPath_56361b3a,
    EvmTransactionPreparationExtractor.Generated.binding_createStatePath_1413da1f,
    EvmTransactionPreparationExtractor.Generated.binding_createStateOog_59de4daa,
    EvmTransactionPreparationExtractor.Generated.binding_createCollision_ec5d99e6,
    EvmTransactionPreparationExtractor.Generated.binding_collisionTrace_c3d5706a,
    EvmTransactionPreparationExtractor.Generated.binding_nullCode_96b75655,
    EvmTransactionPreparationExtractor.Generated.binding_refundValidation_a9be37bf,
    EvmTransactionPreparationExtractor.Generated.binding_gasField_Value_8e37953d,
    EvmTransactionPreparationExtractor.Generated.binding_gasField_StateReservoir_e1d22f60,
    EvmTransactionPreparationExtractor.Generated.binding_gasField_StateGasUsed_0bec4fea,
    EvmTransactionPreparationExtractor.Generated.binding_gasField_StateGasSpill_d91f3125]
  simp only [
    
    
    
    EvmTransactionPreparationExtractor.Generated.bindingReady,
    
    
    
    
    EvmTransactionPreparationExtractor.Generated.admittedBindings]
  simp only [List.any_cons, List.any_nil, BEq.refl, Bool.true_or, Bool.or_true, Bool.or_false, eq_self, and_self]

private theorem admittedSourceBits240 :
    EvmTransactionPreparationExtractor.Generated.binding_gasField_StateGasSpillRefunded_04eed9c8 = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_snapshot_preparation_3bbc9b99 = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_snapshot_topLevel_aa62cfb8 = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_snapshot_authorizationPresence_d3a7a56d = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_snapshot_preparationPresence_888e7bef = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_snapshot_emptyPosition_943962eb = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_environment_rent_d6943ec6 = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_handoff_rentTopLevel_bf7bb936 = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_handoff_executeTransaction_dc2f767c = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_handoff_executeTransaction_tracing_e3ec0aed = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_handoff_executeEvmCall_access_off_21b0c2b6 = true ∧
    EvmTransactionPreparationExtractor.Generated.binding_handoff_executeEvmCall_access_on_9871ea54 = true := by
  simp only [EvmTransactionPreparationExtractor.Generated.binding_gasField_StateGasSpillRefunded_04eed9c8,
    EvmTransactionPreparationExtractor.Generated.binding_snapshot_preparation_3bbc9b99,
    EvmTransactionPreparationExtractor.Generated.binding_snapshot_topLevel_aa62cfb8,
    EvmTransactionPreparationExtractor.Generated.binding_snapshot_authorizationPresence_d3a7a56d,
    EvmTransactionPreparationExtractor.Generated.binding_snapshot_preparationPresence_888e7bef,
    EvmTransactionPreparationExtractor.Generated.binding_snapshot_emptyPosition_943962eb,
    EvmTransactionPreparationExtractor.Generated.binding_environment_rent_d6943ec6,
    EvmTransactionPreparationExtractor.Generated.binding_handoff_rentTopLevel_bf7bb936,
    EvmTransactionPreparationExtractor.Generated.binding_handoff_executeTransaction_dc2f767c,
    EvmTransactionPreparationExtractor.Generated.binding_handoff_executeTransaction_tracing_e3ec0aed,
    EvmTransactionPreparationExtractor.Generated.binding_handoff_executeEvmCall_access_off_21b0c2b6,
    EvmTransactionPreparationExtractor.Generated.binding_handoff_executeEvmCall_access_on_9871ea54]
  simp only [
    
    
    
    EvmTransactionPreparationExtractor.Generated.bindingReady,
    
    
    
    
    EvmTransactionPreparationExtractor.Generated.admittedBindings]
  simp only [List.any_cons, List.any_nil, BEq.refl, Bool.true_or, Bool.or_true, Bool.or_false, eq_self, and_self]

private theorem admittedSemanticPlan : EvmTransactionPreparationExtractor.Generated.semanticPlanReady = true := by
  decide +kernel

private theorem sourceIdentitiesExact_closed : EvmTransactionPreparationExtractor.Generated.sourceIdentitiesExact = true := by
  exact BEq.refl EvmTransactionPreparationExtractor.Generated.sourceIdentities

private theorem compilerReferenceIdentitiesExact_closed : EvmTransactionPreparationExtractor.Generated.compilerReferenceIdentitiesExact = true := by
  exact BEq.refl EvmTransactionPreparationExtractor.Generated.compilerReferenceIdentities

private theorem dependencyIdentitiesExact_closed : EvmTransactionPreparationExtractor.Generated.dependencyIdentitiesExact = true := by
  exact BEq.refl EvmTransactionPreparationExtractor.Generated.dependencyIdentities

private theorem stagesExact_closed : EvmTransactionPreparationExtractor.Generated.stagesExact = true := by
  exact BEq.refl EvmTransactionPreparationExtractor.Generated.admittedStages

private theorem operationsExact_closed : EvmTransactionPreparationExtractor.Generated.operationsExact = true := by
  exact BEq.refl EvmTransactionPreparationExtractor.Generated.admittedOperations

private theorem branchesExact_closed : EvmTransactionPreparationExtractor.Generated.branchesExact = true := by
  exact BEq.refl EvmTransactionPreparationExtractor.Generated.admittedBranches

private theorem adaptersExact_closed : EvmTransactionPreparationExtractor.Generated.adaptersExact = true := by
  exact BEq.refl EvmTransactionPreparationExtractor.Generated.admittedAdapters

private theorem compositionsExact_closed : EvmTransactionPreparationExtractor.Generated.compositionsExact = true := by
  exact BEq.refl EvmTransactionPreparationExtractor.Generated.admittedCompositions

private theorem controlFlowsExact_closed : EvmTransactionPreparationExtractor.Generated.controlFlowsExact = true := by
  exact BEq.refl EvmTransactionPreparationExtractor.Generated.admittedControlFlows

private theorem bindingsExact_closed : EvmTransactionPreparationExtractor.Generated.bindingsExact = true := by
  exact BEq.refl EvmTransactionPreparationExtractor.Generated.admittedBindings

private theorem gasFieldsExact_closed : EvmTransactionPreparationExtractor.Generated.gasFieldsExact = true := by
  exact BEq.refl EvmTransactionPreparationExtractor.Generated.admittedGasFields

private theorem snapshotsExact_closed : EvmTransactionPreparationExtractor.Generated.snapshotsExact = true := by
  exact BEq.refl EvmTransactionPreparationExtractor.Generated.admittedSnapshots

private theorem environmentExact_closed : EvmTransactionPreparationExtractor.Generated.environmentExact = true := by
  exact BEq.refl EvmTransactionPreparationExtractor.Generated.admittedEnvironment

private theorem handoffExact_closed : EvmTransactionPreparationExtractor.Generated.handoffExact = true := by
  exact BEq.refl EvmTransactionPreparationExtractor.Generated.admittedHandoff

private theorem orderedEffectsExact_closed : EvmTransactionPreparationExtractor.Generated.orderedEffectsExact = true := by
  exact BEq.refl EvmTransactionPreparationExtractor.Generated.orderedEffects

private theorem admittedSemanticEvidence : EvmTransactionPreparationExtractor.Generated.semanticEvidenceReady = true := by
  simp only [EvmTransactionPreparationExtractor.Generated.semanticEvidenceReady,
    sourceIdentitiesExact_closed,
    compilerReferenceIdentitiesExact_closed,
    dependencyIdentitiesExact_closed,
    stagesExact_closed,
    operationsExact_closed,
    branchesExact_closed,
    adaptersExact_closed,
    compositionsExact_closed,
    controlFlowsExact_closed,
    bindingsExact_closed,
    gasFieldsExact_closed,
    snapshotsExact_closed,
    environmentExact_closed,
    handoffExact_closed,
    orderedEffectsExact_closed, admittedSemanticPlan, Bool.and_true]

/- The generated artifact exposes one admission bit per normalized phase.  This
   proposition is deliberately checked by reduction over the checked-in
   metadata; it is not a substitute for the source-owned obligations below. -/
set_option maxRecDepth 100000 in
set_option maxHeartbeats 1000000 in
theorem admittedGateClosure :
    EvmTransactionPreparationExtractor.Generated.admittedAuthorizationOperations = true ∧
    EvmTransactionPreparationExtractor.Generated.admittedAuthorizationCreateExclusion = true ∧
    EvmTransactionPreparationExtractor.Generated.admittedContextOperations = true ∧
    EvmTransactionPreparationExtractor.Generated.admittedWarmTransactionOperation = true ∧
    EvmTransactionPreparationExtractor.Generated.admittedEnvironmentOperations = true ∧
    EvmTransactionPreparationExtractor.Generated.admittedTargetOperation = true ∧
    EvmTransactionPreparationExtractor.Generated.admittedDelegatedPrecompileOperation = true ∧
    EvmTransactionPreparationExtractor.Generated.admittedDelegatedChargeOperation = true ∧
    EvmTransactionPreparationExtractor.Generated.admittedDelegatedReadOperation = true ∧
    EvmTransactionPreparationExtractor.Generated.admittedReservoirOperations = true ∧
    EvmTransactionPreparationExtractor.Generated.admittedDeadOperation = true ∧
    EvmTransactionPreparationExtractor.Generated.admittedDeadChargeOperation = true ∧
    EvmTransactionPreparationExtractor.Generated.admittedCreateCollisionOperation = true ∧
    EvmTransactionPreparationExtractor.Generated.admittedCreateCollisionClassificationOperation = true ∧
    EvmTransactionPreparationExtractor.Generated.admittedCreateStorageResetOperation = true ∧
    EvmTransactionPreparationExtractor.Generated.admittedPayValueOperation = true ∧
    EvmTransactionPreparationExtractor.Generated.admittedNullCodeOperation = true ∧
    EvmTransactionPreparationExtractor.Generated.admittedCallOperations = true ∧
    EvmTransactionPreparationExtractor.Generated.admittedContextBranches = true ∧
    EvmTransactionPreparationExtractor.Generated.admittedAuthorizationBranches = true ∧
    EvmTransactionPreparationExtractor.Generated.admittedEnvironmentBranches = true ∧
    EvmTransactionPreparationExtractor.Generated.admittedPreparationBranches = true ∧
    EvmTransactionPreparationExtractor.Generated.admittedCallBranches = true := by
  simp only [EvmTransactionPreparationExtractor.Generated.admittedAuthorizationOperations,
    EvmTransactionPreparationExtractor.Generated.admittedAuthorizationCreateExclusion,
    EvmTransactionPreparationExtractor.Generated.admittedContextOperations,
    EvmTransactionPreparationExtractor.Generated.admittedWarmTransactionOperation,
    EvmTransactionPreparationExtractor.Generated.admittedEnvironmentOperations,
    EvmTransactionPreparationExtractor.Generated.admittedTargetOperation,
    EvmTransactionPreparationExtractor.Generated.admittedDelegatedPrecompileOperation,
    EvmTransactionPreparationExtractor.Generated.admittedDelegatedChargeOperation,
    EvmTransactionPreparationExtractor.Generated.admittedDelegatedReadOperation,
    EvmTransactionPreparationExtractor.Generated.admittedReservoirOperations,
    EvmTransactionPreparationExtractor.Generated.admittedDeadOperation,
    EvmTransactionPreparationExtractor.Generated.admittedDeadChargeOperation,
    EvmTransactionPreparationExtractor.Generated.admittedCreateCollisionOperation,
    EvmTransactionPreparationExtractor.Generated.admittedCreateCollisionClassificationOperation,
    EvmTransactionPreparationExtractor.Generated.admittedCreateStorageResetOperation,
    EvmTransactionPreparationExtractor.Generated.admittedPayValueOperation,
    EvmTransactionPreparationExtractor.Generated.admittedNullCodeOperation,
    EvmTransactionPreparationExtractor.Generated.admittedCallOperations,
    EvmTransactionPreparationExtractor.Generated.admittedContextBranches,
    EvmTransactionPreparationExtractor.Generated.admittedAuthorizationBranches,
    EvmTransactionPreparationExtractor.Generated.admittedEnvironmentBranches,
    EvmTransactionPreparationExtractor.Generated.admittedPreparationBranches,
    EvmTransactionPreparationExtractor.Generated.admittedCallBranches]
  simp only [admittedSourceBits0, admittedSourceBits24, admittedSourceBits48, admittedSourceBits72, admittedSourceBits96, Bool.and_true, eq_self, and_self]


theorem admittedSemanticClosure : EvmTransactionPreparationExtractor.Generated.semanticAdmission = true := by
  delta EvmTransactionPreparationExtractor.Generated.semanticAdmission
  simp only [admittedSourceBits0,
    admittedSourceBits24,
    admittedSourceBits48,
    admittedSourceBits72,
    admittedSourceBits96,
    admittedSourceBits120,
    admittedSourceBits144,
    admittedSourceBits168,
    admittedSourceBits192,
    admittedSourceBits216,
    admittedSourceBits240, admittedSemanticEvidence, Bool.and_true, Bool.true_and]
  decide

theorem admittedLegacyDelegatedWarm : EvmTransactionPreparationExtractor.Generated.admittedLegacyDelegatedWarmOperation = true := by
  simp only [EvmTransactionPreparationExtractor.Generated.admittedLegacyDelegatedWarmOperation]
  simp only [admittedSourceBits24]

end EvmTransactionPreparationExtractor.Refinement
