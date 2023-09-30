// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Reflection;
using System.Threading;
using ConcurrentCollections;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db.Rocks.Config;
using Nethermind.Db.Rocks.Statistics;
using Nethermind.Logging;
using RocksDbSharp;

namespace Nethermind.Db.Rocks;

public class DbOnTheRocks : IDbWithSpan, ITunableDb
{
    private ILogger _logger;

    private string? _fullPath;

    private static readonly ConcurrentDictionary<string, RocksDb> _dbsByPath = new();

    private bool _isDisposing;
    private bool _isDisposed;

    private readonly ConcurrentHashSet<IBatch> _currentBatches = new();

    internal readonly RocksDb _db;

    private IntPtr? _rateLimiter;
    internal WriteOptions? WriteOptions { get; private set; }
    private WriteOptions? _noWalWrite;
    private WriteOptions? _lowPriorityAndNoWalWrite;
    private WriteOptions? _lowPriorityWriteOptions;
    private ReadOptions? _readAheadReadOptions = null;

    internal DbOptions? DbOptions { get; private set; }

    public string Name { get; }

    private static long _maxRocksSize;

    private long _maxThisDbSize;

    protected IntPtr? _cache = null;

    private readonly RocksDbSettings _settings;

    protected readonly PerTableDbConfig _perTableDbConfig;

    private readonly IFileSystem _fileSystem;

    protected readonly RocksDbSharp.Native _rocksDbNative;

    private ITunableDb.TuneType _currentTune = ITunableDb.TuneType.Default;

    private string CorruptMarkerPath => Path.Join(_fullPath, "corrupt.marker");

    private List<DbMetricsUpdater> _metricsUpdaters = new();

    private ManagedIterators _readaheadIterators = new();

