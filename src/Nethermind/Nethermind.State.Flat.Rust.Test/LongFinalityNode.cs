// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Api;
using Nethermind.Core;
using Nethermind.Core.Test.Modules;
using Nethermind.Db;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.State.Flat.PersistedSnapshots.Storage;

namespace Nethermind.State.Flat.Rust.Test;

/// <summary>Long finality on a node <see cref="TestNethermindModule"/> builds, which turns it off.</summary>
internal static class LongFinalityNode
{
    /// <summary>
    /// Turns long finality back on for the node <paramref name="builder"/> builds, with its persisted-snapshot tier in
    /// <paramref name="basePath"/> and its catalog a RocksDB there, which outlives the node.
    /// </summary>
    /// <remarks>
    /// The flat module chose the inert catalog, loader and compactor of the C# backend when it loaded with long finality
    /// off, so on that backend the real ones are registered again, last-wins.
    /// </remarks>
    public static ContainerBuilder Enable(ContainerBuilder builder, FlatDbBackend backend, string basePath)
    {
        builder
            .Intercept<IFlatDbConfig>(static flatDbConfig => flatDbConfig.EnableLongFinality = true)
            .Intercept<IInitConfig>(initConfig => initConfig.BaseDbPath = basePath)
            .AddKeyedSingleton<IDb>(DbNames.PersistedSnapshotCatalog, _ => FlatTestDbs.OpenRocksDbCatalog(basePath));
        if (backend == FlatDbBackend.CSharp)
        {
            builder
                .AddSingleton<ISnapshotCatalog>(static ctx => ctx.Resolve<SnapshotCatalog>())
                .AddSingleton<IPersistedSnapshotLoader, PersistedSnapshotLoader>()
                .AddSingleton<IPersistedSnapshotCompactor, PersistedSnapshotCompactor>();
        }

        return builder;
    }
}
