// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api.Steps;
using Nethermind.State.Flat.History;

namespace Nethermind.Init.Steps;

/// <summary>
/// Forces DI construction of the singleton <see cref="HistoryWindowPruner"/> at startup — nothing else in the
/// container resolves it, and a pruner that is never constructed never subscribes to watermark advances or runs a
/// pass. The pruner itself no-ops for the lifetime of the process when <c>HistoryRetentionBlocks</c> is 0.
/// </summary>
[RunnerStepDependencies(dependencies: [typeof(InitializeBlockTree)])]
public class StartHistoryWindowPruner(HistoryWindowPruner pruner) : IStep
{
    public Task Execute(CancellationToken cancellationToken)
    {
        pruner.ReconcileSliceScopes();
        return Task.CompletedTask;
    }
}
