// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;

namespace Nethermind.Db
{
    public class DbProvider : IDbProvider
    {
        private readonly ConcurrentDictionary<string, IDb> _registeredDbs =
            new(StringComparer.InvariantCultureIgnoreCase);

        public DbProvider(DbModeHint dbMode)
        {
            DbMode = dbMode;
        }

        public DbModeHint DbMode { get; }

        public IDictionary<string, IDb> RegisteredDbs => _registeredDbs;

        public void Dispose()
        {
            foreach (KeyValuePair<string, IDb> registeredDb in _registeredDbs)
            {
                registeredDb.Value?.Dispose();
            }
        }

        public T GetDb<T>(string dbName) where T : class, IDb
        {
            if (!_registeredDbs.TryGetValue(dbName, out IDb? found))
            {
                throw new ArgumentException($"{dbName} database has not been registered in {nameof(DbProvider)}.");
            }

            if (found is not T result)
            {
                throw new IOException(
                    $"An attempt was made to resolve DB {dbName} as {typeof(T)} while its type is {found.GetType()}.");
            }

            return result;
        }

        public void RegisterDb<T>(string dbName, T db) where T : class, IDb
        {
            if (_registeredDbs.ContainsKey(dbName))
            {
                throw new ArgumentException($"{dbName} has already registered.");
            }

            _registeredDbs.TryAdd(dbName, db);
        }
    }
}
