// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using Autofac;
using Nethermind.Core;
using Nethermind.Synchronization.FastSync;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test.FastSync;

public class TreeSyncStoreTestFixtureSource : IEnumerable
{
    public static void RegisterPatriciaStore(ContainerBuilder builder) => builder
        .AddSingleton<ITreeSyncStore, PatriciaTreeSyncStore>()
        .AddSingleton<ITestOperation, PatriciaTreeTestOperation>()
        ;

    // Future:
    // public static void RegisterFlatStore(ContainerBuilder builder) =>
    //     builder.Register(ctx => new FlatTreeSyncStore(...)).As<ITreeSyncStore>().SingleInstance();

    public IEnumerator GetEnumerator()
    {
        yield return new TestFixtureData((Action<ContainerBuilder>)RegisterPatriciaStore)
            .SetArgDisplayNames("Patricia");
        // Future: yield return for Flat
    }

    private interface ITestOperation
    {
        // Add here
    }

    private class PatriciaTreeTestOperation: ITestOperation
    {

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
