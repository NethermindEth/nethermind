// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using Nethermind.Core.Extensions;
using Nethermind.Core;
using Nethermind.Db.Rocks.Config;
using Nethermind.RocksDbBindings;
using IWriteBatch = Nethermind.Core.IWriteBatch;

namespace Nethermind.Db.Rocks;

/// <summary>A database with one column family per value of <typeparamref name="T"/>.</summary>
/// <param name="instance">The open database.</param>
/// <param name="tableConfig">The configuration the database was opened with, which snapshot reads follow.</param>
/// <param name="columnDbs">A store for every column family of <paramref name="instance"/>, disposed with this one.</param>
public class ColumnsDb<T>(RocksDbInstance instance, IRocksDbConfig tableConfig, IDictionary<T, ColumnDb> columnDbs)
    : IColumnsDb<T>, ITunableDb where T : struct, Enum
{
    private readonly IDictionary<T, ColumnDb> _columnDbs = columnDbs;
    // Write batches and the write-ahead log belong to the whole database, so any column can serve them.
    // A batch spanning columns writes with this column's write options, e.g. its WriteAheadLogSync.
    private readonly ColumnDb _anyColumn = columnDbs.Values.First();

    // Cached for ColumnDbSnapshot to avoid per-snapshot recomputation.
    // Initialized once on first CreateSnapshot call; both fields are idempotent (same result from any thread).
    private volatile T[]? _cachedColumnKeys;
    private volatile int _cachedMaxOrdinal = -1;

    internal bool VerifyChecksum { get; } = tableConfig.VerifyChecksum ?? true;
    internal ulong ReadAheadSize { get; } = tableConfig.ReadAheadSize ?? 256UL.KiB;

    internal int PrefetchesInFlight => _columnDbs.Values.Sum(static column => column.PrefetchesInFlight);

    public bool WasRepairedOnOpen => instance.WasRepairedOnOpen;

    public void AcknowledgeRepair() => instance.AcknowledgeRepair();

    public IDbMeta.DbMetric GatherMetric()
    {
        long size = 0, cacheSize = 0, indexSize = 0, memtableSize = 0, totalReads = 0, totalWrites = 0;
        foreach (ColumnDb column in _columnDbs.Values)
        {
            IDbMeta.DbMetric metric = column.GatherMetric();
            size += metric.Size;
            cacheSize += metric.CacheSize;
            indexSize += metric.IndexSize;
            memtableSize += metric.MemtableSize;
            totalReads += metric.TotalReads;
            totalWrites += metric.TotalWrites;
        }

        return new IDbMeta.DbMetric
        {
            Size = size,
            CacheSize = cacheSize,
            IndexSize = indexSize,
            MemtableSize = memtableSize,
            TotalReads = totalReads,
            TotalWrites = totalWrites,
        };
    }

    public long EstimatedCount => _columnDbs.Values.Sum(static column => column.EstimatedCount);

    public void Compact()
    {
        foreach (ColumnDb column in _columnDbs.Values)
        {
            column.Compact();
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// A full flush materializes every column family's memtable into SST, which is required for
    /// <see cref="WriteFlags.DisableWAL"/> writes: they have no WAL entry, so unless their memtable is
    /// flushed they are lost on restart.
    /// </remarks>
    public void Flush(bool onlyWal = false)
    {
        if (onlyWal)
        {
            _anyColumn.Flush(onlyWal: true);
            return;
        }

        foreach (ColumnDb column in _columnDbs.Values)
        {
            column.Flush(onlyWal);
        }
    }

    public void SyncWal() => _anyColumn.SyncWal();

    public void Tune(ITunableDb.TuneType type)
    {
        foreach (ColumnDb column in _columnDbs.Values)
        {
            column.Tune(type);
        }
    }

    public void Clear()
    {
        Dispose();
        instance.Delete();
    }

    public void Dispose()
    {
        foreach (ColumnDb column in _columnDbs.Values)
        {
            column.Dispose();
        }
    }

    public IDb GetColumnDb(T key) => _columnDbs[key];

    public IEnumerable<T> ColumnKeys => _columnDbs.Keys;

    public IReadOnlyColumnDb<T> CreateReadOnly(bool createInMemWriteStore) => new ReadOnlyColumnsDb<T>(this, createInMemWriteStore);

    public IColumnsWriteBatch<T> StartWriteBatch() => new RocksColumnsWriteBatch(this);

    private class RocksColumnsWriteBatch(ColumnsDb<T> columnsDb) : IColumnsWriteBatch<T>
    {
        internal readonly DbOnTheRocks.RocksDbWriteBatch WriteBatch = new(columnsDb._anyColumn);
        private readonly ColumnsDb<T> _columnsDb = columnsDb;

        public IWriteBatch GetColumnBatch(T key) => new RocksColumnWriteBatch(_columnsDb._columnDbs[key], this);

        public void Clear() => WriteBatch.Clear();
        public void Dispose() => WriteBatch.Dispose();
    }

    private class RocksColumnWriteBatch(ColumnDb column, ColumnsDb<T>.RocksColumnsWriteBatch writeBatch) : IWriteBatch
    {
        private readonly ColumnDb _column = column;
        private readonly RocksColumnsWriteBatch _writeBatch = writeBatch;

        public void Dispose() => _writeBatch.Dispose();

        public void Clear() => _writeBatch.WriteBatch.Clear();

        public void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None) => _writeBatch.WriteBatch.Set(key, value, _column._columnFamily, flags);

        public void PutSpan(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, WriteFlags flags = WriteFlags.None) => _writeBatch.WriteBatch.Set(key, value, _column._columnFamily, flags);

        public void Merge(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, WriteFlags flags = WriteFlags.None) => _writeBatch.WriteBatch.Merge(key, value, _column._columnFamily, flags);
    }

    IColumnDbSnapshot<T> IColumnsDb<T>.CreateSnapshot() => CreateSnapshotCore(sequentialReadAhead: false);

    IColumnDbSnapshot<T> IColumnsDb<T>.CreateSnapshot(bool sequentialReadAhead) => CreateSnapshotCore(sequentialReadAhead);

    private ColumnDbSnapshot CreateSnapshotCore(bool sequentialReadAhead) => new(this, instance.Db.CreateSnapshot(), sequentialReadAhead);

    private class ColumnDbSnapshot : IColumnDbSnapshot<T>
    {
        private readonly ColumnsDb<T> _columnsDb;
        private readonly Snapshot _snapshot;
        private readonly ReadOptions _sharedReadOptions;
        private readonly ReadOptions _sharedCacheMissReadOptions;
        private ReadOptions? _readAheadReadOptions;
        private int _disposed;

        // Use a flat array indexed by enum ordinal instead of Dictionary<T, IReadOnlyKeyValueStore>.
        // This eliminates the dictionary + backing array allocation per snapshot.
        private readonly RocksDbReader[] _readers;

        public ColumnDbSnapshot(ColumnsDb<T> columnsDb, Snapshot snapshot, bool sequentialReadAhead)
        {
            _columnsDb = columnsDb;
            _snapshot = snapshot;

            // Create two shared ReadOptions for all column readers instead of 2 per reader —
            // creating many short-lived instances costs a native handle each and, when one is left
            // to its finalizer, Gen1/Gen2 GC pressure from finalizer queue buildup.
            _sharedReadOptions = CreateReadOptions(columnsDb, snapshot);
            _sharedCacheMissReadOptions = CreateReadOptions(columnsDb, snapshot);
            _sharedCacheMissReadOptions.SetFillCache(false);

            // Single shared delegate for GetViewBetween — avoids per-reader closure allocation.
            // Each call still creates its own ReadOptions, disposed by the returned view.
            Func<ReadOptions> readOptionsFactory = () => CreateReadOptions(columnsDb, snapshot);
            // Shared by all column iterator managers; the ReadOptions it returns is created lazily
            // on the first HintReadAhead read, so snapshots that never iterate allocate nothing extra.
            // ReadAheadSize = 0 opts out of readahead iterators, matching DbOnTheRocks.
            Func<ReadOptions>? readAheadOptionsFactory = sequentialReadAhead && columnsDb.ReadAheadSize > 0 ? GetOrCreateReadAheadReadOptions : null;
            T[] keys = CreateKeyCache(columnsDb);
            GetCachedMaxOrdinal(columnsDb, keys);
            _readers = CreateReaders();

            // Cache column keys and max ordinal on the parent ColumnsDb to avoid per-snapshot
            // recomputation. The race is benign (both threads compute identical results) and
            // volatile ensures visibility across cores.
            static T[] CreateKeyCache(ColumnsDb<T> columnsDb)
            {
                T[]? keys = columnsDb._cachedColumnKeys;
                if (keys is null)
                {
                    IDictionary<T, ColumnDb> columnDbs = columnsDb._columnDbs;
                    keys = new T[columnDbs.Count];
                    int idx = 0;
                    foreach (T key in columnDbs.Keys)
                    {
                        keys[idx++] = key;
                    }

                    columnsDb._cachedColumnKeys = keys;
                }

                return keys;
            }
            static void GetCachedMaxOrdinal(ColumnsDb<T> columnsDb, T[] keys)
            {
                if (columnsDb._cachedMaxOrdinal >= 0) return;

                int max = 0;
                for (int i = 0; i < keys.Length; i++)
                {
                    max = Math.Max(max, EnumToInt(keys[i]));
                }

                columnsDb._cachedMaxOrdinal = max;
            }

            // Build flat array of readers indexed by column ordinal
            RocksDbReader[] CreateReaders()
            {
                RocksDbReader[] readers = new RocksDbReader[columnsDb._cachedMaxOrdinal + 1];
                for (int i = 0; i < keys.Length; i++)
                {
                    T k = keys[i];
                    int ordinal = EnumToInt(k);
                    ColumnDb column = columnsDb._columnDbs[k];
                    IColumnFamilyHandle columnFamily = column._columnFamily!;
                    // Internally lazy — until a HintReadAhead read arrives this is just an object allocation.
                    DisposableLazy<DbOnTheRocks.IteratorManager>? iteratorManager = readAheadOptionsFactory is null
                        ? null
                        : new(() => new DbOnTheRocks.IteratorManager(column._db, columnFamily, readAheadOptionsFactory()) { SequentialKeys = true });
                    readers[ordinal] = new RocksDbReader(
                        column,
                        _sharedReadOptions,
                        _sharedCacheMissReadOptions,
                        readOptionsFactory,
                        iteratorManager,
                        columnFamily);
                }

                return readers;
            }
        }

        private static ReadOptions CreateReadOptions(ColumnsDb<T> columnsDb, Snapshot snapshot)
        {
            ReadOptions options = new();
            options.SetVerifyChecksums(columnsDb.VerifyChecksum);
            options.SetSnapshot(snapshot);
            return options;
        }

        /// <summary>
        /// Lazily creates the snapshot-bound <see cref="ReadOptions"/> shared by all column iterator managers.
        /// </summary>
        /// <remarks>
        /// These iterators must not be tailing: tailing iterators
        /// ignore the snapshot and would observe writes made after it.
        /// </remarks>
        private ReadOptions GetOrCreateReadAheadReadOptions()
        {
            ReadOptions? options = Volatile.Read(ref _readAheadReadOptions);
            if (options is not null) return options;

            ReadOptions created = CreateReadOptions(_columnsDb, _snapshot);
            created.SetReadaheadSize(_columnsDb.ReadAheadSize);

            options = Interlocked.CompareExchange(ref _readAheadReadOptions, created, null);
            if (options is not null)
            {
                created.Dispose();
                return options;
            }

            return created;
        }

        public IReadOnlyKeyValueStore GetColumn(T key)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

            int ordinal = EnumToInt(key);
            if ((uint)ordinal >= (uint)_readers.Length || _readers[ordinal] is null)
            {
                throw new KeyNotFoundException($"Column '{key}' is not configured.");
            }

            return _readers[ordinal];
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

            // Iterators pin the snapshot's resources — tear the managers down before releasing anything they read through.
            foreach (RocksDbReader? reader in _readers)
            {
                reader?.IteratorManager?.Dispose();
            }

            // Explicitly destroy native ReadOptions handles to prevent finalizer queue buildup.
            _sharedReadOptions.Dispose();
            _sharedCacheMissReadOptions.Dispose();
            Interlocked.Exchange(ref _readAheadReadOptions, null)?.Dispose();

            _snapshot.Dispose();
        }

        // Non-boxing enum-to-int conversion. JIT eliminates dead branches at
        // instantiation time, so this is zero-cost for any underlying type.
        private static int EnumToInt(T value)
        {
            if (Unsafe.SizeOf<T>() == sizeof(int)) return Unsafe.As<T, int>(ref value);
            if (Unsafe.SizeOf<T>() == sizeof(byte)) return Unsafe.As<T, byte>(ref value);
            if (Unsafe.SizeOf<T>() == sizeof(short)) return Unsafe.As<T, short>(ref value);
            return Convert.ToInt32(value); // fallback for long-backed enums
        }
    }
}
