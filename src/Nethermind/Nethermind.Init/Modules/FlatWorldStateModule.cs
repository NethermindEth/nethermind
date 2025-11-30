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
            .AddSingleton<TrieStoreTrieCacheWarmer>()

            /*
            .AddDatabase(DbNames.FlatMetadata)
            .AddDatabase(DbNames.FlatState)
            .AddDatabase(DbNames.FlatStorage)
            .AddDatabase(DbNames.FlatStateNodes)
            .AddDatabase(DbNames.FlatStateNodesTop)
            .AddDatabase(DbNames.FlatStorageNodes)
            .AddSingleton<IColumnsDb<FlatDbColumns>>((ctx) =>
            {
                return new FakeColumnsDb<FlatDbColumns>(new Dictionary<FlatDbColumns, IDb>()
                {
                    { FlatDbColumns.Metadata, ctx.ResolveKeyed<IDb>(DbNames.FlatMetadata) },
                    { FlatDbColumns.State, ctx.ResolveKeyed<IDb>(DbNames.FlatState) },
                    { FlatDbColumns.Storage, ctx.ResolveKeyed<IDb>(DbNames.FlatStorage) },
                    { FlatDbColumns.StateNodes, ctx.ResolveKeyed<IDb>(DbNames.FlatStateNodes) },
                    { FlatDbColumns.StateNodesTop, ctx.ResolveKeyed<IDb>(DbNames.FlatStateNodesTop) },
                    { FlatDbColumns.StorageNodes, ctx.ResolveKeyed<IDb>(DbNames.FlatStorageNodes) },
                });
            })
            */

            .AddSingleton<FlatDiffRepository.Configuration>(new FlatDiffRepository.Configuration()
            {
                Boundary = 256,
                CompactSize = 16,
                MaxInFlightCompactJob = 32,
                ReadWithTrie = false,
                VerifyWithTrie = true,
                ConcurrentCompactor = 2,
                TrieCacheMemoryTarget = 1.GiB(),
                InlineCompaction = false
            })
            .AddSingleton<IStateReader, FlatStateReader>();

        if (flatDbConfig.ImportFromPruningTrieState)
        {
            builder.AddStep(typeof(ImportFlatDb));
        }
    }
}
