// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Abstractions;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ConcurrentCollections;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Core.Extensions;
using Nethermind.Core.Threading;
using Nethermind.Db.Rocks.Config;
using Nethermind.Logging;
using Nethermind.RocksDbBindings;
using IWriteBatch = Nethermind.Core.IWriteBatch;

namespace Nethermind.Db.Rocks;

public partial class DbOnTheRocks : IDb, ITunableDb, IReadOnlyNativeKeyValueStore, ISnapshotSortedKeyValueStore, IMergeableKeyValueStore, IKeyValueStoreWithSnapshot, IRangeRemovableKeyValueStore, IBatchReadOnlyDb
{
    protected ILogger _logger;

    private static readonly FlushOptions _defaultFlushOptions = new();

    private bool _isDisposing;
    private int _pinnedSlices;
    private readonly InFlightOperations _prefetches = new();
    private bool _nativeCloseDeferred;
    private int _nativeClosed;
    private bool _isDisposed;

    private readonly ConcurrentHashSet<IWriteBatch> _currentBatches = [];

    private readonly RocksDbInstance _instance;
    internal readonly RocksDb _db;
    internal readonly IColumnFamilyHandle? _columnFamily;

    internal WriteOptions WriteOptions { get; }
    private readonly WriteOptions _noWalWrite;
    private readonly WriteOptions _lowPriorityAndNoWalWrite;
    private readonly WriteOptions _lowPriorityWriteOptions;

    private readonly ReadOptions _defaultReadOptions;
    private readonly ReadOptions _hintCacheMissOptions;
    // Block-cache read tier: a read that needs a file fails as incomplete instead of reading it.
    private readonly ReadOptions _cacheOnlyReadOptions;
    private const int BlockCacheTier = 1;
    private readonly ReadOptions? _readAheadReadOptions;
    private readonly ReadOptions _seekReadOptions;

    public string Name { get; }

    private readonly IRocksDbConfig _perTableDbConfig;
    internal bool VerifyChecksum { get; }
    internal ulong ReadAheadSize { get; }
    private readonly ulong _maxBytesForLevelBase;
    private readonly ulong _targetFileSizeBase;
    private readonly int _minWriteBufferToMerge;
    private readonly int _prefixExtractorLength;

    private ITunableDb.TuneType _currentTune = ITunableDb.TuneType.Default;

    public bool WasRepairedOnOpen => _instance.WasRepairedOnOpen;

    // Striped: every concurrent reader (RPC workers, prewarm workers) updates these per Get, and
    // a single shared word per DB serializes them under load.
    internal readonly StripedLong _allocatedSpan = new();
    private readonly StripedLong _totalReads = new();
    private CacheLinePaddedLong _totalWrites;

    private readonly DisposableLazy<IteratorManager>? _iteratorManager;
    private readonly DisposableLazy<IteratorManager> _seekIteratorManager;
    private readonly ulong _writeBufferSize;
    private readonly int _maxWriteBufferNumber;
    private readonly RocksDbReader _reader;

    /// <param name="instance">The open database. The store holds its own lease on it until the store is disposed.</param>
    /// <param name="columnFamily">The column family the store reads and writes, or <see langword="null"/> for the default one.</param>
    /// <param name="name">The name of the store.</param>
    /// <param name="config">The configuration the column family, or the database, was opened with.</param>
    /// <param name="logManager">The log manager.</param>
    public DbOnTheRocks(RocksDbInstance instance, IColumnFamilyHandle? columnFamily, string name, IRocksDbConfig config, ILogManager logManager)
    {
        _logger = logManager.GetClassLogger<DbOnTheRocks>();
        _instance = instance;
        _db = instance.Db;
        _columnFamily = columnFamily;
        Name = name;
        _perTableDbConfig = config;
        VerifyChecksum = config.VerifyChecksum ?? true;
        ReadAheadSize = config.ReadAheadSize ?? 256UL.KiB;

        IDictionary<string, string> optionsAsDict = ExtractOptions(config.RocksDbOptions + config.AdditionalRocksDbOptions);
        _targetFileSizeBase = ulong.Parse(optionsAsDict["target_file_size_base"]);
        _maxBytesForLevelBase = ulong.Parse(optionsAsDict["max_bytes_for_level_base"]);
        _minWriteBufferToMerge = int.Parse(optionsAsDict["min_write_buffer_number_to_merge"]);
        _writeBufferSize = config.WriteBufferSize > 0 ? config.WriteBufferSize.Value : ulong.Parse(optionsAsDict["write_buffer_size"]);
        _maxWriteBufferNumber = config.WriteBufferNumber is not null ? (int)config.WriteBufferNumber.Value : int.Parse(optionsAsDict["max_write_buffer_number"]);
        _prefixExtractorLength = ParsePrefixExtractorLength(optionsAsDict);

        WriteOptions = CreateWriteOptions(config);

        _noWalWrite = CreateWriteOptions(config);
        _noWalWrite.SetDisableWal(true);

        _lowPriorityWriteOptions = CreateWriteOptions(config);
        _lowPriorityWriteOptions.SetLowPriority(true);

        _lowPriorityAndNoWalWrite = CreateWriteOptions(config);
        _lowPriorityAndNoWalWrite.SetDisableWal(true);
        _lowPriorityAndNoWalWrite.SetLowPriority(true);

        _defaultReadOptions = CreateReadOptions();

        _hintCacheMissOptions = CreateReadOptions();
        _hintCacheMissOptions.SetFillCache(false);

        _cacheOnlyReadOptions = CreateReadOptions();
        _cacheOnlyReadOptions.SetReadTier(BlockCacheTier);

        // When a readahead flag is on, the next keys are expected to be after the current key. Increasing this value
        // will increase the chances that the next keys will be in the cache, which reduces iops and latency. This
        // increases throughput, however, if a lot of the keys are not close to the current key, it will increase read
        // bandwidth requirement, since each read must be at least this size. This value is tuned for a batched trie
        // visitor on mainnet with 4GB memory budget and 4Gbps read bandwidth.
        if (config.ReadAheadSize != 0)
        {
            _readAheadReadOptions = CreateReadOptions();
            _readAheadReadOptions.SetReadaheadSize(config.ReadAheadSize ?? 256UL.KiB);
            _readAheadReadOptions.SetTailing(true);
        }

        // A pooled seek iterator outlives the read that rented it, so it must see writes made after its creation.
        // Tailing gives that, at the cost of re-seeking every immutable child whenever a seek goes backwards.
        // They also don't support SeekForPrev and SeekToLast
        _seekReadOptions = CreateReadOptions();
        _seekReadOptions.SetTailing(true);

        _iteratorManager = CreateLazyReadAheadIteratorManager(columnFamily);
        _seekIteratorManager = CreateLazySeekIteratorManager(columnFamily);

        _reader = new RocksDbReader(this, CreateReadOptions, _iteratorManager, columnFamily);

        instance.AcquireLease();
    }

    /// <summary>The database, and for a column also the column, as log messages name it.</summary>
    private string LogName => _columnFamily is null ? Name : $"{_instance.Name}.{Name}";

    internal void HandleFatalDbError(RocksDbException rocksDbException) => _instance.HandleFatalDbError(rocksDbException);

    public void AcknowledgeRepair() => _instance.AcknowledgeRepair();

    protected internal void UpdateReadMetrics() => _totalReads.Increment();

    protected internal void UpdateReadMetrics(int count) => _totalReads.Add(count);

    protected internal void UpdateWriteMetrics() => Interlocked.Increment(ref _totalWrites.Value);

    private long FetchTotalPropertyValue(string propertyName) =>
        (_columnFamily is null ? _db.TryGetIntProperty(propertyName, out ulong value) : _db.TryGetIntProperty(propertyName, _columnFamily, out value)) ? (long)value : 0;

    public IDbMeta.DbMetric GatherMetric()
    {
        if (_isDisposed)
        {
            return new IDbMeta.DbMetric()
            {
                Size = 0,
                CacheSize = 0,
                IndexSize = 0,
                MemtableSize = 0,
                TotalReads = _totalReads.Sum,
                TotalWrites = _totalWrites.Value,
            };
        }
        return new IDbMeta.DbMetric()
        {
            Size = GetSize(),
            CacheSize = GetCacheSize(),
            IndexSize = GetIndexSize(),
            MemtableSize = GetMemtableSize(),
            TotalReads = _totalReads.Sum,
            TotalWrites = _totalWrites.Value,
        };
    }

