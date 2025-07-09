// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;
using Nethermind.Consensus.Validators;
using Nethermind.Logging;

namespace Nethermind.Consensus.Transactions
{
    public class TxFilterPipelineBuilder(ILogManager logManager)
    {
        private readonly TxFilterPipeline _filterPipeline = new(logManager);

        public static ITxFilterPipeline CreateStandardFilteringPipeline(ILogManager logManager, IBlocksConfig blocksConfig)
        {
            return new TxFilterPipelineBuilder(logManager)
                .WithMinGasPriceFilter(blocksConfig)
                .WithBaseFeeFilter()
                .WithHeadTxFilter()
                .Build;
        }

        private TxFilterPipelineBuilder WithHeadTxFilter()
        {
            _filterPipeline.AddTxFilter(new HeadTxValidator().AsTxFilter());
            return this;
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
