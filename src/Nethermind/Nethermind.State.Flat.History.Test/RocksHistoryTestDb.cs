// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Test;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Logging;

namespace Nethermind.State.Flat.History.Test;

/// <summary>Native RocksDB history stores for tests, with default settings.</summary>
internal static class RocksHistoryTestDb
{
    public static DbOnTheRocks CreateDb(string path)
    {
        (DbConfig config, RocksDbConfigFactory factory) = Settings();
        return new DbOnTheRocks(path, new DbSettings("Blocks", "history"), config, factory, LimboLogs.Instance);
    }

    public static ColumnsDb<FlatHistoryColumns> CreateColumns(string path)
    {
        (DbConfig config, RocksDbConfigFactory factory) = Settings();
        return new ColumnsDb<FlatHistoryColumns>(path, new DbSettings("FlatHistory", "history"), config, factory,
            LimboLogs.Instance, Enum.GetValues<FlatHistoryColumns>());
    }

    private static (DbConfig, RocksDbConfigFactory) Settings()
    {
        DbConfig config = new();
        return (config, new RocksDbConfigFactory(config, new PruningConfig(), new TestHardwareInfo(), LimboLogs.Instance, validateConfig: false));
    }
}
