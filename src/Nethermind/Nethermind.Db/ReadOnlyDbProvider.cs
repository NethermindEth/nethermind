//  Copyright (c) 2018 Demerzel Solutions Limited
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

using System;
using System.Collections.Generic;

namespace Nethermind.Db
{
    public class ReadOnlyDbProvider : IReadOnlyDbProvider
    {
        private readonly IDbProvider _wrappedProvider;
        private readonly bool _createInMemoryWriteStore;
        private readonly Dictionary<string, IReadOnlyDb> _registeredDbs = new Dictionary<string, IReadOnlyDb>();
        
        public ReadOnlyDbProvider(IDbProvider wrappedProvider, bool createInMemoryWriteStore)
        {
            _wrappedProvider = wrappedProvider;
            _createInMemoryWriteStore = createInMemoryWriteStore;
            if (wrappedProvider == null)
            {
                throw new ArgumentNullException(nameof(wrappedProvider));
            }

            foreach (var registeredDb in _wrappedProvider.RegisteredDbs)
            {
                RegisterDb(registeredDb.Key, registeredDb.Value);
            }
        }

        public void Dispose()
        {
            // ToDo why we don't dispose dbs here - investigate it or consult with someone
        }

        public IDb BeamStateDb { get; } = new MemDb();

        public DbModeHint DbMode => _wrappedProvider.DbMode;

        public IDictionary<string, IDb> RegisteredDbs => _wrappedProvider.RegisteredDbs;
        public void ClearTempChanges()
        {            
            foreach(var readonlyDb in _registeredDbs.Values)
            {
                readonlyDb.Restore(-1);
            }

            BeamStateDb.Clear();
        }

        public T GetDb<T>(string dbName) where T : IDb
        {
            if (!_registeredDbs.ContainsKey(dbName))
            {
                throw new ArgumentException($"{dbName} wasn't registed.");
            }

            return (T)_registeredDbs[dbName];
        }

        public void RegisterDb<T>(string dbName, T db) where T : IDb
        {
            if (_registeredDbs.ContainsKey(dbName))
            {
                throw new ArgumentException($"{dbName} has already registered.");
            }

            var readonlyDb = db.CreateReadOnly(_createInMemoryWriteStore);
            _registeredDbs.Add(dbName, readonlyDb);
        }
    }
}
