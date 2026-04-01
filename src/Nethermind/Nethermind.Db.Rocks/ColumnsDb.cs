// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using FastEnumUtility;
using Nethermind.Core;
using Nethermind.Db.Rocks.Config;
using Nethermind.Logging;
using RocksDbSharp;
using IWriteBatch = Nethermind.Core.IWriteBatch;

namespace Nethermind.Db.Rocks;

public class ColumnsDb<T> : DbOnTheRocks, IColumnsDb<T> where T : struct, Enum
{
    private readonly IDictionary<T, ColumnDb> _columnDbs = new Dictionary<T, ColumnDb>();

    // Cached for ColumnDbSnapshot to avoid per-snapshot recomputation.
    // Initialized once on first CreateSnapshot call; both fields are idempotent (same result from any thread).
    private volatile T[]? _cachedColumnKeys;
    private volatile int _cachedMaxOrdinal = -1;

    public ColumnsDb(string basePath, DbSettings settings, IDbConfig dbConfig, IRocksDbConfigFactory rocksDbConfigFactory, ILogManager logManager, IReadOnlyList<T> keys, IntPtr? sharedCache = null)
        : this(basePath, settings, dbConfig, rocksDbConfigFactory, logManager, ResolveKeys(keys), sharedCache)
    {
    }

    private ColumnsDb(string basePath, DbSettings settings, IDbConfig dbConfig, IRocksDbConfigFactory rocksDbConfigFactory, ILogManager logManager, (IReadOnlyList<T> Keys, IList<string> ColumnNames) keyInfo, IntPtr? sharedCache)
        : base(basePath, settings, dbConfig, rocksDbConfigFactory, logManager, keyInfo.ColumnNames, sharedCache: sharedCache)
    {
        foreach (T key in keyInfo.Keys)
        {
            _columnDbs[key] = new ColumnDb(_db, this, key.ToString()!);
        }
    }

    protected override long FetchTotalPropertyValue(string propertyName)
    {
        long total = 0;
        foreach (KeyValuePair<T, ColumnDb> kv in _columnDbs)
        {
            long value = long.TryParse(_db.GetProperty(propertyName, kv.Value._columnFamily), out long parsedValue)
                ? parsedValue
                : 0;

            total += value;
        }

        return total;
    }

    public override void Compact()
    {
        foreach (T key in ColumnKeys)
        {
            _columnDbs[key].Compact();
        }
    }

    private static IReadOnlyList<T> GetEnumKeys(IReadOnlyList<T> keys)
    {
        if (typeof(T).IsEnum && keys.Count == 0)
        {
            keys = FastEnum.GetValues<T>().ToArray();
        }

        return keys;
    }

    private static (IReadOnlyList<T> Keys, IList<string> ColumnNames) ResolveKeys(IReadOnlyList<T> keys)
    {
        IReadOnlyList<T> resolvedKeys = GetEnumKeys(keys);
        IList<string> columnNames = resolvedKeys.Select(static key => key.ToString()).ToList();

        return (resolvedKeys, columnNames);
    }

    protected override void BuildOptions<TOptions>(IRocksDbConfig dbConfig, Options<TOptions> options, IntPtr? sharedCache, IMergeOperator? mergeOperator)
    {
        base.BuildOptions(dbConfig, options, sharedCache, mergeOperator);
        options.SetCreateMissingColumnFamilies();
    }

    public IDb GetColumnDb(T key) => _columnDbs[key];

    public IEnumerable<T> ColumnKeys => _columnDbs.Keys;

    public IReadOnlyColumnDb<T> CreateReadOnly(bool createInMemWriteStore)
    {
        return new ReadOnlyColumnsDb<T>(this, createInMemWriteStore);
    }

    public new IColumnsWriteBatch<T> StartWriteBatch()
    {
        return new RocksColumnsWriteBatch(this);
    }

    protected override void ApplyOptions(IDictionary<string, string> options)
    {
        string[] keys = options.Select<KeyValuePair<string, string>, string>(static e => e.Key).ToArray();
        string[] values = options.Select<KeyValuePair<string, string>, string>(static e => e.Value).ToArray();
        foreach (KeyValuePair<T, ColumnDb> cols in _columnDbs)
        {
            _rocksDbNative.rocksdb_set_options_cf(_db.Handle, cols.Value._columnFamily.Handle, keys.Length, keys, values);
        }
        base.ApplyOptions(options);
    }

