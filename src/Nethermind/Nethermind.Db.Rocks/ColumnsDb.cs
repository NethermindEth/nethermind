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
using IWriteBatch = Nethermind.Core.IWriteBatch;

namespace Nethermind.Db.Rocks;

public class ColumnsDb<T> : DbOnTheRocks, IColumnsDb<T> where T : struct, Enum
{
    private readonly IDictionary<T, ColumnDb> _columnDbs = new Dictionary<T, ColumnDb>();

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
        internal RocksDbWriteBatch _writeBatch;
        private readonly ColumnsDb<T> _columnsDb;

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

        public void Dispose()
        {
            _writeBatch.Dispose();
        }

        public void Clear()
        {
            _writeBatch._writeBatch.Clear();
        }

        public void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None)
        {
            _writeBatch._writeBatch.Set(key, value, _column._columnFamily, flags);
        }

        public void Merge(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, WriteFlags flags = WriteFlags.None)
        {
            _writeBatch._writeBatch.Merge(key, value, _column._columnFamily, flags);
        }
    }

    IColumnDbSnapshot<T> IColumnsDb<T>.CreateSnapshot()
    {
        Snapshot snapshot = _db.CreateSnapshot();
        return new ColumnDbSnapshot(this, snapshot);
    }

    private class ColumnDbSnapshot(
        ColumnsDb<T> columnsDb,
        Snapshot snapshot
    ) : IColumnDbSnapshot<T>
    {
        Dictionary<T, IReadOnlyKeyValueStore> _columnDbs = columnsDb.ColumnKeys.ToDictionary((k) => k, (k) =>
        {
            return (IReadOnlyKeyValueStore)(new RocksDbReader(
                columnsDb,
                () =>
                {
                    ReadOptions options = new ReadOptions();
                    options.SetSnapshot(snapshot);
                    return options;
                },
                null,
                columnsDb._columnDbs[k]._columnFamily));
        });

        public IReadOnlyKeyValueStore GetColumn(T key)
        {
            return _columnDbs[key];
        }

        public void Dispose()
        {
            snapshot.Dispose();
        }
    }
}
