// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.State;
using Nethermind.State.Flat;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.ScopeProvider;

namespace Nethermind.Init.Modules;

public class FlatWorldStateModule: Module
{
    protected override void Load(ContainerBuilder builder)
    {
        builder.AddSingleton<MainPruningTrieStoreFactory>(_ => throw new Exception($"{nameof(MainPruningTrieStoreFactory)} disabled."));
        builder.AddSingleton<PruningTrieStateFactory>(_ => throw new Exception($"{nameof(PruningTrieStateFactory)} disabled."));


        builder
            .AddSingleton<ICanonicalStateRootFinder, CanonicalStateRootFinder>()
            .AddSingleton<IWorldStateManager, FlatWorldStateManager>()
            .AddSingleton<IFlatDiffRepository, FlatDiffRepository>()
            .AddColumnDatabase<FlatDbColumns>(DbNames.Flat)
            // .AddSingleton<IPersistence, RocksdbPersistence>()
            .AddSingleton<IPersistence, RocksdbSeparatePersistence>()
            .AddDatabase(DbNames.FlatMetadata)
            .AddDatabase(DbNames.FlatState)
            .AddDatabase(DbNames.FlatStorage)
            .AddDatabase(DbNames.FlatStateNodes)
            .AddDatabase(DbNames.FlatStateNodesTop)
            .AddDatabase(DbNames.FlatStorageNodes)
            .AddSingleton<FlatDiffRepository.Configuration>(new FlatDiffRepository.Configuration()
            {
                Boundary = 128,
                CompactSize = 128,
                MaxInFlightCompactJob = 32,
                ReadWithTrie = false,
                VerifyWithTrie = false,
                ConcurrentCompactor = 4,
                TrieCacheMemoryTarget = 2.GiB(),
                InlineCompaction = false
            })
            .AddSingleton<IStateReader, FlatStateReader>();
    }
}
