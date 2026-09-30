// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Api.Steps;
using Nethermind.Init.Steps;
using Nethermind.State.Pbt.Migration;

namespace Nethermind.State.Pbt.Steps;

/// <summary>Starts seeding the native PBT anchor and the BAL followers before networking, RPC or production.</summary>
/// <remarks>A configured anchor import runs in the background; the BAL follower starts once it lands.</remarks>
[RunnerStepDependencies(dependencies: [typeof(LoadGenesisBlock), typeof(StartMonitoring)], dependents: [typeof(InitializeNetwork)])]
internal sealed class InitializePbtMigration(
    PbtMigrationBootstrap bootstrap,
    PbtMigrationImport import,
    PbtBalFollowerScheduler follower,
    PbtBranchFollower branchFollower,
    MerkleShadowFollower merkleShadow,
    IPbtConfig config) : IStep
{
    public async Task Execute(CancellationToken cancellationToken)
    {
        branchFollower.Start();
        if (PbtMigrationBootstrap.HasSource(config))
        {
            import.Start();
        }
        else
        {
            await bootstrap.Initialize(cancellationToken);
            follower.Start();
        }
        merkleShadow.Start();
    }
}
