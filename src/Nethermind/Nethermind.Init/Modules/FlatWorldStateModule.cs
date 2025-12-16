// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Autofac;
using Nethermind.Api.Steps;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
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
using Nethermind.Synchronization.FastSync;
using Nethermind.Synchronization.ParallelSync;

namespace Nethermind.Init.Modules;

public class FlatWorldStateModule(IFlatDbConfig flatDbConfig): Module
{
    protected override void Load(ContainerBuilder builder)
    {
        builder.AddSingleton<MainPruningTrieStoreFactory>(_ => throw new Exception($"{nameof(MainPruningTrieStoreFactory)} disabled."));
        builder.AddSingleton<PruningTrieStateFactory>(_ => throw new Exception($"{nameof(PruningTrieStateFactory)} disabled."));

        builder
            .AddSingleton<ICanonicalStateRootFinder, CanonicalStateRootFinder>()
            .AddSingleton<IWorldStateManager, FlatWorldStateManager>()
            .AddSingleton<IFlatDiffRepository, FlatDiffRepository>()
            .AddSingleton<ResourcePool>()
            .AddSingleton<Importer>()
            .AddColumnDatabase<FlatDbColumns>(DbNames.Flat)
            .AddSingleton<ITrieWarmer, TrieWarmer>()

            // These fake db are workaround for missing metrics with column db. Probably not a good idea though as
            // a failure in writes in one of the DB will break the db.
            .AddDatabase(DbNames.Preimage)
            .AddDatabase(DbNames.FlatMetadata)
            .AddDatabase(DbNames.FlatAccount)
            .AddDatabase(DbNames.FlatStorage)
            .AddDatabase(DbNames.FlatStateNodes)
            .AddDatabase(DbNames.FlatStateTopNodes)
            .AddDatabase(DbNames.FlatStorageNodes)
            .AddDatabase(DbNames.FlatStorageTopNodes)
            .AddSingleton<IColumnsDb<FlatDbColumns>>((ctx) =>
            {
                return new FakeColumnsDb<FlatDbColumns>(new Dictionary<FlatDbColumns, IDb>()
                {
                    { FlatDbColumns.Metadata, ctx.ResolveKeyed<IDb>(DbNames.FlatMetadata) },
                    { FlatDbColumns.Account, ctx.ResolveKeyed<IDb>(DbNames.FlatAccount) },
                    { FlatDbColumns.Storage, ctx.ResolveKeyed<IDb>(DbNames.FlatStorage) },
                    { FlatDbColumns.StateNodes, ctx.ResolveKeyed<IDb>(DbNames.FlatStateNodes) },
                    { FlatDbColumns.StorageNodes, ctx.ResolveKeyed<IDb>(DbNames.FlatStorageNodes) },
                    { FlatDbColumns.StateTopNodes, ctx.ResolveKeyed<IDb>(DbNames.FlatStateTopNodes) },
                    { FlatDbColumns.StorageTopNodes, ctx.ResolveKeyed<IDb>(DbNames.FlatStorageTopNodes) },
                });
            })

            .AddSingleton<ReadonlyReaderRepository>()
            .AddSingleton<FlatDiffRepository.Configuration, IFlatDbConfig>((config) => new FlatDiffRepository.Configuration()
            {
                Boundary = config.PruningBoundary,
                CompactSize = config.CompactSize,
                CompactInterval = config.CompactInterval,
                MaxInFlightCompactJob = config.MaxInFlightCompactJob,
                ReadWithTrie = config.ReadWithTrie,
                VerifyWithTrie = config.VerifyWithTrie,
                ConcurrentCompactor = 1,
                TrieCacheMemoryTarget = config.TrieCacheMemoryTarget,
                InlineCompaction = config.InlineCompaction,
                DisableTrieWarmer = config.DisableTrieWarmer
            })

            .AddSingleton<IPersistence, IFlatDbConfig, IComponentContext>((flatDbConfig, ctx) =>
            {
                if (
                    flatDbConfig.Layout == FlatLayout.PreimageFlat
                    || flatDbConfig.Layout == FlatLayout.FlatSeparateTopStorage
                    || flatDbConfig.Layout == FlatLayout.Flat
                    || flatDbConfig.Layout == FlatLayout.FlatInTrie
                    )
                {
                    return ctx.Resolve<RocksdbPersistence>();
                }

                if (flatDbConfig.Layout == FlatLayout.FlatTruncatedLeaf)
                {
                    return ctx.Resolve<NoLeafValueRocksdbPersistence>();
                }

                throw new Exception($"Unsupported layout {flatDbConfig.Layout}");
            })

            .AddDecorator<IDbConfig>((ctx, dbConfig) =>
            {
                IFlatDbConfig flatConfig = ctx.Resolve<IFlatDbConfig>();
                dbConfig.IsFlatInTrie = flatConfig.Layout == FlatLayout.FlatInTrie;
                return dbConfig;
            })

            .AddSingleton<RocksdbPersistence>()
            .AddSingleton<RocksdbPersistence.Configuration, IFlatDbConfig>((config) => new RocksdbPersistence.Configuration()
            {
                UsePreimage = config.Layout == FlatLayout.PreimageFlat,
                FlatInTrie = config.Layout == FlatLayout.FlatInTrie,
                SeparateStorageTop = config.Layout == FlatLayout.FlatSeparateTopStorage || config.Layout == FlatLayout.PreimageFlat
            })

            .AddSingleton<NoLeafValueRocksdbPersistence>()
            .AddSingleton<NoLeafValueRocksdbPersistence.Configuration>()
            .AddSingleton<IStateReader, FlatStateReader>()

            .AddDecorator<IRocksDbConfigFactory, FlatBlockCacheAdjuster>()
            ;


        if (flatDbConfig.ImportFromPruningTrieState)
        {
            builder.AddStep(typeof(ImportFlatDb));
        }

        if (flatDbConfig.ImportOnStateSyncFinished)
        {
            builder
                .AddDecorator<ISyncConfig>((ctx, syncConfig) =>
                {
                    // Prevent long range catchup.
                    if (syncConfig.FastSyncCatchUpHeightDelta < 100_000_000)
                    {
                        syncConfig.FastSyncCatchUpHeightDelta = 100_000_000;
                    }

                    return syncConfig;
                })
                .AddSingleton<ImportStateOnStateSyncFinished>()
                .OnActivate<ISyncFeed<StateSyncBatch>>((_, ctx) =>
                {
                    ctx.Resolve<ImportStateOnStateSyncFinished>();
                });
        }
    }