    public long EstimatedCount
    {
        get
        {
            if (_isDisposed)
            {
                return 0;
            }

            try
            {
                return FetchTotalPropertyValue("rocksdb.estimate-num-keys");
            }
            catch (RocksDbException e)
            {
                if (_logger.IsWarn)
                    _logger.Warn($"Failed to read DB key count estimate {e.Message}");
            }

            return 0;
        }
    }

    private long GetSize()
    {
        try
        {
            long sstSize = FetchTotalPropertyValue("rocksdb.total-sst-files-size");
            long blobSize = FetchTotalPropertyValue("rocksdb.total-blob-file-size");
            return sstSize + blobSize;
        }
        catch (RocksDbException e)
        {
            if (_logger.IsWarn)
                _logger.Warn($"Failed to update DB size metrics {e.Message}");
        }

        return 0;
    }

    private long GetCacheSize()
    {
        try
        {
            if (_instance.UsesSharedBlockCache)
            {
                // returning 0 as we are using shared cache.
                return 0;
            }
            return FetchTotalPropertyValue("rocksdb.block-cache-usage");
        }
        catch (RocksDbException e)
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
        catch (RocksDbException e)
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
        catch (RocksDbException e)
        {
            if (_logger.IsWarn)
                _logger.Warn($"Failed to update DB size metrics {e.Message}");
        }

        return 0;
    }

    [GeneratedRegex("(?<optionName>[A-Za-z0-9_\\.]+)\\=(?<optionValue>[^; ]+);", RegexOptions.Singleline | RegexOptions.NonBacktracking | RegexOptions.ExplicitCapture)]
    private static partial Regex ExtractDbOptionsRegex();

    public static IDictionary<string, string> ExtractOptions(string dbOptions)
    {
        Dictionary<string, string> asDict = [];
        if (string.IsNullOrEmpty(dbOptions)) return asDict;

        foreach (Match match in ExtractDbOptionsRegex().Matches(dbOptions))
        {
            asDict[match.Groups["optionName"].ToString()] = match.Groups["optionValue"].ToString();
        }

        return asDict;
    }

    private static WriteOptions CreateWriteOptions(IRocksDbConfig dbConfig)
    {
        WriteOptions options = new();
        // a potential fix for corruption on hard process termination may cause performance degradation
        options.SetSync(dbConfig.WriteAheadLogSync);
        return options;
    }

    internal ReadOptions CreateReadOptions()
    {
        ReadOptions readOptions = new();
        readOptions.SetVerifyChecksums(VerifyChecksum);
        return readOptions;
    }

    byte[]? IReadOnlyKeyValueStore.Get(ReadOnlySpan<byte> key, ReadFlags flags) => _reader.Get(key, flags);

    Span<byte> IReadOnlyKeyValueStore.GetSpan(scoped ReadOnlySpan<byte> key, ReadFlags flags) => _reader.GetSpan(key, flags);

    MemoryManager<byte>? IReadOnlyKeyValueStore.GetOwnedMemory(ReadOnlySpan<byte> key, ReadFlags flags)
    {
        Span<byte> span = ((IReadOnlyKeyValueStore)this).GetSpan(key, flags);
        return span.IsNullOrEmpty() ? null : new DbSpanMemoryManager(this, span);
    }

    int IReadOnlyKeyValueStore.Get(scoped ReadOnlySpan<byte> key, Span<byte> output, ReadFlags flags) => _reader.Get(key, output, flags);

    bool IReadOnlyKeyValueStore.KeyExists(ReadOnlySpan<byte> key) => _reader.KeyExists(key);

    void IReadOnlyKeyValueStore.DangerousReleaseMemory(in ReadOnlySpan<byte> span) => _reader.DangerousReleaseMemory(span);

