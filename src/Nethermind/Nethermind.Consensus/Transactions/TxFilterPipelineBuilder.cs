// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Config;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.TxPool.Filters;

namespace Nethermind.Consensus.Transactions
{
    public class TxFilterPipelineBuilder(ILogManager logManager)
    {
        private readonly TxFilterPipeline _filterPipeline = new(logManager);

        public static ITxFilterPipeline CreateStandardFilteringPipeline(
            ILogManager logManager,
            ISpecProvider? specProvider,
            IBlocksConfig blocksConfig)
        {
            ArgumentNullException.ThrowIfNull(specProvider);

            return new TxFilterPipelineBuilder(logManager)
                .WithMinGasPriceFilter(blocksConfig)
                .WithBaseFeeFilter()
                .WithCustomTxFilter(new TxGasLimitTxFilter())
                .WithCustomTxFilter(new ProofVersionTxFilter())
                .WithCustomTxFilter(new BlobLimitTxFilter())
                .Build;
        }

        public TxFilterPipelineBuilder WithMinGasPriceFilter(IBlocksConfig blocksConfig)
        {
            _filterPipeline.AddTxFilter(new MinGasPriceTxFilter(blocksConfig));
            return this;
        }

        public TxFilterPipelineBuilder WithBaseFeeFilter()
        {
            _filterPipeline.AddTxFilter(new BaseFeeTxFilter());
            return this;
        }

        public TxFilterPipelineBuilder WithCustomTxFilter(ITxFilter txFilter)
        {
            _filterPipeline.AddTxFilter(txFilter);
            return this;
        }

        public TxFilterPipelineBuilder WithNullTxFilter()
        {
            _filterPipeline.AddTxFilter(new NullTxFilter());
            return this;
        }

        public ITxFilterPipeline Build => _filterPipeline;
    }
}
