// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;
using Nethermind.Consensus.Comparers;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Transactions;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.TxPool;

namespace Nethermind.Consensus.Ethash;

public class NethDevBlockProducerTxSourceFactory(
    ISpecProvider specProvider,
    ITxPool txPool,
    ITransactionComparerProvider transactionComparerProvider,
    IBlocksConfig blocksConfig,
    ILogManager logManager) : IBlockProducerTxSourceFactory
{
    public ITxSource Create()
    {
        ITxFilterPipeline txFilterPipeline = new TxFilterPipelineBuilder(logManager)
            .WithBaseFeeFilter(specProvider)
            .WithNullTxFilter()
            .WithMinGasPriceFilter(blocksConfig, specProvider)
            .Build;

        return new TxPoolTxSource(
            txPool,
            specProvider,
            transactionComparerProvider!,
            logManager,
            txFilterPipeline).ServeTxsOneByOne();
    }
}
