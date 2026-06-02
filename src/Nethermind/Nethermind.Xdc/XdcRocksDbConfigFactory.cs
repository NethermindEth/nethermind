// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Db.Rocks.Config;

namespace Nethermind.Xdc;

/// <summary>
/// A custom RocksDb config factory for XDC that bypasses validation for the XdcSnapshots database.
/// This is necessary because XdcSnapshots doesn't have dedicated RocksDb options in the config.
/// </summary>
public class XdcRocksDbConfigFactory(IRocksDbConfigFactory baseFactory, IDbConfig dbConfig) : IRocksDbConfigFactory
{
    public const string XdcSnapshotDbName = "XdcSnapshots";

    public IRocksDbConfig GetForDatabase(string databaseName, string? columnName)
    {
        // For XdcSnapshots, create PerTableDbConfig with validate=false since it doesn't have
        // XdcSnapshotsDbRocksDbOptions configured in IDbConfig
        if (databaseName == XdcSnapshotDbName)
        {
            return new PerTableDbConfig(dbConfig, databaseName, columnName, validate: false);
        }

        return baseFactory.GetForDatabase(databaseName, columnName);
    }
}
