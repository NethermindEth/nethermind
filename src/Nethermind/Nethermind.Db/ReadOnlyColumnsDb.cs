//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

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
