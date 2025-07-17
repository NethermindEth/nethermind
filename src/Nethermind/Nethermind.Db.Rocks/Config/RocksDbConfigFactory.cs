// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Db.Rocks.Config;

public class RocksDbConfigFactory(IDbConfig dbConfig) : IRocksDbConfigFactory
{
    public IRocksDbConfig GetForDatabase(string databaseName, string? columnName)
    {
        return new PerTableDbConfig(dbConfig, databaseName, columnName);
    }
}
