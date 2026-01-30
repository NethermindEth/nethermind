// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using Autofac;
using Nethermind.Logging;
using Nethermind.Synchronization.FastSync;
using Nethermind.Trie;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test.FastSync;

public class TreeSyncStoreTestFixtureSource : IEnumerable
{
    public static void RegisterPatriciaStore(ContainerBuilder builder) =>
        builder.Register(ctx => new PatriciaTreeSyncStore(ctx.Resolve<INodeStorage>(), ctx.Resolve<ILogManager>()))
            .As<ITreeSyncStore>().SingleInstance();

    // Future:
    // public static void RegisterFlatStore(ContainerBuilder builder) =>
    //     builder.Register(ctx => new FlatTreeSyncStore(...)).As<ITreeSyncStore>().SingleInstance();

    public IEnumerator GetEnumerator()
    {
        yield return new TestFixtureData((Action<ContainerBuilder>)RegisterPatriciaStore)
            .SetArgDisplayNames("Patricia");
        // Future: yield return for Flat
    }
}

public class StateSyncFeedTestsFixtureSource : IEnumerable
{
    private static readonly (int peerCount, int maxLatency)[] PeerConfigs =
    [
        (1, 0),
        (1, 100),
        (4, 0),
        (4, 100)
    ];

    public IEnumerator GetEnumerator()
    {
        foreach (var (peerCount, maxLatency) in PeerConfigs)
        {
            yield return new TestFixtureData(
                (Action<ContainerBuilder>)TreeSyncStoreTestFixtureSource.RegisterPatriciaStore,
                peerCount,
                maxLatency
            ).SetArgDisplayNames($"Patricia-{peerCount}peers-{maxLatency}ms");
            // Future: yield return for Flat
        }
    }
}
