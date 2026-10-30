// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Extensions;
using Nethermind.Db.Rocks.Config;
using Nethermind.Logging;
using Nethermind.RocksDbBindings;

namespace Nethermind.Db.Rocks;

/// <summary>Rewrites a database under the current RocksDB configuration.</summary>
/// <remarks>
/// Some RocksDB options only apply to newly written files, so changing them otherwise needs a resync. This copies
/// every column family of the database into a new one opened with the current configuration, then swaps the new
/// database into place and deletes the old one.
/// </remarks>
public class RocksDbRewriter(
    IInitConfig initConfig,
    IDbConfig dbConfig,
    IRocksDbConfigFactory rocksDbConfigFactory,
    HyperClockCacheWrapper sharedCache,
    ILogManager logManager)
{
    private const string RewriteSuffix = "_rewrite";
    private const string OldSuffix = "_old";
    private static readonly nuint ChunkBytes = (nuint)1.MiB;
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(10);

    private readonly ILogger _logger = logManager.GetClassLogger<RocksDbRewriter>();

    /// <summary>Rewrites the database in <paramref name="dbPath"/> and swaps it into place.</summary>
    /// <param name="dbPath">The database directory under the base DB path, e.g. <c>receipts</c>.</param>
    /// <param name="cancellationToken">Stops the copy, leaving the original database in place.</param>
    /// <exception cref="InvalidConfigurationException">
    /// There is no RocksDB database at <paramref name="dbPath"/>, or an earlier rewrite was interrupted after the
    /// old database was moved aside.
    /// </exception>
    public async Task Rewrite(string dbPath, CancellationToken cancellationToken)
    {
        string fullPath = DbOnTheRocks.GetFullDbPath(dbPath, initConfig.BaseDbPath);
        string rewriteFullPath = fullPath + RewriteSuffix;
        string oldFullPath = fullPath + OldSuffix;

        if (!File.Exists(Path.Join(fullPath, "CURRENT")))
            throw new InvalidConfigurationException($"There is no RocksDB database at {fullPath}.", ExitCodes.ForbiddenOptionValue);

        if (Directory.Exists(oldFullPath))
            throw new InvalidConfigurationException(
                $"{oldFullPath} is left over from an interrupted rewrite. Restore or delete it before rewriting {dbPath}.",
                ExitCodes.ForbiddenOptionValue);

        // The old database was never moved aside, so a leftover rewrite is only a partial copy.
        if (Directory.Exists(rewriteFullPath)) Directory.Delete(rewriteFullPath, true);

        string[] families;
        using (DbOptions options = new()) families = [.. RocksDb.ListColumnFamilies(options, fullPath)];

        // A database opened without column families keeps everything in the default family under the table config.
        List<string>? columns = families.Length == 1 ? null : [.. families.Where(static f => f != ColumnFamilies.DefaultName)];

        if (_logger.IsInfo) _logger.Info($"Rewriting {fullPath} with column families {string.Join(", ", families)}");

        using (DbOnTheRocks source = Open(dbPath, dbPath, columns))
        {
            // A columns DB configures its default family only when its column enum has `Default`, and only those
            // write to it, so an empty default family is the one opened without a column config.
            if (columns is not null && !IsEmpty(source, ColumnFamilies.DefaultName)) columns.Add("Default");

            using DbOnTheRocks target = Open(dbPath, dbPath + RewriteSuffix, columns);
            foreach (string family in families)
            {
                await Copy(source, target, columns is null ? null : family, cancellationToken);
            }
        }

        Directory.Move(fullPath, oldFullPath);
        Directory.Move(rewriteFullPath, fullPath);
        Directory.Delete(oldFullPath, true);

        if (_logger.IsInfo) _logger.Info($"Rewrote {fullPath}");
    }

    /// <param name="dbName">The database the config is read for, whatever directory it is opened from.</param>
    private DbOnTheRocks Open(string dbName, string dbPath, IList<string>? columns) =>
        new ColumnCreatingDb(initConfig.BaseDbPath, new DbSettings(char.ToUpperInvariant(dbName[0]) + dbName[1..], dbPath), dbConfig,
            rocksDbConfigFactory, logManager, columns, sharedCache.Handle);

    /// <param name="family">The column family, or <c>null</c> for a database opened without column families.</param>
    private static IColumnFamilyHandle? GetFamily(DbOnTheRocks db, string? family) =>
        family is null ? null : db._db.GetColumnFamily(family);

    private static bool IsEmpty(DbOnTheRocks db, string family)
    {
        using ReadOptions readOptions = db.CreateReadOptions();
        using Iterator iterator = db.CreateIterator(readOptions, GetFamily(db, family));
        iterator.SeekToFirst();
        return !iterator.Valid();
    }

    /// <remarks>
    /// Reads with one iterator into write batches that concurrent writers commit. The writes skip the WAL, so the
    /// family is flushed at the end; a crash before that leaves only the rewrite directory, which the next run
    /// deletes. Batches are native rather than <see cref="DbOnTheRocks.RocksDbWriteBatch"/>, which commits every few
    /// hundred no-WAL writes on the thread filling it, here the reader.
    /// </remarks>
    private async Task Copy(DbOnTheRocks source, DbOnTheRocks target, string? family, CancellationToken cancellationToken)
    {
        IColumnFamilyHandle? sourceFamily = GetFamily(source, family);
        IColumnFamilyHandle? targetFamily = GetFamily(target, family);

        WriteOptions noWal = target.WriteFlagsToWriteOptions(WriteFlags.DisableWAL)!;
        using CancellationTokenSource writerFailed = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Channel<WriteBatch> batches = Channel.CreateBounded<WriteBatch>(Environment.ProcessorCount);
        Task writers = Parallel.ForEachAsync(
            batches.Reader.ReadAllAsync(writerFailed.Token),
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = writerFailed.Token },
            (writeBatch, _) =>
            {
                try
                {
                    using (writeBatch) target._db.Write(writeBatch, noWal);
                }
                catch
                {
                    // Unblocks the reader, which would otherwise wait forever on a full channel.
                    writerFailed.Cancel();
                    throw;
                }

                return ValueTask.CompletedTask;
            });

        long count = 0;
        WriteBatch? batch = new();
        try
        {
            using ReadOptions readOptions = source.CreateReadOptions();
            readOptions.SetFillCache(false);
            readOptions.SetTotalOrderSeek(true);
            readOptions.SetReadaheadSize(source.ReadAheadSize);
            using Iterator iterator = source.CreateIterator(readOptions, sourceFamily);

            long lastProgress = Stopwatch.GetTimestamp();
            for (iterator.SeekToFirst(); iterator.Valid(); iterator.Next())
            {
                batch.Put(iterator.GetKeySpan(), iterator.GetValueSpan(), targetFamily);
                count++;

                if (batch.DataSize < ChunkBytes) continue;
                await batches.Writer.WriteAsync(batch, writerFailed.Token);
                batch = new();

                if (Stopwatch.GetElapsedTime(lastProgress) < ProgressInterval) continue;
                lastProgress = Stopwatch.GetTimestamp();
                if (_logger.IsInfo) _logger.Info($"Rewriting {source.Name} {family}: {count:N0} entries read");
            }

            // An iterator stops being valid on a read error too, which would otherwise swap in a truncated copy.
            iterator.ThrowIfError();

            await batches.Writer.WriteAsync(batch, writerFailed.Token);
            batch = null;
        }
        finally
        {
            // Not handed to a writer, so still the reader's to dispose.
            batch?.Dispose();
            batches.Writer.Complete();
            await writers;
        }

        // DbOnTheRocks.Flush logs a failure instead of throwing, which would swap in a copy missing its memtables.
        using FlushOptions flushOptions = new();
        if (targetFamily is null) target._db.Flush(flushOptions);
        else target._db.Flush(flushOptions, targetFamily);
        if (_logger.IsInfo) _logger.Info($"Rewrote {source.Name} {family}: {count:N0} entries");
    }

    /// <summary>Opens a database that creates the column families it is given, as <see cref="ColumnsDb{T}"/> does.</summary>
    private sealed class ColumnCreatingDb(
        string basePath,
        DbSettings dbSettings,
        IDbConfig dbConfig,
        IRocksDbConfigFactory rocksDbConfigFactory,
        ILogManager logManager,
        IList<string>? columnFamilies,
        nint sharedCache)
        : DbOnTheRocks(basePath, dbSettings, dbConfig, rocksDbConfigFactory, logManager, columnFamilies, sharedCache: sharedCache)
    {
        protected override void BuildOptions<T>(IRocksDbConfig dbConfig, Options<T> options, nint? sharedCache, IMergeOperator? mergeOperator)
        {
            base.BuildOptions(dbConfig, options, sharedCache, mergeOperator);
            options.SetCreateMissingColumnFamilies();
        }
    }
}
