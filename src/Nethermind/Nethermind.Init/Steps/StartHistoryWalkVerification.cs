// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api.Steps;
using Nethermind.State.Flat.History;

namespace Nethermind.Init.Steps;

/// <summary>Starts the every-block history verification; a no-op when <c>FlatDb.HistoryVerifyEveryBlock</c> is
/// off.</summary>
[RunnerStepDependencies(dependencies: [typeof(InitializeNetwork)])]
public class StartHistoryWalkVerification(HistoryWalkVerificationCoordinator coordinator) : IStep
{
    public Task Execute(CancellationToken cancellationToken)
    {
        coordinator.Start();
        return Task.CompletedTask;
    }
}
