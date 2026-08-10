// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api.Steps;
using Nethermind.Init.FlatHistory;

namespace Nethermind.Init.Steps;

/// <summary>
/// Forces DI construction of the singleton <see cref="WindowBackfillCoordinator"/> at startup - nothing else in
/// the container resolves it, and a coordinator that is never constructed never starts its background peer-fed
/// backfill attempt. The coordinator itself no-ops for the lifetime of the process when the database is unwindowed
/// (<c>HistoryRetentionBlocks</c> is 0), mirroring <see cref="StartHistoryWindowPruner"/>.
/// </summary>
[RunnerStepDependencies(dependencies: [typeof(InitializeNetwork)])]
public class StartHistoryWindowBackfill(WindowBackfillCoordinator coordinator) : IStep
{
    public Task Execute(CancellationToken cancellationToken)
    {
        _ = coordinator;
        return Task.CompletedTask;
    }
}
