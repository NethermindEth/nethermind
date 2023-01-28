// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;

namespace Nethermind.Db
{
    public class ReadOnlyColumnsDb<T> : ReadOnlyDb, IColumnsDb<T>
    {
        private readonly IColumnsDb<T> _wrappedDb;
        private readonly bool _createInMemWriteStore;
        private readonly IDictionary<T, ReadOnlyDb> _columnDbs = new Dictionary<T, ReadOnlyDb>();

        public ReadOnlyColumnsDb(IColumnsDb<T> wrappedDb, bool createInMemWriteStore) : base(wrappedDb, createInMemWriteStore)
        {
            _wrappedDb = wrappedDb;
            _createInMemWriteStore = createInMemWriteStore;
        }

        public IDbWithSpan GetColumnDb(T key) => _columnDbs.TryGetValue(key, out var db) ? db : _columnDbs[key] = new ReadOnlyDb(_wrappedDb.GetColumnDb(key), _createInMemWriteStore);

        public IEnumerable<T> ColumnKeys => _wrappedDb.ColumnKeys;

        public override void ClearTempChanges()
        {
            base.ClearTempChanges();
            foreach (var columnDbsValue in _columnDbs.Values)
            {
                columnDbsValue.ClearTempChanges();
            }
        }

        public IReadOnlyDb CreateReadOnly(bool createInMemWriteStore)
        {
            return new ReadOnlyColumnsDb<T>(this, createInMemWriteStore);
        }
    }
}
