// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;

namespace Nethermind.Db
{
    public class MemColumnsDb<TKey> : IColumnsDb<TKey> where TKey : struct, Enum
    {
        private readonly IDictionary<TKey, IDb> _columnDbs = new Dictionary<TKey, IDb>();

        public MemColumnsDb(string _) : this(Enum.GetValues<TKey>())
        {
        }

        public MemColumnsDb(params TKey[] keys)
        {
            foreach (var key in keys)
            {
                GetColumnDb(key);
            }
        }

        public IDb GetColumnDb(TKey key) => !_columnDbs.TryGetValue(key, out var db) ? _columnDbs[key] = new MemDb() : db;
        public IEnumerable<TKey> ColumnKeys => _columnDbs.Keys;

        public IReadOnlyColumnDb<TKey> CreateReadOnly(bool createInMemWriteStore)
        {
            return new ReadOnlyColumnsDb<TKey>(this, createInMemWriteStore);
        }

        public IColumnsWriteBatch<TKey> StartWriteBatch()
        {
            return new InMemoryColumnWriteBatch<TKey>(this);
        }
    }
}
