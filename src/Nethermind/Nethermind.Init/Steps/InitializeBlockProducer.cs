// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Api;
using Nethermind.Api.Steps;
using Nethermind.Consensus;
using Nethermind.Core.ServiceStopper;

namespace Nethermind.Init.Steps
{
    [RunnerStepDependencies(typeof(StartBlockProcessor), typeof(SetupKeyStore), typeof(InitializeNetwork),
        typeof(ReviewBlockTree))]
    public class InitializeBlockProducer(INethermindApi api, IServiceStopper serviceStopper) : IStep
    {
        private readonly IApiWithBlockchain _api = api;
        private readonly IServiceStopper _serviceStopper = serviceStopper;

        public Task Execute(CancellationToken _)
        {
            if (_api.ChainSpec is null) throw new StepDependencyException(nameof(_api.ChainSpec));
            if (_api.BlockProductionPolicy is null)
                throw new StepDependencyException(nameof(_api.BlockProductionPolicy));

            if (!_api.BlockProductionPolicy.ShouldStartBlockProduction())
            {
                return Task.CompletedTask;
            }

            IBlockProducerFactory blockProducerFactory = _api.Context.ResolveOptional<IBlockProducerFactory>()
                ?? throw new NotSupportedException($"Mining in {_api.ChainSpec.SealEngineType} mode is not supported");
            IBlockProducerRunnerFactory blockProducerRunnerFactory = _api.Context.ResolveOptional<IBlockProducerRunnerFactory>()
                ?? throw new NotSupportedException($"Mining in {_api.ChainSpec.SealEngineType} mode is not supported");

            _api.BlockProducer = blockProducerFactory.InitBlockProducer();
            _api.BlockProducerRunner = blockProducerRunnerFactory.InitBlockProducerRunner(_api.BlockProducer);
            _serviceStopper.AddStoppable(_api.BlockProducerRunner);

            return Task.CompletedTask;
        }
    }
}
