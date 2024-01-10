// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Db
{
    public class NullDbFactory : IDbFactory
    {
        private NullDbFactory() { }

        public static NullDbFactory Instance { get; } = new();

        public IDb CreateDb(DbSettings dbSettings)
        {
            throw new InvalidOperationException();
        }

        public IColumnsDb<T> CreateColumnsDb<T>(DbSettings dbSettings) where T : struct, Enum
        {
            throw new InvalidOperationException();
        }
    }
}