    internal byte[]? GetWithIterator(ReadOnlySpan<byte> key, IteratorManager iteratorManager, ReadFlags flags, out bool success)
    {
        ThrowIfDisposing();

        success = true;

        using IteratorManager.RentWrapper wrapper = iteratorManager.Rent(flags);
        Iterator iterator = wrapper.Iterator;

        if (iterator.Valid() && TryCloseReadAhead(iterator, key, iteratorManager.SequentialKeys, out byte[]? closeRes))
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

    // Uses a pool of tailing seek iterators: each one reads a live sequence,
    // so write-batch atomicity is not guaranteed - dirty or phantom reads are possible
    internal static bool TryGetCeilingWithIterator(
        scoped ReadOnlySpan<byte> lowerBoundIncl, scoped ReadOnlySpan<byte> upperBoundExcl, IteratorManager iteratorManager,
        Span<byte> keyBuffer, out int keyLength, Span<byte> valueBuffer, out int valueLength)
    {
        keyLength = 0;
        valueLength = 0;

        using IteratorManager.RentWrapper wrapper = iteratorManager.Rent(ReadFlags.None);
        Iterator iterator = wrapper.Iterator;

        iterator.Seek(lowerBoundIncl);
        if (!iterator.Valid()) return false;

        // pooled iterator's read options are fixed at creation, so upper bound is a span compare instead of a native iterator bound
        ReadOnlySpan<byte> key = iterator.GetKeySpan();
        if (key.SequenceCompareTo(upperBoundExcl) >= 0) return false;

        ReadOnlySpan<byte> value = iterator.GetValueSpan();
        keyLength = key.Length;
        valueLength = value.Length;
        key.TryCopyTo(keyBuffer);
        value.TryCopyTo(valueBuffer);
        return true;
    }

    /// <summary> Pool for calls with <see cref="ReadFlags.HintReadAhead"/> - tailing iterators with large read steps. </summary>
    internal DisposableLazy<IteratorManager>? CreateLazyReadAheadIteratorManager(IColumnFamilyHandle? cf) =>
        _readAheadReadOptions is null ? null : CreateLazyIteratorManager(cf, _readAheadReadOptions);

    /// <summary> Pool for ceiling seeks - tailing iterators. </summary>
    internal DisposableLazy<IteratorManager> CreateLazySeekIteratorManager(IColumnFamilyHandle? cf) =>
        CreateLazyIteratorManager(cf, _seekReadOptions);

    private DisposableLazy<IteratorManager> CreateLazyIteratorManager(IColumnFamilyHandle? cf, ReadOptions readOptions) =>
        new(() => new IteratorManager(_db, cf, readOptions));

    internal byte[]? Get(ReadOnlySpan<byte> key, IColumnFamilyHandle? cf, ReadOptions readOptions)
    {
        try
        {
            return _db.Get(key, cf, readOptions);
        }
        catch (RocksDbException e)
        {
            HandleFatalDbError(e);
            throw;
        }
    }

    /// <summary>
    /// iterator.Next() is about 10 to 20 times faster than iterator.Seek().
    /// Here we attempt to do that first. To prevent futile attempt some logic is added to approximately detect
    /// if the requested key is too far from the current key and skip this entirely.
    /// </summary>
    /// <param name="iterator"></param>
    /// <param name="key"></param>
    /// <param name="result"></param>
    /// <returns></returns>
    private static bool TryCloseReadAhead(Iterator iterator, ReadOnlySpan<byte> key, bool sequentialKeys, out byte[]? result)
    {
        // Probably hash db. Can't really do this with hashdb. Even with batched trie visitor, its going to skip a lot.
        if (!sequentialKeys && key.Length <= 32)
        {
            result = null;
            return false;
        }

        iterator.Next();
        // Next() can exhaust the iterator; key()/Next() on an invalid iterator is undefined per the RocksDB API.
        if (!iterator.Valid())
        {
            result = null;
            return false;
        }

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
        // Skipped for short keys (e.g. 3-byte StateTopNodes keys): the bounded probe below is enough there.
        if (currentKey.Length >= sizeof(ulong) && key.Length >= sizeof(ulong))
        {
            ulong currentKeyInt = BinaryPrimitives.ReadUInt64BigEndian(currentKey);
            ulong requestedKeyInt = BinaryPrimitives.ReadUInt64BigEndian(key);
            ulong distance = requestedKeyInt - currentKeyInt;
            if (distance > 1_000_000_000)
            {
                return false;
            }
        }

        for (int i = 0; i < 5 && compareResult < 0; i++)
        {
            iterator.Next();
            if (!iterator.Valid())
            {
                return false;
            }
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

    public void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None) =>
        SetWithColumnFamily(key, _columnFamily, value, flags);

    internal void SetWithColumnFamily(ReadOnlySpan<byte> key, IColumnFamilyHandle? cf, ReadOnlySpan<byte> value, WriteFlags flags = WriteFlags.None)
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
        catch (RocksDbException e)
        {
            HandleFatalDbError(e);
            throw;
        }
    }

    public WriteOptions? WriteFlagsToWriteOptions(WriteFlags flags) => flags switch
    {
        _ when (flags & WriteFlags.LowPriorityAndNoWAL) == WriteFlags.LowPriorityAndNoWAL => _lowPriorityAndNoWalWrite,
        _ when (flags & WriteFlags.DisableWAL) == WriteFlags.DisableWAL => _noWalWrite,
        _ when (flags & WriteFlags.LowPriority) == WriteFlags.LowPriority => _lowPriorityWriteOptions,
        _ => WriteOptions
    };


    public KeyValuePair<byte[], byte[]?>[] this[byte[][] keys]
    {
        get
        {
            ThrowIfDisposing();
            UpdateReadMetrics(keys.Length);

            try
            {
                if (_columnFamily is null) return _db.MultiGet(keys);

                IColumnFamilyHandle[] columnFamilies = new IColumnFamilyHandle[keys.Length];
                Array.Fill(columnFamilies, _columnFamily);
                return _db.MultiGet(keys, columnFamilies);
            }
            catch (RocksDbException e)
            {
                HandleFatalDbError(e);
                throw;
            }
        }
    }

    internal Span<byte> GetSpanWithColumnFamily(scoped ReadOnlySpan<byte> key, IColumnFamilyHandle? cf, ReadOptions readOptions)
    {
        ObjectDisposedException.ThrowIf(_isDisposing, this);

        UpdateReadMetrics();

        try
        {
            Span<byte> span = _db.GetSpan(key, cf, readOptions);

            if (!span.IsNullOrEmpty())
            {
                _allocatedSpan.Increment();
                // Pressure hints exist so the GC accounts for sizeable native memory held alive by
                // managed wrappers. Sub-threshold spans are transient (released within the request)
                // and each Add/Remove pair mutates GC-global accounting — a contended cost per DB
                // read under concurrent load. The threshold must match DangerousReleaseMemory.
                if (span.Length >= GcPressureSpanThreshold) GC.AddMemoryPressure(span.Length);
            }
            return span;
        }
        catch (RocksDbException e)
        {
            HandleFatalDbError(e);
            throw;
        }
    }

    /// <summary>
    /// Starts one native asynchronous MultiGet for fixed-length keys so that their blocks are in the block cache
    /// before the scalar reads that need them. The values are discarded.
    /// </summary>
    /// <remarks>
    /// The native batch overlaps its disk reads, which scalar reads on one thread cannot. A failed prefetch is only
    /// logged: the scalar read that follows performs and reports the read itself. <see cref="Dispose"/> refuses new
    /// prefetches and waits for the ones in flight before it closes the database.
    /// </remarks>
    internal void Prefetch(ReadOnlySpan<byte> keys, int keyLength, IColumnFamilyHandle? cf, ReadOptions readOptions)
    {
        if (keyLength <= 0 || keys.Length < keyLength || !_prefetches.TryBegin()) return;
        bool pending = false;
        int count = keys.Length / keyLength;
        IColumnFamilyHandle[]? families = null;
        try
        {
            // The binding copies the keys and handles before it returns, so this thread's arrays serve the next batch.
            byte[][] keyArrays = PrefetchBuffers.Keys(keyLength, count);
            families = cf is null ? null : PrefetchBuffers.Families(count);
            for (int i = 0; i < count; i++) keys.Slice(i * keyLength, keyLength).CopyTo(keyArrays[i]);
            if (families is not null) Array.Fill(families, cf);
            Task read = _db.MultiGetAsync(keyArrays, families, readOptions).AsTask();
            Db.Metrics.IncrementPrefetchedKeys(count);
            if (read.IsCompleted)
            {
                if (read.IsFaulted) LogPrefetchFailure(read.Exception!);
            }
            else
            {
                _ = read.ContinueWith(static (t, state) => ((DbOnTheRocks)state!).EndPrefetch(t), this,
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                pending = true;
            }
        }
        catch (Exception e) when (e is RocksDbException or ObjectDisposedException)
        {
            LogPrefetchFailure(e);
        }
        finally
        {
            // A reused array must not keep a disposed database's column family reachable.
            if (families is not null) Array.Clear(families);
            if (!pending) _prefetches.End();
        }
    }

    private void EndPrefetch(Task read)
    {
        if (read.IsFaulted) LogPrefetchFailure(read.Exception!);
        _prefetches.End();
    }

    private void LogPrefetchFailure(Exception e)
    {
        if (_logger.IsDebug) _logger.Debug($"DB {LogName} prefetch failed: {e.Message}");
    }

    /// <summary>The prefetch reads that have started and not completed.</summary>
    internal int PrefetchesInFlight => _prefetches.Count;

    /// <summary>
    /// Per-thread key and column family arrays for <see cref="Prefetch"/>, one exact-length array per batch size, so
    /// a batch allocates nothing on the caller side. Batches larger than <see cref="MaxReusedKeys"/> allocate.
    /// </summary>
    /// <remarks>Each array is made the first time a thread uses its batch size, so a thread keeps only the sizes it uses.</remarks>
    private static class PrefetchBuffers
    {
        private const int MaxReusedKeys = 64;
        private const int MaxReusedKeyLengths = 4;

        [ThreadStatic] private static KeyArrays?[]? t_keys;
        [ThreadStatic] private static IColumnFamilyHandle[]?[]? t_families;

        public static byte[][] Keys(int keyLength, int count)
        {
            if (count > MaxReusedKeys) return NewKeys(keyLength, count);
            KeyArrays?[] byLength = t_keys ??= new KeyArrays?[MaxReusedKeyLengths];
            for (int i = 0; i < byLength.Length; i++)
            {
                KeyArrays? keys = byLength[i];
                if (keys is null) return (byLength[i] = new KeyArrays(keyLength)).ForCount(count);
                if (keys.KeyLength == keyLength) return keys.ForCount(count);
            }

            return NewKeys(keyLength, count);
        }

        public static IColumnFamilyHandle[] Families(int count)
        {
            if (count > MaxReusedKeys) return new IColumnFamilyHandle[count];
            IColumnFamilyHandle[]?[] byCount = t_families ??= new IColumnFamilyHandle[MaxReusedKeys + 1][];
            return byCount[count] ??= new IColumnFamilyHandle[count];
        }

        private static byte[][] NewKeys(int keyLength, int count)
        {
            byte[][] keys = new byte[count][];
            for (int i = 0; i < keys.Length; i++) keys[i] = new byte[keyLength];
            return keys;
        }

        /// <summary>One set of key buffers; the array for each batch size references the first buffers of the set.</summary>
        private sealed class KeyArrays(int keyLength)
        {
            private readonly byte[]?[] _keys = new byte[MaxReusedKeys][];
            private readonly byte[][]?[] _byCount = new byte[MaxReusedKeys + 1][][];

            public int KeyLength { get; } = keyLength;

            public byte[][] ForCount(int count) => _byCount[count] ?? CreateForCount(count);

            private byte[][] CreateForCount(int count)
            {
                byte[][] keys = new byte[count][];
                for (int i = 0; i < count; i++) keys[i] = _keys[i] ??= new byte[KeyLength];
                return _byCount[count] = keys;
            }
        }
    }

    /// <summary>
    /// Whether a read of <paramref name="key"/> is served from the memtables or block cache. The probe runs at the
    /// block-cache read tier, which never reads a file, so a value that would need disk IO reports not cached.
    /// </summary>
    /// <remarks>
    /// The probe reads the latest version, not the caller's snapshot: it only decides whether a prefetch is worth
    /// sending, and the latest version lives in the same memtables and blocks. A miss is a status, not an exception,
    /// and the empty destination copies no value. A failed read counts as not cached; the read that follows reports it.
    /// </remarks>
    internal bool IsCached(ReadOnlySpan<byte> key, IColumnFamilyHandle? cf)
    {
        if (_isDisposing) return true;
        try
        {
            GetResult result = _db.TryGet(key, default, out _, cf, _cacheOnlyReadOptions);
            return result is GetResult.Found or GetResult.NotFound;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
    }

    internal int GetCStyleWithColumnFamily(scoped ReadOnlySpan<byte> key, Span<byte> output, IColumnFamilyHandle? cf, ReadOptions readOptions)
    {
        ObjectDisposedException.ThrowIf(_isDisposing, this);

        UpdateReadMetrics();

        try
        {
            int length = _db.Get(key, output, cf, readOptions);
            return length < 0 ? 0 : length;
        }
        catch (RocksDbException e)
        {
            HandleFatalDbError(e);
            throw;
        }
    }

    public void PutSpan(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, WriteFlags writeFlags) =>
        SetWithColumnFamily(key, _columnFamily, value, writeFlags);

    public void Merge(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, WriteFlags flags = WriteFlags.None) =>
        MergeWithColumnFamily(key, _columnFamily, value, flags);

    internal void MergeWithColumnFamily(ReadOnlySpan<byte> key, IColumnFamilyHandle? cf, ReadOnlySpan<byte> value, WriteFlags flags = WriteFlags.None)
    {
        ObjectDisposedException.ThrowIf(_isDisposing, this);

        UpdateWriteMetrics();

        try
        {
            _db.Merge(key, value, cf, WriteFlagsToWriteOptions(flags));
        }
        catch (RocksDbException e)
        {
            HandleFatalDbError(e);
            throw;
        }
    }

    private const int GcPressureSpanThreshold = 16 * 1024;

    public void DangerousReleaseMemory(in ReadOnlySpan<byte> span)
    {
        if (!span.IsNullOrEmpty())
        {
            _allocatedSpan.Add(-1);
            if (span.Length >= GcPressureSpanThreshold) GC.RemoveMemoryPressure(span.Length);
        }
        _db.DangerousReleaseMemory(span);
    }

    public ReadOnlySpan<byte> GetNativeSlice(scoped ReadOnlySpan<byte> key, out nint handle, ReadFlags flags)
    {
        // Counted before the disposing check, so Dispose either waits for this slice or this read sees it disposing.
        Interlocked.Increment(ref _pinnedSlices);
        bool pinned = false;
        try
        {
            ObjectDisposedException.ThrowIf(_isDisposing, this);

            ReadOptions readOptions = (flags & ReadFlags.HintCacheMiss) != 0 ? _hintCacheMissOptions : _defaultReadOptions;
            PinnedSlice slice;
            try
            {
                if (!_db.TryGetPinned(key, out slice, _columnFamily, readOptions))
                {
                    handle = default;
                    return null;
                }
            }
            catch (RocksDbException e)
            {
                HandleFatalDbError(e);
                throw;
            }

            ReadOnlySpan<byte> value = slice.Value;
            handle = slice.DangerousDetach();
            pinned = true;
            return value;
        }
        finally
        {
            if (!pinned) ReleasePinnedSlice();
        }
    }

    public void DangerousReleaseHandle(nint handle)
    {
        PinnedSlice.DangerousDestroy(handle);
        if (handle != 0) ReleasePinnedSlice();
    }

    /// <summary>How long <see cref="Dispose"/> waits for pinned slices before it leaves the native close to the last release.</summary>
    internal TimeSpan PinnedSliceDrainTimeout { get; init; } = TimeSpan.FromSeconds(10);

    private void ReleasePinnedSlice()
    {
        if (Interlocked.Decrement(ref _pinnedSlices) == 0 && Volatile.Read(ref _nativeCloseDeferred)) CloseNative();
    }

    /// <summary>Waits for the slices <see cref="GetNativeSlice(ReadOnlySpan{byte}, out nint, ReadFlags)"/> handed out to be released.</summary>
    /// <returns><see langword="false"/> if slices are still pinned after <see cref="PinnedSliceDrainTimeout"/>.</returns>
    /// <remarks>
    /// A pinned slice must be released before its database closes, and a background reader, such as a code read ahead
    /// of execution, can still hold one when shutdown disposes the database.
    /// </remarks>
    private bool WaitForPinnedSlices()
    {
        long start = Stopwatch.GetTimestamp();
        SpinWait spin = default;
        while (Volatile.Read(ref _pinnedSlices) != 0)
        {
            if (Stopwatch.GetElapsedTime(start) > PinnedSliceDrainTimeout) return false;
            spin.SpinOnce();
        }

        return true;
    }

    /// <summary>Closes the native database once, from <see cref="Dispose"/> or from the last pinned slice release.</summary>
    private void CloseNative()
    {
        if (Interlocked.Exchange(ref _nativeClosed, 1) == 0) ReleaseUnmanagedResources();
    }

    public void Remove(ReadOnlySpan<byte> key)
    {
        ObjectDisposedException.ThrowIf(_isDisposing, this);

        try
        {
            _db.Remove(key, _columnFamily, WriteOptions);
        }
        catch (RocksDbException e)
        {
            HandleFatalDbError(e);
            throw;
        }
    }

    public void RemoveRange(ReadOnlySpan<byte> firstKeyInclusive, ReadOnlySpan<byte> lastKeyExclusive) =>
        RemoveRange(firstKeyInclusive, lastKeyExclusive, _columnFamily);

    internal void RemoveRange(ReadOnlySpan<byte> firstKeyInclusive, ReadOnlySpan<byte> lastKeyExclusive, IColumnFamilyHandle? cf)
    {
        ObjectDisposedException.ThrowIf(_isDisposing, this);

        try
        {
            // The binding exposes DeleteRange only on a batch, which also puts the tombstone in the WAL.
            using WriteBatch batch = new();
            batch.DeleteRange(firstKeyInclusive, lastKeyExclusive, cf);
            _db.Write(batch, WriteOptions);
        }
        catch (RocksDbException e)
        {
            HandleFatalDbError(e);
            throw;
        }
    }

    public void ReclaimRange(ReadOnlySpan<byte> firstKeyInclusive, ReadOnlySpan<byte> lastKeyExclusive) =>
        ReclaimRange(firstKeyInclusive, lastKeyExclusive, _columnFamily);

    private const int MaxReclaimBoundLength = 128;

    /// <summary>
    /// Largest key strictly below <paramref name="exclusive"/> of the same length, or false when there is none to
    /// express - an all-zero bound, or one longer than the buffer, in which case the caller reclaims nothing rather
    /// than guessing. Decremented as one big-endian value, so for equal-length keys this is the exact predecessor;
    /// a shorter key that is a prefix of <paramref name="exclusive"/> extended with zeroes sorts above the result and
    /// keeps its files, which is the safe direction - nothing at or above the bound can lose one.
    /// </summary>
    private static bool TryLargestBoundBelow(ReadOnlySpan<byte> exclusive, Span<byte> destination, out int length)
    {
        length = exclusive.Length;
        if (length > destination.Length) return false;

        exclusive.CopyTo(destination);

        // Decremented as one big-endian value, borrowing through zeroes rather than truncating them. Truncating drops
        // the bound below every key that shares the removed bytes, which for a number-prefixed key whose height ends
        // in a zero byte means the top of the chunk keeps its files - and a chunk is often that short.
        int i = length - 1;
        while (i >= 0 && destination[i] == 0)
        {
            destination[i--] = 0xFF;
        }

        if (i < 0) return false;

        destination[i]--;
        return true;
    }

    /// <remarks>
    /// A range tombstone frees nothing and does not count towards pending-compaction bytes, so the disk can stay
    /// occupied for weeks. This unlinks the SST files lying entirely inside the range - nearly all of them, for
    /// ascending block-number keys - and hints for the rest.
    /// Callers must tombstone first: an unlink can drop a tombstone covering keys in partially-overlapping deeper
    /// files, and one written immediately before is still in the memtable. Failure is swallowed - the keys are
    /// already gone durably, so only the timing of the space returning is lost.
    /// </remarks>
    internal void ReclaimRange(ReadOnlySpan<byte> firstKeyInclusive, ReadOnlySpan<byte> lastKeyExclusive, IColumnFamilyHandle? cf)
    {
        if (firstKeyInclusive.IsEmpty || lastKeyExclusive.IsEmpty) return;

        // The C API's include_end is true, so it would consider a file whose largest key IS lastKeyExclusive - a key
        // this range does not cover. Lowering the bound to the largest value strictly below it keeps the half-open
        // contract. Conservative by design: it can leave a file behind, never take one it should not have.
        Span<byte> inclusiveBound = stackalloc byte[MaxReclaimBoundLength];
        if (!TryLargestBoundBelow(lastKeyExclusive, inclusiveBound, out int boundLength)) return;
        inclusiveBound = inclusiveBound[..boundLength];

        try
        {
            // Inside the try, unlike every other method here: a pass racing shutdown must not turn a best-effort
            // reclaim into an error, and the disposal check is one of the ways this can fail.
            ObjectDisposedException.ThrowIf(_isDisposing, this);
            _db.DeleteFilesInRange(firstKeyInclusive, inclusiveBound, cf);
            _db.SuggestCompactRange(firstKeyInclusive, inclusiveBound, cf);
        }
        catch (Exception e)
        {
            if (_logger.IsWarn) _logger.Warn($"Could not reclaim storage for a removed key range in {LogName}: {e.Message}. The keys stay removed; the space returns at the next compaction.");
        }
    }

    internal const int FullEnumerationBatchSize = 10_000;

    public IEnumerable<KeyValuePair<byte[], byte[]>> GetAll(bool ordered = false)
    {
        ThrowIfDisposing();
        return GetAllCore(ordered, _columnFamily);
    }

    protected internal Iterator CreateIterator(ReadOptions readOptions, IColumnFamilyHandle? ch = null)
    {
        try
        {
            return _db.NewIterator(ch, readOptions);
        }
        catch (RocksDbException e)
        {
            HandleFatalDbError(e);
            throw;
        }
    }

    public IEnumerable<byte[]> GetAllKeys(bool ordered = false)
    {
        ThrowIfDisposing();
        return GetAllKeysCore(ordered, _columnFamily);
    }

    public IEnumerable<byte[]> GetAllValues(bool ordered = false)
    {
        ThrowIfDisposing();
        return GetAllValuesCore(ordered, _columnFamily);
    }

    // `_isDisposing` check/update is not atomic and concurrent dispose/read calls may still hit a disposed native DB,
    // yet adding any thread synchronization may affect all readers performance
    internal void ThrowIfDisposing() => ObjectDisposedException.ThrowIf(_isDisposing, this);

    private void IteratorSeekToFirstWithErrorHandling(Iterator iterator)
    {
        try
        {
            iterator.SeekToFirst();
        }
        catch (RocksDbException e)
        {
            HandleFatalDbError(e);
            throw;
        }
    }

    private void IteratorSeekWithErrorHandling(Iterator iterator, byte[] key)
    {
        try
        {
            iterator.Seek(key);
        }
        catch (RocksDbException e)
        {
            HandleFatalDbError(e);
            throw;
        }
    }

    private void IteratorNextWithErrorHandling(Iterator iterator)
    {
        try
        {
            iterator.Next();
        }
        catch (RocksDbException e)
        {
            HandleFatalDbError(e);
            throw;
        }
    }

    private void IteratorDisposeWithErrorHandling(Iterator iterator)
    {
        try
        {
            iterator.Dispose();
        }
        catch (RocksDbException e)
        {
            HandleFatalDbError(e);
            throw;
        }
    }

    internal IEnumerable<KeyValuePair<byte[], byte[]>> GetAllCore(bool ordered, IColumnFamilyHandle? ch = null) =>
        GetAllCore(ordered, ch, static iterator => new KeyValuePair<byte[], byte[]>(iterator.GetKeySpan().ToArray(), iterator.GetValueSpan().ToArray()));

    internal IEnumerable<byte[]> GetAllKeysCore(bool ordered, IColumnFamilyHandle? ch = null) =>
        GetAllCore(ordered, ch, static iterator => iterator.GetKeySpan().ToArray());

    internal IEnumerable<byte[]> GetAllValuesCore(bool ordered, IColumnFamilyHandle? ch = null) =>
        GetAllCore(ordered, ch, static iterator => iterator.GetValueSpan().ToArray());

    private IEnumerable<T> GetAllCore<T>(bool ordered, IColumnFamilyHandle? ch, Func<Iterator, T> projection)
    {
        byte[]? resumeKey = null;
        bool hasMore;

        do
        {
            using ArrayPoolList<T> batch = new(FullEnumerationBatchSize);
            hasMore = ReadFullEnumerationBatch(ordered, ch, resumeKey, projection, batch, out resumeKey);

            foreach (T item in batch)
            {
                yield return item;
            }
        }
        while (hasMore);
    }

    private bool ReadFullEnumerationBatch<T>(
        bool ordered,
        IColumnFamilyHandle? ch,
        byte[]? resumeKey,
        Func<Iterator, T> projection,
        ArrayPoolList<T> batch,
        out byte[]? nextResumeKey)
    {
        ThrowIfDisposing();
        nextResumeKey = null;
        ReadOptions readOptions = new();
        Iterator? iterator = null;

        try
        {
            readOptions.SetTailing(!ordered);
            readOptions.SetTotalOrderSeek(true);
            iterator = CreateIterator(readOptions, ch);

            if (resumeKey is null)
            {
                IteratorSeekToFirstWithErrorHandling(iterator);
            }
            else
            {
                IteratorSeekWithErrorHandling(iterator, resumeKey);
                if (iterator.Valid() && iterator.GetKeySpan().SequenceEqual(resumeKey))
                {
                    IteratorNextWithErrorHandling(iterator);
                }
            }

            while (iterator.Valid())
            {
                batch.Add(projection(iterator));
                if (batch.Count == FullEnumerationBatchSize)
                {
                    byte[] boundaryKey = iterator.GetKeySpan().ToArray();
                    IteratorNextWithErrorHandling(iterator);
                    if (iterator.Valid())
                    {
                        nextResumeKey = boundaryKey;
                        return true;
                    }

                    return false;
                }

                IteratorNextWithErrorHandling(iterator);
            }

            return false;
        }
        finally
        {
            try
            {
                if (iterator is not null)
                {
                    IteratorDisposeWithErrorHandling(iterator);
                }
            }
            finally
            {
                readOptions.Dispose();
            }
        }
    }

    protected internal bool KeyExistsWithColumn(ReadOnlySpan<byte> key, IColumnFamilyHandle? cf)
    {
        ObjectDisposedException.ThrowIf(_isDisposing, this);

        try
        {
            return _db.HasKey(key, cf, _defaultReadOptions);
        }
        catch (RocksDbException e)
        {
            HandleFatalDbError(e);
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
        /// we write the batch in smaller batches. This removes atomicity so it's only turned on when the NoWAL flag is on.
        /// It does not work as well as just turning on unordered_write, but Snapshot and Iterator can still work.
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
            nuint size = batch.DataSize;
            if (size > (uint)16.KiB || _reusableWriteBatch is not null)
            {
                batch.Dispose();
                return;
            }

            batch.Clear();
            _reusableWriteBatch = batch;
        }

        public void Clear()
        {
            ObjectDisposedException.ThrowIf(_dbOnTheRocks._isDisposed, _dbOnTheRocks);
            _rocksBatch.Clear();
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
            catch (RocksDbException e)
            {
                _dbOnTheRocks.HandleFatalDbError(e);
                throw;
            }
        }

        public void Delete(ReadOnlySpan<byte> key, IColumnFamilyHandle? cf = null)
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);

            _rocksBatch.Delete(key, cf);
        }

        public void Set(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, IColumnFamilyHandle? cf = null, WriteFlags flags = WriteFlags.None)
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

        public void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None) =>
            Set(key, value, _dbOnTheRocks._columnFamily, flags);

        public void PutSpan(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, WriteFlags flags = WriteFlags.None) =>
            Set(key, value, _dbOnTheRocks._columnFamily, flags);

        public void Merge(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, WriteFlags flags = WriteFlags.None) =>
            Merge(key, value, _dbOnTheRocks._columnFamily, flags);

        public void Merge(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, IColumnFamilyHandle? cf = null, WriteFlags flags = WriteFlags.None)
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);

