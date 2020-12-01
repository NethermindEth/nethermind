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
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Nethermind.Db
{
    public class ReadOnlyDbProvider : IReadOnlyDbProvider
    {
        private readonly IDbProvider _wrappedProvider;
        private readonly bool _createInMemoryWriteStore;
        private readonly ConcurrentDictionary<string, IReadOnlyDb> _registeredDbs = new ConcurrentDictionary<string, IReadOnlyDb>();
        
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
                RegisterReadOnlyDb(registeredDb.Key, registeredDb.Value);
            }
        }

        public void Dispose()
        {
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

        private void RegisterReadOnlyDb<T>(string dbName, T db) where T : IDb
        {
            var readonlyDb = db.CreateReadOnly(_createInMemoryWriteStore);
            _registeredDbs.TryAdd(dbName, readonlyDb);
        }

        public void RegisterDb<T>(string dbName, T db) where T : IDb
        {
            _wrappedProvider.RegisterDb(dbName, db);
            RegisterReadOnlyDb(dbName, db);
        }
    }
}
