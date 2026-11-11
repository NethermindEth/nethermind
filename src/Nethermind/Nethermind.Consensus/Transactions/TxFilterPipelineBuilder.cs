// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Logging;

namespace Nethermind.Consensus.Transactions
{
    public class TxFilterPipelineBuilder(ILogManager logManager)
    {
        private readonly TxFilterPipeline _filterPipeline = new(logManager);

        /// <param name="slots">Places the next block for the EIP-8198 base fee; see <see cref="BaseFeeTxFilter"/>.</param>
        public static ITxFilterPipeline CreateStandardFilteringPipeline(ILogManager logManager, IBlocksConfig blocksConfig, SlotSchedule? slots = null) => new TxFilterPipelineBuilder(logManager)
                .WithMinGasPriceFilter(blocksConfig, slots)
                .WithBaseFeeFilter(slots)
                .WithHeadTxFilter()
                .Build;

        public TxFilterPipelineBuilder WithHeadTxFilter()
        {
            _filterPipeline.AddTxFilter(new HeadTxValidator().AsTxFilter());
            return this;
        }

        public TxFilterPipelineBuilder WithMinGasPriceFilter(IBlocksConfig blocksConfig, SlotSchedule? slots = null)
        {
            _filterPipeline.AddTxFilter(new MinGasPriceTxFilter(blocksConfig, slots) { IncludeRejectionMessage = false });
            return this;
        }

        public TxFilterPipelineBuilder WithBaseFeeFilter(SlotSchedule? slots = null)
        {
            _filterPipeline.AddTxFilter(new BaseFeeTxFilter(slots));
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
