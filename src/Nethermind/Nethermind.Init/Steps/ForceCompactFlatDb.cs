// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api.Steps;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Logging;
using Nethermind.State.Flat;

namespace Nethermind.Init.Steps;

/// <summary>
/// Rewrites the flat Account and Storage columns with a forced full RocksDB compaction when
/// <see cref="IDbConfig.FlatDbForceCompactOnStart"/> is set, so that an existing database adopts RocksDB options
/// that only apply to newly written SSTs (compression, block format).
/// </summary>
/// <remarks>
/// Runs before <see cref="InitializeBlockchain"/> so no block is processed against the half-rewritten column set.
/// The compaction is blocking and can take hours on a mainnet-sized database, hence the opt-in flag.
/// </remarks>
[RunnerStepDependencies(
    dependencies: [typeof(InitializeBlockTree)],
    dependents: [typeof(InitializeBlockchain)]
)]
public class ForceCompactFlatDb(
    IColumnsDb<FlatDbColumns> flatDb,
    IDbConfig dbConfig,
    ILogManager logManager
) : IStep
{
    private static readonly FlatDbColumns[] CompactedColumns = [FlatDbColumns.Account, FlatDbColumns.Storage];
    private static readonly TimeSpan ProgressLogInterval = TimeSpan.FromMinutes(1);

    private readonly ILogger _logger = logManager.GetClassLogger<ForceCompactFlatDb>();

    public Task Execute(CancellationToken cancellationToken) =>
        dbConfig.FlatDbForceCompactOnStart
            // Offloaded: the compaction blocks for as long as it takes, and the steps manager may otherwise run it
            // inline on the thread that is still creating the remaining steps.
            ? Task.Run(() => CompactColumns(cancellationToken), cancellationToken)
            : Task.CompletedTask;

    private void CompactColumns(CancellationToken cancellationToken)
    {
        if (_logger.IsInfo) _logger.Info($"Forced full compaction of the flat db requested by {nameof(IDbConfig.FlatDbForceCompactOnStart)}. Block processing waits for it to complete.");

        long startTimestamp = Stopwatch.GetTimestamp();
        foreach (FlatDbColumns column in CompactedColumns)
        {
            if (cancellationToken.IsCancellationRequested) return;
            CompactColumn(column);
        }

        if (_logger.IsInfo) _logger.Info($"Forced full compaction of the flat db completed in {Stopwatch.GetElapsedTime(startTimestamp).TotalSeconds:N0}s.");
    }

    private void CompactColumn(FlatDbColumns column)
    {
        if (flatDb.GetColumnDb(column) is not ColumnDb columnDb)
        {
            if (_logger.IsWarn) _logger.Warn($"Flat {column} column is not backed by RocksDB. Skipping its forced compaction.");
            return;
        }

        long sizeBefore = columnDb.SstFilesSize;
        if (_logger.IsInfo) _logger.Info($"Forced full compaction of the flat {column} column started. SST files size {sizeBefore / 1L.MiB:N0} MB.");

        long startTimestamp = Stopwatch.GetTimestamp();

        // RocksDB reports no progress for a manual compaction, so report the live SST size instead — it moves as
        // files are rewritten, which is enough to tell a running compaction from a hung one.
        using (new Timer(
            _ => { if (_logger.IsInfo) _logger.Info($"Forced full compaction of the flat {column} column still running after {Stopwatch.GetElapsedTime(startTimestamp).TotalMinutes:N0}min. SST files size {columnDb.SstFilesSize / 1L.MiB:N0} MB."); },
            null, ProgressLogInterval, ProgressLogInterval))
        {
            columnDb.ForceFullCompaction();
        }

        long sizeAfter = columnDb.SstFilesSize;
        if (_logger.IsInfo) _logger.Info($"Forced full compaction of the flat {column} column completed in {Stopwatch.GetElapsedTime(startTimestamp).TotalSeconds:N0}s. SST files size {sizeBefore / 1L.MiB:N0} MB -> {sizeAfter / 1L.MiB:N0} MB.");
    }
}
