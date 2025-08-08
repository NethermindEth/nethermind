// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using NonBlocking;

namespace Nethermind.Db;

public class DbTracker
{
    private readonly ConcurrentDictionary<string, IDbMeta> _createdDbs = new ConcurrentDictionary<string, IDbMeta>();

    public void AddDb(string name, IDbMeta dbMeta)
    {
        _createdDbs.TryAdd(name, dbMeta);
    }

    public IEnumerable<KeyValuePair<string, IDbMeta>> GetAllDbMeta()
    {
        return _createdDbs;
    }

    public class DbFactoryInterceptor(DbTracker tracker, IDbFactory baseFactory) : IDbFactory
    {
        public IDb CreateDb(DbSettings dbSettings)
        {
            IDb db = baseFactory.CreateDb(dbSettings);
            if (db is IDbMeta dbMeta)
            {
                tracker.AddDb(dbSettings.DbName, dbMeta);
            }
            return db;
        }

        public IColumnsDb<T> CreateColumnsDb<T>(DbSettings dbSettings) where T : struct, Enum
        {
            IColumnsDb<T> db = baseFactory.CreateColumnsDb<T>(dbSettings);
            if (db is IDbMeta dbMeta)
            {
                tracker.AddDb(dbSettings.DbName, dbMeta);
            }
            return db;
        }

        public string GetFullDbPath(DbSettings dbSettings) => baseFactory.GetFullDbPath(dbSettings);
    }
}
