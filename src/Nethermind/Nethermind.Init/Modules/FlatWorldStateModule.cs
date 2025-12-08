// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Autofac;
using Nethermind.Api.Steps;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.Init.Steps;
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
            .AddSingleton<ICanonicalStateRootFinder, CanonicalStateRootFinder>()
            .AddSingleton<IWorldStateManager, FlatWorldStateManager>()
            .AddSingleton<IFlatDiffRepository, FlatDiffRepository>()
            .AddSingleton<Importer>()
            .AddColumnDatabase<FlatDbColumns>(DbNames.Flat)
            .AddSingleton<IPersistence, RocksdbPersistence>()
            // .AddSingleton<IPersistence, TrieOnlyRocksdbPersistence>()
            .AddSingleton<TrieWarmer>()

            // These fake db are workaround for missing metrics with column db. Probably not a good idea though as
            // a failure in writes in one of the DB will break the db.
            .AddDatabase(DbNames.Preimage)
            .AddDatabase(DbNames.FlatMetadata)
            .AddDatabase(DbNames.FlatState)
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
                    { FlatDbColumns.State, ctx.ResolveKeyed<IDb>(DbNames.FlatState) },
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
            .AddSingleton<RocksdbPersistence.Configuration, IFlatDbConfig>((config) => new RocksdbPersistence.Configuration()
            {
                UsePreimage = config.UsePreimage
            })
            .AddSingleton<IStateReader, FlatStateReader>();

        if (flatDbConfig.ImportFromPruningTrieState)
        {
            builder.AddStep(typeof(ImportFlatDb));
        }
    }
}
