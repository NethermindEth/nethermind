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
                ctx.Resolve<IPersistedSnapshotLoader>(),
                ctx.Resolve<IFlatDbConfig>(),
                ctx.Resolve<IBlocksConfig>(),
                ctx.Resolve<ILogManager>(),
                ctx.Resolve<IMetricsConfig>().EnableDetailedMetric))
            .AddSingleton<IResourcePool, ResourcePool>()
            .AddSingleton<ITrieNodeCache, TrieNodeCache>()
            .AddSingleton<ICompactionSchedule, CompactionSchedule>()
            .AddSingleton<ISnapshotCompactor, SnapshotCompactor>()
            .AddSingleton<IPersistenceManager, PersistenceManager>()
            // Shared ArenaManager + BlobArenaManager singletons: the persisted-snapshot repo and
            // the compactor MUST resolve the same instances, otherwise compaction would write
            // through a different mmap than the repository reads from.
            .AddSingleton<ArenaManager, IFlatDbConfig, IInitConfig, ILogManager>((cfg, initConfig, logManager) =>
            {
                string basePath = Path.Combine(initConfig.BaseDbPath, "persisted_snapshot");
                return new ArenaManager(Path.Combine(basePath, "arena"), cfg, logManager);
            })
            .AddSingleton<IArenaManager>(ctx => ctx.Resolve<ArenaManager>())
            .AddSingleton<BlobArenaManager, IFlatDbConfig, IInitConfig>((cfg, initConfig) =>
            {
                string basePath = Path.Combine(initConfig.BaseDbPath, "persisted_snapshot");
                return new BlobArenaManager(
                    Path.Combine(basePath, "blob"),
                    cfg.ArenaFileSizeBytes);
            })
            .AddSingleton<IPersistedSnapshotCompactor, PersistedSnapshotCompactor>()
            .AddSingleton<ISnapshotRepository, SnapshotRepository>()
            // Loads the persisted tier from the catalog at startup (driven by FlatDbManager) and owns
            // its teardown; depends on ISnapshotRepository so DI disposes it before the repository.
            .AddSingleton<IPersistedSnapshotLoader, PersistedSnapshotLoader>()
            // Owns the build half of in-memory -> persisted base conversion; resolves the same shared
            // arena/blob singletons the repository reads through.
            .AddSingleton<IPersistedSnapshotConverter, PersistedSnapshotConverter>()
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
            .AddKeyedSingleton<IDb>(DbNames.PersistedSnapshotCatalog, ctx =>
                ctx.Resolve<IColumnsDb<PersistedSnapshotCatalogColumns>>().GetColumnDb(PersistedSnapshotCatalogColumns.Catalog))
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

        // EnableLongFinality off: swap in the Null compactor so no background compaction runs.
        // The conversion paths in PersistenceManager.DetermineSnapshotAction are also gated on this
        // flag, so the persisted tier stays empty — though SnapshotRepository still constructs its
        // persisted-tier arena/blob/catalog stores under `<data-dir>/persisted_snapshot/`.
        if (!flatDbConfig.EnableLongFinality)
        {
            builder.AddSingleton<IPersistedSnapshotCompactor>(NullPersistedSnapshotCompactor.Instance);
        }

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
