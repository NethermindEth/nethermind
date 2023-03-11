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
    private readonly IDictionary<T, IDbWithSpan> _columnDbs = new Dictionary<T, IDbWithSpan>();

    public ColumnsDb(string basePath, RocksDbSettings settings, IDbConfig dbConfig, ILogManager logManager, IReadOnlyList<T> keys)
        : base(basePath, settings, dbConfig, logManager, GetColumnFamilies(dbConfig, settings, GetEnumKeys(keys)))
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

    private static ColumnFamilies GetColumnFamilies(IDbConfig dbConfig, RocksDbSettings settings, IReadOnlyList<T> keys)
    {
        InitCache(dbConfig);

        ColumnFamilies result = new();
        ulong blockCacheSize = new PerTableDbConfig(dbConfig, settings).BlockCacheSize;
        foreach (T key in keys)
        {
            ColumnFamilyOptions columnFamilyOptions = new();
            columnFamilyOptions.OptimizeForPointLookup(blockCacheSize);
            columnFamilyOptions.SetBlockBasedTableFactory(
                new BlockBasedTableOptions()
                    .SetFilterPolicy(BloomFilterPolicy.Create())
                    .SetBlockCache(_cache));
            result.Add(key.ToString(), columnFamilyOptions);
        }
        return result;
    }

    protected override DbOptions BuildOptions(IDbConfig dbConfig)
    {
        DbOptions options = base.BuildOptions(dbConfig);
        options.SetCreateMissingColumnFamilies();
        return options;
    }

    public IDbWithSpan GetColumnDb(T key) => _columnDbs[key];

    public IEnumerable<T> ColumnKeys => _columnDbs.Keys;

    public IReadOnlyDb CreateReadOnly(bool createInMemWriteStore)
    {
        return new ReadOnlyColumnsDb<T>(this, createInMemWriteStore);
    }
}
