// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace Nethermind.Db
{
    public class ReadOnlyDbProvider(IDbProvider wrappedProvider, bool createInMemoryWriteStore) : IReadOnlyDbProvider
    {
        private readonly IDbProvider _wrappedProvider = wrappedProvider ?? throw new ArgumentNullException(nameof(wrappedProvider));
        private readonly bool _createInMemoryWriteStore = createInMemoryWriteStore;
        private readonly ConcurrentDictionary<string, IReadOnlyDb> _registeredDbs = new(StringComparer.InvariantCultureIgnoreCase);
        private ConcurrentDictionary<string, ColumnDb>? _registeredColumnDbs;

        public void Dispose()
        {
            foreach (KeyValuePair<string, IReadOnlyDb> registeredDb in _registeredDbs)
            {
                registeredDb.Value?.Dispose();
            }
            if (_registeredColumnDbs is null) return;
            foreach (KeyValuePair<string, ColumnDb> registeredColumnDb in _registeredColumnDbs)
            {
                registeredColumnDb.Value.Db.Dispose();
            }
        }

        public void ClearTempChanges()
        {
            foreach (KeyValuePair<string, IReadOnlyDb> kvp in _registeredDbs)
            {
                kvp.Value.ClearTempChanges();
            }

            if (_registeredColumnDbs is null) return;

            foreach (KeyValuePair<string, ColumnDb> registeredColumnDb in _registeredColumnDbs)
            {
                registeredColumnDb.Value.ClearTempChanges();
            }
        }

        public T GetDb<T>(string dbName) where T : class, IDb => (T)_registeredDbs
                .GetOrAdd(dbName, (_) => _wrappedProvider
                    .GetDb<T>(dbName)
                    .CreateReadOnly(_createInMemoryWriteStore));

        public IColumnsDb<T> GetColumnDb<T>(string dbName) where T : notnull => (IColumnsDb<T>)LazyInitializer
            .EnsureInitialized(ref _registeredColumnDbs, static () => new(StringComparer.InvariantCultureIgnoreCase))
            .GetOrAdd(dbName, static (name, provider) => new ColumnDb(
                provider._wrappedProvider.GetColumnDb<T>(name).CreateReadOnly(provider._createInMemoryWriteStore),
                static db => ((IReadOnlyColumnDb<T>)db).ClearTempChanges()), this).Db;

        private readonly struct ColumnDb(IDisposable db, Action<IDisposable> clear)
        {
            public IDisposable Db => db;

            public void ClearTempChanges() => clear(db);
        }
    }
}
