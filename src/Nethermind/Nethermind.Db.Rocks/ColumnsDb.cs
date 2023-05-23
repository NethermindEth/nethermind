// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using FastEnumUtility;
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

    public IReadOnlyDb CreateReadOnly(bool createInMemWriteStore)
    {
        return new ReadOnlyColumnsDb<T>(this, createInMemWriteStore);
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
}
