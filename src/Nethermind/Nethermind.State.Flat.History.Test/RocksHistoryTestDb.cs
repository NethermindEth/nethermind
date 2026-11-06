// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Core;
using Nethermind.Core.Test.Modules;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Logging;
using Testably.Abstractions;

namespace Nethermind.State.Flat.History.Test;

/// <summary>Native RocksDB history stores for tests, with the production settings.</summary>
internal static class RocksHistoryTestDb
{
    public static DbOnTheRocks CreateDb(string path)
    {
        using IContainer configs = CreateConfigs();
        return (DbOnTheRocks)CreateFactory(path, configs.Resolve<IDbConfig>(), configs.Resolve<IRocksDbConfigFactory>())
            .CreateDb(new DbSettings("Blocks", "history"));
    }

    public static ColumnsDb<FlatHistoryColumns> CreateColumns(string path)
    {
        using IContainer configs = CreateConfigs();
        IDbConfig config = configs.Resolve<IDbConfig>();
        // The registered factory validates per-column options in Debug, and IDbConfig defines none for most history columns.
        RocksDbConfigFactory factory = new(config, configs.Resolve<IPruningConfig>(), configs.Resolve<IHardwareInfo>(),
            LimboLogs.Instance, validateConfig: false);
        return (ColumnsDb<FlatHistoryColumns>)CreateFactory(path, config, factory)
            .CreateColumnsDb<FlatHistoryColumns>(new DbSettings("FlatHistory", "history"));
    }

    private static RocksDbFactory CreateFactory(string path, IDbConfig config, IRocksDbConfigFactory configFactory) =>
        new(configFactory, config, new HyperClockCacheWrapper(), new RealFileSystem(), LimboLogs.Instance, path);

    private static IContainer CreateConfigs() => new ContainerBuilder().AddModule(new TestNethermindModule()).Build();
}
