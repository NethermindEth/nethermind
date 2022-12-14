// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Db
{
    public class MemDbFactory : IMemDbFactory
    {
        public IColumnsDb<T> CreateColumnsDb<T>(string dbName) => new MemColumnsDb<T>(dbName);

        public IDb CreateDb(string dbName) => new MemDb(dbName);
    }
}
