// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.Reflection;
using System.Threading;
using ConcurrentCollections;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Extensions;
using Nethermind.Db.Rocks.Config;
using Nethermind.Db.Rocks.Statistics;
using Nethermind.Logging;
using RocksDbSharp;
using IWriteBatch = Nethermind.Core.IWriteBatch;

namespace Nethermind.Db.Rocks;

public class DbOnTheRocks : IDb, ITunableDb
{
    protected ILogger _logger;

    private string? _fullPath;

    private static readonly ConcurrentDictionary<string, RocksDb> _dbsByPath = new();

    private bool _isDisposing;
    private bool _isDisposed;

    private readonly ConcurrentHashSet<IWriteBatch> _currentBatches = new();

    internal readonly RocksDb _db;

    private IntPtr? _rateLimiter;
    internal WriteOptions? WriteOptions { get; private set; }
    private WriteOptions? _noWalWrite;
    private WriteOptions? _lowPriorityAndNoWalWrite;
    private WriteOptions? _lowPriorityWriteOptions;

    private ReadOptions _defaultReadOptions = null!;
    private ReadOptions _hintCacheMissOptions = null!;
    private ReadOptions? _readAheadReadOptions = null;

    internal DbOptions? DbOptions { get; private set; }

    public string Name { get; }

    private static long _maxRocksSize;

    private long _maxThisDbSize;

    protected IntPtr? _cache = null;
    protected IntPtr? _rowCache = null;

    private readonly DbSettings _settings;

    protected readonly PerTableDbConfig _perTableDbConfig;

    private readonly IFileSystem _fileSystem;

    protected readonly RocksDbSharp.Native _rocksDbNative;

    private ITunableDb.TuneType _currentTune = ITunableDb.TuneType.Default;

    private string CorruptMarkerPath => Path.Join(_fullPath, "corrupt.marker");

    private readonly List<IDisposable> _metricsUpdaters = new();

    private readonly ManagedIterators _readaheadIterators = new();

    internal long _allocatedSpan = 0;
    private long _totalReads;
    private long _totalWrites;

    public DbOnTheRocks(
        string basePath,
        DbSettings dbSettings,
        IDbConfig dbConfig,
        ILogManager logManager,
        IList<string>? columnFamilies = null,
        RocksDbSharp.Native? rocksDbNative = null,
        IFileSystem? fileSystem = null,
        IntPtr? sharedCache = null)
    {
        _logger = logManager.GetClassLogger();
        _settings = dbSettings;
        Name = _settings.DbName;
        _fileSystem = fileSystem ?? new FileSystem();
        _rocksDbNative = rocksDbNative ?? RocksDbSharp.Native.Instance;
        _perTableDbConfig = new PerTableDbConfig(dbConfig, _settings);
        _db = Init(basePath, dbSettings.DbPath, dbConfig, logManager, columnFamilies, dbSettings.DeleteOnStart, sharedCache);

        if (_perTableDbConfig.AdditionalRocksDbOptions is not null)
        {
            ApplyOptions(_perTableDbConfig.AdditionalRocksDbOptions);
        }
    }

    protected virtual RocksDb DoOpen(string path, (DbOptions Options, ColumnFamilies? Families) db)
    {
        (DbOptions options, ColumnFamilies? families) = db;
        return families is null ? RocksDb.Open(options, path) : RocksDb.Open(options, path, families);
    }

    private RocksDb Open(string path, (DbOptions Options, ColumnFamilies? Families) db)
    {
        RepairIfCorrupted(db.Options);

        return DoOpen(path, db);
    }

    private RocksDb Init(string basePath, string dbPath, IDbConfig dbConfig, ILogManager? logManager,
        IList<string>? columnNames = null, bool deleteOnStart = false, IntPtr? sharedCache = null)
    {
        _fullPath = GetFullDbPath(dbPath, basePath);
        _logger = logManager?.GetClassLogger() ?? default;
        if (!Directory.Exists(_fullPath))
        {
            Directory.CreateDirectory(_fullPath);
        }
        else if (deleteOnStart)
        {
            Delete();
        }

        try
        {
            // ReSharper disable once VirtualMemberCallInConstructor
            if (_logger.IsDebug) _logger.Debug($"Building options for {Name} DB");
            DbOptions = new DbOptions();
            BuildOptions(_perTableDbConfig, DbOptions, sharedCache);

            ColumnFamilies? columnFamilies = null;
            if (columnNames is not null)
            {
                columnFamilies = new ColumnFamilies();
                foreach (string enumColumnName in columnNames)
                {
                    string columnFamily = enumColumnName;

                    // "default" is a special column name with rocksdb, which is what previously not specifying column goes to
                    if (columnFamily == "Default") columnFamily = "default";

                    ColumnFamilyOptions options = new();
                    IntPtr? cacheForColumn = _cache ?? sharedCache;
                    BuildOptions(new PerTableDbConfig(dbConfig, _settings, columnFamily), options, cacheForColumn);
                    columnFamilies.Add(columnFamily, options);
                }
            }

            // ReSharper disable once VirtualMemberCallInConstructor
            if (_logger.IsDebug) _logger.Debug($"Loading DB {Name,-13} from {_fullPath} with max memory footprint of {_maxThisDbSize / 1000 / 1000,5} MB");
            RocksDb db = _dbsByPath.GetOrAdd(_fullPath, (s, tuple) => Open(s, tuple), (DbOptions, columnFamilies));

            if (dbConfig.EnableMetricsUpdater)
            {
                DbMetricsUpdater<DbOptions> metricUpdater = new DbMetricsUpdater<DbOptions>(Name, DbOptions, db, null, dbConfig, _logger);
                metricUpdater.StartUpdating();
                _metricsUpdaters.Add(metricUpdater);

                if (columnFamilies is not null)
                {
                    foreach (ColumnFamilies.Descriptor columnFamily in columnFamilies)
                    {
                        if (columnFamily.Name == "default") continue;
                        if (db.TryGetColumnFamily(columnFamily.Name, out ColumnFamilyHandle handle))
                        {
                            DbMetricsUpdater<ColumnFamilyOptions> columnMetricUpdater = new DbMetricsUpdater<ColumnFamilyOptions>(
                                Name + "_" + columnFamily.Name, columnFamily.Options, db, handle, dbConfig, _logger);
                            columnMetricUpdater.StartUpdating();
                            _metricsUpdaters.Add(columnMetricUpdater);
                        }
                    }
                }
            }

            return db;
        }
        catch (DllNotFoundException e) when (e.Message.Contains("libdl"))
        {
            throw new ApplicationException(
                $"Unable to load 'libdl' necessary to init the RocksDB database. Please run{Environment.NewLine}" +
                $"sudo apt-get update && sudo apt-get install libsnappy-dev libc6-dev libc6 unzip{Environment.NewLine}" +
                "or similar depending on your distribution.");
        }
        catch (RocksDbException x) when (x.Message.Contains("LOCK"))
        {
            if (_logger.IsWarn) _logger.Warn("If your database did not close properly you need to call 'find -type f -name '*LOCK*' -delete' from the database folder");
            throw;
        }
        catch (RocksDbSharpException x)
        {
            CreateMarkerIfCorrupt(x);
            throw;
        }

    }

