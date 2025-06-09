// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Steps;

namespace Nethermind.Init.Steps
{
    [RunnerStepDependencies(typeof(InitializeBlockchain))]
    public class StartBlockProcessor(IMainProcessingContext mainProcessingContext) : IStep
    {
        public Task Execute(CancellationToken _)
        {
            mainProcessingContext.BlockchainProcessor.Start();
            return Task.CompletedTask;
        }
    }
}
