// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using FastEnumUtility;
using Nethermind.Core;
using Nethermind.Db.Rocks.Config;
using Nethermind.Logging;
using RocksDbSharp;

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

    public IDbWithSpan GetColumnDb(T key) => _columnDbs[key];

    public IEnumerable<T> ColumnKeys => _columnDbs.Keys;

    public IReadOnlyColumnDb<T> CreateReadOnly(bool createInMemWriteStore)
    {
        return new ReadOnlyColumnsDb<T>(this, createInMemWriteStore);
    }

    public new IColumnsBatch<T> StartBatch()
    {
        return new RocksColumnsBatch(this);
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

    private class RocksColumnsBatch : IColumnsBatch<T>
    {
        internal RocksDbBatch _batch;
        private ColumnsDb<T> _columnsDb;

        public RocksColumnsBatch(ColumnsDb<T> columnsDb)
        {
            _batch = new RocksDbBatch(columnsDb);
            _columnsDb = columnsDb;
        }

        public IBatch GetColumnBatch(T key)
        {
            return new RocksColumnBatch(_columnsDb._columnDbs[key], this);
        }

        public void Dispose()
        {
            _batch.Dispose();
        }
    }

    private class RocksColumnBatch : IBatch
    {
        private readonly ColumnDb _column;
        private readonly RocksColumnsBatch _batch;

        public RocksColumnBatch(ColumnDb column, RocksColumnsBatch batch)
        {
            _column = column;
            _batch = batch;
        }

        public void Dispose()
        {
            _batch.Dispose();
        }

        public byte[]? Get(ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None)
        {
            return _column.Get(key, flags);
        }

        public void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None)
        {
            _batch._batch.Set(key, value, _column._columnFamily, flags);
        }
    }
}
