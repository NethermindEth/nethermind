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
                ctx.Resolve<PersistedSnapshotRepositories>(),
                ctx.Resolve<PersistedSnapshotBloomFilterManager>()))
            .AddSingleton<PersistedSnapshotBloomFilterManager>()
            .AddSingleton<IResourcePool, ResourcePool>()
            .AddSingleton<ITrieNodeCache, TrieNodeCache>()
            .AddSingleton<ISnapshotCompactor, SnapshotCompactor>()
            .AddSingleton<IPersistenceManager, PersistenceManager>()
            // Each (ArenaManager, BlobArenaManager, BlobArenaCatalog, PersistedSnapshotRepository,
            // PersistedSnapshotCompactor) set is built per tier in a single factory so both the
            // repo and the compactor share the same ArenaManager instance. Tiers are
            // independent — small and large each own their own catalogs and file pools;
            // snapshots only resolve NodeRefs through their own repo's blob manager.
            .AddSingleton<PerTierState>((ctx) =>
            {
                IFlatDbConfig cfg = ctx.Resolve<IFlatDbConfig>();
                ILogManager logManager = ctx.Resolve<ILogManager>();
                string basePath = Path.Combine(ctx.Resolve<IInitConfig>().BaseDbPath, "persisted_snapshot");
                IColumnsDb<FlatDbColumns> columns = ctx.Resolve<IColumnsDb<FlatDbColumns>>();
                // Shared across both tiers. A per-tier split would let a stale narrow bloom
                // in one tier under-cover a wider compacted snapshot leased from the other
                // tier, producing silent false negatives on bundle reads (see FlatDbManager.GatherSnapshots).
                PersistedSnapshotBloomFilterManager bloomManager = ctx.Resolve<PersistedSnapshotBloomFilterManager>();

                ArenaManager smallArena = new(Path.Combine(basePath, "small", "arena"), cfg.PersistedSnapshotSmallArenaPageCacheBytes, cfg.ArenaFileSizeBytes, cfg.PersistedSnapshotFadviseOnPageEviction);
                BlobArenaCatalog smallBlobCatalog = new(columns.GetColumnDb(FlatDbColumns.SmallBlobArenaCatalog));
                BlobArenaManager smallBlobs = new(Path.Combine(basePath, "small", "blob"), cfg.ArenaFileSizeBytes, smallBlobCatalog, ArenaReservationTags.BlobSmall);
                IDb smallCatalogDb = columns.GetColumnDb(FlatDbColumns.SmallPersistedSnapshotCatalog);
                PersistedSnapshotRepository smallRepo = new(smallArena, smallBlobs, smallBlobCatalog, smallCatalogDb, cfg, bloomManager, ArenaReservationTags.BlobBackedSmall, ArenaReservationTags.BlobSmall);
                PersistedSnapshotCompactor smallCompactor = new(
                    smallRepo, smallArena, cfg, logManager, bloomManager,
                    minCompactSize: cfg.MinCompactSize,
                    maxCompactSize: cfg.CompactSize / 2,
                    tierLabel: "small",
                    reservationTag: ArenaReservationTags.BlobBackedSmall);

                ArenaManager largeArena = new(Path.Combine(basePath, "large", "arena"), cfg.PersistedSnapshotLargeArenaPageCacheBytes, cfg.ArenaFileSizeBytes, cfg.PersistedSnapshotFadviseOnPageEviction);
                BlobArenaCatalog largeBlobCatalog = new(columns.GetColumnDb(FlatDbColumns.LargeBlobArenaCatalog));
                BlobArenaManager largeBlobs = new(Path.Combine(basePath, "large", "blob"), cfg.ArenaFileSizeBytes, largeBlobCatalog, ArenaReservationTags.BlobLarge);
                IDb largeCatalogDb = columns.GetColumnDb(FlatDbColumns.LargePersistedSnapshotCatalog);
                PersistedSnapshotRepository largeRepo = new(largeArena, largeBlobs, largeBlobCatalog, largeCatalogDb, cfg, bloomManager, ArenaReservationTags.BlobBackedLarge, ArenaReservationTags.BlobLarge);
                PersistedSnapshotCompactor largeCompactor = new(
                    largeRepo, largeArena, cfg, logManager, bloomManager,
                    minCompactSize: cfg.CompactSize * 2,
                    maxCompactSize: cfg.PersistedSnapshotMaxCompactSize,
                    tierLabel: "large",
                    reservationTag: ArenaReservationTags.BlobBackedLarge);

                smallRepo.LoadFromCatalog();
                largeRepo.LoadFromCatalog();
                return new PerTierState(
                    new PersistedSnapshotRepositories(smallRepo, largeRepo),
                    new PersistedSnapshotCompactors(smallCompactor, largeCompactor));
            })
            .AddSingleton<PersistedSnapshotRepositories>((ctx) => ctx.Resolve<PerTierState>().Repositories)
            .AddSingleton<PersistedSnapshotCompactors>((ctx) => ctx.Resolve<PerTierState>().Compactors)
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
