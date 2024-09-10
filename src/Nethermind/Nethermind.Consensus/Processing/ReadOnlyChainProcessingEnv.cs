// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Validators;
using Nethermind.Core.Specs;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Consensus.Processing
{
    /// <summary>
    /// Not thread safe.
    /// </summary>
    public class ReadOnlyChainProcessingEnv : IDisposable
    {
        private readonly BlockchainProcessor _blockProcessingQueue;
        public IBlockProcessor BlockProcessor { get; }
        public IBlockchainProcessor ChainProcessor { get; }
        public IBlockProcessingQueue BlockProcessingQueue { get; }

        public ReadOnlyChainProcessingEnv(ReadOnlyTxProcessingEnv scope,
            IBlockValidator blockValidator,
            IBlockPreprocessorStep recoveryStep,
            IRewardCalculator rewardCalculator,
            IReceiptStorage receiptStorage,
            ISpecProvider specProvider,
            IBlockTree blockTree,
            IStateReader stateReader,
            ILogManager logManager,
            IBlockProcessor.IBlockTransactionsExecutor? blockTransactionsExecutor = null)
        {
            IBlockProcessor.IBlockTransactionsExecutor transactionsExecutor =
                blockTransactionsExecutor ?? new BlockProcessor.BlockValidationTransactionsExecutor(scope.TransactionProcessor);

            BlockProcessor = CreateBlockProcessor(scope, blockTree, blockValidator, rewardCalculator, receiptStorage, specProvider, logManager, transactionsExecutor);

            _blockProcessingQueue = new BlockchainProcessor(blockTree, BlockProcessor, recoveryStep, stateReader, logManager, BlockchainProcessor.Options.NoReceipts);
            BlockProcessingQueue = _blockProcessingQueue;
            ChainProcessor = new OneTimeChainProcessor(_blockProcessingQueue);
            _blockProcessingQueue = new BlockchainProcessor(blockTree, BlockProcessor, recoveryStep, stateReader, logManager, BlockchainProcessor.Options.NoReceipts);
            BlockProcessingQueue = _blockProcessingQueue;
            ChainProcessor = new OneTimeChainProcessor(_blockProcessingQueue);
        }

        protected virtual IBlockProcessor CreateBlockProcessor(ReadOnlyTxProcessingEnv scope,
            IBlockTree blockTree,
            IBlockValidator blockValidator,
            IRewardCalculator rewardCalculator,
            IReceiptStorage receiptStorage,
            ISpecProvider specProvider,
            ILogManager logManager,
            IBlockProcessor.IBlockTransactionsExecutor transactionsExecutor)
        {
            return new BlockProcessor(
                specProvider,
                blockValidator,
                rewardCalculator,
                transactionsExecutor,
                scope.WorldStateProvider,
                receiptStorage,
                new BlockhashStore(specProvider),
                logManager);
        }

        public void Dispose()
        {
            _blockProcessingQueue?.Dispose();
        }
    }
}
