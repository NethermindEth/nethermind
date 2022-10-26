//  Copyright (c) 2021 Demerzel Solutions Limited
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

using Nethermind.Consensus;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Logging;

namespace Nethermind.Merge.Plugin.BlockProduction
{
    public class PostMergeBlockProducerFactory
    {
        protected readonly ISpecProvider _specProvider;
        protected readonly ISealEngine _sealEngine;
        protected readonly ITimestamper _timestamper;
        protected readonly IMiningConfig _miningConfig;
        protected readonly ILogManager _logManager;
        protected readonly IGasLimitCalculator? _gasLimitCalculator;

        public PostMergeBlockProducerFactory(
            ISpecProvider specProvider,
            ISealEngine sealEngine,
            ITimestamper timestamper,
            IMiningConfig miningConfig,
            ILogManager logManager,
            IGasLimitCalculator? gasLimitCalculator = null)
        {
            _specProvider = specProvider;
            _sealEngine = sealEngine;
            _timestamper = timestamper;
            _miningConfig = miningConfig;
            _logManager = logManager;
            _gasLimitCalculator = gasLimitCalculator;
        }

        public virtual PostMergeBlockProducer Create(
            BlockProducerEnv producerEnv,
            IBlockProductionTrigger blockProductionTrigger,
            ITxSource? txSource = null)
        {

            return new PostMergeBlockProducer(
                txSource ?? producerEnv.TxSource,
                producerEnv.ChainProcessor,
                producerEnv.BlockTree,
                blockProductionTrigger,
                producerEnv.ReadOnlyStateProvider,
                _gasLimitCalculator ?? new TargetAdjustedGasLimitCalculator(_specProvider, _miningConfig),
                _sealEngine,
                _timestamper,
                _specProvider,
                _logManager,
                _miningConfig);
        }
    }
}
