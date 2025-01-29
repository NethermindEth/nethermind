// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;
using Nethermind.Consensus.Comparers;
using Nethermind.Consensus.Transactions;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.TxPool;

namespace Nethermind.Consensus.Producers
{
    public class TxPoolTxSourceFactory
    {
        private readonly ITxPool _txPool;
        private readonly ISpecProvider _specProvider;
        private readonly ITransactionComparerProvider _transactionComparerProvider;
        private readonly IBlocksConfig _blocksConfig;
        private readonly ILogManager _logManager;

        public TxPoolTxSourceFactory(
            ITxPool txPool,
            ISpecProvider specProvider,
            ITransactionComparerProvider transactionComparerProvider,
            IBlocksConfig blocksConfig,
            ILogManager logManager)
        {
            _txPool = txPool;
            _specProvider = specProvider;
            _transactionComparerProvider = transactionComparerProvider;
            _blocksConfig = blocksConfig;
            _logManager = logManager;
        }

        public virtual TxPoolTxSource Create()
        {
            ITxFilterPipeline txSourceFilterPipeline = TxFilterPipelineBuilder.CreateStandardFilteringPipeline(_logManager, _specProvider, _blocksConfig);
            return new TxPoolTxSource(_txPool, _specProvider, _transactionComparerProvider, _logManager, txSourceFilterPipeline);
        }
    }
}
