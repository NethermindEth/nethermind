// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Validators;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Consensus.Processing
{
    /// <summary>
    /// Not thread safe.
    /// </summary>
    public class ReadOnlyChainProcessingEnv
    {
        public IBlockchainProcessor ChainProcessor { get; }

        public ReadOnlyChainProcessingEnv(
            ReadOnlyTxProcessingEnv txEnv,
            IBlockValidator blockValidator,
            IBlockPreprocessorStep recoveryStep,
            IRewardCalculator rewardCalculator,
            IReceiptStorage receiptStorage,
            IReadOnlyDbProvider dbProvider,
            ISpecProvider specProvider,
            ILogManager logManager,
            IBlockProcessor.IBlockTransactionsExecutor? blockTransactionsExecutor = null)
        {
            IBlockProcessor.IBlockTransactionsExecutor transactionsExecutor =
                blockTransactionsExecutor ?? new BlockProcessor.BlockValidationTransactionsExecutor(txEnv.TransactionProcessor, txEnv.StateProvider);

            BlockProcessor? blockProcessor = new(
                specProvider,
                blockValidator,
                rewardCalculator,
                transactionsExecutor,
                txEnv.StateProvider,
                receiptStorage,
                NullWitnessCollector.Instance,
                logManager);

            BlockchainProcessor? blockProcessingQueue = new(txEnv.BlockTree, blockProcessor, recoveryStep, txEnv.StateReader, logManager, BlockchainProcessor.Options.NoReceipts);
            ChainProcessor = new OneTimeChainProcessor(dbProvider, blockProcessingQueue);
        }
    }
}