            _rocksBatch.Merge(key, value, cf);
            _writeFlags = flags;

            if ((flags & WriteFlags.DisableWAL) != 0) FlushOnTooManyWrites();
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
            catch (RocksDbException e)
            {
                _dbOnTheRocks.HandleFatalDbError(e);
                throw;
            }
        }
    }

    public void Flush(bool onlyWal = false)
    {
        ObjectDisposedException.ThrowIf(_isDisposing, this);

        InnerFlush(onlyWal);
    }

    public void FlushOrThrow()
    {
        ObjectDisposedException.ThrowIf(_isDisposing, this);
        try
        {
            FlushMemtable();
        }
        catch (RocksDbException e)
        {
            HandleFatalDbError(e);
            throw;
        }
    }

    /// <summary>The whole store, as true open bounds at the native layer - a managed null can marshal as an empty
    /// key, and an [empty, empty) range compacts nothing at all.</summary>
    public void Compact() => CompactOpenRange(_columnFamily, forceBottommost: false);

    /// <summary>Forcing the bottommost level is what makes a dead-weight compaction return space - the default
    /// moves tombstones down and leaves them there - and is exactly wrong for routine compactions, which would
    /// rewrite every already-settled file on every call.</summary>
    internal void CompactOpenRange(IColumnFamilyHandle? cf, bool forceBottommost) =>
        _db.CompactRange(null, null, forceBottommost, cf);

    /// <inheritdoc/>
    public virtual bool CompactIfDeadWeightExceeds(double deadRatio)
    {
        if (!ExceedsDeadWeight(GatherProperty("rocksdb.aggregated-table-properties"), GatherProperty("rocksdb.total-sst-files-size"), deadRatio)) return false;

        if (_logger.IsInfo) _logger.Info($"Compacting {LogName}: its files are mostly tombstones. This runs until done and aborts cleanly on shutdown.");
        CompactOpenRange(_columnFamily, forceBottommost: true);
        return true;
    }

    /// <inheritdoc/>
    public virtual void InterruptCompactions() => _db.DisableManualCompaction();

    internal string? GatherProperty(string name) => _columnFamily is null ? _db.GetProperty(name) : _db.GetProperty(name, _columnFamily);

    /// <summary>Decided from the tombstone counts the SST files themselves aggregate, never from a live-size
    /// estimate: that estimate reads file key ranges and cannot see puts shadowed by higher-level tombstones,
    /// which is exactly the shape mass deletion leaves. Small stores never qualify: their debt is not worth a
    /// rewrite.</summary>
    internal static bool ExceedsDeadWeight(string? aggregatedTableProperties, string? totalFilesSize, double deadRatio)
    {
        if (!long.TryParse(totalFilesSize, out long totalBytes) || totalBytes <= MinDeadWeightCompactionBytes) return false;
        if (!TryParseTableProperty(aggregatedTableProperties, "# entries=", out long entries)
            || !TryParseTableProperty(aggregatedTableProperties, "# deletions=", out long deletions)) return false;

        long puts = entries - deletions;
        return deletions > 0 && (puts <= 0 || deletions >= puts * deadRatio);
    }

    private static bool TryParseTableProperty(string? aggregated, string key, out long value)
    {
        value = 0;
        if (aggregated is null) return false;

        int start = aggregated.IndexOf(key, StringComparison.Ordinal);
        if (start < 0) return false;
        start += key.Length;

        int end = start;
        while (end < aggregated.Length && char.IsAsciiDigit(aggregated[end])) end++;
        return end > start && long.TryParse(aggregated.AsSpan(start, end - start), out value);
    }

    private const long MinDeadWeightCompactionBytes = 1L << 30;

    public virtual void SyncWal()
    {
        ObjectDisposedException.ThrowIf(_isDisposing, this);
        try
        {
            FlushWal();
        }
        catch (RocksDbException e)
        {
            HandleFatalDbError(e);
            throw;
        }
    }

    private void FlushWal() => _db.FlushWal(sync: true);

    private void InnerFlush(bool onlyWal)
    {
        try
        {
            FlushWal();

            if (!onlyWal)
            {
                FlushMemtable();
            }
        }
        catch (RocksDbException e)
        {
            // Fast-shuts down on corruption or IO error; anything else falls through, so log it
            // rather than swallowing the flush failure silently.
            HandleFatalDbError(e);
            if (_logger.IsWarn) _logger.Warn($"Failed to flush {LogName} DB: {e.Message}");
        }
    }

    private void FlushMemtable()
    {
        if (_columnFamily is null) _db.Flush(_defaultFlushOptions);
        else _db.Flush(_defaultFlushOptions, _columnFamily);
    }

    public virtual void Clear()
    {
        Dispose();
        _instance.Delete();
    }

    protected virtual void ReleaseUnmanagedResources()
    {
        // ReSharper disable once ConstantConditionalAccessQualifier
        // running in finalizer, potentially not fully constructed
        foreach (IWriteBatch batch in _currentBatches)
        {
            batch.Dispose();
        }

        _iteratorManager?.Dispose();
        _seekIteratorManager.Dispose();

        _defaultReadOptions.Dispose();
        _hintCacheMissOptions.Dispose();
        _cacheOnlyReadOptions.Dispose();
        _readAheadReadOptions?.Dispose();
        _seekReadOptions.Dispose();

        _instance.Dispose();
    }

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _isDisposing, true, false)) return;
        _prefetches.Close();

        if (_logger.IsInfo) _logger.Info($"Disposing DB {LogName}");
        bool drained = WaitForPinnedSlices();
        // An unfinished prefetch holds its own lease on the native database, which then closes when the read completes.
        if (!_prefetches.WaitForAll(PinnedSliceDrainTimeout) && _logger.IsWarn)
            _logger.Warn($"DB {LogName} closes when its {PrefetchesInFlight} prefetch reads complete");

        _reader.Dispose();

        if (_perTableDbConfig.FlushOnExit != FlushOnExitMode.None)
            InnerFlush(onlyWal: _perTableDbConfig.FlushOnExit == FlushOnExitMode.WalOnly);

        if (drained)
        {
            CloseNative();
        }
        else
        {
            // A slice still pinned would read freed memory, so the last release closes the database.
            if (_logger.IsWarn) _logger.Warn($"DB {LogName} closes when its {Volatile.Read(ref _pinnedSlices)} pinned slices are released");
            Interlocked.Exchange(ref _nativeCloseDeferred, true);
            if (Volatile.Read(ref _pinnedSlices) == 0) CloseNative();
        }

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

    public void Tune(ITunableDb.TuneType type)
    {
        if (_currentTune == type) return;

        // See https://github.com/EighteenZi/rocksdb_wiki/blob/master/RocksDB-Tuning-Guide.md
        switch (type)
        {
            // Depending on tune type, allow num of L0 files to grow causing compaction to occur in a larger size. This
            // reduces write amplification at the expense of read response time and amplification while the tune is
            // active. Additionally, the larger compaction causes larger spikes of IO, larger memory usage, and may temporarily
            // use up a large amount of disk space. User may not want to enable this if they plan to run a validator node
            // while the node is still syncing or run another node on the same machine. Specifying a rate limit
            // smoothens this spike somewhat by not blocking writes while allowing compaction to happen in background
            // at 1/10th the specified speed (if rate limited).
            //
            // Total writes written on different tunes during mainnet sync in TB.
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
            // than read measured from a process. It is likely that most files are cached as I have 128GB of RAM.
            // Also notice that the heavier the tune, the higher the reads.
            case ITunableDb.TuneType.WriteBias:
                // Keep the same l1 size but apply other adjustment which should increase the buffer number and make
                // l0 the same size as l1 but keep the LSM the same. This improves flush parallelization and
                // write amplification due to mismatch of l0 and l1 size, but does not reduce compaction from other
                // levels.
                ApplyOptions(GetHeavyWriteOptions(_maxBytesForLevelBase));
                break;
            case ITunableDb.TuneType.HeavyWrite:
                // Compaction spikes are clear at this point. Will definitely affect attestations performance.
                // It's unclear if it improves or slows down sync time. Seems to be the sweet spot.
                ApplyOptions(GetHeavyWriteOptions(2UL.GiB));
                break;
            case ITunableDb.TuneType.AggressiveHeavyWrite:
                // For when you are desperate, but don't wanna disable compaction completely, because you don't want
                // peers to drop. Tend to be faster than disabling compaction completely, except if your ratelimit
                // is a bit low and your compaction is lagging behind, which will trigger slowdown, so sync will hang
                // intermittently, but at least peer count is stable.
                ApplyOptions(GetHeavyWriteOptions(16UL.GiB));
                break;
            case ITunableDb.TuneType.DisableCompaction:
                // Completely disable compaction. On mainnet, the max num of l0 files for state seems to be about 10800.
                // Blocksdb are way more at 53000. Final compaction for state db needs 30 minutes, while blocks db need
                // 13 hours. Receipts db don't show up in metrics likely because it's a column db.
                // Ram usage at that time was 86 GB. The default buffer size for blocks on mainnet is too low
                // to make this work reasonably well.
                // L0 to L1 compaction is known to be slower than other levels, so its
                // Snap sync performance suffers as it does have some read during stitching.
                // If you don't specify a lower open files limit, it tends to crash, like... the whole system
                // crashes. I don't have any open file limit at OS level.
                // Also, if a peer sends a packet that causes a query to the state db during snap sync like GetNodeData
                // or some of the tx filter querying state, It'll cause the network stack to hang and triggers
                // large peer drops. Also happens on lesser tune, but weaker.
                // State sync essentially hangs until that completes because its read heavy, and the uncompacted db is
                // slow to a halt.
                // Additionally, the number of open files handles measured from collectd jumped massively higher. Some
                // user config may not be able to handle this.
                // With all those cons, this results in the minimum writes amplification possible via tweaking compaction
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

    public void SetWriteBuffer(long sizeBytes) =>
        ApplyOptions(
        [
            new("write_buffer_size", sizeBytes.ToString()),
            new("max_bytes_for_level_base", (sizeBytes * 4).ToString()),
        ]);

    private void ApplyOptions(IEnumerable<KeyValuePair<string, string>> options)
    {
        if (_columnFamily is null) _db.SetOptions(options);
        else _db.SetOptions(_columnFamily, options);
    }

    private Dictionary<string, string> GetStandardOptions() =>
        // Defaults are from rocksdb source code
        new()
        {
            { "write_buffer_size", _writeBufferSize.ToString() },
            { "max_write_buffer_number", _maxWriteBufferNumber.ToString() },

            { "level0_file_num_compaction_trigger", 4.ToString() },
            { "level0_slowdown_writes_trigger", 20.ToString() },

            // Very high, so that after moving from HeavyWrite, we don't immediately hang.
            // This does mean that under a very rare case, the l0 file can accumulate, which slows down the db
            // until they get compacted.
            { "level0_stop_writes_trigger", 1024.ToString() },

            { "max_bytes_for_level_base", _maxBytesForLevelBase.ToString() },
            { "target_file_size_base", _targetFileSizeBase.ToString() },
            { "disable_auto_compactions", "false" },

            { "enable_blob_files", "false" },

            { "soft_pending_compaction_bytes_limit", 64.GiB.ToString() },
            { "hard_pending_compaction_bytes_limit", 256.GiB.ToString() },
        };

    private static Dictionary<string, string> GetHashDbOptions() =>
        new()
        {
            // Some database config is slightly faster on a hash db database. These are applied when hash db is detected
            // to prevent unexpected regression.
            { "table_factory.block_size", "4096" },
            { "table_factory.block_restart_interval", "16" },
            { "compression", "kSnappyCompression" },
            { "max_bytes_for_level_multiplier", "10" },
            { "max_bytes_for_level_base", "256000000" },
        };

    /// <summary>
    /// Allow number of l0 files to grow very large. This dramatically increases read response time by about
    /// (l0FileNumTarget / (default num (4) + max level usually (4)), but it saves write bandwidth as l0->l1 happens
    /// in larger size. In addition to that, the large base l1 size means the number of levels is a bit lower.
    /// Note: Regardless of max_open_files config, the number of files handles jumped by this number when compacting. It
    /// could be that l0->l1 compaction does not (or can't?) follow the max_open_files limit.
    /// </summary>
    /// <param name="l0SizeTarget">
    ///  This caps the maximum allowed number of l0 files, which is also the read response time amplification.
    /// </param>
    /// <returns></returns>
    private Dictionary<string, string> GetHeavyWriteOptions(ulong l0SizeTarget)
    {
        // Make buffer (probably) smaller so that it does not take too much memory to have many of them.
        // More buffer means more parallel flush, but each read has to go through all buffers one by one, much like l0
        // but no io, only cpu.
        // bufferSize*maxBufferNumber = 16MB*Core count, which is the max memory used, which tends to be the case as it's now
        // stalled by compaction instead of a flush.
        // The buffer is not compressed unlike l0File, so to account for it, its size needs to be slightly larger.
        ulong targetFileSize = 16UL.MiB;
        ulong bufferSize = (ulong)(targetFileSize / _perTableDbConfig.CompressibilityHint);
        ulong l0FileSize = targetFileSize * (ulong)_minWriteBufferToMerge;
        ulong maxBufferNumber = (ulong)Environment.ProcessorCount;

        // Guide recommends having l0 and l1 to be the same size. They have to be compacted together, so if l1 is larger,
        // the extra size in l1 is basically extra rewrites. If l0 is larger... then I don't know why not. Even so, it seems to
        // always get triggered when l0 size exceeds max_bytes_for_level_base even if the file number is less than l0FileNumTarget.
        ulong l0FileNumTarget = l0SizeTarget / l0FileSize;
        ulong l1SizeTarget = l0SizeTarget;

        return new()
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
            { "soft_pending_compaction_bytes_limit", 100000.GiB.ToString() },
            { "hard_pending_compaction_bytes_limit", 100000.GiB.ToString() },
        };
    }

    private Dictionary<string, string> GetDisableCompactionOptions()
    {
        Dictionary<string, string> heavyWriteOption = GetHeavyWriteOptions(32UL.GiB);

        heavyWriteOption["disable_auto_compactions"] = "true";
        // Increase the size of the write buffer, which reduces the number of l0 files by 4x. This does slow down
        // the memtable a little bit. So if you are not write limited, you'll get memtable limited instead.
        // This does increase the total memory buffer size, but counterintuitively, this reduces overall memory usage
        // as it ran out of bloom filter cache, so it needs to do actual IO.
        heavyWriteOption["write_buffer_size"] = 64.MiB.ToString();

        return heavyWriteOption;
    }


    private static Dictionary<string, string> GetBlobFilesOptions() =>
        // Enable blob files, see: https://rocksdb.org/blog/2021/05/26/integrated-blob-db.html
        // This is very useful for blocks, as it almost eliminates 95% of the compaction as the main db no longer
        // store the actual data, but only points to blob files. This config reduces total blocks db writes from about
        // 4.6 TB to 0.76 TB, where even the WAL took 0.45 TB (wal is not compressed), with peak writes of about 300MBps,
        // it may not even saturate a SATA SSD on a 1GBps internet.

        // You don't want to turn this on other DB as it does add an indirection which take up an additional iop.
        // But for large values like blocks (3MB decompressed to 8MB), the response time increase is negligible.
        // However, without a large buffer size, it will create tens of thousands of small files. There are
        // various workaround it, but it all increases total writes, which defeats the purpose.
        // Additionally, as the `max_bytes_for_level_base` is set to very low, existing users will suddenly
        // get a lot of compaction. So can't turn this on all the time. Turning this back off will just put back
        // new data to SST files.

        new()
        {
            { "enable_blob_files", "true" },
            { "blob_compression_type", "kSnappyCompression" },

            // Make the file size big, so we have less of them.
            { "write_buffer_size", 256.MiB.ToString() },
            // Current memtable + 2 concurrent writes. Can't have too many of these as it takes up RAM.
            { "max_write_buffer_number", 3.ToString() },

            // These two are SST files instead of the blobs, which are now much smaller.
            { "max_bytes_for_level_base", 4.MiB.ToString() },
            { "target_file_size_base", 1.MiB.ToString() },
        };

    /// <summary>
    /// Iterators should not be kept for long as it will pin some memory block and sst file. This would show up as
    /// temporary higher disk usage or memory usage.
    ///
    /// This class handles a periodic timer that periodically disposes all iterators.
    /// </summary>
    public class IteratorManager : IDisposable
    {
        private readonly ManagedIterators _readaheadIterators = new();
        private readonly ManagedIterators _readaheadIterators2 = new();
        private readonly ManagedIterators _readaheadIterators3 = new();
        private readonly RocksDb _rocksDb;
        private readonly IColumnFamilyHandle? _cf;
        private readonly ReadOptions _readOptions;
        private readonly Timer _timer;

        // used to guarantee iterators in timer are not accessed after DB disposal
        private readonly Lock _disposeLock = new();
        private bool _isDisposed;

        // This is about once every two second maybe at max throughput.
        private const int IteratorUsageLimit = 1000000;

        /// <summary>Enables forward probes for short, sequential flat trie keys.</summary>
        internal bool SequentialKeys { get; init; }

        public IteratorManager(RocksDb rocksDb, IColumnFamilyHandle? cf, ReadOptions readOptions)
        {
            _rocksDb = rocksDb;
            _cf = cf;
            _readOptions = readOptions;

            _timer = new Timer(OnTimer, null, TimeSpan.Zero, TimeSpan.FromSeconds(10));
        }

        private void OnTimer(object? state)
        {
            // Skip the tick instead of stacking up callbacks
            if (!_disposeLock.TryEnter()) return;

            try
            {
                if (_isDisposed) return;

                _readaheadIterators.ClearIterators();
                _readaheadIterators2.ClearIterators();
                _readaheadIterators3.ClearIterators();
            }
            finally
            {
                _disposeLock.Exit();
            }
        }

        public void Dispose()
        {
            lock (_disposeLock)
            {
                if (_isDisposed) return;
                _isDisposed = true;

                _timer.Dispose();
                _readaheadIterators.DisposeAll();
                _readaheadIterators2.DisposeAll();
                _readaheadIterators3.DisposeAll();
            }
        }

        public RentWrapper Rent(ReadFlags flags)
        {
            ManagedIterators iterators = GetIterators(flags);
            IteratorHolder holder = iterators.Value!;
            // If null, we create a new one.
            Iterator? iterator = Interlocked.Exchange(ref holder.Iterator, null);
            return new RentWrapper(iterator ?? _rocksDb.NewIterator(_cf, _readOptions), flags, this);
        }

        private ManagedIterators GetIterators(ReadFlags flags) => flags switch
        {
            _ when (flags & ReadFlags.HintReadAhead2) != 0 => _readaheadIterators2,
            _ when (flags & ReadFlags.HintReadAhead3) != 0 => _readaheadIterators3,
            _ => _readaheadIterators
        };

        private void Return(Iterator iterator, ReadFlags flags)
        {
            ManagedIterators iterators = GetIterators(flags);
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
            // Well... this is weird. I'll just dispose of it.
            oldIterator?.Dispose();
        }

        public readonly struct RentWrapper(Iterator iterator, ReadFlags flags, IteratorManager manager) : IDisposable
        {
            public Iterator Iterator => iterator;

            public void Dispose() => manager.Return(iterator, flags);
        }

        // Note: use of threadlocal is very important as the seek forward is fast, but the seek backward is not fast.
        private sealed class ManagedIterators() : ThreadLocal<IteratorHolder>(static () => new IteratorHolder(), trackAllValues: true)
        {
            private bool _disposed = false;

            public void ClearIterators()
            {
                if (_disposed) return;
                if (Values is not { } values) return;
                foreach (IteratorHolder iterator in values)
                {
                    iterator?.Dispose();
                }
            }

            public void DisposeAll()
            {
                ClearIterators();
                Dispose();
            }

            protected override void Dispose(bool disposing)
            {
                // Note: This is called from finalizer thread, so we can't use foreach to dispose of all values
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

            public void Dispose() => Interlocked.Exchange(ref Iterator, null)?.Dispose();
        }
    }

    public byte[]? FirstKey => GetEdgeKey(first: true, _columnFamily);

    public byte[]? LastKey => GetEdgeKey(first: false, _columnFamily);

    /// <summary>Returns the smallest or largest key of the database or column family, or null when it is empty.</summary>
    /// <remarks>Uses total order, since a prefix-extractor database (code) otherwise skips its unflushed memtable.</remarks>
    internal byte[]? GetEdgeKey(bool first, IColumnFamilyHandle? cf = null)
    {
        using ReadOptions readOptions = CreateReadOptions();
        readOptions.SetTotalOrderSeek(true);
        using Iterator iterator = CreateIterator(readOptions, cf);
        if (first) iterator.SeekToFirst();
        else iterator.SeekToLast();
        return iterator.Valid() ? iterator.GetKeySpan().ToArray() : null;
    }

    public ISortedView GetViewBetween(ReadOnlySpan<byte> firstKey, ReadOnlySpan<byte> lastKey, ReadFlags flags = ReadFlags.None) => GetViewBetween(firstKey, lastKey, _columnFamily, flags);

    /// <summary>Whether a range view over these bounds can leave the prefix bucket its seek lands in, by the
    /// extractor configured for this store's column family.</summary>
    internal bool CrossesPrefixBucket(ReadOnlySpan<byte> firstKey, ReadOnlySpan<byte> lastKey)
    {
        int length = _prefixExtractorLength;
        if (length == 0) return false;
        if (firstKey.Length < length || lastKey.Length < length) return true;
        return !firstKey[..length].SequenceEqual(lastKey[..length]);
    }

    private static int ParsePrefixExtractorLength(IDictionary<string, string> options)
    {
        // "nullptr" is RocksDB's own spelling for "no extractor", so it means no bucket exists rather than the
        // widest possible one.
        if (!options.TryGetValue("prefix_extractor", out string? extractor)
            || string.IsNullOrWhiteSpace(extractor)
            || extractor.Trim().Equals("nullptr", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        int separator = extractor.LastIndexOfAny([':', '.']);
        // An extractor whose width cannot be read is treated as unbounded, so every range asks for total order.
        return separator >= 0 && int.TryParse(extractor.AsSpan(separator + 1), out int length) && length > 0
            ? length
            : int.MaxValue;
    }

    internal ISortedView GetViewBetween(ReadOnlySpan<byte> firstKey, ReadOnlySpan<byte> lastKey, IColumnFamilyHandle? cf, ReadFlags flags = ReadFlags.None)
    {
        ReadOptions readOptions = CreateReadOptions();
        if ((flags & ReadFlags.HintCacheMiss) != 0) readOptions.SetFillCache(false);
        if ((flags & ReadFlags.HintReadAhead) != 0) readOptions.SetReadaheadSize(ReadAheadSize);

        // A view promises every key between its bounds. Where a prefix extractor is configured - the code
        // database has one, along with a hash index and a prefix-hash memtable - an iterator is only required to
        // stay correct within the seek key's prefix bucket, so a range crossing buckets can come back short or
        // empty. Total order fixes that at the cost of the prefix index and bloom, so it is asked for only when
        // the range can actually leave the bucket; a range that stays inside one keeps the optimisation.
        bool totalOrder = CrossesPrefixBucket(firstKey, lastKey);
        if (totalOrder) readOptions.SetTotalOrderSeek(true);
        readOptions.SetIterateBounds(firstKey, lastKey);

        Iterator iterator = CreateIterator(readOptions, cf);
        return (flags & ReadFlags.HintRepeatedReads) != 0
            ? new RebindableRocksdbSortedView(iterator, readOptions, this, firstKey.Length, lastKey.Length, totalOrder)
            : new RocksdbSortedView(iterator, readOptions);
    }

    /// <inheritdoc/>
    public bool TryGetCeilingInSnapshot(
        scoped ReadOnlySpan<byte> lowerBoundInclusive, scoped ReadOnlySpan<byte> upperBoundExclusive,
        Span<byte> keyBuffer, out int keyLength, Span<byte> valueBuffer, out int valueLength) =>
        _reader.TryGetCeilingInSnapshot(lowerBoundInclusive, upperBoundExclusive, keyBuffer, out keyLength, valueBuffer, out valueLength);

    public bool TryGetCeiling(
        scoped ReadOnlySpan<byte> lowerBoundIncl, scoped ReadOnlySpan<byte> upperBoundExcl,
        Span<byte> keyBuffer, out int keyLength, Span<byte> valueBuffer, out int valueLength
    )
    {
        ThrowIfDisposing();

        return TryGetCeilingWithIterator(
            lowerBoundIncl, upperBoundExcl, _seekIteratorManager.Value,
            keyBuffer, out keyLength, valueBuffer, out valueLength
        );
    }

    public IKeyValueStoreSnapshot CreateSnapshot()
    {
        Snapshot snapshot = _db.CreateSnapshot();
        return new RocksDbSnapshot(this, () =>
        {
            ReadOptions readOptions = CreateReadOptions();
            readOptions.SetSnapshot(snapshot);
            return readOptions;
        }, _columnFamily, snapshot);
    }

    public sealed class RocksDbSnapshot(
        DbOnTheRocks mainDb,
        Func<ReadOptions> readOptionsFactory,
        IColumnFamilyHandle? columnFamily,
        Snapshot snapshot
    ) : RocksDbReader(mainDb, readOptionsFactory, null, columnFamily), IKeyValueStoreSnapshot, ISnapshotSortedKeyValueStore
    {
        private int _disposed;

        /// <inheritdoc/>
        bool ISnapshotSortedKeyValueStore.TryGetCeilingInSnapshot(
            scoped ReadOnlySpan<byte> lowerBoundInclusive, scoped ReadOnlySpan<byte> upperBoundExclusive,
            Span<byte> keyBuffer, out int keyLength, Span<byte> valueBuffer, out int valueLength) =>
            TryGetCeilingInSnapshot(lowerBoundInclusive, upperBoundExclusive, keyBuffer, out keyLength, valueBuffer, out valueLength);

        public override void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            base.Dispose();
            snapshot.Dispose();
        }
    }
}
