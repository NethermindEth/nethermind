// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using Nethermind.Api;
using Nethermind.Api.Steps;
using Nethermind.Consensus;
using Nethermind.Consensus.Producers;
using Nethermind.Logging;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.Init.Steps
{
    [RunnerStepDependencies(typeof(InitializeBlockProducer), typeof(ReviewBlockTree),
        typeof(InitializePrecompiles))] // Unfortunately EngineRPC API need review blockTree
    public class StartBlockProducer(
        INethermindApi api,
        IBlockProductionPolicy blockProductionPolicy,
        Lazy<IBlockProducerRunner> blockProducerRunner) : IStep
    {
        private readonly IApiWithBlockchain _api = api;

        public Task Execute(CancellationToken _)
        {
            if (blockProductionPolicy.ShouldStartBlockProduction() && blockProducerRunner.Value is not NoBlockProducerRunner)
            {
                if (_api.BlockTree is null) throw new StepDependencyException(nameof(_api.BlockTree));

                ILogger logger = _api.LogManager.GetClassLogger<StartBlockProducer>();
                if (logger.IsInfo) logger.Info($"Starting {_api.SealEngineType} block producer & sealer");
                _api.Context.ResolveOptional<IProducedBlockSuggester>();
                blockProducerRunner.Value.Start();
            }

            return Task.CompletedTask;
        }
    }
}
