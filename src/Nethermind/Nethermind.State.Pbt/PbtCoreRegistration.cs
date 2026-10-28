// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Api;
using Nethermind.Core;
using Nethermind.Config;
using Nethermind.Api.Monitoring;
using Nethermind.State.Flat.PersistedSnapshots.Storage;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.State.Pbt.NodeBuffer;
using Nethermind.State.Pbt.PersistedSnapshots;
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
using Nethermind.State.Pbt.Snapshot;
using Nethermind.State.Pbt.Sync;
using Nethermind.Synchronization.FastSync;
using Nethermind.Synchronization.SnapSync;

namespace Nethermind.State.Pbt;

public static class PbtCoreRegistration
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
            .AddSingleton<IPbtGroupRecordBuffer, IPbtConfig>(c => new PbtGroupRecordBuffer(c.CompressedGroupRecordBufferMemoryBudget,
                PbtGroupRecordBuffer.DefaultGenerationBytes))
            .AddPbtPersisted(config)
            .AddSingleton<PbtSnapshotRepository>(ctx => new PbtSnapshotRepository(
                ctx.Resolve<IMetricsConfig>(), ctx.ResolveKeyed<ISnapshotCatalog>(DbNames.Pbt),
                ctx.Resolve<PbtPersistedPublicationGate>()))
            .AddSingleton<PbtSnapshotCompactor>()
            .AddKeyedSingleton<ICompactionSchedule>(DbNames.Pbt, ctx => CreateCompactionSchedule(ctx.ResolveKeyed<IDb>(DbNames.Metadata), config, ctx.Resolve<ILogManager>()))
            .AddSingleton<PbtPersistenceManager>(ctx => new PbtPersistenceManager(
                config, ctx.Resolve<IStateHeaderProvider>(), ctx.Resolve<IPbtPersistence>(), ctx.Resolve<PbtSnapshotRepository>(),
                ctx.ResolveKeyed<ICompactionSchedule>(DbNames.Pbt), ctx.Resolve<IStatePersistenceBarrier>(), ctx.Resolve<ILogManager>(),
                ctx.Resolve<IPbtPersistedSnapshotLoader>(), ctx.Resolve<IPbtPersistedSnapshotCompactor>(), ctx.Resolve<IProcessExitSource>().Token))
            .AddSingleton<IPbtDbManager>(ctx => new PbtDbManager(
                ctx.Resolve<PbtSnapshotRepository>(), ctx.Resolve<PbtPersistenceManager>(), ctx.Resolve<IPbtPersistence>(),
                ctx.Resolve<IPbtResourcePool>(), ctx.Resolve<IPbtGroupRecordBuffer>(), ctx.Resolve<PbtSnapshotCompactor>(), ctx.Resolve<IProcessExitSource>(),
                ctx.Resolve<ILogManager>(), ctx.Resolve<IMetricsConfig>(), ctx.Resolve<IPbtPersistedSnapshotLoader>()))
            .AddSingleton<PbtStateReader>()
            .AddSingleton<PbtWorldStateManager>()
            .Add<PbtOverridableWorldScope>()
            .AddSingleton<ISnapTrieFactory, PbtUnsupportedSnapTrieFactory>()
            .AddSingleton<ITreeSyncStore, PbtUnsupportedTreeSyncStore>()
            .AddSingleton<IPruningTrieStateAdminRpcModule, FlatWorldStateModule.PruningTrieStateAdminRpcModuleStub>()
            .AddSingleton<PbtRebuilder, PbtRocksDbPersistence, IPbtConfig, ILogManager>(
                (target, pbtConfig, logManager) => new PbtRebuilder(target, pbtConfig, pbtConfig.ImportConcurrency, logManager))
            .AddSingleton<PbtLeafIngestion>()
            .AddSingleton<PbtAnchorImport>()
            .RegisterSingletonJsonRpcModule<IMigrationDebugRpcModule, MigrationDebugRpcModule>();

    private static ContainerBuilder AddPbtPersisted(this ContainerBuilder builder, IPbtConfig config)
    {
        builder.AddSingleton<PbtPersistedPublicationGate>();
        if (!config.EnableLongFinality)
            return builder
                .AddKeyedSingleton<ISnapshotCatalog>(DbNames.Pbt, _ => NullSnapshotCatalog.Instance)
                .AddSingleton<IPbtPersistedSnapshotLoader>(NullPbtPersistedSnapshotLoader.Instance)
                .AddSingleton<IPbtPersistedSnapshotCompactor>(NullPbtPersistedSnapshotCompactor.Instance);

        return builder
            .AddKeyedSingleton<IDb>(PbtSnapshotCatalog.DatabaseKey, ctx => ctx.Resolve<IDbFactory>().CreateDb(new DbSettings(
                PbtSnapshotCatalog.DatabaseKey, Path.Combine(DbNames.Pbt, "persistedSnapshot", "catalog"))))
            .AddKeyedSingleton<ISnapshotCatalog>(DbNames.Pbt, ctx => new PbtSnapshotCatalog(ctx.ResolveKeyed<IDb>(PbtSnapshotCatalog.DatabaseKey)))
            .AddKeyedSingleton<IArenaManager>(DbNames.Pbt, ctx => new ArenaManager(
                Path.Combine(ctx.Resolve<IInitConfig>().BaseDbPath, DbNames.Pbt, "persistedSnapshot", "arena"),
                new FlatDbConfig
                {
                    ArenaFileSizeBytes = config.ArenaFileSizeBytes,
                    PersistedSnapshotDedicatedArenaThresholdBytes = config.PersistedSnapshotDedicatedArenaThresholdBytes,
                    PersistedSnapshotArenaPageCacheBytes = config.PersistedSnapshotArenaPageCacheBytes,
                    PersistedSnapshotPunchHoleOnReclaim = config.PersistedSnapshotPunchHoleOnReclaim,
                }, ctx.Resolve<ILogManager>()))
            .AddKeyedSingleton<BlobArenaManager>(DbNames.Pbt, ctx => new BlobArenaManager(
                Path.Combine(ctx.Resolve<IInitConfig>().BaseDbPath, DbNames.Pbt, "persistedSnapshot", "blob"), config.ArenaFileSizeBytes))
            .AddSingleton<IPbtPersistedSnapshotLoader, PbtPersistedSnapshotLoader>()
            .AddSingleton<IPbtPersistedSnapshotCompactor, PbtPersistedSnapshotCompactor>();
    }

    /// <summary>Builds the flat compaction schedule over the PBT compaction settings.</summary>
    /// <remarks>The offset is stored under flat's metadata key, so a node running both backends compacts both on the same boundaries.</remarks>
    public static ICompactionSchedule CreateCompactionSchedule(IDb metadataDb, IPbtConfig config, ILogManager logManager) =>
        new CompactionSchedule(metadataDb, new FlatDbConfig { CompactSize = (ulong)config.CompactSize, CompactionOffset = config.CompactionOffset, RegenerateCompactionOffset = config.RegenerateCompactionOffset, PersistedSnapshotMaxCompactSize = config.PersistedSnapshotMaxCompactSize }, logManager);
}
