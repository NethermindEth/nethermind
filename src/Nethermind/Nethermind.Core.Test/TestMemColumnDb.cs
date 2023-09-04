// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Db;

namespace Nethermind.Core.Test;

public class TestMemColumnsDb<TKey> : TestMemDb, IColumnsDb<TKey>
    where TKey : notnull
{
    private readonly IDictionary<TKey, IDbWithSpan> _columnDbs = new Dictionary<TKey, IDbWithSpan>();

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

    public IDbWithSpan GetColumnDb(TKey key) => !_columnDbs.TryGetValue(key, out var db) ? _columnDbs[key] = new TestMemDb() : db;
    public IEnumerable<TKey> ColumnKeys => _columnDbs.Keys;

    public IReadOnlyDb CreateReadOnly(bool createInMemWriteStore)
    {
        return new ReadOnlyColumnsDb<TKey>(this, createInMemWriteStore);
    }
}
