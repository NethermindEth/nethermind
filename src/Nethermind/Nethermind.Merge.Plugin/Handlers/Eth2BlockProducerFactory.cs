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

using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;

namespace Nethermind.Merge.Plugin.Handlers
{
    public class Eth2BlockProducerFactory
    {
        private readonly ISpecProvider _specProvider;
        private readonly ISealEngine _sealEngine;
        private readonly ITimestamper _timestamper;
        private readonly IMiningConfig _miningConfig;
        private readonly ILogManager _logManager;
        private readonly IGasLimitCalculator? _gasLimitCalculator;

        public Eth2BlockProducerFactory(
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

        public Eth2BlockProducer Create(
            Eth2BlockProductionContext eth2BlockProductionContext,
            ITxSource? txSource = null,
            IBlockProductionTrigger blockProductionTrigger = null) // ToDo temp hack with passing block production trigger for MEV & ETH2 
        {
            BlockProducerEnv producerEnv = eth2BlockProductionContext.BlockProducerEnv;
                
            return new Eth2BlockProducer(
                txSource ?? producerEnv.TxSource,
                producerEnv.ChainProcessor,
                producerEnv.BlockTree,
                blockProductionTrigger ?? eth2BlockProductionContext.BlockProductionTrigger,
                producerEnv.ReadOnlyStateProvider,
                _gasLimitCalculator ?? new TargetAdjustedGasLimitCalculator(_specProvider, _miningConfig),
                _sealEngine,
                _timestamper,
                _specProvider,
                _logManager);
        }
    }
}
