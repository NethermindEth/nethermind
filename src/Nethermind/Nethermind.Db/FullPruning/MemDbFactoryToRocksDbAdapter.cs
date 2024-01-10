// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Db.FullPruning
{
    public class MemDbFactoryToRocksDbAdapter : IDbFactory
    {
        private readonly IMemDbFactory _memDbFactory;

        public MemDbFactoryToRocksDbAdapter(IMemDbFactory memDbFactory)
        {
            _memDbFactory = memDbFactory;
        }

        public IDb CreateDb(DbSettings dbSettings) => _memDbFactory.CreateDb(dbSettings.DbName);

        public IColumnsDb<T> CreateColumnsDb<T>(DbSettings dbSettings) where T : struct, Enum => _memDbFactory.CreateColumnsDb<T>(dbSettings.DbName);
    }
}
