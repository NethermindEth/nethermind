// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Api.Steps;
using Nethermind.Consensus;
using Nethermind.Consensus.Transactions;
using Nethermind.Core.ServiceStopper;

namespace Nethermind.Init.Steps
{
    [RunnerStepDependencies(typeof(StartBlockProcessor), typeof(SetupKeyStore), typeof(InitializeNetwork),
        typeof(ReviewBlockTree))]
    public class InitializeBlockProducer : IStep
    {
        private readonly IApiWithBlockchain _api;
        private readonly IServiceStopper _serviceStopper;

        public InitializeBlockProducer(INethermindApi api, IServiceStopper serviceStopper)
        {
            _api = api;
            _serviceStopper = serviceStopper;
        }

        public Task Execute(CancellationToken _)
        {
            if (_api.ChainSpec is null) throw new StepDependencyException(nameof(_api.ChainSpec));
            if (_api.BlockProductionPolicy is null)
                throw new StepDependencyException(nameof(_api.BlockProductionPolicy));

            if (!_api.BlockProductionPolicy.ShouldStartBlockProduction())
            {
                return Task.CompletedTask;
            }

            IConsensusPlugin? consensusPlugin = _api.GetConsensusPlugin();
            if (consensusPlugin is null)
            {
                throw new NotSupportedException($"Mining in {_api.ChainSpec.SealEngineType} mode is not supported");
            }

            IBlockProducerFactory blockProducerFactory = consensusPlugin;
            IBlockProducerRunnerFactory blockProducerRunnerFactory = consensusPlugin;

            foreach (IConsensusWrapperPlugin wrapperPlugin in _api.GetConsensusWrapperPlugins()
                         .OrderBy(static (p) => p.Priority))
            {
                blockProducerFactory =
                    new ConsensusWrapperToBlockProducerFactoryAdapter(wrapperPlugin, blockProducerFactory);
                blockProducerRunnerFactory =
                    new ConsensusWrapperToBlockProducerRunnerFactoryAdapter(wrapperPlugin, blockProducerRunnerFactory);
            }

            _api.BlockProducer = blockProducerFactory.InitBlockProducer();
            _api.BlockProducerRunner = blockProducerRunnerFactory.InitBlockProducerRunner(_api.BlockProducer);
            _serviceStopper.AddStoppable(_api.BlockProducerRunner);

            return Task.CompletedTask;
        }

        private class ConsensusWrapperToBlockProducerFactoryAdapter(
            IConsensusWrapperPlugin consensusWrapperPlugin,
            IBlockProducerFactory baseBlockProducerFactory) : IBlockProducerFactory
        {
            public IBlockProducer InitBlockProducer()
            {
                return consensusWrapperPlugin.InitBlockProducer(baseBlockProducerFactory);
            }
        }

        private class ConsensusWrapperToBlockProducerRunnerFactoryAdapter(
            IConsensusWrapperPlugin consensusWrapperPlugin,
            IBlockProducerRunnerFactory baseBlockProducerRunnerFactory) : IBlockProducerRunnerFactory
        {
            public IBlockProducerRunner InitBlockProducerRunner(IBlockProducer blockProducer)
            {
                return consensusWrapperPlugin.InitBlockProducerRunner(baseBlockProducerRunnerFactory, blockProducer);
            }
        }
    }
}
