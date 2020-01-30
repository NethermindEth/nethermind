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
using Nethermind.Clique;
using Nethermind.Runner.Ethereum.Context;

namespace Nethermind.Runner.Ethereum.Steps
{
    public class StartBlockProducerClique : StartBlockProducer
    {
        private readonly CliqueEthereumRunnerContext _context;

        public StartBlockProducerClique(CliqueEthereumRunnerContext context) : base(context)
        {
            _context = context;
        }

        protected override void BuildProducer()
        {
            if (_context.Logger.IsWarn) _context.Logger.Warn("Starting Clique block producer & sealer");
            BlockProducerContext producerChain = GetProducerChain();
            CliqueConfig cliqueConfig = new CliqueConfig {BlockPeriod = _context.ChainSpec.Clique.Period, Epoch = _context.ChainSpec.Clique.Epoch};
            _context.BlockProducer = new CliqueBlockProducer(
                producerChain.PendingTxSelector,
                producerChain.ChainProcessor,
                producerChain.ReadOnlyStateProvider,
                _context.BlockTree,
                _context.Timestamper,
                _context.CryptoRandom,
                _context.SnapshotManager,
                _context.Sealer,
                _context.NodeKey.Address,
                cliqueConfig,
                _context.LogManager);
        }
    }
}