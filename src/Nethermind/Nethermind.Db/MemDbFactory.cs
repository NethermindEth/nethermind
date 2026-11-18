// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Db
{
    public class MemDbFactory : IDbFactory
    {
        public IColumnsDb<T> CreateColumnsDb<T>(string dbName) where T : struct, Enum => new SnapshotableMemColumnsDb<T>(dbName);

        public IDb CreateDb(DbSettings dbSettings) => IsStateDb(dbSettings.DbName)
            ? new SnapshotableMemDb(dbSettings.DbName)
            : new MemDb(dbSettings.DbName);

        private static bool IsStateDb(string name)
        {
            if (!name.StartsWith(DbNames.State, StringComparison.OrdinalIgnoreCase)) return false;
            ReadOnlySpan<char> suffix = name.AsSpan(DbNames.State.Length);
            foreach (char digit in suffix)
            {
                if (digit is < '0' or > '9') return false;
            }
            return true;
        }

        public IColumnsDb<T> CreateColumnsDb<T>(DbSettings dbSettings) where T : struct, Enum => new SnapshotableMemColumnsDb<T>(dbSettings.DbName);
    }
}
