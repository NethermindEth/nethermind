// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
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

public partial class DbOnTheRocks : IDb, ITunableDb, IReadOnlyNativeKeyValueStore
{
    protected ILogger _logger;

    private string? _fullPath;

    private static readonly ConcurrentDictionary<string, RocksDb> _dbsByPath = new();

    private bool _isDisposing;
    private bool _isDisposed;

    private readonly ConcurrentHashSet<IWriteBatch> _currentBatches = new();

    internal readonly RocksDb _db;

    internal WriteOptions? WriteOptions { get; private set; }
    private WriteOptions? _noWalWrite;
    private WriteOptions? _lowPriorityAndNoWalWrite;
    private WriteOptions? _lowPriorityWriteOptions;

    private ReadOptions _defaultReadOptions = null!;
    private ReadOptions _hintCacheMissOptions = null!;
    internal ReadOptions? _readAheadReadOptions = null;

    internal DbOptions? DbOptions { get; private set; }

    public string Name { get; }

    private static long _maxRocksSize;

    private long _maxThisDbSize;

    private IntPtr? _rowCache = null;

    private readonly DbSettings _settings;

    private readonly PerTableDbConfig _perTableDbConfig;
    private ulong _maxBytesForLevelBase;
    private ulong _targetFileSizeBase;
    private int _minWriteBufferToMerge;

    private readonly IFileSystem _fileSystem;

    protected readonly RocksDbSharp.Native _rocksDbNative;

    private ITunableDb.TuneType _currentTune = ITunableDb.TuneType.Default;

    private string CorruptMarkerPath => Path.Join(_fullPath, "corrupt.marker");

    private readonly List<IDisposable> _metricsUpdaters = new();

    internal long _allocatedSpan = 0;
    private long _totalReads;
    private long _totalWrites;

    private readonly IteratorManager _iteratorManager;
    private ulong _writeBufferSize;
    private int _maxWriteBufferNumber;

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
        _iteratorManager = new IteratorManager(_db, null, _readAheadReadOptions);
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

                    ColumnFamilyOptions options = new();
                    BuildOptions(new PerTableDbConfig(dbConfig, _settings, columnFamily), options, sharedCache);

                    // "default" is a special column name with rocksdb, which is what previously not specifying column goes to
                    if (columnFamily == "Default") columnFamily = "default";
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

            if (_perTableDbConfig.EnableFileWarmer)
            {
                WarmupFile(_fullPath, db);
            }

