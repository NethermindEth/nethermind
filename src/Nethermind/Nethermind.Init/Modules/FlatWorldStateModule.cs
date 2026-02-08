// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using Microsoft.AspNetCore.Http;
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
using Nethermind.State;
using Nethermind.State.Flat;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.ScopeProvider;
using Nethermind.State.Flat.Sync;
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

            // Disable some pruning trie store specific  components
            .AddSingleton<IPruningTrieStateAdminRpcModule, PruningTrieStateAdminRpcModuleStub>()
            .AddSingleton<MainPruningTrieStoreFactory>(_ => throw new NotSupportedException($"{nameof(MainPruningTrieStoreFactory)} disabled."))
            .AddSingleton<PruningTrieStateFactory>(_ => throw new NotSupportedException($"{nameof(PruningTrieStateFactory)} disabled."))

            // The actual flatDb components
            .AddSingleton<IFlatDbManager>((ctx) => new FlatDbManager(
                ctx.Resolve<IResourcePool>(),
                ctx.Resolve<IProcessExitSource>(),
                ctx.Resolve<ITrieNodeCache>(),
                ctx.Resolve<ISnapshotCompactor>(),
                ctx.Resolve<ISnapshotRepository>(),
                ctx.Resolve<IPersistenceManager>(),
                ctx.Resolve<IFlatDbConfig>(),
                ctx.Resolve<ILogManager>(),
                ctx.Resolve<IMetricsConfig>().EnableDetailedMetric))
            .AddSingleton<IResourcePool, ResourcePool>()
            .AddSingleton<ITrieNodeCache, TrieNodeCache>()
            .AddSingleton<ISnapshotCompactor, SnapshotCompactor>()
            .AddSingleton<IPersistenceManager, PersistenceManager>()
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
                syncConfig.SnapServingEnabled = true;
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
