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