    private class RocksColumnsWriteBatch : IColumnsWriteBatch<T>
    {
        internal readonly RocksDbWriteBatch WriteBatch;
        private readonly ColumnsDb<T> _columnsDb;

        public RocksColumnsWriteBatch(ColumnsDb<T> columnsDb)
        {
            WriteBatch = new RocksDbWriteBatch(columnsDb);
            _columnsDb = columnsDb;
        }

        public IWriteBatch GetColumnBatch(T key) => new RocksColumnWriteBatch(_columnsDb._columnDbs[key], this);

        public void Clear() => WriteBatch.Clear();
        public void Dispose() => WriteBatch.Dispose();
    }

    private class RocksColumnWriteBatch : IWriteBatch
    {
        private readonly ColumnDb _column;
        private readonly RocksColumnsWriteBatch _writeBatch;

        public RocksColumnWriteBatch(ColumnDb column, RocksColumnsWriteBatch writeBatch)
        {
            _column = column;
            _writeBatch = writeBatch;
        }

        public void Dispose()
        {
            _writeBatch.Dispose();
        }

        public void Clear()
        {
            _writeBatch.WriteBatch.Clear();
        }

        public void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None)
        {
            _writeBatch.WriteBatch.Set(key, value, _column._columnFamily, flags);
        }

        public void Merge(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, WriteFlags flags = WriteFlags.None)
        {
            _writeBatch.WriteBatch.Merge(key, value, _column._columnFamily, flags);
        }
    }

    IColumnDbSnapshot<T> IColumnsDb<T>.CreateSnapshot()
    {
        Snapshot snapshot = _db.CreateSnapshot();
        return new ColumnDbSnapshot(this, snapshot);
    }

    private class ColumnDbSnapshot : IColumnDbSnapshot<T>
    {
        private readonly Snapshot _snapshot;
        private readonly ReadOptions _sharedReadOptions;
        private readonly ReadOptions _sharedCacheMissReadOptions;
        private readonly RocksDbSharp.Native _rocksDbNative;
        private int _disposed;

        // Use a flat array indexed by enum ordinal instead of Dictionary<T, IReadOnlyKeyValueStore>.
        // This eliminates the dictionary + backing array allocation per snapshot.
        private readonly RocksDbReader[] _readers;

        public ColumnDbSnapshot(ColumnsDb<T> columnsDb, Snapshot snapshot)
        {
            _snapshot = snapshot;
            _rocksDbNative = columnsDb._rocksDbNative;

            // Create two shared ReadOptions for all column readers instead of 2 per reader.
            // ReadOptions in RocksDbSharp has a finalizer but no IDisposable — creating many
            // short-lived instances causes Gen1/Gen2 GC pressure from finalizer queue buildup.
            _sharedReadOptions = CreateReadOptions(columnsDb, snapshot);
            _sharedCacheMissReadOptions = CreateReadOptions(columnsDb, snapshot);
            _sharedCacheMissReadOptions.SetFillCache(false);

            // Single shared delegate for GetViewBetween — avoids per-reader closure allocation.
            // Note: each GetViewBetween call still creates a new ReadOptions with a finalizer;
            // that is pre-existing behavior not addressed by this PR.
            Func<ReadOptions> readOptionsFactory = () => CreateReadOptions(columnsDb, snapshot);
            T[] keys = CreateKeyCache(columnsDb);
            GetCachedMaxOrdinal(columnsDb, keys);
            _readers = CreateReaders();

            static ReadOptions CreateReadOptions(ColumnsDb<T> columnsDb, Snapshot snapshot)
            {
                ReadOptions options = new ReadOptions();
                options.SetVerifyChecksums(columnsDb.VerifyChecksum);
                options.SetSnapshot(snapshot);
                return options;
            }

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
                    readers[EnumToInt(k)] = new RocksDbReader(
                        columnsDb,
                        _sharedReadOptions,
                        _sharedCacheMissReadOptions,
                        readOptionsFactory,
                        columnFamily: columnsDb._columnDbs[k]._columnFamily);
                }

                return readers;
            }
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

            // Explicitly destroy native ReadOptions handles to prevent finalizer queue buildup.
            // GC.SuppressFinalize prevents the finalizer from running on already-destroyed handles.
            DestroyReadOptions(_sharedReadOptions);
            DestroyReadOptions(_sharedCacheMissReadOptions);

            _snapshot.Dispose();
        }

        private void DestroyReadOptions(ReadOptions options)
        {
            _rocksDbNative.rocksdb_readoptions_destroy(options.Handle);
            GC.SuppressFinalize(options);
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