    private void CreateMarkerIfCorrupt(RocksDbSharpException rocksDbException)
    {
        if (rocksDbException.Message.Contains("Corruption:"))
        {
            if (_logger.IsWarn) _logger.Warn($"Corrupted DB detected on path {_fullPath}. Please restart Nethermind to attempt repair.");
            _fileSystem.File.WriteAllText(CorruptMarkerPath, "marker");
        }
    }

    private void RepairIfCorrupted(DbOptions dbOptions)
    {
        string corruptMarker = CorruptMarkerPath;

        if (!_fileSystem.File.Exists(corruptMarker))
        {
            return;
        }

        if (_logger.IsWarn) _logger.Warn($"Corrupted DB marker detected for db {_fullPath}. Attempting repair...");
        _rocksDbNative.rocksdb_repair_db(dbOptions.Handle, _fullPath);

        if (_logger.IsWarn) _logger.Warn($"Repair completed. Some data may be lost. Consider a full resync.");
        _fileSystem.File.Delete(corruptMarker);
    }

    protected internal void UpdateReadMetrics()
    {
        Interlocked.Increment(ref _totalReads);
    }

    protected internal void UpdateWriteMetrics()
    {
        Interlocked.Increment(ref _totalWrites);
    }

    protected virtual long FetchTotalPropertyValue(string propertyName)
    {
        long value = long.TryParse(_db.GetProperty(propertyName), out long parsedValue)
            ? parsedValue
            : 0;

        return value;
    }

    public IDbMeta.DbMetric GatherMetric(bool includeSharedCache = false)
    {
        return new IDbMeta.DbMetric()
        {
            Size = GetSize(),
            CacheSize = GetCacheSize(includeSharedCache),
            IndexSize = GetIndexSize(),
            MemtableSize = GetMemtableSize(),
            TotalReads = _totalReads,
            TotalWrites = _totalWrites,
        };
    }

    private long GetSize()
    {
        try
        {
            long sstSize = FetchTotalPropertyValue("rocksdb.total-sst-files-size");
            long blobSize = FetchTotalPropertyValue("rocksdb.total-blob-file-size");
            return sstSize + blobSize;
        }
        catch (RocksDbSharpException e)
        {
            if (_logger.IsWarn)
                _logger.Warn($"Failed to update DB size metrics {e.Message}");
        }

        return 0;
    }

    private long GetCacheSize(bool includeSharedCache = false)
    {
        try
        {
            if (_cache is null && !includeSharedCache)
            {
                // returning 0 as we are using shared cache.
                return 0;
            }
            return FetchTotalPropertyValue("rocksdb.block-cache-usage");
        }
        catch (RocksDbSharpException e)
        {
            if (_logger.IsWarn)
                _logger.Warn($"Failed to update DB size metrics {e.Message}");
        }

        return 0;
    }

    private long GetIndexSize()
    {
        try
        {
            return FetchTotalPropertyValue("rocksdb.estimate-table-readers-mem");
        }
        catch (RocksDbSharpException e)
        {
            if (_logger.IsWarn)
                _logger.Warn($"Failed to update DB size metrics {e.Message}");
        }

        return 0;
    }

    private long GetMemtableSize()
    {
        try
        {
            return FetchTotalPropertyValue("rocksdb.cur-size-all-mem-tables");
        }
        catch (RocksDbSharpException e)
        {
            if (_logger.IsWarn)
                _logger.Warn($"Failed to update DB size metrics {e.Message}");
        }

        return 0;
    }

