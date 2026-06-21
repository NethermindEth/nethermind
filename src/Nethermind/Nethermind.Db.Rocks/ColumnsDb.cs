// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using FastEnumUtility;
using Nethermind.Db.Rocks.Config;
using Nethermind.Logging;

namespace Nethermind.Db.Rocks;

/// <summary>
/// MDBX-backed collection of named column databases.
/// </summary>
public sealed class ColumnsDb<T> : DbOnTheRocks, IColumnsDb<T> where T : struct, Enum
{
    private readonly Dictionary<T, ColumnDb> _columnDbs = [];

    public ColumnsDb(
        string basePath,
        DbSettings settings,
        IDbConfig dbConfig,
        IRocksDbConfigFactory rocksDbConfigFactory,
        ILogManager logManager,
        IReadOnlyList<T> keys,
        IntPtr? sharedCache = null)
        : base(basePath, settings, dbConfig, rocksDbConfigFactory, logManager, openMainTable: true, sharedCache)
    {
        IReadOnlyList<T> resolvedKeys = ResolveKeys(keys);
        for (int i = 0; i < resolvedKeys.Count; i++)
        {
            T key = resolvedKeys[i];
            string columnName = key.ToString();
            IMergeOperator? mergeOperator = settings.ColumnsMergeOperators is not null &&
                settings.ColumnsMergeOperators.TryGetValue(columnName, out IMergeOperator? columnMergeOperator)
                    ? columnMergeOperator
                    : settings.MergeOperator;
            uint dbi = string.Equals(columnName, "Default", StringComparison.Ordinal) ? MainDbi : OpenColumn(columnName);
            _columnDbs[key] = (ColumnDb)CreateColumnDb(columnName, dbi, mergeOperator);
        }
    }

    public IEnumerable<T> ColumnKeys => _columnDbs.Keys;

    public IDb GetColumnDb(T key) =>
        _columnDbs[key];

    public new IColumnsWriteBatch<T> StartWriteBatch() =>
        new MdbxColumnsWriteBatch<T>(Mdbx, _columnDbs);

    public new IColumnDbSnapshot<T> CreateSnapshot() =>
        new MdbxColumnDbSnapshot<T>(Mdbx, _columnDbs);

    public override void Clear()
    {
        uint[] dbis = GC.AllocateUninitializedArray<uint>(_columnDbs.Count + 1);
        int count = 0;

        foreach (ColumnDb column in _columnDbs.Values)
        {
            if (column.Dbi != MainDbi)
            {
                dbis[count++] = column.Dbi;
            }
        }

        dbis[count++] = MainDbi;
        Mdbx.DropTables(dbis, count);
    }

    public override void Dispose() =>
        base.Dispose();

    private static IReadOnlyList<T> ResolveKeys(IReadOnlyList<T> keys)
    {
        if (keys.Count > 0)
        {
            return keys;
        }

        return FastEnum.GetValues<T>();
    }
}