            return db;
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
        catch (RocksDbSharpException x)
        {
            CreateMarkerIfCorrupt(x);
            throw;
        }

    }

    private void WarmupFile(string basePath, RocksDb db)
    {
        long availableMemory = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        _logger.Info($"Warming up database {Name} assuming {availableMemory} bytes of available memory");
        List<(FileMetadata metadata, DateTime creationTime)> fileMetadatas = new();

        foreach (LiveFileMetadata liveFileMetadata in db.GetLiveFilesMetadata())
        {
            string fullPath = Path.Join(basePath, liveFileMetadata.FileMetadata.FileName);
            try
            {
                DateTime creationTime = File.GetCreationTimeUtc(fullPath);
                fileMetadatas.Add((liveFileMetadata.FileMetadata, creationTime));
            }
            catch (IOException)
            {
                // Maybe the file is gone or something. We ignore it.
            }
        }

        fileMetadatas.Sort((item1, item2) =>
        {
            // Sort them by level so that lower level get priority
            int levelDiff = item1.metadata.FileLevel - item2.metadata.FileLevel;
            if (levelDiff != 0) return levelDiff;

            // Otherwise, we pick which file is newest.
            return item2.creationTime.CompareTo(item1.creationTime);
        });

        long totalSize = 0;
        fileMetadatas = fileMetadatas.TakeWhile(metadata =>
        {
            availableMemory -= (long)metadata.metadata.FileSize;
            bool take = availableMemory > 0;
            if (take)
            {
                totalSize += (long)metadata.metadata.FileSize;
            }
            return take;
        })
            // We reverse them again so that lower level goes last so that it is the freshest.
            // Not all of the available memory is actually available so we are probably over reading things.
            .Reverse()
            .ToList();

        long totalRead = 0;
        Parallel.ForEach(fileMetadatas, (task) =>
        {
            string fullPath = Path.Join(basePath, task.metadata.FileName);
            _logger.Info($"{(totalRead * 100 / (double)totalSize):00.00}% Warming up file {fullPath}");

            try
            {
                byte[] buffer = new byte[512.KiB()];
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

    private void CreateMarkerIfCorrupt(RocksDbSharpException rocksDbException)
    {
        if (rocksDbException.Message.Contains("Corruption:") || rocksDbException.Message.Contains("IO error"))
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
            if (!includeSharedCache)
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

    [GeneratedRegex("(?<optionName>[^; ]+)\\=(?<optionValue>[^; ]+);", RegexOptions.Singleline | RegexOptions.NonBacktracking | RegexOptions.ExplicitCapture)]
    private static partial Regex ExtractDbOptionsRegex();

    public static IDictionary<string, string> ExtractOptions(string dbOptions)
    {
        Dictionary<string, string> asDict = new();
        if (string.IsNullOrEmpty(dbOptions)) return asDict;

        foreach (Match match in ExtractDbOptionsRegex().Matches(dbOptions))
        {
            asDict[match.Groups["optionName"].ToString()] = match.Groups["optionValue"].ToString();
        }

        return asDict;
    }

    protected virtual void BuildOptions<T>(PerTableDbConfig dbConfig, Options<T> options, IntPtr? sharedCache) where T : Options<T>
    {
        // This section is about the table factory.. and block cache apparently.
        // This effect the format of the SST files and usually require resync to take effect.
        // Note: Keep in mind, the term 'index' here usually means mapping to a block, not to a value.
        #region TableFactory sections

        string allOptions = dbConfig.RocksDbOptions + dbConfig.AdditionalRocksDbOptions;
        IDictionary<string, string> optionsAsDict = ExtractOptions(allOptions);
        _targetFileSizeBase = ulong.Parse(optionsAsDict["target_file_size_base"]);
        _maxBytesForLevelBase = ulong.Parse(optionsAsDict["max_bytes_for_level_base"]);
        _minWriteBufferToMerge = int.Parse(optionsAsDict["min_write_buffer_number_to_merge"]);
        _writeBufferSize = ulong.Parse(optionsAsDict["write_buffer_size"]);
        _maxWriteBufferNumber = int.Parse(optionsAsDict["max_write_buffer_number"]);

        BlockBasedTableOptions tableOptions = new();
        options.SetBlockBasedTableFactory(tableOptions);
        IntPtr optsPtr = Marshal.StringToHGlobalAnsi(dbConfig.RocksDbOptions);
        try
        {
            _rocksDbNative.rocksdb_get_options_from_string(options.Handle, optsPtr, options.Handle);
        }
        finally
        {
            Marshal.FreeHGlobal(optsPtr);
        }

        ulong blockCacheSize = 0;
        if (optionsAsDict.TryGetValue("block_based_table_factory.block_cache", out string? blockCacheSizeStr))
        {
            blockCacheSize = ulong.Parse(blockCacheSizeStr);
        }

        if (sharedCache is not null && blockCacheSize == 0)
        {
            tableOptions.SetBlockCache(sharedCache.Value);
        }

        if (dbConfig.WriteBufferSize is not null)
        {
            _writeBufferSize = dbConfig.WriteBufferSize.Value;
            options.SetWriteBufferSize(dbConfig.WriteBufferSize.Value);
        }

        if (dbConfig.WriteBufferNumber is not null)
        {
            _maxWriteBufferNumber = (int)dbConfig.WriteBufferNumber.Value;
            options.SetMaxWriteBufferNumber(_maxWriteBufferNumber);
        }
        if (_maxWriteBufferNumber < 1) throw new InvalidConfigurationException($"Error initializing {Name} db. Max write buffer number must be more than 1. max write buffer number: {_maxWriteBufferNumber}", ExitCodes.GeneralError);

        #endregion

        #region WriteBuffer

        // Note: Write buffer and write buffer num are modified by MemoryHintMan.
        lock (_dbsByPath)
        {
            ulong writeBufferSize = _writeBufferSize;
            int writeBufferNumber = _maxWriteBufferNumber;
            _maxThisDbSize += (long)writeBufferSize * writeBufferNumber;
            Interlocked.Add(ref _maxRocksSize, _maxThisDbSize);
            if (_logger.IsDebug)
                _logger.Debug(
                    $"Expected max memory footprint of {Name} DB is {_maxThisDbSize / 1000 / 1000} MB ({writeBufferNumber} * {writeBufferSize / 1000 / 1000} MB + {blockCacheSize / 1000 / 1000} MB)");
            if (_logger.IsDebug) _logger.Debug($"Total max DB footprint so far is {_maxRocksSize / 1000 / 1000} MB");
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

        // VERY important to reduce stalls. Allow L0->L1 compaction to happen with multiple thread.
        _rocksDbNative.rocksdb_options_set_max_subcompactions(options.Handle, (uint)Environment.ProcessorCount);

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
            optsPtr = Marshal.StringToHGlobalAnsi(dbConfig.AdditionalRocksDbOptions);
            try
            {
                _rocksDbNative.rocksdb_get_options_from_string(options.Handle, optsPtr, options.Handle);
            }
            finally
            {
                Marshal.FreeHGlobal(optsPtr);
            }
        }

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
        _defaultReadOptions.SetVerifyChecksums(dbConfig.VerifyChecksum ?? true);

        _hintCacheMissOptions = new ReadOptions();
        _hintCacheMissOptions.SetVerifyChecksums(dbConfig.VerifyChecksum ?? true);
        _hintCacheMissOptions.SetFillCache(false);

        // When readahead flag is on, the next keys are expected to be after the current key. Increasing this value,
        // will increase the chances that the next keys will be in the cache, which reduces iops and latency. This
        // increases throughput, however, if a lot of the keys are not close to the current key, it will increase read
        // bandwidth requirement, since each read must be at least this size. This value is tuned for a batched trie
        // visitor on mainnet with 4GB memory budget and 4Gbps read bandwidth.
        if (dbConfig.ReadAheadSize != 0)
        {
            _readAheadReadOptions = new ReadOptions();
            _readAheadReadOptions.SetVerifyChecksums(dbConfig.VerifyChecksum ?? true);
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
        return GetWithColumnFamily(key, null, _iteratorManager, flags);
    }

    internal byte[]? GetWithColumnFamily(ReadOnlySpan<byte> key, ColumnFamilyHandle? cf, IteratorManager iteratorManager, ReadFlags flags = ReadFlags.None)
    {
        ObjectDisposedException.ThrowIf(_isDisposing, this);

        UpdateReadMetrics();

        try
        {
            if (_readAheadReadOptions is not null && (flags & ReadFlags.HintReadAhead) != 0)
            {
                byte[]? result = GetWithIterator(key, cf, iteratorManager, flags, out bool success);
                if (success)
                {
                    return result;
                }
            }

            return Get(key, cf, flags);
        }
        catch (RocksDbSharpException e)
        {
            CreateMarkerIfCorrupt(e);
            throw;
        }
    }

    private unsafe byte[]? GetWithIterator(ReadOnlySpan<byte> key, ColumnFamilyHandle? cf, IteratorManager iteratorManager, ReadFlags flags, out bool success)
    {
        success = true;

        using IteratorManager.RentWrapper wrapper = iteratorManager.Rent(flags);
        Iterator iterator = wrapper.Iterator;

        if (iterator.Valid() && TryCloseReadAhead(iterator, key, out byte[]? closeRes))
        {
            return closeRes;
        }

        iterator.Seek(key);
        if (iterator.Valid() && Bytes.AreEqual(iterator.GetKeySpan(), key))
        {
            return iterator.Value();
        }

        success = false;
        return null;
    }

    private unsafe byte[]? Get(ReadOnlySpan<byte> key, ColumnFamilyHandle? cf, ReadFlags flags)
    {
        // TODO: update when merged upstream: https://github.com/curiosity-ai/rocksdb-sharp/pull/61
        // return _db.Get(key, cf, (flags & ReadFlags.HintCacheMiss) != 0 ? _hintCacheMissOptions : _defaultReadOptions);

        nint db = _db.Handle;
        nint read_options = ((flags & ReadFlags.HintCacheMiss) != 0 ? _hintCacheMissOptions : _defaultReadOptions).Handle;
        UIntPtr skLength = (UIntPtr)key.Length;
        IntPtr handle;
        IntPtr errPtr;
        fixed (byte* ptr = &MemoryMarshal.GetReference(key))
        {
            handle = cf is null
                        ? Native.Instance.rocksdb_get_pinned(db, read_options, ptr, skLength, out errPtr)
                        : Native.Instance.rocksdb_get_pinned_cf(db, read_options, cf.Handle, ptr, skLength, out errPtr);
        }

        if (errPtr != IntPtr.Zero) ThrowRocksDbException(errPtr);
        if (handle == IntPtr.Zero) return null;

        try
        {
            IntPtr valuePtr = Native.Instance.rocksdb_pinnableslice_value(handle, out UIntPtr valueLength);
            if (valuePtr == IntPtr.Zero)
            {
                return null;
            }

            int length = (int)valueLength;
            byte[] result = new byte[length];
            new ReadOnlySpan<byte>((void*)valuePtr, length).CopyTo(new Span<byte>(result));
            return result;
        }
        finally
        {
            Native.Instance.rocksdb_pinnableslice_destroy(handle);
        }

        [DoesNotReturn]
        [StackTraceHidden]
        static unsafe void ThrowRocksDbException(nint errPtr)
        {
            throw new RocksDbException(errPtr);
        }
    }

    /// <summary>
    /// iterator.Next() is about 10 to 20 times faster than iterator.Seek().
    /// Here we attempt to do that first. To prevent futile attempt some logic is added to approximately detect
    /// if the requested key is too far from the current key and skip this entirely.
    /// </summary>
    /// <param name="iterator"></param>
    /// <param name="key"></param>
    /// <returns></returns>
    private bool TryCloseReadAhead(Iterator iterator, ReadOnlySpan<byte> key, out byte[]? result)
    {
        // Probably hash db. Can't really do this with hashdb. Even with batched trie visitor, its going to skip a lot.
        if (key.Length <= 32)
        {
            result = null;
            return false;
        }

        iterator.Next();
        ReadOnlySpan<byte> currentKey = iterator.GetKeySpan();
        int compareResult = currentKey.SequenceCompareTo(key);
        if (compareResult == 0)
        {
            result = iterator.Value();
            return true; // This happens A LOT.
        }

        result = null;
        if (compareResult > 0)
        {
            return false;
        }

        // This happens, 0.5% of the time.
        // This is only useful for state as storage have way too different different address range between different
        // contract. That said, there isn't any real good threshold. Threshold is for some reasonably high value
        // above the average distance.
        ulong currentKeyInt = BinaryPrimitives.ReadUInt64BigEndian(currentKey);
        ulong requestedKeyInt = BinaryPrimitives.ReadUInt64BigEndian(key);
        ulong distance = requestedKeyInt - currentKeyInt;
        if (distance > 1_000_000_000)
        {
            return false;
        }

        for (int i = 0; i < 5 && compareResult < 0; i++)
        {
            iterator.Next();
            compareResult = iterator.GetKeySpan().SequenceCompareTo(key);
        }

        if (compareResult == 0)
        {
            result = iterator.Value();
            return true;
        }

        if (compareResult > 0)
        {
            // We've skipped it somehow
            result = null;
            return true;
        }

        return false;
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

    public Span<byte> GetSpan(scoped ReadOnlySpan<byte> key, ReadFlags flags)
    {
        return GetSpanWithColumnFamily(key, null, flags);
    }

    internal Span<byte> GetSpanWithColumnFamily(scoped ReadOnlySpan<byte> key, ColumnFamilyHandle? cf, ReadFlags flags)
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

    public ReadOnlySpan<byte> GetNativeSlice(scoped ReadOnlySpan<byte> key, out IntPtr handle, ReadFlags flags)
        => GetNativeSlice(key, null, out handle, flags);

    public unsafe ReadOnlySpan<byte> GetNativeSlice(scoped ReadOnlySpan<byte> key, ColumnFamilyHandle? cf, out IntPtr handle, ReadFlags flags)
    {
        // TODO: update when merged upstream: https://github.com/curiosity-ai/rocksdb-sharp/pull/61
        // return _db.Get(key, cf, (flags & ReadFlags.HintCacheMiss) != 0 ? _hintCacheMissOptions : _defaultReadOptions);

        handle = default;
        nint db = _db.Handle;
        nint read_options = ((flags & ReadFlags.HintCacheMiss) != 0 ? _hintCacheMissOptions : _defaultReadOptions).Handle;
        UIntPtr skLength = (UIntPtr)key.Length;
        IntPtr errPtr;
        IntPtr slice;
        fixed (byte* ptr = &MemoryMarshal.GetReference(key))
        {
            slice = cf is null
                        ? Native.Instance.rocksdb_get_pinned(db, read_options, ptr, skLength, out errPtr)
                        : Native.Instance.rocksdb_get_pinned_cf(db, read_options, cf.Handle, ptr, skLength, out errPtr);
        }

        if (errPtr != IntPtr.Zero) ThrowRocksDbException(errPtr);
        if (slice == IntPtr.Zero) return null;

        try
        {
            IntPtr valuePtr = Native.Instance.rocksdb_pinnableslice_value(slice, out UIntPtr valueLength);
            if (valuePtr == IntPtr.Zero)
            {
                Native.Instance.rocksdb_pinnableslice_destroy(slice);
                return null;
            }

            int length = (int)valueLength;
            handle = slice;
            return new ReadOnlySpan<byte>((void*)valuePtr, length);
        }
        catch
        {
            Native.Instance.rocksdb_pinnableslice_destroy(slice);
            throw;
        }

        [DoesNotReturn]
        [StackTraceHidden]
        static unsafe void ThrowRocksDbException(nint errPtr)
        {
            throw new RocksDbException(errPtr);
        }
    }

    public void DangerousReleaseHandle(IntPtr handle)
    {
        if (handle != default)
            Native.Instance.rocksdb_pinnableslice_destroy(handle);
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
        ObjectDisposedException.ThrowIf(_isDisposed, this);

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
        private const int MaxWritesOnNoWal = 256;
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

    public void Flush(bool onlyWal = false)
    {
        ObjectDisposedException.ThrowIf(_isDisposing, this);

        InnerFlush(onlyWal);
    }

    public virtual void Compact()
    {
        _db.CompactRange(Keccak.Zero.BytesToArray(), Keccak.MaxValue.BytesToArray());
    }

    private void InnerFlush(bool onlyWal)
    {
        try
        {
            _rocksDbNative.rocksdb_flush_wal(_db.Handle, true);

            if (!onlyWal)
            {
                _rocksDbNative.rocksdb_flush(_db.Handle, FlushOptions.DefaultFlushOptions.Handle);
            }
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

        _iteratorManager.Dispose();
        _db.Dispose();

        if (_rowCache.HasValue)
        {
            _rocksDbNative.rocksdb_cache_destroy(_rowCache.Value);
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

        if (_perTableDbConfig.FlushOnExit) InnerFlush(false);
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
                ApplyOptions(GetHeavyWriteOptions(_maxBytesForLevelBase));
                break;
            case ITunableDb.TuneType.HeavyWrite:
                // Compaction spikes are clear at this point. Will definitely affect attestation performance.
                // Its unclear if it improve or slow down sync time. Seems to be the sweet spot.
                ApplyOptions(GetHeavyWriteOptions((ulong)2.GiB()));
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
            case ITunableDb.TuneType.HashDb:
                ApplyOptions(GetHashDbOptions());
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
            { "write_buffer_size", _writeBufferSize.ToString() },
            { "max_write_buffer_number", _maxWriteBufferNumber.ToString() },

            { "level0_file_num_compaction_trigger", 4.ToString() },
            { "level0_slowdown_writes_trigger", 20.ToString() },

            // Very high, so that after moving from HeavyWrite, we don't immediately hang.
            // This does means that under very rare case, the l0 file can accumulate, which slow down the db
            // until they get compacted.
            { "level0_stop_writes_trigger", 1024.ToString() },

            { "max_bytes_for_level_base", _maxBytesForLevelBase.ToString() },
            { "target_file_size_base", _targetFileSizeBase.ToString() },
            { "disable_auto_compactions", "false" },

            { "enable_blob_files", "false" },

            { "soft_pending_compaction_bytes_limit", 64.GiB().ToString() },
            { "hard_pending_compaction_bytes_limit", 256.GiB().ToString() },
        };
    }

    private IDictionary<string, string> GetHashDbOptions()
    {
        return new Dictionary<string, string>()
        {
            // Some database config is slightly faster on hash db database. These are applied when hash db is detected
            // to prevent unexpected regression.
            { "table_factory.block_size", "4096" },
            { "table_factory.block_restart_interval", "16" },
            { "compression", "kSnappyCompression" },
            { "max_bytes_for_level_multiplier", "10" },
            { "max_bytes_for_level_base", "256000000" },
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
        // bufferSize*maxBufferNumber = 16MB*Core count, which is the max memory used, which tend to be the case as its now
        // stalled by compaction instead of flush.
        // The buffer is not compressed unlike l0File, so to account for it, its size need to be slightly larger.
        ulong targetFileSize = (ulong)16.MiB();
        ulong bufferSize = (ulong)(targetFileSize / _perTableDbConfig.CompressibilityHint);
        ulong l0FileSize = targetFileSize * (ulong)_minWriteBufferToMerge;
        ulong maxBufferNumber = (ulong)Environment.ProcessorCount;

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

    /// <summary>
    /// Iterators should not be kept for long as it will pin some memory block and sst file. This would show up as
    /// temporary higher disk usage or memory usage.
    ///
    /// This class handles a periodic timer which periodically dispose all iterator.
    /// </summary>
    internal class IteratorManager : IDisposable
    {
        private readonly ManagedIterators _readaheadIterators = new();
        private readonly ManagedIterators _readaheadIterators2 = new();
        private readonly ManagedIterators _readaheadIterators3 = new();
        private readonly RocksDb _rocksDb;
        private readonly ColumnFamilyHandle? _cf;
        private readonly ReadOptions? _readOptions;
        private readonly Timer _timer;
        private bool _isDisposed;

        // This is about once every two second maybe at max throughput.
        private const int IteratorUsageLimit = 1000000;

        public IteratorManager(RocksDb rocksDb, ColumnFamilyHandle? cf, ReadOptions? readOptions)
        {
            _rocksDb = rocksDb;
            _cf = cf;
            _readOptions = readOptions;

            _timer = new Timer(OnTimer, null, TimeSpan.Zero, TimeSpan.FromSeconds(10));
        }

        private void OnTimer(object? state)
        {
            if (_isDisposed) return;
            _readaheadIterators.ClearIterators();
            _readaheadIterators2.ClearIterators();
            _readaheadIterators3.ClearIterators();
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;
            _timer.Dispose();
            _readaheadIterators.DisposeAll();
            _readaheadIterators2.DisposeAll();
            _readaheadIterators3.DisposeAll();
        }

        public RentWrapper Rent(ReadFlags flags)
        {

            ManagedIterators iterators = _readaheadIterators;
            if ((flags & ReadFlags.HintReadAhead2) != 0)
            {
                iterators = _readaheadIterators2;
            }
            else if ((flags & ReadFlags.HintReadAhead3) != 0)
            {
                iterators = _readaheadIterators3;
            }

            IteratorHolder holder = iterators.Value!;
            // If null, we create a new one.
            Iterator? iterator = Interlocked.Exchange(ref holder.Iterator, null);
            return new RentWrapper(iterator ?? _rocksDb.NewIterator(_cf, _readOptions), flags, this);
        }

        private void Return(Iterator iterator, ReadFlags flags)
        {
            ManagedIterators iterators = _readaheadIterators;
            if ((flags & ReadFlags.HintReadAhead2) != 0)
            {
                iterators = _readaheadIterators2;
            }
            else if ((flags & ReadFlags.HintReadAhead3) != 0)
            {
                iterators = _readaheadIterators3;
            }

            IteratorHolder holder = iterators.Value!;

            // We don't keep using the same iterator for too long.
            if (holder.Usage > IteratorUsageLimit)
            {
                iterator.Dispose();
                holder.Usage = 0;
                return;
            }

            holder.Usage++;

            Iterator? oldIterator = Interlocked.Exchange(ref holder.Iterator, iterator);
            // Well... this is weird. I'll just dispose it.
            oldIterator?.Dispose();
        }

        public readonly struct RentWrapper(Iterator iterator, ReadFlags flags, IteratorManager manager) : IDisposable
        {
            public Iterator Iterator => iterator;

            public void Dispose()
            {
                manager.Return(iterator, flags);
            }
        }

        // Note: use of threadlocal is very important as the seek forward is fast, but the seek backward is not fast.
        private sealed class ManagedIterators : ThreadLocal<IteratorHolder>
        {
            private bool _disposed = false;

            public ManagedIterators() : base(static () => new IteratorHolder(), trackAllValues: true)
            {
            }

            public void ClearIterators()
            {
                if (_disposed) return;
                if (Values is null) return;
                foreach (IteratorHolder iterator in Values)
                {
                    iterator.Dispose();
                }
            }

            public void DisposeAll()
            {
                ClearIterators();
                Dispose();
            }

            protected override void Dispose(bool disposing)
            {
                // Note: This is called from finalizer thread, so we can't use foreach to dispose all values
                Value?.Dispose();
                Value = null!;
                _disposed = true;
                base.Dispose(disposing);
            }
        }

        private class IteratorHolder : IDisposable
        {
            public Iterator? Iterator = null;
            public int Usage = 0;

            public void Dispose()
            {
                Interlocked.Exchange(ref Iterator, null)?.Dispose();
            }
        }
    }
}
