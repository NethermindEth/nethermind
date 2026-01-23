// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using Nethermind.Api.Steps;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Db.Rocks.Config;
using Nethermind.Init.Steps;
using Nethermind.Logging;
using Nethermind.Monitoring.Config;
using Nethermind.State;
using Nethermind.State.Flat;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.ScopeProvider;

namespace Nethermind.Init.Modules;

public class FlatWorldStateModule(IFlatDbConfig flatDbConfig) : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        // Just in case
        builder.AddSingleton<MainPruningTrieStoreFactory>(_ => throw new Exception($"{nameof(MainPruningTrieStoreFactory)} disabled."));
        builder.AddSingleton<PruningTrieStateFactory>(_ => throw new Exception($"{nameof(PruningTrieStateFactory)} disabled."));

        builder
            .AddSingleton<IWorldStateManager, FlatWorldStateManager>()
            .AddSingleton<IFlatDbManager>((ctx) => new FlatDbManager(
                ctx.Resolve<ResourcePool>(),
                ctx.Resolve<IProcessExitSource>(),
                ctx.Resolve<TrieNodeCache>(),
                ctx.Resolve<SnapshotCompactor>(),
                ctx.Resolve<ISnapshotRepository>(),
                ctx.Resolve<PersistenceManager>(),
                ctx.Resolve<IFlatDbConfig>(),
                ctx.Resolve<ILogManager>(),
                ctx.Resolve<IMetricsConfig>().EnableDetailedMetric))
            .AddSingleton<ResourcePool>()
            .AddSingleton<Importer>()
            .AddSingleton<TrieNodeCache>()
            .AddSingleton<SnapshotCompactor>()
            .AddSingleton<PersistenceManager>()
            .AddSingleton<ISnapshotRepository, SnapshotRepository>()
            .AddColumnDatabase<FlatDbColumns>(DbNames.Flat)
            .AddSingleton<ITrieWarmer, TrieWarmer>()

            .AddSingleton<IPersistence, IFlatDbConfig, IComponentContext>((flatDbConfig, ctx) =>
            {
                IPersistence persistence = flatDbConfig.Layout switch
                {
                    FlatLayout.Flat => ctx.Resolve<RocksdbPersistence>(),
                    FlatLayout.FlatInTrie => ctx.Resolve<FlatInTriePersistence>(),
                    FlatLayout.PreimageFlat => ctx.Resolve<PreimageRocksdbPersistence>(),
                    _ => throw new Exception($"Unsupported layout {flatDbConfig.Layout}")
                };

                if (flatDbConfig.EnablePreimageRecording)
                {
                    IDb preimageDb = ctx.ResolveKeyed<IDb>(DbNames.Preimage);
                    persistence = new PreimageRecordingPersistence(persistence, preimageDb);
                }

                return persistence;
            })

            .AddSingleton<PreimageRocksdbPersistence>()
            .AddDatabase(DbNames.Preimage)

            .AddSingleton<RocksdbPersistence>()
            .AddSingleton<FlatInTriePersistence>()

            .AddSingleton<IStateReader, FlatStateReader>()

            .AddDecorator<IRocksDbConfigFactory, FlatRocksDbConfigAdjuster>()

            .OnActivate<IWorldStateManager>((worldStateManager, ctx) =>
            {
                new TrieStoreBoundaryWatcher(worldStateManager, ctx.Resolve<IBlockTree>(), ctx.Resolve<ILogManager>());
            })
            ;

        if (flatDbConfig.ImportFromPruningTrieState)
        {
            builder.AddStep(typeof(ImportFlatDb));
        }
        else
        {
            builder
                .Intercept<ISyncConfig>((syncConfig) =>
                {
                    if (syncConfig.FastSync || syncConfig.SnapSync)
                    {
                        throw new InvalidOperationException("Fast sync and snaps sync");
                    }
                });
        }
    }
}
