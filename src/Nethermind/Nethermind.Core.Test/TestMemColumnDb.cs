// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Db;

namespace Nethermind.Core.Test;

public class TestMemColumnsDb<TKey> : IColumnsDb<TKey>
    where TKey : struct, Enum
{
    private readonly IDictionary<TKey, TestMemDb> _columnDbs = new Dictionary<TKey, TestMemDb>();

    public TestMemColumnsDb()
    {
    }

    public TestMemColumnsDb(params TKey[] keys)
    {
        foreach (var key in keys)
        {
            GetColumnDb(key);
        }
    }

    public IDb GetColumnDb(TKey key) => !_columnDbs.TryGetValue(key, out var db) ? _columnDbs[key] = new TestMemDb() : db;
    public IEnumerable<TKey> ColumnKeys => _columnDbs.Keys;

    public IColumnsWriteBatch<TKey> StartWriteBatch()
    {
        EnsureAllKey();
        return new InMemoryColumnWriteBatch<TKey>(this);
    }

    public IColumnDbSnapshot<TKey> CreateSnapshot()
    {
        EnsureAllKey();
        return new Snapshot(_columnDbs);
    }

    public void Dispose() { }
    public void Flush(bool onlyWal = false) { }

    private void EnsureAllKey()
    {
        foreach (TKey key in Enum.GetValues<TKey>())
        {
            GetColumnDb(key);
        }
    }

    private class Snapshot(IDictionary<TKey, TestMemDb> columns) : IColumnDbSnapshot<TKey>
    {
        public IReadOnlyKeyValueStore GetColumn(TKey key)
        {
            return columns[key];
        }

        public void Dispose()
        {
        }
    }
}
