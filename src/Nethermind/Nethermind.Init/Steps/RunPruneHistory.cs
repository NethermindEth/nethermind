// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api.Steps;
using Nethermind.History;

namespace Nethermind.Init.Steps;

[StepCommand("prune-history", "Prune historical blocks, receipts and block access lists per the History configuration, then exit.")]
[RunnerStepDependencies(typeof(InitializeBlockTree), typeof(StartMonitoring))]
public class RunPruneHistory(HistoryPruner historyPruner) : IStep
{
    /// <inheritdoc/>
    /// <exception cref="HistoryPruner.HistoryPrunerException">Pruning is disabled or its boundary could not be established.</exception>
    public Task Execute(CancellationToken cancellationToken)
    {
        historyPruner.PruneToCompletion(cancellationToken);
        return Task.CompletedTask;
    }
}
