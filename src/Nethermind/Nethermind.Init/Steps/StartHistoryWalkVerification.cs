// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api.Steps;
using Nethermind.Init.FlatHistory;

namespace Nethermind.Init.Steps;

/// <summary>
/// Forces DI construction of the singleton <see cref="HistoryWalkVerificationCoordinator"/> at startup, mirroring
/// <see cref="StartArchiveClone"/>. The coordinator no-ops for the lifetime of the process when
/// <c>Flat.HistoryVerifyEveryBlock</c> is off.
/// </summary>
[RunnerStepDependencies(dependencies: [typeof(InitializeNetwork)])]
public class StartHistoryWalkVerification(HistoryWalkVerificationCoordinator coordinator) : IStep
{
    public Task Execute(CancellationToken cancellationToken)
    {
        _ = coordinator;
        return Task.CompletedTask;
    }
}
