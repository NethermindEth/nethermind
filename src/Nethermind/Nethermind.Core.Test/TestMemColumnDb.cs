// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Db;

namespace Nethermind.Core.Test;

public class TestMemColumnsDb<TKey> : IColumnsDb<TKey>
    where TKey : notnull
{
    private readonly IDictionary<TKey, IDb> _columnDbs = new Dictionary<TKey, IDb>();

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
        return new InMemoryColumnWriteBatch<TKey>(this);
    }
}
