// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Db
{
    public interface IMemDbFactory
    {
        IDb CreateDb(string dbName);

        IColumnsDb<T> CreateColumnsDb<T>(string dbName);
    }
}
