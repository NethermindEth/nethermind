// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Api;
using Nethermind.Core;
using Nethermind.Core.Memory;
using Nethermind.Db;
using Nethermind.Db.Rocks.Config;
using Nethermind.Init.Modules;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.Admin;
using Nethermind.Logging;
using Nethermind.State.Flat;
using Nethermind.State.Pbt.Image;
using Nethermind.State.Pbt.Migration;
using Nethermind.State.Pbt.Persistence;
using Nethermind.State.Pbt.Persistence.TrieNodeLog;
using Nethermind.State.Pbt.ScopeProvider;
using Nethermind.State.Pbt.Sync;
using Nethermind.Synchronization.FastSync;
using Nethermind.Synchronization.SnapSync;

namespace Nethermind.State.Pbt;

internal static class PbtCoreRegistration
{
    /// <summary>Registers the PBT state graph shared by the native and migration modes.</summary>
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
            .AddKeyedSingleton<ICompactionSchedule>(DbNames.Pbt, ctx => CreateCompactionSchedule(ctx.ResolveKeyed<IDb>(DbNames.Metadata), config, ctx.Resolve<ILogManager>()))
            .AddSingleton<PbtPersistenceCoordinator>()
            .AddSingleton<IPbtDbManager, PbtDbManager>()
            .AddSingleton<PbtStateReader>()
            .AddSingleton<PbtWorldStateManager>()
            .Add<PbtOverridableWorldScope>()
            .AddSingleton<ISnapTrieFactory, PbtUnsupportedSnapTrieFactory>()
            .AddSingleton<ITreeSyncStore, PbtUnsupportedTreeSyncStore>()
            .AddSingleton<IPruningTrieStateAdminRpcModule, FlatWorldStateModule.PruningTrieStateAdminRpcModuleStub>()
            .AddSingleton<PbtAnchorImport>()
            .RegisterSingletonJsonRpcModule<IMigrationDebugRpcModule, MigrationDebugRpcModule>();

    /// <summary>Builds the flat compaction schedule over the PBT compaction settings.</summary>
    /// <remarks>The offset is stored under flat's metadata key, so a node running both backends compacts both on the same boundaries.</remarks>
    public static ICompactionSchedule CreateCompactionSchedule(IDb metadataDb, IPbtConfig config, ILogManager logManager) =>
        new CompactionSchedule(metadataDb, new FlatDbConfig { CompactSize = (ulong)config.CompactSize, CompactionOffset = config.CompactionOffset }, logManager);
}
