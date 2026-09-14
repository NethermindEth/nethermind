// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Api.Steps;
using Nethermind.Config;
using Nethermind.Init.Steps;
using Nethermind.State.Pbt.Migration;

namespace Nethermind.State.Pbt.Steps;

/// <summary>Seeds the native PBT anchor and starts the BAL follower before networking, RPC or production.</summary>
[RunnerStepDependencies(dependencies: [typeof(LoadGenesisBlock)], dependents: [typeof(InitializeNetwork)])]
internal sealed class InitializePbtMigration(PbtMigrationBootstrap bootstrap, PbtBalFollowerScheduler follower, IProcessExitSource exitSource) : IStep
{
    public async Task Execute(CancellationToken cancellationToken)
    {
        if (await bootstrap.Initialize(cancellationToken))
        {
            exitSource.Exit(0);
            throw new TaskCanceledException("Offline PBT export completed.");
        }
        follower.Start();
    }
}
