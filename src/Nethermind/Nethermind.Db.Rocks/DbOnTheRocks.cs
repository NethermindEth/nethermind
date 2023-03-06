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
using Nethermind.Core;
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

    internal DbOptions? DbOptions { get; private set; }

    public string Name { get; }

    private static long _maxRocksSize;

    private long _maxThisDbSize;

    private static int _cacheInitialized;

    protected static IntPtr _cache;

    private readonly RocksDbSettings _settings;

    protected readonly PerTableDbConfig _perTableDbConfig;

    private readonly IFileSystem _fileSystem;

    protected readonly RocksDbSharp.Native _rocksDbNative;

    private ITunableDb.TuneType _currentTune = ITunableDb.TuneType.Default;

    private string CorruptMarkerPath => Path.Join(_fullPath, "corrupt.marker");

    protected static void InitCache(IDbConfig dbConfig)
    {
        if (Interlocked.CompareExchange(ref _cacheInitialized, 1, 0) == 0)
        {
            _cache = RocksDbSharp.Native.Instance.rocksdb_cache_create_lru(new UIntPtr(dbConfig.BlockCacheSize));
            Interlocked.Add(ref _maxRocksSize, (long)dbConfig.BlockCacheSize);
        }
    }

    public DbOnTheRocks(
        string basePath,
        RocksDbSettings rocksDbSettings,
        IDbConfig dbConfig,
        ILogManager logManager,
        ColumnFamilies? columnFamilies = null,
        RocksDbSharp.Native? rocksDbNative = null,
        IFileSystem? fileSystem = null)
    {
        _logger = logManager.GetClassLogger();
        _settings = rocksDbSettings;
        Name = _settings.DbName;
        _fileSystem = fileSystem ?? new FileSystem();
        _rocksDbNative = rocksDbNative ?? RocksDbSharp.Native.Instance;
        _perTableDbConfig = new PerTableDbConfig(dbConfig, _settings);
        _db = Init(basePath, rocksDbSettings.DbPath, dbConfig, logManager, columnFamilies, rocksDbSettings.DeleteOnStart);
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
        ColumnFamilies? columnFamilies = null, bool deleteOnStart = false)
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
            DbOptions = BuildOptions(dbConfig);
            InitCache(dbConfig);

            // ReSharper disable once VirtualMemberCallInConstructor
            if (_logger.IsDebug) _logger.Debug($"Loading DB {Name,-13} from {_fullPath} with max memory footprint of {_maxThisDbSize / 1000 / 1000}MB");
            RocksDb db = _dbsByPath.GetOrAdd(_fullPath, (s, tuple) => Open(s, tuple), (DbOptions, columnFamilies));

            if (dbConfig.EnableMetricsUpdater)
            {
                new DbMetricsUpdater(Name, DbOptions, db, dbConfig, _logger).StartUpdating();
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

    protected virtual DbOptions BuildOptions(IDbConfig dbConfig)
    {
        _maxThisDbSize = 0;
        BlockBasedTableOptions tableOptions = new();
        tableOptions.SetBlockSize(16 * 1024);
        tableOptions.SetPinL0FilterAndIndexBlocksInCache(true);
        tableOptions.SetCacheIndexAndFilterBlocks(_perTableDbConfig.CacheIndexAndFilterBlocks);

        tableOptions.SetFilterPolicy(BloomFilterPolicy.Create());
        tableOptions.SetFormatVersion(4);

        ulong blockCacheSize = _perTableDbConfig.BlockCacheSize;

        tableOptions.SetBlockCache(_cache);

        // IntPtr cache = RocksDbSharp.Native.Instance.rocksdb_cache_create_lru(new UIntPtr(blockCacheSize));
        // tableOptions.SetBlockCache(cache);

        DbOptions options = new();
        options.SetCreateIfMissing();
        options.SetAdviseRandomOnOpen(true);
        options.OptimizeForPointLookup(
            blockCacheSize); // I guess this should be the one option controlled by the DB size property - bind it to LRU cache size
        //options.SetCompression(CompressionTypeEnum.rocksdb_snappy_compression);
        //options.SetLevelCompactionDynamicLevelBytes(true);

        /*
         * Multi-Threaded Compactions
         * Compactions are needed to remove multiple copies of the same key that may occur if an application overwrites an existing key. Compactions also process deletions of keys. Compactions may occur in multiple threads if configured appropriately.
         * The entire database is stored in a set of sstfiles. When a memtable is full, its content is written out to a file in Level-0 (L0). RocksDB removes duplicate and overwritten keys in the memtable when it is flushed to a file in L0. Some files are periodically read in and merged to form larger files - this is called compaction.
         * The overall write throughput of an LSM database directly depends on the speed at which compactions can occur, especially when the data is stored in fast storage like SSD or RAM. RocksDB may be configured to issue concurrent compaction requests from multiple threads. It is observed that sustained write rates may increase by as much as a factor of 10 with multi-threaded compaction when the database is on SSDs, as compared to single-threaded compactions.
         * TKS: Observed 500MB/s compared to ~100MB/s between multithreaded and single thread compactions on my machine (processor count is returning 12 for 6 cores with hyperthreading)
         * TKS: CPU goes to insane 30% usage on idle - compacting only app
         */
        options.SetMaxBackgroundCompactions(Environment.ProcessorCount);

        if (_perTableDbConfig.MaxOpenFiles.HasValue)
        {
            options.SetMaxOpenFiles(_perTableDbConfig.MaxOpenFiles.Value);
        }

        if (_perTableDbConfig.MaxWriteBytesPerSec.HasValue)
        {
            _rateLimiter =
                _rocksDbNative.rocksdb_ratelimiter_create(_perTableDbConfig.MaxWriteBytesPerSec.Value, 1000, 10);
            _rocksDbNative.rocksdb_options_set_ratelimiter(options.Handle, _rateLimiter.Value);
        }

        ulong writeBufferSize = _perTableDbConfig.WriteBufferSize;
        options.SetWriteBufferSize(writeBufferSize);
        int writeBufferNumber = (int)_perTableDbConfig.WriteBufferNumber;
        options.SetMaxWriteBufferNumber(writeBufferNumber);
        options.SetMinWriteBufferNumberToMerge(2);

        lock (_dbsByPath)
        {
            _maxThisDbSize += (long)writeBufferSize * writeBufferNumber;
            Interlocked.Add(ref _maxRocksSize, _maxThisDbSize);
            if (_logger.IsDebug)
                _logger.Debug(
                    $"Expected max memory footprint of {Name} DB is {_maxThisDbSize / 1000 / 1000}MB ({writeBufferNumber} * {writeBufferSize / 1000 / 1000}MB + {blockCacheSize / 1000 / 1000}MB)");
            if (_logger.IsDebug) _logger.Debug($"Total max DB footprint so far is {_maxRocksSize / 1000 / 1000}MB");
            ThisNodeInfo.AddInfo("Mem est DB   :", $"{_maxRocksSize / 1000 / 1000}MB".PadLeft(8));
        }

        options.SetBlockBasedTableFactory(tableOptions);

        options.SetMaxBackgroundFlushes(Environment.ProcessorCount);
        options.IncreaseParallelism(Environment.ProcessorCount);
        options.SetRecycleLogFileNum(dbConfig
            .RecycleLogFileNum); // potential optimization for reusing allocated log files

        //            options.SetLevelCompactionDynamicLevelBytes(true); // only switch on on empty DBs
        WriteOptions = new WriteOptions();
        WriteOptions.SetSync(dbConfig
            .WriteAheadLogSync); // potential fix for corruption on hard process termination, may cause performance degradation

        if (dbConfig.EnableDbStatistics)
        {
            options.EnableStatistics();
        }
        options.SetStatsDumpPeriodSec(dbConfig.StatsDumpPeriodSec);

        return options;
    }

    public byte[]? this[byte[] key]
    {
        get
        {
            if (_isDisposing)
            {
                throw new ObjectDisposedException($"Attempted to read form a disposed database {Name}");
            }

            UpdateReadMetrics();

            try
            {
                return _db.Get(key);
            }
            catch (RocksDbSharpException e)
            {
                CreateMarkerIfCorrupt(e);
                throw;
            }
        }
        set
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
                    _db.Remove(key, null, WriteOptions);
                }
                else
                {
                    _db.Put(key, value, null, WriteOptions);
                }
            }
            catch (RocksDbSharpException e)
            {
                CreateMarkerIfCorrupt(e);
                throw;
            }
        }
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

    public Span<byte> GetSpan(byte[] key)
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

    public void PutSpan(byte[] key, ReadOnlySpan<byte> value)
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

    public void Remove(byte[] key)
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

    public IEnumerable<KeyValuePair<byte[], byte[]>> GetAll(bool ordered = false)
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

    public IEnumerable<KeyValuePair<byte[], byte[]>> GetAllCore(Iterator iterator)
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
            yield return new KeyValuePair<byte[], byte[]>(iterator.Key(), iterator.Value());

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

    public bool KeyExists(byte[] key)
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
        private bool _isDisposed;

        internal readonly WriteBatch _rocksBatch;

        public RocksDbBatch(DbOnTheRocks dbOnTheRocks)
        {
            _dbOnTheRocks = dbOnTheRocks;

            if (_dbOnTheRocks._isDisposing)
            {
                throw new ObjectDisposedException($"Attempted to create a batch on a disposed database {_dbOnTheRocks.Name}");
            }

            _rocksBatch = new WriteBatch();
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
                _dbOnTheRocks._db.Write(_rocksBatch, _dbOnTheRocks.WriteOptions);
                _dbOnTheRocks._currentBatches.TryRemove(this);
                _rocksBatch.Dispose();
            }
            catch (RocksDbSharpException e)
            {
                _dbOnTheRocks.CreateMarkerIfCorrupt(e);
                throw;
            }
        }

        public byte[]? this[byte[] key]
        {
            get
            {
                // Not checking _isDisposing here as for some reason, sometimes is is read after dispose
                return _dbOnTheRocks[key];
            }
            set
            {
                if (_isDisposed)
                {
                    throw new ObjectDisposedException($"Attempted to write a disposed batch {_dbOnTheRocks.Name}");
                }

                if (value is null)
                {
                    _rocksBatch.Delete(key);
                }
                else
                {
                    _rocksBatch.Put(key, value);
                }
            }
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

        _db.Dispose();

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
            // active. Additionally, the larger compaction causes larger spikes of IO. User may not want to enable this
            // if they plan to run a validator node while the node is still syncing, or run another node on the same
            // machine. Specifying a rate limit smoothens this spike somewhat by not blocking writes while allowing
            // compaction to happen in background at 1/10th the specified speed (if rate limited).
            //
            // Read and writes written on different tune during sync:
            // +---------------------------+---------+----------+-----------+-------------+
            // | Tune                      | Total   | SnapSync | OldBodies | OldReceipts |
            // +---------------------------+---------+----------+-----------+-------------+
            // | Default                   | 12.5 |  3.20 TB |   ?.?0 TB |             |
            // | WriteBias                 | 10.B |  5.68 TB / 2.60 TB |   4.00 TB |     4.00 TB |
            // | HeavyWrite                |  24.2 / 8.4 |  8.7 / 1.85 TB |   5.95 / 3.12 | 5.9 / 3.4 TB |
            // | AggressiveHeavyWrite      |  6.0 |  1.20 TB |   2.50 TB |     2.25 TB |
            // | VeryAggressiveHeavyWrite  |  ?.? |  ?.?? TB |           |             |
            // | DisableCompaction         |  2.9 |  0.67 TB |           |             |
            // +---------------------------+---------+----------+-----------+-------------+
            // Note, in practice on my system, the reads does not reach the SSD. Read measured from SSD is much lower
            // than read measured from process. It is likely that most files are cached as I have 128GB of RAM.
            case ITunableDb.TuneType.WriteBias:
                // The default l1SizeTarget is 256MB, so the compaction is fairly light. But the default options is not very
                // efficient for write amplification to conserve memory, so the write amplification reduction is noticeable.
                // Does not seems to impact sync performance, might improve sync time slightly if user is IO limited.
                ApplyOptions(GetHeavyWriteOptions(64, (ulong) 1.GiB()));
                break;
            case ITunableDb.TuneType.HeavyWrite:
                // Compaction spikes are clear at this point. Will definitely affect attestation performance.
                // Its unclear if it improve or slow down sync time. Seems to be the sweet spot.
                ApplyOptions(GetHeavyWriteOptions(256, (ulong) 5.GiB()));
                break;
            case ITunableDb.TuneType.AggressiveHeavyWrite:
                // Large, 1 minute long compaction every 7 minute or so. If you don't specify a ratelimiter, snap sync
                // effectively hang during this 1 minute and snap pivot change every time. Likely slow down sync time.
                ApplyOptions(GetHeavyWriteOptions(512, (ulong) 20.GiB()));
                break;
            case ITunableDb.TuneType.VeryAggressiveHeavyWrite:
                ApplyOptions(GetHeavyWriteOptions(1024, (ulong) 50.GiB()));
                break;
            case ITunableDb.TuneType.DisableCompaction:
                // Completely disable compaction. On mainnet, max num of l0 files for state seems to be about 5400.
                // Snap sync performance suffer as it does have some read during sync. If you don't specify
                // a lower open files limit, it has a tendency to crash. Also, if a peer send a packet that causes a query
                // to the state db during snap sync like GetNodeData or some of the tx filter querying state, It'll cause
                // the network stack to hang and triggers a large peer drops. Also happens on lesser tune, but weaker.
                // With all those cons, this result in the minimum write amplification possible via tweaking compaction.
                // Not recommended for mainnet, unless you are desperate.
                IDictionary<string, string> heavyWriteOption = GetHeavyWriteOptions(10000, (ulong) 100.GiB());
                heavyWriteOption["disable_auto_compactions"] = "true";
                ApplyOptions(heavyWriteOption);
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
            { "level0_file_num_compaction_trigger", 4.ToString() },
            { "level0_slowdown_writes_trigger", 20.ToString() },
            { "level0_stop_writes_trigger", 36.ToString() },

            { "max_bytes_for_level_base", 256.MiB().ToString() },
            { "disable_auto_compactions", "false" },

            { "soft_pending_compaction_bytes_limit", 64.GiB().ToString() },
            { "hard_pending_compaction_bytes_limit", 256.GiB().ToString() },
        };
    }

    /// <summary>
    /// Allow num of l0 file to grow very large. This dramatically increase read response time by about
    /// (l0FileNumTarget / (default num (4) + max level usually (4)). but it saves write bandwidth as l0->l1 happens
    /// in larger size.
    /// </summary>
    /// <param name="l0FileNumTarget">
    ///  This caps the maximum allowed number of l0 files, which is also the read response time amplification.
    ///  Sometimes, l1SizeTarget will take into effect first, but for some db, like code, this limit
    ///  will be reached first. It depends on the size of l0 files which seems to be its write buffer size * 2.
    /// </param>
    /// <param name="l1SizeTarget">
    ///  Specify a target size for l1. This target how much the l0->l1 will need to compact, which control
    ///  how often and how long do each compaction take, and also effect write amplification.
    ///
    ///  Contrary to the doc, l0->l1 compaction still seems to get triggered at l1 size even if l0 file num is not triggered.
    ///  Anyway, its recommended to set this the same size as l0 anyway.
    /// </param>
    /// <returns></returns>
    private IDictionary<string, string> GetHeavyWriteOptions(ulong l0FileNumTarget, ulong l1SizeTarget)
    {
        return new Dictionary<string, string>()
        {
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
}
