// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain.Processing;
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
    public class ReadOnlyChainProcessingEnv : IDisposable
    {
        private readonly IReadOnlyTxProcessorSourceExt _txEnv;

        private readonly BlockchainProcessor _blockProcessingQueue;
        public IBlockProcessor BlockProcessor { get; }
        public IBlockchainProcessor ChainProcessor { get; }
        public IBlockProcessingQueue BlockProcessingQueue { get; }
        public IWorldState WorldState => _txEnv.WorldState;

        public ReadOnlyChainProcessingEnv(
            IReadOnlyTxProcessorSourceExt txEnv,
            IBlockValidator blockValidator,
            IBlockPreprocessorStep recoveryStep,
            IRewardCalculator rewardCalculator,
            IReceiptStorage receiptStorage,
            IReadOnlyDbProvider dbProvider,
            ISpecProvider specProvider,
            ILogManager logManager,
            IBlockProcessor.IBlockTransactionsExecutor? blockTransactionsExecutor = null)
        {
            _txEnv = txEnv;
            IBlockProcessor.IBlockTransactionsExecutor transactionsExecutor =
                blockTransactionsExecutor ?? new BlockProcessor.BlockValidationTransactionsExecutor(_txEnv.TransactionProcessor, WorldState);

            BlockProcessor = new BlockProcessor(
                specProvider,
                blockValidator,
                rewardCalculator,
                transactionsExecutor,
                WorldState,
                receiptStorage,
                NullWitnessCollector.Instance,
                logManager);

            _blockProcessingQueue = new BlockchainProcessor(_txEnv.BlockTree, BlockProcessor, recoveryStep, _txEnv.StateReader, logManager, BlockchainProcessor.Options.NoReceipts);
            BlockProcessingQueue = _blockProcessingQueue;
            ChainProcessor = new OneTimeChainProcessor(dbProvider, _blockProcessingQueue);
        }

        public void Dispose()
        {
            _blockProcessingQueue?.Dispose();
        }
    }
}
