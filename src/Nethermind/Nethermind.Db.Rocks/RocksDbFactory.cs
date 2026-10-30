// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using FastEnumUtility;
using Nethermind.Api;
using Nethermind.Config;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Extensions;
using Nethermind.Db.Rocks.Config;
using Nethermind.Db.Rocks.Statistics;
using Nethermind.Logging;
using Nethermind.RocksDbBindings;

namespace Nethermind.Db.Rocks;

public class RocksDbFactory : IDbFactory
{
    private readonly IDbConfig _dbConfig;

    private readonly ILogManager _logManager;
    private readonly ILogger _logger;

    private readonly string _basePath;

    private readonly HyperClockCacheWrapper _sharedCache;
    private readonly IRocksDbConfigFactory _rocksDbConfigFactory;
    private readonly IFileSystem _fileSystem;

    private static long _maxRocksSize;

    public RocksDbFactory(IRocksDbConfigFactory rocksDbConfigFactory, IDbConfig dbConfig, IInitConfig initConfig, HyperClockCacheWrapper sharedCache, IFileSystem fileSystem, ILogManager logManager)
        : this(rocksDbConfigFactory, dbConfig, sharedCache, fileSystem, logManager, initConfig.BaseDbPath)
    {

    }

    public RocksDbFactory(IRocksDbConfigFactory rocksDbConfigFactory, IDbConfig dbConfig, HyperClockCacheWrapper sharedCache, IFileSystem fileSystem, ILogManager logManager, string basePath)
    {
        _rocksDbConfigFactory = rocksDbConfigFactory;
        _dbConfig = dbConfig;
        _fileSystem = fileSystem;
        _logManager = logManager;
        _basePath = basePath;

        _logger = _logManager.GetClassLogger<RocksDbFactory>();
        if (_logger.IsDebug) _logger.Debug($"Shared memory size is {dbConfig.SharedBlockCacheSize}");

        _sharedCache = sharedCache;
    }

    public IDb CreateDb(DbSettings dbSettings)
    {
        IRocksDbConfig config = _rocksDbConfigFactory.GetForDatabase(dbSettings.DbName, null);
        using RocksDbInstance instance = Open(dbSettings, config, null);
        return new DbOnTheRocks(instance, null, dbSettings.DbName, config, _logManager);
    }

    public IColumnsDb<T> CreateColumnsDb<T>(DbSettings dbSettings) where T : struct, Enum
    {
        IRocksDbConfig tableConfig = _rocksDbConfigFactory.GetForDatabase(dbSettings.DbName, null);
        (T Key, string Name, IRocksDbConfig Config)[] columns = FastEnum.GetValues<T>()
            .Select(key => (key, key.ToString(), _rocksDbConfigFactory.GetForDatabase(dbSettings.DbName, key.ToString())))
            .ToArray();

        using RocksDbInstance instance = Open(dbSettings, tableConfig, columns.Select(static column => (column.Name, column.Config)).ToArray());
        Dictionary<T, ColumnDb> columnDbs = [];
        try
        {
            foreach ((T key, string name, IRocksDbConfig config) in columns)
            {
                columnDbs[key] = new ColumnDb(instance, ToColumnFamilyName(name), config, _logManager);
            }
        }
        catch
        {
            // Each column built so far holds a lease, which would otherwise keep the database open.
            foreach (ColumnDb columnDb in columnDbs.Values)
            {
                columnDb.Dispose();
            }

            throw;
        }

        return new ColumnsDb<T>(instance, tableConfig, columnDbs);
    }

    public string GetFullDbPath(DbSettings dbSettings) => DbOnTheRocks.GetFullDbPath(dbSettings.DbPath, _basePath);

    // "default" is a special column name with rocksdb, which is what previously not specifying column goes to
    private static string ToColumnFamilyName(string columnName) => columnName == "Default" ? "default" : columnName;

