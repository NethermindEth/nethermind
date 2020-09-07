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

using Nethermind.Blockchain;
using Nethermind.Blockchain.Processing;
using Nethermind.Blockchain.Producers;
using Nethermind.Consensus.Transactions;
using Nethermind.Logging;
using Nethermind.Runner.Ethereum.Api;

namespace Nethermind.Runner.Ethereum.Steps
{
    public class StartBlockProducerNethDev : StartBlockProducer
    {
        private readonly NethDevNethermindApi _api;

        public StartBlockProducerNethDev(NethDevNethermindApi api) : base(api)
        {
            _api = api;
        }

        protected override void BuildProducer()
        {
            ILogger logger = _api.LogManager.GetClassLogger();
            if (logger.IsWarn) logger.Warn("Starting Neth Dev block producer & sealer");
            BlockProducerContext producerChain = GetProducerChain();
            _api.BlockProducer = new DevBlockProducer(
                producerChain.TxSource,
                producerChain.ChainProcessor,
                producerChain.ReadOnlyStateProvider,
                _api.BlockTree,
                _api.BlockProcessingQueue,
                _api.TxPool,
                _api.Timestamper, 
                _api.LogManager);
        }

        protected override ITxSource CreateTxSourceForProducer(
            ReadOnlyTxProcessingEnv processingEnv,
            ReadOnlyTxProcessorSource readOnlyTxProcessorSource) 
            => new SinglePendingTxSelector(base.CreateTxSourceForProducer(processingEnv, readOnlyTxProcessorSource));
    }
}
