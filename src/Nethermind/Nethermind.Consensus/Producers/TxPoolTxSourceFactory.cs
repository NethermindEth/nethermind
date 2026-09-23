// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac.Features.AttributeFilters;
using Nethermind.Config;
using Nethermind.Consensus.Comparers;
using Nethermind.Consensus.Transactions;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.TxPool;

namespace Nethermind.Consensus.Producers
{
    public class TxPoolTxSourceFactory(
        ITxPool txPool,
        ISpecProvider specProvider,
        ITransactionComparerProvider transactionComparerProvider,
        IBlocksConfig blocksConfig,
        ILogManager logManager,
        [KeyFilter(ITxValidator.SpecChangeTxValidatorKey)] ITxValidator specChangeTxValidator) : IBlockProducerTxSourceFactory
    {
        public virtual ITxSource Create()
        {
            ITxFilterPipeline txSourceFilterPipeline = TxFilterPipelineBuilder.CreateStandardFilteringPipeline(logManager, blocksConfig);
            return new TxPoolTxSource(txPool, specProvider, transactionComparerProvider, logManager, txSourceFilterPipeline, blocksConfig, specChangeTxValidator);
        }
    }
}
