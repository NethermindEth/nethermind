// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO.Abstractions;
using Ethereum.Test.Base;
using Nethermind.Blockchain.BeaconBlockRoot;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Validators;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.Tracing.GethStyle;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Trie.Pruning;

namespace Evm.T8NTool;

public abstract class T8nTest
{
    private readonly ILogManager _logManager = LimboLogs.Instance;

    protected T8nResult RunTest(T8nTestCase test)
    {

        IDb stateDb = new MemDb();
        IDb codeDb = new MemDb();

        IReleaseSpec spec = test.SpecProvider.GetSpec((ForkActivation)test.CurrentNumber);
        KzgPolynomialCommitments.InitializeAsync();
        BlockHeader header = test.GetBlockHeader();
        BlockHeader? parentHeader = test.GetParentBlockHeader();

        if (test.Fork.IsEip1559Enabled)
        {
            test.CurrentBaseFee = header.BaseFeePerGas = CalculateBaseFeePerGas(test, parentHeader);
        }

        if (parentHeader != null)
        {
            header.ExcessBlobGas = BlobGasCalculator.CalculateExcessBlobGas(parentHeader, spec);
        }

        var blockhashProvider = GetBlockHashProvider(test, header, parentHeader);

        TrieStore trieStore = new(stateDb, _logManager);
        WorldState stateProvider = new(trieStore, codeDb, _logManager);
        CodeInfoRepository codeInfoRepository = new();
        IVirtualMachine virtualMachine = new VirtualMachine(
            blockhashProvider,
            test.SpecProvider,
            codeInfoRepository,
            _logManager);

        TransactionProcessor transactionProcessor = new(
            test.SpecProvider,
            stateProvider,
            virtualMachine,
            codeInfoRepository,
            _logManager);

        GeneralStateTestBase.InitializeTestState(test.Pre, stateProvider, test.SpecProvider);

        var ecdsa = new EthereumEcdsa(test.SpecProvider.ChainId);
        foreach (var transaction in test.Transactions)
        {
            transaction.ChainId ??= test.StateChainId;
            transaction.SenderAddress ??= ecdsa.RecoverAddress(transaction);
        }

        BlockHeader[] uncles = test.Ommers
            .Select(ommer => Build.A.BlockHeader
                .WithNumber(test.CurrentNumber - ommer.Delta)
                .WithBeneficiary(ommer.Address)
                .TestObject)
            .ToArray();

        Block block = Build.A.Block.WithHeader(header).WithTransactions(test.Transactions)
            .WithWithdrawals(test.Withdrawals).WithUncles(uncles).TestObject;

        var withdrawalProcessor = new WithdrawalProcessor(stateProvider, _logManager);
        withdrawalProcessor.ProcessWithdrawals(block, spec);

        CalculateReward(test.StateReward, block, stateProvider, spec);
        BlockReceiptsTracer blockReceiptsTracer = new BlockReceiptsTracer();
        StorageTxTracer storageTxTracer = new();
        CompositeBlockTracer compositeBlockTracer = new();
        compositeBlockTracer.Add(storageTxTracer);
        if (test.IsTraceEnabled)
        {
            GethLikeBlockFileTracer gethLikeBlockFileTracer =
                new(block, test.GethTraceOptions, new FileSystem());
            compositeBlockTracer.Add(gethLikeBlockFileTracer);
        }

        blockReceiptsTracer.SetOtherTracer(compositeBlockTracer);

        blockReceiptsTracer.StartNewBlockTrace(block);

        BeaconBlockRootHandler beaconBlockRootHandler = new(transactionProcessor, stateProvider);
        if (test.ParentBeaconBlockRoot != null)
        {
            beaconBlockRootHandler.StoreBeaconRoot(block, spec, storageTxTracer);
        }

        int txIndex = 0;
        TransactionExecutionReport transactionExecutionReport = new();

        var txValidator = new TxValidator(test.StateChainId);

        foreach (var tx in test.Transactions)
        {
            ValidationResult txIsValid = txValidator.IsWellFormed(tx, spec);
            if (txIsValid)
            {
                blockReceiptsTracer.StartNewTxTrace(tx);
                TransactionResult transactionResult = transactionProcessor
                    .Execute(tx, new BlockExecutionContext(header), blockReceiptsTracer);
                blockReceiptsTracer.EndTxTrace();

                transactionExecutionReport.ValidTransactions.Add(tx);
                if (transactionResult.Success)
                {
                    transactionExecutionReport.SuccessfulTransactions.Add(tx);
                    blockReceiptsTracer.LastReceipt.PostTransactionState = null;
                    blockReceiptsTracer.LastReceipt.BlockHash = null;
                    blockReceiptsTracer.LastReceipt.BlockNumber = 0;
                    transactionExecutionReport.SuccessfulTransactionReceipts.Add(blockReceiptsTracer.LastReceipt);
                }
                else if (transactionResult.Error != null)
                {
                    transactionExecutionReport.RejectedTransactionReceipts.Add(new RejectedTx(txIndex,
                        GethErrorMappings.GetErrorMapping(transactionResult.Error, tx.SenderAddress.ToString(true),
                            tx.Nonce, stateProvider.GetNonce(tx.SenderAddress))));
                    stateProvider.Reset();
                }

                txIndex++;
            }
            else if (txIsValid.Error != null)
            {
                transactionExecutionReport.RejectedTransactionReceipts.Add(new RejectedTx(txIndex,
                    GethErrorMappings.GetErrorMapping(txIsValid.Error)));
            }
        }

        blockReceiptsTracer.EndBlockTrace();

        stateProvider.Commit(test.SpecProvider.GetSpec((ForkActivation)1));
        stateProvider.CommitTree(test.CurrentNumber);

        if (test.CurrentCoinbase != null && !stateProvider.AccountExists(test.CurrentCoinbase))
        {
            stateProvider.CreateAccount(test.CurrentCoinbase, 0);
        }

        stateProvider.Commit(test.SpecProvider.GetSpec((ForkActivation)1));

        stateProvider.RecalculateStateRoot();

        return T8nResult.ConstructT8NResult(stateProvider, block, test, storageTxTracer,
                blockReceiptsTracer, test.SpecProvider, header, transactionExecutionReport);
    }

    private static IBlockhashProvider GetBlockHashProvider(T8nTestCase test, BlockHeader header,
        BlockHeader? parent)
    {
        var t8NBlockHashProvider = new T8NBlockHashProvider();

        if (header.Hash != null) t8NBlockHashProvider.Insert(header.Hash, header.Number);
        if (parent?.Hash != null) t8NBlockHashProvider.Insert(parent.Hash, parent.Number);
        foreach (var blockHash in test.BlockHashes)
        {
            t8NBlockHashProvider.Insert(blockHash.Value, long.Parse(blockHash.Key));
        }

        return t8NBlockHashProvider;
    }

    private static UInt256 CalculateBaseFeePerGas(T8nTestCase test, BlockHeader? parentHeader)
    {
        if (test.CurrentBaseFee.HasValue) return test.CurrentBaseFee.Value;
        return BaseFeeCalculator.Calculate(parentHeader, test.Fork);
    }

    private static void CalculateReward(string? stateReward, Block block,
        WorldState stateProvider, IReleaseSpec spec)
    {
        if (stateReward == null) return;

        var rewardCalculator = new RewardCalculator(UInt256.Parse(stateReward));
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
