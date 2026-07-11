// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using Autofac;
using Nethermind.Api.Steps;
using Nethermind.Blockchain;
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
        // Warmer wiring:
        //   â€¢ TrieWarmerWorkerCount == 0 â†’ user explicitly disabled warming.
        //   â€¢ SparseTrieWarmer == None â†’ user explicitly disabled warming.
        //   â€¢ Legacy / SparseProof / default â†’ run the warmer. Even in sparse-authoritative
        //     mode the Legacy walker is NOT pure waste: it loads Patricia nodes through
        //     ReadOnlySnapshotBundle / flat-DB columns and so primes the OS page cache for
        //     the same byte ranges the sparse MultiProofReader will read milliseconds later.
        //     EXPB 26636221404 proved removing it costs ~70 ms p95 on realblocks (MIN
        //     jumps 0.5 â†’ 35 ms once every block pays full cold I/O). Drop the warmer only
        //     after the sparse-aware prefetcher (M5) is in place; that one will warm the
        //     same pages without the Patricia-side allocations.
        bool useNoopWarmer =
            flatDbConfig.TrieWarmerWorkerCount == 0
            || flatDbConfig.SparseTrieWarmer == SparseTrieWarmerVariant.None;

        builder

            // Implementation of nethermind interfaces
            .AddSingleton<FlatStateReader>()
            .AddSingleton<FlatWorldStateManager>()
            .AddSingleton<FlatStateBoundary>()

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
            .AddSingleton<IArenaManager, IFlatDbConfig, IInitConfig, ILogManager>((cfg, initConfig, logManager) =>
            {
                string basePath = Path.Combine(initConfig.BaseDbPath, "persistedSnapshot");
                return new ArenaManager(Path.Combine(basePath, "arena"), cfg, logManager);
            })
            .AddSingleton<BlobArenaManager, IFlatDbConfig, IInitConfig>((cfg, initConfig) =>
            {
                string basePath = Path.Combine(initConfig.BaseDbPath, "persistedSnapshot");
                return new BlobArenaManager(
                    Path.Combine(basePath, "blob"),
                    cfg.ArenaFileSizeBytes);
            })
            .AddSingleton<IPersistedSnapshotCompactor, PersistedSnapshotCompactor>()
            .AddSingleton<ISnapshotRepository, SnapshotRepository>()
            .AddSingleton<IPersistedSnapshotLoader, PersistedSnapshotLoader>()
            .AddSingleton<ITrieWarmer>(useNoopWarmer
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
            .AddKeyedSingleton<IDb>(DbNames.PersistedSnapshotCatalog, ctx => ctx
                .Resolve<IDbFactory>()
                .CreateDb(new DbSettings(
                    nameof(DbNames.PersistedSnapshotCatalog),
                    Path.Combine("persistedSnapshot", "catalog"))))
            .AddSingleton<SnapshotCatalog>()
            .AddSingleton<ISnapshotCatalog>(ctx => ctx.Resolve<SnapshotCatalog>())
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

                IPersistence cachedReader = new CachedReaderPersistence(persistence, exitSource, logManager);
                return new CarryForwardCachingPersistence(cachedReader);
            })
            ;

        if (!flatDbConfig.EnableLongFinality)
        {
            builder
                .AddSingleton<ISnapshotCatalog>(NullSnapshotCatalog.Instance)
                .AddSingleton<IPersistedSnapshotLoader>(NullPersistedSnapshotLoader.Instance)
                .AddSingleton<IPersistedSnapshotCompactor>(NullPersistedSnapshotCompactor.Instance);
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
    }
}
