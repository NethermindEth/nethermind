// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using Nethermind.Logging;
using Nethermind.Synchronization.FastSync;
using Nethermind.Trie;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test.FastSync;

public class TreeSyncStoreTestFixtureSource : IEnumerable
{
    public static ITreeSyncStore CreatePatriciaStore(INodeStorage nodeStorage, ILogManager logManager) =>
        new PatriciaTreeSyncStore(nodeStorage, logManager);

    // Future:
    // public static ITreeSyncStore CreateFlatStore(INodeStorage nodeStorage, ILogManager logManager) =>
    //     new FlatTreeSyncStore(nodeStorage, logManager);

    public IEnumerator GetEnumerator()
    {
        yield return new TestFixtureData((Func<INodeStorage, ILogManager, ITreeSyncStore>)CreatePatriciaStore)
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
                (Func<INodeStorage, ILogManager, ITreeSyncStore>)TreeSyncStoreTestFixtureSource.CreatePatriciaStore,
                peerCount,
                maxLatency
            ).SetArgDisplayNames($"Patricia-{peerCount}peers-{maxLatency}ms");
            // Future: yield return for Flat
        }
    }
}
