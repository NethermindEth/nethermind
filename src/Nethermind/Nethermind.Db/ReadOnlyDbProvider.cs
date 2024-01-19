// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Nethermind.Db
{
    public class ReadOnlyDbProvider : IReadOnlyDbProvider
    {
        private readonly IDbProvider _wrappedProvider;
        private readonly bool _createInMemoryWriteStore;
        private readonly ConcurrentDictionary<string, IReadOnlyDb> _registeredDbs = new(StringComparer.InvariantCultureIgnoreCase);
        private readonly ConcurrentDictionary<string, object> _registeredColumnDbs = new(StringComparer.InvariantCultureIgnoreCase);

        public ReadOnlyDbProvider(IDbProvider? wrappedProvider, bool createInMemoryWriteStore)
        {
            _wrappedProvider = wrappedProvider ?? throw new ArgumentNullException(nameof(wrappedProvider));
            _createInMemoryWriteStore = createInMemoryWriteStore;
            ArgumentNullException.ThrowIfNull(wrappedProvider);
        }

        public void Dispose()
        {
            foreach (KeyValuePair<string, IReadOnlyDb> registeredDb in _registeredDbs)
            {
                registeredDb.Value?.Dispose();
            }
            foreach (KeyValuePair<string, object> registeredColumnDb in _registeredColumnDbs)
            {
                (registeredColumnDb.Value as IDisposable)!.Dispose();
            }
        }

        public void ClearTempChanges()
        {
            foreach (IReadOnlyDb readonlyDb in _registeredDbs.Values)
            {
                readonlyDb.ClearTempChanges();
            }
        }

        public T GetDb<T>(string dbName) where T : class, IDb
        {
            return (T)_registeredDbs
                .GetOrAdd(dbName, (_) => _wrappedProvider
                    .GetDb<T>(dbName)
                    .CreateReadOnly(_createInMemoryWriteStore));
        }

        public IColumnsDb<T> GetColumnDb<T>(string dbName)
        {
            return (IColumnsDb<T>)_registeredColumnDbs
                .GetOrAdd(dbName, (_) => _wrappedProvider
                    .GetColumnDb<T>(dbName)
                    .CreateReadOnly(_createInMemoryWriteStore));
        }

        public void RegisterDb<T>(string dbName, T db) where T : class, IDb
        {
            _wrappedProvider.RegisterDb(dbName, db);
        }

        public void RegisterColumnDb<T>(string dbName, IColumnsDb<T> db)
        {
            _wrappedProvider.RegisterColumnDb(dbName, db);
        }
    }
}
