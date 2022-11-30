// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
            if (wrappedProvider is null)
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
            if (_registeredDbs is not null)
            {
                foreach (KeyValuePair<string, IReadOnlyDb> registeredDb in _registeredDbs)
                {
                    registeredDb.Value?.Dispose();
                }
            }
        }

        public DbModeHint DbMode => _wrappedProvider.DbMode;

        public IDictionary<string, IDb> RegisteredDbs => _wrappedProvider.RegisteredDbs;

        public void ClearTempChanges()
        {
            foreach (IReadOnlyDb readonlyDb in _registeredDbs.Values)
            {
                readonlyDb.ClearTempChanges();
            }
        }

        public T GetDb<T>(string dbName) where T : class, IDb
        {
            if (!_registeredDbs.ContainsKey(dbName))
            {
                throw new ArgumentException($"{dbName} database has not been registered in {nameof(ReadOnlyDbProvider)}.");
            }

            _registeredDbs.TryGetValue(dbName, out IReadOnlyDb? found);
            T result = found as T;
            if (result is null && found is not null)
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
