// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api.Steps;
using Nethermind.Consensus.Processing;
using Nethermind.History;

namespace Nethermind.Init.Steps
{
    [RunnerStepDependencies(typeof(InitializeBlockchain))]
    public class StartBlockProcessor(IMainProcessingContext mainProcessingContext, IHistoryPruner historyPruner) : IStep
    {
        public Task Execute(CancellationToken cancellationToken)
        {
            // The pruner subscribes to ProcessingQueueEmpty, which Start() raises once; created any later, an idle node never prunes.
            _ = historyPruner;
            mainProcessingContext.BlockchainProcessor.Start();
            return Task.CompletedTask;
        }
    }
}