    protected virtual void BuildOptions<T>(PerTableDbConfig dbConfig, Options<T> options, IntPtr? sharedCache) where T : Options<T>
    {
        // This section is about the table factory.. and block cache apparently.
        // This effect the format of the SST files and usually require resync to take effect.
        // Note: Keep in mind, the term 'index' here usually means mapping to a block, not to a value.
        #region TableFactory sections

        // TODO: Try PlainTable and Cuckoo table.
        BlockBasedTableOptions tableOptions = new();

        // Note, this is before compression. On disk size may be lower. The on disk size is the minimum amount of read
        // each io will do. On most SSD, the minimum read size is 4096 byte. So don't set it to lower than that, unless
        // you have an optane drive or some kind of RAM disk. Lower block size also means bigger index size.
        tableOptions.SetBlockSize((ulong)(dbConfig.BlockSize ?? 16 * 1024));

        // No significant downside. Just set it.
        tableOptions.SetPinL0FilterAndIndexBlocksInCache(true);

        // If true, the index does not reside on dedicated memory space, but uses the block cache as memory space and
        // it can be released. This sounds great, except that without two level index, the index size is fairly large,
        // about 0.5MB each, so if the index is not in the cache, the whole thing need to re-read. This cause very spiky
        // block processing time. With two level index, this setting only apply to the top level index, which is only
        // a couple MB on StateDb. So we keep it as false to not introduce regression on user who synced before 1.19.
        // On mainnet, the total index size for statedb is about 3GB if I'm not mistaken. Its linearly proportional to
        // database size and inversely proportional to blocksize.
        tableOptions.SetCacheIndexAndFilterBlocks(dbConfig.CacheIndexAndFilterBlocks);

        // Make the index in cache have higher priority, so it is kept more in cache.
        _rocksDbNative.rocksdb_block_based_options_set_cache_index_and_filter_blocks_with_high_priority(tableOptions.Handle, true);

        if (dbConfig.UseTwoLevelIndex)
        {
            // Two level index split the index into two level. First index point to second level index, which actually
            // point to the block, which get bsearched to the value. This means potentially two iop instead of one per
            // read, and probably more processing overhead. But it significantly reduces memory usage and make block
            // processing time more consistent. So its enabled by default. That said, if you got the RAM, maybe disable
            // this.
            // See https://rocksdb.org/blog/2017/05/12/partitioned-index-filter.html
            tableOptions.SetIndexType(BlockBasedTableIndexType.TwoLevelIndex);
            _rocksDbNative.rocksdb_block_based_options_set_partition_filters(tableOptions.Handle, true);
            _rocksDbNative.rocksdb_block_based_options_set_metadata_block_size(tableOptions.Handle, 4096);
        }
        else if (dbConfig.UseHashIndex)
        {
            // Hash index need prefix extractor.
            // I'm not sure if this index goes directly to value or not.
            // Also means it can't do range scan.
            // I'm guessing, if the prefix is the whole key, its useless.
            tableOptions.SetIndexType(BlockBasedTableIndexType.Hash);
        }

        tableOptions.SetFormatVersion(5);
        tableOptions.SetFilterPolicy(BloomFilterPolicy.Create(10, false));

        // Default value is 16.
        // So each block consist of several "restart" and each "restart" is BlockRestartInterval number of key.
        // They key within the same restart is delta-encoded with the key before it. This mean a read will have to go
        // through a minimum of "BlockRestartInterval" number of key, probably. That is my understanding.
        // Reducing this is likely going to improve CPU usage at the cost of increased uncompressed size, which effect
        // cache utilization.
        _rocksDbNative.rocksdb_block_based_options_set_block_restart_interval(tableOptions.Handle, dbConfig.BlockRestartInterval);

        // This adds a hashtable-like index per block (the 16kb block)
        // In theory, this should reduce CPU, but I don't see any different.
        // It seems to increase disk space use by about 1 GB, which again, could be just noise. I'll just keep this.
        // That said, on lower block size, it'll probably be useless.
        // Note, the index points to a restart interval (see above), not to the value itself.
        _rocksDbNative.rocksdb_block_based_options_set_data_block_index_type(tableOptions.Handle, 1);
        _rocksDbNative.rocksdb_block_based_options_set_data_block_hash_ratio(tableOptions.Handle, 0.75);

        ulong blockCacheSize = dbConfig.BlockCacheSize;
        if (sharedCache is not null && blockCacheSize == 0)
        {
            tableOptions.SetBlockCache(sharedCache.Value);
        }
        else
        {
            _cache = _rocksDbNative.rocksdb_cache_create_lru(new UIntPtr(blockCacheSize));
            tableOptions.SetBlockCache(_cache.Value);
        }

        options.SetBlockBasedTableFactory(tableOptions);

        // When true, (for some reason the binding is int, but 1 is true), bloom filters for last level is not created.
        // This reduces disk space utilization, but read of non-existent key will have to go through the database
        // instead of checking a bloom filter.
        options.SetOptimizeFiltersForHits(dbConfig.OptimizeFiltersForHits ? 1 : 0);

        if (dbConfig.DisableCompression == true)
        {
            options.SetCompression(Compression.No);
        }
        else if (dbConfig.OnlyCompressLastLevel)
        {
            // So the bottommost level is about 80-90% of the database. So it may make sense to only compress that
            // part, which make the top level faster, and/or mmap-able.
            options.SetCompression(Compression.No);
            _rocksDbNative.rocksdb_options_set_bottommost_compression(options.Handle, 0x1); // 0x1 is snappy.
        }

        // Target size of each SST file. Increase to reduce number of file. Default is 64MB.
        options.SetTargetFileSizeBase(dbConfig.TargetFileSizeBase);

        // Multiply the target size of SST file by this much every level down, further reduce number of file.
        // Does not have much downside on hash based DB, but might disable some move optimization on db with
        // blocknumber key, or halfpath/flatdb layout.
        options.SetTargetFileSizeMultiplier(dbConfig.TargetFileSizeMultiplier);

        #endregion

        // This section affect the write buffer, or memtable. Note, the size of write buffer affect the size of l0
        // file which affect compactions. The options here does not effect how the sst files are read... probably.
        // But read does go through the write buffer first, before going through the rowcache (or is it before memtable?)
        // block cache and then finally the LSM/SST files.
        #region WriteBuffer
        _rocksDbNative.rocksdb_options_set_memtable_whole_key_filtering(options.Handle, true);
        _rocksDbNative.rocksdb_options_set_memtable_prefix_bloom_size_ratio(options.Handle, 0.02);

        // Note: Write buffer and write buffer num are modified by MemoryHintMan.
        ulong writeBufferSize = dbConfig.WriteBufferSize;
        options.SetWriteBufferSize(writeBufferSize);
        int writeBufferNumber = (int)dbConfig.WriteBufferNumber;
        if (writeBufferNumber < 1) throw new InvalidConfigurationException($"Error initializing {Name} db. Max write buffer number must be more than 1. max write buffer number: {writeBufferNumber}", ExitCodes.GeneralError);
        options.SetMaxWriteBufferNumber(writeBufferNumber);
        lock (_dbsByPath)
        {
            _maxThisDbSize += (long)writeBufferSize * writeBufferNumber;
            Interlocked.Add(ref _maxRocksSize, _maxThisDbSize);
            if (_logger.IsDebug)
                _logger.Debug(
                    $"Expected max memory footprint of {Name} DB is {_maxThisDbSize / 1000 / 1000} MB ({writeBufferNumber} * {writeBufferSize / 1000 / 1000} MB + {blockCacheSize / 1000 / 1000} MB)");
            if (_logger.IsDebug) _logger.Debug($"Total max DB footprint so far is {_maxRocksSize / 1000 / 1000} MB");
            ThisNodeInfo.AddInfo("Mem est DB   :", $"{_maxRocksSize / 1000 / 1000} MB".PadLeft(8));
        }

        if (dbConfig.UseHashSkipListMemtable)
        {
            // Use a hashtable of skiplist, instead of skiplist.
            // This has shown to be quite effective at reducing CPU usage at the expense of raw concurrent throughput
            // in the case of flatdb layout's storage db. Reduces sync throughput though. Could improve block processing
            // time. Need prefix extractor. Can't do range scan between prefixes. Well, there is a flag to force it,
            // but it'll cost some CPU.
            options.SetAllowConcurrentMemtableWrite(false);

            // Default value.
            UIntPtr bucketCount = 1000000; // Seems quite large. Wonder why this is the default value.
            int skiplistHeight = 4;
            int skiplistBranchingFactor = 4;
            _rocksDbNative.rocksdb_options_set_hash_skip_list_rep(options.Handle, bucketCount, skiplistHeight, skiplistBranchingFactor);
        }

        // This is basically useless on write only database. However, for halfpath with live pruning, flatdb, or
        // (maybe?) full sync where keys are deleted, replaced, or re-inserted, two memtable can merge together
        // resulting in a reduced total memtable size to be written. This does seems to reduce sync throughput though.
        options.SetMinWriteBufferNumberToMerge(dbConfig.MinWriteBufferNumberToMerge);

        if (dbConfig.MaxWriteBufferSizeToMaintain.HasValue)
        {
            // Allow maintaining some of the flushed write buffer. Why do you want to do this? Because write buffer
            // act like a write cache. Recently written key are likely to be read back. Plus, in state db, intermediate
            // nodes that is being read is likely going to get modified, so having those node in rowcache/blockcache
            // can be useless, might as well put memory here.
            // Note: each memtable need to be checked, so it may make sense to also increase the write buffer size.
            _rocksDbNative.rocksdb_options_set_max_write_buffer_size_to_maintain(options.Handle, dbConfig.MaxWriteBufferSizeToMaintain.Value);
        }

        #endregion

        // This section affect compactions, flushes and the LSM shape.
        #region Compaction
        /*
         * Multi-Threaded Compactions
         * Compactions are needed to remove multiple copies of the same key that may occur if an application overwrites an existing key. Compactions also process deletions of keys. Compactions may occur in multiple threads if configured appropriately.
         * The entire database is stored in a set of sstfiles. When a memtable is full, its content is written out to a file in Level-0 (L0). RocksDB removes duplicate and overwritten keys in the memtable when it is flushed to a file in L0. Some files are periodically read in and merged to form larger files - this is called compaction.
         * The overall write throughput of an LSM database directly depends on the speed at which compactions can occur, especially when the data is stored in fast storage like SSD or RAM. RocksDB may be configured to issue concurrent compaction requests from multiple threads. It is observed that sustained write rates may increase by as much as a factor of 10 with multi-threaded compaction when the database is on SSDs, as compared to single-threaded compactions.
         * TKS: Observed 500MB/s compared to ~100MB/s between multithreaded and single thread compactions on my machine (processor count is returning 12 for 6 cores with hyperthreading)
         * TKS: CPU goes to insane 30% usage on idle - compacting only app
         */
        options.SetMaxBackgroundCompactions(Environment.ProcessorCount);
        options.SetMaxBackgroundFlushes(Environment.ProcessorCount);

        // This one set the threadpool env, so its actually different from the above two
        options.IncreaseParallelism(Environment.ProcessorCount);

        options.SetLevelCompactionDynamicLevelBytes(false);

        // VERY important to reduce stalls. Allow L0->L1 compaction to happen with multiple thread.
        _rocksDbNative.rocksdb_options_set_max_subcompactions(options.Handle, (uint)Environment.ProcessorCount);

        // Main config for LSM shape, also effect write amplification.
        // MaxBytesForLevelBase is 256MB by default. But if write buffer is lowered, it could be preferable to reduce
        // this as well to match total write buffer to reduce write amplification, but it can increase number of level
        // which in turn, make write amplification higher anyway.
        options.SetMaxBytesForLevelBase(dbConfig.MaxBytesForLevelBase);
        // MaxBytesForLevelMultiplier is 10 by default. Lowering this will deepens the LSM, which may reduce write
        // amplification (unless the LSM is too deep), at the expense of read performance. But then, you have bloom
        // filter anyway, and recently written keys are likely to be read and they tend to be at the top of the LSM
        // tree which means they are more cacheable, so at that point you are trading CPU for cacheability.
        options.SetMaxBytesForLevelMultiplier(dbConfig.MaxBytesForLevelMultiplier);

        // For reducing temporarily used disk space but come at the cost of parallel compaction.
        if (dbConfig.MaxCompactionBytes.HasValue)
        {
            options.SetMaxCompactionBytes(dbConfig.MaxCompactionBytes.Value);
        }

        // Significantly reduces IOPs during syncing, but take up quite some memory.
        if (dbConfig.CompactionReadAhead is not null && dbConfig.CompactionReadAhead != 0)
        {
            options.SetCompactionReadaheadSize(dbConfig.CompactionReadAhead.Value);
        }
        #endregion

        #region Other options

        if (dbConfig.RowCacheSize > 0)
        {
            // Row cache is basically a per-key cache. Nothing special to it. This is different from block cache
            // which cache the whole block at once, so read still need to traverse the block index, so this could be
            // more CPU efficient.
            // Note: Memtable also act like a per-key cache, that does not get updated on read. So in some case
            // maybe it make more sense to put more memory to memtable.
            _rowCache = _rocksDbNative.rocksdb_cache_create_lru(new UIntPtr(dbConfig.RowCacheSize.Value));
            _rocksDbNative.rocksdb_options_set_row_cache(options.Handle, _rowCache.Value);
        }

        if (dbConfig.PrefixExtractorLength.HasValue)
        {
            options.SetPrefixExtractor(SliceTransform.CreateFixedPrefix(dbConfig.PrefixExtractorLength.Value));
        }

        options.SetCreateIfMissing();
        options.SetAdviseRandomOnOpen(true);
        if (dbConfig.MaxOpenFiles.HasValue)
        {
            options.SetMaxOpenFiles(dbConfig.MaxOpenFiles.Value);
        }
        options.SetRecycleLogFileNum(dbConfig
            .RecycleLogFileNum); // potential optimization for reusing allocated log files

        // Bypass OS cache. This may reduce response time, but if cache size is not increased, it has less effective
        // cache. Also, OS cache is compressed cache, so even if cache size is increased, the effective cache size
        // is still lower as block cache is uncompressed.
        options.SetUseDirectReads(dbConfig.UseDirectReads.GetValueOrDefault());
        options.SetUseDirectIoForFlushAndCompaction(dbConfig.UseDirectIoForFlushAndCompactions.GetValueOrDefault());

        if (dbConfig.AllowMmapReads)
        {
            // Only work if disable compression is false.
            // Note: if SkipVerifyChecksum is false, checksum is calculated on every reads.
            // Note: This bypass block cache.
            options.SetAllowMmapReads(true);
        }

        if (dbConfig.MaxBytesPerSec.HasValue)
        {
            _rateLimiter =
                _rocksDbNative.rocksdb_ratelimiter_create(dbConfig.MaxBytesPerSec.Value, 1000, 10);
            _rocksDbNative.rocksdb_options_set_ratelimiter(options.Handle, _rateLimiter.Value);
        }

        if (dbConfig.EnableDbStatistics)
        {
            options.EnableStatistics();
        }
        options.SetStatsDumpPeriodSec(dbConfig.StatsDumpPeriodSec);
        #endregion

        #region read-write options
        WriteOptions = CreateWriteOptions(dbConfig);

        _noWalWrite = CreateWriteOptions(dbConfig);
        _noWalWrite.DisableWal(1);

        _lowPriorityWriteOptions = CreateWriteOptions(dbConfig);
        _rocksDbNative.rocksdb_writeoptions_set_low_pri(_lowPriorityWriteOptions.Handle, true);

        _lowPriorityAndNoWalWrite = CreateWriteOptions(dbConfig);
        _lowPriorityAndNoWalWrite.DisableWal(1);
        _rocksDbNative.rocksdb_writeoptions_set_low_pri(_lowPriorityAndNoWalWrite.Handle, true);

        _defaultReadOptions = new ReadOptions();
        _defaultReadOptions.SetVerifyChecksums(dbConfig.VerifyChecksum);

        _hintCacheMissOptions = new ReadOptions();
        _hintCacheMissOptions.SetVerifyChecksums(dbConfig.VerifyChecksum);
        _hintCacheMissOptions.SetFillCache(false);

        // When readahead flag is on, the next keys are expected to be after the current key. Increasing this value,
        // will increase the chances that the next keys will be in the cache, which reduces iops and latency. This
        // increases throughput, however, if a lot of the keys are not close to the current key, it will increase read
        // bandwidth requirement, since each read must be at least this size. This value is tuned for a batched trie
        // visitor on mainnet with 4GB memory budget and 4Gbps read bandwidth.
        if (dbConfig.ReadAheadSize != 0)
        {
            _readAheadReadOptions = new ReadOptions();
            _readAheadReadOptions.SetVerifyChecksums(dbConfig.VerifyChecksum);
            _readAheadReadOptions.SetReadaheadSize(dbConfig.ReadAheadSize ?? (ulong)256.KiB());
            _readAheadReadOptions.SetTailing(true);
        }
        #endregion
    }

