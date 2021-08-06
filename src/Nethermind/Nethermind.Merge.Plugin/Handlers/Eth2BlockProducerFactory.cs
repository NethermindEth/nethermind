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
using Nethermind.Blockchain.Comparers;
using Nethermind.Blockchain.Processing;
using Nethermind.Blockchain.Producers;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Rewards;
using Nethermind.Blockchain.Validators;
using Nethermind.Consensus;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Trie.Pruning;
using Nethermind.TxPool;

namespace Nethermind.Merge.Plugin.Handlers
{
    public class Eth2BlockProducerFactory
    {
        private readonly ITxSource? _additionalTxSource;
        private readonly IGasLimitCalculator? _gasLimitCalculator;

        public Eth2BlockProducerFactory(ITxSource? additionalTxSource = null) : this(additionalTxSource, null)
        {
            _additionalTxSource = additionalTxSource;
        }

        protected Eth2BlockProducerFactory(ITxSource? additionalTxSource, IGasLimitCalculator? gasLimitCalculator)
        {
            _additionalTxSource = additionalTxSource;
            _gasLimitCalculator = gasLimitCalculator;
        }

        public Eth2BlockProducer Create(
            IBlockProducerEnvFactory blockProducerEnvFactory,
            IBlockTree blockTree,
            IBlockProductionTrigger blockProductionTrigger,
            ISpecProvider specProvider,
            ISigner engineSigner,
            ITimestamper timestamper,
            IMiningConfig miningConfig,
            ILogManager logManager)
        {
            BlockProducerEnv producerEnv = GetProducerEnv(blockProducerEnvFactory);
                
            return new Eth2BlockProducer(
                producerEnv.TxSource,
                producerEnv.ChainProcessor,
                blockTree,
                blockProductionTrigger,
                producerEnv.ReadOnlyStateProvider,
                _gasLimitCalculator ?? new TargetAdjustedGasLimitCalculator(specProvider, miningConfig),
                engineSigner,
                timestamper,
                specProvider,
                logManager);
        }

        protected BlockProducerEnv GetProducerEnv(IBlockProducerEnvFactory blockProducerEnvFactory) => 
            blockProducerEnvFactory.Create(_additionalTxSource);
    }
}
