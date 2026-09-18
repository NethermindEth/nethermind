// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Validators;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;

namespace Evm.Formal;

internal static class ProcessingCoverageGate
{
    private const int CurrentSchemaVersion = 1;
    private const string VerificationManifestRelativePath = "tools/Evm/Lean/verification-manifest.json";
    private const string JsonNewLine = "\n";

    private static readonly string[] RequiredBlockOrder =
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
        "synchronous result classification and head/processed-chain finalization",
    ];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private static readonly AnchorDefinition[] TransactionAnchors =
    [
        new("process", "src/Nethermind/Nethermind.Evm/TransactionProcessing/TransactionProcessor.cs", "public TransactionResult Process(", "TransactionProcessorBase.Process"),
        new("execute-core", "src/Nethermind/Nethermind.Evm/TransactionProcessing/TransactionProcessor.cs", "private TransactionResult ExecuteCore(", "TransactionProcessorBase.ExecuteCore"),
        new("recover-sender-before-intrinsic", "src/Nethermind/Nethermind.Evm/TransactionProcessing/TransactionProcessor.cs", "RecoverSenderBeforeIntrinsicGas(tx, spec);", "TransactionProcessorBase.RecoverSenderBeforeIntrinsicGas"),
        new("calculate-intrinsic-gas", "src/Nethermind/Nethermind.Evm/TransactionProcessing/TransactionProcessor.cs", "IntrinsicGas<TGasPolicy> intrinsicGas = CalculateIntrinsicGas(tx, spec, header.GasLimit);", "TransactionProcessorBase.CalculateIntrinsicGas"),
        new("validate-static", "src/Nethermind/Nethermind.Evm/TransactionProcessing/TransactionProcessor.cs", "if (!(result = ValidateStatic(tx, header, spec, opts, in intrinsicGas))) return result;", "TransactionProcessorBase.ValidateStatic"),
        new("effective-gas-price", "src/Nethermind/Nethermind.Evm/TransactionProcessing/TransactionProcessor.cs", "UInt256 effectiveGasPrice = CalculateEffectiveGasPrice(tx, spec.IsEip1559Enabled, header.BaseFeePerGas, out UInt256 opcodeGasPrice);", "TransactionProcessorBase.CalculateEffectiveGasPrice"),
        new("recover-sender", "src/Nethermind/Nethermind.Evm/TransactionProcessing/TransactionProcessor.cs", "bool deleteCallerAccount = RecoverSenderIfNeeded(tx, spec, opts, effectiveGasPrice);", "TransactionProcessorBase.RecoverSenderIfNeeded"),
        new("validate-sender", "src/Nethermind/Nethermind.Evm/TransactionProcessing/TransactionProcessor.cs", "if (!(result = ValidateSender(tx, header, spec, tracer, opts)) ||", "TransactionProcessorBase.ValidateSender"),
        new("buy-gas", "src/Nethermind/Nethermind.Evm/TransactionProcessing/TransactionProcessor.cs", "!(result = BuyGas(tx, spec, tracer, opts, effectiveGasPrice, out UInt256 premiumPerGas, out UInt256 senderReservedGasPayment, out UInt256 blobBaseFee)) ||", "TransactionProcessorBase.BuyGas"),
        new("increment-nonce", "src/Nethermind/Nethermind.Evm/TransactionProcessing/TransactionProcessor.cs", "!(result = IncrementNonce(tx, header, spec, tracer, opts)))", "TransactionProcessorBase.IncrementNonce"),
        new("prepare-simple-transfer", "src/Nethermind/Nethermind.Evm/TransactionProcessing/TransactionProcessor.cs", "Address? simpleTransferRecipient = PrepareSimpleTransferFastPath(tx, spec, out CodeInfo? preloadedCodeInfo, out Address? preloadedDelegationAddress);", "TransactionProcessorBase.PrepareSimpleTransferFastPath"),
        new("commit-before-execution", "src/Nethermind/Nethermind.Evm/TransactionProcessing/TransactionProcessor.cs", "if (commitBeforeExecution) WorldState.Commit(spec, tracer.IsTracingState ? tracer : NullTxTracer.Instance, commitRoots: false);", "IWorldState.Commit"),
        new("calculate-available-gas", "src/Nethermind/Nethermind.Evm/TransactionProcessing/TransactionProcessor.cs", "if (!(result = CalculateAvailableGas(tx, spec, in intrinsicGas, out TGasPolicy gasAvailable))) return result;", "TransactionProcessorBase.CalculateAvailableGas"),
        new("simple-transfer-dispatch", "src/Nethermind/Nethermind.Evm/TransactionProcessing/TransactionProcessor.cs", "return ExecuteSimpleTransfer(tx, header, spec, tracer, opts, restore, commit, deleteCallerAccount, simpleTransferRecipient, in intrinsicGas, gasAvailable, in opcodeGasPrice, in premiumPerGas, in senderReservedGasPayment, in blobBaseFee);", "TransactionProcessorBase.ExecuteSimpleTransfer"),
        new("evm-dispatch", "src/Nethermind/Nethermind.Evm/TransactionProcessing/TransactionProcessor.cs", "return ExecuteEvmTransaction(tx, header, spec, tracer, opts, restore, commit, deleteCallerAccount, in intrinsicGas, gasAvailable, in opcodeGasPrice, in premiumPerGas, in senderReservedGasPayment, in blobBaseFee, preloadedCodeInfo, preloadedDelegationAddress);", "TransactionProcessorBase.ExecuteEvmTransaction"),
        new("authorization-processing", "src/Nethermind/Nethermind.Evm/TransactionProcessing/TransactionProcessor.cs", "if (!ProcessDelegations(tx, spec, accessTracker, ref gasAvailable, ref executionIntrinsicGasStandard, out delegationRefunds))", "TransactionProcessorBase.ProcessDelegations"),
        new("execution-environment", "src/Nethermind/Nethermind.Evm/TransactionProcessing/TransactionProcessor.cs", "if (!(result = BuildExecutionEnvironment(tx, spec, _codeInfoRepository, accessTracker, preloadedCodeInfo, preloadedDelegationAddress, loadRecipient, ref gasAvailable, ref topFrameOutOfGas, out ExecutionEnvironment e))) return result;", "TransactionProcessorBase.BuildExecutionEnvironment"),
        new("vm-call", "src/Nethermind/Nethermind.Evm/TransactionProcessing/TransactionProcessor.cs", "ExecuteEvmCall<OffFlag>(tx, header, spec, tracer, opts, delegationRefunds, executionIntrinsicGas, postIntrinsicStateReservoir, accessTracker, gasAvailable, env, topFrameOutOfGas, out TransactionSubstate substate, out GasConsumed spentGas)", "TransactionProcessorBase.ExecuteEvmCall"),
        new("create-destination-classification", "src/Nethermind/Nethermind.Evm/TransactionProcessing/TransactionProcessor.cs", "bool deploymentPrepared = PrepareDeployment(", "TransactionProcessorBase.ExecuteEvmCall"),
        new("create-state-gas-charge", "src/Nethermind/Nethermind.Evm/TransactionProcessing/TransactionProcessor.cs", "if (!TGasPolicy.TryConsumeCreateStateGas(ref gasAvailable))", "TransactionProcessorBase.ExecuteEvmCall"),
        new("create-collision-check", "src/Nethermind/Nethermind.Evm/TransactionProcessing/TransactionProcessor.cs", "if (!deploymentPrepared)", "TransactionProcessorBase.ExecuteEvmCall"),
        new("create-pay-value", "src/Nethermind/Nethermind.Evm/TransactionProcessing/TransactionProcessor.cs", "PayValue(tx, spec, opts);", "TransactionProcessorBase.ExecuteEvmCall", 1),
        new("update-header-and-fees", "src/Nethermind/Nethermind.Evm/TransactionProcessing/TransactionProcessor.cs", "UpdateHeaderGasUsedAndPayFees(tx, header, spec, tracer, opts, in substate, in spentGas, premiumPerGas, in opcodeGasPrice, blobBaseFee, statusCode);", "TransactionProcessorBase.UpdateHeaderGasUsedAndPayFees", 0),
        new("finalize-transaction", "src/Nethermind/Nethermind.Evm/TransactionProcessing/TransactionProcessor.cs", "return FinalizeTransaction(tx, spec, tracer, opts, restore, commit, deleteCallerAccount, in senderReservedGasPayment, env.ExecutingAccount, in substate, spentGas, statusCode);", "TransactionProcessorBase.FinalizeTransaction"),
        new("simple-transfer-refund", "src/Nethermind/Nethermind.Evm/TransactionProcessing/TransactionProcessor.cs", "GasConsumed spentGas = Refund(tx, header, spec, opts, in substate, in gasAvailable, in opcodeGasPrice, codeInsertRefunds: 0, in floorGas, in standardGas, postIntrinsicStateReservoir);", "TransactionProcessorBase.Refund"),
        new("evm-refund", "src/Nethermind/Nethermind.Evm/TransactionProcessing/TransactionProcessor.cs", "gasConsumed = Refund(tx, header, spec, opts, in substate, gasAvailable, VirtualMachine.TxExecutionContext.GasPrice, (ulong)delegationRefunds, gas.FloorGas, gas.Standard, postIntrinsicStateReservoir, topLevelCreateStateGasCharged);", "TransactionProcessorBase.Refund"),
        new("simple-update-header-and-fees", "src/Nethermind/Nethermind.Evm/TransactionProcessing/TransactionProcessor.cs", "UpdateHeaderGasUsedAndPayFees(tx, header, spec, tracer, opts, in substate, in spentGas, premiumPerGas, in opcodeGasPrice, blobBaseFee, statusCode);", "TransactionProcessorBase.UpdateHeaderGasUsedAndPayFees", 1),
        new("simple-finalize", "src/Nethermind/Nethermind.Evm/TransactionProcessing/TransactionProcessor.cs", "return FinalizeTransaction(tx, spec, tracer, opts, restore, commit, deleteCallerAccount, in senderReservedGasPayment, recipient, in substate, spentGas, statusCode);", "TransactionProcessorBase.FinalizeTransaction"),
        new("pay-fees", "src/Nethermind/Nethermind.Evm/TransactionProcessing/TransactionProcessor.cs", "protected virtual void PayFees(", "TransactionProcessorBase.PayFees"),
        new("pay-refund", "src/Nethermind/Nethermind.Evm/TransactionProcessing/TransactionProcessor.cs", "protected virtual void PayRefund(", "TransactionProcessorBase.PayRefund"),
        new("pay-fees-call", "src/Nethermind/Nethermind.Evm/TransactionProcessing/TransactionProcessor.cs", "PayFees(tx, header, spec, tracer, in substate, spentGas.SpentGas, premiumPerGas, in effectiveGasPrice, blobBaseFee, statusCode);", "TransactionProcessorBase.UpdateHeaderGasUsedAndPayFees"),
        new("pay-refund-call-failed-halt", "src/Nethermind/Nethermind.Evm/TransactionProcessing/TransactionProcessor.cs", "PayRefund(tx, (tx.GasLimit - spentGas) * gasPrice, spec);", "TransactionProcessorBase.Refund"),
        new("pay-refund-call", "src/Nethermind/Nethermind.Evm/TransactionProcessing/TransactionProcessor.cs", "PayRefund(tx, (tx.GasLimit - settlement.SpentGas) * gasPrice, spec);", "TransactionProcessorBase.Refund"),
        new("receipt-start", "src/Nethermind/Nethermind.Consensus/Processing/TransactionProcessorAdapterExtensions.cs", "using ITxTracer tracer = receiptsTracer.StartNewTxTrace(currentTx);", "TransactionProcessorAdapterExtensions.ProcessTransaction"),
        new("receipt-end", "src/Nethermind/Nethermind.Consensus/Processing/TransactionProcessorAdapterExtensions.cs", "receiptsTracer.EndTxTrace();", "TransactionProcessorAdapterExtensions.ProcessTransaction"),
    ];

    private static readonly PhaseDefinition[] TransactionPhases =
    [
        new(
            "validation",
            "Intrinsic, static, gas-limit, and transaction-shape validation before execution.",
            ["process", "execute-core", "recover-sender-before-intrinsic", "calculate-intrinsic-gas", "validate-static"]),
        new(
            "sender-auth-recovery",
            "Signer recovery, EIP-7702 authorization processing, sender checks, gas reservation, and nonce update.",
            ["effective-gas-price", "recover-sender", "validate-sender", "buy-gas", "increment-nonce", "authorization-processing"]),
        new(
            "pre-execution",
            "Fast-path selection, pre-execution state preparation, and execution-environment construction.",
            ["prepare-simple-transfer", "commit-before-execution", "calculate-available-gas", "execution-environment"]),
        new(
            "simple-transfer-vm",
            "The no-code value-transfer path or the EVM transaction/frame path.",
            ["simple-transfer-dispatch", "evm-dispatch", "vm-call"]),
        new(
            "snapshots-rollback",
            "Top-level and preparation snapshots, CREATE admission, collision/revert/exception restoration, and commit/restore mode cleanup.",
            ["preparation-snapshot", "preparation-restore", "top-level-snapshot", "create-destination-classification", "create-state-gas-charge", "create-state-gas-restore", "create-collision-check", "collision-restore", "create-pay-value", "execution-restore", "deployment-restore", "finalize-restore-reset", "finalize-commit", "finalize-reset-transient"]),
        new(
            "fee-refund-receipt-settlement",
            "Refund and multidimensional block-gas accounting, fee settlement, final state commit/restore, and receipt tracing.",
            ["receipt-start", "simple-transfer-refund", "evm-refund", "pay-refund-call-failed-halt", "pay-refund-call", "simple-update-header-and-fees", "update-header-and-fees", "pay-fees-call", "simple-finalize", "finalize-transaction", "pay-fees", "pay-refund", "receipt-end"]),
    ];

    private static readonly AnchorDefinition[] SnapshotAnchors =
    [
        new("preparation-snapshot", "src/Nethermind/Nethermind.Evm/TransactionProcessing/TransactionProcessor.cs", "preExecutionSnapshot = WorldState.TakeSnapshot();", "TransactionProcessorBase.ExecuteEvmTransaction"),
        new("preparation-restore", "src/Nethermind/Nethermind.Evm/TransactionProcessing/TransactionProcessor.cs", "WorldState.Restore(preExecutionSnapshot);", "TransactionProcessorBase.ExecuteEvmTransaction"),
        new("top-level-snapshot", "src/Nethermind/Nethermind.Evm/TransactionProcessing/TransactionProcessor.cs", "Snapshot snapshot = WorldState.TakeSnapshot();", "TransactionProcessorBase.ExecuteEvmCall"),
        new("create-state-gas-restore", "src/Nethermind/Nethermind.Evm/TransactionProcessing/TransactionProcessor.cs", "WorldState.Restore(snapshot);", "TransactionProcessorBase.ExecuteEvmCall", 0),
        new("collision-restore", "src/Nethermind/Nethermind.Evm/TransactionProcessing/TransactionProcessor.cs", "WorldState.Restore(snapshot);", "TransactionProcessorBase.ExecuteEvmCall", 1),
        new("execution-restore", "src/Nethermind/Nethermind.Evm/TransactionProcessing/TransactionProcessor.cs", "WorldState.Restore(snapshot);", "TransactionProcessorBase.ExecuteEvmCall", 2),
        new("deployment-restore", "src/Nethermind/Nethermind.Evm/TransactionProcessing/TransactionProcessor.cs", "WorldState.Restore(snapshot);", "TransactionProcessorBase.ExecuteEvmCall", 3),
        new("finalize-restore-reset", "src/Nethermind/Nethermind.Evm/TransactionProcessing/TransactionProcessor.cs", "WorldState.Reset(resetBlockChanges: false);", "TransactionProcessorBase.FinalizeTransaction", 1),
        new("finalize-commit", "src/Nethermind/Nethermind.Evm/TransactionProcessing/TransactionProcessor.cs", "WorldState.Commit(spec, tracer.IsTracingState ? tracer : NullStateTracer.Instance, commitRoots: !spec.IsEip658Enabled);", "TransactionProcessorBase.FinalizeTransaction"),
        new("finalize-reset-transient", "src/Nethermind/Nethermind.Evm/TransactionProcessing/TransactionProcessor.cs", "WorldState.ResetTransient();", "TransactionProcessorBase.FinalizeTransaction"),
    ];

    private static readonly BlockPhaseDefinition[] BlockPhases =
    [
        new(
            "suggested-block validation",
            "Pre-processing checks on the suggested block before branch execution.",
            [new("suggested-checks", "src/Nethermind/Nethermind.Consensus/Processing/BlockchainProcessor.cs", "if (!RunSimpleChecksAheadOfProcessing(suggestedBlock, options))", "BlockchainProcessor.Process")]),
        new(
            "synchronous branch selection and preparation",
            "The synchronous BlockchainProcessor path decides whether work should run and prepares the selected branch before its blocks are preprocessed.",
            [
                new("blockchain-should-process", "src/Nethermind/Nethermind.Consensus/Processing/BlockchainProcessor.cs", "suggestedBlock.IsGenesis", "BlockchainProcessor.Process"),
                new("blockchain-prepare-branch", "src/Nethermind/Nethermind.Consensus/Processing/BlockchainProcessor.cs", "using ProcessingBranch processingBranch = PrepareProcessingBranch(suggestedBlock, options);", "BlockchainProcessor.Process"),
                new("blockchain-prepare-blocks", "src/Nethermind/Nethermind.Consensus/Processing/BlockchainProcessor.cs", "PrepareBlocksToProcess(suggestedBlock, options, processingBranch);", "BlockchainProcessor.Process"),
            ]),
        new(
            "sender and EIP-7702 authority recovery",
            "PrepareBlocksToProcess invokes the registered block preprocessor for every block selected for synchronous branch execution.",
            [
                new("preprocess", "src/Nethermind/Nethermind.Consensus/Processing/BlockchainProcessor.cs", "for (int i = 0; i < _preprocessorSteps.Count; i++)", "BlockchainProcessor.Preprocess"),
                new("preprocess-selected-block", "src/Nethermind/Nethermind.Consensus/Processing/BlockchainProcessor.cs", "Preprocess(blocksToProcess[i]);", "BlockchainProcessor.PrepareBlocksToProcess"),
                new("recover-data", "src/Nethermind/Nethermind.Consensus/Processing/RecoverSignatures.cs", "public void RecoverData(Block block)", "RecoverSignatures.RecoverData"),
                new("di-recover-signatures", "src/Nethermind/Nethermind.Init/Modules/BlockProcessingModule.cs", ".AddFirst<IBlockPreprocessorStep, RecoverSignatures>()", "BlockProcessingModule.Load"),
            ]),
        new(
            "open world-state scope at parent root",
            "Branch processing opens the world-state scope rooted at the branch parent.",
            [new("begin-scope", "src/Nethermind/Nethermind.Consensus/Processing/BranchProcessor.cs", "worldStateCloser = stateProvider.BeginScope(baseBlock);", "BranchProcessor.Process")]),
        new(
            "DAO transition when applicable",
            "The standard block processor applies the DAO irregular state transition when the fork block matches.",
            [
                new("bal-prepare", "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.cs", "_balManager.PrepareForProcessing(suggestedBlock, spec, options);", "BlockProcessor.ProcessOne"),
                new("dao-call", "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.cs", "ApplyDaoTransition(suggestedBlock);", "BlockProcessor.ProcessOne"),
                new("block-preparation", "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.cs", "Block block = PrepareBlockForProcessing(suggestedBlock);", "BlockProcessor.ProcessOne"),
                new("dao-implementation", "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.std.cs", "private partial void ApplyDaoTransition(Block block)", "BlockProcessor.ApplyDaoTransition"),
            ]),
        new(
            "beacon-root system call",
            "Pre-transaction system state transition for the parent beacon block root.",
            [
                new("block-trace-start", "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.cs", "ReceiptsTracer.StartNewBlockTrace(block);", "BlockProcessor.ProcessBlock"),
                new("executor-context", "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.cs", "_blockTransactionsExecutor.SetBlockExecutionContext(CreateBlockExecutionContext(block.Header, spec));", "BlockProcessor.ProcessBlock"),
                new("bal-setup", "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.cs", "_balManager.Setup(block);", "BlockProcessor.ProcessBlock"),
                new("beacon-root", "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.cs", "_systemContractHandler.StoreBeaconRoot(block, spec, NullTxTracer.Instance);", "BlockProcessor.ProcessBlock"),
                new("blockhash-state", "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.cs", "_systemContractHandler.ApplyBlockhashStateChanges(header, spec);", "BlockProcessor.ProcessBlock"),
                new("pre-system-commit", "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.cs", "CommitState(spec);", "BlockProcessor.ProcessBlock", 0),
            ]),
        new(
            "historical blockhash state change",
            "Pre-transaction historical blockhash state transition.",
            [new("blockhash-state-phase", "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.cs", "_systemContractHandler.ApplyBlockhashStateChanges(header, spec);", "BlockProcessor.ProcessBlock")]),
        new(
            "user transaction fold",
            "The registered executor folds transactions through the sequential adapter path; the BAL decorator may select a separate parallel path.",
            [
                new("executor-dispatch", "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.cs", "TxReceipt[] receipts = _blockTransactionsExecutor.ProcessTransactions(block, options, ReceiptsTracer, token);", "BlockProcessor.ProcessBlock"),
                new("sequential-loop", "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.BlockValidationTransactionsExecutor.cs", "for (int i = 0; i < block.Transactions.Length; i++)", "BlockValidationTransactionsExecutor.ProcessTransactions"),
                new("sequential-transaction", "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.BlockValidationTransactionsExecutor.cs", "result = transactionProcessor.ProcessTransaction(currentTx, receiptsTracer, processingOptions, _stateProvider);", "BlockValidationTransactionsExecutor.ProcessTransaction"),
                new("adapter-execute", "src/Nethermind/Nethermind.Evm/TransactionProcessing/ExecuteTransactionProcessorAdapter.cs", "transactionProcessor.Execute(transaction, txTracer);", "ExecuteTransactionProcessorAdapter.Execute"),
                new("transactions-executed", "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.cs", "TransactionsExecuted?.Invoke();", "BlockProcessor.ProcessBlock"),
                new("post-transaction-commit", "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.cs", "CommitState(spec);", "BlockProcessor.ProcessBlock", 1),
            ]),
        new(
            "blob gas and receipt root and bloom",
            "Post-transaction blob accounting and receipt/bloom derivation.",
            [
                new("blob-gas", "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.cs", "header.BlobGasUsed = BlobGasCalculator.CalculateBlobGas(block.Transactions);", "BlockProcessor.ProcessBlock"),
                new("background-receipts", "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.cs", "bloomsAndReceiptsRootTask = Task.Run(() =>", "BlockProcessor.ProcessBlock"),
                new("calculate-blooms", "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.cs", "CalculateBlooms(receipts);", "BlockProcessor.CalculateBlooms", 0),
                new("receipt-root", "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.cs", "header.ReceiptsRoot = CalculateReceiptsRoot(receipts, spec, block);", "BlockProcessor.ProcessBlock"),
                new("receipt-gas-accumulate", "src/Nethermind/Nethermind.Blockchain/Tracing/BlockReceiptsTracer.cs", "BlockReceiptGasAccountingResult accounting = BlockReceiptGasAccountingKernel.Accumulate(", "BlockReceiptsTracer.UpdateCumulativeGasTracking"),
                new("receipt-gas-header", "src/Nethermind/Nethermind.Blockchain/Tracing/BlockReceiptsTracer.cs", "Block.Header.GasUsed = accounting.HeaderGasUsed;", "BlockReceiptsTracer.UpdateCumulativeGasTracking", 0),
                new("receipt-gas-restore", "src/Nethermind/Nethermind.Blockchain/Tracing/BlockReceiptsTracer.cs", "BlockReceiptGasAccountingResult accounting = BlockReceiptGasAccountingKernel.FromTotals(", "BlockReceiptsTracer.Restore"),
                new("receipt-gas-restore-header", "src/Nethermind/Nethermind.Blockchain/Tracing/BlockReceiptsTracer.cs", "Block.Header.GasUsed = accounting.HeaderGasUsed;", "BlockReceiptsTracer.Restore", 1),
                new("receipt-gas-kernel", "src/Nethermind/Nethermind.Blockchain/Tracing/BlockReceiptGasAccountingKernel.cs", "public static BlockReceiptGasAccountingResult Accumulate(", "BlockReceiptGasAccountingKernel.Accumulate"),
            ]),
        new(
            "rewards",
            "Block rewards are applied after transaction execution and receipt derivation.",
            [new("miner-rewards", "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.cs", "ApplyMinerRewards(block, blockTracer, spec);", "BlockProcessor.ProcessBlock")]),
        new(
            "withdrawals",
            "Consensus withdrawals are processed after rewards.",
            [
                new("withdrawals", "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.cs", "_systemContractHandler.ProcessWithdrawals(block, spec);", "BlockProcessor.ProcessBlock"),
                new("withdrawal-commit", "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.cs", "CommitState(spec);", "BlockProcessor.ProcessBlock", 2),
            ]),
        new(
            "execution requests and system calls",
            "Execution requests and their system-call state transitions are applied after withdrawals.",
            [
                new("execution-requests", "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.cs", "_systemContractHandler.ProcessExecutionRequests(block, _stateProvider, receipts, spec);", "BlockProcessor.ProcessBlock"),
                new("system-processor", "src/Nethermind/Nethermind.Evm/TransactionProcessing/SystemTransactionProcessor.cs", "protected override TransactionResult Execute(Transaction tx, ITxTracer tracer, ExecutionOptions opts)", "SystemTransactionProcessor.Execute"),
                new("end-block-trace", "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.cs", "ReceiptsTracer.EndBlockTrace(accumulateBlockBloom: bloomsAndReceiptsRootTask is null);", "BlockProcessor.ProcessBlock"),
            ]),
        new(
            "storage and state roots",
            "The final state/storage commit is followed by state-root calculation for the processed header.",
            [
                new("commit-roots", "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.cs", "CommitStateAndStorageRoots(spec);", "BlockProcessor.CommitStateAndStorageRoots"),
                new("eip161-reap-empty-accounts", "src/Nethermind/Nethermind.State/WorldState.cs", "ReapEmptyAccounts();", "WorldState.Commit"),
                new("persistent-storage-commit", "src/Nethermind/Nethermind.State/WorldState.cs", "_persistentStorageProvider.Commit(tracer);", "WorldState.Commit"),
                new("account-changes", "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.cs", "SetAccountChanges(block);", "BlockProcessor.ProcessBlock"),
                new("state-root", "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.cs", "ComputeStateRoot(header);", "BlockProcessor.ComputeStateRoot"),
            ]),
        new(
            "block access list",
            "The generated block access list is finalized after state and header-derived roots.",
            [new("set-bal", "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.cs", "_balManager.SetBlockAccessList(block);", "BlockProcessor.ProcessBlock")]),
        new(
            "processed-header validation",
            "The processed header and receipts are checked against the unchanged suggested header.",
            [
                new("process-failure-cleanup", "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.cs", "if (!processed) block.DisposeAccountChanges();", "BlockProcessor.ProcessOne"),
                new("processed-validation-call", "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.cs", "ValidateProcessedBlock(suggestedBlock, options, block, receipts);", "BlockProcessor.ProcessOne"),
                new("processed-validation-failure", "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.cs", "throw new InvalidBlockException(suggestedBlock, error);", "BlockProcessor.ValidateProcessedBlock"),
                new("processed-validation", "src/Nethermind/Nethermind.Consensus/Validators/BlockValidator.cs", "public bool ValidateProcessedBlock(Block processedBlock, TxReceipt[] receipts, Block suggestedBlock, out string? error)", "BlockValidator.ValidateProcessedBlock"),
                new("store-tx-receipts", "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.cs", "StoreTxReceipts(block, receipts, spec);", "BlockProcessor.ProcessOne"),
            ]),
        new(
            "CommitTree invocation",
            "Production CommitTree call boundary after block processing and validation; this inventory does not prove durable database persistence.",
            [
                new("branch-block-entrypoint", "src/Nethermind/Nethermind.Consensus/Processing/BranchProcessor.cs", "(processedBlock, receipts) = blockProcessor.ProcessOne(suggestedBlock, blockOptions, blockTracer, spec, token);", "BranchProcessor.Process"),
                new("sequential-retry-catch", "src/Nethermind/Nethermind.Consensus/Processing/BranchProcessor.cs", "catch (BlockProcessor.BlockAccessListSequentialRetryException) when (", "BranchProcessor.Process"),
                new("retry-discard-scope", "src/Nethermind/Nethermind.Consensus/Processing/BranchProcessor.cs", "worldStateCloser.Dispose();", "BranchProcessor.Process"),
                new("retry-parent-scope", "src/Nethermind/Nethermind.Consensus/Processing/BranchProcessor.cs", "worldStateCloser = stateProvider.BeginScope(preBlockBaseBlock);", "BranchProcessor.Process"),
                new("retry-block-entrypoint", "src/Nethermind/Nethermind.Consensus/Processing/BranchProcessor.cs", "(processedBlock, receipts) = blockProcessor.ProcessOne(suggestedBlock, retryOptions, blockTracer, spec, token);", "BranchProcessor.Process"),
                new("inclusion-signal-start", "src/Nethermind/Nethermind.Consensus/Processing/BranchProcessor.cs", "bool inclusionListSatisfied = !checkInclusionList", "BranchProcessor.Process"),
                new("inclusion-signal-query", "src/Nethermind/Nethermind.Consensus/Processing/BranchProcessor.cs", "|| inclusionListSatisfactionChecker.IsSatisfied(processedBlock, suggestedBlock, stateProvider);", "BranchProcessor.Process"),
                new("processed-inclusion-signal", "src/Nethermind/Nethermind.Consensus/Processing/BranchProcessor.cs", "processedBlock.IsInclusionListSatisfied = inclusionListSatisfied;", "BranchProcessor.Process"),
                new("suggested-inclusion-signal", "src/Nethermind/Nethermind.Consensus/Processing/BranchProcessor.cs", "suggestedBlock.IsInclusionListSatisfied = inclusionListSatisfied;", "BranchProcessor.Process"),
                new("successful-prewarm-queue", "src/Nethermind/Nethermind.Consensus/Processing/BranchProcessor.cs", "QueueClearCaches(preWarmTask);", "BranchProcessor.Process", 1),
                new("successful-prewarm-wait", "src/Nethermind/Nethermind.Consensus/Processing/BranchProcessor.cs", "WaitAndClear(ref preWarmTask);", "BranchProcessor.Process", 1),
                new("pre-commit-call", "src/Nethermind/Nethermind.Consensus/Processing/BranchProcessor.cs", "PreCommitBlock(suggestedBlock.Header);", "BranchProcessor.Process"),
                new("successful-prefix-count", "src/Nethermind/Nethermind.Consensus/Processing/BranchProcessor.cs", "processedBlocksCount = i + 1;", "BranchProcessor.Process"),
                new("periodic-scope-dispose", "src/Nethermind/Nethermind.Consensus/Processing/BranchProcessor.cs", "worldStateCloser?.Dispose();", "BranchProcessor.Process", 0),
                new("periodic-scope-reopen", "src/Nethermind/Nethermind.Consensus/Processing/BranchProcessor.cs", "worldStateCloser = stateProvider.BeginScope(previousBranchStateRoot);", "BranchProcessor.Process"),
                new("successful-block-reset", "src/Nethermind/Nethermind.Consensus/Processing/BranchProcessor.cs", "stateProvider.Reset();", "BranchProcessor.Process"),
                new("branch-failure-catch", "src/Nethermind/Nethermind.Consensus/Processing/BranchProcessor.cs", "catch (Exception ex) // try to restore at all cost", "BranchProcessor.Process"),
                new("final-scope-dispose", "src/Nethermind/Nethermind.Consensus/Processing/BranchProcessor.cs", "worldStateCloser?.Dispose();", "BranchProcessor.Process", 1),
                new("commit-tree", "src/Nethermind/Nethermind.Consensus/Processing/BranchProcessor.cs", "stateProvider.CommitTree(block.Number);", "BranchProcessor.PreCommitBlock"),
            ],
            "CommitTree invocation"),
        new(
            "synchronous result classification and head/processed-chain finalization",
            "BlockchainProcessor classifies an invalid branch result and, after success, propagates total difficulty and conditionally updates and marks the main chain.",
            [
                new("blockchain-process-branch", "src/Nethermind/Nethermind.Consensus/Processing/BlockchainProcessor.cs", "Block[]? processedBlocks = ProcessBranch(processingBranch, options, tracer, token, out error);", "BlockchainProcessor.Process"),
                new("blockchain-invalid-catch", "src/Nethermind/Nethermind.Consensus/Processing/BlockchainProcessor.cs", "catch (InvalidBlockException ex)", "BlockchainProcessor.ProcessBranch", 1),
                new("blockchain-delete-invalid", "src/Nethermind/Nethermind.Consensus/Processing/BlockchainProcessor.cs", "DeleteInvalidBlocks(in processingBranch, invalidBlockHash);", "BlockchainProcessor.ProcessBranch"),
                new("blockchain-total-difficulty", "src/Nethermind/Nethermind.Consensus/Processing/BlockchainProcessor.cs", "lastProcessed.Header.TotalDifficulty = suggestedBlock.TotalDifficulty;", "BlockchainProcessor.Process"),
                new("blockchain-update-main-chain", "src/Nethermind/Nethermind.Consensus/Processing/BlockchainProcessor.cs", "_blockTree.TryUpdateMainChain(suggestedBlock.Header, wereProcessed: true, preloadedBlocks: processingBranch.Blocks.AsSpan())", "BlockchainProcessor.Process"),
                new("blockchain-mark-processed", "src/Nethermind/Nethermind.Consensus/Processing/BlockchainProcessor.cs", "_blockTree.MarkChainAsProcessed(processingBranch.Blocks);", "BlockchainProcessor.Process"),
            ]),
    ];

    private static readonly AnchorDefinition[] EntryPointAnchors =
    [
        new("di-transaction", "src/Nethermind/Nethermind.Init/Modules/BlockProcessingModule.cs", ".AddScoped<ITransactionProcessor, EthereumTransactionProcessor>()", "BlockProcessingModule.Load"),
        new("di-block", "src/Nethermind/Nethermind.Init/Modules/BlockProcessingModule.cs", ".AddScoped<IBlockProcessor, BlockProcessor>()", "BlockProcessingModule.Load"),
        new("di-adapter-factory", "src/Nethermind/Nethermind.Init/Modules/BlockProcessingModule.cs", ".AddScoped<ITransactionProcessorAdapter, ITransactionProcessor, TransactionProcessorAdapterFactory>(", "BlockProcessingModule.Load"),
        new("di-main-context", "src/Nethermind/Nethermind.Init/Modules/BlockProcessingModule.cs", ".AddSingleton<IMainProcessingContext, MainProcessingContext>()", "BlockProcessingModule.Load"),
        new("main-context-type", "src/Nethermind/Nethermind.Init/Modules/MainProcessingContext.cs", "public class MainProcessingContext : IMainProcessingContext", "MainProcessingContext"),
        new("main-context-block-processor", "src/Nethermind/Nethermind.Init/Modules/MainProcessingContext.cs", "public IBlockProcessor BlockProcessor => _components.BlockProcessor;", "MainProcessingContext.BlockProcessor"),
        new("block-entrypoint", "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.cs", "public (Block Block, TxReceipt[] Receipts) ProcessOne(", "BlockProcessor.ProcessOne"),
        new("transaction-entrypoint", "src/Nethermind/Nethermind.Evm/TransactionProcessing/ITransactionProcessor.cs", "public TransactionResult Execute(Transaction transaction, ITxTracer txTracer)", "ITransactionProcessorExtensions.Execute"),
        new("sequential-executor-registration", "src/Nethermind/Nethermind.Init/Modules/BlockProcessingModule.cs", ".AddScoped<IBlockProcessor.IBlockTransactionsExecutor, BlockProcessor.BlockValidationTransactionsExecutor>()", "BlockProcessingModule.StandardBlockValidationModule"),
        new("parallel-executor-registration", "src/Nethermind/Nethermind.Init/Modules/BlockProcessingModule.cs", ".AddDecorator<IBlockProcessor.IBlockTransactionsExecutor, BlockProcessor.ParallelBlockValidationTransactionsExecutor>()", "BlockProcessingModule.StandardBlockValidationModule"),
        new("main-context-blockchain-scope", "src/Nethermind/Nethermind.Init/Modules/MainProcessingContext.cs", ".AddScoped<BlockchainProcessor, IBranchProcessor, IProcessingStats, IEnumerable<IBlockTracer>>(", "MainProcessingContext constructor"),
    ];

    private static readonly AnchorDefinition[] ParallelAnchors =
    [
        new("parallel-type", "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.ParallelBlockValidationTransactionsExecutor.cs", "public class ParallelBlockValidationTransactionsExecutor(", "ParallelBlockValidationTransactionsExecutor"),
        new("parallel-bal-gate", "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.ParallelBlockValidationTransactionsExecutor.cs", "if (!balManager.Enabled)", "ParallelBlockValidationTransactionsExecutor.ProcessTransactions"),
        new("parallel-selection", "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.ParallelBlockValidationTransactionsExecutor.cs", "ExecutionFlags.ParallelExecution && !block.IsGenesis && balManager.ParallelExecutionEnabled", "ParallelBlockValidationTransactionsExecutor.ProcessTransactions"),
        new("parallel-worker", "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.ParallelBlockValidationTransactionsExecutor.cs", "ProcessTransactionsParallel(block, processingOptions, receiptsTracer, token)", "ParallelBlockValidationTransactionsExecutor.ProcessTransactions"),
        new("parallel-bal-manager", "src/Nethermind/Nethermind.Init/Modules/BlockProcessingModule.cs", ".AddScoped<IBlockAccessListManager, BlockAccessListManager>()", "BlockProcessingModule.Load"),
    ];

    private static readonly ReflectionDefinition[] ReflectionDefinitions =
    [
        new(typeof(EthereumTransactionProcessor), "Process"),
        new(typeof(EthereumTransactionProcessor), "ExecuteCore"),
        new(typeof(EthereumTransactionProcessor), "RecoverSenderBeforeIntrinsicGas"),
        new(typeof(EthereumTransactionProcessor), "CalculateIntrinsicGas"),
        new(typeof(EthereumTransactionProcessor), "ValidateStatic"),
        new(typeof(EthereumTransactionProcessor), "ValidateGas"),
        new(typeof(EthereumTransactionProcessor), "RecoverSenderIfNeeded"),
        new(typeof(EthereumTransactionProcessor), "BuyGas"),
        new(typeof(EthereumTransactionProcessor), "IncrementNonce"),
        new(typeof(EthereumTransactionProcessor), "ProcessDelegations"),
        new(typeof(EthereumTransactionProcessor), "BuildExecutionEnvironment"),
        new(typeof(EthereumTransactionProcessor), "ExecuteSimpleTransfer"),
        new(typeof(EthereumTransactionProcessor), "ExecuteEvmTransaction"),
        new(typeof(EthereumTransactionProcessor), "ExecuteEvmCall"),
        new(typeof(EthereumTransactionProcessor), "Refund"),
        new(typeof(EthereumTransactionProcessor), "UpdateHeaderGasUsedAndPayFees"),
        new(typeof(EthereumTransactionProcessor), "PayFees"),
        new(typeof(EthereumTransactionProcessor), "PayRefund"),
        new(typeof(EthereumTransactionProcessor), "FinalizeTransaction"),
        new(typeof(ITransactionProcessorExtensions), "Execute"),
        new(typeof(ExecuteTransactionProcessorAdapter), "Execute"),
        new(typeof(SystemTransactionProcessor<>), "Execute"),
        new(typeof(BlockProcessor), "ProcessOne"),
        new(typeof(BlockProcessor), "ProcessBlock"),
        new(typeof(BlockProcessor), "ValidateProcessedBlock"),
        new(typeof(BlockProcessor), "PrepareBlockForProcessing"),
        new(typeof(BlockProcessor), "CommitStateAndStorageRoots"),
        new(typeof(BlockProcessor), "ComputeStateRoot"),
        new(typeof(BlockProcessor.BlockValidationTransactionsExecutor), "ProcessTransactions"),
        new(typeof(BlockProcessor.ParallelBlockValidationTransactionsExecutor), "ProcessTransactions"),
        new(typeof(BlockchainProcessor), "Process"),
        new(typeof(BranchProcessor), "Process"),
        new(typeof(BranchProcessor), "PreCommitBlock"),
        new(typeof(BlockValidator), "ValidateProcessedBlock"),
        new(typeof(IWorldState), "CommitTree"),
    ];

    public static int Run(string? repositoryRoot, string? outputPath, TextWriter output, TextWriter error)
    {
        try
        {
            string root = ResolveRepositoryRoot(repositoryRoot);
            ProcessingCoverageDocument document = BuildDocument(root);
            string json = JsonSerializer.Serialize(document, JsonOptions) + JsonNewLine;
            json = json.Replace("\r\n", JsonNewLine, StringComparison.Ordinal).Replace('\r', '\n');

            if (outputPath is null)
            {
                output.Write(json);
            }
            else
            {
                string fullOutputPath = Path.GetFullPath(outputPath);
                string parent = Path.GetDirectoryName(fullOutputPath)
                    ?? throw new InvalidOperationException($"Processing coverage output has no parent: {outputPath}");
                Directory.CreateDirectory(parent);
                File.WriteAllText(fullOutputPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }

            return 0;
        }
        catch (Exception exception)
        {
            error.WriteLine($"formal processing-coverage: {exception.Message}");
            return 1;
        }
    }

    private static string ResolveRepositoryRoot(string? repositoryRoot)
    {
        string root = repositoryRoot is null
            ? Directory.GetCurrentDirectory()
            : Path.GetFullPath(repositoryRoot);

        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"Repository root does not exist: {root}");

        string manifestPath = GetRepositoryPath(root, VerificationManifestRelativePath);
        if (!File.Exists(manifestPath))
            throw new FileNotFoundException($"Pinned verification manifest is missing: {manifestPath}");

        return root;
    }

    private static ProcessingCoverageDocument BuildDocument(string repositoryRoot)
    {
        PinnedConfiguration pinned = ReadPinnedConfiguration(repositoryRoot);
        Dictionary<string, SourceText> sourceTexts = LoadSources(repositoryRoot);

        Dictionary<string, SourceAnchor> anchors = new(StringComparer.Ordinal);
        AddAnchors(anchors, sourceTexts, EntryPointAnchors);
        AddAnchors(anchors, sourceTexts, ParallelAnchors);
        AddAnchors(anchors, sourceTexts, SnapshotAnchors);
        foreach (BlockPhaseDefinition phase in BlockPhases)
            AddAnchors(anchors, sourceTexts, phase.Anchors);
        AddAnchors(anchors, sourceTexts, TransactionAnchors);

        TransactionPipeline transaction = BuildTransactionPipeline(anchors);
        BlockPipeline block = BuildBlockPipeline(anchors, pinned.RequiredBlockOrder);
        ReflectionInventory reflection = BuildReflectionInventory();
        EntryPointInventory entryPoints = BuildEntryPoints(anchors, reflection);
        ExecutionModeInventory executionModes = BuildExecutionModes(anchors, reflection);

        CoverageChecks checks = new(
            entryPoints.DiRegistrationsAnchored,
            entryPoints.SequentialEntrypointAnchored,
            transaction.PhaseOrderValidated,
            block.RequiredManifestOrderValidated,
            block.ProcessOneOrderValidated,
            block.ProcessBlockOrderValidated,
            block.CommitTreeAnchored,
            block.BranchOrderValidated,
            block.SequentialRetryAnchored,
            block.SuccessfulPrefixAnchored,
            block.InclusionSignalAnchored,
            block.ScopeLifecycleAnchored,
            executionModes.ParallelBalPathRegistered,
            executionModes.ParallelEquivalenceProved);

        EnsureChecksPass(checks);

        List<SourceFile> sourceFiles = [];
        foreach (KeyValuePair<string, SourceText> source in sourceTexts)
        {
            sourceFiles.Add(new SourceFile(source.Key, source.Value.Sha256));
        }

        sourceFiles.Sort(static (left, right) => string.CompareOrdinal(left.Path, right.Path));

        return new ProcessingCoverageDocument(
            CurrentSchemaVersion,
            "standard-mainnet-processing-reachability-and-order-inventory",
            "reachability-and-order-inventory-only",
            new TargetConfiguration(pinned.Chain, pinned.Release, pinned.Activation, pinned.Eip7778Enabled, pinned.NethermindCommit),
            entryPoints,
            transaction,
            block,
            executionModes,
            new InventoryCounts(transaction.PhaseCount, transaction.StepCount, block.PhaseCount, sourceFiles.Count, reflection.MethodCount),
            sourceFiles.ToArray(),
            checks,
            [
                "This artifact is a source-anchored reachability and order inventory, not a semantic or formal proof of transaction or block processing.",
                "The standard-mainnet target follows the pinned Amsterdam configuration at synthetic timestamp 0 / block 0; it is not a claim about currently scheduled live-mainnet activation.",
                "The sequential route is anchored through the production DI registrations and the BAL decorator fallback. Plugin replacements and use-case-specific child scopes are outside this standard registration inventory.",
                "The registered parallel/BAL executor is identified separately, but equivalence to the sequential block relation is intentionally unproved.",
                "The CommitTree phase is an invocation anchor at the production call boundary; trie/database durability is not proved.",
                "EVM opcode/frame/precompile semantics, cryptographic recovery correctness, trie/database durability, CLR/JIT behavior, and state-root implementation correctness remain outside this gate.",
            ]);
    }

    private static void EnsureChecksPass(CoverageChecks checks)
    {
        if (!checks.DiRegistrationsAnchored ||
            !checks.SequentialEntrypointAnchored ||
            !checks.TransactionPhaseOrderValidated ||
            !checks.RequiredManifestBlockOrderValidated ||
            !checks.ProcessOneOrderValidated ||
            !checks.ProcessBlockOrderValidated ||
            !checks.CommitTreeAnchored ||
            !checks.BranchOrderValidated ||
            !checks.SequentialRetryAnchored ||
            !checks.SuccessfulPrefixAnchored ||
            !checks.InclusionSignalAnchored ||
            !checks.ScopeLifecycleAnchored ||
            !checks.ParallelBalPathRegistered)
        {
            throw new InvalidOperationException("Standard-mainnet processing coverage checks did not pass.");
        }
    }

    private static PinnedConfiguration ReadPinnedConfiguration(string repositoryRoot)
    {
        string manifestPath = GetRepositoryPath(repositoryRoot, VerificationManifestRelativePath);
        using JsonDocument document = JsonDocument.Parse(ReadRequiredFile(manifestPath));
        JsonElement root = document.RootElement;
        if (root.GetProperty("schemaVersion").GetInt32() != CurrentSchemaVersion)
            throw new InvalidOperationException("Unsupported verification manifest schema version.");

        JsonElement pins = root.GetProperty("pins");
        string commit = pins.GetProperty("nethermindCommit").GetString() ?? string.Empty;
        if (!System.Text.RegularExpressions.Regex.IsMatch(commit, "^[0-9a-f]{40}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            throw new InvalidOperationException("Verification manifest has an invalid Nethermind commit pin.");

        JsonElement fork = pins.GetProperty("fork");
        string chain = fork.GetProperty("chain").GetString() ?? string.Empty;
        string release = fork.GetProperty("release").GetString() ?? string.Empty;
        string activation = fork.GetProperty("activation").GetString() ?? string.Empty;
        bool eip7778 = fork.GetProperty("eip7778Enabled").GetBoolean();
        if (!string.Equals(chain, "mainnet", StringComparison.Ordinal) ||
            !string.Equals(release, "Amsterdam", StringComparison.Ordinal) ||
            !string.Equals(activation, "synthetic timestamp 0 / block 0", StringComparison.Ordinal) ||
            !eip7778)
        {
            throw new InvalidOperationException("Processing coverage requires the pinned mainnet Amsterdam configuration with EIP-7778 enabled at synthetic timestamp 0 / block 0.");
        }

        JsonElement requiredOrder = root.GetProperty("requiredBlockOrder");
        if (requiredOrder.ValueKind is not JsonValueKind.Array)
            throw new InvalidOperationException("Verification manifest requiredBlockOrder must be an array.");

        List<string> manifestOrder = [];
        foreach (JsonElement item in requiredOrder.EnumerateArray())
        {
            if (item.ValueKind is not JsonValueKind.String)
                throw new InvalidOperationException("Verification manifest requiredBlockOrder contains a non-string entry.");
            manifestOrder.Add(item.GetString()!);
        }

        if (!AreEqual(manifestOrder, RequiredBlockOrder))
            throw new InvalidOperationException("Verification manifest requiredBlockOrder is missing, changed, or reordered.");

        return new PinnedConfiguration(chain, release, activation, eip7778, commit, manifestOrder.ToArray());
    }

    private static Dictionary<string, SourceText> LoadSources(string repositoryRoot)
    {
        HashSet<string> paths = new(StringComparer.Ordinal) { VerificationManifestRelativePath };
        AddPaths(paths, EntryPointAnchors);
        AddPaths(paths, ParallelAnchors);
        AddPaths(paths, SnapshotAnchors);
        AddPaths(paths, TransactionAnchors);
        foreach (BlockPhaseDefinition phase in BlockPhases)
            AddPaths(paths, phase.Anchors);

        Dictionary<string, SourceText> sources = new(StringComparer.Ordinal);
        foreach (string path in paths)
        {
            string fullPath = GetRepositoryPath(repositoryRoot, path);
            byte[] bytes = ReadRequiredBytes(fullPath);
            string text = Encoding.UTF8.GetString(bytes);
            sources.Add(path, new SourceText(text, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()));
        }

        return sources;
    }

    private static void AddPaths(HashSet<string> paths, IReadOnlyList<AnchorDefinition> definitions)
    {
        foreach (AnchorDefinition definition in definitions)
            paths.Add(definition.Path);
    }

    private static void AddAnchors(
        Dictionary<string, SourceAnchor> anchors,
        IReadOnlyDictionary<string, SourceText> sources,
        IReadOnlyList<AnchorDefinition> definitions)
    {
        foreach (AnchorDefinition definition in definitions)
        {
            if (anchors.ContainsKey(definition.Id))
                continue;

            if (!sources.TryGetValue(definition.Path, out SourceText? source))
                throw new InvalidOperationException($"Anchor {definition.Id} refers to an unloaded source: {definition.Path}");

            int index = FindOccurrence(source.Text, definition.Needle, definition.Occurrence);
            int line = 1;
            for (int i = 0; i < index; i++)
            {
                if (source.Text[i] == '\n') line++;
            }

            int lineStart = source.Text.LastIndexOf('\n', index) + 1;
            int lineEnd = source.Text.IndexOf('\n', index);
            if (lineEnd < 0) lineEnd = source.Text.Length;
            string sourceLine = source.Text[lineStart..lineEnd].Trim();
            if (sourceLine.StartsWith("//", StringComparison.Ordinal))
                throw new InvalidOperationException($"Anchor {definition.Id} resolved to a comment at {definition.Path}:{line}.");

            anchors.Add(definition.Id, new SourceAnchor(definition.Id, definition.Symbol, definition.Path, line, sourceLine));
        }
    }

    private static int FindOccurrence(string source, string needle, int occurrence)
    {
        int found = -1;
        int count = 0;
        int searchStart = 0;
        while (searchStart < source.Length)
        {
            int index = source.IndexOf(needle, searchStart, StringComparison.Ordinal);
            if (index < 0) break;
            found = index;
            if (occurrence >= 0 && count == occurrence)
                return index;
            count++;
            searchStart = index + needle.Length;
        }

        if (occurrence < 0 && count == 1)
            return found;
        if (occurrence >= 0 && count > occurrence)
            throw new InvalidOperationException($"Anchor occurrence {occurrence} for '{needle}' could not be selected deterministically.");
        throw new InvalidOperationException($"Required source anchor is missing or ambiguous: '{needle}' (matches: {count}).");
    }

    private static TransactionPipeline BuildTransactionPipeline(IReadOnlyDictionary<string, SourceAnchor> anchors)
    {
        List<PipelinePhase> phases = [];
        int phaseOrder = 1;
        int stepCount = 0;
        foreach (PhaseDefinition definition in TransactionPhases)
        {
            List<ProcessingStep> steps = [];
            int stepOrder = 1;
            foreach (string anchorId in definition.AnchorIds)
            {
                if (!anchors.TryGetValue(anchorId, out SourceAnchor? anchor) || anchor is null)
                    throw new InvalidOperationException($"Transaction phase {definition.Id} is missing anchor {anchorId}.");

                steps.Add(new ProcessingStep(stepOrder++, anchor.Symbol, [anchor]));
                stepCount++;
            }

            phases.Add(new PipelinePhase(phaseOrder++, definition.Id, definition.Description, steps.ToArray()));
        }

        ValidateTransactionOrder(anchors);
        ValidateSnapshotOrder(anchors);
        return new TransactionPipeline(phases.ToArray(), phases.Count, stepCount, true);
    }

    private static void ValidateTransactionOrder(IReadOnlyDictionary<string, SourceAnchor> anchors)
    {
        string[] order =
        [
            "process", "execute-core", "recover-sender-before-intrinsic", "calculate-intrinsic-gas", "validate-static",
            "effective-gas-price", "recover-sender", "validate-sender", "buy-gas", "increment-nonce",
            "prepare-simple-transfer", "commit-before-execution", "calculate-available-gas", "simple-transfer-dispatch",
            "evm-dispatch", "authorization-processing", "execution-environment", "vm-call", "update-header-and-fees",
            "finalize-transaction",
        ];
        ValidateOrderedAnchors(anchors, order, "transaction entrypoint");
        ValidateOrderedAnchors(anchors, ["simple-transfer-dispatch", "simple-transfer-refund", "simple-update-header-and-fees", "simple-finalize"], "simple-transfer settlement path");
        ValidateOrderedAnchors(anchors, ["evm-dispatch", "authorization-processing", "execution-environment", "vm-call", "update-header-and-fees", "finalize-transaction"], "EVM settlement path");
        ValidateOrderedAnchors(anchors, ["update-header-and-fees", "pay-fees-call"], "fee settlement implementation");
    }

    private static void ValidateSnapshotOrder(IReadOnlyDictionary<string, SourceAnchor> anchors)
    {
        ValidateOrderedAnchors(anchors, ["preparation-snapshot", "preparation-restore"], "authorization preparation rollback");
        ValidateOrderedAnchors(
            anchors,
            ["top-level-snapshot", "create-destination-classification", "create-state-gas-charge", "create-state-gas-restore", "create-collision-check", "collision-restore", "create-pay-value", "execution-restore", "deployment-restore"],
            "top-level CREATE admission and execution rollback");
    }

    private static void ValidateOrderedAnchors(IReadOnlyDictionary<string, SourceAnchor> anchors, IReadOnlyList<string> order, string name)
    {
        SourceAnchor? previous = null;
        foreach (string anchorId in order)
        {
            if (!anchors.TryGetValue(anchorId, out SourceAnchor? current))
                throw new InvalidOperationException($"{name} is missing anchor {anchorId}.");
            if (previous is not null && (!string.Equals(previous.Path, current.Path, StringComparison.Ordinal) || current.Line <= previous.Line))
                throw new InvalidOperationException($"{name} anchors are missing or reordered at {current.Path}:{current.Line}.");
            previous = current;
        }
    }

    private static BlockPipeline BuildBlockPipeline(IReadOnlyDictionary<string, SourceAnchor> anchors, IReadOnlyList<string> manifestOrder)
    {
        if (!AreEqual(manifestOrder, RequiredBlockOrder))
            throw new InvalidOperationException("Required block phase order differs from the pinned manifest.");

        Dictionary<string, BlockPhaseDefinition> definitions = new(StringComparer.Ordinal);
        foreach (BlockPhaseDefinition definition in BlockPhases)
        {
            string manifestId = definition.ManifestId ?? definition.Id;
            if (!definitions.TryAdd(manifestId, definition))
                throw new InvalidOperationException($"Duplicate block phase: {manifestId}");
        }

        List<PipelinePhase> phases = [];
        int order = 1;
        foreach (string phaseId in manifestOrder)
        {
            if (!definitions.TryGetValue(phaseId, out BlockPhaseDefinition? definition))
                throw new InvalidOperationException($"Required block phase is not implemented: {phaseId}");

            List<ProcessingStep> steps = [];
            int stepOrder = 1;
            foreach (AnchorDefinition anchorDefinition in definition.Anchors)
            {
                if (!anchors.TryGetValue(anchorDefinition.Id, out SourceAnchor? anchor))
                    throw new InvalidOperationException($"Block phase {phaseId} is missing anchor {anchorDefinition.Id}.");
                steps.Add(new ProcessingStep(stepOrder++, anchor.Symbol, [anchor]));
            }

            phases.Add(new PipelinePhase(order++, definition.Id, definition.Description, steps.ToArray()));
        }

        ValidateBlockProcessOneOrder(anchors);
        ValidateBlockProcessBlockOrder(anchors);
        ValidateWorldStateCommitOrder(anchors);
        ValidateReceiptAccountingOrder(anchors);
        ValidateBlockchainProcessOrder(anchors);
        ValidateBlockchainFailureOrder(anchors);
        ValidateBranchOrder(anchors);
        return new BlockPipeline(phases.ToArray(), manifestOrder.ToArray(), phases.Count, true, true, true, true, true, true, true, true, true);
    }

    private static void ValidateBlockProcessOneOrder(IReadOnlyDictionary<string, SourceAnchor> anchors) =>
        ValidateOrderedAnchors(anchors, ["bal-prepare", "dao-call", "block-preparation", "processed-validation-call", "store-tx-receipts"], "BlockProcessor.ProcessOne dispatch");

    private static void ValidateBlockProcessBlockOrder(IReadOnlyDictionary<string, SourceAnchor> anchors) =>
        ValidateOrderedAnchors(
            anchors,
            [
                "block-trace-start", "executor-context", "bal-setup", "beacon-root", "blockhash-state", "pre-system-commit",
                "executor-dispatch", "transactions-executed", "post-transaction-commit", "blob-gas", "background-receipts",
                "calculate-blooms", "receipt-root", "miner-rewards", "withdrawals", "withdrawal-commit", "execution-requests",
                "end-block-trace", "commit-roots", "account-changes", "state-root", "set-bal",
            ],
            "BlockProcessor.ProcessBlock body");

    private static void ValidateWorldStateCommitOrder(IReadOnlyDictionary<string, SourceAnchor> anchors) =>
        ValidateOrderedAnchors(
            anchors,
            ["eip161-reap-empty-accounts", "persistent-storage-commit"],
            "EIP-161 empty-account reaping before persistent storage commit");

    private static void ValidateBlockchainProcessOrder(IReadOnlyDictionary<string, SourceAnchor> anchors) =>
        ValidateOrderedAnchors(
            anchors,
            [
                "suggested-checks", "blockchain-should-process", "blockchain-prepare-branch", "blockchain-prepare-blocks",
                "blockchain-process-branch", "blockchain-total-difficulty", "blockchain-update-main-chain", "blockchain-mark-processed",
            ],
            "BlockchainProcessor synchronous success path");

    private static void ValidateReceiptAccountingOrder(IReadOnlyDictionary<string, SourceAnchor> anchors) =>
        ValidateOrderedAnchors(
            anchors,
            ["receipt-gas-accumulate", "receipt-gas-header", "receipt-gas-restore", "receipt-gas-restore-header"],
            "BlockReceiptsTracer accumulation and restore adapters");

    private static void ValidateBlockchainFailureOrder(IReadOnlyDictionary<string, SourceAnchor> anchors) =>
        ValidateOrderedAnchors(
            anchors,
            ["blockchain-invalid-catch", "blockchain-delete-invalid"],
            "BlockchainProcessor invalid-block failure path");

    private static void ValidateBranchOrder(IReadOnlyDictionary<string, SourceAnchor> anchors) =>
        ValidateOrderedAnchors(
            anchors,
            [
                "begin-scope", "branch-block-entrypoint", "sequential-retry-catch", "retry-discard-scope",
                "retry-parent-scope", "retry-block-entrypoint", "inclusion-signal-start", "inclusion-signal-query",
                "processed-inclusion-signal", "suggested-inclusion-signal", "successful-prewarm-queue",
                "successful-prewarm-wait", "pre-commit-call", "successful-prefix-count", "periodic-scope-dispose",
                "periodic-scope-reopen", "successful-block-reset", "branch-failure-catch", "final-scope-dispose",
            ],
            "BranchProcessor state scope, retry, successful prefix, and failure path");

    private static bool AreEqual(IReadOnlyList<string> actual, IReadOnlyList<string> expected)
    {
        if (actual.Count != expected.Count)
            return false;

        for (int i = 0; i < expected.Count; i++)
        {
            if (!string.Equals(actual[i], expected[i], StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    private static EntryPointInventory BuildEntryPoints(IReadOnlyDictionary<string, SourceAnchor> anchors, ReflectionInventory reflection)
    {
        SourceAnchor[] registrationAnchors =
        [
            anchors["di-transaction"],
            anchors["di-block"],
            anchors["di-adapter-factory"],
            anchors["di-main-context"],
            anchors["sequential-executor-registration"],
            anchors["parallel-executor-registration"],
            anchors["parallel-bal-manager"],
        ];

        List<DiRegistration> registrations =
        [
            new("ITransactionProcessor", "EthereumTransactionProcessor", "scoped", "service", anchors["di-transaction"]),
            new("IBlockProcessor", "BlockProcessor", "scoped", "service", anchors["di-block"]),
            new("ITransactionProcessorAdapter", "ExecuteTransactionProcessorAdapter via TransactionProcessorAdapterFactory", "scoped", "factory", anchors["di-adapter-factory"]),
            new("IMainProcessingContext", "MainProcessingContext", "singleton", "service", anchors["di-main-context"]),
            new("IBlockTransactionsExecutor", "BlockValidationTransactionsExecutor", "scoped", "service", anchors["sequential-executor-registration"]),
            new("IBlockTransactionsExecutor", "ParallelBlockValidationTransactionsExecutor", "scoped decorator", "decorator", anchors["parallel-executor-registration"]),
            new("IBlockAccessListManager", "BlockAccessListManager", "scoped", "service", anchors["parallel-bal-manager"]),
        ];

        bool semanticEntrypoints = reflection.Contains("BlockProcessor", "ProcessOne") &&
            reflection.Contains("ITransactionProcessorExtensions", "Execute") &&
            reflection.Contains("EthereumTransactionProcessor", "Process");
        bool sequentialEntrypoint = semanticEntrypoints &&
            reflection.Contains("BlockValidationTransactionsExecutor", "ProcessTransactions") &&
            reflection.Contains("ExecuteTransactionProcessorAdapter", "Execute");
        return new EntryPointInventory(
            new DiInventory(registrations.ToArray(), registrationAnchors),
            new Entrypoint("MainProcessingContext.BlockProcessor", anchors["main-context-block-processor"], semanticEntrypoints),
            new Entrypoint("BlockProcessor.ProcessOne", anchors["block-entrypoint"], reflection.Contains("BlockProcessor", "ProcessOne")),
            new Entrypoint("ITransactionProcessor.Execute", anchors["transaction-entrypoint"], reflection.Contains("ITransactionProcessorExtensions", "Execute")),
            true,
            sequentialEntrypoint);
    }

    private static ExecutionModeInventory BuildExecutionModes(IReadOnlyDictionary<string, SourceAnchor> anchors, ReflectionInventory reflection)
    {
        SourceAnchor[] modeAnchors =
        [
            anchors["parallel-executor-registration"],
            anchors["parallel-type"],
            anchors["parallel-bal-gate"],
            anchors["parallel-selection"],
            anchors["parallel-worker"],
            anchors["parallel-bal-manager"],
        ];

        return new ExecutionModeInventory(
            new ParallelMode(
                "registered-parallel-bal",
                "BlockProcessor.ParallelBlockValidationTransactionsExecutor",
                "selected when ExecutionFlags.ParallelExecution && !block.IsGenesis && balManager.ParallelExecutionEnabled; otherwise the decorator delegates to the sequential inner executor",
                modeAnchors,
                reflection.Contains("ParallelBlockValidationTransactionsExecutor", "ProcessTransactions"),
                "unproved"),
            true,
            false);
    }

    private static ReflectionInventory BuildReflectionInventory()
    {
        List<ReflectedMethod> methods = [];
        foreach (ReflectionDefinition definition in ReflectionDefinitions)
        {
            MethodInfo method = FindMethod(definition.Type, definition.MethodName)
                ?? throw new InvalidOperationException($"Semantic reflection could not resolve {definition.Type.FullName}.{definition.MethodName}.");
            methods.Add(new ReflectedMethod(
                definition.Type.FullName ?? definition.Type.Name,
                definition.MethodName,
                method.DeclaringType?.FullName ?? definition.Type.FullName ?? definition.Type.Name,
                method.IsStatic,
                method.IsPublic,
                method.IsFamily,
                method.IsPrivate,
                method.GetParameters().Length));
        }

        return new ReflectionInventory(methods.ToArray());
    }

    private static MethodInfo? FindMethod(Type type, string name)
    {
        for (Type? current = type; current is not null; current = current.BaseType)
        {
            MethodInfo[] methods = current.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            foreach (MethodInfo method in methods)
            {
                if (string.Equals(method.Name, name, StringComparison.Ordinal))
                    return method;
            }
        }

        return null;
    }

    private static string GetRepositoryPath(string repositoryRoot, string relativePath)
    {
        string fullPath = Path.GetFullPath(Path.Combine(repositoryRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        string fullRoot = Path.GetFullPath(repositoryRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Processing coverage path escapes repository root: {relativePath}");
        return fullPath;
    }

    private static string ReadRequiredFile(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Required processing coverage source is missing: {path}");
        return File.ReadAllText(path, Encoding.UTF8);
    }

    private static byte[] ReadRequiredBytes(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Required processing coverage source is missing: {path}");
        return File.ReadAllBytes(path);
    }

    private sealed record AnchorDefinition(string Id, string Path, string Needle, string Symbol, int Occurrence = -1);

    private sealed record SourceText(string Text, string Sha256);

    private sealed record SourceAnchor(string Id, string Symbol, string Path, int Line, string Text);

    private sealed record PhaseDefinition(string Id, string Description, string[] AnchorIds);

    private sealed record BlockPhaseDefinition(string Id, string Description, AnchorDefinition[] Anchors, string? ManifestId = null);

    private sealed record ReflectionDefinition(Type Type, string MethodName);

    private sealed record PinnedConfiguration(string Chain, string Release, string Activation, bool Eip7778Enabled, string NethermindCommit, string[] RequiredBlockOrder);

    private sealed record TargetConfiguration(string Chain, string Release, string Activation, bool Eip7778Enabled, string NethermindCommit);

    private sealed record ProcessingCoverageDocument(
        int SchemaVersion,
        string Claim,
        string Scope,
        TargetConfiguration Target,
        EntryPointInventory EntryPoints,
        TransactionPipeline Transaction,
        BlockPipeline Block,
        ExecutionModeInventory ExecutionModes,
        InventoryCounts Counts,
        SourceFile[] Sources,
        CoverageChecks Checks,
        string[] Limitations);

    private sealed record EntryPointInventory(
        DiInventory Di,
        Entrypoint MainProcessingContextBlockProcessor,
        Entrypoint BlockEntrypoint,
        Entrypoint TransactionEntrypoint,
        bool DiRegistrationsAnchored,
        bool SequentialEntrypointAnchored);

    private sealed record DiInventory(DiRegistration[] Registrations, SourceAnchor[] RegistrationAnchors);

    private sealed record DiRegistration(string Service, string Implementation, string Lifetime, string Kind, SourceAnchor Source);

    private sealed record Entrypoint(string Name, SourceAnchor Source, bool SemanticReflectionResolved);

    private sealed record TransactionPipeline(PipelinePhase[] Phases, int PhaseCount, int StepCount, bool PhaseOrderValidated);

    private sealed record BlockPipeline(
        PipelinePhase[] Phases,
        string[] RequiredManifestOrder,
        int PhaseCount,
        bool RequiredManifestOrderValidated,
        bool ProcessOneOrderValidated,
        bool ProcessBlockOrderValidated,
        bool CommitTreeAnchored,
        bool BranchOrderValidated,
        bool SequentialRetryAnchored,
        bool SuccessfulPrefixAnchored,
        bool InclusionSignalAnchored,
        bool ScopeLifecycleAnchored);

    private sealed record PipelinePhase(int Order, string Name, string Description, ProcessingStep[] Steps);

    private sealed record ProcessingStep(int Order, string Name, SourceAnchor[] Sources);

    private sealed record ExecutionModeInventory(ParallelMode Parallel, bool ParallelBalPathRegistered, bool ParallelEquivalenceProved);

    private sealed record ParallelMode(string Name, string Implementation, string Selection, SourceAnchor[] Sources, bool SemanticReflectionResolved, string Equivalence);

    private sealed record ReflectionInventory(ReflectedMethod[] Methods)
    {
        public int MethodCount => Methods.Length;

        public bool Contains(string typeName, string methodName)
        {
            foreach (ReflectedMethod method in Methods)
            {
                if (method.Type.EndsWith(typeName, StringComparison.Ordinal) && string.Equals(method.Method, methodName, StringComparison.Ordinal))
                    return true;
            }

            return false;
        }
    }

    private sealed record ReflectedMethod(string Type, string Method, string DeclaringType, bool Static, bool Public, bool Family, bool Private, int ParameterCount);

    private sealed record InventoryCounts(int TransactionPhases, int TransactionSteps, int BlockPhases, int SourceFiles, int ReflectedMethods);

    private sealed record SourceFile(string Path, string Sha256);

    private sealed record CoverageChecks(
        bool DiRegistrationsAnchored,
        bool SequentialEntrypointAnchored,
        bool TransactionPhaseOrderValidated,
        bool RequiredManifestBlockOrderValidated,
        bool ProcessOneOrderValidated,
        bool ProcessBlockOrderValidated,
        bool CommitTreeAnchored,
        bool BranchOrderValidated,
        bool SequentialRetryAnchored,
        bool SuccessfulPrefixAnchored,
        bool InclusionSignalAnchored,
        bool ScopeLifecycleAnchored,
        bool ParallelBalPathRegistered,
        bool ParallelEquivalenceProved);
}
