// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO.Abstractions;
using Ethereum.Test.Base;
using Evm.T8n.Errors;
using Evm.T8n.JsonTypes;
using Nethermind.Blockchain.BeaconBlockRoot;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Validators;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.Tracing.GethStyle;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Trie.Pruning;

namespace Evm.T8n;

public static class T8nExecutor
{
    private static ILogManager _logManager = LimboLogs.Instance;

    public static T8nExecutionResult Execute(T8nCommandArguments arguments)
    {
        T8nTest test = T8nInputProcessor.ProcessInputAndConvertToT8nTest(arguments);

        KzgPolynomialCommitments.InitializeAsync();

        IDb stateDb = new MemDb();
        IDb codeDb = new MemDb();

        TrieStore trieStore = TestTrieStoreFactory.Build(stateDb, _logManager);
        WorldState stateProvider = new(trieStore, codeDb, _logManager);
        CodeInfoRepository codeInfoRepository = new();
        IBlockhashProvider blockhashProvider = ConstructBlockHashProvider(test);

        IVirtualMachine virtualMachine = new VirtualMachine(
            blockhashProvider,
            test.SpecProvider,
            _logManager);
        TransactionProcessor transactionProcessor = new(
            test.SpecProvider,
            stateProvider,
            virtualMachine,
            codeInfoRepository,
            _logManager);

        stateProvider.CreateAccount(test.CurrentCoinbase, 0);
        GeneralStateTestBase.InitializeTestState(test.Alloc, stateProvider, test.SpecProvider);

        Block block = test.ConstructBlock();
        var withdrawalProcessor = new WithdrawalProcessor(stateProvider, _logManager);
        withdrawalProcessor.ProcessWithdrawals(block, test.Spec);

        ApplyRewards(block, stateProvider, test.Spec, test.SpecProvider);

        CompositeBlockTracer compositeBlockTracer = new();

        StorageTxTracer storageTxTracer = new();
        compositeBlockTracer.Add(storageTxTracer);
        if (test.IsTraceEnabled)
        {
            compositeBlockTracer.Add(new GethLikeBlockFileTracer(block, test.GethTraceOptions, new FileSystem()));
        }

        BlockReceiptsTracer blockReceiptsTracer = new();
        blockReceiptsTracer.SetOtherTracer(compositeBlockTracer);
        blockReceiptsTracer.StartNewBlockTrace(block);

        var blkCtx = new BlockExecutionContext(block.Header, test.Spec);
        BeaconBlockRootHandler beaconBlockRootHandler = new(transactionProcessor, stateProvider);
        if (test.ParentBeaconBlockRoot is not null)
        {
            beaconBlockRootHandler.StoreBeaconRoot(block, in blkCtx, test.Spec, storageTxTracer);
        }

        int txIndex = 0;
        TransactionExecutionReport transactionExecutionReport = new();
        var txValidator = new TxValidator(test.StateChainId);

        foreach (Transaction transaction in test.Transactions)
        {
            ValidationResult txIsValid = txValidator.IsWellFormed(transaction, test.Spec);

            if (!txIsValid)
            {
                if (txIsValid.Error is not null)
                {
                    var error = GethErrorMappings.GetErrorMapping(txIsValid.Error);
                    transactionExecutionReport.RejectedTransactionReceipts.Add(new RejectedTx(txIndex, error));
                }
                continue;
            }

            blockReceiptsTracer.StartNewTxTrace(transaction);
            TransactionResult transactionResult = transactionProcessor
                .Execute(transaction, in blkCtx, blockReceiptsTracer);
            blockReceiptsTracer.EndTxTrace();

            transactionExecutionReport.ValidTransactions.Add(transaction);

            if (transactionResult.Success)
            {
                transactionExecutionReport.SuccessfulTransactions.Add(transaction);
                blockReceiptsTracer.LastReceipt.PostTransactionState = null;
                blockReceiptsTracer.LastReceipt.BlockHash = null;
                blockReceiptsTracer.LastReceipt.BlockNumber = 0;
                transactionExecutionReport.SuccessfulTransactionReceipts.Add(blockReceiptsTracer.LastReceipt);
            }
            else if (transactionResult.Error is not null && transaction.SenderAddress is not null)
            {
                var error = GethErrorMappings.GetErrorMapping(transactionResult.Error,
                    transaction.SenderAddress.ToString(true),
                    transaction.Nonce, stateProvider.GetNonce(transaction.SenderAddress));

                transactionExecutionReport.RejectedTransactionReceipts.Add(new RejectedTx(txIndex, error));
                stateProvider.Reset();
            }

            txIndex++;
        }

        blockReceiptsTracer.EndBlockTrace();

        stateProvider.Commit(test.SpecProvider.GetSpec((ForkActivation)1));
        stateProvider.CommitTree(test.CurrentNumber);

        return T8nExecutionResult.ConstructT8nExecutionResult(stateProvider, block, test, storageTxTracer,
            blockReceiptsTracer, test.SpecProvider, transactionExecutionReport);
    }

    private static IBlockhashProvider ConstructBlockHashProvider(T8nTest test)
    {
        var t8NBlockHashProvider = new T8nBlockHashProvider();

        foreach (KeyValuePair<string, Hash256> blockHash in test.BlockHashes)
        {
            t8NBlockHashProvider.Insert(blockHash.Value, long.Parse(blockHash.Key));
        }

        return t8NBlockHashProvider;
    }

    private static void ApplyRewards(Block block, WorldState stateProvider, IReleaseSpec spec, ISpecProvider specProvider)
    {
        var rewardCalculator = new RewardCalculator(specProvider);
        BlockReward[] rewards = rewardCalculator.CalculateRewards(block);

        foreach (BlockReward reward in rewards)
        {
            if (!stateProvider.AccountExists(reward.Address))
            {
                stateProvider.CreateAccount(reward.Address, reward.Value);
            }
            else
            {
                stateProvider.AddToBalance(reward.Address, reward.Value, spec);
            }
        }
    }
}
