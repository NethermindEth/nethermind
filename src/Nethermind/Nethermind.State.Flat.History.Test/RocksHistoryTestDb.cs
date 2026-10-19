// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using Nethermind.Core;
using Nethermind.Core.Test.Modules;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Logging;

namespace Nethermind.State.Flat.History.Test;

/// <summary>Native RocksDB history stores for tests, with the production settings.</summary>
internal static class RocksHistoryTestDb
{
    public static DbOnTheRocks CreateDb(string path)
    {
        using IContainer configs = CreateConfigs();
        return new DbOnTheRocks(path, new DbSettings("Blocks", "history"), configs.Resolve<IDbConfig>(),
            configs.Resolve<IRocksDbConfigFactory>(), LimboLogs.Instance);
    }

    public static ColumnsDb<FlatHistoryColumns> CreateColumns(string path)
    {
        using IContainer configs = CreateConfigs();
        IDbConfig config = configs.Resolve<IDbConfig>();
        // The registered factory validates per-column options in Debug, and IDbConfig defines none for most history columns.
        RocksDbConfigFactory factory = new(config, configs.Resolve<IPruningConfig>(), configs.Resolve<IHardwareInfo>(),
            LimboLogs.Instance, validateConfig: false);
        return new ColumnsDb<FlatHistoryColumns>(path, new DbSettings("FlatHistory", "history"), config, factory,
            LimboLogs.Instance, Enum.GetValues<FlatHistoryColumns>());
    }

    private static IContainer CreateConfigs() => new ContainerBuilder().AddModule(new TestNethermindModule()).Build();
}
