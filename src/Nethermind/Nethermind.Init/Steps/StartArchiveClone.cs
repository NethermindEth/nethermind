// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api.Steps;
using Nethermind.Init.FlatHistory;

namespace Nethermind.Init.Steps;

/// <summary>
/// Forces DI construction of the singleton <see cref="ArchiveCloneCoordinator"/> at startup, the clone-mode
/// counterpart to <see cref="StartHistoryWindowBackfill"/>. The coordinator no-ops for the lifetime of the process
/// when <c>Flat.HistoryArchiveCloneEnabled</c> is off.
/// </summary>
[RunnerStepDependencies(dependencies: [typeof(InitializeNetwork)])]
public class StartArchiveClone(ArchiveCloneCoordinator coordinator) : IStep
{
    public Task Execute(CancellationToken cancellationToken)
    {
        _ = coordinator;
        return Task.CompletedTask;
    }
}
