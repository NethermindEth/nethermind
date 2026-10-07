// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Api;
using Nethermind.Core;
using Nethermind.Core.Memory;
using Nethermind.Db;
using Nethermind.Db.Rocks.Config;
using Nethermind.Init.Modules;
using Nethermind.Logging;
using Nethermind.State.Pbt.Persistence;
using Nethermind.State.Pbt.Persistence.TrieNodeLog;

namespace Nethermind.State.Pbt;

internal static class PbtCoreRegistration
{
    /// <summary>Registers the PBT persistence and layer management shared by the native, mirror and migration modes.</summary>
    public static ContainerBuilder AddPbtCore(this ContainerBuilder builder, IPbtConfig config) =>
        builder
            .AddColumnDatabase<PbtColumns>(DbNames.Pbt)
            .AddDecorator<IRocksDbConfigFactory, PbtRocksDbConfigAdjuster>()
            .AddSingleton<ITrieNodeLog, IPbtConfig, IInitConfig, IColumnsDb<PbtColumns>, ILogManager>(TrieNodeLog.Create)
            .AddSingleton<IPbtPersistence, PbtRocksDbPersistence>()
            .AddDecorator<IPbtPersistence, PbtCachedReaderPersistence>()
            // A second pool would halve each pool's hit rate.
            .AddSingleton<IPbtResourcePool, PbtResourcePool>()
            .AddSingleton<IRefCountingMemoryProvider>(PbtNodeGroupMemory.CreateProvider(config))
            .AddSingleton<IPbtTrieNodeCache, PbtTrieNodeCache>()
            .AddSingleton<PbtSnapshotRepository>()
            .AddSingleton<PbtSnapshotCompactor>()
            .AddSingleton<PbtCompactionSchedule>()
            .AddSingleton<PbtPersistenceCoordinator>()
            .AddSingleton<IPbtDbManager, PbtDbManager>();
}
