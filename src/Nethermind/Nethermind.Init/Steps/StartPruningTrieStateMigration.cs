// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api.Steps;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Exceptions;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State.Flat;
using Nethermind.State.Flat.Migration;
using Nethermind.State.Flat.Persistence;
using Nethermind.Trie;

namespace Nethermind.Init.Steps;

/// <summary>Points a flat DB that starts migrating from the patricia trie state at the head's state, then starts the copy.</summary>
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
    Lazy<IPersistence> persistence,
    Lazy<IPersistenceManager> persistenceManager,
    Lazy<HalfPathMigrator> migrator,
    Lazy<INodeStorage> nodeStorage,
    IBlockTree blockTree,
    ILogManager logManager) : IStep
{
    public Task Execute(CancellationToken cancellationToken)
    {
        if (!activationPolicy.ShouldTurnOnFlatDb() || migrationCursor.Value.Current is null) return Task.CompletedTask;

        StateId current;
        using (IPersistence.IPersistenceReader reader = persistence.Value.CreateReader())
        {
            current = reader.CurrentState;
        }

        if (current == StateId.PreGenesis) PointAtHead();
        migrator.Value.Start();
        return Task.CompletedTask;
    }

    private void PointAtHead()
    {
        BlockHeader head = blockTree.Head?.Header
            ?? throw new InvalidConfigurationException(
                $"Cannot migrate from the patricia trie state: the block tree has no head. Remove FlatDb.{nameof(IFlatDbConfig.MigrateFromPruningTrieState)} and delete the '{DbNames.Flat}' directory to start the node normally.",
                ExitCodes.ForbiddenOptionValue);

        if (head.StateRoot is null || !nodeStorage.Value.KeyExists(null, TreePath.Empty, head.StateRoot.ValueHash256))
        {
            throw new InvalidConfigurationException(
                $"Cannot migrate from the patricia trie state: it does not contain head state root {head.StateRoot}. Remove FlatDb.{nameof(IFlatDbConfig.MigrateFromPruningTrieState)} and delete the '{DbNames.Flat}' directory to start the node normally.",
                ExitCodes.ForbiddenOptionValue);
        }

        ILogger logger = logManager.GetClassLogger<StartPruningTrieStateMigration>();
        if (logger.IsInfo) logger.Info($"Migrating to the flat DB from the patricia trie state at {head.ToString(BlockHeader.Format.Short)} with state root {head.StateRoot}.");

        // Through the resolved persistence, so the reader caches follow the new state.
        persistence.Value.CreateWriteBatch(StateId.PreGenesis, new StateId(head)).Dispose();
        persistence.Value.Flush();
        persistenceManager.Value.ResetPersistedStateId();
    }
}
