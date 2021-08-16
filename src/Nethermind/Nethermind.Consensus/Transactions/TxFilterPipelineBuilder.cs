//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

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
            ISpecProvider specProvider,
            IMiningConfig? miningConfig = null)
        {
            return new TxFilterPipelineBuilder(logManager)
                .WithMinGasPriceFilter(miningConfig?.MinGasPrice ?? UInt256.Zero, specProvider)
                .WithBaseFeeFilter(specProvider)
                .Build;
        }

        public TxFilterPipelineBuilder(ILogManager logManager)
        {
            _filterPipeline = new TxFilterPipeline(logManager);
        }
        
        public TxFilterPipelineBuilder WithMinGasPriceFilter(UInt256 minGasPrice, ISpecProvider specProvider)
        {
            _filterPipeline.AddTxFilter(new MinGasPriceTxFilter(minGasPrice, specProvider));
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
