// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api.Steps;
using Nethermind.Consensus;
using Nethermind.Consensus.Producers;

namespace Nethermind.Init.Steps
{
    [RunnerStepDependencies(typeof(StartBlockProcessor), typeof(SetupKeyStore), typeof(InitializeNetwork),
        typeof(LoadGenesisBlock))]
    public class InitializeBlockProducer(
        IBlockProductionPolicy blockProductionPolicy,
        Lazy<IBlockProducerRunner> blockProducerRunner) : IStep
    {
        public Task Execute(CancellationToken cancellationToken)
        {
            // Builds the producer at this point of the startup order. The container adds the runner to the
            // service stopper when it activates it.
            if (blockProductionPolicy.ShouldStartBlockProduction())
            {
                _ = blockProducerRunner.Value;
            }

            return Task.CompletedTask;
        }
    }
}
