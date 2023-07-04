// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;

namespace Nethermind.Db
{
    public class MemColumnsDb<TKey> : MemDb, IColumnsDb<TKey>
    {
        private readonly IDictionary<TKey, IDbWithSpan> _columnDbs = new Dictionary<TKey, IDbWithSpan>();

        public MemColumnsDb(string name)
            : base(name)
        {
        }

        public MemColumnsDb(params TKey[] keys)
        {
            foreach (var key in keys)
            {
                GetColumnDb(key);
            }
        }

        public IDbWithSpan GetColumnDb(TKey key) => !_columnDbs.TryGetValue(key, out var db) ? _columnDbs[key] = new MemDb() : db;
        public IEnumerable<TKey> ColumnKeys => _columnDbs.Keys;

        public IReadOnlyDb CreateReadOnly(bool createInMemWriteStore)
        {
            return new ReadOnlyColumnsDb<TKey>(this, createInMemWriteStore);
        }
    }
}
