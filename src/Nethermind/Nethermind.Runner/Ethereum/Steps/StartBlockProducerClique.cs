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
using Nethermind.Consensus;
using Nethermind.Consensus.Clique;
using Nethermind.Logging;
using Nethermind.Runner.Ethereum.Api;
using Nethermind.Specs;

namespace Nethermind.Runner.Ethereum.Steps
{
    public class StartBlockProducerClique : StartBlockProducer
    {
        private readonly CliqueNethermindApi _api;

        public StartBlockProducerClique(CliqueNethermindApi api) : base(api)
        {
            _api = api;
        }

        protected override void BuildProducer()
        {
            if (_api.ChainSpec == null) throw new StepDependencyException(nameof(_api.ChainSpec));
            if (_api.SnapshotManager == null) throw new StepDependencyException(nameof(_api.SnapshotManager));
            if (_api.Signer == null) throw new StepDependencyException(nameof(_api.Signer));
            if (_api.BlockTree == null) throw new StepDependencyException(nameof(_api.BlockTree));
            if (_api.Sealer == null) throw new StepDependencyException(nameof(_api.Sealer));

            ILogger logger = _api.LogManager.GetClassLogger();
            if (logger.IsWarn) logger.Warn("Starting Clique block producer & sealer");
            BlockProducerContext producerChain = GetProducerChain();
            CliqueConfig cliqueConfig = new CliqueConfig {BlockPeriod = _api.ChainSpec.Clique.Period, Epoch = _api.ChainSpec.Clique.Epoch};
            _api.BlockProducer = new CliqueBlockProducer(
                producerChain.TxSource,
                producerChain.ChainProcessor,
                producerChain.ReadOnlyStateProvider,
                _api.BlockTree,
                _api.Timestamper,
                _api.CryptoRandom,
                _api.SnapshotManager,
                _api.Sealer,
                new TargetAdjustedGasLimitCalculator(GoerliSpecProvider.Instance, new MiningConfig()), 
                cliqueConfig,
                _api.LogManager);
        }
    }
}
