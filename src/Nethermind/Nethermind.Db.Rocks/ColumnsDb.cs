// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using FastEnumUtility;
using Nethermind.Core;
using Nethermind.Db.Rocks.Config;
using Nethermind.Logging;
using Nethermind.Trie;
using RocksDbSharp;
using IWriteBatch = Nethermind.Core.IWriteBatch;

namespace Nethermind.Db.Rocks;

public class ColumnsDb<T> : DbOnTheRocks, IColumnsDb<T> where T : struct, Enum
{
    private readonly IDictionary<T, ColumnDb> _columnDbs = new Dictionary<T, ColumnDb>();

    public ColumnsDb(string basePath, RocksDbSettings settings, IDbConfig dbConfig, ILogManager logManager, IReadOnlyList<T> keys, IntPtr? sharedCache = null)
        : base(basePath, settings, dbConfig, logManager, GetEnumKeys(keys).Select((key) => key.ToString()).ToList(), sharedCache: sharedCache)
    {
        keys = GetEnumKeys(keys);

        foreach (T key in keys)
        {
            _columnDbs[key] = new ColumnDb(_db, this, key.ToString()!);
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

    protected override void BuildOptions<O>(PerTableDbConfig dbConfig, Options<O> options, IntPtr? sharedCache)
    {
        base.BuildOptions(dbConfig, options, sharedCache);
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
        string[] keys = options.Select<KeyValuePair<string, string>, string>(e => e.Key).ToArray();
        string[] values = options.Select<KeyValuePair<string, string>, string>(e => e.Value).ToArray();
        foreach (KeyValuePair<T, ColumnDb> cols in _columnDbs)
        {
            _rocksDbNative.rocksdb_set_options_cf(_db.Handle, cols.Value._columnFamily.Handle, keys.Length, keys, values);
        }
        base.ApplyOptions(options);
    }

    private class RocksColumnsWriteBatch : IColumnsWriteBatch<T>
    {
        internal RocksDbWriteBatch _writeBatch;
        private ColumnsDb<T> _columnsDb;

        public RocksColumnsWriteBatch(ColumnsDb<T> columnsDb)
        {
            _writeBatch = new RocksDbWriteBatch(columnsDb);
            _columnsDb = columnsDb;
        }

        public IWriteBatch GetColumnBatch(T key)
        {
            return new RocksColumnWriteBatch(_columnsDb._columnDbs[key], this);
        }

        public void Dispose()
        {
            _writeBatch.Dispose();
        }
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

        public void DeleteByRange(Span<byte> startKey, Span<byte> endKey)
        {
            _writeBatch._writeBatch.DeleteByRange(startKey, endKey, _column._columnFamily);
        }

        public void Dispose()
        {
            _writeBatch.Dispose();
        }

        public void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None)
        {
            _writeBatch._writeBatch.Set(key, value, _column._columnFamily, flags);
        }
    }
}

public class CachedColumnsDb<T> : IColumnsDb<T> where T : struct, Enum
{
    protected readonly IDictionary<T, IDb> _columnCacheWrappers = new Dictionary<T, IDb>();
    private readonly IColumnsDb<T> _wrappedDb;

    public CachedColumnsDb(IColumnsDb<T> wrappedDb, int maxCapacity)
    {
        _wrappedDb = wrappedDb;

        foreach (T key in Enum.GetValues(typeof(T)))
            _columnCacheWrappers[key] = new CachingDb(wrappedDb.GetColumnDb(key), maxCapacity);
    }

    public IDb GetColumnDb(T key) => _columnCacheWrappers[key];
    public IEnumerable<T> ColumnKeys => _wrappedDb.ColumnKeys;
    public IColumnsWriteBatch<T> StartWriteBatch() => _wrappedDb.StartWriteBatch();
}
