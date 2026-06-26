// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Nethermind.Db
{
    public class ReadOnlyDbProvider(IDbProvider wrappedProvider, bool createInMemoryWriteStore) : IReadOnlyDbProvider
    {
        private readonly IDbProvider _wrappedProvider = wrappedProvider ?? throw new ArgumentNullException(nameof(wrappedProvider));
        private readonly bool _createInMemoryWriteStore = createInMemoryWriteStore;
        private readonly ConcurrentDictionary<string, IReadOnlyDb> _registeredDbs = new(StringComparer.InvariantCultureIgnoreCase);
        private readonly ConcurrentDictionary<string, IDisposable> _registeredColumnDbs = new(StringComparer.InvariantCultureIgnoreCase);

        public void Dispose()
        {
            foreach (KeyValuePair<string, IReadOnlyDb> registeredDb in _registeredDbs)
            {
                registeredDb.Value?.Dispose();
            }
            foreach (KeyValuePair<string, IDisposable> registeredColumnDb in _registeredColumnDbs)
            {
                registeredColumnDb.Value.Dispose();
            }
        }

        public void ClearTempChanges()
        {
            foreach (KeyValuePair<string, IReadOnlyDb> kvp in _registeredDbs)
            {
                kvp.Value.ClearTempChanges();
            }
        }

        public T GetDb<T>(string dbName) where T : class, IDb => (T)_registeredDbs
                .GetOrAdd(dbName, (_) => _wrappedProvider
                    .GetDb<T>(dbName)
                    .CreateReadOnly(_createInMemoryWriteStore));

        public IColumnsDb<T> GetColumnDb<T>(string dbName) where T : notnull => (IColumnsDb<T>)_registeredColumnDbs
                .GetOrAdd(dbName, (_) => _wrappedProvider
                    .GetColumnDb<T>(dbName)
                    .CreateReadOnly(_createInMemoryWriteStore));
    }
}
