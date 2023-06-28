// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Validators;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Logging;

namespace Nethermind.Consensus.Processing
{
    /// <summary>
    /// Not thread safe.
    /// </summary>
    public class ReadOnlyChainProcessingEnv : ReadOnlyChainProcessingEnvBase
    {
        public IBlockchainProcessor ChainProcessor { get; }

        public ReadOnlyChainProcessingEnv(
            IReadOnlyTxProcessingEnv txEnv,
            IBlockValidator blockValidator,
            IBlockPreprocessorStep recoveryStep,
            IRewardCalculator rewardCalculator,
            IReceiptStorage receiptStorage,
            IReadOnlyDbProvider dbProvider,
            ISpecProvider specProvider,
            ILogManager logManager,
            IBlockProcessor.IBlockTransactionsExecutor? blockTransactionsExecutor = null) : base(txEnv, blockValidator, recoveryStep, rewardCalculator, receiptStorage, dbProvider, specProvider, logManager, blockTransactionsExecutor)
        {
            ChainProcessor = new OneTimeChainProcessor(dbProvider, _blockProcessingQueue);
        }
    }

    public class MultiCallReadOnlyChainProcessingEnv : ReadOnlyChainProcessingEnvBase
    {
        public IBlockchainProcessor ChainProcessor { get; }

        public MultiCallReadOnlyChainProcessingEnv(
            IReadOnlyTxProcessingEnv txEnv,
            IBlockValidator blockValidator,
            IBlockPreprocessorStep recoveryStep,
            IRewardCalculator rewardCalculator,
            IReceiptStorage receiptStorage,
            IReadOnlyDbProvider dbProvider,
            ISpecProvider specProvider,
            ILogManager logManager,
            IBlockProcessor.IBlockTransactionsExecutor? blockTransactionsExecutor = null) : base(txEnv, blockValidator, recoveryStep, rewardCalculator, receiptStorage, dbProvider, specProvider, logManager, blockTransactionsExecutor)
        {
            ChainProcessor = new MultiCallChainProcessor(dbProvider, _blockProcessingQueue);
        }
    }
}
