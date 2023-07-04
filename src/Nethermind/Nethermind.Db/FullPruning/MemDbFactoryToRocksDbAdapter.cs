// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Db.FullPruning
{
    public class MemDbFactoryToRocksDbAdapter : IRocksDbFactory
    {
        private readonly IMemDbFactory _memDbFactory;

        public MemDbFactoryToRocksDbAdapter(IMemDbFactory memDbFactory)
        {
            _memDbFactory = memDbFactory;
        }

        public IDb CreateDb(RocksDbSettings rocksDbSettings) => _memDbFactory.CreateDb(rocksDbSettings.DbName);

        public IColumnsDb<T> CreateColumnsDb<T>(RocksDbSettings rocksDbSettings) where T : struct, Enum => _memDbFactory.CreateColumnsDb<T>(rocksDbSettings.DbName);
    }
}
