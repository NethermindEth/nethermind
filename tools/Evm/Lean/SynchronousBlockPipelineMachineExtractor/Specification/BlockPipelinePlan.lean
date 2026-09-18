-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

import SynchronousBlockPipelineMachineExtractor.Specification.BlockPipelineState

namespace SynchronousBlockPipelineMachineExtractor.Specification

structure PhaseContract where
  phase : Phase
  label : String
  hooks : List HookId
  deriving DecidableEq, Repr

/- The hook list is phase membership only. Runtime order is represented by
   source-checked partial-order edges in the package audit, with separate arms
   for the synchronous receipt path and the task-local background path. -/
def phasePlan : List PhaseContract :=
    [.mk .suggestedBlockValidation (Phase.label .suggestedBlockValidation)
       [.suggestedBlockSimpleChecks],
    .mk .synchronousBranchSelectionAndPreparation
       (Phase.label .synchronousBranchSelectionAndPreparation)
       [.evaluateEligibility, .selectAndPrepareBranch],
    .mk .senderAndAuthorityRecovery (Phase.label .senderAndAuthorityRecovery)
       [.recoverSignatures, .recoverSignatureImplementation, .recoverAuthorities],
   .mk .openWorldStateScope (Phase.label .openWorldStateScope)
      [.beginBranchScope, .beginGenesisScope],
   .mk .daoTransition (Phase.label .daoTransition)
      [.prepareBal, .selectSystemContractHandler, .applyDaoTransition,
       .prepareBlockForProcessing],
   .mk .beaconRootSystemCall (Phase.label .beaconRootSystemCall)
      [.setOtherTracer, .startBlockTrace, .setBlockExecutionContext,
       .setupBlockAccessList, .storeBeaconRoot],
   .mk .historicalBlockhashStateChange (Phase.label .historicalBlockhashStateChange)
      [.applyBlockhashStateChanges, .commitPreTransactionState],
   .mk .userTransactionFold (Phase.label .userTransactionFold)
      [.executeTransactionFold, .transactionsExecutedSignal,
       .commitPostTransactionState, .buildReceiptsAndCumulativePaidGas],
   .mk .blobGasReceiptRootAndBloom (Phase.label .blobGasReceiptRootAndBloom)
      [.calculateBlobGas, .calculateReceiptBlooms, .accumulateBlockBloom,
       .calculateReceiptsRoot, .installSynchronousReceiptArtifacts,
       .scheduleBackgroundReceiptArtifacts, .awaitBackgroundReceiptArtifacts],
   .mk .rewards (Phase.label .rewards) [.applyMinerRewards],
   .mk .withdrawals (Phase.label .withdrawals)
      [.processWithdrawals, .commitPostSystemState],
   .mk .executionRequestsAndSystemCalls (Phase.label .executionRequestsAndSystemCalls)
      [.processExecutionRequests, .endBlockTrace],
   .mk .storageAndStateRoots (Phase.label .storageAndStateRoots)
      [.commitStorageAndStateRoots, .setAccountChanges, .computeStateRoot],
   .mk .blockAccessList (Phase.label .blockAccessList) [.finalizeBal],
   .mk .processedHeaderValidation (Phase.label .processedHeaderValidation)
      [.validateProcessedBlock, .disposeAccountChanges],
    .mk .commitTreeInvocation (Phase.label .commitTreeInvocation)
       [.disposeRetryScope, .reopenRetryScope, .recordInclusionListSignal, .waitPrewarm,
        .commitTree, .incrementSuccessfulPrefix, .disposeCheckpointScope,
        .reopenCheckpointScope, .resetScope, .disposeBranchScope, .catchBranchException,
        .completeBranchProcessing],
   .mk .synchronousResultClassificationAndHeadFinalization
      (Phase.label .synchronousResultClassificationAndHeadFinalization)
      [.classifySynchronousResult, .updateTotalDifficulty, .tryUpdateMainChain,
       .markChainAsProcessed]]

def synchronousReceiptPlan : List HookId :=
  [.calculateReceiptBlooms, .calculateReceiptsRoot, .installSynchronousReceiptArtifacts]

def backgroundReceiptPlan : List HookId :=
  [.scheduleBackgroundReceiptArtifacts, .calculateReceiptBlooms, .accumulateBlockBloom,
   .calculateReceiptsRoot, .awaitBackgroundReceiptArtifacts]

def processOnePlan : List PhaseContract :=
  (phasePlan.drop 4).take 11

