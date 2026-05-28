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
using Nethermind.State.Flat;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.State.Flat.ScopeProvider;
using Nethermind.State.Flat.PersistedSnapshots.Storage;
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
                ctx.Resolve<IPersistedSnapshotRepository>(),
                ctx.Resolve<PersistedSnapshotBloomFilterManager>()))
            .AddSingleton<PersistedSnapshotBloomFilterManager>()
            .AddSingleton<IResourcePool, ResourcePool>()
            .AddSingleton<ITrieNodeCache, TrieNodeCache>()
            .AddSingleton<ICompactionSchedule, CompactionSchedule>()
            .AddSingleton<ISnapshotCompactor, SnapshotCompactor>()
            .AddSingleton<IPersistenceManager, PersistenceManager>()
            // Shared ArenaManager + BlobArenaManager: the persisted-snapshot repo and the
            // compactor MUST resolve the same instances, otherwise compaction would write
            // through a different mmap than the repository reads from. Registering them
            // here as singletons keeps both consumers naturally on the same instance and
            // lets IPersistedSnapshotRepository / IPersistedSnapshotCompactor be registered
            // separately below.
            //
            // EnableLongFinality off: arena/blob construction is skipped and the Null
            // impls of repo/compactor are returned. The ArenaManager / BlobArenaManager
            // singletons are still registered but never actually resolved in that mode
            // (the Null impls don't reach them).
            .AddSingleton<ArenaManager, IFlatDbConfig, IInitConfig>((cfg, initConfig) =>
            {
                string basePath = Path.Combine(initConfig.BaseDbPath, "persisted_snapshot");
                return new ArenaManager(
                    Path.Combine(basePath, "arena"),
                    cfg.PersistedSnapshotArenaPageCacheBytes,
                    cfg.ArenaFileSizeBytes,
                    cfg.PersistedSnapshotFadviseOnPageEviction,
                    tier: PersistedSnapshotTier.Persisted,
                    punchHoleOnReclaim: cfg.PersistedSnapshotPunchHoleOnReclaim);
            })
            .AddSingleton<BlobArenaManager, IFlatDbConfig, IInitConfig>((cfg, initConfig) =>
            {
                string basePath = Path.Combine(initConfig.BaseDbPath, "persisted_snapshot");
                return new BlobArenaManager(
                    Path.Combine(basePath, "blob"),
                    cfg.ArenaFileSizeBytes,
                    PersistedSnapshotTier.Persisted,
                    punchHoleOnReclaim: cfg.PersistedSnapshotPunchHoleOnReclaim);
            })
            .AddSingleton<IPersistedSnapshotRepository>((ctx) =>
            {
                IFlatDbConfig cfg = ctx.Resolve<IFlatDbConfig>();
                // Feature flag off: skip arena / blob / catalog construction entirely and
                // wire a null implementation. Conversion paths in PersistenceManager.
                // DetermineSnapshotAction are also gated on this flag, so no
                // ConvertSnapshotToPersistedSnapshot call will ever reach the repo — this
                // guarantees no on-disk artefacts under `<data-dir>/persisted_snapshot/`.
                if (!cfg.EnableLongFinality) return NullPersistedSnapshotRepository.Instance;

                IColumnsDb<PersistedSnapshotCatalogColumns> catalogColumns =
                    ctx.Resolve<IColumnsDb<PersistedSnapshotCatalogColumns>>();
                IDb catalogDb = catalogColumns.GetColumnDb(PersistedSnapshotCatalogColumns.Catalog);
                PersistedSnapshotRepository repo = new(
                    ctx.Resolve<ArenaManager>(),
                    ctx.Resolve<BlobArenaManager>(),
                    catalogDb, cfg,
                    ctx.Resolve<PersistedSnapshotBloomFilterManager>());
                repo.LoadFromCatalog();
                return repo;
            })
            .AddSingleton<IPersistedSnapshotCompactor>((ctx) =>
            {
                IFlatDbConfig cfg = ctx.Resolve<IFlatDbConfig>();
                if (!cfg.EnableLongFinality) return NullPersistedSnapshotCompactor.Instance;

                return new PersistedSnapshotCompactor(
                    ctx.Resolve<IPersistedSnapshotRepository>(),
                    ctx.Resolve<ArenaManager>(),
                    cfg,
                    ctx.Resolve<ICompactionSchedule>(),
                    ctx.Resolve<ILogManager>(),
                    ctx.Resolve<PersistedSnapshotBloomFilterManager>(),
                    minCompactSize: cfg.MinCompactSize,
                    maxCompactSize: cfg.PersistedSnapshotMaxCompactSize);
            })
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
