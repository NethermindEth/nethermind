// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Steps;
using Nethermind.Consensus;
using Nethermind.Consensus.Producers;
using Nethermind.Core.ServiceStopper;

namespace Nethermind.Init.Steps
{
    [RunnerStepDependencies(typeof(StartBlockProcessor), typeof(SetupKeyStore), typeof(InitializeNetwork),
        typeof(ReviewBlockTree))]
    public class InitializeBlockProducer(
        INethermindApi api,
        IServiceStopper serviceStopper,
        IBlockProductionPolicy blockProductionPolicy,
        Lazy<IEnumerable<IBlockProducerFactory>> blockProducerFactories,
        Lazy<IEnumerable<IBlockProducerRunnerFactory>> blockProducerRunnerFactories) : IStep
    {
        private readonly IApiWithBlockchain _api = api;

        public Task Execute(CancellationToken _)
        {
            if (!blockProductionPolicy.ShouldStartBlockProduction())
            {
                return Task.CompletedTask;
            }

            // LastOrDefault mirrors Autofac single-resolve "last registration wins", so a module can override
            // the engine factory (e.g. XdcSubnet over Xdc); empty means the engine has no producer (e.g. Taiko).
            IBlockProducerFactory blockProducerFactory = blockProducerFactories.Value.LastOrDefault()
                ?? throw new NotSupportedException($"Mining in {_api.ChainSpec!.SealEngineType} mode is not supported");
            IBlockProducerRunnerFactory blockProducerRunnerFactory = blockProducerRunnerFactories.Value.LastOrDefault()
                ?? throw new NotSupportedException($"Mining in {_api.ChainSpec!.SealEngineType} mode is not supported");

            _api.BlockProducer = blockProducerFactory.InitBlockProducer();
            _api.BlockProducerRunner = blockProducerRunnerFactory.InitBlockProducerRunner(_api.BlockProducer);
            serviceStopper.AddStoppable(_api.BlockProducerRunner);

            return Task.CompletedTask;
        }
    }
}
