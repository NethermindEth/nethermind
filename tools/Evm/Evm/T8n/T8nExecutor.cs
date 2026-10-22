// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Ethereum.Test.Base;
using Evm.T8n.Errors;
using Evm.T8n.JsonTypes;
using Nethermind.Blockchain.BeaconBlockRoot;
using Nethermind.Blockchain.Blocks;
using Nethermind.Consensus.ExecutionRequests;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Validators;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Db;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Blockchain.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.Blockchain;
using Nethermind.State;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using Testably.Abstractions;

namespace Evm.T8n;

public static class T8nExecutor
{
    private static readonly ILogManager _logManager = LimboLogs.Instance;

    /// <remarks>
    /// Follows the order of geth's <c>t8n</c>: system calls, transactions, rewards, withdrawals and execution requests.
    /// Invalid transactions are rejected instead of failing the block.
    /// </remarks>
    public static T8nExecutionResult Execute(T8nCommandArguments arguments)
    {
        T8nTest test = T8nInputProcessor.ProcessInputAndConvertToT8nTest(arguments);

        KzgPolynomialCommitments.InitializeAsync().Wait();

        StateWriteRecorder stateWrites = CreateStateWriteRecorder();
        IWorldState stateProvider = new WorldState(stateWrites, _logManager);
        using IDisposable _ = stateProvider.BeginScope(IWorldState.PreGenesis);
        EthereumCodeInfoRepository codeInfoRepository = new(stateProvider);
        IBlockhashProvider blockhashProvider = new T8nBlockHashProvider(test.BlockHashes ?? []);

        IVirtualMachine virtualMachine = new EthereumVirtualMachine(
            blockhashProvider,
            test.SpecProvider,
            _logManager);
        EthereumTransactionProcessor transactionProcessor = new(
            BlobBaseFeeCalculator.Instance,
            test.SpecProvider,
            stateProvider,
            virtualMachine,
            codeInfoRepository,
            _logManager);

        GeneralStateTestBase.InitializeTestState(test.Alloc, stateProvider, test.SpecProvider);

        Block block = test.ConstructBlock();

        CompositeBlockTracer compositeBlockTracer = new();
        using GethLikeBlockFileTracer? fileTracer = test.IsTraceEnabled
            ? new GethLikeBlockFileTracer(block, test.GethTraceOptions, new RealFileSystem(), test.Spec)
            : null;
        if (fileTracer is not null)
        {
            compositeBlockTracer.Add(fileTracer);
            if (arguments.OutputBaseDir.Length > 0) Directory.CreateDirectory(arguments.OutputBaseDir);
        }

        BlockReceiptsTracer blockReceiptsTracer = new();
        blockReceiptsTracer.SetOtherTracer(compositeBlockTracer);
        blockReceiptsTracer.StartNewBlockTrace(block);

        virtualMachine.SetBlockExecutionContext(new BlockExecutionContext(block.Header, test.Spec));
        if (block.ParentBeaconBlockRoot is not null)
        {
            new BeaconBlockRootHandler(transactionProcessor, stateProvider).StoreBeaconRoot(block, test.Spec, NullTxTracer.Instance);
        }

        // As in geth, the parent hash is recorded only when the env carries block hashes.
        if (test.BlockHashes is not null)
        {
            new BlockhashStore(stateProvider).ApplyBlockhashStateChanges(block.Header, test.Spec);
        }

        TransactionExecutionReport transactionExecutionReport = new();
        TxValidator txValidator = new(test.StateChainId);
        ulong blobGasUsed = 0;

        for (int txIndex = 0; txIndex < test.Transactions.Length; txIndex++)
        {
            Transaction transaction = test.Transactions[txIndex];
            ValidationResult txIsValid = txValidator.IsWellFormed(transaction, test.Spec);

            if (!txIsValid)
            {
                if (txIsValid.Error is not null)
                {
                    string error = GethErrorMappings.GetErrorMapping(txIsValid.Error);
                    transactionExecutionReport.RejectedTransactionReceipts.Add(new RejectedTx(txIndex, error));
                }
                continue;
            }

            if (transaction.SupportsBlobs && block.ExcessBlobGas is null)
            {
                transactionExecutionReport.RejectedTransactionReceipts.Add(new RejectedTx(txIndex, "blob tx used but field env.ExcessBlobGas missing"));
                continue;
            }

            ulong txBlobGas = BlobGasCalculator.CalculateBlobGas(transaction);
            ulong maxBlobGas = test.Spec.GasCosts.MaxBlobGasPerBlock;
            if (blobGasUsed + txBlobGas > maxBlobGas)
            {
                transactionExecutionReport.RejectedTransactionReceipts.Add(new RejectedTx(txIndex,
                    $"blob gas ({blobGasUsed + txBlobGas}) would exceed maximum allowance {maxBlobGas}"));
                continue;
            }

            blockReceiptsTracer.StartNewTxTrace(transaction);
            TransactionResult transactionResult = transactionProcessor
                .Execute(transaction, blockReceiptsTracer);
            blockReceiptsTracer.EndTxTrace();
            if (fileTracer is not null)
            {
                File.Move(fileTracer.FileNames.Last(), Path.Combine(arguments.OutputBaseDir, $"trace-{txIndex}-{transaction.Hash}.jsonl"), overwrite: true);
            }

            if (transactionResult.TransactionExecuted)
            {
                blobGasUsed += txBlobGas;
                transactionExecutionReport.SuccessfulTransactions.Add(transaction);
                transactionExecutionReport.SuccessfulTransactionReceipts.Add(blockReceiptsTracer.LastReceipt);
            }
            else
            {
                string error = transaction.SenderAddress is null
                    ? transactionResult.ErrorDescription
                    : GethErrorMappings.GetErrorMapping(transactionResult.ErrorDescription,
                        transaction.SenderAddress.ToString(true),
                        transaction.Nonce, stateProvider.GetNonce(transaction.SenderAddress));

                transactionExecutionReport.RejectedTransactionReceipts.Add(new RejectedTx(txIndex, error));
                stateProvider.Reset(resetBlockChanges: false);
            }
        }

        blockReceiptsTracer.EndBlockTrace();

        if (test.AreRewardsEnabled)
        {
            ApplyRewards(block, stateProvider, test.Spec, test.SpecProvider);
        }

        new WithdrawalProcessor(stateProvider, _logManager).ProcessWithdrawals(block, test.Spec);

        // System calls run with EIP-158 off, so empty accounts touched so far must be pruned before them, as BlockProcessor does.
        stateProvider.Commit(test.Spec, commitRoots: false);

        new ExecutionRequestsProcessor(transactionProcessor).ProcessExecutionRequests(block, stateProvider,
            [.. transactionExecutionReport.SuccessfulTransactionReceipts], test.Spec);

        stateProvider.Commit(test.Spec);
        stateProvider.CommitTree(test.CurrentNumber);

        return T8nExecutionResult.ConstructT8nExecutionResult(stateProvider, stateWrites, block, test, transactionExecutionReport);
    }

    private static StateWriteRecorder CreateStateWriteRecorder()
    {
        PruningConfig pruningConfig = new();
        TestFinalizedStateProvider finalizedStateProvider = new(pruningConfig.PruningBoundary);
        IDbProvider dbProvider = TestMemDbProvider.Init();
        TrieStore trieStore = new(
            new NodeStorage(dbProvider.StateDb),
            No.Pruning,
            Persist.EveryBlock,
            finalizedStateProvider,
            pruningConfig,
            _logManager);
        finalizedStateProvider.TrieStore = trieStore;
        return new StateWriteRecorder(new TrieStoreScopeProvider(trieStore, dbProvider.CodeDb, UnavailableStateHeaderProvider.Instance, _logManager));
    }

    private static void ApplyRewards(Block block, IWorldState stateProvider, IReleaseSpec spec, ISpecProvider specProvider)
    {
        RewardCalculator rewardCalculator = new(specProvider);
        BlockReward[] rewards = rewardCalculator.CalculateRewards(block);

        foreach (BlockReward reward in rewards)
        {
            stateProvider.AddToBalanceAndCreateIfNotExists(reward.Address, reward.Value, spec);
        }
    }
}
