// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.BeaconBlockRoot;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.ExecutionRequests;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Validators;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core.Specs;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Consensus.Processing
{
    /// <summary>
    /// Not thread safe.
    /// </summary>
    public class ReadOnlyChainProcessingEnv : IAsyncDisposable
    {
        private readonly BlockchainProcessor _blockProcessingQueue;
        public IBlockProcessor BlockProcessor { get; }
        public IBlockchainProcessor ChainProcessor { get; }
        public IBlockProcessingQueue BlockProcessingQueue { get; }

        public ReadOnlyChainProcessingEnv(
            IReadOnlyTxProcessingScope scope,
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
                blockTransactionsExecutor ?? new BlockProcessor.BlockValidationTransactionsExecutor(scope.TransactionProcessor, scope.WorldState);

            BlockProcessor = CreateBlockProcessor(scope, blockTree, blockValidator, rewardCalculator, receiptStorage, specProvider, logManager, transactionsExecutor);

            _blockProcessingQueue = new BlockchainProcessor(blockTree, BlockProcessor, recoveryStep, stateReader, logManager, BlockchainProcessor.Options.NoReceipts);
            BlockProcessingQueue = _blockProcessingQueue;
            ChainProcessor = new OneTimeChainProcessor(scope.WorldState, _blockProcessingQueue);
        }

        protected virtual IBlockProcessor CreateBlockProcessor(
            IReadOnlyTxProcessingScope scope,
            IBlockTree blockTree,
            IBlockValidator blockValidator,
            IRewardCalculator rewardCalculator,
            IReceiptStorage receiptStorage,
            ISpecProvider specProvider,
            ILogManager logManager,
            IBlockProcessor.IBlockTransactionsExecutor transactionsExecutor
        )
        {
            return new BlockProcessor(
                specProvider,
                blockValidator,
                rewardCalculator,
                transactionsExecutor,
                scope.WorldState,
                receiptStorage,
                new BeaconBlockRootHandler(scope.TransactionProcessor, scope.WorldState),
                new BlockhashStore(specProvider, scope.WorldState),
                logManager,
                new WithdrawalProcessor(scope.WorldState, logManager),
                new ExecutionRequestsProcessor(scope.TransactionProcessor)
            );
        }

        public ValueTask DisposeAsync() => _blockProcessingQueue?.DisposeAsync() ?? default;
    }
}
