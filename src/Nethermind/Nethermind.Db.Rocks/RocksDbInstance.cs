// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using Nethermind.Core.Utils;
using Nethermind.Logging;
using Nethermind.RocksDbBindings;

namespace Nethermind.Db.Rocks;

/// <summary>
/// An open native database together with the native state that has to live exactly as long as it.
/// </summary>
/// <remarks>
/// <see cref="RocksDbFactory"/> opens it holding one lease. Every <see cref="DbOnTheRocks"/> over it, one per column
/// for a column database, acquires its own lease, and the native database closes when the last lease is released.
/// </remarks>
public sealed class RocksDbInstance(
    RocksDb db,
    DbSettings settings,
    string fullPath,
    DbOptions dbOptions,
    ColumnFamilies? columnFamilies,
    IReadOnlyList<IDisposable> metricsUpdaters,
    IReadOnlyList<Cache> rowCaches,
    long memoryPressure,
    bool usesSharedBlockCache,
    bool wasRepairedOnOpen,
    IFileSystem fileSystem,
    Action fatalShutdown,
    ILogger logger) : RefCountingDisposable
{
    internal RocksDb Db => db;

    internal string Name => settings.DbName;

    internal bool UsesSharedBlockCache => usesSharedBlockCache;

    public bool WasRepairedOnOpen { get; private set; } = wasRepairedOnOpen;

    public void AcknowledgeRepair()
    {
        fileSystem.File.Delete(RepairedMarkerPath(fullPath));
        WasRepairedOnOpen = false;
    }

    internal static string CorruptMarkerPath(string fullPath) => Path.Join(fullPath, "corrupt.marker");

    internal static string RepairedMarkerPath(string fullPath) => Path.Join(fullPath, "repaired.marker");

    internal void HandleFatalDbError(RocksDbException rocksDbException) =>
        HandleFatalDbError(rocksDbException, Name, fullPath, fileSystem, fatalShutdown, logger);

    internal static void HandleFatalDbError(RocksDbException rocksDbException, string name, string fullPath,
        IFileSystem fileSystem, Action fatalShutdown, ILogger logger)
    {
        bool corruption = rocksDbException.Message.Contains("Corruption:", StringComparison.Ordinal);
        bool ioError = rocksDbException.Message.Contains("IO error", StringComparison.Ordinal);
        if (!corruption && !ioError)
        {
            return;
        }

        // Only genuine on-disk corruption (RocksDB reports it as "Corruption:") gets the marker,
        // which schedules the lossy rocksdb_repair_db on the next start. A transient/environmental
        // "IO error" (fd exhaustion, a full disk, revoked permissions) is not on-disk corruption:
        // repair cannot fix it and running it against an otherwise-healthy DB destroys data, so we
        // never write the marker for it. Either way we fast-shutdown, because continuing past a
        // failed write would apply later writes as if it had succeeded and corrupt state at the
        // application layer even when the DB files themselves are intact.
        if (corruption)
        {
            if (logger.IsWarn) logger.Warn($"Corrupted DB detected on path {fullPath}. Please restart Nethermind to attempt repair.");
            fileSystem.File.WriteAllText(CorruptMarkerPath(fullPath), "marker");
        }
        else if (logger.IsWarn)
        {
            logger.Warn($"IO error on DB path {fullPath}. Shutting down without repair; please restart Nethermind.");
        }

        logger.Error($"Fast shutdown due to {name} DB {(corruption ? "corruption" : "IO error")}. Please restart.");
        fatalShutdown();
    }

    internal void Delete() => Delete(fullPath, settings, logger);

    internal static void Delete(string fullPath, DbSettings settings, ILogger logger)
    {
        try
        {
            if (Directory.Exists(fullPath))
            {
                // We want to keep the folder if it can have subfolders with copied databases from pruning
                if (settings.CanDeleteFolder)
                {
                    Directory.Delete(fullPath, true);
                }
                else
                {
                    foreach (string file in Directory.EnumerateFiles(fullPath))
                    {
                        File.Delete(file);
                    }
                }
            }
        }
        catch (Exception e)
        {
            if (logger.IsWarn) logger.Warn($"Could not delete the {settings.DbName} database. {e.Message}");
        }
    }

    protected override void CleanUp()
    {
        foreach (IDisposable metricsUpdater in metricsUpdaters)
        {
            metricsUpdater.Dispose();
        }

        db.Dispose();

        foreach (Cache rowCache in rowCaches)
        {
            rowCache.Dispose();
        }

        if (memoryPressure > 0) GC.RemoveMemoryPressure(memoryPressure);

        // A merge operator is called back through the options it was set on, so they must outlive the database.
        GC.KeepAlive(dbOptions);
        GC.KeepAlive(columnFamilies);
    }
}
