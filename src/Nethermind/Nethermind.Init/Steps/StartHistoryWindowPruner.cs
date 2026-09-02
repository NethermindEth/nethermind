// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api.Steps;
using Nethermind.State.Flat.History;

namespace Nethermind.Init.Steps;

/// <summary>Reconciles the slice allow-list and starts the window pruner; both no-op when
/// <c>HistoryRetentionBlocks</c> is 0.</summary>
[RunnerStepDependencies(dependencies: [typeof(InitializeBlockTree)])]
public class StartHistoryWindowPruner(HistoryWindowPruner pruner) : IStep
{
    public Task Execute(CancellationToken cancellationToken)
    {
        pruner.ReconcileSliceScopes();
        pruner.Start();
        return Task.CompletedTask;
    }
}
