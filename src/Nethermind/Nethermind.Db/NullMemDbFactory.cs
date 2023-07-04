// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Db
{
    public class NullMemDbFactory : IMemDbFactory
    {
        private NullMemDbFactory() { }

        public static NullMemDbFactory Instance { get; } = new();

        public IDb CreateDb(string dbName)
        {
            throw new InvalidOperationException();
        }

        public IColumnsDb<T> CreateColumnsDb<T>(string dbName)
        {
            throw new InvalidOperationException();
        }
    }
}
