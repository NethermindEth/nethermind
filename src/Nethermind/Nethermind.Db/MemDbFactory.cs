// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Db
{
    public class MemDbFactory : IDbFactory
    {
        public IColumnsDb<T> CreateColumnsDb<T>(string dbName) where T : struct, Enum => new MemColumnsDb<T>(dbName);

        public IDb CreateDb(DbSettings dbSettings)
        {
            return new MemDb(dbSettings.DbName);
        }

        public IColumnsDb<T> CreateColumnsDb<T>(DbSettings dbSettings) where T : struct, Enum
        {
            return new MemColumnsDb<T>(dbSettings.DbName);
        }
    }
}
