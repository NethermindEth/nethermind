// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Api.Steps;
using Nethermind.Init.Steps;
using Nethermind.State.Pbt.Migration;

namespace Nethermind.State.Pbt.Steps;

/// <summary>Seeds the native PBT anchor and starts the BAL followers before networking, RPC or production.</summary>
[RunnerStepDependencies(dependencies: [typeof(LoadGenesisBlock)], dependents: [typeof(InitializeNetwork)])]
internal sealed class InitializePbtMigration(PbtMigrationBootstrap bootstrap, PbtBalFollowerScheduler follower, MerkleShadowFollower merkleShadow) : IStep
{
    public async Task Execute(CancellationToken cancellationToken)
    {
        await bootstrap.Initialize(cancellationToken);
        follower.Start();
        merkleShadow.Start();
    }
}