def branchPlan : List PhaseContract :=
  (phasePlan.drop 3).take 13

def chainPlan : List PhaseContract :=
  (phasePlan.take 4) ++ (phasePlan.drop 14)

def allHookDependencies : List HookDependency :=
  [.mk .suggestedBlockSimpleChecks .suggestedBlockValidation
      "BlockchainProcessor.cs" "BlockchainProcessor.RunSimpleChecksAheadOfProcessing"
      "suggested block + options" "bool signal or escaping missing-data exception" .signal false false false,
   .mk .evaluateEligibility .synchronousBranchSelectionAndPreparation
      "BlockchainProcessor.cs" "BlockchainProcessor.Process eligibility"
      "suggested block + head + options" "process/skip signal" .signal false false false,
   .mk .selectAndPrepareBranch .synchronousBranchSelectionAndPreparation
      "BlockchainProcessor.cs" "BlockchainProcessor.PrepareProcessingBranch"
      "suggested block + options + block tree" "selected branch + parent" .escape false false false,
   .mk .recoverSignatures .senderAndAuthorityRecovery
      "BlockchainProcessor.cs" "BlockchainProcessor.Preprocess"
      "selected blocks" "recovered transaction/auth fields" .escape false false false,
   .mk .recoverSignatureImplementation .senderAndAuthorityRecovery
      "RecoverSignatures.cs" "RecoverSignatures.RecoverData"
      "block/transaction signatures + fork" "sender and authority fields or escaping recovery failure" .escape true false false,
   .mk .recoverAuthorities .senderAndAuthorityRecovery
      "BlockchainProcessor.cs" "BlockchainProcessor.Preprocess"
      "selected blocks" "recovered sender/EIP-7702 authority fields" .escape false false false,
   .mk .beginBranchScope .openWorldStateScope
      "BranchProcessor.cs" "BranchProcessor.Process"
      "parent root + scope provider" "owned scope" .escape true false false,
   .mk .beginGenesisScope .openWorldStateScope
      "BranchProcessor.cs" "BranchProcessor.Process"
      "null base + genesis + externally open scope" "externally owned scope" .signal false false false,
   .mk .prepareBal .daoTransition
      "BlockProcessor.cs" "BlockProcessor.ProcessOne"
      "block + spec + options" "BAL mode prepared" .escape false false true,
   .mk .selectSystemContractHandler .daoTransition
      "BlockProcessor.cs" "BlockProcessor.ProcessOne"
      "BAL mode" "standard/BAL handler" .escape false false false,
   .mk .applyDaoTransition .daoTransition
      "BlockProcessor.cs" "BlockProcessor.ApplyDaoTransition"
      "suggested block + spec" "journal effects" .escape true false true,
   .mk .prepareBlockForProcessing .daoTransition
      "BlockProcessor.cs" "BlockProcessor.PrepareBlockForProcessing"
      "proposed block" "processing clone" .escape true false false,
   .mk .setOtherTracer .beaconRootSystemCall
      "BlockProcessor.cs" "BlockReceiptsTracer.SetOtherTracer"
      "block tracer" "tracer context" .escape false false false,
   .mk .startBlockTrace .beaconRootSystemCall
      "BlockProcessor.cs" "BlockReceiptsTracer.StartNewBlockTrace"
      "processing block" "trace context" .escape false false false,
   .mk .setBlockExecutionContext .beaconRootSystemCall
      "BlockProcessor.cs" "IBlockTransactionsExecutor.SetBlockExecutionContext"
      "header + spec" "execution context" .escape false false false,
   .mk .setupBlockAccessList .beaconRootSystemCall
      "BlockProcessor.cs" "IBlockAccessListManager.Setup"
      "processing block" "BAL accumulator" .retrySequential false false true,
   .mk .storeBeaconRoot .beaconRootSystemCall
      "BlockProcessor.cs" "SystemContractHandler.StoreBeaconRoot"
      "block + spec + null tracer" "journal effects" .escape true false true,
   .mk .applyBlockhashStateChanges .historicalBlockhashStateChange
      "BlockProcessor.cs" "SystemContractHandler.ApplyBlockhashStateChanges"
      "header + spec" "journal effects" .escape true false true,
   .mk .commitPreTransactionState .historicalBlockhashStateChange
      "BlockProcessor.cs" "BlockProcessor.CommitState"
      "spec" "journal boundary" .escape true false true,
   .mk .executeTransactionFold .userTransactionFold
      "BlockProcessor.BlockValidationTransactionsExecutor.cs" "IBlockTransactionsExecutor.ProcessTransactions"
      "block + options + receipt tracer + cancellation" "receipts + execution/state counters" .rejectInvalidBlock true false true,
   .mk .transactionsExecutedSignal .userTransactionFold
      "BlockProcessor.cs" "BlockProcessor.TransactionsExecuted"
      "completed transaction fold" "cancellation signal" .signal false false false,
   .mk .commitPostTransactionState .userTransactionFold
      "BlockProcessor.cs" "BlockProcessor.CommitState"
      "spec + completed transaction fold" "journal boundary" .escape true true true,
   .mk .buildReceiptsAndCumulativePaidGas .userTransactionFold
      "BlockReceiptsTracer.cs" "BlockReceiptsTracer.BuildReceipt"
      "settlement + previous receipt counter" "receipt paid/cumulative gas" .escape false false true,
   .mk .calculateBlobGas .blobGasReceiptRootAndBloom
      "BlockProcessor.cs" "BlobGasCalculator.CalculateBlobGas"
      "transactions" "blob gas fields" .escape true false false,
   .mk .calculateReceiptBlooms .blobGasReceiptRootAndBloom
      "BlockProcessor.cs" "BlockProcessor.CalculateBlooms"
      "updated receipts + logs" "receipt blooms" .escape true true false,
   .mk .accumulateBlockBloom .blobGasReceiptRootAndBloom
      "BlockProcessor.cs" "BlockProcessor.AccumulateBlockBloom"
      "updated receipts" "block bloom" .escape false true false,
   .mk .calculateReceiptsRoot .blobGasReceiptRootAndBloom
      "BlockProcessor.cs" "BlockProcessor.CalculateReceiptsRoot"
      "updated receipts + spec + block" "receipt root" .escape false true false,
   .mk .installSynchronousReceiptArtifacts .blobGasReceiptRootAndBloom
      "BlockProcessor.cs" "BlockProcessor.ProcessBlock"
      "synchronous bloom/root" "header bloom/root" .escape true true false,
   .mk .scheduleBackgroundReceiptArtifacts .blobGasReceiptRootAndBloom
      "BlockProcessor.cs" "BlockProcessor.ProcessBlock"
      "updated receipts + spec + block" "pending receipt task" .escape false true false,
   .mk .awaitBackgroundReceiptArtifacts .blobGasReceiptRootAndBloom
      "BlockProcessor.cs" "BlockProcessor.ProcessBlock"
      "pending task" "header bloom/root or escaping failure" .escape true true false,
   .mk .applyMinerRewards .rewards
      "BlockProcessor.cs" "BlockProcessor.ApplyMinerRewards"
      "processed block + tracer + spec" "journal effects" .escape true false true,
   .mk .processWithdrawals .withdrawals
      "BlockProcessor.cs" "SystemContractHandler.ProcessWithdrawals"
      "block + spec" "journal effects" .escape true false true,
   .mk .commitPostSystemState .withdrawals
      "BlockProcessor.cs" "BlockProcessor.CommitState"
      "withdrawal/reward journal + spec" "journal boundary" .escape true false true,
   .mk .processExecutionRequests .executionRequestsAndSystemCalls
      "BlockProcessor.cs" "SystemContractHandler.ProcessExecutionRequests"
      "block + state provider + updated receipts + spec" "requests hash/observations" .escape true true true,
   .mk .endBlockTrace .executionRequestsAndSystemCalls
      "BlockProcessor.cs" "BlockReceiptsTracer.EndBlockTrace"
      "trace + receipt mode" "trace completion" .escape false true false,
   .mk .commitStorageAndStateRoots .storageAndStateRoots
      "BlockProcessor.cs" "BlockProcessor.CommitStateAndStorageRoots"
      "spec" "storage/state journal boundary" .escape true false true,
   .mk .setAccountChanges .storageAndStateRoots
      "BlockProcessor.cs" "BlockProcessor.SetAccountChanges"
      "processed block" "account-change observation" .escape false false true,
   .mk .computeStateRoot .storageAndStateRoots
      "BlockProcessor.cs" "BlockProcessor.ComputeStateRoot"
      "processing header" "state root" .escape true false true,
   .mk .finalizeBal .blockAccessList
      "BlockProcessor.cs" "IBlockAccessListManager.SetBlockAccessList"
      "processed block" "BAL observation" .escape true false true,
   .mk .validateProcessedBlock .processedHeaderValidation
      "BlockProcessor.cs" "BlockProcessor.ValidateProcessedBlock"
      "proposed + processing block + receipts + options" "accepted or caught InvalidBlock" .rejectInvalidBlock false true true,
   .mk .disposeAccountChanges .processedHeaderValidation
      "BlockProcessor.cs" "BlockProcessor.ValidateProcessedBlock/ProcessOne finally"
      "failed block" "account changes disposed" .signal true false true,
   .mk .disposeRetryScope .commitTreeInvocation
      "BranchProcessor.cs" "BranchProcessor.Process BAL retry"
      "failed parallel attempt" "disposed attempt scope" .escape true false true,
   .mk .reopenRetryScope .commitTreeInvocation
      "BranchProcessor.cs" "BranchProcessor.Process BAL retry"
      "pre-block base root" "fresh sequential scope" .escape true false false,
   .mk .disposeCheckpointScope .commitTreeInvocation
      "BranchProcessor.cs" "BranchProcessor.Process checkpoint"
      "successful non-edge checkpoint" "disposed checkpoint scope" .escape true false true,
   .mk .reopenCheckpointScope .commitTreeInvocation
      "BranchProcessor.cs" "BranchProcessor.Process checkpoint"
      "previous branch state root" "reopened checkpoint scope" .escape true false true,
   .mk .waitPrewarm .commitTreeInvocation
      "BranchProcessor.cs" "BranchProcessor.Process"
      "prewarm task" "waited/cancelled task" .escape false false true,
   .mk .commitTree .commitTreeInvocation
      "BranchProcessor.cs" "BranchProcessor.PreCommitBlock"
      "successful processed header" "logical persistence invocation" .escape false false true,
   .mk .recordInclusionListSignal .commitTreeInvocation
      "BranchProcessor.cs" "BranchProcessor.Process inclusion signal"
      "processed block + suggested block + post-execution state" "Option Bool signal copied to both blocks" .signal true false true,
   .mk .incrementSuccessfulPrefix .commitTreeInvocation
      "BranchProcessor.cs" "BranchProcessor.Process success prefix"
      "successful CommitTree" "processed prefix count" .signal false false true,
   .mk .resetScope .commitTreeInvocation
      "BranchProcessor.cs" "IWorldState.Reset"
      "successful block" "next-block journal baseline" .escape true false true,
   .mk .disposeBranchScope .commitTreeInvocation
      "BranchProcessor.cs" "BranchProcessor.Process finally"
      "branch completion/exception" "disposed owned scope" .escape true false true,
   .mk .catchBranchException .commitTreeInvocation
      "BranchProcessor.cs" "BranchProcessor.Process catch"
      "escaping branch exception" "recorded exception then rethrow" .escape false false true,
   .mk .completeBranchProcessing .commitTreeInvocation
      "BranchProcessor.cs" "BranchProcessor.Process finally"
      "disposed scope + prefix count + exception" "completion event" .signal false false false,
   .mk .classifySynchronousResult .synchronousResultClassificationAndHeadFinalization
      "BlockchainProcessor.cs" "BlockchainProcessor.ProcessBranch"
      "branch result/error" "accepted, skipped, invalid or escaped" .signal false false false,
   .mk .updateTotalDifficulty .synchronousResultClassificationAndHeadFinalization
      "BlockchainProcessor.cs" "BlockchainProcessor.Process"
      "last processed + suggested block" "total-difficulty observation" .escape true false false,
   .mk .tryUpdateMainChain .synchronousResultClassificationAndHeadFinalization
      "BlockchainProcessor.cs" "IBlockTree.TryUpdateMainChain"
      "suggested header + processed blocks" "bool signal" .signal true false false,
   .mk .markChainAsProcessed .synchronousResultClassificationAndHeadFinalization
      "BlockchainProcessor.cs" "IBlockTree.MarkChainAsProcessed"
      "processed branch" "processed-chain observation" .escape true false false]

def ordinaryTransactionBoundary : List String :=
  ["TransactionProcessorBase.Process",
   "TransactionProcessorBase.ExecuteCore",
   "EthereumTransactionProcessor",
   "TransactionSettlementKernel",
   "BlockReceiptsTracer.BuildReceipt"]

def systemTransactionBoundary : List String :=
  ["SystemTransactionRoutingKernel",
   "SystemTransactionProcessor.Execute",
   "SystemTransactionProcessor.PayFees",
   "SystemTransactionProcessor.ValidateGas"]

end SynchronousBlockPipelineMachineExtractor.Specification
