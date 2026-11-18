// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api.Steps;
using Nethermind.State.Flat.Migration;

namespace Nethermind.Init.Steps;

/// <summary>Points a flat DB the migration has just started on at the head's state, see <see cref="HalfPathMigrator.PointAtHeadOnFirstStart"/>.</summary>
/// <remarks>
/// A no-op unless a migration is in progress and flat has no state yet, which is only its first start: on a restart flat already
/// holds the state block processing has moved it to. It runs before anything reads state, which on AuRa includes building block processing.
/// </remarks>
[RunnerStepDependencies(
    dependencies: [typeof(InitializeBlockTree)],
    dependents: [typeof(InitializeBlockchain)]
)]
public class PreparePruningTrieStateMigration(
    FlatStateActivationPolicy activationPolicy,
    Lazy<HalfPathMigrationCursor> migrationCursor,
    Lazy<HalfPathMigrator> migrator) : IStep
{
    public Task Execute(CancellationToken cancellationToken)
    {
        if (!activationPolicy.ShouldTurnOnFlatDb() || migrationCursor.Value.Current is null) return Task.CompletedTask;

        migrator.Value.PointAtHeadOnFirstStart();
        return Task.CompletedTask;
    }
}
