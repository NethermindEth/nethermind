// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using Nethermind.Logging;
using Nethermind.Synchronization.SnapSync;
using Nethermind.Trie;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test.SnapSync;

public class SnapTrieFactoryTestFixtureSource : IEnumerable
{
    public static ISnapTrieFactory CreatePatriciaFactory(INodeStorage nodeStorage, ILogManager logManager) =>
        new PatriciaSnapTrieFactory(nodeStorage, logManager);

    // Add future factories here:
    // public static ISnapTrieFactory CreateFlatFactory(INodeStorage nodeStorage, ILogManager logManager) =>
    //     new FlatSnapTrieFactory(nodeStorage, logManager);

    public IEnumerator GetEnumerator()
    {
        yield return new TestFixtureData((Func<INodeStorage, ILogManager, ISnapTrieFactory>)CreatePatriciaFactory)
            .SetArgDisplayNames("Patricia");
        // Future: yield return new TestFixtureData((Func<INodeStorage, ILogManager, ISnapTrieFactory>)CreateFlatFactory)
        //     .SetArgDisplayNames("Flat");
    }
}