    public DbOnTheRocks(
        string basePath,
        RocksDbSettings rocksDbSettings,
        IDbConfig dbConfig,
        ILogManager logManager,
        IList<string>? columnFamilies = null,
        RocksDbSharp.Native? rocksDbNative = null,
        IFileSystem? fileSystem = null,
        IntPtr? sharedCache = null)
    {
        _logger = logManager.GetClassLogger();
        _settings = rocksDbSettings;
        Name = _settings.DbName;
        _fileSystem = fileSystem ?? new FileSystem();
        _rocksDbNative = rocksDbNative ?? RocksDbSharp.Native.Instance;
        _perTableDbConfig = new PerTableDbConfig(dbConfig, _settings);
        _db = Init(basePath, rocksDbSettings.DbPath, dbConfig, logManager, columnFamilies, rocksDbSettings.DeleteOnStart, sharedCache);

        if (_perTableDbConfig.AdditionalRocksDbOptions != null)
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
        _logger = logManager?.GetClassLogger() ?? NullLogger.Instance;
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
            if (columnNames != null)
            {
                columnFamilies = new ColumnFamilies();
                foreach (string columnFamily in columnNames)
                {
                    ColumnFamilyOptions options = new();
                    BuildOptions(new PerTableDbConfig(dbConfig, _settings, columnFamily), options, sharedCache);
                    columnFamilies.Add(columnFamily, options);
                }
            }

            // ReSharper disable once VirtualMemberCallInConstructor
            if (_logger.IsDebug) _logger.Debug($"Loading DB {Name,-13} from {_fullPath} with max memory footprint of {_maxThisDbSize / 1000 / 1000,5} MB");
            RocksDb db = _dbsByPath.GetOrAdd(_fullPath, (s, tuple) => Open(s, tuple), (DbOptions, columnFamilies));

            if (dbConfig.EnableMetricsUpdater)
            {
                _metricsUpdaters.Add(new DbMetricsUpdater(Name, DbOptions, db, null, dbConfig, _logger));
                if (columnFamilies != null)
                {
                    foreach (ColumnFamilies.Descriptor columnFamily in columnFamilies)
                    {
                        if (db.TryGetColumnFamily(columnFamily.Name, out ColumnFamilyHandle handle))
                        {
                            _metricsUpdaters.Add(new DbMetricsUpdater(Name + "_" + columnFamily.Name, DbOptions, db, handle, dbConfig, _logger));
                        }
                    }
                }

                foreach (DbMetricsUpdater metricsUpdater in _metricsUpdaters)
                {
                    metricsUpdater.StartUpdating();
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
            if (_logger.IsWarn) _logger.Warn("If your database did not close properly you need to call 'find -type f -name '*LOCK*' -delete' from the databse folder");
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
        if (_settings.UpdateReadMetrics is not null)
            _settings.UpdateReadMetrics?.Invoke();
        else
            Metrics.OtherDbReads++;
    }

    protected internal void UpdateWriteMetrics()
    {
        if (_settings.UpdateWriteMetrics is not null)
            _settings.UpdateWriteMetrics?.Invoke();
        else
            Metrics.OtherDbWrites++;
    }

    public long GetSize()
    {
        try
        {
            return long.TryParse(_db.GetProperty("rocksdb.total-sst-files-size"), out long size) ? size : 0;
        }
        catch (RocksDbSharpException e)
        {
            if (_logger.IsWarn)
                _logger.Warn($"Failed to update DB size metrics {e.Message}");
        }

        return 0;
    }

    public long GetCacheSize()
    {
        try
        {
            return long.TryParse(_db.GetProperty("rocksdb.block-cache-usage"), out long size) ? size : 0;
        }
        catch (RocksDbSharpException e)
        {
            if (_logger.IsWarn)
                _logger.Warn($"Failed to update DB size metrics {e.Message}");
        }

        return 0;
    }

    public long GetIndexSize()
    {
        try
        {
            return long.TryParse(_db.GetProperty("rocksdb.estimate-table-readers-mem"), out long size) ? size : 0;
        }
        catch (RocksDbSharpException e)
        {
            if (_logger.IsWarn)
                _logger.Warn($"Failed to update DB size metrics {e.Message}");
        }

        return 0;
    }

    public long GetMemtableSize()
    {
        try
        {
            return long.TryParse(_db.GetProperty("rocksdb.cur-size-all-mem-tables"), out long size) ? size : 0;
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
        _maxThisDbSize = 0;
        BlockBasedTableOptions tableOptions = new();
        tableOptions.SetBlockSize((ulong)(dbConfig.BlockSize ?? 16 * 1024));
        tableOptions.SetPinL0FilterAndIndexBlocksInCache(true);
        tableOptions.SetCacheIndexAndFilterBlocks(dbConfig.CacheIndexAndFilterBlocks);
        tableOptions.SetIndexType(BlockBasedTableIndexType.TwoLevelIndex);
        _rocksDbNative.rocksdb_block_based_options_set_partition_filters(tableOptions.Handle, true);
        _rocksDbNative.rocksdb_block_based_options_set_metadata_block_size(tableOptions.Handle, 4096);
        _rocksDbNative.rocksdb_block_based_options_set_cache_index_and_filter_blocks_with_high_priority(tableOptions.Handle, true);
        tableOptions.SetFormatVersion(5);

        /*
        ColumnFamilyOptions* ColumnFamilyOptions::OptimizeForPointLookup(
            uint64_t block_cache_size_mb) {
          BlockBasedTableOptions block_based_options;
          block_based_options.data_block_index_type =
              BlockBasedTableOptions::kDataBlockBinaryAndHash;
          block_based_options.data_block_hash_table_util_ratio = 0.75;
          block_based_options.filter_policy.reset(NewBloomFilterPolicy(10));
          block_based_options.block_cache =
              NewLRUCache(static_cast<size_t>(block_cache_size_mb * 1024 * 1024));
          table_factory.reset(new BlockBasedTableFactory(block_based_options));
          memtable_prefix_bloom_size_ratio = 0.02;
          memtable_whole_key_filtering = true;
          return this;
        }
         */

        // Rewrote OptimizeForPointLookup to be able to use shared block cache.
        tableOptions.SetFilterPolicy(BloomFilterPolicy.Create(10, false));

        // In theory, this should reduce CPU, but I don't see any different.
        // It seems increase disk space use by about 1 GB, which again, could be just noise. I'll just keep this.
        // That said, on lower block size, it'll probably be useless.
        _rocksDbNative.rocksdb_block_based_options_set_data_block_index_type(tableOptions.Handle, 1);
        _rocksDbNative.rocksdb_block_based_options_set_data_block_hash_ratio(tableOptions.Handle, 0.75);

        _rocksDbNative.rocksdb_options_set_memtable_whole_key_filtering(options.Handle, true);
        _rocksDbNative.rocksdb_options_set_memtable_prefix_bloom_size_ratio(options.Handle, 0.02);
        options.SetOptimizeFiltersForHits(1);

        ulong blockCacheSize = dbConfig.BlockCacheSize;
        if (sharedCache != null && blockCacheSize == 0)
        {
            tableOptions.SetBlockCache(sharedCache.Value);
        }
        else
        {
            _cache = RocksDbSharp.Native.Instance.rocksdb_cache_create_lru(new UIntPtr(blockCacheSize));
            tableOptions.SetBlockCache(_cache.Value);
        }

        options.SetCreateIfMissing();
        options.SetAdviseRandomOnOpen(true);

        /*
         * Multi-Threaded Compactions
         * Compactions are needed to remove multiple copies of the same key that may occur if an application overwrites an existing key. Compactions also process deletions of keys. Compactions may occur in multiple threads if configured appropriately.
         * The entire database is stored in a set of sstfiles. When a memtable is full, its content is written out to a file in Level-0 (L0). RocksDB removes duplicate and overwritten keys in the memtable when it is flushed to a file in L0. Some files are periodically read in and merged to form larger files - this is called compaction.
         * The overall write throughput of an LSM database directly depends on the speed at which compactions can occur, especially when the data is stored in fast storage like SSD or RAM. RocksDB may be configured to issue concurrent compaction requests from multiple threads. It is observed that sustained write rates may increase by as much as a factor of 10 with multi-threaded compaction when the database is on SSDs, as compared to single-threaded compactions.
         * TKS: Observed 500MB/s compared to ~100MB/s between multithreaded and single thread compactions on my machine (processor count is returning 12 for 6 cores with hyperthreading)
         * TKS: CPU goes to insane 30% usage on idle - compacting only app
         */
        options.SetMaxBackgroundCompactions(Environment.ProcessorCount);

        if (dbConfig.MaxOpenFiles.HasValue)
        {
            options.SetMaxOpenFiles(dbConfig.MaxOpenFiles.Value);
        }

        if (dbConfig.MaxBytesPerSec.HasValue)
        {
            _rateLimiter =
                _rocksDbNative.rocksdb_ratelimiter_create(dbConfig.MaxBytesPerSec.Value, 1000, 10);
            _rocksDbNative.rocksdb_options_set_ratelimiter(options.Handle, _rateLimiter.Value);
        }

        ulong writeBufferSize = dbConfig.WriteBufferSize;
        options.SetWriteBufferSize(writeBufferSize);
        int writeBufferNumber = (int)dbConfig.WriteBufferNumber;
        options.SetMaxWriteBufferNumber(writeBufferNumber);

        // Memtable is slower to writes the bigger it gets, but small memtable means small l0 file size, which means
        // higher write amp for l0->l1 compaction. This allows multiple memtable to go into one l0 file so that it is
        // not too small so it helps with write amp when setting small write buffer. AFAIK that is its only use compared
        // to just specifying large write buffer. Sadly, this setting can't dynamically change.
        options.SetMinWriteBufferNumberToMerge(2);

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

        options.SetBlockBasedTableFactory(tableOptions);

        options.SetMaxBackgroundFlushes(Environment.ProcessorCount);
        options.IncreaseParallelism(Environment.ProcessorCount);
        options.SetRecycleLogFileNum(dbConfig
            .RecycleLogFileNum); // potential optimization for reusing allocated log files

        options.SetMaxBytesForLevelBase(dbConfig.MaxBytesForLevelBase);
        options.SetUseDirectReads(dbConfig.UseDirectReads.GetValueOrDefault());
        options.SetUseDirectIoForFlushAndCompaction(dbConfig.UseDirectIoForFlushAndCompactions.GetValueOrDefault());

        // VERY important to reduce stalls. Allow L0->L1 compaction to happen with multiple thread.
        _rocksDbNative.rocksdb_options_set_max_subcompactions(options.Handle, (uint)Environment.ProcessorCount);

        if (dbConfig.CompactionReadAhead != null && dbConfig.CompactionReadAhead != 0)
        {
            options.SetCompactionReadaheadSize(dbConfig.CompactionReadAhead.Value);
        }

        if (dbConfig.DisableCompression == true)
        {
            options.SetCompression(Compression.No);
        }

        if (dbConfig.EnableDbStatistics)
        {
            options.EnableStatistics();
        }
        options.SetStatsDumpPeriodSec(dbConfig.StatsDumpPeriodSec);

        WriteOptions = CreateWriteOptions(dbConfig);

        _noWalWrite = CreateWriteOptions(dbConfig);
        _noWalWrite.DisableWal(1);

        _lowPriorityWriteOptions = CreateWriteOptions(dbConfig);
        Native.Instance.rocksdb_writeoptions_set_low_pri(_lowPriorityWriteOptions.Handle, true);

        _lowPriorityAndNoWalWrite = CreateWriteOptions(dbConfig);
        _lowPriorityAndNoWalWrite.DisableWal(1);
        Native.Instance.rocksdb_writeoptions_set_low_pri(_lowPriorityAndNoWalWrite.Handle, true);

        // When readahead flag is on, the next keys are expected to be after the current key. Increasing this value,
        // will increase the chances that the next keys will be in the cache, which reduces iops and latency. This
        // increases throughput, however, if a lot of the keys are not close to the current key, it will increase read
        // bandwidth requirement, since each read must be at least this size. This value is tuned for a batched trie
        // visitor on mainnet with 4GB memory budget and 4Gbps read bandwidth.
        if (dbConfig.ReadAheadSize != 0)
        {
            _readAheadReadOptions = new ReadOptions();
            _readAheadReadOptions.SetReadaheadSize(dbConfig.ReadAheadSize ?? (ulong)256.KiB());
            _readAheadReadOptions.SetTailing(true);
        }
    }

    private WriteOptions CreateWriteOptions(PerTableDbConfig dbConfig) {
        WriteOptions options = new();

        // potential fix for corruption on hard process termination, may cause performance degradation
        options.SetSync(dbConfig.WriteAheadLogSync);

        // On concurrent writes, the `hint` here is the node within the skiplist. Makes concurrent sorted writes faster.
        _rocksDbNative.rocksdb_writeoptions_set_memtable_insert_hint_per_batch(options.Handle, true);
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
        if (_isDisposing)
        {
            throw new ObjectDisposedException($"Attempted to read form a disposed database {Name}");
        }

        UpdateReadMetrics();

        try
        {
            if (_readAheadReadOptions != null && (flags & ReadFlags.HintReadAhead) != 0)
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

            return _db.Get(key, cf);
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

    internal void SetWithColumnFamily(ReadOnlySpan<byte> key, ColumnFamilyHandle? cf, byte[]? value, WriteFlags flags = WriteFlags.None)
    {
        if (_isDisposing)
        {
            throw new ObjectDisposedException($"Attempted to write to a disposed database {Name}");
        }

        UpdateWriteMetrics();

        try
        {
            if (value is null)
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

    public Span<byte> GetSpan(ReadOnlySpan<byte> key)
    {
        if (_isDisposing)
        {
            throw new ObjectDisposedException($"Attempted to read form a disposed database {Name}");
        }

        UpdateReadMetrics();

        try
        {
            Span<byte> span = _db.GetSpan(key);
            if (!span.IsNullOrEmpty())
                GC.AddMemoryPressure(span.Length);
            return span;
        }
        catch (RocksDbSharpException e)
        {
            CreateMarkerIfCorrupt(e);
            throw;
        }
    }

    public void PutSpan(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value)
    {
        if (_isDisposing)
        {
            throw new ObjectDisposedException($"Attempted to write form a disposed database {Name}");
        }

        UpdateWriteMetrics();

        try
        {
            _db.Put(key, value, null, WriteOptions);
        }
        catch (RocksDbSharpException e)
        {
            CreateMarkerIfCorrupt(e);
            throw;
        }
    }

    public void DangerousReleaseMemory(in Span<byte> span)
    {
        if (!span.IsNullOrEmpty())
            GC.RemoveMemoryPressure(span.Length);
        _db.DangerousReleaseMemory(span);
    }

    public void Remove(ReadOnlySpan<byte> key)
    {
        if (_isDisposing)
        {
            throw new ObjectDisposedException($"Attempted to delete form a disposed database {Name}");
        }

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
        if (_isDisposing)
        {
            throw new ObjectDisposedException($"Attempted to create an iterator on a disposed database {Name}");
        }

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

    public IEnumerable<byte[]> GetAllValues(bool ordered = false)
    {
        if (_isDisposing)
        {
            throw new ObjectDisposedException($"Attempted to read form a disposed database {Name}");
        }

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

    public IEnumerable<KeyValuePair<byte[], byte[]?>> GetAllCore(Iterator iterator)
    {
        try
        {
            if (_isDisposing)
            {
                throw new ObjectDisposedException($"Attempted to read form a disposed database {Name}");
            }

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
        if (_isDisposing)
        {
            throw new ObjectDisposedException($"Attempted to read form a disposed database {Name}");
        }

        try
        {
            // seems it has no performance impact
            return _db.Get(key) is not null;
            // return _db.Get(key, 32, _keyExistsBuffer, 0, 0, null, null) != -1;
        }
        catch (RocksDbSharpException e)
        {
            CreateMarkerIfCorrupt(e);
            throw;
        }
    }

    public IBatch StartBatch()
    {
        IBatch batch = new RocksDbBatch(this);
        _currentBatches.Add(batch);
        return batch;
    }

    internal class RocksDbBatch : IBatch
    {
        private readonly DbOnTheRocks _dbOnTheRocks;
        private readonly WriteBatch _rocksBatch;
        private WriteFlags _writeFlags = WriteFlags.None;
        private bool _isDisposed;

        public RocksDbBatch(DbOnTheRocks dbOnTheRocks)
        {
            _dbOnTheRocks = dbOnTheRocks;
            _rocksBatch = new WriteBatch();

            if (_dbOnTheRocks._isDisposing)
            {
                throw new ObjectDisposedException($"Attempted to create a batch on a disposed database {_dbOnTheRocks.Name}");
            }
        }

        public void Dispose()
        {
            if (_dbOnTheRocks._isDisposed)
            {
                throw new ObjectDisposedException($"Attempted to commit a batch on a disposed database {_dbOnTheRocks.Name}");
            }

            if (_isDisposed)
            {
                return;
            }
            _isDisposed = true;

            try
            {
                _dbOnTheRocks._db.Write(_rocksBatch, _dbOnTheRocks.WriteFlagsToWriteOptions(_writeFlags));
                _dbOnTheRocks._currentBatches.TryRemove(this);
                _rocksBatch.Dispose();
            }
            catch (RocksDbSharpException e)
            {
                _dbOnTheRocks.CreateMarkerIfCorrupt(e);
                throw;
            }
        }

        public byte[]? Get(ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None)
        {
            // Not checking _isDisposing here as for some reason, sometimes is is read after dispose
            // Note: The batch itself is not checked. Will need to use WriteBatchWithIndex to do that, but that will
            // hurt perf.
            return _dbOnTheRocks.Get(key, flags);
        }

        public void Delete(ReadOnlySpan<byte> key, ColumnFamilyHandle? cf = null)
        {
            if (_isDisposed)
            {
                throw new ObjectDisposedException($"Attempted to write a disposed batch {_dbOnTheRocks.Name}");
            }

            _rocksBatch.Delete(key, cf);
        }

        public void Set(ReadOnlySpan<byte> key, byte[]? value, ColumnFamilyHandle? cf = null, WriteFlags flags = WriteFlags.None)
        {
            if (_isDisposed)
            {
                throw new ObjectDisposedException($"Attempted to write a disposed batch {_dbOnTheRocks.Name}");
            }

            _rocksBatch.Put(key, value, cf);
            _writeFlags = flags;
        }

        public void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None)
        {
            if (_isDisposed)
            {
                throw new ObjectDisposedException($"Attempted to write a disposed batch {_dbOnTheRocks.Name}");
            }

            _rocksBatch.Put(key, value);
            _writeFlags = flags;
        }
    }

    public void Flush()
    {
        if (_isDisposing)
        {
            throw new ObjectDisposedException($"Attempted to flush a disposed database {Name}");
        }

        InnerFlush();
    }

    public void Compact()
    {
        _db.CompactRange(Keccak.Zero.BytesToArray(), Keccak.MaxValue.BytesToArray());
    }

    private void InnerFlush()
    {
        try
        {
            RocksDbSharp.Native.Instance.rocksdb_flush(_db.Handle, FlushOptions.DefaultFlushOptions.Handle);
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
        foreach (IBatch batch in _currentBatches)
        {
            batch.Dispose();
        }

        _readaheadIterators.DisposeAll();

        _db.Dispose();

        if (_cache.HasValue)
        {
            _rocksDbNative.rocksdb_cache_destroy(_cache.Value);
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

        foreach (DbMetricsUpdater dbMetricsUpdater in _metricsUpdaters)
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
                ApplyOptions(GetHeavyWriteOptions(_perTableDbConfig.MaxBytesForLevelBase / (ulong)8.MiB()));
                break;
            case ITunableDb.TuneType.HeavyWrite:
                // Compaction spikes are clear at this point. Will definitely affect attestation performance.
                // Its unclear if it improve or slow down sync time. Seems to be the sweet spot.
                ApplyOptions(GetHeavyWriteOptions(256));
                break;
            case ITunableDb.TuneType.AggressiveHeavyWrite:
                // For when, you are desperate, but don't wanna disable compaction completely, because you don't want
                // peers to drop. Tend to be faster than disabling compaction completely, except if your ratelimit
                // is a bit low and your compaction is lagging behind, which will trigger slowdown, so sync will hang
                // intermittently, but at least peer count is stable.
                ApplyOptions(GetHeavyWriteOptions(1024));
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
            { "level0_stop_writes_trigger", 36.ToString() },

            { "max_bytes_for_level_base", _perTableDbConfig.MaxBytesForLevelBase.ToString() },
            { "target_file_size_base", 64.MiB().ToString() },
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
    private IDictionary<string, string> GetHeavyWriteOptions(ulong l0FileNumTarget)
    {
        // Make buffer (probably) smaller so that it does not take too much memory to have many of them.
        // More buffer means more parallel flush, but each read have to go through all buffer one by one much like l0
        // but no io, only cpu.
        // bufferSize*maxBufferNumber = 128MB, which is the max memory used, which tend to be the case as its now
        // stalled by compaction instead of flush.
        // The skiplist (memtable) have a branching factor of 4, so I assume it slows down a little as num of record
        // grows by 4x. So we are looking at 1,4,16,64,256,1k,4k,16k,64k and so on num of keys. Assuming average size of
        // 120 bytes per key for state db, we are targeting between 4k to 16k of keys per memtable.
        // A small memtable however, have the drawback of making l0 files smaller. Causing more compaction overhead.
        ulong bufferSize = (ulong)4.MiB();
        ulong writeBufferToMerge = 2;
        ulong maxBufferNumber = 16 * writeBufferToMerge;

        // Guide recommend to have l0 and l1 to be the same size. They have to be compacted together so if l1 is larger,
        // the extra size in l1 is basically extra rewrites. If l0 is larger... then I don't know why not. Even so, it seems to
        // always get triggered when l0 size exceed max_bytes_for_level_base even if file num is less than l0FileNumTarget.
        ulong l1SizeTarget = l0FileNumTarget * bufferSize * writeBufferToMerge;

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
        IDictionary<string, string> heavyWriteOption = GetHeavyWriteOptions(2048);

        heavyWriteOption["disable_auto_compactions"] = "true";
        // Increase the size of the write buffer, which reduces the number of l0 file by 4x. This does slows down
        // the memtable a little bit. So if you are not write limited, you'll get memtable limited instead.
        // This does increase the total memory buffer size, but counterintuitively, this reduces overall memory usage
        // as it ran out of bloom filter cache so it need to do actual IO.
        heavyWriteOption["write_buffer_size"] = 16.MiB().ToString();

        return heavyWriteOption;
    }


    private IDictionary<string, string> GetBlobFilesOptions()
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
            { "write_buffer_size", 64.MiB().ToString() },
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
