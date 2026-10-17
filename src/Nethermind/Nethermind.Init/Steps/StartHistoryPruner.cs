// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api.Steps;
using Nethermind.Consensus.Processing;
using Nethermind.History;

namespace Nethermind.Init.Steps;

/// <summary>Schedules the first history pruning pass once startup is past the block-tree fixer and the Era import,
/// then one on every <c>ProcessingQueueEmpty</c>.</summary>
/// <remarks>
/// The first pass is scheduled here because the queue's one startup signal fires before this step runs, so a node
/// that receives no blocks would never prune its backlog. The subscription lives here rather than in the pruner so
/// that resolving the pruner (as the <c>prune-history</c> command does) does not build the block processing stack.
/// </remarks>
[RunnerStepDependencies(dependencies: [typeof(InitializeNetwork), typeof(ReviewBlockTree)])]
public class StartHistoryPruner(IHistoryPruner historyPruner, IHistoryConfig historyConfig, IBlockProcessingQueue blockProcessingQueue) : IStep
{
    public Task Execute(CancellationToken cancellationToken)
    {
        if (historyConfig.Enabled())
        {
            blockProcessingQueue.ProcessingQueueEmpty += (_, _) => historyPruner.SchedulePruneHistory();
            historyPruner.SchedulePruneHistory();
        }

        return Task.CompletedTask;
    }
}