    /// <summary>Opens the database, with a column family for each of <paramref name="columns"/> when given.</summary>
    /// <returns>The open database, holding one lease the caller must release.</returns>
    internal RocksDbInstance Open(DbSettings settings, IRocksDbConfig tableConfig, IReadOnlyList<(string Name, IRocksDbConfig Config)>? columns)
    {
        string name = settings.DbName;
        string fullPath = GetFullDbPath(settings);
        if (!Directory.Exists(fullPath))
        {
            Directory.CreateDirectory(fullPath);
        }
        else if (settings.DeleteOnStart)
        {
            RocksDbInstance.Delete(fullPath, settings, _logger);
        }

        try
        {
            if (_logger.IsDebug) _logger.Debug($"Building options for {name} DB");
            NativeAccounting accounting = new();
            DbOptions dbOptions = new();
            BuildOptions(name, tableConfig, dbOptions, settings.MergeOperator, accounting);

            ColumnFamilies? columnFamilies = null;
            if (columns is not null)
            {
                dbOptions.SetCreateMissingColumnFamilies();
                columnFamilies = [];
                foreach ((string columnName, IRocksDbConfig columnConfig) in columns)
                {
                    ColumnFamilyOptions options = new();
                    IMergeOperator? mergeOperator = settings.ColumnsMergeOperators?.GetValueOrDefault(columnName);
                    BuildOptions(name, columnConfig, options, mergeOperator, accounting);
                    columnFamilies.Add(ToColumnFamilyName(columnName), options);
                }
            }

            if (_logger.IsDebug) _logger.Debug($"Loading DB {name,-13} from {fullPath} with max memory footprint of {accounting.MaxSize / 1000 / 1000,5} MB");
            bool wasRepairedOnOpen = RepairIfCorrupted(settings, fullPath, dbOptions);
            RocksDb db = DoOpen(fullPath, dbOptions, columnFamilies);
            if (accounting.MemoryPressure > 0) GC.AddMemoryPressure(accounting.MemoryPressure);

            List<IDisposable> metricsUpdaters = [];
            RocksDbInstance instance = new(db, settings, fullPath, dbOptions, columnFamilies, metricsUpdaters, accounting.RowCaches,
                accounting.MemoryPressure, accounting.UsesSharedBlockCache, wasRepairedOnOpen, _fileSystem, FatalShutdown, _logger);
            try
            {
                if (_dbConfig.EnableMetricsUpdater)
                {
                    metricsUpdaters.Add(StartMetricsUpdater(name, dbOptions, db, null, accounting.UsesSharedBlockCache));

                    if (columnFamilies is not null)
                    {
                        foreach (ColumnFamilies.Descriptor columnFamily in columnFamilies)
                        {
                            if (columnFamily.Name == "default") continue;
                            if (db.TryGetColumnFamily(columnFamily.Name, out IColumnFamilyHandle? handle))
                            {
                                metricsUpdaters.Add(StartMetricsUpdater(name + "_" + columnFamily.Name, columnFamily.Options, db, handle, accounting.UsesSharedBlockCache));
                            }
                        }
                    }
                }

                if (tableConfig.EnableFileWarmer)
                {
                    WarmupFile(name, fullPath, db);
                }

                return instance;
            }
            catch
            {
                instance.Dispose();
                throw;
            }
        }
        catch (DllNotFoundException e) when (e.Message.Contains("libdl"))
        {
            throw;
        }
        catch (RocksDbException x) when (x.Message.Contains("LOCK"))
        {
            if (_logger.IsWarn) _logger.Warn("If your database did not close properly you need to call 'find -type f -name '*LOCK*' -delete' from the database folder");
            throw;
        }
        catch (RocksDbException x)
        {
            RocksDbInstance.HandleFatalDbError(x, name, fullPath, _fileSystem, FatalShutdown, _logger);
            throw;
        }
    }

    protected virtual RocksDb DoOpen(string path, DbOptions options, ColumnFamilies? families) =>
        families is null ? RocksDb.Open(options, path) : RocksDb.Open(options, path, families);

    /// <summary>Terminates the process after a fatal DB error. Overridden in tests to observe the call without exiting.</summary>
    protected virtual void FatalShutdown() => Environment.Exit(ExitCodes.DbCorruption);

    protected virtual void RepairDb(DbOptions dbOptions, string path) => RocksDb.Repair(dbOptions, path);

    private DbMetricsUpdater<T> StartMetricsUpdater<T>(string name, Options<T> options, RocksDb db, IColumnFamilyHandle? handle, bool usesSharedBlockCache) where T : Options<T>
    {
        DbMetricsUpdater<T> metricUpdater = new(name, options, db, handle, _dbConfig, usesSharedBlockCache, _logger);
        metricUpdater.StartUpdating();
        return metricUpdater;
    }

