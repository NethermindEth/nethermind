// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Db
{
    public class NullRocksDbFactory : IRocksDbFactory
    {
        private NullRocksDbFactory() { }

        public static NullRocksDbFactory Instance { get; } = new();

        public IDb CreateDb(RocksDbSettings rocksDbSettings)
        {
            throw new InvalidOperationException();
        }

        public IColumnsDb<T> CreateColumnsDb<T>(RocksDbSettings rocksDbSettings) where T : struct, Enum
        {
            throw new InvalidOperationException();
        }
    }
}
