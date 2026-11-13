// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api.Steps;
using Nethermind.State.Flat.Migration;

namespace Nethermind.Init.Steps;

/// <summary>Starts the migration from the patricia trie state, see <see cref="HalfPathMigrator.Start"/>.</summary>
/// <remarks>
/// A no-op unless a migration is in progress. The flat components are resolved only then, so a patricia node does not open the flat DB.
/// </remarks>
[RunnerStepDependencies(
    dependencies: [typeof(InitializeBlockTree)],
    dependents: [typeof(InitializeBlockchain)]
)]
public class StartPruningTrieStateMigration(
    FlatStateActivationPolicy activationPolicy,
    Lazy<HalfPathMigrationCursor> migrationCursor,
    Lazy<HalfPathMigrator> migrator) : IStep
{
    public Task Execute(CancellationToken cancellationToken)
    {
        if (!activationPolicy.ShouldTurnOnFlatDb() || migrationCursor.Value.Current is null) return Task.CompletedTask;

        migrator.Value.Start();
        return Task.CompletedTask;
    }
}