    private bool RepairIfCorrupted(DbSettings settings, string fullPath, DbOptions dbOptions)
    {
        string corruptMarker = RocksDbInstance.CorruptMarkerPath(fullPath);
        string repairedMarker = RocksDbInstance.RepairedMarkerPath(fullPath);
        bool persistRepairMarker = settings.PersistRepairMarkerUntilAcknowledged;

        if (_fileSystem.File.Exists(corruptMarker))
        {
            if (_logger.IsWarn) _logger.Warn($"Corrupted DB marker detected for db {fullPath}. Attempting repair...");
            RepairDb(dbOptions, fullPath);

            if (persistRepairMarker)
            {
                _fileSystem.File.WriteAllText(repairedMarker, DateTime.UtcNow.ToString("O"));
                if (_logger.IsWarn) _logger.Warn("Repair completed. Some data may be lost. Wrote repaired.marker.");
            }
            else if (_logger.IsWarn)
            {
                _logger.Warn("Repair completed. Some data may be lost.");
            }

            _fileSystem.File.Delete(corruptMarker);
            return true;
        }

        return persistRepairMarker && _fileSystem.File.Exists(repairedMarker);
    }

    private void WarmupFile(string name, string basePath, RocksDb db)
    {
        long availableMemory = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        _logger.Info($"Warming up database {name} assuming {availableMemory} bytes of available memory");
        List<LiveFileMetadata>? liveFiles = db.GetLiveFilesMetadata();
        if (liveFiles is null)
        {
            if (_logger.IsWarn) _logger.Warn($"Unable to read live files metadata of database {name}. Skipping warmup.");
            return;
        }

        List<(FileMetadata metadata, DateTime creationTime)> fileMetadataEntries = [];

        foreach (LiveFileMetadata liveFileMetadata in liveFiles)
        {
            string fullPath = Path.Join(basePath, liveFileMetadata.FileMetadata.FileName);
            try
            {
                DateTime creationTime = File.GetCreationTimeUtc(fullPath);
                fileMetadataEntries.Add((liveFileMetadata.FileMetadata, creationTime));
            }
            catch (IOException)
            {
                // Maybe the file is gone or something. We ignore it.
            }
        }

        CollectionsMarshal.AsSpan(fileMetadataEntries).Sort(default(FileMetadataByLevelThenNewestComparer));

        long totalSize = 0;
        fileMetadataEntries = fileMetadataEntries.TakeWhile(metadata =>
            {
                availableMemory -= (long)metadata.metadata.FileSize;
                bool take = availableMemory > 0;
                if (take)
                {
                    totalSize += (long)metadata.metadata.FileSize;
                }
                return take;
            })
            // We reverse them again so that the lower level goes last so that it is the freshest.
            // Not all the available memory is actually available, so we are probably over reading things.
            .Reverse()
            .ToList();

        long totalRead = 0;
        Parallel.ForEach(fileMetadataEntries, (task) =>
        {
            string fullPath = Path.Join(basePath, task.metadata.FileName);
            _logger.Info($"{(totalRead * 100 / (double)totalSize):00.00}% Warming up file {fullPath}");

            try
            {
                byte[] buffer = new byte[512.KiB];
                using FileStream stream = File.OpenRead(fullPath);
                int readCount = buffer.Length;
                while (readCount == buffer.Length)
                {
                    readCount = stream.Read(buffer);
                    Interlocked.Add(ref totalRead, readCount);
                }
            }
            catch (FileNotFoundException)
            {
                // Happens sometimes. We do nothing here.
            }
            catch (IOException e)
            {
                // Something unusual, but nothing noteworthy.
                _logger.Warn($"Exception warming up {fullPath} {e}");
            }
        });
    }

