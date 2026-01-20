// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Autofac;
using Nethermind.Api.Steps;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Init.Steps;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.State.Flat;
using Nethermind.State.Flat.Importer;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.ScopeProvider;

namespace Nethermind.Init.Modules;

public class FlatWorldStateModule(IFlatDbConfig flatDbConfig): Module
{
    protected override void Load(ContainerBuilder builder)
    {
        builder.AddSingleton<MainPruningTrieStoreFactory>(_ => throw new Exception($"{nameof(MainPruningTrieStoreFactory)} disabled."));
        builder.AddSingleton<PruningTrieStateFactory>(_ => throw new Exception($"{nameof(PruningTrieStateFactory)} disabled."));

        builder
            .AddSingleton<IWorldStateManager, FlatWorldStateManager>()
            .AddSingleton<IFlatDbManager, FlatDbManager>()
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

            .AddDecorator<IRocksDbConfigFactory, FlatBlockCacheAdjuster>()

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
            // Disable statedb so that it does not compact which mess with metrics.
            builder.AddKeyedSingleton<IDb>(DbNames.State, new MemDb());
        }
    }

    private class FlatBlockCacheAdjuster : IRocksDbConfigFactory, IDisposable
    {
        private readonly IRocksDbConfigFactory _rocksDbConfigFactory;
        private readonly ILogger _logger;
        private readonly IFlatDbConfig _flatDbConfig;
        private readonly IDisposableStack _disposeStack;

        public FlatBlockCacheAdjuster(IRocksDbConfigFactory rocksDbConfigFactory, IFlatDbConfig flatDbConfig, IDisposableStack disposeStack, ILogManager logManager)
        {
            _disposeStack = disposeStack;
            _logger = logManager.GetClassLogger<FlatBlockCacheAdjuster>();
            _rocksDbConfigFactory = rocksDbConfigFactory;
            _flatDbConfig = flatDbConfig;
        }

        public void Dispose()
        {
        }

        public IRocksDbConfig GetForDatabase(string databaseName, string? columnName)
        {
            IRocksDbConfig config = _rocksDbConfigFactory.GetForDatabase(databaseName, columnName);
            if (databaseName == nameof(DbNames.Flat) || databaseName.StartsWith(nameof(DbNames.Flat)))
            {
                string additionalConfig = "";
                if (_flatDbConfig.Layout == FlatLayout.FlatInTrie)
                {
                    // For flat in trie, add optimize filter for hits and turn on partitioned index, this reduces
                    // memory at expense of latency.
                    additionalConfig = config.RocksDbOptions +
                                      "optimize_filters_for_hits=true;" +
                                      "block_based_table_factory.partition_filters=true;" +
                                      "block_based_table_factory.index_type=kTwoLevelIndexSearch;";
                }

                IntPtr? cacheHandle = null;
                if (databaseName.EndsWith(nameof(FlatDbColumns.Account)) || columnName == nameof(FlatDbColumns.Account))
                {
                    ulong cacheCapacity = (ulong)(_flatDbConfig.BlockCacheSizeBudget * 0.3);
                    _logger.Info($"Setting {(cacheCapacity/(ulong)1.MiB()):N0} MB of block cache to account");
                    HyperClockCacheWrapper cacheWrapper = new HyperClockCacheWrapper(cacheCapacity);
                    cacheHandle = cacheWrapper.Handle;
                    _disposeStack.Push(cacheWrapper);
                }

                if (databaseName.EndsWith(nameof(FlatDbColumns.Storage)) || columnName == nameof(FlatDbColumns.Storage))
                {
                    ulong cacheCapacity = (ulong)(_flatDbConfig.BlockCacheSizeBudget * 0.7);
                    _logger.Info($"Setting {(cacheCapacity/(ulong)1.MiB()):N0} MB of block cache to storage");
                    HyperClockCacheWrapper cacheWrapper = new HyperClockCacheWrapper(cacheCapacity);
                    cacheHandle = cacheWrapper.Handle;
                    _disposeStack.Push(cacheWrapper);
                }

                config = new AdjustedRocksdbConfig(config, additionalConfig, config.WriteBufferSize.GetValueOrDefault(), cacheHandle);
            }

            return config;
        }
    }
}
