// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using Autofac;
using Nethermind.Api.Steps;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.FullPruning;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Db.Rocks.Config;
using Nethermind.Init.Steps;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules.Admin;
using Nethermind.Logging;
using Nethermind.Monitoring.Config;
using Nethermind.Api;
using Nethermind.State;
using Nethermind.State.Flat;
using Nethermind.State.SnapServer;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.State.Flat.ScopeProvider;
using Nethermind.State.Flat.Storage;
using Nethermind.State.Flat.Sync;
using Nethermind.State.Flat.Sync.Snap;
using Nethermind.Synchronization.FastSync;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.SnapSync;

namespace Nethermind.Init.Modules;

public class FlatWorldStateModule(IFlatDbConfig flatDbConfig) : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        builder

            // Implementation of nethermind interfaces
            .AddSingleton<IWorldStateManager, FlatWorldStateManager>()
            .OnActivate<IWorldStateManager>((worldStateManager, ctx) =>
            {
                new TrieStoreBoundaryWatcher(worldStateManager, ctx.Resolve<IBlockTree>(), ctx.Resolve<ILogManager>());
            })
            .AddSingleton<IStateReader, FlatStateReader>()
            .AddSingleton<ISnapServer, IWorldStateManager>(wsm => wsm.SnapServer)

            // Disable some pruning trie store specific  components
            .AddSingleton<IPruningTrieStateAdminRpcModule, PruningTrieStateAdminRpcModuleStub>()
            .AddSingleton<MainPruningTrieStoreFactory>(_ => throw new NotSupportedException($"{nameof(MainPruningTrieStoreFactory)} disabled."))
            .AddSingleton<PruningTrieStateFactory>(_ => throw new NotSupportedException($"{nameof(PruningTrieStateFactory)} disabled."))
            .AddSingleton<CompositePruningTrigger>(_ => throw new NotSupportedException($"{nameof(CompositePruningTrigger)} disabled."))
            .AddSingleton<IFullPrunerFactory>(_ => throw new NotSupportedException($"{nameof(IFullPrunerFactory)} disabled."))

            // The actual flatDb components
            .AddSingleton<IFlatDbManager>((ctx) => new FlatDbManager(
                ctx.Resolve<IResourcePool>(),
                ctx.Resolve<IProcessExitSource>(),
                ctx.Resolve<ITrieNodeCache>(),
                ctx.Resolve<ISnapshotCompactor>(),
                ctx.Resolve<ISnapshotRepository>(),
                ctx.Resolve<IPersistenceManager>(),
                ctx.Resolve<IFlatDbConfig>(),
                ctx.Resolve<IBlocksConfig>(),
                ctx.Resolve<ILogManager>(),
                ctx.Resolve<IMetricsConfig>().EnableDetailedMetric,
                ctx.Resolve<IPersistedSnapshotRepository>()))
            .AddSingleton<IResourcePool, ResourcePool>()
            .AddSingleton<ITrieNodeCache, TrieNodeCache>()
            .AddSingleton<ISnapshotCompactor, SnapshotCompactor>()
            .AddSingleton<IPersistenceManager, PersistenceManager>()
            // Single shared page tracker — its slot key already namespaces by arenaId
            // (`(arenaId << 32) | pageIdx`), so one tracker correctly partitions the
            // configured byte budget between the compacted and base arenas instead of
            // each arena getting its own full budget.
            .AddSingleton<PageResidencyTracker>((ctx) =>
                PageResidencyTracker.FromByteBudget(ctx.Resolve<IFlatDbConfig>().PersistedSnapshotPageCacheBytes))
            .AddSingleton<IArenaManager>((ctx) =>
            {
                IFlatDbConfig cfg = ctx.Resolve<IFlatDbConfig>();
                string basePath = Path.Combine(ctx.Resolve<IInitConfig>().BaseDbPath, "persisted_snapshots");
                return new ArenaManager(Path.Combine(basePath, "arenas", "compacted"), ctx.Resolve<PageResidencyTracker>(), cfg.ArenaFileSizeBytes, cfg.PersistedSnapshotFadviseOnPageEviction);
            })
            .AddSingleton<IPersistedSnapshotRepository>((ctx) =>
            {
                IFlatDbConfig cfg = ctx.Resolve<IFlatDbConfig>();
                string basePath = Path.Combine(ctx.Resolve<IInitConfig>().BaseDbPath, "persisted_snapshots");
                ArenaManager baseArena = new(Path.Combine(basePath, "arenas"), ctx.Resolve<PageResidencyTracker>(), cfg.ArenaFileSizeBytes, cfg.PersistedSnapshotFadviseOnPageEviction);
                IArenaManager compactedArena = ctx.Resolve<IArenaManager>();
                IDb catalogDb = ctx.Resolve<IColumnsDb<FlatDbColumns>>().GetColumnDb(FlatDbColumns.PersistedSnapshotCatalog);
                PersistedSnapshotRepository repo = new(baseArena, compactedArena, catalogDb, cfg);
                repo.LoadFromCatalog();
                return repo;
            })
            .AddSingleton<IPersistedSnapshotCompactor, PersistedSnapshotCompactor>()
            .AddSingleton<ISnapshotRepository, SnapshotRepository>()
            .AddSingleton<ITrieWarmer>(flatDbConfig.TrieWarmerWorkerCount == 0
                ? _ => new NoopTrieWarmer()
                : ctx => ctx.Resolve<TrieWarmer>())
            .AddSingleton<TrieWarmer>()
            .Add<FlatOverridableWorldScope>()

            // Sync components
            .AddSingleton<ISnapTrieFactory, FlatSnapTrieFactory>()
            .AddSingleton<IFlatStateRootIndex>((ctx) => new FlatStateRootIndex(
                ctx.Resolve<IBlockTree>(),
                ctx.Resolve<ISyncConfig>().SnapServingMaxDepth))
            .AddSingleton<ITreeSyncStore, FlatTreeSyncStore>()
            .Intercept<ISyncConfig>((syncConfig) =>
            {
                syncConfig.SnapServingEnabled ??= true;
            })
            .AddSingleton<IFullStateFinder, FlatFullStateFinder>()

            // Persistences
            .AddColumnDatabase<FlatDbColumns>(DbNames.Flat)
            .AddSingleton<RocksDbPersistence>()
            .AddSingleton<FlatInTriePersistence>()
            .AddDecorator<IRocksDbConfigFactory, FlatRocksDbConfigAdjuster>()

            .AddSingleton<PreimageRocksdbPersistence>()
            .AddDatabase(DbNames.Preimage)

            .AddSingleton<IPersistence, IFlatDbConfig, IProcessExitSource, ILogManager, IComponentContext>((flatDbConfig, exitSource, logManager, ctx) =>
            {
                IPersistence persistence = flatDbConfig.Layout switch
                {
                    FlatLayout.Flat => ctx.Resolve<RocksDbPersistence>(),
                    FlatLayout.FlatInTrie => ctx.Resolve<FlatInTriePersistence>(),
                    FlatLayout.PreimageFlat => ctx.Resolve<PreimageRocksdbPersistence>(),
                    _ => throw new NotSupportedException($"Unsupported layout {flatDbConfig.Layout}")
                };

                if (flatDbConfig.EnablePreimageRecording)
                {
                    IDb preimageDb = ctx.ResolveKeyed<IDb>(DbNames.Preimage);
                    persistence = new PreimageRecordingPersistence(persistence, preimageDb);
                }

                return new CachedReaderPersistence(persistence, exitSource, logManager);
            })
            ;

        if (flatDbConfig.ImportFromPruningTrieState)
        {
            builder
                .AddSingleton<Importer>()
                .AddStep(typeof(ImportFlatDb));
        }
    }

    /// <summary>
    /// Need to stub out, or it will register trie store specific module
    /// </summary>
    private class PruningTrieStateAdminRpcModuleStub : IPruningTrieStateAdminRpcModule
    {
        public ResultWrapper<PruningStatus> admin_prune() => ResultWrapper<PruningStatus>.Success(PruningStatus.Disabled);

        public ResultWrapper<string> admin_verifyTrie(BlockParameter block) => ResultWrapper<string>.Success("disable");
    }
}