    private readonly struct FileMetadataByLevelThenNewestComparer : IComparer<(FileMetadata metadata, DateTime creationTime)>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Compare(
            (FileMetadata metadata, DateTime creationTime) item1,
            (FileMetadata metadata, DateTime creationTime) item2)
        {
            int levelDiff = item1.metadata.FileLevel - item2.metadata.FileLevel;
            return levelDiff != 0 ? levelDiff : item2.creationTime.CompareTo(item1.creationTime);
        }
    }

    private const string OptimizeFiltersForHitsOption = "optimize_filters_for_hits=";

    /// <summary>
    /// Normalizes a RocksDB options string by removing earlier occurrences of optimize_filters_for_hits,
    /// keeping only the last one. This is needed because RocksDB does not allow overriding this option
    /// when specified multiple times in the same options string.
    /// </summary>
    public static string NormalizeRocksDbOptions(string dbOptions)
    {
        if (string.IsNullOrEmpty(dbOptions)) return dbOptions ?? string.Empty;

        int lastIndex = dbOptions.LastIndexOf(OptimizeFiltersForHitsOption, StringComparison.Ordinal);
        if (lastIndex == -1) return dbOptions;

        // Remove all earlier occurrences, keep only the last one
        int searchStart = 0;
        while (true)
        {
            int index = dbOptions.IndexOf(OptimizeFiltersForHitsOption, searchStart, StringComparison.Ordinal);
            if (index == -1 || index == lastIndex) break;

            // Find the end of this option (next semicolon)
            int endIndex = dbOptions.IndexOf(';', index);
            if (endIndex == -1) break;

            dbOptions = dbOptions.Remove(index, endIndex - index + 1);
            lastIndex = dbOptions.LastIndexOf(OptimizeFiltersForHitsOption, StringComparison.Ordinal);
        }

        return dbOptions;
    }

    /// <summary>The native allocations and memory budget of a database being opened, summed across its column families.</summary>
    private sealed class NativeAccounting
    {
        public long MaxSize;
        public long MemoryPressure;
        public bool UsesSharedBlockCache;
        public readonly List<Cache> RowCaches = [];
    }

    private void BuildOptions<T>(string name, IRocksDbConfig dbConfig, Options<T> options, IMergeOperator? mergeOperator, NativeAccounting accounting) where T : Options<T>
    {
        // This section is about the table factory and block cache, apparently.
        // This affects the format of the SST files and usually requires resyncing to take effect.
        // Note: Keep in mind, the term 'index' here usually means mapping to a block, not to a value.
        #region TableFactory sections

        string allOptions = dbConfig.RocksDbOptions + dbConfig.AdditionalRocksDbOptions;
        IDictionary<string, string> optionsAsDict = DbOnTheRocks.ExtractOptions(allOptions);
        ulong writeBufferSize = ulong.Parse(optionsAsDict["write_buffer_size"]);
        int maxWriteBufferNumber = int.Parse(optionsAsDict["max_write_buffer_number"]);

        ulong blockCacheSize = 0;
        if (optionsAsDict.TryGetValue("block_based_table_factory.block_cache", out string? blockCacheSizeStr))
        {
            blockCacheSize = ulong.Parse(blockCacheSizeStr);
        }

        BlockBasedTableOptions? tableOptions = new();
        ulong perCallCachePressure = 0;
        if (dbConfig.BlockCache is not null)
        {
            tableOptions.SetBlockCache(dbConfig.BlockCache.Value);
        }
        else if (blockCacheSize == 0)
        {
            tableOptions.SetBlockCache(_sharedCache.Handle);
            accounting.UsesSharedBlockCache = true;
        }
        else
        {
            perCallCachePressure = blockCacheSize;
        }

        // Note: the ordering is important.
        // changes to the table options must be applied before setting to set.
        options.SetBlockBasedTableFactory(tableOptions);

        options.ApplyFromString(NormalizeRocksDbOptions(dbConfig.RocksDbOptions));

        if (dbConfig.WriteBufferSize > 0)
        {
            writeBufferSize = dbConfig.WriteBufferSize.Value;
            options.SetWriteBufferSize(dbConfig.WriteBufferSize.Value);
        }

        if (dbConfig.WriteBufferNumber is not null)
        {
            maxWriteBufferNumber = (int)dbConfig.WriteBufferNumber.Value;
            options.SetMaxWriteBufferNumber(maxWriteBufferNumber);
        }
        if (maxWriteBufferNumber < 1) throw new InvalidConfigurationException($"Error initializing {name} db. Max write buffer number must be more than 1. max write buffer number: {maxWriteBufferNumber}", ExitCodes.GeneralError);

        #endregion

        #region WriteBuffer

        accounting.MaxSize += (long)writeBufferSize * maxWriteBufferNumber;
        long totalSize = Interlocked.Add(ref _maxRocksSize, accounting.MaxSize);
        if (_logger.IsDebug) _logger.Debug($"Expected max memory footprint of {name} DB is {accounting.MaxSize / 1000 / 1000} MB ({maxWriteBufferNumber} * {writeBufferSize / 1000 / 1000} MB + {blockCacheSize / 1000 / 1000} MB)");
        if (_logger.IsDebug) _logger.Debug($"Total max DB footprint so far is {totalSize / 1000 / 1000} MB");

        accounting.MemoryPressure += (long)writeBufferSize + (long)perCallCachePressure;

        #endregion

        // This section affects compactions, flushes and the LSM shape.
        #region Compaction
        /*
         * Multi-Threaded Compactions are needed to remove multiple copies of the same key that may occur if an application overwrites an existing key. Compactions also process deletions of keys. Compactions may occur in multiple threads if configured appropriately.
         * The entire database is stored in a set of sstfiles. When a memtable is full, its content is written out to a file in Level-0 (L0). RocksDB removes duplicate and overwritten keys in the memtable when it is flushed to a file in L0. Some files are periodically read in and merged to form larger files - this is called compaction.
         * The overall writing throughput of an LSM database directly depends on the speed at which compactions can occur, especially when the data is stored in fast storage like SSD or RAM. RocksDB may be configured to issue concurrent compaction requests from multiple threads. It is observed that sustained write rates may increase by as much as a factor of 10 with multi-threaded compaction when the database is on SSDs, as compared to single-threaded compactions.
         * TKS: Observed 500MB/s compared to ~100MB/s between multithreaded and single thread compactions on my machine (processor count is returning 12 for 6 cores with hyperthreading)
         * TKS: CPU goes to insane 30% usage on idle - compacting only app
         */
        options.SetMaxBackgroundCompactions(Environment.ProcessorCount);
        options.SetMaxBackgroundFlushes(Environment.ProcessorCount);

        // This one set the threadpool env, so its actually different from the above two
        options.IncreaseParallelism(Environment.ProcessorCount);

        // VERY important to reduce stalls. Allow L0->L1 compaction to happen with multiple thread.
        options.SetMaxSubcompactions((uint)Environment.ProcessorCount);

        if (dbConfig.CompactOnDeletions)
        {
            options.AddCompactOnDeletionCollectorFactory(
                (nuint)CompactOnDeletionSlidingWindowKeys,
                (nuint)CompactOnDeletionTriggerKeys,
                CompactOnDeletionFileRatio);
        }

        #endregion

        #region Other options

        if (dbConfig.RowCacheSize > 0)
        {
            // Row cache is basically a per-key cache. Nothing special about it. This is different from a block cache
            // that caches the whole block at once, so read still needs to traverse the block index, so this could be
            // more CPU efficient.
            // Note: Memtable also acts like a per-key cache that does not get updated on read. So in some case
            // maybe it makes more sense to put more memory to memtable.
            Cache rowCache = Cache.CreateLru(dbConfig.RowCacheSize.Value);
            accounting.RowCaches.Add(rowCache);
            options.SetRowCache(rowCache);
        }

        options.SetCreateIfMissing();

        if (dbConfig.MaxOpenFiles.HasValue)
        {
            options.SetMaxOpenFiles(dbConfig.MaxOpenFiles.Value);
        }

        if (dbConfig.EnableDbStatistics)
        {
            options.EnableStatistics();
        }
        options.SetStatsDumpPeriodSec(dbConfig.StatsDumpPeriodSec);

        if (dbConfig.AdditionalRocksDbOptions is not null)
        {
            options.ApplyFromString(NormalizeRocksDbOptions(dbConfig.AdditionalRocksDbOptions));
        }

        if (mergeOperator is not null)
        {
            options.SetMergeOperator(new MergeOperatorAdapter(mergeOperator));
        }

        #endregion
    }

    private const ulong CompactOnDeletionSlidingWindowKeys = 100_000;
    private const ulong CompactOnDeletionTriggerKeys = 50_000;
    private const double CompactOnDeletionFileRatio = 0.3;
}
