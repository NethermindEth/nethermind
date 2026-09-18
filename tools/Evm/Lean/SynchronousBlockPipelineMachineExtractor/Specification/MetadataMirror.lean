-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

namespace SynchronousBlockPipelineMachineExtractor.Specification

/- This list is a byte-checked mirror of the C# route metadata. It is not a semantic admission. -/
def metadataMirrorSha256 : String := "fb997fb68896341b2d32709c30d44fe91e0f503a6a5d9c1debe6ca472046a0fc"

def phaseKeys : List String :=
[
  "suggested-block validation",
  "synchronous branch selection and preparation",
  "sender and EIP-7702 authority recovery",
  "open world-state scope at parent root",
  "DAO transition when applicable",
  "beacon-root system call",
  "historical blockhash state change",
  "user transaction fold",
  "blob gas and receipt root and bloom",
  "rewards",
  "withdrawals",
  "execution requests and system calls",
  "storage and state roots",
  "block access list",
  "processed-header validation",
  "CommitTree invocation",
  "synchronous result classification and head/processed-chain finalization"
]

def hookKeys : List String :=
[
  "SuggestedBlockSimpleChecks|suggested-block validation|BlockchainProcessor.cs|BlockchainProcessor.RunSimpleChecksAheadOfProcessing|suggested block + options|bool signal or escaping missing-data exception|Signal|false|false|false",
  "EvaluateEligibility|synchronous branch selection and preparation|BlockchainProcessor.cs|BlockchainProcessor.Process eligibility|suggested block + head + options|process/skip signal|Signal|false|false|false",
  "SelectAndPrepareBranch|synchronous branch selection and preparation|BlockchainProcessor.cs|BlockchainProcessor.PrepareProcessingBranch|suggested block + options + block tree|selected branch + parent|Escape|false|false|false",
  "RecoverSignatures|sender and EIP-7702 authority recovery|BlockchainProcessor.cs|BlockchainProcessor.Preprocess|selected blocks|recovered transaction/auth fields|Escape|false|false|false",
  "RecoverSignatureImplementation|sender and EIP-7702 authority recovery|RecoverSignatures.cs|RecoverSignatures.RecoverData|block/transaction signatures + fork|sender and authority fields or escaping recovery failure|Escape|true|false|false",
  "RecoverAuthorities|sender and EIP-7702 authority recovery|BlockchainProcessor.cs|BlockchainProcessor.Preprocess|selected blocks|recovered sender/EIP-7702 authority fields|Escape|false|false|false",
  "BeginBranchScope|open world-state scope at parent root|BranchProcessor.cs|BranchProcessor.Process|parent root + scope provider|owned scope|Escape|true|false|false",
  "BeginGenesisScope|open world-state scope at parent root|BranchProcessor.cs|BranchProcessor.Process|null base + genesis + externally open scope|externally owned scope|Signal|false|false|false",
  "PrepareBal|DAO transition when applicable|BlockProcessor.cs|BlockProcessor.ProcessOne|block + spec + options|BAL mode prepared|Escape|false|false|true",
  "SelectSystemContractHandler|DAO transition when applicable|BlockProcessor.cs|BlockProcessor.ProcessOne|BAL mode|standard/BAL handler|Escape|false|false|false",
  "ApplyDaoTransition|DAO transition when applicable|BlockProcessor.cs|BlockProcessor.ApplyDaoTransition|suggested block + spec|journal effects|Escape|true|false|true",
  "PrepareBlockForProcessing|DAO transition when applicable|BlockProcessor.cs|BlockProcessor.PrepareBlockForProcessing|proposed block|processing clone|Escape|true|false|false",
  "SetOtherTracer|beacon-root system call|BlockProcessor.cs|BlockReceiptsTracer.SetOtherTracer|block tracer|tracer context|Escape|false|false|false",
  "StartBlockTrace|beacon-root system call|BlockProcessor.cs|BlockReceiptsTracer.StartNewBlockTrace|processing block|trace context|Escape|false|false|false",
  "SetBlockExecutionContext|beacon-root system call|BlockProcessor.cs|IBlockTransactionsExecutor.SetBlockExecutionContext|header + spec|execution context|Escape|false|false|false",
  "SetupBlockAccessList|beacon-root system call|BlockProcessor.cs|IBlockAccessListManager.Setup|processing block|BAL accumulator|RetrySequential|false|false|true",
  "StoreBeaconRoot|beacon-root system call|BlockProcessor.cs|SystemContractHandler.StoreBeaconRoot|block + spec + null tracer|journal effects|Escape|true|false|true",
  "ApplyBlockhashStateChanges|historical blockhash state change|BlockProcessor.cs|SystemContractHandler.ApplyBlockhashStateChanges|header + spec|journal effects|Escape|true|false|true",
  "CommitPreTransactionState|historical blockhash state change|BlockProcessor.cs|BlockProcessor.CommitState|spec|journal boundary|Escape|true|false|true",
  "ExecuteTransactionFold|user transaction fold|BlockProcessor.BlockValidationTransactionsExecutor.cs|IBlockTransactionsExecutor.ProcessTransactions|block + options + receipt tracer + cancellation|receipts + execution/state counters|RejectInvalidBlock|true|false|true",
  "TransactionsExecutedSignal|user transaction fold|BlockProcessor.cs|BlockProcessor.TransactionsExecuted|completed transaction fold|cancellation signal|Signal|false|false|false",
  "CommitPostTransactionState|user transaction fold|BlockProcessor.cs|BlockProcessor.CommitState|spec + completed transaction fold|journal boundary|Escape|true|true|true",
  "BuildReceiptsAndCumulativePaidGas|user transaction fold|BlockReceiptsTracer.cs|BlockReceiptsTracer.BuildReceipt|settlement + previous receipt counter|receipt paid/cumulative gas|Escape|false|false|true",
  "CalculateBlobGas|blob gas and receipt root and bloom|BlockProcessor.cs|BlobGasCalculator.CalculateBlobGas|transactions|blob gas fields|Escape|true|false|false",
  "CalculateReceiptBlooms|blob gas and receipt root and bloom|BlockProcessor.cs|BlockProcessor.CalculateBlooms|updated receipts + logs|receipt blooms|Escape|true|true|false",
  "AccumulateBlockBloom|blob gas and receipt root and bloom|BlockProcessor.cs|BlockProcessor.AccumulateBlockBloom|updated receipts|block bloom|Escape|false|true|false",
  "CalculateReceiptsRoot|blob gas and receipt root and bloom|BlockProcessor.cs|BlockProcessor.CalculateReceiptsRoot|updated receipts + spec + block|receipt root|Escape|false|true|false",
  "InstallSynchronousReceiptArtifacts|blob gas and receipt root and bloom|BlockProcessor.cs|BlockProcessor.ProcessBlock|synchronous bloom/root|header bloom/root|Escape|true|true|false",
  "ScheduleBackgroundReceiptArtifacts|blob gas and receipt root and bloom|BlockProcessor.cs|BlockProcessor.ProcessBlock|updated receipts + spec + block|pending receipt task|Escape|false|true|false",
  "AwaitBackgroundReceiptArtifacts|blob gas and receipt root and bloom|BlockProcessor.cs|BlockProcessor.ProcessBlock|pending task|header bloom/root or escaping failure|Escape|true|true|false",
  "ApplyMinerRewards|rewards|BlockProcessor.cs|BlockProcessor.ApplyMinerRewards|processed block + tracer + spec|journal effects|Escape|true|false|true",
  "ProcessWithdrawals|withdrawals|BlockProcessor.cs|SystemContractHandler.ProcessWithdrawals|block + spec|journal effects|Escape|true|false|true",
  "CommitPostSystemState|withdrawals|BlockProcessor.cs|BlockProcessor.CommitState|withdrawal/reward journal + spec|journal boundary|Escape|true|false|true",
  "ProcessExecutionRequests|execution requests and system calls|BlockProcessor.cs|SystemContractHandler.ProcessExecutionRequests|block + state provider + updated receipts + spec|requests hash/observations|Escape|true|true|true",
  "EndBlockTrace|execution requests and system calls|BlockProcessor.cs|BlockReceiptsTracer.EndBlockTrace|trace + receipt mode|trace completion|Escape|false|true|false",
  "CommitStorageAndStateRoots|storage and state roots|BlockProcessor.cs|BlockProcessor.CommitStateAndStorageRoots|spec|storage/state journal boundary|Escape|true|false|true",
  "SetAccountChanges|storage and state roots|BlockProcessor.cs|BlockProcessor.SetAccountChanges|processed block|account-change observation|Escape|false|false|true",
  "ComputeStateRoot|storage and state roots|BlockProcessor.cs|BlockProcessor.ComputeStateRoot|processing header|state root|Escape|true|false|true",
  "FinalizeBal|block access list|BlockProcessor.cs|IBlockAccessListManager.SetBlockAccessList|processed block|BAL observation|Escape|true|false|true",
  "ValidateProcessedBlock|processed-header validation|BlockProcessor.cs|BlockProcessor.ValidateProcessedBlock|proposed + processing block + receipts + options|accepted or caught InvalidBlock|RejectInvalidBlock|false|true|true",
  "DisposeAccountChanges|processed-header validation|BlockProcessor.cs|BlockProcessor.ValidateProcessedBlock/ProcessOne finally|failed block|account changes disposed|Signal|true|false|true",
  "DisposeRetryScope|CommitTree invocation|BranchProcessor.cs|BranchProcessor.Process BAL retry|failed parallel attempt|disposed attempt scope|Escape|true|false|true",
  "ReopenRetryScope|CommitTree invocation|BranchProcessor.cs|BranchProcessor.Process BAL retry|pre-block base root|fresh sequential scope|Escape|true|false|false",
  "DisposeCheckpointScope|CommitTree invocation|BranchProcessor.cs|BranchProcessor.Process checkpoint|successful non-edge checkpoint|disposed checkpoint scope|Escape|true|false|true",
  "ReopenCheckpointScope|CommitTree invocation|BranchProcessor.cs|BranchProcessor.Process checkpoint|previous branch state root|reopened checkpoint scope|Escape|true|false|true",
  "WaitPrewarm|CommitTree invocation|BranchProcessor.cs|BranchProcessor.Process|prewarm task|waited/cancelled task|Escape|false|false|true",
  "CommitTree|CommitTree invocation|BranchProcessor.cs|BranchProcessor.PreCommitBlock|successful processed header|logical persistence invocation|Escape|false|false|true",
  "RecordInclusionListSignal|CommitTree invocation|BranchProcessor.cs|BranchProcessor.Process inclusion signal|processed block + suggested block + post-execution state|Option Bool signal copied to both blocks|Signal|true|false|true",
  "IncrementSuccessfulPrefix|CommitTree invocation|BranchProcessor.cs|BranchProcessor.Process success prefix|successful CommitTree|processed prefix count|Signal|false|false|true",
  "ResetScope|CommitTree invocation|BranchProcessor.cs|IWorldState.Reset|successful block|next-block journal baseline|Escape|true|false|true",
  "DisposeBranchScope|CommitTree invocation|BranchProcessor.cs|BranchProcessor.Process finally|branch completion/exception|disposed owned scope|Escape|true|false|true",
  "CatchBranchException|CommitTree invocation|BranchProcessor.cs|BranchProcessor.Process catch|escaping branch exception|recorded exception then rethrow|Escape|false|false|true",
  "CompleteBranchProcessing|CommitTree invocation|BranchProcessor.cs|BranchProcessor.Process finally|disposed scope + prefix count + exception|completion event|Signal|false|false|false",
  "ClassifySynchronousResult|synchronous result classification and head/processed-chain finalization|BlockchainProcessor.cs|BlockchainProcessor.ProcessBranch|branch result/error|accepted, skipped, invalid or escaped|Signal|false|false|false",
  "UpdateTotalDifficulty|synchronous result classification and head/processed-chain finalization|BlockchainProcessor.cs|BlockchainProcessor.Process|last processed + suggested block|total-difficulty observation|Escape|true|false|false",
  "TryUpdateMainChain|synchronous result classification and head/processed-chain finalization|BlockchainProcessor.cs|IBlockTree.TryUpdateMainChain|suggested header + processed blocks|bool signal|Signal|true|false|false",
  "MarkChainAsProcessed|synchronous result classification and head/processed-chain finalization|BlockchainProcessor.cs|IBlockTree.MarkChainAsProcessed|processed branch|processed-chain observation|Escape|true|false|false"
]

end SynchronousBlockPipelineMachineExtractor.Specification
