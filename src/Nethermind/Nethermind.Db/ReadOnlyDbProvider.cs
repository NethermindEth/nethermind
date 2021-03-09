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

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;

namespace Nethermind.Db
{
    public class ReadOnlyDbProvider : IReadOnlyDbProvider
    {
        private readonly IDbProvider _wrappedProvider;
        private readonly bool _createInMemoryWriteStore;
        private readonly ConcurrentDictionary<string, IReadOnlyDb> _registeredDbs = new(StringComparer.InvariantCultureIgnoreCase);
        
        public ReadOnlyDbProvider(IDbProvider? wrappedProvider, bool createInMemoryWriteStore)
        {
            _wrappedProvider = wrappedProvider ?? throw new ArgumentNullException(nameof(wrappedProvider));
            _createInMemoryWriteStore = createInMemoryWriteStore;
            if (wrappedProvider == null)
            {
                throw new ArgumentNullException(nameof(wrappedProvider));
            }
            
            foreach ((string key, IDb value) in _wrappedProvider.RegisteredDbs)
            {
                RegisterReadOnlyDb(key, value);
            }
        }

        public void Dispose()
        {
            if (_registeredDbs != null)
            {
                foreach (KeyValuePair<string, IReadOnlyDb> registeredDb in _registeredDbs)
                {
                    registeredDb.Value?.Dispose();
                }
            }
        }

        public IDb BeamTempDb { get; } = new MemDb();

        public DbModeHint DbMode => _wrappedProvider.DbMode;

        public IDictionary<string, IDb> RegisteredDbs => _wrappedProvider.RegisteredDbs;
        
        public void ClearTempChanges()
        {            
            foreach(IReadOnlyDb readonlyDb in _registeredDbs.Values)
            {
                readonlyDb.ClearTempChanges();
            }
            
            BeamTempDb.Clear();
        }

        public T GetDb<T>(string dbName) where T : class, IDb
        {
            if (!_registeredDbs.ContainsKey(dbName))
            {
                throw new ArgumentException($"{dbName} database has not been registered in {nameof(ReadOnlyDbProvider)}.");
            }

            _registeredDbs.TryGetValue(dbName, out IReadOnlyDb? found);
            T result = found as T;
            if (result == null && found != null)
            {
                throw new IOException(
                    $"An attempt was made to resolve DB {dbName} as {typeof(T)} while its type is {found.GetType()}.");
            }

            return result;
        }

        private void RegisterReadOnlyDb<T>(string dbName, T db) where T : IDb
        {
            IReadOnlyDb readonlyDb = db.CreateReadOnly(_createInMemoryWriteStore);
            _registeredDbs.TryAdd(dbName, readonlyDb);
        }

        public void RegisterDb<T>(string dbName, T db) where T : class, IDb
        {
            _wrappedProvider.RegisterDb(dbName, db);
            RegisterReadOnlyDb(dbName, db);
        }
    }
}
