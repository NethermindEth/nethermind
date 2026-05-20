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
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.State.Flat.ScopeProvider;
using Nethermind.State.Flat.Storage;
using Nethermind.State.Flat.Sync;
using Nethermind.State.Flat.Sync.Snap;

namespace Nethermind.Init.Modules;

public class FlatWorldStateModule(IFlatDbConfig flatDbConfig) : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        builder

            // Implementation of nethermind interfaces
            .AddSingleton<FlatStateReader>()
            .AddSingleton<FlatWorldStateManager>()

            // Stub out the pruning trie store admin RPC with a disabled response.
            .AddSingleton<PruningTrieStateAdminRpcModuleStub>()

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
            // Each (ArenaManager, BlobArenaManager, PersistedSnapshotRepository,
            // PersistedSnapshotCompactor) set is built per tier in a single factory so both the
            // repo and the compactor share the same ArenaManager instance. Tiers are
            // independent — small and large each own their own catalog and file pools;
            // snapshots only resolve NodeRefs through their own repo's blob manager.
            .AddSingleton<PerTierState>((ctx) =>
            {
                IFlatDbConfig cfg = ctx.Resolve<IFlatDbConfig>();

                // Feature flag off: skip arena / blob / catalog construction entirely and wire
                // null implementations. Conversion paths in PersistenceManager.DetermineSnapshotAction
                // are also gated on this flag, so no ConvertSnapshotToPersistedSnapshot call will
                // ever reach the repo — this guarantees no on-disk artefacts under
                // `<data-dir>/persisted_snapshot/`.
                if (!cfg.EnableLongFinality)
                {
                    return new PerTierState(
                        new PersistedSnapshotRepositories(NullPersistedSnapshotRepository.Instance, NullPersistedSnapshotRepository.Instance),
                        new PersistedSnapshotCompactors(NullPersistedSnapshotCompactor.Instance, NullPersistedSnapshotCompactor.Instance));
                }

                ILogManager logManager = ctx.Resolve<ILogManager>();
                string basePath = Path.Combine(ctx.Resolve<IInitConfig>().BaseDbPath, "persisted_snapshot");
                IColumnsDb<PersistedSnapshotCatalogColumns> catalogColumns =
                    ctx.Resolve<IColumnsDb<PersistedSnapshotCatalogColumns>>();
                // Shared across both tiers. A per-tier split would let a stale narrow bloom
                // in one tier under-cover a wider compacted snapshot leased from the other
                // tier, producing silent false negatives on bundle reads (see FlatDbManager.GatherSnapshots).
                PersistedSnapshotBloomFilterManager bloomManager = ctx.Resolve<PersistedSnapshotBloomFilterManager>();

                ArenaManager smallArena = new(Path.Combine(basePath, "small", "arena"), cfg.PersistedSnapshotSmallArenaPageCacheBytes, cfg.ArenaFileSizeBytes, cfg.PersistedSnapshotFadviseOnPageEviction, tier: PersistedSnapshotTier.Small, punchHoleOnReclaim: cfg.PersistedSnapshotPunchHoleOnReclaim);
                BlobArenaManager smallBlobs = new(Path.Combine(basePath, "small", "blob"), cfg.ArenaFileSizeBytes, PersistedSnapshotTier.Small, punchHoleOnReclaim: cfg.PersistedSnapshotPunchHoleOnReclaim);
                IDb smallCatalogDb = catalogColumns.GetColumnDb(PersistedSnapshotCatalogColumns.Small);
                PersistedSnapshotRepository smallRepo = new(smallArena, smallBlobs, smallCatalogDb, cfg, bloomManager);
                PersistedSnapshotCompactor smallCompactor = new(
                    smallRepo, smallArena, cfg, logManager, bloomManager,
                    minCompactSize: cfg.MinCompactSize,
                    maxCompactSize: cfg.CompactSize / 2,
                    tier: PersistedSnapshotTier.Small);

                ArenaManager largeArena = new(Path.Combine(basePath, "large", "arena"), cfg.PersistedSnapshotLargeArenaPageCacheBytes, cfg.ArenaFileSizeBytes, cfg.PersistedSnapshotFadviseOnPageEviction, tier: PersistedSnapshotTier.Large, punchHoleOnReclaim: cfg.PersistedSnapshotPunchHoleOnReclaim);
                BlobArenaManager largeBlobs = new(Path.Combine(basePath, "large", "blob"), cfg.ArenaFileSizeBytes, PersistedSnapshotTier.Large, punchHoleOnReclaim: cfg.PersistedSnapshotPunchHoleOnReclaim);
                IDb largeCatalogDb = catalogColumns.GetColumnDb(PersistedSnapshotCatalogColumns.Large);
                PersistedSnapshotRepository largeRepo = new(largeArena, largeBlobs, largeCatalogDb, cfg, bloomManager);
                PersistedSnapshotCompactor largeCompactor = new(
                    largeRepo, largeArena, cfg, logManager, bloomManager,
                    minCompactSize: cfg.CompactSize * 2,
                    maxCompactSize: cfg.PersistedSnapshotMaxCompactSize,
                    tier: PersistedSnapshotTier.Large);

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
            .AddSingleton<FlatSnapTrieFactory>()
            .AddSingleton<IFlatStateRootIndex>((ctx) => new FlatStateRootIndex(
                ctx.Resolve<IBlockTree>(),
                ctx.Resolve<ISyncConfig>().SnapServingMaxDepth))
            .AddSingleton<FlatTreeSyncStore>()
            .AddSingleton<FlatFullStateFinder>()

            // Persistences
            .AddColumnDatabase<FlatDbColumns>(DbNames.Flat)
            // Persisted snapshot catalog: dedicated columned RocksDB co-located with the
            // arena/blob files it indexes under <BaseDbPath>/persisted_snapshot/catalog/.
            // Wiping persisted_snapshot/ therefore wipes the catalog alongside the data.
            .AddSingleton<IColumnsDb<PersistedSnapshotCatalogColumns>>((ctx) => ctx
                .Resolve<IDbFactory>()
                .CreateColumnsDb<PersistedSnapshotCatalogColumns>(new DbSettings(
                    nameof(DbNames.PersistedSnapshotCatalog),
                    Path.Combine("persisted_snapshot", "catalog"))))
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

    internal class PruningTrieStateAdminRpcModuleStub : IPruningTrieStateAdminRpcModule
    {
        public ResultWrapper<PruningStatus> admin_prune() => ResultWrapper<PruningStatus>.Success(PruningStatus.Disabled);

        public ResultWrapper<string> admin_verifyTrie(BlockParameter block) => ResultWrapper<string>.Success("disabled");
    }
}
