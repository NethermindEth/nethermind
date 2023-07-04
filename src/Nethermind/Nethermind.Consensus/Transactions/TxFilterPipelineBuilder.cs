// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Config;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Logging;

namespace Nethermind.Consensus.Transactions
{
    public class TxFilterPipelineBuilder
    {
        private readonly ITxFilterPipeline _filterPipeline;

        public static ITxFilterPipeline CreateStandardFilteringPipeline(
            ILogManager logManager,
            ISpecProvider? specProvider,
            IBlocksConfig blocksConfig)
        {
            if (specProvider is null) throw new ArgumentNullException(nameof(specProvider));

            return new TxFilterPipelineBuilder(logManager)
                .WithMinGasPriceFilter(blocksConfig, specProvider)
                .WithBaseFeeFilter(specProvider)
                .Build;
        }

        public TxFilterPipelineBuilder(ILogManager logManager)
        {
            _filterPipeline = new TxFilterPipeline(logManager);
        }

        public TxFilterPipelineBuilder WithMinGasPriceFilter(IBlocksConfig blocksConfig, ISpecProvider specProvider)
        {
            _filterPipeline.AddTxFilter(new MinGasPriceTxFilter(blocksConfig, specProvider));
            return this;
        }

        public TxFilterPipelineBuilder WithBaseFeeFilter(ISpecProvider specProvider)
        {
            _filterPipeline.AddTxFilter(new BaseFeeTxFilter(specProvider));
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
