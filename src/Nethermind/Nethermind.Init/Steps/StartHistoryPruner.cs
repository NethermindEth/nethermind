// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api.Steps;
using Nethermind.History;

namespace Nethermind.Init.Steps;

/// <summary>Schedules the first history pruning pass once startup is past the block-tree fixer and the Era import.</summary>
/// <remarks>
/// The pruner otherwise runs only on <c>ProcessingQueueEmpty</c>, whose one startup signal fires before the pruner is
/// created by the network step, so a node that receives no blocks would never prune its backlog.
/// </remarks>
[RunnerStepDependencies(dependencies: [typeof(InitializeNetwork)])]
public class StartHistoryPruner(IHistoryPruner historyPruner) : IStep
{
    public Task Execute(CancellationToken cancellationToken)
    {
        historyPruner.SchedulePruneHistory();
        return Task.CompletedTask;
    }
}
