// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Find;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.Comparers;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Transactions;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.TxPool;
using System;
using System.Collections.Generic;
using System.Text;

namespace Nethermind.Xdc;

internal class XdcTxPoolTxSourceFactory(
        ITxPool txPool,
        ISpecProvider specProvider,
        IBlocksConfig blocksConfig,
        IBlockFinder blockFinder,
        ILogManager logManager) : IBlockProducerTxSourceFactory
{
    public virtual ITxSource Create()
    {
        ITxFilterPipeline txSourceFilterPipeline = TxFilterPipelineBuilder.CreateStandardFilteringPipeline(logManager, blocksConfig);
        return new TxPoolTxSource(txPool, specProvider, new XdcTransactionComparerProvider(specProvider, blockFinder), logManager, txSourceFilterPipeline, blocksConfig);
    }
}
