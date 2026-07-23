// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api.Steps;
using Nethermind.Config;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State.Flat;
using Nethermind.State.Flat.Persistence;

namespace Nethermind.Init.Steps;

/// <summary>
/// Runs the one-shot <see cref="FlatLayout.PreimageFlatV1"/> to <see cref="FlatLayout.PreimageFlat"/> storage key
/// migration and exits, so the node is restarted on the migrated layout.
/// </summary>
/// <remarks>
/// Runs before <see cref="InitializeBlockchain"/>, which also gates <see cref="InitializeNetwork"/>, so nothing has
/// resolved <see cref="IPersistence"/> yet and the flat DB is quiescent while the storage column is rewritten. For
/// the same reason this step must not depend on <see cref="IPersistence"/> itself: the DB is on the old layout
/// throughout the run, and only the next run configures the new one.
/// </remarks>
[RunnerStepDependencies(dependencies: [], dependents: [typeof(InitializeBlockchain)])]
public class MigratePreimageStorageKeys(
    IColumnsDb<FlatDbColumns> flatDb,
    IDbFactory dbFactory,
    IProcessExitSource exitSource,
    ILogManager logManager
) : IStep
{
    private readonly ILogger _logger = logManager.GetClassLogger<MigratePreimageStorageKeys>();

    public async Task Execute(CancellationToken cancellationToken)
    {
        PreimageStorageKeyMigration migration = new(flatDb, CreateScratchDb, logManager);

        try
        {
            if (!await Task.Run(() => migration.Run(cancellationToken), cancellationToken))
            {
                DeleteLeftoverScratchDb();
                return;
            }
        }
        catch (OperationCanceledException)
        {
            if (_logger.IsInfo) _logger.Info("Migration cancelled. Run with --FlatDb.MigrateToPreimageFlat true again to resume it.");
            exitSource.Exit(1);
            return;
        }

        if (_logger.IsWarn) _logger.Warn($"Restart with --FlatDb.Layout {nameof(FlatLayout.PreimageFlat)} to run on the migrated flat DB.");
        exitSource.Exit(0);
    }

    /// <summary>Holds a converted copy of the whole storage column, so it needs as much disk as that column uses.</summary>
    private IDb CreateScratchDb() => dbFactory.CreateDb(ScratchDbSettings);

    /// <summary>
    /// Drops a scratch DB left behind by a crash between the layout stamp and the migration's own cleanup, which
    /// would otherwise keep a full-size copy of the storage column around unnoticed.
    /// </summary>
    private void DeleteLeftoverScratchDb()
    {
        string path = dbFactory.GetFullDbPath(ScratchDbSettings);
        if (!Directory.Exists(path)) return;

        if (_logger.IsWarn) _logger.Warn($"Deleting the migration scratch DB left over at {path}.");
        Directory.Delete(path, recursive: true);
    }

    private static DbSettings ScratchDbSettings => new("PreimageKeyMigration", "preimageKeyMigration") { SkipMetricsTracking = true };
}
