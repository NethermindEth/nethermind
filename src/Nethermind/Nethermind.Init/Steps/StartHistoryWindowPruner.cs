// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api.Steps;
using Nethermind.State.Flat.History;

namespace Nethermind.Init.Steps;

/// <summary>Reconciles the slice allow-list, which refuses slices on an unwindowed database, and starts the
/// window pruner, which only runs when <c>HistoryRetention</c> is <c>Rolling</c>.</summary>
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
