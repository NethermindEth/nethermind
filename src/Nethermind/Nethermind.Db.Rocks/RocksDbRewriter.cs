// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Abstractions;
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
/// Some RocksDB options only apply to newly written files, so changing them otherwise needs a resync.
/// <see cref="Rewrite"/> copies every column family into a new database and swaps it into place;
/// <see cref="Compact"/> rewrites the files in place by forcing a compaction of every column family.
/// </remarks>
public class RocksDbRewriter(
    IInitConfig initConfig,
    IDbConfig dbConfig,
    IRocksDbConfigFactory rocksDbConfigFactory,
    HyperClockCacheWrapper sharedCache,
    IFileSystem fileSystem,
    ILogManager logManager)
{
    private const string RewriteSuffix = "_rewrite";
    private const string OldSuffix = "_old";
    private static readonly nuint ChunkBytes = (nuint)1.MiB;
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CompactionLogInterval = TimeSpan.FromSeconds(1);
    private const string CompactReadBytes = "rocksdb.compact.read.bytes";
    private const string CompactWriteBytes = "rocksdb.compact.write.bytes";

    private readonly ILogger _logger = logManager.GetClassLogger<RocksDbRewriter>();
    private readonly StatisticsDbFactory _dbFactory = new(rocksDbConfigFactory, dbConfig, initConfig, sharedCache, fileSystem, logManager);

    /// <summary>Rewrites the database in <paramref name="dbPath"/> and swaps it into place.</summary>
    /// <param name="dbPath">The database directory under the base DB path, e.g. <c>receipts</c>.</param>
    /// <param name="cancellationToken">Stops the copy, leaving the original database in place.</param>
    /// <exception cref="InvalidConfigurationException">
    /// <paramref name="dbPath"/> does not name a RocksDB database this can rewrite safely, or an earlier rewrite was
    /// interrupted after the old database was moved aside.
    /// </exception>
    /// <remarks>Needs free space for a second copy of the database, which a failed or cancelled copy deletes.</remarks>
    public async Task Rewrite(string dbPath, CancellationToken cancellationToken)
    {
        string fullPath = GetExistingFullPath(dbPath);
        string rewriteFullPath = fullPath + RewriteSuffix;
        string oldFullPath = fullPath + OldSuffix;

        (DbOnTheRocks source, string[] families, List<string>? columns) = OpenExisting(dbPath, fullPath);
        using (source)
        {
            // Only after the source holds its lock, so a concurrent run cannot have its copy deleted. The old
            // database was never moved aside, so a leftover rewrite is only a partial copy.
            if (Directory.Exists(rewriteFullPath)) Directory.Delete(rewriteFullPath, true);
            if (_logger.IsInfo) _logger.Info($"Rewriting {fullPath} with column families {string.Join(", ", families)}");

            try
            {
                using DbOnTheRocks target = Open(dbPath, dbPath + RewriteSuffix, columns);
                foreach (string family in families)
                {
                    await Copy(source, target, columns is null ? null : family, cancellationToken);
                }
            }
            catch
            {
                // A copy can be as large as the database, so it is not left behind until the next run.
                if (Directory.Exists(rewriteFullPath)) Directory.Delete(rewriteFullPath, true);
                throw;
            }
        }

        // Markers kept beside the RocksDB files, such as the flat layout one, belong to the database too.
        foreach (string marker in Directory.GetFiles(fullPath, "*.marker"))
            File.Copy(marker, Path.Join(rewriteFullPath, Path.GetFileName(marker)));

        Directory.Move(fullPath, oldFullPath);
        Directory.Move(rewriteFullPath, fullPath);
        Directory.Delete(oldFullPath, true);

        if (_logger.IsInfo) _logger.Info($"Rewrote {fullPath}");
    }

    /// <summary>Forces a compaction of the whole key range of every column family in <paramref name="dbPath"/>.</summary>
    /// <param name="dbPath">The database directory under the base DB path, e.g. <c>receipts</c>.</param>
    /// <param name="cancellationToken">Interrupts the running compaction.</param>
    /// <exception cref="InvalidConfigurationException">
    /// <paramref name="dbPath"/> does not name a RocksDB database this can compact safely.
    /// </exception>
    /// <exception cref="IOException">RocksDB reported a background error, so the compaction may not have completed.</exception>
    /// <remarks>
    /// The bottommost level is forced too, and blob garbage collection relocates every blob, so every file is
    /// rewritten under the current configuration rather than only the ones the default compaction would pick.
    /// Needs free space for up to a copy of the largest column family.
    /// </remarks>
    public async Task Compact(string dbPath, CancellationToken cancellationToken)
    {
        string fullPath = GetExistingFullPath(dbPath);
        (DbOnTheRocks db, string[] families, List<string>? columns) = OpenExisting(dbPath, fullPath);
        if (_logger.IsInfo) _logger.Info($"Compacting {fullPath} with column families {string.Join(", ", families)}");

        using (db)
        using (cancellationToken.Register(db.InterruptCompactions))
        {
            foreach (string family in families)
            {
                try
                {
                    await CompactFamily(db, columns is null ? null : family, family, cancellationToken);
                }
                catch (RocksDbException) when (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(cancellationToken);
                }
            }
        }
    }

    /// <remarks>
    /// Logs every <see cref="CompactionLogInterval"/> from RocksDB's compaction byte counters. They are database-wide,
    /// so a background compaction running alongside is counted too. Progress is bytes read against
    /// <see cref="EstimateCompactionRead"/>.
    /// </remarks>
    private async Task CompactFamily(DbOnTheRocks db, string? family, string familyName, CancellationToken cancellationToken)
    {
        IColumnFamilyHandle? familyHandle = GetFamily(db, family);
        RelocateBlobs(db, familyHandle);

        string prefix = $"Compacting {db.Name} {familyName}";
        List<(int Level, long Size)> levels = ReadLevelSizes(db, familyHandle);
        long estimatedRead = EstimateCompactionRead(levels);
        if (_logger.IsInfo)
        {
            string[] levelSizes = new string[levels.Count];
            for (int i = 0; i < levels.Count; i++) levelSizes[i] = $"L{levels[i].Level} {Mib(levels[i].Size):N0} MiB";
            _logger.Info($"{prefix}: {levels.Count} levels ({string.Join(", ", levelSizes)}), ~{Mib(estimatedRead):N0} MiB to read");
        }

        (long startRead, long startWritten) = ReadCompactionBytes(db);
        (long lastRead, long lastWritten) = (startRead, startWritten);
        long start = Stopwatch.GetTimestamp();
        long lastLog = start;

        Task compaction = Task.Factory.StartNew(
            () => db.CompactOpenRange(familyHandle, forceBottommost: true), TaskCreationOptions.LongRunning);
        while (await Task.WhenAny(compaction, Task.Delay(CompactionLogInterval)) != compaction)
        {
            (long read, long written) = ReadCompactionBytes(db);
            double seconds = Stopwatch.GetElapsedTime(lastLog).TotalSeconds;
            lastLog = Stopwatch.GetTimestamp();
            float percentage = Math.Clamp((read - startRead) / (float)Math.Max(estimatedRead, 1L), 0, 1);
            if (_logger.IsInfo)
                _logger.Info($"{prefix} {Mib(read - startRead):N0} / ~{Mib(estimatedRead):N0} MiB read " +
                    $"({percentage.ToString("P2", CultureInfo.InvariantCulture),8}) {Progress.GetMeter(percentage, 1)}| " +
                    $"{Mib(read - lastRead) / seconds:N1} MiB/s read | {Mib(written - lastWritten) / seconds:N1} MiB/s written");
            (lastRead, lastWritten) = (read, written);
        }

        await compaction;
        // An interrupted compaction returns normally, and the C API discards the status of a failed one.
        cancellationToken.ThrowIfCancellationRequested();
        if (!db._db.TryGetIntProperty("rocksdb.background-errors", out ulong errors) || errors != 0)
            throw new IOException($"RocksDB reported background errors while compacting {db.Name} {familyName}, so it may not have completed.");

        (long totalRead, long totalWritten) = ReadCompactionBytes(db);
        if (_logger.IsInfo)
            _logger.Info($"Compacted {db.Name} {familyName} in {Stopwatch.GetElapsedTime(start)}: " +
                $"{Mib(totalRead - startRead):N0} MiB read, {Mib(totalWritten - startWritten):N0} MiB written");
    }

    /// <summary>Makes the compaction move every value out of the existing blob files.</summary>
    /// <remarks>
    /// A compaction otherwise rewrites only the blob references, so values written while blob files were enabled
    /// stay in them. The options last only while the database is open here; the node opens it with its own.
    /// </remarks>
    private static void RelocateBlobs(DbOnTheRocks db, IColumnFamilyHandle? family)
    {
        const string numBlobFiles = "rocksdb.num-blob-files";
        bool known = family is null
            ? db._db.TryGetIntProperty(numBlobFiles, out ulong blobFiles)
            : db._db.TryGetIntProperty(numBlobFiles, family, out blobFiles);
        if (known && blobFiles == 0) return;

        KeyValuePair<string, string>[] options =
        [
            new("enable_blob_garbage_collection", "true"),
            new("blob_garbage_collection_age_cutoff", "1.0"),
        ];
        if (family is null) db._db.SetOptions(options);
        else db._db.SetOptions(family, options);
    }

    private static double Mib(long bytes) => bytes / 1024.0 / 1024.0;

    /// <summary>The non-empty levels of a column family and their size, top level first.</summary>
    /// <remarks>
    /// <c>rocksdb.levelstats</c> is two header lines, then one <c>level files size</c> row per level with the size in
    /// whole MiB, so a level under half a MiB reads as empty.
    /// </remarks>
    private static List<(int Level, long Size)> ReadLevelSizes(DbOnTheRocks db, IColumnFamilyHandle? family)
    {
        const string levelStats = "rocksdb.levelstats";
        string stats = (family is null ? db._db.GetProperty(levelStats) : db._db.GetProperty(levelStats, family)) ?? "";

        List<(int Level, long Size)> levels = [];
        string[] rows = stats.Split('\n');
        for (int i = 2; i < rows.Length; i++)
        {
            string[] columns = rows[i].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (columns.Length != 3) continue;

            long size = (long)(double.Parse(columns[2], CultureInfo.InvariantCulture) * 1.MiB);
            if (size > 0) levels.Add((int.Parse(columns[0], CultureInfo.InvariantCulture), size));
        }

        return levels;
    }

    /// <summary>Estimates how much a forced manual compaction of the given levels reads.</summary>
    /// <remarks>
    /// The compaction merges the levels top down, so each merge reads everything above plus the level it lands on;
    /// <c>kForce</c> then rewrites the bottom level, by then all of the data, once more. Overwritten and deleted
    /// entries dropping out on the way make the real figure smaller.
    /// </remarks>
    private static long EstimateCompactionRead(List<(int Level, long Size)> levels)
    {
        long above = 0;
        long read = 0;
        foreach ((_, long size) in levels)
        {
            if (above > 0) read += above + size;
            above += size;
        }

        // Data only in L0 is still compacted out of it before the bottom pass.
        if (levels is [(0, _)]) read += above;

        return read + above;
    }

    private static (long Read, long Written) ReadCompactionBytes(DbOnTheRocks db)
    {
        string statistics = db._db.GetProperty("rocksdb.options-statistics") ?? "";
        return (ReadTicker(statistics, CompactReadBytes), ReadTicker(statistics, CompactWriteBytes));
    }

    /// <remarks>
    /// A ticker is a line of the statistics string reading <c>name COUNT : value</c>. The compaction tickers are
    /// never the first line, so matching after a line break keeps a longer name ending in the same text out.
    /// </remarks>
    private static long ReadTicker(string statistics, string name)
    {
        string label = "\n" + name + " COUNT : ";
        int start = statistics.IndexOf(label, StringComparison.Ordinal);
        if (start < 0) return 0;

        start += label.Length;
        int end = statistics.IndexOf('\n', start);
        return long.Parse(statistics.AsSpan(start, (end < 0 ? statistics.Length : end) - start), CultureInfo.InvariantCulture);
    }

    /// <exception cref="InvalidConfigurationException">
    /// <paramref name="dbPath"/> does not name a RocksDB database directly under the base DB path that can be opened
    /// here without losing data or state kept beside it.
    /// </exception>
    private string GetExistingFullPath(string dbPath)
    {
        // A path could reach outside the base DB path, and a trailing separator would nest the rewrite in the source.
        if (dbPath is "" or "." or ".." || dbPath.AsSpan().IndexOfAny(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) >= 0)
            throw Reject($"'{dbPath}' is not the name of a database directory under {initConfig.BaseDbPath}, e.g. receipts.");

        // Its columns need the merge operators of the running log index to read or compact pending merges.
        if (string.Equals(dbPath, DbNames.LogIndex, StringComparison.OrdinalIgnoreCase))
            throw Reject($"{DbNames.LogIndex} cannot be rewritten or compacted.");

        string fullPath = DbOnTheRocks.GetFullDbPath(dbPath, initConfig.BaseDbPath);
        string oldFullPath = fullPath + OldSuffix;
        bool exists = File.Exists(Path.Join(fullPath, "CURRENT"));
        if (Directory.Exists(oldFullPath))
            throw Reject(exists
                ? $"{oldFullPath} is left over from an interrupted rewrite that already replaced {fullPath}. Delete it first."
                : $"{fullPath} is missing after an interrupted rewrite. Move the complete copy {fullPath + RewriteSuffix} to {fullPath}, then delete {oldFullPath}.");

        if (!exists) throw Reject($"There is no RocksDB database at {fullPath}.");

        // A rewrite lands beside the link, on the base DB disk, and leaves the linked data behind.
        if (new DirectoryInfo(fullPath).LinkTarget is not null)
            throw Reject($"{fullPath} is a link. Run this on the directory it links to instead.");

        // Opening repairs a corrupt database without recording the repair, and a rewrite drops the record, so the
        // flat state would never learn that it needs to resync.
        if (File.Exists(RocksDbInstance.CorruptMarkerPath(fullPath)) || File.Exists(RocksDbInstance.RepairedMarkerPath(fullPath)))
            throw Reject($"{fullPath} has a pending repair. Start the node to complete it first.");

        return fullPath;
    }

    private static InvalidConfigurationException Reject(string message) => new(message, ExitCodes.ForbiddenOptionValue);

    /// <summary>Opens an existing database under the current config, with every column family it holds.</summary>
    /// <returns>
    /// The database, its column family names, and the columns it was opened with, or <c>null</c> when it has no
    /// column families besides the default one.
    /// </returns>
    private (DbOnTheRocks Db, string[] Families, List<string>? Columns) OpenExisting(string dbPath, string fullPath)
    {
        string[] families;
        using (DbOptions options = new()) families = [.. RocksDb.ListColumnFamilies(options, fullPath)];

        // A database opened without column families keeps everything in the default family under the table config.
        List<string>? columns = null;
        if (families.Length != 1)
        {
            columns = [];
            foreach (string family in families)
            {
                if (family != ColumnFamilies.DefaultName) columns.Add(family);
            }
        }

        DbOnTheRocks db = Open(dbPath, dbPath, columns);
        if (columns is null || IsEmpty(db, ColumnFamilies.DefaultName)) return (db, families, columns);

        // A columns DB configures its default family only when its column enum has `Default`, and only those
        // write to it, so a default family holding data is reopened under its column config.
        db.Dispose();
        columns.Add("Default");
        return (Open(dbPath, dbPath, columns), families, columns);
    }

    /// <param name="dbName">The database the config is read for, whatever directory it is opened from.</param>
    private DbOnTheRocks Open(string dbName, string dbPath, IList<string>? columns)
    {
        DbSettings settings = new(char.ToUpperInvariant(dbName[0]) + dbName[1..], dbPath);
        IRocksDbConfig tableConfig = rocksDbConfigFactory.GetForDatabase(settings.DbName, null);
        (string, IRocksDbConfig)[]? columnConfigs = null;
        if (columns is not null)
        {
            columnConfigs = new (string, IRocksDbConfig)[columns.Count];
            for (int i = 0; i < columns.Count; i++)
                columnConfigs[i] = (columns[i], rocksDbConfigFactory.GetForDatabase(settings.DbName, columns[i]));
        }

        using RocksDbInstance instance = _dbFactory.Open(settings, tableConfig, columnConfigs);
        return new DbOnTheRocks(instance, null, settings.DbName, tableConfig, logManager);
    }

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

        ProgressLogger progress = CreateProgress(source, sourceFamily, family);
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

                progress.Update((ulong)count);
                if (Stopwatch.GetElapsedTime(lastProgress) < ProgressInterval) continue;
                lastProgress = Stopwatch.GetTimestamp();
                progress.LogProgress();
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
            batches.Writer.TryComplete();
            try
            {
                await writers;
            }
            finally
            {
                // Left over when the writers stopped early on cancellation or a failure.
                while (batches.Reader.TryRead(out WriteBatch? pending)) pending.Dispose();
            }
        }

        // DbOnTheRocks.Flush logs a failure instead of throwing, which would swap in a copy missing its memtables.
        using FlushOptions flushOptions = new();
        if (targetFamily is null) target._db.Flush(flushOptions);
        else target._db.Flush(flushOptions, targetFamily);
        if (_logger.IsInfo) _logger.Info($"Rewrote {source.Name} {family}: {count:N0} entries");
    }

    /// <remarks>The target is RocksDB's key count estimate, so the copy can finish short of or past it.</remarks>
    private ProgressLogger CreateProgress(DbOnTheRocks source, IColumnFamilyHandle? sourceFamily, string? family)
    {
        const string estimateNumKeys = "rocksdb.estimate-num-keys";
        bool hasEstimate = sourceFamily is null
            ? source._db.TryGetIntProperty(estimateNumKeys, out ulong estimate)
            : source._db.TryGetIntProperty(estimateNumKeys, sourceFamily, out estimate);

        string prefix = $"Rewriting {source.Name} {family}";
        ProgressLogger progress = new(prefix, logManager);
        progress.Reset(0, hasEstimate ? estimate : 0);
        progress.SetFormat(p =>
        {
            float percentage = Math.Clamp(p.CurrentValue / (float)Math.Max(p.TargetValue, 1UL), 0, 1);
            return $"{prefix} {p.CurrentValue:N0} / ~{p.TargetValue:N0} ({percentage.ToString("P2", CultureInfo.InvariantCulture),8}) " +
                $"{Progress.GetMeter(percentage, 1)}| {p.CurrentPerSecond:N0} keys/s";
        });
        return progress;
    }

    /// <summary>Opens databases that collect statistics for the compaction progress log.</summary>
    private sealed class StatisticsDbFactory(
        IRocksDbConfigFactory rocksDbConfigFactory,
        IDbConfig dbConfig,
        IInitConfig initConfig,
        HyperClockCacheWrapper sharedCache,
        IFileSystem fileSystem,
        ILogManager logManager)
        : RocksDbFactory(rocksDbConfigFactory, dbConfig, initConfig, sharedCache, fileSystem, logManager)
    {
        protected override RocksDb DoOpen(string path, DbOptions options, ColumnFamilies? families)
        {
            options.EnableStatistics();
            return base.DoOpen(path, options, families);
        }
    }
}
