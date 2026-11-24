// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api.Steps;
using Nethermind.Consensus;
using Nethermind.Consensus.Producers;
using Nethermind.Core.ServiceStopper;

namespace Nethermind.Init.Steps
{
    [RunnerStepDependencies(typeof(StartBlockProcessor), typeof(SetupKeyStore), typeof(InitializeNetwork),
        typeof(LoadGenesisBlock))]
    public class InitializeBlockProducer(
        IServiceStopper serviceStopper,
        IBlockProductionPolicy blockProductionPolicy,
        Lazy<IBlockProducerRunner> blockProducerRunner) : IStep
    {
        public Task Execute(CancellationToken _)
        {
            if (blockProductionPolicy.ShouldStartBlockProduction())
            {
                serviceStopper.AddStoppable(blockProducerRunner.Value);
            }

            return Task.CompletedTask;
        }
    }
}