    private static WriteOptions CreateWriteOptions(PerTableDbConfig dbConfig)
    {
        WriteOptions options = new();
        // potential fix for corruption on hard process termination, may cause performance degradation
        options.SetSync(dbConfig.WriteAheadLogSync);
        return options;
    }

    public byte[]? this[ReadOnlySpan<byte> key]
    {
        get => Get(key, ReadFlags.None);
        set => Set(key, value, WriteFlags.None);
    }

    public byte[]? Get(ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None)
    {
        return GetWithColumnFamily(key, null, _readaheadIterators, flags);
    }

    internal byte[]? GetWithColumnFamily(ReadOnlySpan<byte> key, ColumnFamilyHandle? cf, ManagedIterators readaheadIterators, ReadFlags flags = ReadFlags.None)
    {
        ObjectDisposedException.ThrowIf(_isDisposing, this);

        UpdateReadMetrics();

        try
        {
            if (_readAheadReadOptions is not null && (flags & ReadFlags.HintReadAhead) != 0)
            {
                if (!readaheadIterators.IsValueCreated)
                {
                    readaheadIterators.Value = _db.NewIterator(cf, _readAheadReadOptions);
                }

                Iterator iterator = readaheadIterators.Value!;
                iterator.Seek(key);
                if (iterator.Valid() && Bytes.AreEqual(iterator.GetKeySpan(), key))
                {
                    return iterator.Value();
                }
            }

            return _db.Get(key, cf, (flags & ReadFlags.HintCacheMiss) != 0 ? _hintCacheMissOptions : _defaultReadOptions);
        }
        catch (RocksDbSharpException e)
        {
            CreateMarkerIfCorrupt(e);
            throw;
        }
    }

