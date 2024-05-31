// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;

namespace Nethermind.Db;

public class CycleDbFactory(IDbFactory dbFactory, params string[] cycleDbs) : IDbFactory
{
    public IDb CreateDb(DbSettings dbSettings)
    {
        IDb db = dbFactory.CreateDb(dbSettings);
        return cycleDbs.Contains(dbSettings.DbName, StringComparer.InvariantCultureIgnoreCase)
            ? new CycleDb(db, dbSettings, dbFactory)
            : db;
    }

    public IColumnsDb<T> CreateColumnsDb<T>(DbSettings dbSettings) where T : struct, Enum =>
        dbFactory.CreateColumnsDb<T>(dbSettings);
}
