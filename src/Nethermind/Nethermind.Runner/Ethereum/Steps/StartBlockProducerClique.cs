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
using Nethermind.Consensus.Clique;
using Nethermind.Logging;
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
            if (_context.ChainSpec == null) throw new StepDependencyException(nameof(_context.ChainSpec));
            if (_context.SnapshotManager == null) throw new StepDependencyException(nameof(_context.SnapshotManager));
            if (_context.Signer == null) throw new StepDependencyException(nameof(_context.Signer));
            if (_context.BlockTree == null) throw new StepDependencyException(nameof(_context.BlockTree));
            if (_context.Sealer == null) throw new StepDependencyException(nameof(_context.Sealer));

            ILogger logger = _context.LogManager.GetClassLogger();
            if (logger.IsWarn) logger.Warn("Starting Clique block producer & sealer");
            BlockProducerContext producerChain = GetProducerChain();
            CliqueConfig cliqueConfig = new CliqueConfig {BlockPeriod = _context.ChainSpec.Clique.Period, Epoch = _context.ChainSpec.Clique.Epoch};
            _context.BlockProducer = new CliqueBlockProducer(
                producerChain.TxSource,
                producerChain.ChainProcessor,
                producerChain.ReadOnlyStateProvider,
                _context.BlockTree,
                _context.Timestamper,
                _context.CryptoRandom,
                _context.SnapshotManager,
                _context.Sealer,
                cliqueConfig,
                _context.LogManager);
        }
    }
}