    private class FlatBlockCacheAdjuster : IRocksDbConfigFactory, IDisposable
    {
        private readonly IRocksDbConfigFactory _rocksDbConfigFactory;
        private readonly IntPtr _flatDbBlockCache;
        private readonly HashSet<(string, string?)> _columnsWithBlockCache;
        private readonly ILogger _logger;

        public FlatBlockCacheAdjuster(IRocksDbConfigFactory rocksDbConfigFactory, IFlatDbConfig flatDbConfig, ILogManager logManager)
        {
            _logger = logManager.GetClassLogger<FlatBlockCacheAdjuster>();
            _rocksDbConfigFactory = rocksDbConfigFactory;
            _flatDbBlockCache = RocksDbSharp.Native.Instance.rocksdb_cache_create_lru(new UIntPtr((uint)flatDbConfig.BlockCacheSizeBudget));

            FlatDbColumns[] columns;

            if (flatDbConfig.Layout == FlatLayout.FlatInTrie)
            {
                columns =
                [
                    FlatDbColumns.StateNodes,
                    FlatDbColumns.StorageNodes
                ];
            }
            else
            {
                columns =
                [
                    FlatDbColumns.Account,
                    FlatDbColumns.Storage
                ];
            }

            _columnsWithBlockCache = new HashSet<(string, string?)>();
            foreach (FlatDbColumns col in columns)
            {
                _columnsWithBlockCache.Add(("Flat", col.ToString()));
                _columnsWithBlockCache.Add(("Flat" + col.ToString(), null));
            }
        }

        public void Dispose()
        {
            RocksDbSharp.Native.Instance.rocksdb_cache_destroy(_flatDbBlockCache);
        }

        public IRocksDbConfig GetForDatabase(string databaseName, string? columnName)
        {
            IRocksDbConfig config = _rocksDbConfigFactory.GetForDatabase(databaseName, columnName);
            if (_columnsWithBlockCache.Contains((databaseName, columnName)))
            {
                _logger.Warn($"Adjusting db {databaseName}, {columnName} with shared block cache");
                config = new AdjustedRocksdbConfig(config, "", config.WriteBufferSize.GetValueOrDefault(), _flatDbBlockCache);
            }

            return config;
        }
    }

    public class ImportStateOnStateSyncFinished
    {
        private readonly Importer _importer;
        private readonly ITreeSync _treeSync;

        public ImportStateOnStateSyncFinished(Importer importer, ITreeSync treeSync)
        {
            _importer = importer;
            _treeSync = treeSync;
            _treeSync.SyncCompleted += TreeSyncOnOnVerifyPostSyncCleanup;
        }

        private void TreeSyncOnOnVerifyPostSyncCleanup(object? sender, ITreeSync.SyncCompletedEventArgs evt)
        {
            _treeSync.SyncCompleted -= TreeSyncOnOnVerifyPostSyncCleanup;

            // Note: this block
            StateId stateId = new StateId(evt.Pivot);
            _importer.Copy(stateId);
        }
    }
}