    public void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None)
    {
        SetWithColumnFamily(key, null, value, flags);
    }

    internal void SetWithColumnFamily(ReadOnlySpan<byte> key, ColumnFamilyHandle? cf, ReadOnlySpan<byte> value, WriteFlags flags = WriteFlags.None)
    {
        ObjectDisposedException.ThrowIf(_isDisposing, this);

        UpdateWriteMetrics();

        try
        {
            if (value.IsNull())
            {
                _db.Remove(key, cf, WriteFlagsToWriteOptions(flags));
            }
            else
            {
                _db.Put(key, value, cf, WriteFlagsToWriteOptions(flags));
            }
        }
        catch (RocksDbSharpException e)
        {
            CreateMarkerIfCorrupt(e);
            throw;
        }
    }

    public WriteOptions? WriteFlagsToWriteOptions(WriteFlags flags)
    {
        if ((flags & WriteFlags.LowPriorityAndNoWAL) == WriteFlags.LowPriorityAndNoWAL)
        {
            return _lowPriorityAndNoWalWrite;
        }

        if ((flags & WriteFlags.DisableWAL) == WriteFlags.DisableWAL)
        {
            return _noWalWrite;
        }

        if ((flags & WriteFlags.LowPriority) == WriteFlags.LowPriority)
        {
            return _lowPriorityWriteOptions;
        }

        return WriteOptions;
    }


    public KeyValuePair<byte[], byte[]?>[] this[byte[][] keys]
    {
        get
        {
            try
            {
                return _db.MultiGet(keys);
            }
            catch (RocksDbSharpException e)
            {
                CreateMarkerIfCorrupt(e);
                throw;
            }
        }
    }

    public Span<byte> GetSpan(ReadOnlySpan<byte> key, ReadFlags flags)
    {
        return GetSpanWithColumnFamily(key, null, flags);
    }

    internal Span<byte> GetSpanWithColumnFamily(ReadOnlySpan<byte> key, ColumnFamilyHandle? cf, ReadFlags flags)
    {
        ObjectDisposedException.ThrowIf(_isDisposing, this);

        UpdateReadMetrics();

        try
        {
            Span<byte> span = _db.GetSpan(key, cf, (flags & ReadFlags.HintCacheMiss) != 0 ? _hintCacheMissOptions : _defaultReadOptions);

            if (!span.IsNullOrEmpty())
            {
                Interlocked.Increment(ref _allocatedSpan);
                GC.AddMemoryPressure(span.Length);
            }
            return span;
        }
        catch (RocksDbSharpException e)
        {
            CreateMarkerIfCorrupt(e);
            throw;
        }
    }

    public void PutSpan(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, WriteFlags writeFlags)
    {
        SetWithColumnFamily(key, null, value, writeFlags);
    }

    public void DangerousReleaseMemory(in ReadOnlySpan<byte> span)
    {
        if (!span.IsNullOrEmpty())
        {
            Interlocked.Decrement(ref _allocatedSpan);
            GC.RemoveMemoryPressure(span.Length);
        }
        _db.DangerousReleaseMemory(span);
    }

    public void Remove(ReadOnlySpan<byte> key)
    {
        ObjectDisposedException.ThrowIf(_isDisposing, this);

        try
        {
            _db.Remove(key, null, WriteOptions);
        }
        catch (RocksDbSharpException e)
        {
            CreateMarkerIfCorrupt(e);
            throw;
        }
    }

    public IEnumerable<KeyValuePair<byte[], byte[]?>> GetAll(bool ordered = false)
    {
        ObjectDisposedException.ThrowIf(_isDisposing, this);

        Iterator iterator = CreateIterator(ordered);
        return GetAllCore(iterator);
    }

    protected internal Iterator CreateIterator(bool ordered = false, ColumnFamilyHandle? ch = null)
    {
        ReadOptions readOptions = new();
        readOptions.SetTailing(!ordered);

        try
        {
            return _db.NewIterator(ch, readOptions);
        }
        catch (RocksDbSharpException e)
        {
            CreateMarkerIfCorrupt(e);
            throw;
        }
    }

    public IEnumerable<byte[]> GetAllKeys(bool ordered = false)
    {
        if (_isDisposing)
        {
            throw new ObjectDisposedException($"Attempted to read form a disposed database {Name}");
        }

        Iterator iterator = CreateIterator(ordered);
        return GetAllKeysCore(iterator);
    }

    public IEnumerable<byte[]> GetAllValues(bool ordered = false)
    {
        ObjectDisposedException.ThrowIf(_isDisposing, this);

        Iterator iterator = CreateIterator(ordered);
        return GetAllValuesCore(iterator);
    }

    internal IEnumerable<byte[]> GetAllValuesCore(Iterator iterator)
    {
        try
        {
            try
            {
                iterator.SeekToFirst();
            }
            catch (RocksDbSharpException e)
            {
                CreateMarkerIfCorrupt(e);
                throw;
            }

            while (iterator.Valid())
            {
                yield return iterator.Value();
                try
                {
                    iterator.Next();
                }
                catch (RocksDbSharpException e)
                {
                    CreateMarkerIfCorrupt(e);
                    throw;
                }
            }
        }
        finally
        {
            try
            {
                iterator.Dispose();
            }
            catch (RocksDbSharpException e)
            {
                CreateMarkerIfCorrupt(e);
                throw;
            }
        }
    }

    internal IEnumerable<byte[]> GetAllKeysCore(Iterator iterator)
    {
        try
        {
            try
            {
                iterator.SeekToFirst();
            }
            catch (RocksDbSharpException e)
            {
                CreateMarkerIfCorrupt(e);
                throw;
            }

            while (iterator.Valid())
            {
                yield return iterator.Key();
                try
                {
                    iterator.Next();
                }
                catch (RocksDbSharpException e)
                {
                    CreateMarkerIfCorrupt(e);
                    throw;
                }
            }
        }
        finally
        {
            try
            {
                iterator.Dispose();
            }
            catch (RocksDbSharpException e)
            {
                CreateMarkerIfCorrupt(e);
                throw;
            }
        }
    }

    public IEnumerable<KeyValuePair<byte[], byte[]?>> GetAllCore(Iterator iterator)
    {
        try
        {
            ObjectDisposedException.ThrowIf(_isDisposing, this);

            try
            {
                iterator.SeekToFirst();
            }
            catch (RocksDbSharpException e)
            {
                CreateMarkerIfCorrupt(e);
                throw;
            }

            while (iterator.Valid())
            {
                yield return new KeyValuePair<byte[], byte[]?>(iterator.Key(), iterator.Value());

                try
                {
                    iterator.Next();
                }
                catch (RocksDbSharpException e)
                {
                    CreateMarkerIfCorrupt(e);
                    throw;
                }
            }
        }
        finally
        {
            try
            {
                iterator.Dispose();
            }
            catch (RocksDbSharpException e)
            {
                CreateMarkerIfCorrupt(e);
                throw;
            }
        }
    }

    public bool KeyExists(ReadOnlySpan<byte> key)
    {
        return KeyExistsWithColumn(key, null);
    }

    protected internal bool KeyExistsWithColumn(ReadOnlySpan<byte> key, ColumnFamilyHandle? cf)
    {
        ObjectDisposedException.ThrowIf(_isDisposing, this);

        try
        {
            return _db.HasKey(key, cf, _defaultReadOptions);
        }
        catch (RocksDbSharpException e)
        {
            CreateMarkerIfCorrupt(e);
            throw;
        }
    }

    public IWriteBatch StartWriteBatch()
    {
        IWriteBatch writeBatch = new RocksDbWriteBatch(this);
        _currentBatches.Add(writeBatch);
        return writeBatch;
    }

    internal class RocksDbWriteBatch : IWriteBatch
    {
        private readonly DbOnTheRocks _dbOnTheRocks;
        private WriteBatch _rocksBatch;
        private WriteFlags _writeFlags = WriteFlags.None;
        private bool _isDisposed;

        [ThreadStatic]
        private static WriteBatch? _reusableWriteBatch;

        /// <summary>
        /// Because of how rocksdb parallelize writes, a large write batch can stall other new concurrent writes, so
        /// we writes the batch in smaller batches. This removes atomicity so its only turned on when NoWAL flag is on.
        /// It does not work as well as just turning on unordered_write, but Snapshot and Iterator can still works.
        /// </summary>
        private const int MaxWritesOnNoWal = 128;
        private int _writeCount;

        public RocksDbWriteBatch(DbOnTheRocks dbOnTheRocks)
        {
            _dbOnTheRocks = dbOnTheRocks;
            _rocksBatch = CreateWriteBatch();

            ObjectDisposedException.ThrowIf(_dbOnTheRocks._isDisposing, _dbOnTheRocks);
        }

        private static WriteBatch CreateWriteBatch()
        {
            if (_reusableWriteBatch is null) return new WriteBatch();

            WriteBatch batch = _reusableWriteBatch;
            _reusableWriteBatch = null;
            return batch;
        }

        private static void ReturnWriteBatch(WriteBatch batch)
        {
            Native.Instance.rocksdb_writebatch_data(batch.Handle, out UIntPtr size);
            if (size > (uint)16.KiB() || _reusableWriteBatch is not null)
            {
                batch.Dispose();
                return;
            }

            batch.Clear();
            _reusableWriteBatch = batch;
        }

        public void Dispose()
        {
            ObjectDisposedException.ThrowIf(_dbOnTheRocks._isDisposed, _dbOnTheRocks);

            if (_isDisposed)
            {
                return;
            }
            _isDisposed = true;

            try
            {
                _dbOnTheRocks._db.Write(_rocksBatch, _dbOnTheRocks.WriteFlagsToWriteOptions(_writeFlags));

                _dbOnTheRocks._currentBatches.TryRemove(this);
                ReturnWriteBatch(_rocksBatch);
            }
            catch (RocksDbSharpException e)
            {
                _dbOnTheRocks.CreateMarkerIfCorrupt(e);
                throw;
            }
        }

        public void Delete(ReadOnlySpan<byte> key, ColumnFamilyHandle? cf = null)
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);

            _rocksBatch.Delete(key, cf);
        }

        public void Set(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, ColumnFamilyHandle? cf = null, WriteFlags flags = WriteFlags.None)
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);

            if (value.IsNull())
            {
                _rocksBatch.Delete(key, cf);
            }
            else
            {
                _rocksBatch.Put(key, value, cf);
            }
            _writeFlags = flags;

            if ((flags & WriteFlags.DisableWAL) != 0) FlushOnTooManyWrites();
        }

        public void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None)
        {
            Set(key, value, null, flags);
        }

        public void PutSpan(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, WriteFlags flags = WriteFlags.None)
        {
            Set(key, value, null, flags);
        }

        private void FlushOnTooManyWrites()
        {
            if (Interlocked.Increment(ref _writeCount) % MaxWritesOnNoWal != 0) return;

            WriteBatch currentBatch = Interlocked.Exchange(ref _rocksBatch, CreateWriteBatch());

            try
            {
                _dbOnTheRocks._db.Write(currentBatch, _dbOnTheRocks.WriteFlagsToWriteOptions(_writeFlags));
                ReturnWriteBatch(currentBatch);
            }
            catch (RocksDbSharpException e)
            {
                _dbOnTheRocks.CreateMarkerIfCorrupt(e);
                throw;
            }
        }
    }

    public void Flush()
    {
        ObjectDisposedException.ThrowIf(_isDisposing, this);

        InnerFlush();
    }

    public virtual void Compact()
    {
        _db.CompactRange(Keccak.Zero.BytesToArray(), Keccak.MaxValue.BytesToArray());
    }

    private void InnerFlush()
    {
        try
        {
            _rocksDbNative.rocksdb_flush(_db.Handle, FlushOptions.DefaultFlushOptions.Handle);
        }
        catch (RocksDbSharpException e)
        {
            CreateMarkerIfCorrupt(e);
        }
    }

    public void Clear()
    {
        Dispose();
        Delete();
    }

    private void Delete()
    {
        try
        {
            string fullPath = _fullPath!;
            if (Directory.Exists(fullPath))
            {
                // We want to keep the folder if it can have subfolders with copied databases from pruning
                if (_settings.CanDeleteFolder)
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
            if (_logger.IsWarn) _logger.Warn($"Could not delete the {Name} database. {e.Message}");
        }
    }

    private class FlushOptions
    {
        internal static FlushOptions DefaultFlushOptions { get; } = new();

        public FlushOptions()
        {
            Handle = RocksDbSharp.Native.Instance.rocksdb_flushoptions_create();
        }

        public IntPtr Handle { get; private set; }

        ~FlushOptions()
        {
            if (Handle != IntPtr.Zero)
            {
                RocksDbSharp.Native.Instance.rocksdb_flushoptions_destroy(Handle);
                Handle = IntPtr.Zero;
            }
        }
    }

    private void ReleaseUnmanagedResources()
    {
        // ReSharper disable once ConstantConditionalAccessQualifier
        // running in finalizer, potentially not fully constructed
        foreach (IWriteBatch batch in _currentBatches)
        {
            batch.Dispose();
        }

        _readaheadIterators.DisposeAll();

        _db.Dispose();

        if (_cache.HasValue)
        {
            _rocksDbNative.rocksdb_cache_destroy(_cache.Value);
        }

        if (_rowCache.HasValue)
        {
            _rocksDbNative.rocksdb_cache_destroy(_rowCache.Value);
        }

        if (_rateLimiter.HasValue)
        {
            _rocksDbNative.rocksdb_ratelimiter_destroy(_rateLimiter.Value);
        }
    }

    public void Dispose()
    {
        if (_isDisposing) return;
        _isDisposing = true;

        if (_logger.IsInfo) _logger.Info($"Disposing DB {Name}");

        foreach (IDisposable dbMetricsUpdater in _metricsUpdaters)
        {
            dbMetricsUpdater.Dispose();
        }

        InnerFlush();
        ReleaseUnmanagedResources();

        _dbsByPath.Remove(_fullPath!, out _);

        _isDisposed = true;
    }

    public static string GetFullDbPath(string dbPath, string basePath) => dbPath.GetApplicationResourcePath(basePath);

    /// <summary>
    /// Returns RocksDB version.
    /// </summary>
    /// <remarks>Since NuGet package version matches the underlying RocksDB native library version,
    /// this method returns the package version.</remarks>
    public static string? GetRocksDbVersion()
    {
        Assembly? rocksDbAssembly = Assembly.GetAssembly(typeof(RocksDb));
        Version? version = rocksDbAssembly?.GetName().Version;
        return version?.ToString(3);
    }

    public virtual void Tune(ITunableDb.TuneType type)
    {
        if (_currentTune == type) return;

        // See https://github.com/EighteenZi/rocksdb_wiki/blob/master/RocksDB-Tuning-Guide.md
        switch (type)
        {
            // Depending on tune type, allow num of L0 files to grow causing compaction to occur in larger size. This
            // reduces write amplification at the expense of read response time and amplification while the tune is
            // active. Additionally, the larger compaction causes larger spikes of IO, larger memory usage, and may temporarily
            // use up large amount of disk space. User may not want to enable this if they plan to run a validator node
            // while the node is still syncing, or run another node on the same machine. Specifying a rate limit
            // smoothens this spike somewhat by not blocking writes while allowing compaction to happen in background
            // at 1/10th the specified speed (if rate limited).
            //
            // Total writes written on different tune during mainnet sync in TB.
            // +-----------------------+-------+-------+-------+-------+-------+---------+
            // | L0FileNumTarget       | Total | State | Code  | Header| Blocks| Receipts |
            // +-----------------------+-------+-------+-------+-------+-------+---------+
            // | Default               | 5.055 | 2.27  | 0.242 | 0.123 | 1.14  | 1.280   |
            // | WriteBias             | 4.962 | 2.12  | 0.049 | 0.132 | 1.14  | 1.080   |
            // | HeavyWrite            | 3.592 | 1.32  | 0.032 | 0.116 | 1.14  | 0.984   |
            // | AggressiveHeavyWrite  | 3.029 | 0.92  | 0.024 | 0.118 | 1.14  | 0.827   |
            // | DisableCompaction     | 2.215 | 0.36  | 0.031 | 0.137 | 1.14  | 0.547   |
            // +-----------------------+-------+-------+-------+-------+-------+---------+
            // Note, in practice on my machine, the reads does not reach the SSD. Read measured from SSD is much lower
            // than read measured from process. It is likely that most files are cached as I have 128GB of RAM.
            // Also notice that the heavier the tune, the higher the reads.
            case ITunableDb.TuneType.WriteBias:
                // Keep the same l1 size but apply other adjustment which should increase buffer number and make
                // l0 the same size as l1, but keep the LSM the same. This improve flush parallelization, and
                // write amplification due to mismatch of l0 and l1 size, but does not reduce compaction from other
                // levels.
                ApplyOptions(GetHeavyWriteOptions(_perTableDbConfig.MaxBytesForLevelBase));
                break;
            case ITunableDb.TuneType.HeavyWrite:
                // Compaction spikes are clear at this point. Will definitely affect attestation performance.
                // Its unclear if it improve or slow down sync time. Seems to be the sweet spot.
                ApplyOptions(GetHeavyWriteOptions((ulong)4.GiB()));
                break;
            case ITunableDb.TuneType.AggressiveHeavyWrite:
                // For when, you are desperate, but don't wanna disable compaction completely, because you don't want
                // peers to drop. Tend to be faster than disabling compaction completely, except if your ratelimit
                // is a bit low and your compaction is lagging behind, which will trigger slowdown, so sync will hang
                // intermittently, but at least peer count is stable.
                ApplyOptions(GetHeavyWriteOptions((ulong)16.GiB()));
                break;
            case ITunableDb.TuneType.DisableCompaction:
                // Completely disable compaction. On mainnet, max num of l0 files for state seems to be about 10800.
                // Blocksdb are way more at 53000. Final compaction for state db need 30 minute, while blocks db need
                // 13 hour. Receipts db don't show up in metrics likely because its a column db.
                // Ram usage at that time was 86 GB. The default buffer size for blocks on mainnet is too low
                // to make this work reasonably well.
                // L0 to L1 compaction is known to be slower than other level so its
                // Snap sync performance suffer as it does have some read during stitching.
                // If you don't specify a lower open files limit, it has a tendency to crash, like.. the whole system
                // crash. I don't have any open file limit at OS level.
                // Also, if a peer send a packet that causes a query to the state db during snap sync like GetNodeData
                // or some of the tx filter querying state, It'll cause the network stack to hang and triggers a
                // large peer drops. Also happens on lesser tune, but weaker.
                // State sync essentially hang until that completes because its read heavy, and the uncompacted db is
                // slow to a halt.
                // Additionally, the number of open files handles measured from collectd jumped massively higher. Some
                // user config may not be able to handle this.
                // With all those cons, this result in the minimum write amplification possible via tweaking compaction
                // without changing memory budget. Not recommended for mainnet, unless you are very desperate.
                ApplyOptions(GetDisableCompactionOptions());
                break;
            case ITunableDb.TuneType.EnableBlobFiles:
                ApplyOptions(GetBlobFilesOptions());
                break;
            case ITunableDb.TuneType.Default:
            default:
                ApplyOptions(GetStandardOptions());
                break;
        }

        _currentTune = type;
    }

    protected virtual void ApplyOptions(IDictionary<string, string> options)
    {
        _db.SetOptions(options);
    }

    private IDictionary<string, string> GetStandardOptions()
    {
        // Defaults are from rocksdb source code
        return new Dictionary<string, string>()
        {
            { "write_buffer_size", _perTableDbConfig.WriteBufferSize.ToString() },
            { "max_write_buffer_number", _perTableDbConfig.WriteBufferNumber.ToString() },

            { "level0_file_num_compaction_trigger", 4.ToString() },
            { "level0_slowdown_writes_trigger", 20.ToString() },

            // Very high, so that after moving from HeavyWrite, we don't immediately hang.
            // This does means that under very rare case, the l0 file can accumulate, which slow down the db
            // until they get compacted.
            { "level0_stop_writes_trigger", 1024.ToString() },

            { "max_bytes_for_level_base", _perTableDbConfig.MaxBytesForLevelBase.ToString() },
            { "target_file_size_base", _perTableDbConfig.TargetFileSizeBase.ToString() },
            { "disable_auto_compactions", "false" },

            { "enable_blob_files", "false" },

            { "soft_pending_compaction_bytes_limit", 64.GiB().ToString() },
            { "hard_pending_compaction_bytes_limit", 256.GiB().ToString() },
        };
    }

    /// <summary>
    /// Allow num of l0 file to grow very large. This dramatically increase read response time by about
    /// (l0FileNumTarget / (default num (4) + max level usually (4)). but it saves write bandwidth as l0->l1 happens
    /// in larger size. In addition to that, the large base l1 size means the number of level is a bit lower.
    /// Note: Regardless of max_open_files config, the number of files handle jumped by this number when compacting. It
    /// could be that l0->l1 compaction does not (or cant?) follow the max_open_files limit.
    /// </summary>
    /// <param name="l0FileNumTarget">
    ///  This caps the maximum allowed number of l0 files, which is also the read response time amplification.
    /// </param>
    /// <returns></returns>
    private IDictionary<string, string> GetHeavyWriteOptions(ulong l0SizeTarget)
    {
        // Make buffer (probably) smaller so that it does not take too much memory to have many of them.
        // More buffer means more parallel flush, but each read have to go through all buffer one by one much like l0
        // but no io, only cpu.
        // bufferSize*maxBufferNumber = 128MB, which is the max memory used, which tend to be the case as its now
        // stalled by compaction instead of flush.
        ulong bufferSize = (ulong)16.MiB();
        ulong l0FileSize = bufferSize * (ulong)_perTableDbConfig.MinWriteBufferNumberToMerge;
        ulong maxBufferNumber = 8;

        // Guide recommend to have l0 and l1 to be the same size. They have to be compacted together so if l1 is larger,
        // the extra size in l1 is basically extra rewrites. If l0 is larger... then I don't know why not. Even so, it seems to
        // always get triggered when l0 size exceed max_bytes_for_level_base even if file num is less than l0FileNumTarget.
        ulong l0FileNumTarget = l0SizeTarget / l0FileSize;
        ulong l1SizeTarget = l0SizeTarget;

        return new Dictionary<string, string>()
        {
            { "write_buffer_size", bufferSize.ToString() },
            { "max_write_buffer_number", maxBufferNumber.ToString() },

            { "max_bytes_for_level_base", l1SizeTarget.ToString() },
            { "level0_file_num_compaction_trigger", l0FileNumTarget.ToString() },

            // Note: If ratelimiter is not specified and if delayed_write_rate is not specified, the default is 16MBps.
            //   which basically means it'll hang.
            { "level0_slowdown_writes_trigger", (l0FileNumTarget * 2).ToString() },
            { "level0_stop_writes_trigger", (l0FileNumTarget * 4).ToString() },

            // Very high, so slowdown is only triggered by file num. Make things easier to predict.
            { "soft_pending_compaction_bytes_limit", 100000.GiB().ToString() },
            { "hard_pending_compaction_bytes_limit", 100000.GiB().ToString() },
        };
    }

    private IDictionary<string, string> GetDisableCompactionOptions()
    {
        IDictionary<string, string> heavyWriteOption = GetHeavyWriteOptions((ulong)32.GiB());

        heavyWriteOption["disable_auto_compactions"] = "true";
        // Increase the size of the write buffer, which reduces the number of l0 file by 4x. This does slows down
        // the memtable a little bit. So if you are not write limited, you'll get memtable limited instead.
        // This does increase the total memory buffer size, but counterintuitively, this reduces overall memory usage
        // as it ran out of bloom filter cache so it need to do actual IO.
        heavyWriteOption["write_buffer_size"] = 64.MiB().ToString();

        return heavyWriteOption;
    }


    private static IDictionary<string, string> GetBlobFilesOptions()
    {
        // Enable blob files, see: https://rocksdb.org/blog/2021/05/26/integrated-blob-db.html
        // This is very useful for blocks, as it almost eliminate 95% of the compaction as the main db no longer
        // store the actual data, but only points to blob files. This config reduces total blocks db writes from about
        // 4.6 TB to 0.76 TB, where even the the WAL took 0.45 TB (wal is not compressed), with peak writes of about 300MBps,
        // it may not even saturate a SATA SSD on a 1GBps internet.

        // You don't want to turn this on on other DB as it does add an indirection which take up an additional iop.
        // But for large values like blocks (3MB decompressed to 8MB), the response time increase is negligible.
        // However without a large buffer size, it will create tens of thousands of small files. There are
        // various workaround it, but it all increase total writes, which defeats the purpose.
        // Additionally, as the `max_bytes_for_level_base` is set to very low, existing user will suddenly
        // get a lot of compaction. So cant turn this on all the time. Turning this back off, will just put back
        // new data to SST files.

        return new Dictionary<string, string>()
        {
            { "enable_blob_files", "true" },
            { "blob_compression_type", "kSnappyCompression" },

            // Make file size big, so we have less of them.
            { "write_buffer_size", 256.MiB().ToString() },
            // Current memtable + 2 concurrent writes. Can't have too many of these as it take up RAM.
            { "max_write_buffer_number", 3.ToString() },

            // These two are SST files instead of the blobs, which are now much smaller.
            { "max_bytes_for_level_base", 4.MiB().ToString() },
            { "target_file_size_base", 1.MiB().ToString() },
        };
    }

    // Note: use of threadlocal is very important as the seek forward is fast, but the seek backward is not fast.
    internal sealed class ManagedIterators : ThreadLocal<Iterator>
    {
        public ManagedIterators() : base(trackAllValues: true)
        {
        }

        public void DisposeAll()
        {
            foreach (Iterator iterator in Values)
            {
                iterator.Dispose();
            }

            Dispose();
        }

        protected override void Dispose(bool disposing)
        {
            // Note: This is called from finalizer thread, so we can't use foreach to dispose all values
            Value?.Dispose();
            Value = null!;
            base.Dispose(disposing);
        }
    }
}
